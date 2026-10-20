using System.Text.Json;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Content;
using Dam.Domain.Licensing;

namespace Dam.Domain.Assets;

/// <summary>Canonical asset events (spec A4.3).</summary>
public sealed record AssetCreated(Guid AssetId, string AssetType) : DomainEvent, IEntityEvent
{
    public override string Name => "asset.created";
    public string EntityType => "asset";
    public Guid EntityId => AssetId;
}

public sealed record AssetMetadataUpdated(Guid AssetId) : DomainEvent, IEntityEvent
{
    public override string Name => "asset.metadata.updated";
    public string EntityType => "asset";
    public Guid EntityId => AssetId;
}

public sealed record AssetLegalHoldChanged(Guid AssetId, bool Hold) : DomainEvent, IEntityEvent
{
    public override string Name => "asset.legal_hold.changed";
    public string EntityType => "asset";
    public Guid EntityId => AssetId;
}

public sealed record AssetStatusChanged(Guid AssetId, string From, string To) : DomainEvent, IEntityEvent
{
    public override string Name => "asset.status.changed";
    public string EntityType => "asset";
    public Guid EntityId => AssetId;
}

/// <summary>
/// The central aggregate (spec A4.1). An asset is a governed record; its files are versions (Step 1.4). Until a first
/// version exists it is a "shell": metadata and placement, no content.
/// </summary>
public sealed class Asset : AggregateRoot
{
    private Asset() { }

