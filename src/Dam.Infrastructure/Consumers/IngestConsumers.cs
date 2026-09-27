using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Infrastructure.Ingest;
using Dam.Infrastructure.Messaging;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dam.Infrastructure.Consumers;

/// <summary>
/// The ingest pipeline runs in the worker as a chain of small consumers, each triggered by the previous stage's event:
/// hash -> scan -> sniff -> extract -> promote -> perceptual hash. Every stage checks the upload's recorded stage first, so a
/// redelivered message is harmless, and a stage that gives up marks the upload failed with a reason instead of leaving it hanging.
/// </summary>
public abstract class IngestConsumer(DamDbContext db, IObjectStore store, IOutbox outbox, IClock clock) : EventConsumer
{
    protected DamDbContext Db => db;
    protected IObjectStore Store => store;
    protected IOutbox Outbox => outbox;
    protected IClock Clock => clock;

    protected async Task<Upload?> LoadAsync(EventEnvelope message, CancellationToken ct)
    {
        var id = JsonDocument.Parse(message.Payload).RootElement.GetProperty("UploadId").GetGuid();
        return await db.Uploads.FirstOrDefaultAsync(u => u.Id == id, ct);
    }

    protected static Dictionary<string, JsonElement> Meta(string key, object value) => new() { [key] = JsonSerializer.SerializeToElement(value) };

    /// <summary>The worker gave up: say so. A file must never sit half-ingested with nothing to tell the user why.</summary>
    public override async Task OnParkedAsync(EventEnvelope message, string error, CancellationToken ct)
    {
        var upload = await LoadAsync(message, ct);
        if (upload is { Status: UploadStatus.Ready }) upload.MarkFailed($"Ingest stopped at the '{upload.IngestStage}' step after several attempts: {error}");
    }

    protected async Task RejectAsync(Upload upload, string reason, CancellationToken ct)
    {
        upload.MarkRejected(reason);
        await store.DeleteAsync(upload.Bucket, upload.StorageKey, ct);   // refused bytes are not kept
        await outbox.EnqueueAsync([new IngestRejected(upload.Id, upload.AssetId, reason)], ct);
    }
}

/// <summary>Scans the staged file with ClamAV. Malware blocks the asset and the file is moved aside; no verdict means no promotion.</summary>
public sealed class ScanUploadConsumer(DamDbContext db, IObjectStore store, IOutbox outbox, IClock clock, IVirusScanner scanner, ScanSettings settings, ILogger<ScanUploadConsumer> log)
    : IngestConsumer(db, store, outbox, clock)
{
    public override string Name => "ingest-scan";
    public override IReadOnlyList<string> RoutingKeys => ["ingest.requested"];

    public override async Task HandleAsync(EventEnvelope message, CancellationToken ct)
    {
        var upload = await LoadAsync(message, ct);
        if (upload is null || !upload.IsAt("hashed")) return;
        var size = upload.SizeBytes ?? upload.DeclaredSize;

        await using var content = await Store.OpenReadAsync(upload.Bucket, upload.StorageKey, ct);
        var outcome = await scanner.ScanAsync(content, size, ct); // throws when clamd cannot be reached: retried, then the upload fails

        var record = Meta("scan", new
        {
            engine = outcome.Engine,
            verdict = outcome.Verdict.ToString().ToLowerInvariant(),
            signature = outcome.Signature,
            note = outcome.Note,
            at = Clock.UtcNow,
        });

        switch (outcome.Verdict)
        {
            case ScanVerdict.Infected:
                {
                    var reason = $"Virus detected: {outcome.Signature}";
                    upload.MarkBlocked(reason);
                    var asset = await Db.Assets.FirstAsync(a => a.Id == upload.AssetId, ct);
                    asset.ChangeStatus(AssetStatuses.Blocked, reason, Clock.UtcNow);
                    // Keep the evidence apart from ordinary staging, then remove it from the normal path.
                    var quarantineKey = $"infected/{upload.StorageKey}";
                    await Store.CopyAsync(upload.Bucket, upload.StorageKey, upload.Bucket, quarantineKey, size, ct);
                    await Store.DeleteAsync(upload.Bucket, upload.StorageKey, ct);
                    await Outbox.EnqueueAsync([new IngestBlocked(upload.Id, upload.AssetId, outcome.Signature ?? "unknown")], ct);
                    log.LogWarning("Upload {Upload} blocked: {Reason}", upload.Id, reason);
                    return;
                }
            case ScanVerdict.Skipped when settings.Oversize == "block" && outcome.Note?.StartsWith("oversize", StringComparison.Ordinal) == true:
                await RejectAsync(upload, "The file is too large to be scanned for malware, and this tenant does not accept unscanned files.", ct);
                return;
        }
        upload.MarkScanned(record);
        await Outbox.EnqueueAsync([new IngestScanned(upload.Id, upload.AssetId)], ct);
    }
}

