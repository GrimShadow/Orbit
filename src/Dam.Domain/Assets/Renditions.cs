using System.Globalization;
using Dam.Domain.Common;

namespace Dam.Domain.Assets;

public static class RenditionStatus
{
    public const string Ready = "ready";
    public const string Failed = "failed";
}

/// <summary>A derived file of an asset version (spec A4.1, A6). Renditions are cached and can always be regenerated from the original.</summary>
public sealed class Rendition : TenantEntity
{
    private Rendition() { }

    public Guid AssetId { get; private set; }
    public Guid VersionId { get; private set; }
    public string Profile { get; private set; } = "";
    public string Bucket { get; private set; } = "";
    public string StorageKey { get; private set; } = "";
    public string Mime { get; private set; } = "";
    public int Width { get; private set; }
    public int Height { get; private set; }
    public long SizeBytes { get; private set; }
    /// <summary>Seconds, for video. Null for stills.</summary>
    public double? Duration { get; private set; }
    /// <summary>1-based page number for a PDF page rendition ("pdf_pages"); 0 for everything else.</summary>
    public int PageNo { get; private set; }
    public string Status { get; private set; } = RenditionStatus.Ready;
    public DateTimeOffset CreatedAt { get; private set; }

    public static string KeyFor(Guid tenantId, Guid assetId, Guid versionId, string profile, int pageNo, string extension) =>
        pageNo > 0
            ? $"{tenantId:N}/{assetId:N}/{versionId:N}/{profile}_{pageNo}.{extension}"
            : $"{tenantId:N}/{assetId:N}/{versionId:N}/{profile}.{extension}";

    /// <summary>The id is derived from the version, profile and page, so generating the same rendition twice finds the same row.</summary>
    public static Guid IdFor(Guid versionId, string profile, int pageNo = 0)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(versionId.ToByteArray().Concat(System.Text.Encoding.UTF8.GetBytes($"rendition:{profile}:{pageNo}")).ToArray());
        return new Guid(bytes.AsSpan(0, 16));
    }

    public static Rendition Create(Guid tenantId, Guid assetId, Guid versionId, string profile, string bucket, string key, string mime,
        int width, int height, long size, DateTimeOffset now, double? duration = null, int pageNo = 0) => new()
    {
        Id = IdFor(versionId, profile, pageNo), TenantId = tenantId, AssetId = assetId, VersionId = versionId, Profile = profile, Bucket = bucket,
        StorageKey = key, Mime = mime, Width = width, Height = height, SizeBytes = size, Duration = duration, PageNo = pageNo, CreatedAt = now,
    };
}

public sealed record RenditionReady(Guid AssetId, Guid VersionId, string Profile) : DomainEvent, IEntityEvent
{
    public override string Name => "rendition.ready";
    public string EntityType => "asset";
    public Guid EntityId => AssetId;
}

/// <summary>What a named still-image profile produces. Resizes to a longest edge and never enlarges.</summary>
public sealed record RenditionProfile(string Name, int LongestEdge, string Format, int Quality);

/// <summary>What a named video transcode produces (spec A6). <see cref="Letterbox"/> exact-fits to <see cref="Width"/>x<see cref="Height"/>
/// with padding (signage); otherwise the output keeps the source's aspect ratio and only <see cref="Height"/> is a target.</summary>
public sealed record VideoProfile(string Name, int Width, int Height, long BitrateBps, int? MaxDurationSeconds, bool Letterbox);

public enum SourceKind { Image, Video, Pdf }

public static class RenditionProfiles
{
    /// <summary>The untouched file. Not generated: served straight from the originals bucket, only to roles cleared for it.</summary>
    public const string Original = "original";

    public static SourceKind? KindOf(string mime) => mime switch
    {
        "image/svg+xml" => null,   // can reference external files and already scales; never rasterised
        _ when mime.StartsWith("image/", StringComparison.Ordinal) => SourceKind.Image,
        _ when mime.StartsWith("video/", StringComparison.Ordinal) => SourceKind.Video,
        "application/pdf" => SourceKind.Pdf,
        _ => null,
    };

    /// <summary>Image profiles made when a version arrives (spec A6).</summary>
    public static readonly IReadOnlyList<RenditionProfile> Images =
    [
        new("thumb", 240, "webp", 75),
        new("preview", 1024, "webp", 80),
        new("web", 1920, "webp", 80),
        new("web_jpeg", 1920, "jpg", 82),   // the fallback for clients without WebP
        new("mobile", 1080, "webp", 80),
    ];

    /// <summary>The still images made from a PDF's first page: the same shapes as the image "thumb" and "preview".</summary>
    public static readonly IReadOnlyList<RenditionProfile> PdfStills = [.. Images.Where(p => p.Name is "thumb" or "preview")];

    /// <summary>Every page, at manual-viewer size (spec A6 "pdf_pages"). One rendition row per page, named by <see cref="PdfPageName"/>.</summary>
    public const string PdfPagesProfile = "pdf_pages";
    public static readonly RenditionProfile PdfPage = new(PdfPagesProfile, 1600, "webp", 80);
    public static string PdfPageName(int page) => $"{PdfPagesProfile}-{page}";
    public static bool TryParsePdfPage(string name, out int page)
    {
        page = 0;
        return name.StartsWith(PdfPagesProfile + "-", StringComparison.Ordinal)
            && int.TryParse(name.AsSpan(PdfPagesProfile.Length + 1), out page) && page > 0;
    }

