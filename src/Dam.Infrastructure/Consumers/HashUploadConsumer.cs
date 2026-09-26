using System.Security.Cryptography;
using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Infrastructure.Messaging;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dam.Infrastructure.Consumers;

/// <summary>
/// After an upload is assembled, streams the object from storage and computes its SHA-256 (the server never trusts a
/// client-supplied hash), then decides: duplicate of existing content, or ready for ingest. Runs in the worker so the
/// API is not held up hashing a multi-gigabyte file.
/// </summary>
public sealed class HashUploadConsumer(DamDbContext db, IObjectStore store, IOutbox outbox, IClock clock, ILogger<HashUploadConsumer> log) : EventConsumer
{
    public override string Name => "hash-upload";
    public override IReadOnlyList<string> RoutingKeys => ["upload.completed"];

    public override async Task HandleAsync(EventEnvelope message, CancellationToken ct)
    {
        var uploadId = JsonDocument.Parse(message.Payload).RootElement.GetProperty("UploadId").GetGuid();
        var upload = await db.Uploads.FirstOrDefaultAsync(u => u.Id == uploadId, ct);
        if (upload is null || upload.Status != UploadStatus.Processing) return; // already handled (redelivery) or gone

        var asset = await db.Assets.FirstAsync(a => a.Id == upload.AssetId, ct);
        var head = await store.HeadAsync(upload.Bucket, upload.StorageKey, ct);
        if (head is null) { upload.MarkFailed("The uploaded object is missing from storage."); return; }
        if (head.Size != upload.DeclaredSize)
        {
            upload.MarkFailed($"The uploaded size ({head.Size} bytes) differs from the declared size ({upload.DeclaredSize} bytes).");
            await store.DeleteAsync(upload.Bucket, upload.StorageKey, ct);
            return;
        }

        var (sha256, size) = await HashAsync(upload, ct);

        // Two identical files hashed at the same moment must not both pass: serialise on (tenant, hash) until commit.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({upload.TenantId.ToString("N") + sha256}, 0))", ct);

        var existing = await db.Assets.FirstOrDefaultAsync(a => a.ChecksumSha256 == sha256 && a.Status != AssetStatuses.Deleted && a.Id != asset.Id, ct);
        var sameAsCurrent = asset.ChecksumSha256 == sha256 && asset.CurrentVersionId is not null; // re-sending the file the asset already has
        if ((existing is not null || sameAsCurrent) && !upload.AllowDuplicate)
        {
            var of = existing?.Id ?? asset.Id;
            upload.MarkDuplicate(sha256, size, of);
            await store.DeleteAsync(upload.Bucket, upload.StorageKey, ct);
            // A shell created only for this file has nothing left to hold, so remove it from view.
            if (asset.CurrentVersionId is null && asset.ChecksumSha256 is null && of != asset.Id)
                asset.ChangeStatus(AssetStatuses.Deleted, $"Duplicate of {of}", clock.UtcNow);
            await outbox.EnqueueAsync([new UploadDuplicate(upload.Id, asset.Id, of)], ct);
            log.LogInformation("Upload {Upload} is a duplicate of asset {Of}", upload.Id, of);
            return;
        }

        upload.MarkReady(sha256, size);
        asset.SetChecksum(sha256, clock.UtcNow);
        await outbox.EnqueueAsync([new IngestRequested(upload.Id, asset.Id, upload.Bucket, upload.StorageKey, upload.FileName, upload.ContentType, size, sha256)], ct);
    }

    /// <summary>The worker gave up (e.g. storage unreachable): say so, rather than leaving the upload "processing" forever.</summary>
    public override async Task OnParkedAsync(EventEnvelope message, string error, CancellationToken ct)
    {
        var uploadId = JsonDocument.Parse(message.Payload).RootElement.GetProperty("UploadId").GetGuid();
        var upload = await db.Uploads.FirstOrDefaultAsync(u => u.Id == uploadId, ct);
        if (upload is { Status: UploadStatus.Processing }) upload.MarkFailed($"Processing failed after several attempts: {error}");
    }

    private async Task<(string Sha256, long Size)> HashAsync(Upload upload, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = await store.OpenReadAsync(upload.Bucket, upload.StorageKey, ct);
        var buffer = new byte[1024 * 1024];
        long total = 0;
        int n;
        while ((n = await stream.ReadAsync(buffer, ct)) > 0)
        {
            hash.AppendData(buffer, 0, n);
            total += n;
        }
        return (Convert.ToHexStringLower(hash.GetHashAndReset()), total);
    }
}