/// <summary>Identifies the real content from its first bytes and refuses anything that does not suit the asset type.</summary>
public sealed class SniffUploadConsumer(DamDbContext db, IObjectStore store, IOutbox outbox, IClock clock) : IngestConsumer(db, store, outbox, clock)
{
    public override string Name => "ingest-sniff";
    public override IReadOnlyList<string> RoutingKeys => ["ingest.scanned"];

    public override async Task HandleAsync(EventEnvelope message, CancellationToken ct)
    {
        var upload = await LoadAsync(message, ct);
        if (upload is null || !upload.IsAt("scanned")) return;
        var asset = await Db.Assets.FirstAsync(a => a.Id == upload.AssetId, ct);

        var size = upload.SizeBytes ?? upload.DeclaredSize;
        if (size > UploadPolicy.MaxSize(asset.Type))
        {
            await RejectAsync(upload, $"The file is larger than the {UploadPolicy.MaxSize(asset.Type) / UploadPolicy.Gib} GB limit for '{asset.Type}' assets.", ct);
            return;
        }

        var header = await Store.ReadRangeAsync(upload.Bucket, upload.StorageKey, 0, ContentSniffer.HeaderBytes, ct);
        var sniff = ContentSniffer.Sniff(header);
        var verdict = ContentSniffer.Judge(asset.Type, upload.FileName, sniff);
        if (!verdict.Allowed)
        {
            await RejectAsync(upload, verdict.Reason!, ct);
            return;
        }
        upload.MarkSniffed(sniff.Mime, Meta("sniff", new
        {
            kind = sniff.Kind.ToString(),
            format = sniff.Format,
            mime = sniff.Mime,
            declaredExtension = UploadPolicy.Extension(upload.FileName),
            warnings = verdict.Warnings,
        }));
        await Outbox.EnqueueAsync([new IngestSniffed(upload.Id, upload.AssetId)], ct);
    }
}

/// <summary>Reads descriptive technical metadata (EXIF, video streams, PDF properties). Best-effort: a parse problem is a warning.</summary>
public sealed class ExtractUploadConsumer(DamDbContext db, IObjectStore store, IOutbox outbox, IClock clock, TechnicalMetadataExtractor extractor)
    : IngestConsumer(db, store, outbox, clock)
{
    public override string Name => "ingest-extract";
    public override IReadOnlyList<string> RoutingKeys => ["ingest.sniffed"];

    public override async Task HandleAsync(EventEnvelope message, CancellationToken ct)
    {
        var upload = await LoadAsync(message, ct);
        if (upload is null || !upload.IsAt("sniffed")) return;

        var sniff = upload.TechnicalMetadata["sniff"];
        var kind = Enum.Parse<ContentKind>(sniff.GetProperty("kind").GetString()!);
        var format = sniff.GetProperty("format").GetString()!;
        var extracted = await extractor.ExtractAsync(kind, format, upload.Bucket, upload.StorageKey, upload.SizeBytes ?? upload.DeclaredSize, upload.FileName, ct);
        upload.MarkExtracted(extracted);
        await Outbox.EnqueueAsync([new IngestExtracted(upload.Id, upload.AssetId)], ct);
    }
}

/// <summary>Stores the checked file as the asset's next version in the originals bucket and removes the staged copy.</summary>
public sealed class PromoteUploadConsumer(DamDbContext db, IObjectStore store, IOutbox outbox, IClock clock) : IngestConsumer(db, store, outbox, clock)
{
    public override string Name => "ingest-promote";
    public override IReadOnlyList<string> RoutingKeys => ["ingest.extracted"];

    public override async Task HandleAsync(EventEnvelope message, CancellationToken ct)
    {
        var upload = await LoadAsync(message, ct);
        if (upload is null || !upload.IsAt("extracted")) return;
        var asset = await Db.Assets.FirstAsync(a => a.Id == upload.AssetId, ct);
        var size = upload.SizeBytes ?? upload.DeclaredSize;

        var versionId = AssetVersion.IdFor(upload.Id);
        var existing = await Db.AssetVersions.FirstOrDefaultAsync(v => v.Id == versionId, ct);
        if (existing is null)
        {
            var next = (await Db.AssetVersions.Where(v => v.AssetId == asset.Id).MaxAsync(v => (int?)v.VersionNo, ct) ?? 0) + 1;
            var technical = new Dictionary<string, JsonElement>(upload.TechnicalMetadata)
            {
                ["file"] = JsonSerializer.SerializeToElement(new { fileName = upload.FileName, sizeBytes = size, sha256 = upload.Sha256, mime = upload.DetectedMime }),
            };
            existing = AssetVersion.Create(upload.TenantId, asset.Id, next, Store.OriginalsBucket, upload.Id, size, upload.DetectedMime ?? "application/octet-stream",
                upload.Sha256!, technical, next == 1 ? "Initial upload" : "New version uploaded", upload.CreatedBy, Clock.UtcNow);
            Db.AssetVersions.Add(existing);

            // Copy first. If this message is redelivered after a partial failure the key is the same, so the copy simply repeats.
            if (await Store.HeadAsync(existing.Bucket, existing.StorageKey, ct) is not { } already || already.Size != size)
                await Store.CopyAsync(upload.Bucket, upload.StorageKey, existing.Bucket, existing.StorageKey, size, ct);
            var stored = await Store.HeadAsync(existing.Bucket, existing.StorageKey, ct);
            if (stored is null || stored.Size != size) throw new InvalidOperationException("The stored copy does not match the uploaded file's size.");
        }

        asset.SetCurrentVersion(existing.Id, Clock.UtcNow);
        await Store.DeleteAsync(upload.Bucket, upload.StorageKey, ct); // idempotent: deleting an absent object is fine
        upload.MarkIngested();
        await Outbox.EnqueueAsync([new AssetVersionAdded(asset.Id, existing.Id, existing.VersionNo)], ct);
    }
}