    public Guid? FolderId { get; private set; }
    public string Type { get; private set; } = "";
    public string Status { get; private set; } = AssetStatuses.Draft;
    public string Title { get; private set; } = "";
    public Dictionary<string, JsonElement> Metadata { get; private set; } = [];
    /// <summary>The schema version the metadata was validated against. Old assets keep validating against their own version.</summary>
    public int MetadataSchemaVersion { get; private set; }
    /// <summary>Every term code used in the metadata, for facets and search.</summary>
    public List<string> TaxonomyTerms { get; private set; } = [];
    public List<string> Tags { get; private set; } = [];
    /// <summary>The EFFECTIVE access-control attributes (region, dealer, brand, channel; ADR-0002): derived from metadata, then narrowed by the
    /// asset's rights (ADR-0033). This is what the policy engine and the search index see.</summary>
    public Dictionary<string, string[]> Attributes { get; private set; } = [];
    /// <summary>The attributes metadata alone derives, before rights narrow them. Kept so a change of rights can re-derive <see cref="Attributes"/>.</summary>
    public Dictionary<string, string[]> MetadataAttributes { get; private set; } = [];
    /// <summary>A copy of the assigned rights' channels (empty: every channel), kept in step by <see cref="AssignRights"/> so an edit of the metadata
    /// re-derives the attributes without loading the rights record.</summary>
    public List<string> RightsChannels { get; private set; } = [];
    /// <summary>A copy of the assigned rights' regions (empty: worldwide).</summary>
    public List<string> RightsRegions { get; private set; } = [];
    public Guid? OwnerId { get; private set; }
    public Guid? CurrentVersionId { get; private set; }
    public Guid? RightsId { get; private set; }
    public DateTimeOffset? PublishAt { get; private set; }
    public DateTimeOffset? ExpireAt { get; private set; }
    public Guid? RetentionPolicyId { get; private set; }
    /// <summary>Blocks retention purge regardless of policy (spec RetentionPolicy.legal_hold_exempt is the one override:
    /// a policy marked exempt purges through a hold anyway, e.g. for content a legal hold was never meant to cover).</summary>
    public bool LegalHold { get; private set; }
    /// <summary>SHA-256 (hex) of the content, set once the upload has been hashed.</summary>
    public string? ChecksumSha256 { get; private set; }
    public string? Phash { get; private set; }
    public string StatusReason { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Asset CreateShell(Guid tenantId, Guid? folderId, string type, string title, Guid? ownerId, DateTimeOffset now)
    {
        var a = new Asset { TenantId = tenantId, FolderId = folderId, Type = type, Title = title.Trim(), OwnerId = ownerId, CreatedAt = now, UpdatedAt = now };
        a.Raise(new AssetCreated(a.Id, type));
        return a;
    }

    public void SetMetadata(Dictionary<string, JsonElement> metadata, int schemaVersion, Projection projection, DateTimeOffset now)
    {
        Metadata = metadata;
        MetadataSchemaVersion = schemaVersion;
        TaxonomyTerms = projection.TaxonomyTerms;
        MetadataAttributes = projection.Attributes;
        Attributes = RightsScope.Apply(MetadataAttributes, RightsChannels, RightsRegions);
        UpdatedAt = now;
        Raise(new AssetMetadataUpdated(Id));
    }

    /// <summary>
    /// Assigns (or, with a null id, clears) the asset's rights and re-derives its access attributes from the metadata attributes and the rights'
    /// <paramref name="channels"/> and <paramref name="regions"/>. Also the way to refresh the copy when the same rights record changed. Returns
    /// whether anything changed; if so the asset must be re-indexed, which <see cref="AssetRightsChanged"/> asks for.
    /// </summary>
    public bool AssignRights(Guid? rightsId, IReadOnlyCollection<string> channels, IReadOnlyCollection<string> regions, DateTimeOffset now)
    {
        var newChannels = RightsRules.NormalizeValues(rightsId is null ? [] : channels);
        var newRegions = RightsRules.NormalizeValues(rightsId is null ? [] : regions);
        var effective = RightsScope.Apply(MetadataAttributes, newChannels, newRegions);
        var same = RightsId == rightsId && RightsRules.SameValues(RightsChannels, newChannels) && RightsRules.SameValues(RightsRegions, newRegions)
                   && RightsScope.SameAttributes(Attributes, effective);
        if (same) return false;
        var previous = RightsId;
        RightsId = rightsId;
        RightsChannels = newChannels;
        RightsRegions = newRegions;
        Attributes = effective;
        UpdatedAt = now;
        Raise(new AssetRightsChanged(Id, rightsId, previous));
        return true;
    }

    /// <summary>Adds tags the asset does not have yet (normalised like any tag), up to <paramref name="maxTags"/> in all. Returns the ones actually added.
    /// Unlike <see cref="SetTags"/> this raises <see cref="AssetMetadataUpdated"/>, so the change is audited, delivered and re-indexed.</summary>
    public IReadOnlyList<string> AddTags(IEnumerable<string> tags, int maxTags, DateTimeOffset now)
    {
        var added = new List<string>();
        var current = Tags.ToHashSet(StringComparer.Ordinal);
        foreach (var t in Normalize(tags))
        {
            if (current.Count >= maxTags) break;
            if (current.Add(t)) added.Add(t);
        }
        if (added.Count == 0) return added;
        Tags = Normalize(current);
        UpdatedAt = now;
        Raise(new AssetMetadataUpdated(Id));
        return added;
    }

    public void Rename(string title, DateTimeOffset now) { Title = title.Trim(); UpdatedAt = now; }
    public void SetTags(IEnumerable<string> tags, DateTimeOffset now) { Tags = Normalize(tags); UpdatedAt = now; }
    public void MoveTo(Guid? folderId, DateTimeOffset now) { FolderId = folderId; UpdatedAt = now; }
    public void SetChecksum(string sha256, DateTimeOffset now) { ChecksumSha256 = sha256; UpdatedAt = now; }
    public void SetCurrentVersion(Guid versionId, DateTimeOffset now) { CurrentVersionId = versionId; UpdatedAt = now; }
    public void SetPhash(string? phash, DateTimeOffset now) { Phash = phash; UpdatedAt = now; }

    /// <summary>When to publish or expire this asset automatically (spec Step 1.6). Setting either does not itself change
    /// status: the scheduler only acts once the asset is actually in the status the timer applies to (approved for
    /// <paramref name="publishAt"/>, published for <paramref name="expireAt"/>), and clears the timer once it fires.</summary>
    public void SetSchedule(DateTimeOffset? publishAt, DateTimeOffset? expireAt, DateTimeOffset now) { PublishAt = publishAt; ExpireAt = expireAt; UpdatedAt = now; }
    public void SetRetentionPolicy(Guid? policyId, DateTimeOffset now) { RetentionPolicyId = policyId; UpdatedAt = now; }

    /// <summary>Legal hold changes are compliance-relevant, so unlike most simple field edits this one is audited.</summary>
    public void SetLegalHold(bool hold, DateTimeOffset now)
    {
        if (LegalHold == hold) return;
        LegalHold = hold;
        UpdatedAt = now;
        Raise(new AssetLegalHoldChanged(Id, hold));
    }

    public void ChangeStatus(string to, string reason, DateTimeOffset now)
    {
        if (Status == to) return;
        var from = Status;
        Status = to;
        StatusReason = reason;
        UpdatedAt = now;
        Raise(new AssetStatusChanged(Id, from, to));
    }

    public static List<string> Normalize(IEnumerable<string> tags) =>
        tags.Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct().Order().ToList();

    /// <summary>The facts the policy engine needs about this asset.</summary>
    public ResourceContext ToResource(string? folderPath) => new()
    {
        Type = "asset",
        TenantId = TenantId,
        Status = Status,
        FolderPath = folderPath,
        AssetType = Type,
        OwnerId = OwnerId,
        Attributes = Attributes,
    };

    /// <summary>What is derived from metadata: term codes and access attributes.</summary>
    public sealed record Projection(List<string> TaxonomyTerms, Dictionary<string, string[]> Attributes);
}

public enum UploadStatus
{
    /// <summary>Multipart upload open; parts are being sent.</summary>
    Initiated,
    /// <summary>All parts received and assembled; the worker is hashing the object.</summary>
    Processing,
    /// <summary>Hashed, not a duplicate; ingest has been requested (Step 1.4 takes over).</summary>
    Ready,
    /// <summary>Identical content already exists; the staged object was discarded.</summary>
    Duplicate,
    Failed,
    Aborted,
    /// <summary>The virus scan found malware. The file is kept apart in quarantine and the asset is blocked.</summary>
    Blocked,
    /// <summary>Scanned, checked, described and stored as a version: the upload's work is done.</summary>
    Ingested,
}

/// <summary>One resumable file transfer for an asset. The bytes go straight to object storage, never through the API.</summary>
public sealed class Upload : TenantEntity
{
    private Upload() { }

