using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Dam.Application.Abstractions;
using NetVips;
using Dam.Domain.Assets;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dam.Infrastructure.Delivery;

/// <summary>An object in storage that can be sent to a client as-is.</summary>
public sealed record ServedObject(string Bucket, string Key, string Mime, long Size, bool CacheHit);

public sealed class SourceTooLargeException(long size, long limit) : Exception($"The source is {size} bytes; on-the-fly rendering is limited to {limit}.");

/// <summary>
/// Produces renditions and transforms from an original, storing each under a deterministic key so the same request is only ever
/// rendered once (spec A3.3 / Step 1.5). Used by the worker (eager profiles) and by the delivery origin (on demand). Dispatches on
/// the version's kind: still images resize directly; video makes a poster frame plus transcodes; a PDF rasterises its pages.
/// </summary>
public sealed class RenditionService(DamDbContext db, IObjectStore store, IOutbox outbox, IClock clock, DeliverySettings settings, VideoRenderer video, PdfRenderer pdf, OfficeConverter office)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

    public static SourceKind? KindOf(AssetVersion v) => RenditionProfiles.KindOf(v.Mime);
    public static bool CanRender(AssetVersion v) => KindOf(v) is not null;

    /// <summary>Every profile eagerly generated for a new version (spec Step 1.4 "asset.version.added"). Never throws for a source it
    /// cannot decode or convert: that is left for the delivery origin to try again on demand and report if it still fails.</summary>
    public async Task GenerateEagerAsync(AssetVersion version, CancellationToken ct)
    {
        switch (KindOf(version))
        {
            case SourceKind.Image:
                var image = await ReadSourceAsync(version, ct);
                foreach (var profile in RenditionProfiles.Images) await StillAsync(version, profile, 0, image, ct);
                break;

            case SourceKind.Video:
                var source = await ReadSourceAsync(version, ct);
                var frame = await video.ExtractFrameAsync(source, ct);
                foreach (var still in RenditionProfiles.VideoStills) await StillAsync(version, still, 0, frame, ct);
                var (frameWidth, frameHeight) = FrameSize(frame);
                foreach (var profile in RenditionProfiles.Videos) await TranscodeAsync(version, profile, source, frameWidth, frameHeight, ct);
                break;

            case SourceKind.Pdf:
                await GeneratePdfDerivedAsync(version, await ReadSourceAsync(version, ct), ct);
                break;

            case SourceKind.Office:
                await GeneratePdfDerivedAsync(version, await AsPdfAsync(version, SourceKind.Office, ct), ct);
                break;
        }
    }

    /// <summary>Everything a PDF's bytes produce: page-1 stills and every page up to the configured limit. Office documents reach
    /// here already converted, so a PDF and a converted Office document are indistinguishable from this point on.</summary>
    private async Task GeneratePdfDerivedAsync(AssetVersion version, byte[] pdfBytes, CancellationToken ct)
    {
        var page1 = await pdf.RasterizePageAsync(pdfBytes, 1, ct);
        foreach (var still in RenditionProfiles.PdfStills) await StillAsync(version, still, 0, page1, ct);
        await StillAsync(version, RenditionProfiles.PdfPage, 1, page1, ct);
        var pages = Math.Min(await pdf.PageCountAsync(pdfBytes, ct), settings.MaxPdfPages);
        for (var n = 2; n <= pages; n++) await StillAsync(version, RenditionProfiles.PdfPage, n, await pdf.RasterizePageAsync(pdfBytes, n, ct), ct);
    }

    /// <summary>The bytes to rasterise as a PDF: the version itself (PDF) or its LibreOffice-converted form (Office).</summary>
    private async Task<byte[]> AsPdfAsync(AssetVersion version, SourceKind kind, CancellationToken ct) =>
        kind == SourceKind.Office
            ? await office.ConvertToPdfAsync(await ReadSourceAsync(version, ct), RenditionProfiles.OfficeExtension(version.Mime), ct)
            : await ReadSourceAsync(version, ct);

    /// <summary>The named still-image profile, video transcode or PDF page for a version: reuses the stored copy, otherwise renders one.</summary>
    public async Task<ServedObject?> GetProfileAsync(AssetVersion version, string name, bool watermark, CancellationToken ct)
    {
        var kind = KindOf(version);
        if (kind is null) return null;

        if (kind is SourceKind.Pdf or SourceKind.Office && RenditionProfiles.TryParsePdfPage(name, out var page))
            return watermark ? null : await GetOrRenderAsync(version, RenditionProfiles.PdfPagesProfile, page, () => RasterizeThenRenderAsync(version, kind.Value, page, RenditionProfiles.PdfPage, ct), ct);

        if (RenditionProfiles.FindImage(kind.Value, name) is { } still)
        {
            if (!watermark) return await GetOrRenderAsync(version, name, 0, () => StillSourceAsync(version, kind.Value, still, ct), ct);
            var frame = await SourceFrameAsync(version, kind.Value, ct);
            return frame is null ? null : await GetCachedAsync(version, $"x/{Hash($"profile:{name}|wm")}", () => ImageRenderer.Render(frame, RenderRequest.ForProfile(still, watermark: true)), ct);
        }

        if (kind == SourceKind.Video && RenditionProfiles.FindVideo(name) is not null)
            // A video is never transcoded out clean for an unpublished asset: watermarking a moving picture is future work (ADR-0007).
            return watermark ? null : await GetOrRenderAsync(version, name, 0, async () =>
            {
                var src = await ReadSourceAsync(version, ct);
                var (w, h) = FrameSize(await video.ExtractFrameAsync(src, ct));
                var rendered = await video.TranscodeAsync(src, RenditionProfiles.FindVideo(name)!, w, h, ct);
                return (new RenderedImage(rendered.Bytes, "video/mp4", rendered.Width, rendered.Height), rendered.DurationSeconds);
            }, ct);

        return null;
    }

    /// <summary>A one-off image transform (or watermarked variant), cached by a hash of exactly what was asked for. Images only.</summary>
    public async Task<ServedObject?> GetTransformAsync(AssetVersion version, TransformSpec spec, bool watermark, CancellationToken ct)
    {
        if (KindOf(version) != SourceKind.Image) return null;
        var source = await ReadSourceAsync(version, ct);
        return await GetCachedAsync(version, $"x/{Hash($"transform:{spec.Canonical()}|wm:{watermark}")}", () => ImageRenderer.Render(source, RenderRequest.ForTransform(spec, watermark)), ct);
    }

    /// <summary>The frame a watermark is drawn onto: the image itself, a video's poster frame, or a PDF's first page.</summary>
    private async Task<byte[]?> SourceFrameAsync(AssetVersion version, SourceKind kind, CancellationToken ct) => kind switch
    {
        SourceKind.Image => await ReadSourceAsync(version, ct),
        SourceKind.Video => await video.ExtractFrameAsync(await ReadSourceAsync(version, ct), ct),
        SourceKind.Pdf or SourceKind.Office => await pdf.RasterizePageAsync(await AsPdfAsync(version, kind, ct), 1, ct),
        _ => null,
    };

    private async Task<(RenderedImage, double?)> StillSourceAsync(AssetVersion version, SourceKind kind, RenditionProfile profile, CancellationToken ct)
    {
        var frame = await SourceFrameAsync(version, kind, ct) ?? throw new InvalidOperationException("No source frame was available.");
        return (ImageRenderer.Render(frame, RenderRequest.ForProfile(profile)), null);
    }

    private async Task<(RenderedImage, double?)> RasterizeThenRenderAsync(AssetVersion version, SourceKind kind, int page, RenditionProfile profile, CancellationToken ct)
    {
        var bytes = await AsPdfAsync(version, kind, ct);
        var count = await pdf.PageCountAsync(bytes, ct);
        if (page > count) throw new PageNotFoundException(page, count);
        return (ImageRenderer.Render(await pdf.RasterizePageAsync(bytes, page, ct), RenderRequest.ForProfile(profile)), null);
    }

    /// <summary>Records a still-image rendition from bytes already produced (the version itself, a page raster, or a poster frame).</summary>
    private Task<ServedObject> StillAsync(AssetVersion version, RenditionProfile profile, int pageNo, byte[] sourceBytes, CancellationToken ct) =>
        StoreAsync(version, profile.Name, pageNo, ImageRenderer.Render(sourceBytes, RenderRequest.ForProfile(profile)), null, ct);

    private async Task TranscodeAsync(AssetVersion version, VideoProfile profile, byte[] source, int sourceWidth, int sourceHeight, CancellationToken ct)
    {
        var rendered = await video.TranscodeAsync(source, profile, sourceWidth, sourceHeight, ct);
        await StoreAsync(version, profile.Name, 0, new RenderedImage(rendered.Bytes, "video/mp4", rendered.Width, rendered.Height), rendered.DurationSeconds, ct);
    }

    private static (int Width, int Height) FrameSize(byte[] png)
    {
        using var img = Image.NewFromBuffer(png);
        return (img.Width, img.Height);
    }

    private async Task<ServedObject?> GetOrRenderAsync(AssetVersion version, string profile, int pageNo, Func<Task<(RenderedImage Image, double? Duration)>> render, CancellationToken ct)
    {
        var existing = await db.Renditions.AsNoTracking().FirstOrDefaultAsync(r => r.VersionId == version.Id && r.Profile == profile && r.PageNo == pageNo && r.Status == RenditionStatus.Ready, ct);
        if (existing is not null && await store.HeadAsync(existing.Bucket, existing.StorageKey, ct) is not null)
            return new ServedObject(existing.Bucket, existing.StorageKey, existing.Mime, existing.SizeBytes, CacheHit: true);

        var key = $"pending:{version.Id:N}:{profile}:{pageNo}";
        var gate = Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            existing = await db.Renditions.AsNoTracking().FirstOrDefaultAsync(r => r.VersionId == version.Id && r.Profile == profile && r.PageNo == pageNo && r.Status == RenditionStatus.Ready, ct);
            if (existing is not null && await store.HeadAsync(existing.Bucket, existing.StorageKey, ct) is not null)
                return new ServedObject(existing.Bucket, existing.StorageKey, existing.Mime, existing.SizeBytes, CacheHit: true);

            (RenderedImage Image, double? Duration) result;
            try { result = await render(); }
            catch (PageNotFoundException) { return null; }
            return await StoreAsync(version, profile, pageNo, result.Image, result.Duration, ct);
        }
        finally { gate.Release(); }
    }

    private sealed class PageNotFoundException(int page, int count) : Exception($"Page {page} does not exist; the document has {count} page(s).");

    private async Task<ServedObject> StoreAsync(AssetVersion version, string profile, int pageNo, RenderedImage rendered, double? duration, CancellationToken ct)
    {
        var key = Rendition.KeyFor(version.TenantId, version.AssetId, version.Id, profile, pageNo, RenditionProfiles.Extension(FormatOf(rendered.Mime)));
        var gate = Locks.GetOrAdd($"store:{key}", _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var bucket = store.RenditionsBucket;
            await store.PutAsync(bucket, key, rendered.Bytes, rendered.Mime, ct);

            var row = await db.Renditions.FirstOrDefaultAsync(r => r.VersionId == version.Id && r.Profile == profile && r.PageNo == pageNo, ct);
            if (row is not null) { db.Renditions.Remove(row); await db.SaveChangesAsync(ct); }   // replace a stale or failed row with the fresh one
            db.Renditions.Add(Rendition.Create(version.TenantId, version.AssetId, version.Id, profile, bucket, key, rendered.Mime,
                rendered.Width, rendered.Height, rendered.Bytes.Length, clock.UtcNow, duration, pageNo));
            await outbox.EnqueueAsync([new RenditionReady(version.AssetId, version.Id, profile)], ct);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { db.ChangeTracker.Clear(); }   // another request stored the same rendition first; the object is identical
            return new ServedObject(bucket, key, rendered.Mime, rendered.Bytes.Length, CacheHit: false);
        }
        finally { gate.Release(); }
    }

    private static string FormatOf(string mime) => mime switch { "video/mp4" => "mp4", "image/webp" => "webp", "image/png" => "png", "image/jpeg" => "jpg", _ => "bin" };

    /// <summary>A rendered image cached under a key whose extension is fixed by the caller's own knowledge of the format (both call
    /// sites here always ask for an image, so the format is settled before rendering starts).</summary>
    private async Task<ServedObject> GetCachedAsync(AssetVersion version, string keyTail, Func<RenderedImage> render, CancellationToken ct)
    {
        var bucket = store.RenditionsBucket;
        var gate = Locks.GetOrAdd($"cache:{version.Id:N}:{keyTail}", _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var rendered = render();
            var key = $"{version.TenantId:N}/{version.AssetId:N}/{version.Id:N}/{keyTail}.{RenditionProfiles.Extension(FormatOf(rendered.Mime))}";
            var existing = await store.HeadAsync(bucket, key, ct);
            if (existing is not null) return new ServedObject(bucket, key, rendered.Mime, existing.Size, CacheHit: true);
            await store.PutAsync(bucket, key, rendered.Bytes, rendered.Mime, ct);
            return new ServedObject(bucket, key, rendered.Mime, rendered.Bytes.Length, CacheHit: false);
        }
        finally { gate.Release(); }
    }

    private async Task<byte[]> ReadSourceAsync(AssetVersion version, CancellationToken ct)
    {
        if (version.SizeBytes > settings.MaxSourceBytes) throw new SourceTooLargeException(version.SizeBytes, settings.MaxSourceBytes);
        await using var s = await store.OpenReadAsync(version.Bucket, version.StorageKey, ct);
        using var ms = new MemoryStream((int)Math.Min(version.SizeBytes, int.MaxValue));
        await s.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private static string Hash(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..32];
}