/// <summary>Computes a perceptual hash of new image versions so near-duplicates (resized, recompressed) can be found.</summary>
public sealed class PerceptualHashConsumer(DamDbContext db, IObjectStore store, IOutbox outbox, IClock clock, ILogger<PerceptualHashConsumer> log)
    : IngestConsumer(db, store, outbox, clock)
{
    private const long MaxImageBytes = 256L * 1024 * 1024;

    public override string Name => "ingest-phash";
    public override IReadOnlyList<string> RoutingKeys => ["asset.version.added"];

    /// <summary>Nothing depends on this stage, so when it gives up the upload stays ingested.</summary>
    public override Task OnParkedAsync(EventEnvelope message, string error, CancellationToken ct) => Task.CompletedTask;

    public override async Task HandleAsync(EventEnvelope message, CancellationToken ct)
    {
        var doc = JsonDocument.Parse(message.Payload).RootElement;
        var assetId = doc.GetProperty("AssetId").GetGuid();
        var versionId = doc.GetProperty("VersionId").GetGuid();

        var version = await Db.AssetVersions.FirstOrDefaultAsync(v => v.Id == versionId, ct);
        var asset = await Db.Assets.FirstOrDefaultAsync(a => a.Id == assetId, ct);
        if (version is null || asset is null || asset.CurrentVersionId != versionId) return;                  // superseded by a newer version
        if (!version.Mime.StartsWith("image/", StringComparison.Ordinal) || version.Mime == "image/svg+xml") return;
        if (version.SizeBytes > MaxImageBytes) { log.LogInformation("Image {Version} is too large for a perceptual hash", version.Id); return; }

        await using var stream = await Store.OpenReadAsync(version.Bucket, version.StorageKey, ct);
        var hash = ImageHasher.HashOf(stream);
        if (hash is null) { log.LogInformation("Image {Version} could not be decoded for a perceptual hash", version.Id); return; }
        asset.SetPhash(PerceptualHash.ToHex(hash.Value), Clock.UtcNow);
    }
}

/// <summary>
/// Makes the image renditions of a new version ahead of time so the first viewer does not wait. Anything missed is made on demand by
/// the delivery origin, so this stage never blocks or fails an upload.
/// </summary>
public sealed class RenditionConsumer(DamDbContext db, IObjectStore store, IOutbox outbox, IClock clock, Delivery.RenditionService renditions, ILogger<RenditionConsumer> log)
    : IngestConsumer(db, store, outbox, clock)
{
    public override string Name => "renditions";
    public override IReadOnlyList<string> RoutingKeys => ["asset.version.added"];
    public override Task OnParkedAsync(EventEnvelope message, string error, CancellationToken ct) => Task.CompletedTask;

    public override async Task HandleAsync(EventEnvelope message, CancellationToken ct)
    {
        var versionId = JsonDocument.Parse(message.Payload).RootElement.GetProperty("VersionId").GetGuid();
        var version = await Db.AssetVersions.FirstOrDefaultAsync(v => v.Id == versionId, ct);
        if (version is null || !Delivery.RenditionService.CanRender(version)) return;

        try { await renditions.GenerateEagerAsync(version, ct); }
        catch (Delivery.UnsupportedImageException ex)
        {
            log.LogInformation("Version {Version} cannot be rendered: {Reason}", version.Id, ex.Message);
        }
        catch (Delivery.SourceTooLargeException ex)
        {
            log.LogInformation("Version {Version} is too large to render eagerly: {Reason}", version.Id, ex.Message);
        }
        catch (Delivery.MediaToolMissingException ex)
        {
            log.LogWarning("Renditions for {Version} were skipped: {Reason}", version.Id, ex.Message);
        }
        catch (Delivery.MediaToolFailedException ex)
        {
            log.LogInformation("Version {Version} could not be fully rendered: {Reason}", version.Id, ex.Message);
        }
    }
}