    public Guid AssetId { get; private set; }
    public Guid? BatchId { get; private set; }
    public UploadStatus Status { get; private set; }
    public string FileName { get; private set; } = "";
    public long DeclaredSize { get; private set; }
    public string ContentType { get; private set; } = "";
    public string Bucket { get; private set; } = "";
    public string StorageKey { get; private set; } = "";
    public string S3UploadId { get; private set; } = "";
    public int PartSize { get; private set; }
    public int PartCount { get; private set; }
    public bool AllowDuplicate { get; private set; }
    public string? Sha256 { get; private set; }
    public long? SizeBytes { get; private set; }
    public Guid? DuplicateOfAssetId { get; private set; }
    public string Error { get; private set; } = "";
    /// <summary>Where the ingest pipeline has got to: hashed, scanned, sniffed, extracted, promoted (or rejected/blocked).</summary>
    public string IngestStage { get; private set; } = "";
    /// <summary>Type detected from the file's bytes (never from its name).</summary>
    public string? DetectedMime { get; private set; }
    /// <summary>Everything learned about the file so far: scan verdict, detected kind, EXIF, video streams, PDF properties…</summary>
    public Dictionary<string, JsonElement> TechnicalMetadata { get; private set; } = [];
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public static Upload Start(Guid id, Guid tenantId, Guid assetId, Guid? batchId, string fileName, long size, string contentType, string bucket,
        string s3UploadId, int partSize, bool allowDuplicate, Guid? createdBy, DateTimeOffset now, TimeSpan lifetime)
    {
        return new Upload
        {
            Id = id,
            TenantId = tenantId,
            AssetId = assetId,
            BatchId = batchId,
            Status = UploadStatus.Initiated,
            FileName = fileName,
            DeclaredSize = size,
            ContentType = contentType,
            Bucket = bucket,
            StorageKey = KeyFor(tenantId, assetId, id),
            S3UploadId = s3UploadId,
            PartSize = partSize,
            PartCount = PartsFor(size, partSize),
            AllowDuplicate = allowDuplicate,
            CreatedBy = createdBy,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
        };
    }

    /// <summary>The object key is built from ids only, never from the file name, so it cannot be steered by the client.</summary>
    public static string KeyFor(Guid tenantId, Guid assetId, Guid uploadId) => $"staging/{tenantId:N}/{assetId:N}/{uploadId:N}";

    public static int PartsFor(long size, int partSize) => (int)Math.Max(1, (size + partSize - 1) / partSize);

