namespace Dam.Domain.Assets;

/// <summary>
/// What may be uploaded, decided from the file name before any bytes move. This is the first gate only: Step 1.4 sniffs
/// the real content (magic bytes) and scans it, because a name or a declared type can lie.
/// </summary>
public static class UploadPolicy
{
    public const long Gib = 1024L * 1024 * 1024;
    /// <summary>Absolute ceiling for a single file (spec A8: 20 GB).</summary>
    public const long MaxFileBytes = 20 * Gib;

    private static readonly Dictionary<string, string[]> Extensions = new()
    {
        ["image"] = ["jpg", "jpeg", "png", "gif", "webp", "avif", "tif", "tiff", "bmp", "svg", "heic", "heif", "psd"],
        ["video"] = ["mp4", "mov", "m4v", "webm", "mkv", "avi", "mxf"],
        ["audio"] = ["mp3", "wav", "aac", "m4a", "flac", "ogg"],
        ["document"] = ["pdf", "doc", "docx", "ppt", "pptx", "xls", "xlsx", "txt", "rtf", "csv", "odt", "ods", "odp"],
        ["manual"] = ["pdf", "html", "htm", "epub", "zip"],
        ["3d"] = ["glb", "gltf", "usdz", "obj", "fbx", "zip"],
        ["html5"] = ["zip"],
    };

    /// <summary>Never accepted as an asset of any type, including "other".</summary>
    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "dll", "msi", "bat", "cmd", "com", "scr", "ps1", "vbs", "jar", "app", "dmg", "apk", "so", "sh", "pif",
    };

    private static readonly Dictionary<string, long> MaxBytes = new()
    {
        ["image"] = 2 * Gib, ["video"] = 20 * Gib, ["audio"] = 2 * Gib, ["document"] = 2 * Gib,
        ["manual"] = 2 * Gib, ["3d"] = 5 * Gib, ["html5"] = 1 * Gib, ["other"] = 5 * Gib,
    };

    public static string Extension(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot < 0 || dot == fileName.Length - 1 ? "" : fileName[(dot + 1)..].ToLowerInvariant();
    }

    /// <summary>Strips any path, control characters and over-long names so the stored name is safe to show and log.</summary>
    public static string CleanFileName(string fileName)
    {
        var name = fileName.Replace('\\', '/').Split('/').Last();
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return name.Length > 255 ? name[^255..] : name;
    }

    public static bool IsBlocked(string fileName) => Blocked.Contains(Extension(fileName));

    /// <summary>The asset types a file can be created as: the typed ones above, plus the catch-all "other".</summary>
    public static bool IsKnownType(string assetType) => assetType == "other" || Extensions.ContainsKey(assetType);

    public static bool IsAllowed(string assetType, string fileName)
    {
        var ext = Extension(fileName);
        if (ext.Length == 0 || IsBlocked(fileName)) return false;
        return assetType == "other" || (Extensions.TryGetValue(assetType, out var allowed) && allowed.Contains(ext));
    }

    public static long MaxSize(string assetType) => MaxBytes.GetValueOrDefault(assetType, MaxBytes["other"]);

    /// <summary>Best guess of the asset type for a file (used by bulk upload). Unknown but harmless files become "other".</summary>
    public static string? InferType(string fileName)
    {
        var ext = Extension(fileName);
        if (ext.Length == 0 || IsBlocked(fileName)) return null;
        if (ext == "zip") return "other"; // ambiguous: an html5 bundle, a 3D package or a manual. The uploader must choose.
        // A PDF is a document by default; the uploader can pick "manual" explicitly.
        foreach (var type in new[] { "image", "video", "audio", "document", "3d" })
            if (Extensions[type].Contains(ext)) return type;
        return "other";
    }
}