public static class DeliveryRegistration
{
    /// <summary>Renditions only (the worker's need): no signing key required.</summary>
    public static IServiceCollection AddDamRendering(this IServiceCollection services, IConfiguration cfg)
    {
        var settings = new DeliverySettings();
        if (cfg["DAM_DELIVERY_PUBLIC_BASE_URL"] is { Length: > 0 } pub) settings.PublicBaseUrl = pub;
        if (cfg["DAM_CDN_BASE_URL"] is { Length: > 0 } cdn) settings.CdnBaseUrl = cdn;
        if (int.TryParse(cfg["DAM_DELIVERY_TOKEN_TTL_MINUTES"], out var ttl) && ttl > 0) settings.DefaultTokenTtl = TimeSpan.FromMinutes(ttl);
        if (int.TryParse(cfg["DAM_DELIVERY_TOKEN_MAX_MINUTES"], out var max) && max > 0) settings.MaxTokenTtl = TimeSpan.FromMinutes(max);
        if (settings.DefaultTokenTtl > settings.MaxTokenTtl) settings.DefaultTokenTtl = settings.MaxTokenTtl;
        if (int.TryParse(cfg["DAM_DELIVERY_MAX_AGE_SECONDS"], out var age) && age >= 0) settings.MaxAgeSeconds = age;
        if (long.TryParse(cfg["DAM_RENDER_MAX_SOURCE_MB"], out var mb) && mb > 0) settings.MaxSourceBytes = mb * 1024 * 1024;
        if (int.TryParse(cfg["DAM_PDF_MAX_PAGES"], out var pages) && pages > 0) settings.MaxPdfPages = pages;
        services.AddSingleton(settings);

        var tools = new MediaToolSettings();
        if (cfg["DAM_FFMPEG_PATH"] is { Length: > 0 } ffmpeg) tools.FfmpegPath = ffmpeg;
        if (cfg["DAM_FFPROBE_PATH"] is { Length: > 0 } ffprobe) tools.FfprobePath = ffprobe;
        if (cfg["DAM_PDFTOPPM_PATH"] is { Length: > 0 } toppm) tools.PdftoppmPath = toppm;
        if (cfg["DAM_PDFINFO_PATH"] is { Length: > 0 } info) tools.PdfinfoPath = info;
        if (cfg["DAM_SOFFICE_PATH"] is { Length: > 0 } soffice) tools.SofficePath = soffice;
        if (cfg["DAM_VIDEO_PRESET"] is { Length: > 0 } preset) tools.Preset = preset;
        if (int.TryParse(cfg["DAM_OFFICE_MAX_CONCURRENCY"], out var officeConcurrency) && officeConcurrency > 0) tools.OfficeMaxConcurrency = officeConcurrency;
        services.AddSingleton(tools);
        services.AddSingleton<VideoRenderer>();
        services.AddSingleton<PdfRenderer>();
        services.AddSingleton<OfficeConverter>();
        services.AddScoped<RenditionService>();
        return services;
    }

    /// <summary>Everything the API and the delivery origin need: rendering plus the token signer (DAM_DELIVERY_SIGNING_KEY, comma-separated to rotate).</summary>
    public static IServiceCollection AddDamDelivery(this IServiceCollection services, IConfiguration cfg)
    {
        services.AddDamRendering(cfg);
        var keys = (cfg["DAM_DELIVERY_SIGNING_KEY"] ?? throw new InvalidOperationException("DAM_DELIVERY_SIGNING_KEY is not set (at least 32 characters; separate several with commas to rotate).")).Split(',');
        services.AddSingleton(new DeliveryTokenSigner(keys));
        return services;
    }
}
