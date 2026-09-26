using System.Text;

namespace Dam.Domain.Assets;

public enum ContentKind { Unknown, Image, Video, Audio, Pdf, Office, Archive, Text, Html, Svg, Model3d, Executable }

/// <param name="Format">Short name of what the bytes are, e.g. "jpeg", "mp4", "zip".</param>
/// <param name="Extensions">File extensions that legitimately hold this format (empty when unknown).</param>
public sealed record SniffResult(ContentKind Kind, string Mime, string Format, IReadOnlyList<string> Extensions);

public sealed record SniffVerdict(bool Allowed, string? Reason, IReadOnlyList<string> Warnings);

/// <summary>
/// Decides what a file really is from its first bytes ("magic numbers"), never from its name or the type the browser claimed
/// (spec A7.7). Pure and dependency-free, so it is exhaustively unit-tested. Covers the formats the allow-list accepts, plus
/// the executable formats that must never get in.
/// </summary>
public static class ContentSniffer
{
    /// <summary>How many leading bytes the sniffer needs.</summary>
    public const int HeaderBytes = 64 * 1024;

    private static readonly SniffResult Unknown = new(ContentKind.Unknown, "application/octet-stream", "unknown", []);

    public static SniffResult Sniff(ReadOnlySpan<byte> h)
    {
        if (h.Length == 0) return Unknown;

        // ---- executables first: these are refused whatever the file is called
        if (Starts(h, 0x4D, 0x5A)) return Exe("application/vnd.microsoft.portable-executable", "pe");
        if (Starts(h, 0x7F, 0x45, 0x4C, 0x46)) return Exe("application/x-elf", "elf");
        if (Starts(h, 0xFE, 0xED, 0xFA, 0xCE) || Starts(h, 0xFE, 0xED, 0xFA, 0xCF) || Starts(h, 0xCE, 0xFA, 0xED, 0xFE)
            || Starts(h, 0xCF, 0xFA, 0xED, 0xFE) || Starts(h, 0xCA, 0xFE, 0xBA, 0xBE)) return Exe("application/x-mach-binary", "macho");
        if (Starts(h, (byte)'#', (byte)'!')) return Exe("text/x-shellscript", "script");

        // ---- images
        if (Starts(h, 0xFF, 0xD8, 0xFF)) return new(ContentKind.Image, "image/jpeg", "jpeg", ["jpg", "jpeg"]);
        if (Starts(h, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)) return new(ContentKind.Image, "image/png", "png", ["png"]);
        if (Ascii(h, 0, "GIF87a") || Ascii(h, 0, "GIF89a")) return new(ContentKind.Image, "image/gif", "gif", ["gif"]);
        if (Ascii(h, 0, "RIFF") && Ascii(h, 8, "WEBP")) return new(ContentKind.Image, "image/webp", "webp", ["webp"]);
        if (Starts(h, 0x49, 0x49, 0x2A, 0x00) || Starts(h, 0x4D, 0x4D, 0x00, 0x2A)) return new(ContentKind.Image, "image/tiff", "tiff", ["tif", "tiff"]);
        if (Ascii(h, 0, "8BPS")) return new(ContentKind.Image, "image/vnd.adobe.photoshop", "psd", ["psd"]);
        if (Ascii(h, 0, "BM") && h.Length >= 18 && IsBmpHeader(BitConverter.ToUInt32(h.Slice(14, 4)))) return new(ContentKind.Image, "image/bmp", "bmp", ["bmp"]);

        // ---- ISO base media (mp4, mov, m4a, heic, avif): "ftyp" at byte 4
        if (Ascii(h, 4, "ftyp") && h.Length >= 12)
        {
            var brand = Encoding.ASCII.GetString(h.Slice(8, 4));
            return brand switch
            {
                "heic" or "heix" or "hevc" or "hevx" or "mif1" or "msf1" => new(ContentKind.Image, "image/heic", "heic", ["heic", "heif"]),
                "avif" or "avis" => new(ContentKind.Image, "image/avif", "avif", ["avif"]),
                "qt  " => new(ContentKind.Video, "video/quicktime", "mov", ["mov"]),
                "M4A " or "M4B " => new(ContentKind.Audio, "audio/mp4", "m4a", ["m4a"]),
                _ => new(ContentKind.Video, "video/mp4", "mp4", ["mp4", "m4v", "mov"]),
            };
        }
        if (Ascii(h, 4, "moov") || Ascii(h, 4, "mdat") || Ascii(h, 4, "wide") || Ascii(h, 4, "free") || Ascii(h, 4, "skip"))
            return new(ContentKind.Video, "video/quicktime", "mov", ["mov", "mp4"]);
        if (Starts(h, 0x1A, 0x45, 0xDF, 0xA3)) return new(ContentKind.Video, "video/x-matroska", "matroska", ["mkv", "webm"]);
        if (Ascii(h, 0, "RIFF") && Ascii(h, 8, "AVI ")) return new(ContentKind.Video, "video/x-msvideo", "avi", ["avi"]);
        if (Starts(h, 0x06, 0x0E, 0x2B, 0x34)) return new(ContentKind.Video, "application/mxf", "mxf", ["mxf"]);

        // ---- audio
        if (Ascii(h, 0, "RIFF") && Ascii(h, 8, "WAVE")) return new(ContentKind.Audio, "audio/wav", "wav", ["wav"]);
        if (Ascii(h, 0, "ID3")) return new(ContentKind.Audio, "audio/mpeg", "mp3", ["mp3"]);
        if (Ascii(h, 0, "fLaC")) return new(ContentKind.Audio, "audio/flac", "flac", ["flac"]);
        if (Ascii(h, 0, "OggS")) return new(ContentKind.Audio, "audio/ogg", "ogg", ["ogg"]);
        if (h.Length >= 2 && h[0] == 0xFF && (h[1] & 0xE0) == 0xE0)
            return (h[1] & 0x06) == 0 ? new(ContentKind.Audio, "audio/aac", "aac", ["aac"]) : new(ContentKind.Audio, "audio/mpeg", "mp3", ["mp3"]);

        // ---- 3D
        if (Ascii(h, 0, "glTF")) return new(ContentKind.Model3d, "model/gltf-binary", "glb", ["glb"]);
        if (Ascii(h, 0, "Kaydara FBX Binary")) return new(ContentKind.Model3d, "application/octet-stream", "fbx", ["fbx"]);

        // ---- documents and containers
        var pdfWindow = h.Length > 1024 ? h[..1024] : h;
        if (pdfWindow.IndexOf("%PDF-"u8) >= 0) return new(ContentKind.Pdf, "application/pdf", "pdf", ["pdf"]);
        if (Starts(h, 0x50, 0x4B, 0x03, 0x04) || Starts(h, 0x50, 0x4B, 0x05, 0x06))
            return new(ContentKind.Archive, "application/zip", "zip", ["zip", "docx", "xlsx", "pptx", "odt", "ods", "odp", "epub", "usdz"]);
        if (Starts(h, 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1)) return new(ContentKind.Office, "application/x-ole-storage", "ole", ["doc", "xls", "ppt"]);

        // ---- text based: only if it really is text (no NUL bytes, valid UTF-8)
        return SniffText(h);
    }

