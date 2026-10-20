using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Domain.Ai;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Infrastructure.Delivery;
using Dam.Infrastructure.Persistence;
using Dam.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dam.Infrastructure.Ai;

/// <summary>At most this many enrichments make provider calls at once in this process (DAM_AI_CONCURRENCY), however many messages are being handled.</summary>
public sealed class AiConcurrency(AiOptions options) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(options.Concurrency, options.Concurrency);
    public Task WaitAsync(CancellationToken ct) => _gate.WaitAsync(ct);
    public void Release() => _gate.Release();
    public void Dispose() => _gate.Dispose();
}

internal enum EnrichKind { Image, Document, Video, Audio, Unsupported }

internal sealed class NoAudioException() : Exception("The file has no audio track.");
internal sealed class SourceMissingException(string profile) : Exception($"No '{profile}' rendition could be made to analyse.");

/// <summary>
/// Enriches one version of one asset (ADR-0035): suggested tags, the text in pictures and pages, transcripts, the language, and the vectors semantic search
/// uses. It always works from a rendition (the 1,024 px preview, the 1,920 px web image for OCR, the video poster, the PDF's page images), never the original
/// picture, and extracts audio for transcription with ffmpeg. A step the tenant switched off or the provider cannot do is recorded as skipped. A provider that
/// is unreachable aborts the whole attempt (the worker retries it later); a provider that refuses one input fails only that step.
/// </summary>
public sealed class EnrichmentPipeline(
    DamDbContext db, IObjectStore store, IServiceScopeFactory scopes, DeliverySettings delivery, IAiResolver resolver, AiOptions opts, AudioExtractor audio,
    AiRunTracker tracker, AiConcurrency gate, IClock clock, ITenantContext tenant, ILogger<EnrichmentPipeline> log)
{
    private const int MinTextForLanguage = 20;
    private const float MinLanguageConfidence = 0.5f;

    public async Task RunAsync(Guid assetId, Guid? versionId, bool force, CancellationToken ct)
    {
        var asset = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == assetId, ct);
        if (asset is null || asset.Status is AssetStatuses.Deleted or AssetStatuses.Blocked) return;
        var vid = versionId ?? asset.CurrentVersionId;
        if (vid is null || vid != asset.CurrentVersionId) return;   // no file yet, or a newer version has taken over
        var version = await db.AssetVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == vid, ct);
        if (version is null) return;

        var previous = await db.AiRuns.AsNoTracking().FirstOrDefaultAsync(r => r.AssetId == assetId && r.VersionId == vid, ct);
        if (previous is { Status: AiRunStatuses.Succeeded } && !force) return;   // a redelivery or a backfill of finished work

        var ai = await resolver.ResolveAsync(ct);
        var kind = Classify(version);
        if (!ai.IsActive) { await SkipAsync(assetId, vid.Value, ai.DisabledReason ?? "AI is not enabled for this tenant.", ct); return; }
        if (kind == EnrichKind.Unsupported) { await SkipAsync(assetId, vid.Value, $"Files of type '{version.Mime}' are not analysed.", ct); return; }

        await tracker.MarkRunningAsync(tenant.TenantId, assetId, vid.Value, ct);   // visible straight away, in a transaction of its own
        var run = await db.AiRuns.FirstOrDefaultAsync(r => r.AssetId == assetId && r.VersionId == vid, ct);
        if (run is null) { run = db.AiRuns.Add(AiRun.Create(tenant.TenantId, assetId, vid.Value, clock.UtcNow)).Entity; run.Start(clock.UtcNow); }   // the tracker already started an existing one

        var known = await db.AiSuggestions.AsNoTracking().Where(s => s.AssetId == assetId && s.VersionId == vid).Select(s => new { s.Kind, s.Value }).ToListAsync(ct);
        var work = new Work(asset, version, ai, known.Select(k => (k.Kind, k.Value)).ToHashSet());

        await gate.WaitAsync(ct);
        try
        {
            switch (kind)
            {
                case EnrichKind.Image: await ImageAsync(work, ct); break;
                case EnrichKind.Document: await DocumentAsync(work, ct); break;
                case EnrichKind.Video: await VideoAsync(work, ct); break;
                case EnrichKind.Audio: await AudioAsync(work, ct); break;
            }
        }
        finally { gate.Release(); }

        var suggestions = await PersistAsync(work, ct);
        var now = clock.UtcNow;
        if (work.Ok > 0)
        {
            run.Succeed(work.Steps, now);
            var announced = new AssetAiEnriched(assetId, vid.Value, suggestions);
            db.Outbox.Add(new OutboxMessage { EventType = AssetAiEnriched.EventName, Payload = JsonSerializer.Serialize(announced), CorrelationId = Activity.Current?.TraceId.ToString(), CreatedAt = now });
        }
        else if (work.Failed > 0) run.Fail(work.Steps, work.FirstError ?? "Every step failed.", now);
        else run.Skip("Nothing to do: every step is switched off or not offered by the provider.", work.Steps, now);
        log.LogInformation("AI enrichment of asset {Asset} version {Version}: {Status} ({Ok} ok, {Failed} failed, {Suggestions} suggestions)",
            assetId, vid, run.Status, work.Ok, work.Failed, suggestions);
    }

    private async Task SkipAsync(Guid assetId, Guid versionId, string reason, CancellationToken ct)
    {
        var run = await db.AiRuns.FirstOrDefaultAsync(r => r.AssetId == assetId && r.VersionId == versionId, ct)
                  ?? db.AiRuns.Add(AiRun.Create(tenant.TenantId, assetId, versionId, clock.UtcNow)).Entity;
        run.Skip(reason, [], clock.UtcNow);
    }

    private static EnrichKind Classify(AssetVersion v)
    {
        if (v.Mime.StartsWith("audio/", StringComparison.Ordinal)) return EnrichKind.Audio;
        return RenditionProfiles.KindOf(v.Mime) switch
        {
            SourceKind.Image => EnrichKind.Image,
            SourceKind.Video => EnrichKind.Video,
            SourceKind.Pdf or SourceKind.Office => EnrichKind.Document,
            _ => EnrichKind.Unsupported,
        };
    }

    // ------------------------------------------------------------------------------------------------- flows

    private async Task ImageAsync(Work w, CancellationToken ct)
    {
        await TagStepAsync(w, "preview", ct);
        await OcrImageStepAsync(w, "web", ct);
        await ImageEmbeddingStepAsync(w, "preview", ct);
        await LanguageStepAsync(w, ct);
        await TextEmbeddingStepAsync(w, ct);
    }

    private async Task VideoAsync(Work w, CancellationToken ct)
    {
        await TagStepAsync(w, "poster", ct);
        await ImageEmbeddingStepAsync(w, "poster", ct);
        await TranscribeStepAsync(w, ct);
        await LanguageStepAsync(w, ct);
        await TextEmbeddingStepAsync(w, ct);
    }

    private async Task AudioAsync(Work w, CancellationToken ct)
    {
        await TranscribeStepAsync(w, ct);
        await LanguageStepAsync(w, ct);
        await TextEmbeddingStepAsync(w, ct);
    }

    private async Task DocumentAsync(Work w, CancellationToken ct)
    {
        var first = RenditionProfiles.PdfPageName(1);
        await TagStepAsync(w, first, ct);
        await ImageEmbeddingStepAsync(w, first, ct);

        // A PDF's own text is better than OCR of its pages, and free. Scans and Office files (converted to page images) are read with OCR.
        if (w.Ai.Features.Ocr && w.Version.Mime == "application/pdf")
            await StepAsync(w, "text-layer", allowed: true, why: null, async () =>
            {
                var bytes = await ReadSourceAsync(w.Version, ct);
                var text = await Task.Run(() => PdfTextLayer.TryRead(bytes), ct);
                if (text is not null) { w.OcrText = text; w.OcrModel = "pdf-text-layer"; }
                return ("read", "pdf-text-layer", text is null ? "no text layer (a scan?)" : $"{text.Length} characters");
            });
        if (w.OcrText is null) await OcrPagesStepAsync(w, ct);

        await LanguageStepAsync(w, ct);
        await TextEmbeddingStepAsync(w, ct);
    }

    // ------------------------------------------------------------------------------------------------- steps

    private async Task TagStepAsync(Work w, string profile, CancellationToken ct) =>
        await StepAsync(w, "tags", Allowed(w, w.Ai.Features.Tags, w.Caps.Tags, out var why), why, async () =>
        {
            using var image = new MemoryStream(await RenditionBytesAsync(w.Version, profile, ct), writable: false);
            var r = await w.Provider.TagImageAsync(image, new TagOptions(opts.TagTopK, opts.TagThreshold), ct);
            var skip = w.Asset.Tags.Concat(w.Known.Where(k => k.Kind == AiSuggestionKinds.Tag).Select(k => k.Value));
            w.Tags = TagCandidates.Select(r.Tags.Select(t => (t.Label, t.Score)), skip, opts.TagTopK, opts.TagThreshold ?? 0f).ToList();
            w.TagModel = r.Model;
            return (r, r.Model, $"{w.Tags.Count} new of {r.Tags.Count}");
        });

    private async Task OcrImageStepAsync(Work w, string profile, CancellationToken ct) =>
        await StepAsync(w, "ocr", Allowed(w, w.Ai.Features.Ocr, w.Caps.Ocr, out var why), why, async () =>
        {
            using var image = new MemoryStream(await RenditionBytesAsync(w.Version, profile, ct), writable: false);
            var r = await w.Provider.OcrAsync(image, new OcrOptions(w.Ai.OcrLanguage), ct);
            w.OcrText = string.IsNullOrWhiteSpace(r.Text) ? null : r.Text.Trim();
            w.OcrLanguage = r.Language;
            w.OcrModel = r.Model;
            return (r, r.Model, w.OcrText is null ? "no text found" : $"{w.OcrText.Length} characters");
        });

    private async Task OcrPagesStepAsync(Work w, CancellationToken ct) =>
        await StepAsync(w, "ocr", Allowed(w, w.Ai.Features.Ocr, w.Caps.Ocr, out var why), why, async () =>
        {
            var pages = new List<string>();
            var model = "";
            var limit = Math.Min(opts.MaxOcrPages, KnownPageCount(w.Version) ?? int.MaxValue);   // the file's own page count, when ingest recorded one
            for (var n = 1; n <= limit; n++)
            {
                byte[] bytes;
                try { bytes = await RenditionBytesAsync(w.Version, RenditionProfiles.PdfPageName(n), ct); }
                catch (SourceMissingException) when (n > 1) { break; }   // fewer pages than the cap
                using var image = new MemoryStream(bytes, writable: false);
                var r = await w.Provider.OcrAsync(image, new OcrOptions(w.Ai.OcrLanguage), ct);
                model = r.Model;
                w.OcrLanguage ??= r.Language;
                if (!string.IsNullOrWhiteSpace(r.Text)) pages.Add(r.Text.Trim());
            }
            w.OcrText = pages.Count == 0 ? null : string.Join("\n\n", pages);
            w.OcrModel = model;
            return (model, model, w.OcrText is null ? "no text found" : $"{pages.Count} page(s), {w.OcrText.Length} characters");
        });

    /// <summary>The page count ingest recorded for a PDF (or an Office document's own page or slide count), if any.</summary>
    private static int? KnownPageCount(AssetVersion v)
    {
        foreach (var (section, names) in new[] { ("pdf", new[] { "pages" }), ("office", new[] { "pages", "slides" }) })
            if (v.TechnicalMetadata.TryGetValue(section, out var info) && info.ValueKind == JsonValueKind.Object)
                foreach (var name in names)
                    if (info.TryGetProperty(name, out var p) && (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n) || p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out n)) && n > 0)
                        return n;
        return null;
    }

    private async Task ImageEmbeddingStepAsync(Work w, string profile, CancellationToken ct) =>
        await StepAsync(w, "image-embedding", Allowed(w, w.Ai.Features.Embeddings, w.Caps.ImageEmbeddings, out var why), why, async () =>
        {
            using var image = new MemoryStream(await RenditionBytesAsync(w.Version, profile, ct), writable: false);
            var r = await w.Provider.EmbedImageAsync(image, ct);
            if (r.Dim != opts.ImageDim)
                throw new AiRequestException($"The image model returned {r.Dim}-dimensional vectors but DAM_AI_IMAGE_DIM is {opts.ImageDim}. Set it to match (and reindex).");
            w.ImageVector = VectorMath.Normalize(r.Vector);
            w.ImageModel = r.Model;
            return (r, r.Model, $"{r.Dim} dimensions");
        });

    private async Task TranscribeStepAsync(Work w, CancellationToken ct) =>
        await StepAsync(w, "transcript", Allowed(w, w.Ai.Features.Transcribe, w.Caps.Transcribe, out var why), why, async () =>
        {
            var plan = AudioPlan.For(opts.MaxAudioMinutes);
            using var extracted = await audio.ExtractAsync(w.Version, plan, ct) ?? throw new NoAudioException();
            await using var stream = extracted.Open();
            var r = await w.Provider.TranscribeAsync(stream, new TranscribeOptions(null, plan.Seconds, ExtractedAudio.FileName), ct);
            w.Transcript = r;
            w.TranscriptCapped = r.DurationMs >= plan.Seconds * 1000L - 1000;
            return (r, r.Model, r.Segments.Count == 0 ? "no speech found" : $"{r.Segments.Count} segments, {r.DurationMs / 1000}s{(w.TranscriptCapped ? " (cut at the length limit)" : "")}");
        });

    private async Task LanguageStepAsync(Work w, CancellationToken ct)
    {
        var text = w.Transcript is { Segments.Count: > 0 } t ? string.Join(' ', t.Segments.Select(s => s.Text)) : w.OcrText;
        if (text is null || text.Trim().Length < MinTextForLanguage)
        {
            w.Steps.Add(new AiStep { Name = "language", Status = AiStepStatuses.Skipped, Detail = "Not enough text to tell." });
            return;
        }
        // The transcript's or OCR engine's own answer is a good one when the provider has no detector, or the detector is unsure.
        var own = LanguageCodes.Normalize(w.Transcript?.Language ?? w.OcrLanguage);
        var allowed = Allowed(w, w.Ai.Features.Ocr || w.Ai.Features.Transcribe, w.Caps.DetectLanguage, out var why);
        if (!allowed && own is not null)
        {
            w.Languages.Add((own, 0.6f, w.Transcript?.Model ?? w.OcrModel));
            w.Steps.Add(new AiStep { Name = "language", Status = AiStepStatuses.Ok, Model = w.Transcript?.Model ?? w.OcrModel, Detail = "From the transcript or OCR engine." });
            w.Ok++;
            return;
        }
        await StepAsync(w, "language", allowed, why, async () =>
        {
            var r = await w.Provider.DetectLanguageAsync(text.Length > 2000 ? text[..2000] : text, ct);
            if (r.Confidence >= MinLanguageConfidence) w.Languages.Add((r.Language, r.Confidence, r.Model));
            else if (own is not null) w.Languages.Add((own, 0.6f, w.Transcript?.Model ?? w.OcrModel));
            return (r, r.Model, $"{r.Language} ({r.Confidence:0.00})");
        });
    }

    /// <summary>
    /// One vector for the asset's words: the mean of the embeddings of its title and tags (one piece) and the first chunks of the text read from it or spoken in
    /// it, unit-length. Averaging keeps one vector per asset (a transcript is not one passage), and the title and tags keep a short OCR text anchored to what the
    /// asset is. The vector is derived data: editing the title later does not change it until the asset is enriched again.
    /// </summary>
    private async Task TextEmbeddingStepAsync(Work w, CancellationToken ct) =>
        await StepAsync(w, "text-embedding", Allowed(w, w.Ai.Features.Embeddings, w.Caps.TextEmbeddings, out var why), why, async () =>
        {
            var pieces = new List<string>();
            var head = string.Join(". ", new[] { w.Asset.Title, string.Join(", ", w.Asset.Tags.Concat(w.Tags.Select(t => t.Label)).Distinct()) }.Where(s => s.Length > 0));
            if (head.Length > 0) pieces.Add(head.Length > opts.ChunkChars ? head[..opts.ChunkChars] : head);
            var body = w.Transcript is { Segments.Count: > 0 } t
                ? TextChunker.Chunk(t.Segments, opts.ChunkChars, opts.MaxEmbedChunks - 1)
                : TextChunker.Chunk(w.OcrText, opts.ChunkChars, opts.MaxEmbedChunks - 1);
            pieces.AddRange(body);
            if (pieces.Count == 0) throw new NoTextException();

            var r = await w.Provider.EmbedTextsAsync(pieces, new TextEmbedOptions(TextEmbeddingKind.Text, TextEmbeddingRole.Passage), ct);
            if (r.Vectors.Any(v => v.Length != opts.TextDim))
                throw new AiRequestException($"The text model returned {r.Vectors[0].Length}-dimensional vectors but DAM_AI_TEXT_DIM is {opts.TextDim}. Set it to match (and reindex).");
            w.TextVector = VectorMath.NormalizedMean(r.Vectors.ToList());
            w.TextModel = r.Model;
            return (r, r.Model, $"{pieces.Count} piece(s), {opts.TextDim} dimensions");
        });

    private sealed class NoTextException() : Exception("There is no text to embed.");

    /// <summary>Runs one step: gated off or unsupported is a skip, an unreachable provider aborts the attempt, anything the provider or the source gets wrong fails just this step.</summary>
    private async Task<T?> StepAsync<T>(Work w, string name, bool allowed, string? why, Func<Task<(T Value, string? Model, string? Detail)>> body) where T : class
    {
        if (!allowed)
        {
            w.Steps.Add(new AiStep { Name = name, Status = AiStepStatuses.Skipped, Detail = why });
            return null;
        }
        var sw = Stopwatch.StartNew();
        try
        {
            var (value, model, detail) = await body();
            w.Steps.Add(new AiStep { Name = name, Status = AiStepStatuses.Ok, DurationMs = (int)sw.ElapsedMilliseconds, Model = model, Detail = detail });
            w.Ok++;
            return value;
        }
        catch (AiNotSupportedException e)
        {
            w.Steps.Add(new AiStep { Name = name, Status = AiStepStatuses.Skipped, DurationMs = (int)sw.ElapsedMilliseconds, Detail = e.Message });
            return null;
        }
        catch (Exception e) when (e is NoAudioException or NoTextException)
        {
            w.Steps.Add(new AiStep { Name = name, Status = AiStepStatuses.Skipped, DurationMs = (int)sw.ElapsedMilliseconds, Detail = e.Message });
            return null;
        }
        catch (Exception e) when (e is AiRequestException or SourceMissingException or SourceTooLargeException or UnsupportedImageException or MediaToolMissingException or MediaToolFailedException)
        {
            // Only the message goes into the record: it never contains the content or a credential.
            log.LogInformation("AI step {Step} failed for asset {Asset}: {Reason}", name, w.Asset.Id, e.Message);
            w.Steps.Add(new AiStep { Name = name, Status = AiStepStatuses.Failed, DurationMs = (int)sw.ElapsedMilliseconds, Detail = Trim(e.Message) });
            w.Failed++;
            w.FirstError ??= $"{name}: {Trim(e.Message)}";
            return null;
        }
    }

    private static string Trim(string s) => s.Length > 400 ? s[..400] : s;

    private static bool Allowed(Work w, bool featureOn, bool capable, out string? why)
    {
        why = !featureOn ? "Switched off in this tenant's AI settings." : !capable ? $"Not offered by the {w.Ai.ProviderKind} provider." : null;
        return featureOn && capable;
    }

    // ------------------------------------------------------------------------------------------------ sources

    /// <summary>
    /// The bytes of one rendition, from the stored copy. The rendition consumer (which runs alongside this one) makes them as soon as a version arrives, so this
    /// waits for the one it needs (<c>DAM_AI_RENDITION_WAIT_SECONDS</c>) instead of racing it: two workers storing the same rendition at once is what the
    /// renditions code tolerates least, and the audit trail's per-tenant lock must not be held across a wait on an AI provider. If it never appears (that
    /// consumer is not running, or the page does not exist) the rendition is made on demand, through a scope and transaction of its own.
    /// </summary>
    private async Task<byte[]> RenditionBytesAsync(AssetVersion version, string profile, CancellationToken ct)
    {
        var (rowProfile, pageNo) = RenditionProfiles.TryParsePdfPage(profile, out var page) ? (RenditionProfiles.PdfPagesProfile, page) : (profile, 0);
        var wait = pageNo > 1 ? TimeSpan.FromSeconds(Math.Min(8, opts.RenditionWait.TotalSeconds)) : opts.RenditionWait;   // later pages may simply not exist
        var until = clock.UtcNow + wait;
        while (true)
        {
            var row = await db.Renditions.AsNoTracking()
                .Where(r => r.VersionId == version.Id && r.Profile == rowProfile && r.PageNo == pageNo && r.Status == RenditionStatus.Ready)
                .Select(r => new { r.Bucket, r.StorageKey, r.SizeBytes }).FirstOrDefaultAsync(ct);
            if (row is not null && await store.HeadAsync(row.Bucket, row.StorageKey, ct) is not null) return await ReadAsync(row.Bucket, row.StorageKey, row.SizeBytes, ct);
            if (clock.UtcNow >= until) break;
            await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
        }

        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<MutableTenantContext>().TenantId = tenant.TenantId;
        var renditions = scope.ServiceProvider.GetRequiredService<RenditionService>();
        var served = await renditions.GetProfileAsync(version, profile, watermark: false, ct) ?? throw new SourceMissingException(profile);
        return await ReadAsync(served.Bucket, served.Key, served.Size, ct);
    }

    private async Task<byte[]> ReadAsync(string bucket, string key, long size, CancellationToken ct)
    {
        await using var s = await store.OpenReadAsync(bucket, key, ct);
        using var ms = new MemoryStream((int)Math.Min(size, int.MaxValue));
        await s.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private async Task<byte[]> ReadSourceAsync(AssetVersion version, CancellationToken ct)
    {
        if (version.SizeBytes > delivery.MaxSourceBytes) throw new SourceTooLargeException(version.SizeBytes, delivery.MaxSourceBytes);
        await using var s = await store.OpenReadAsync(version.Bucket, version.StorageKey, ct);
        using var ms = new MemoryStream((int)Math.Min(version.SizeBytes, int.MaxValue));
        await s.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    // ---------------------------------------------------------------------------------------------- persistence

    private async Task<int> PersistAsync(Work w, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var a = w.Asset;
        var count = 0;
        foreach (var (label, score) in w.Tags)
        {
            db.AiSuggestions.Add(AiSuggestion.Create(a.TenantId, a.Id, w.Version.Id, AiSuggestionKinds.Tag, label, score, w.TagModel, now));
            count++;
        }
        if (w.Languages.OrderByDescending(l => l.Confidence).FirstOrDefault() is { Code: not null } best
            && !w.Known.Contains((AiSuggestionKinds.Language, best.Code)) && !HasLanguage(a, best.Code))
        {
            db.AiSuggestions.Add(AiSuggestion.Create(a.TenantId, a.Id, w.Version.Id, AiSuggestionKinds.Language, best.Code, best.Confidence, best.Model, now));
            count++;
        }

        if (w.OcrText is not null || w.Transcript is not null)
        {
            var extract = await db.AssetExtracts.FirstOrDefaultAsync(e => e.AssetId == a.Id && e.VersionId == w.Version.Id, ct);
            if (extract is null) { extract = AssetExtract.Create(a.TenantId, a.Id, w.Version.Id, now); db.AssetExtracts.Add(extract); }
            if (w.OcrText is not null) extract.SetOcr(w.OcrText, w.OcrLanguage, string.IsNullOrEmpty(w.OcrModel) ? "pdf-text-layer" : w.OcrModel, now);
            if (w.Transcript is { } t) extract.SetTranscript(t.Segments, t.Language, t.DurationMs, t.Model, now);
        }

        await UpsertEmbeddingAsync(w, EmbeddingKinds.Image, w.ImageVector, w.ImageModel, now, ct);
        await UpsertEmbeddingAsync(w, EmbeddingKinds.Text, w.TextVector, w.TextModel, now, ct);
        return count;
    }

    private async Task UpsertEmbeddingAsync(Work w, string kind, float[]? vector, string model, DateTimeOffset now, CancellationToken ct)
    {
        if (vector is null) return;
        var row = await db.AssetEmbeddings.FirstOrDefaultAsync(e => e.AssetId == w.Asset.Id && e.VersionId == w.Version.Id && e.Kind == kind, ct);
        if (row is null) db.AssetEmbeddings.Add(AssetEmbedding.Create(w.Asset.TenantId, w.Asset.Id, w.Version.Id, kind, model, vector, now));
        else row.Replace(model, vector, now);
    }

    private static bool HasLanguage(Asset a, string code) =>
        a.Metadata.TryGetValue("language", out var v) && (v.ValueKind == JsonValueKind.String
            ? string.Equals(v.GetString(), code, StringComparison.OrdinalIgnoreCase)
            : v.ValueKind == JsonValueKind.Array && v.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && string.Equals(x.GetString(), code, StringComparison.OrdinalIgnoreCase)));

    /// <summary>What one run has gathered so far.</summary>
    private sealed class Work(Asset asset, AssetVersion version, AiTenantContext ai, HashSet<(string Kind, string Value)> known)
    {
        public Asset Asset { get; } = asset;
        public AssetVersion Version { get; } = version;
        public AiTenantContext Ai { get; } = ai;
        public IAiProvider Provider => Ai.Provider;
        public AiCapabilities Caps => Ai.Provider.Capabilities;
        public HashSet<(string Kind, string Value)> Known { get; } = known;
        public List<AiStep> Steps { get; } = [];
        public int Ok { get; set; }
        public int Failed { get; set; }
        public string? FirstError { get; set; }

        public List<(string Label, float Score)> Tags { get; set; } = [];
        public string TagModel { get; set; } = "";
        public string? OcrText { get; set; }
        public string? OcrLanguage { get; set; }
        public string OcrModel { get; set; } = "";
        public TranscriptResult? Transcript { get; set; }
        public bool TranscriptCapped { get; set; }
        public float[]? ImageVector { get; set; }
        public string ImageModel { get; set; } = "";
        public float[]? TextVector { get; set; }
        public string TextModel { get; set; } = "";
        public List<(string Code, float Confidence, string Model)> Languages { get; } = [];
    }
}
