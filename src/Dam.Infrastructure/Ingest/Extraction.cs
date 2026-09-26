using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using Dam.Application.Abstractions;
using Dam.Domain.Assets;
using MetadataExtractor;
using UglyToad.PdfPig;

namespace Dam.Infrastructure.Ingest;

/// <summary>
/// ffprobe reads duration, codecs and resolution over HTTP range requests against a presigned URL, so a 20 GB video is
/// probed without being downloaded. If ffprobe is not installed, probing is reported as unavailable (not an error).
/// </summary>
public sealed class FfprobeMediaProbe(string ffprobePath, IObjectStore store) : IMediaProbe
{
    private static readonly JsonSerializerOptions OmitNulls = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private bool _missing;

    public async Task<JsonElement?> ProbeAsync(string bucket, string key, CancellationToken ct)
    {
        if (_missing) return null;
        var url = store.PresignGet(bucket, key, TimeSpan.FromMinutes(30));
        var psi = new ProcessStartInfo(ffprobePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Arguments are passed as a list (no shell), so the URL cannot be interpreted as anything but data.
        foreach (var a in new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-i", url }) psi.ArgumentList.Add(a);

        Process process;
        try { process = Process.Start(psi) ?? throw new InvalidOperationException("ffprobe did not start"); }
        catch (Win32Exception) { _missing = true; return null; }

        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            var output = await stdout;
            if (process.ExitCode != 0) throw new InvalidOperationException($"ffprobe could not read the file: {Trim(await stderr)}");
            return Summarise(JsonDocument.Parse(output).RootElement);
        }
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s.Trim();

    /// <summary>A compact, stable shape instead of ffprobe's full dump.</summary>
    public static JsonElement Summarise(JsonElement probe)
    {
        var streams = new List<object>();
        if (probe.TryGetProperty("streams", out var ss))
            foreach (var s in ss.EnumerateArray())
            {
                var type = Str(s, "codec_type");
                if (type is not ("video" or "audio")) continue;
                streams.Add(new
                {
                    type,
                    codec = Str(s, "codec_name"),
                    width = Int(s, "width"),
                    height = Int(s, "height"),
                    frameRate = Rate(Str(s, "avg_frame_rate")),
                    channels = Int(s, "channels"),
                    sampleRate = Int(s, "sample_rate"),
                    bitRate = Long(s, "bit_rate"),
                    pixelFormat = Str(s, "pix_fmt"),
                });
            }
        var format = probe.TryGetProperty("format", out var f) ? f : default;
        return JsonSerializer.SerializeToElement(new
        {
            formatName = format.ValueKind == JsonValueKind.Object ? Str(format, "format_name") : null,
            durationSeconds = format.ValueKind == JsonValueKind.Object ? Dbl(format, "duration") : null,
            bitRate = format.ValueKind == JsonValueKind.Object ? Long(format, "bit_rate") : null,
            streams,
        }, OmitNulls);
    }

    private static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? Int(JsonElement e, string n) => e.TryGetProperty(n, out var v) ? v.ValueKind == JsonValueKind.Number ? v.GetInt32() : int.TryParse(v.GetString(), out var i) ? i : null : null;
    private static long? Long(JsonElement e, string n) => e.TryGetProperty(n, out var v) && long.TryParse(v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText(), CultureInfo.InvariantCulture, out var l) ? l : null;
    private static double? Dbl(JsonElement e, string n) => e.TryGetProperty(n, out var v) && double.TryParse(v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText(), CultureInfo.InvariantCulture, out var d) ? Math.Round(d, 3) : null;
    private static double? Rate(string? r)
    {
        if (r is null) return null;
        var parts = r.Split('/');
        return parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var n) && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var d) && d != 0 ? Math.Round(n / d, 3) : null;
    }
}

/// <summary>
/// Collects descriptive technical metadata by content kind. This stage is best-effort: a file that cannot be parsed
/// (corrupt, encrypted, too large to read into memory) yields a warning, and the file still ingests.
/// </summary>
public sealed class TechnicalMetadataExtractor(IObjectStore store, IMediaProbe probe)
{
    private const long ImageReadBytes = 64L * 1024 * 1024;
    private const long DocumentReadBytes = 256L * 1024 * 1024;

