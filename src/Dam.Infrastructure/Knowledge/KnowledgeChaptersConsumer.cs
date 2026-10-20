using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Domain.Assets;
using Dam.Domain.Knowledge;
using Dam.Infrastructure.Messaging;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dam.Infrastructure.Knowledge;

public static class KnowledgeRegistration
{
    /// <summary>The worker side of the knowledge module (ADR-0039): the consumer that reads a document's chapters.</summary>
    public static IServiceCollection AddDamKnowledgeWorker(this IServiceCollection services) => services.AddEventConsumer<KnowledgeChaptersConsumer>();
}

/// <summary>
/// Reads the table of contents of knowledge documents (ADR-0039 decision 7): a PDF's bookmarks, the headings of an HTML file, or the headings of the entry page of
/// an HTML5 bundle. Runs when a new version of an asset that has a knowledge document arrives, and when a document is created or a re-index is asked for. It always
/// reads the asset's CURRENT version, so a message about a version that was already replaced does the right thing too.
/// </summary>
/// <remarks>
/// Parsing a large PDF or bundle can take seconds, so the consumer opts out of the host transaction (ADR-0038): it holds no transaction and no audit-chain lock while it
/// reads, and saves the chapters in one short write. Idempotent: a version whose chapters are already indexed is not read again, so a redelivery is harmless.
/// </remarks>
public sealed class KnowledgeChaptersConsumer(DamDbContext db, IObjectStore store, IClock clock, ILogger<KnowledgeChaptersConsumer> log) : EventConsumer
{
    /// <summary>A PDF larger than this is not read for its outline (the same ceiling the ingest metadata extractor uses).</summary>
    public const long MaxPdfBytes = 256L * 1024 * 1024;

    /// <summary>A bundle larger than this is not opened.</summary>
    public const long MaxBundleBytes = 2L * 1024 * 1024 * 1024;

    public override string Name => "knowledge-chapters";
    public override IReadOnlyList<string> RoutingKeys => ["asset.version.added", KnowledgeDocumentCreated.EventName, KnowledgeReindexRequested.EventName];
    public override bool RunsInHostTransaction => false;

    /// <summary>The document simply keeps the chapters it has; nothing depends on this stage.</summary>
    public override Task OnParkedAsync(EventEnvelope message, string error, CancellationToken ct) => Task.CompletedTask;

    public override async Task HandleAsync(EventEnvelope message, CancellationToken ct)
    {
        using var payload = JsonDocument.Parse(message.Payload);
        var root = payload.RootElement;
        if (!root.TryGetProperty("AssetId", out var a) || !a.TryGetGuid(out var assetId)) return;
        var force = message.Name == KnowledgeReindexRequested.EventName && root.TryGetProperty("Force", out var f) && f.ValueKind == JsonValueKind.True;

        var doc = await db.KnowledgeDocuments.FirstOrDefaultAsync(d => d.AssetId == assetId, ct);
        if (doc is null) return;                                          // not a knowledge asset (or the mapping was removed): nothing to do
        var versionId = await db.Assets.AsNoTracking().Where(x => x.Id == assetId).Select(x => x.CurrentVersionId).FirstOrDefaultAsync(ct);
        if (versionId is null) return;                                    // no file yet: the version event will come
        if (!force && doc.ChaptersIndexedVersionId == versionId) return;  // already done (a redelivery, or a second trigger for the same version)

        var version = await db.AssetVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId, ct);
        if (version is null) return;

        if (!force && doc.ChaptersSource == ChapterSources.Manual)
        {
            doc.KeepManualFor(version.Id, clock.UtcNow);
            await db.SaveChangesAsync(ct);
            return;
        }

        var assetType = await db.Assets.AsNoTracking().Where(x => x.Id == assetId).Select(x => x.Type).FirstAsync(ct);
        var reading = await ReadAsync(version, assetType, ct);           // the slow part: no transaction is open

        // Things may have changed while the file was read: a person may have entered chapters, or a newer version may have arrived (its own message handles it).
        await db.Entry(doc).ReloadAsync(ct);
        if (!force && doc.ChaptersSource == ChapterSources.Manual)
        {
            doc.KeepManualFor(version.Id, clock.UtcNow);
            await db.SaveChangesAsync(ct);
            return;
        }
        var current = await db.Assets.AsNoTracking().Where(x => x.Id == assetId).Select(x => x.CurrentVersionId).FirstOrDefaultAsync(ct);
        if (current != version.Id) return;

        doc.ApplyIndexed(version.Id, reading.Source, reading.Chapters, clock.UtcNow);
        await db.SaveChangesAsync(ct);
        if (reading.Truncated) log.LogInformation("The outline of version {Version} has more than {Max} entries; the rest were left out", version.Id, KnowledgeLimits.MaxChapters);
    }

    private async Task<ChapterReading> ReadAsync(AssetVersion version, string assetType, CancellationToken ct)
    {
        try
        {
            if (version.Mime == "application/pdf")
            {
                if (version.SizeBytes > MaxPdfBytes) { log.LogInformation("Version {Version} is too large to read for chapters", version.Id); return ChapterReading.Empty; }
                await using var s = await store.OpenReadAsync(version.Bucket, version.StorageKey, ct);
                using var ms = new MemoryStream();
                await s.CopyToAsync(ms, ct);
                return PdfOutlineReader.Read(ms.ToArray());
            }
            if (version.Mime == "text/html")
            {
                var bytes = await store.ReadRangeAsync(version.Bucket, version.StorageKey, 0, Math.Min(version.SizeBytes, HtmlHeadingReader.MaxHtmlChars), ct);
                return HtmlHeadingReader.Read(BundleEntryReader.DecodeText(bytes));
            }
            if (version.Mime.Contains("zip", StringComparison.OrdinalIgnoreCase) && assetType is "html5" or "manual")
            {
                if (version.SizeBytes > MaxBundleBytes) { log.LogInformation("Version {Version} is too large to open as a bundle", version.Id); return ChapterReading.Empty; }
                // Opening the zip reads through a ranged view of the stored object, which blocks; run it off the consumer's own flow.
                return await Task.Run(() =>
                {
                    using var zip = new ObjectRangeStream(store, version.Bucket, version.StorageKey, version.SizeBytes, ct);
                    var (html, _) = BundleEntryReader.ReadEntryPage(zip);
                    return html is null ? ChapterReading.Empty : HtmlHeadingReader.Read(html);
                }, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not Amazon.S3.AmazonS3Exception)
        {
            // A file that cannot be read has no chapters; it must never fail the message (and so the retries) over a damaged document.
            log.LogInformation("Version {Version} could not be read for chapters: {Reason}", version.Id, ex.GetType().Name);
        }
        return ChapterReading.Empty;
    }
}