    /// <summary>The still frame made from a video: a 240px poster for lists and a larger one for a detail view.</summary>
    public static readonly IReadOnlyList<RenditionProfile> VideoStills = [new("thumb", 240, "webp", 75), new("poster", 1024, "webp", 80)];

    /// <summary>Video transcodes made when a version arrives (spec A6). H.264 throughout: simpler and more portable than mixing in H.265.</summary>
    public static readonly IReadOnlyList<VideoProfile> Videos =
    [
        new("preview", 0, 720, 2_000_000, MaxDurationSeconds: 8, Letterbox: false),
        new("mobile", 0, 720, 2_500_000, MaxDurationSeconds: null, Letterbox: false),
        new("signage_1080", 1920, 1080, 12_000_000, MaxDurationSeconds: null, Letterbox: true),
        new("signage_4k", 3840, 2160, 20_000_000, MaxDurationSeconds: null, Letterbox: true),
    ];

    public static RenditionProfile? FindImage(SourceKind kind, string name) => kind switch
    {
        SourceKind.Image => Images.FirstOrDefault(p => p.Name == name),
        SourceKind.Pdf when TryParsePdfPage(name, out _) => PdfPage,
        SourceKind.Pdf => PdfStills.FirstOrDefault(p => p.Name == name),
        SourceKind.Video => VideoStills.FirstOrDefault(p => p.Name == name),
        _ => null,
    };

    public static VideoProfile? FindVideo(string name) => Videos.FirstOrDefault(p => p.Name == name);

    public static bool IsKnown(string name) =>
        name == Original || Images.Any(p => p.Name == name) || VideoStills.Any(p => p.Name == name) ||
        Videos.Any(p => p.Name == name) || TryParsePdfPage(name, out _);

    public static string Extension(string format) => format switch { "jpg" or "jpeg" => "jpg", var f => f };
    public static string MimeOf(string format) => format switch
    {
        "webp" => "image/webp", "avif" => "image/avif", "png" => "image/png", "mp4" => "video/mp4", _ => "image/jpeg",
    };
}

/// <summary>
/// On-the-fly transform parameters (spec A6: w, h, fit, fmt, q, focal). <see cref="Canonical"/> is the one spelling of a request that is
/// signed, so any change to a parameter changes the signature.
/// </summary>
public sealed record TransformSpec(int? Width, int? Height, string Fit, string Format, int Quality, double? FocalX, double? FocalY)
{
    public const int MaxEdge = 8192;
    private static readonly string[] Fits = ["cover", "contain", "fill"];
    private static readonly string[] Formats = ["webp", "avif", "jpg", "png"];

    public string Canonical()
    {
        var parts = new List<string>();
        if (Width is { } w) parts.Add($"w={w}");
        if (Height is { } h) parts.Add($"h={h}");
        parts.Add($"fit={Fit}");
        parts.Add($"fmt={Format}");
        parts.Add($"q={Quality}");
        if (FocalX is { } fx && FocalY is { } fy) parts.Add(string.Create(CultureInfo.InvariantCulture, $"focal={fx:0.###},{fy:0.###}"));
        return string.Join('&', parts);
    }

    /// <summary>Parses and validates. Returns an error message (never throws) so the caller can answer 400 or 422.</summary>
    public static (TransformSpec? Spec, string? Error) Parse(string? w, string? h, string? fit, string? fmt, string? q, string? focal)
    {
        int? width = null, height = null;
        if (!string.IsNullOrEmpty(w)) { if (!int.TryParse(w, NumberStyles.None, CultureInfo.InvariantCulture, out var v) || v < 1 || v > MaxEdge) return (null, $"w must be 1..{MaxEdge}."); width = v; }
        if (!string.IsNullOrEmpty(h)) { if (!int.TryParse(h, NumberStyles.None, CultureInfo.InvariantCulture, out var v) || v < 1 || v > MaxEdge) return (null, $"h must be 1..{MaxEdge}."); height = v; }
        if (width is null && height is null) return (null, "Give w, h or both.");

        var fitValue = string.IsNullOrEmpty(fit) ? "cover" : fit.ToLowerInvariant();
        if (!Fits.Contains(fitValue)) return (null, "fit must be cover, contain or fill.");
        var fmtValue = string.IsNullOrEmpty(fmt) ? "webp" : fmt.ToLowerInvariant();
        if (fmtValue == "jpeg") fmtValue = "jpg";
        if (!Formats.Contains(fmtValue)) return (null, "fmt must be webp, avif, jpg or png.");
        var quality = 80;
        if (!string.IsNullOrEmpty(q) && (!int.TryParse(q, NumberStyles.None, CultureInfo.InvariantCulture, out quality) || quality is < 1 or > 100)) return (null, "q must be 1..100.");

        double? fx = null, fy = null;
        if (!string.IsNullOrEmpty(focal))
        {
            var bits = focal.Split(',');
            if (bits.Length != 2 || !double.TryParse(bits[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) || !double.TryParse(bits[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                || x is < 0 or > 1 || y is < 0 or > 1) return (null, "focal must be two numbers between 0 and 1, like 0.3,0.6.");
            fx = Math.Round(x, 3); fy = Math.Round(y, 3);
        }
        return (new TransformSpec(width, height, fitValue, fmtValue, quality, fx, fy), null);
    }
}