    private static SniffResult SniffText(ReadOnlySpan<byte> h)
    {
        var body = h;
        if (body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF) body = body[3..];
        if (body.IndexOf((byte)0) >= 0) return Unknown;
        string text;
        try { text = new UTF8Encoding(false, true).GetString(body.Length > 8192 ? body[..8192].TrimEnd((byte)0) : body); }
        catch (DecoderFallbackException) { return Unknown; }        // binary, or cut mid-character
        catch (ArgumentException) { return Unknown; }

        // Real text has no control characters other than tab, newline, carriage return and form feed.
        if (text.Any(c => char.IsControl(c) && c is not ('\t' or '\n' or '\r' or '\f'))) return Unknown;

        var t = text.TrimStart();
        if (t.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) || (t.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && text.Contains("<svg", StringComparison.OrdinalIgnoreCase)))
            return new(ContentKind.Svg, "image/svg+xml", "svg", ["svg"]);
        if (t.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) || t.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
            return new(ContentKind.Html, "text/html", "html", ["html", "htm"]);
        if (t.StartsWith("{\\rtf", StringComparison.Ordinal)) return new(ContentKind.Text, "application/rtf", "rtf", ["rtf"]);
        return new(ContentKind.Text, "text/plain", "text", ["txt", "csv", "obj", "gltf", "json", "md"]);
    }

    /// <summary>Is this content acceptable for an asset of <paramref name="assetType"/>? The name only adds a warning.</summary>
    public static SniffVerdict Judge(string assetType, string fileName, SniffResult sniff)
    {
        var ext = UploadPolicy.Extension(fileName);
        var warnings = new List<string>();
        if (sniff.Kind == ContentKind.Executable) return new(false, "The file contains executable code, which is never accepted.", warnings);

        var zipBased = new HashSet<string> { "docx", "xlsx", "pptx", "odt", "ods", "odp" };
        bool ok = assetType switch
        {
            "image" => sniff.Kind is ContentKind.Image or ContentKind.Svg,
            "video" => sniff.Kind == ContentKind.Video,
            "audio" => sniff.Kind == ContentKind.Audio,
            "document" => sniff.Kind is ContentKind.Pdf or ContentKind.Office or ContentKind.Text || (sniff.Kind == ContentKind.Archive && zipBased.Contains(ext)),
            "manual" => sniff.Kind is ContentKind.Pdf or ContentKind.Html || (sniff.Kind == ContentKind.Archive && ext is "epub" or "zip"),
            "3d" => sniff.Kind == ContentKind.Model3d || (sniff.Kind == ContentKind.Archive && ext is "zip" or "usdz") || (sniff.Kind == ContentKind.Text && ext is "obj" or "gltf"),
            "html5" => sniff.Kind == ContentKind.Archive && ext == "zip",
            "other" => true,
            _ => false,
        };
        if (!ok)
            return new(false, $"The content looks like {Describe(sniff)}, which is not accepted for a '{assetType}' asset.", warnings);

        if (sniff.Extensions.Count > 0 && ext.Length > 0 && !sniff.Extensions.Contains(ext))
            warnings.Add($"The content is {Describe(sniff)} but the file name ends in .{ext}.");
        return new(true, null, warnings);
    }

    private static string Describe(SniffResult s) => s.Kind == ContentKind.Unknown ? "unrecognised data" : $"{s.Format} ({s.Mime})";

    private static SniffResult Exe(string mime, string format) => new(ContentKind.Executable, mime, format, []);
    private static bool Starts(ReadOnlySpan<byte> h, params byte[] magic) => h.Length >= magic.Length && h[..magic.Length].SequenceEqual(magic);
    private static bool Starts(ReadOnlySpan<byte> h, params int[] magic)
    {
        if (h.Length < magic.Length) return false;
        for (var i = 0; i < magic.Length; i++) if (h[i] != magic[i]) return false;
        return true;
    }
    private static bool Ascii(ReadOnlySpan<byte> h, int offset, string s) =>
        h.Length >= offset + s.Length && h.Slice(offset, s.Length).SequenceEqual(Encoding.ASCII.GetBytes(s));
    private static bool IsBmpHeader(uint size) => size is 12 or 40 or 52 or 56 or 64 or 108 or 124;
}