    public void MarkProcessing(DateTimeOffset now) { Status = UploadStatus.Processing; CompletedAt = now; }
    public void MarkReady(string sha256, long size) { Status = UploadStatus.Ready; Sha256 = sha256; SizeBytes = size; IngestStage = "hashed"; }
    public void MarkDuplicate(string sha256, long size, Guid duplicateOf) { Status = UploadStatus.Duplicate; Sha256 = sha256; SizeBytes = size; DuplicateOfAssetId = duplicateOf; }
    public void MarkFailed(string error) { Status = UploadStatus.Failed; Error = error.Length > 500 ? error[..500] : error; }
    public void MarkAborted() => Status = UploadStatus.Aborted;

    // ---- ingest stages (Step 1.4). Each moves the stage forward and can be repeated safely by checking the current stage first.
    public bool IsAt(string stage) => Status == UploadStatus.Ready && IngestStage == stage;

    public void MarkScanned(Dictionary<string, JsonElement> scan) { IngestStage = "scanned"; Merge(scan); }

    public void MarkSniffed(string mime, Dictionary<string, JsonElement> sniff) { IngestStage = "sniffed"; DetectedMime = mime; Merge(sniff); }

    public void MarkExtracted(Dictionary<string, JsonElement> extracted) { IngestStage = "extracted"; Merge(extracted); }

    public void MarkIngested() { Status = UploadStatus.Ingested; IngestStage = "promoted"; }

    public void MarkBlocked(string reason) { Status = UploadStatus.Blocked; IngestStage = "blocked"; Error = reason.Length > 500 ? reason[..500] : reason; }

    public void MarkRejected(string reason) { Status = UploadStatus.Failed; IngestStage = "rejected"; Error = reason.Length > 500 ? reason[..500] : reason; }

    private void Merge(Dictionary<string, JsonElement> more)
    {
        var merged = new Dictionary<string, JsonElement>(TechnicalMetadata);
        foreach (var (k, v) in more) merged[k] = v;
        TechnicalMetadata = merged; // a new instance, so change tracking sees the edit
    }
    public bool IsOpen => Status == UploadStatus.Initiated;
}

/// <summary>One stored file of an asset. Versions are immutable; restoring an old one creates a new version (spec A4.1).</summary>
public sealed class AssetVersion : TenantEntity
{
    private AssetVersion() { }

    public Guid AssetId { get; private set; }
    public int VersionNo { get; private set; }
    public string Bucket { get; private set; } = "";
    public string StorageKey { get; private set; } = "";
    public long SizeBytes { get; private set; }
    public string Mime { get; private set; } = "";
    public string Sha256 { get; private set; } = "";
    public Dictionary<string, JsonElement> TechnicalMetadata { get; private set; } = [];
    public string ChangeNote { get; private set; } = "";
    /// <summary>The upload that produced this version. Makes promotion safe to repeat: one upload, one version.</summary>
    public Guid UploadId { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The id is derived from the upload, so a redelivered message finds the same version instead of making a second.</summary>
    public static Guid IdFor(Guid uploadId)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(uploadId.ToByteArray().Concat("version"u8.ToArray()).ToArray());
        return new Guid(bytes.AsSpan(0, 16));
    }

    public static string KeyFor(Guid tenantId, Guid assetId, Guid versionId) => $"{tenantId:N}/{assetId:N}/{versionId:N}";

    public static AssetVersion Create(Guid tenantId, Guid assetId, int versionNo, string bucket, Guid uploadId, long size, string mime, string sha256,
        Dictionary<string, JsonElement> technical, string changeNote, Guid? createdBy, DateTimeOffset now)
    {
        var id = IdFor(uploadId);
        return new AssetVersion
        {
            Id = id,
            TenantId = tenantId,
            AssetId = assetId,
            VersionNo = versionNo,
            Bucket = bucket,
            StorageKey = KeyFor(tenantId, assetId, id),
            SizeBytes = size,
            Mime = mime,
            Sha256 = sha256,
            TechnicalMetadata = technical,
            ChangeNote = changeNote,
            UploadId = uploadId,
            CreatedBy = createdBy,
            CreatedAt = now,
        };
    }

