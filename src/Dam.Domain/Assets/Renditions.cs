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
    public string Status { get; private set; } = RenditionStatus.Ready;
    public DateTimeOffset CreatedAt { get; private set; }

    public static string KeyFor(Guid tenantId, Guid assetId, Guid versionId, string profile, string extension) =>
        $"{tenantId:N}/{assetId:N}/{versionId:N}/{profile}.{extension}";

    /// <summary>The id is derived from the version and profile, so generating the same rendition twice finds the same row.</summary>
    public static Guid IdFor(Guid versionId, string profile)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(versionId.ToByteArray().Concat(System.Text.Encoding.UTF8.GetBytes("rendition:" + profile)).ToArray());
        return new Guid(bytes.AsSpan(0, 16));
    }

    public static Rendition Create(Guid tenantId, Guid assetId, Guid versionId, string profile, string bucket, string key, string mime,
        int width, int height, long size, DateTimeOffset now) => new()
    {
        Id = IdFor(versionId, profile), TenantId = tenantId, AssetId = assetId, VersionId = versionId, Profile = profile, Bucket = bucket,
        StorageKey = key, Mime = mime, Width = width, Height = height, SizeBytes = size, CreatedAt = now,
    };
}

public sealed record RenditionReady(Guid AssetId, Guid VersionId, string Profile) : DomainEvent, IEntityEvent
{
    public override string Name => "rendition.ready";
    public string EntityType => "asset";
    public Guid EntityId => AssetId;
}

/// <summary>What a named profile produces. Image profiles resize to a longest edge and never enlarge.</summary>
public sealed record RenditionProfile(string Name, int LongestEdge, string Format, int Quality);

public static class RenditionProfiles
{
    /// <summary>The untouched file. Not generated: served straight from the originals bucket, only to roles cleared for it.</summary>
    public const string Original = "original";

    /// <summary>Image profiles made when a version arrives (spec A6). Video, PDF and signage profiles are added in Step 1.5b.</summary>
    public static readonly IReadOnlyList<RenditionProfile> Images =
    [
        new("thumb", 240, "webp", 75),
        new("preview", 1024, "webp", 80),
        new("web", 1920, "webp", 80),
        new("web_jpeg", 1920, "jpg", 82),   // the fallback for clients without WebP
        new("mobile", 1080, "webp", 80),
    ];

    public static RenditionProfile? Find(string name) => Images.FirstOrDefault(p => p.Name == name);
    public static bool IsKnown(string name) => name == Original || Find(name) is not null;

    public static string Extension(string format) => format switch { "jpg" or "jpeg" => "jpg", var f => f };
    public static string MimeOf(string format) => format switch
    {
        "webp" => "image/webp", "avif" => "image/avif", "png" => "image/png", _ => "image/jpeg",
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
