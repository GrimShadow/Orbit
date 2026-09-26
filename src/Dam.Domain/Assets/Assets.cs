using System.Text.Json;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Content;

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
    /// <summary>Access-control attributes (region, dealer, brand, channel) derived from metadata; see ADR-0002.</summary>
    public Dictionary<string, string[]> Attributes { get; private set; } = [];
    public Guid? OwnerId { get; private set; }
    public Guid? CurrentVersionId { get; private set; }
    public Guid? RightsId { get; private set; }
    public DateTimeOffset? PublishAt { get; private set; }
    public DateTimeOffset? ExpireAt { get; private set; }
    public Guid? RetentionPolicyId { get; private set; }
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
        Attributes = projection.Attributes;
        UpdatedAt = now;
        Raise(new AssetMetadataUpdated(Id));
    }

    public void Rename(string title, DateTimeOffset now) { Title = title.Trim(); UpdatedAt = now; }
    public void SetTags(IEnumerable<string> tags, DateTimeOffset now) { Tags = Normalize(tags); UpdatedAt = now; }
    public void MoveTo(Guid? folderId, DateTimeOffset now) { FolderId = folderId; UpdatedAt = now; }
    public void SetChecksum(string sha256, DateTimeOffset now) { ChecksumSha256 = sha256; UpdatedAt = now; }

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
        Type = "asset", TenantId = TenantId, Status = Status, FolderPath = folderPath, AssetType = Type, OwnerId = OwnerId, Attributes = Attributes,
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
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public static Upload Start(Guid id, Guid tenantId, Guid assetId, Guid? batchId, string fileName, long size, string contentType, string bucket,
        string s3UploadId, int partSize, bool allowDuplicate, Guid? createdBy, DateTimeOffset now, TimeSpan lifetime)
    {
        return new Upload
        {
            Id = id, TenantId = tenantId, AssetId = assetId, BatchId = batchId, Status = UploadStatus.Initiated, FileName = fileName,
            DeclaredSize = size, ContentType = contentType, Bucket = bucket, StorageKey = KeyFor(tenantId, assetId, id), S3UploadId = s3UploadId,
            PartSize = partSize, PartCount = PartsFor(size, partSize), AllowDuplicate = allowDuplicate, CreatedBy = createdBy, CreatedAt = now,
            ExpiresAt = now + lifetime,
        };
    }

    /// <summary>The object key is built from ids only, never from the file name, so it cannot be steered by the client.</summary>
    public static string KeyFor(Guid tenantId, Guid assetId, Guid uploadId) => $"staging/{tenantId:N}/{assetId:N}/{uploadId:N}";

    public static int PartsFor(long size, int partSize) => (int)Math.Max(1, (size + partSize - 1) / partSize);

    public void MarkProcessing(DateTimeOffset now) { Status = UploadStatus.Processing; CompletedAt = now; }
    public void MarkReady(string sha256, long size) { Status = UploadStatus.Ready; Sha256 = sha256; SizeBytes = size; }
    public void MarkDuplicate(string sha256, long size, Guid duplicateOf) { Status = UploadStatus.Duplicate; Sha256 = sha256; SizeBytes = size; DuplicateOfAssetId = duplicateOf; }
    public void MarkFailed(string error) { Status = UploadStatus.Failed; Error = error.Length > 500 ? error[..500] : error; }
    public void MarkAborted() => Status = UploadStatus.Aborted;
    public bool IsOpen => Status == UploadStatus.Initiated;
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