    /// <summary>A new version whose bytes are a copy of an earlier one (spec A4.1: "rollback creates a new version pointing at
    /// old storage"). Versions are immutable, so restoring never rewrites history; it just makes an old file current again.
    /// There is no upload behind it, so <see cref="UploadId"/> is a fresh id with no other meaning than satisfying the
    /// one-version-per-upload uniqueness rule.</summary>
    public static AssetVersion Restore(Guid tenantId, Guid assetId, int versionNo, string bucket, AssetVersion source, Guid? createdBy, DateTimeOffset now) =>
        Create(tenantId, assetId, versionNo, bucket, Uuid7.NewGuid(), source.SizeBytes, source.Mime, source.Sha256,
            new Dictionary<string, JsonElement>(source.TechnicalMetadata), $"Restored from version {source.VersionNo}", createdBy, now);
}

public sealed record AssetVersionAdded(Guid AssetId, Guid VersionId, int VersionNo) : DomainEvent, IEntityEvent
{
    public override string Name => "asset.version.added";
    public string EntityType => "asset";
    public Guid EntityId => AssetId;
}

public sealed record IngestScanned(Guid UploadId, Guid AssetId) : DomainEvent, IEntityEvent
{
    public override string Name => "ingest.scanned";
    public string EntityType => "upload";
    public Guid EntityId => UploadId;
}

public sealed record IngestSniffed(Guid UploadId, Guid AssetId) : DomainEvent, IEntityEvent
{
    public override string Name => "ingest.sniffed";
    public string EntityType => "upload";
    public Guid EntityId => UploadId;
}

public sealed record IngestExtracted(Guid UploadId, Guid AssetId) : DomainEvent, IEntityEvent
{
    public override string Name => "ingest.extracted";
    public string EntityType => "upload";
    public Guid EntityId => UploadId;
}

/// <summary>The virus scan found malware; the asset is blocked and the file is kept apart.</summary>
public sealed record IngestBlocked(Guid UploadId, Guid AssetId, string Signature) : DomainEvent, IEntityEvent
{
    public override string Name => "ingest.blocked";
    public string EntityType => "upload";
    public Guid EntityId => UploadId;
}

/// <summary>The file was refused (wrong content for its type, executable, oversize…). Recorded for the audit trail.</summary>
public sealed record IngestRejected(Guid UploadId, Guid AssetId, string Reason) : DomainEvent, IEntityEvent
{
    public override string Name => "ingest.rejected";
    public string EntityType => "upload";
    public Guid EntityId => UploadId;
}

public sealed record UploadCompleted(Guid UploadId, Guid AssetId) : DomainEvent, IEntityEvent
{
    public override string Name => "upload.completed";
    public string EntityType => "upload";
    public Guid EntityId => UploadId;
}

public sealed record UploadDuplicate(Guid UploadId, Guid AssetId, Guid DuplicateOfAssetId) : DomainEvent, IEntityEvent
{
    public override string Name => "upload.duplicate";
    public string EntityType => "upload";
    public Guid EntityId => UploadId;
}

/// <summary>Published once a file is safely in staging and hashed; the ingest pipeline (Step 1.4) consumes it.</summary>
public sealed record IngestRequested(Guid UploadId, Guid AssetId, string Bucket, string StorageKey, string FileName, string ContentType, long SizeBytes, string Sha256)
    : DomainEvent, IEntityEvent
{
    public override string Name => "ingest.requested";
    public string EntityType => "asset";
    public Guid EntityId => AssetId;
}

/// <summary>Derives term codes and access attributes from metadata using the schema's field definitions.</summary>
public static class MetadataProjector
{
    public static Asset.Projection Project(IReadOnlyList<FieldDefinition> fields, IReadOnlyDictionary<string, JsonElement> metadata)
    {
        var terms = new List<string>();
        var attributes = new Dictionary<string, List<string>>();
        foreach (var f in fields)
        {
            if (f.Type is not (FieldType.Vocabulary or FieldType.TermTree) || !metadata.TryGetValue(f.Name, out var value)) continue;
            var codes = value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                : value.ValueKind == JsonValueKind.String ? [value.GetString()!] : [];
            terms.AddRange(codes);
            if (f.AccessDimension is { } dim)
            {
                if (!attributes.TryGetValue(dim, out var list)) attributes[dim] = list = [];
                list.AddRange(codes);
            }
        }
        return new Asset.Projection(terms.Distinct().Order().ToList(), attributes.ToDictionary(kv => kv.Key, kv => kv.Value.Distinct().Order().ToArray()));
    }
}