    public async Task<Dictionary<string, JsonElement>> ExtractAsync(ContentKind kind, string format, string bucket, string key, long size, string fileName, CancellationToken ct)
    {
        var result = new Dictionary<string, JsonElement>();
        var notes = new List<string>();
        var ext = UploadPolicy.Extension(fileName);
        try
        {
            switch (kind)
            {
                case ContentKind.Image when format != "svg":
                    result["image"] = await ImageAsync(bucket, key, size, notes, ct);
                    break;
                case ContentKind.Pdf:
                    result["pdf"] = await PdfAsync(bucket, key, size, notes, ct);
                    break;
                case ContentKind.Archive when ext is "docx" or "xlsx" or "pptx":
                    result["document"] = await OfficeAsync(bucket, key, size, notes, ct);
                    break;
                case ContentKind.Video or ContentKind.Audio:
                    var media = await probe.ProbeAsync(bucket, key, ct);
                    if (media is { } m) result["media"] = m; else notes.Add("ffprobe is not installed, so audio/video details were not read.");
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            notes.Add($"Some details could not be read: {ex.Message}");
        }
        result["extraction"] = JsonSerializer.SerializeToElement(new { status = notes.Count == 0 ? "ok" : "partial", notes });
        return result;
    }

    private async Task<JsonElement> ImageAsync(string bucket, string key, long size, List<string> notes, CancellationToken ct)
    {
        var bytes = await store.ReadRangeAsync(bucket, key, 0, Math.Min(size, ImageReadBytes), ct);
        var info = new Dictionary<string, object?>();

        try
        {
            using var img = NetVips.Image.NewFromBuffer(bytes);
            info["width"] = img.Width; info["height"] = img.Height; info["bands"] = img.Bands; info["hasAlpha"] = img.HasAlpha();
        }
        catch (Exception ex) { notes.Add($"Image size could not be read: {ex.Message}"); }

        try
        {
            var exif = new Dictionary<string, string>();
            var iptc = new Dictionary<string, string>();
            foreach (var dir in ImageMetadataReader.ReadMetadata(new MemoryStream(bytes)))
            {
                if (dir.Name == "GPS") info["hasGps"] = true; // whether a location is embedded is recorded; the coordinates deliberately are not
                if (dir.Name == "XMP") info["hasXmp"] = true;
                if (dir.Name == "ICC Profile") info["hasIccProfile"] = true;
                foreach (var tag in dir.Tags)
                {
                    var d = tag.Description;
                    if (d is null || d.Length > 300) continue;
                    if (dir.Name is "Exif IFD0" or "Exif SubIFD" && Wanted.Contains(tag.Name)) exif[tag.Name] = d;
                    else if (dir.Name == "IPTC" && WantedIptc.Contains(tag.Name)) iptc[tag.Name] = d;
                }
            }
            if (exif.Count > 0) info["exif"] = exif;
            if (iptc.Count > 0) info["iptc"] = iptc;
        }
        catch (ImageProcessingException) { /* no embedded metadata is normal (e.g. a PNG made by a screenshot tool) */ }
        catch (Exception ex) { notes.Add($"Embedded metadata could not be read: {ex.Message}"); }

        if (size > ImageReadBytes) notes.Add("The image is large; only its first 64 MB were read for metadata.");
        return JsonSerializer.SerializeToElement(info);
    }

    private static readonly HashSet<string> Wanted = ["Make", "Model", "Software", "Orientation", "Date/Time", "Date/Time Original", "Exposure Time", "F-Number", "ISO Speed Ratings", "Focal Length", "Color Space"];
    private static readonly HashSet<string> WantedIptc = ["Keywords", "Caption/Abstract", "Copyright Notice", "By-line", "Object Name"];

    private async Task<JsonElement> PdfAsync(string bucket, string key, long size, List<string> notes, CancellationToken ct)
    {
        if (size > DocumentReadBytes) { notes.Add("The PDF is too large to read for metadata."); return JsonSerializer.SerializeToElement(new { }); }
        await using var s = await store.OpenReadAsync(bucket, key, ct);
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        try
        {
            using var doc = PdfDocument.Open(ms.ToArray());
            var page = doc.NumberOfPages > 0 ? doc.GetPage(1) : null;
            var i = doc.Information;
            return JsonSerializer.SerializeToElement(new
            {
                pages = doc.NumberOfPages,
                title = Clean(i.Title),
                author = Clean(i.Author),
                producer = Clean(i.Producer),
                creator = Clean(i.Creator),
                created = Clean(i.CreationDate),
                modified = Clean(i.ModifiedDate),
                pageWidthPt = page?.Width,
                pageHeightPt = page?.Height,
            });
        }
        catch (Exception ex)
        {
            notes.Add($"The PDF could not be read (it may be encrypted or damaged): {ex.Message}");
            return JsonSerializer.SerializeToElement(new { });
        }
    }

    private async Task<JsonElement> OfficeAsync(string bucket, string key, long size, List<string> notes, CancellationToken ct)
    {
        if (size > DocumentReadBytes / 2) { notes.Add("The document is too large to read for metadata."); return JsonSerializer.SerializeToElement(new { }); }
        await using var s = await store.OpenReadAsync(bucket, key, ct);
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var info = new Dictionary<string, string?>();

        if (zip.GetEntry("docProps/core.xml") is { } core)
        {
            var x = XDocument.Load(core.Open());
            foreach (var (name, label) in new[] { ("title", "title"), ("creator", "author"), ("lastModifiedBy", "lastModifiedBy"), ("created", "created"), ("modified", "modified") })
                if (x.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value is { Length: > 0 } v) info[label] = Clean(v);
        }
        if (zip.GetEntry("docProps/app.xml") is { } app)
        {
            var x = XDocument.Load(app.Open());
            foreach (var name in new[] { "Application", "Pages", "Slides", "Words" })
                if (x.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value is { Length: > 0 } v) info[char.ToLowerInvariant(name[0]) + name[1..]] = Clean(v);
        }
        return JsonSerializer.SerializeToElement(info);
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Length > 300 ? s[..300] : s.Trim();
}
