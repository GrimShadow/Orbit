using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Dam.Domain.Assets;

namespace Dam.Infrastructure.Delivery;

/// <summary>Paths and tunables for the external tools video and PDF renditions depend on (env: DAM_FFMPEG_*, DAM_PDF_*).</summary>
public sealed class MediaToolSettings
{
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
    public string PdftoppmPath { get; set; } = "pdftoppm";
    public string PdfinfoPath { get; set; } = "pdfinfo";
    /// <summary>x264 speed/quality trade-off. "veryfast" in production; tests use "ultrafast" for speed.</summary>
    public string Preset { get; set; } = "veryfast";
    /// <summary>A PDF with more pages than this only has its first pages rendered as "pdf_pages".</summary>
    public int MaxPdfPages { get; set; } = 200;
    public TimeSpan ToolTimeout { get; set; } = TimeSpan.FromMinutes(5);
}

public sealed class MediaToolMissingException(string tool) : Exception($"'{tool}' is not installed or not on PATH.");
public sealed class MediaToolFailedException(string tool, string detail) : Exception($"{tool} could not process the file: {detail}");

/// <summary>Runs a short-lived external process against a local file and captures its output, the way every tool here needs.</summary>
internal static class ExternalProcess
{
    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct, bool binaryStdout = false)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);

        Process process;
        try { process = Process.Start(psi) ?? throw new InvalidOperationException($"{exe} did not start"); }
        catch (Win32Exception) { throw new MediaToolMissingException(exe); }

        using (process)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);
            var stdout = binaryStdout ? Task.FromResult("") : process.StandardOutput.ReadToEndAsync(linked.Token);
            var stderr = process.StandardError.ReadToEndAsync(linked.Token);
            try { await process.WaitForExitAsync(linked.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            return (process.ExitCode, await stdout, Trim(await stderr));
        }
    }

    public static async Task<byte[]> RunForBinaryStdoutAsync(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);

        Process process;
        try { process = Process.Start(psi) ?? throw new InvalidOperationException($"{exe} did not start"); }
        catch (Win32Exception) { throw new MediaToolMissingException(exe); }

        using (process)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);
            var stdout = new MemoryStream();
            var copy = process.StandardOutput.BaseStream.CopyToAsync(stdout, linked.Token);
            var stderr = process.StandardError.ReadToEndAsync(linked.Token);
            try { await Task.WhenAll(copy, process.WaitForExitAsync(linked.Token)); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            if (process.ExitCode != 0) throw new MediaToolFailedException(exe, Trim(await stderr));
            return stdout.ToArray();
        }
    }

    private static string Trim(string s) => s.Length > 400 ? s[..400] : s.Trim();
}

/// <summary>A source file staged to local disk so ffmpeg and Poppler (which need to seek) can work on it, cleaned up on dispose.</summary>
internal sealed class TempSource : IDisposable
{
    /// <summary>A scratch folder of its own, marked so desktop search (Spotlight, and equivalents elsewhere) never indexes the
    /// stream of short-lived video and PDF files ffmpeg/Poppler read and write here.</summary>
    public static readonly string Dir = MediaScratch.EnsureDirectory();

    public string Path { get; }
    public TempSource(byte[] bytes, string extension)
    {
        Path = System.IO.Path.Combine(Dir, $"dam-{Guid.NewGuid():N}.{extension}");
        File.WriteAllBytes(Path, bytes);
    }
    public void Dispose() { try { File.Delete(Path); } catch (IOException) { } }
}

internal static class MediaScratch
{
    public static string EnsureDirectory()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dam-media-scratch");
        Directory.CreateDirectory(dir);
        var marker = System.IO.Path.Combine(dir, ".metadata_never_index");
        if (!File.Exists(marker)) { try { File.WriteAllBytes(marker, []); } catch (IOException) { } }
        return dir;
    }
}

public sealed record RenderedVideo(byte[] Bytes, int Width, int Height, double? DurationSeconds);

/// <summary>FFmpeg-backed video poster extraction and transcoding (spec D8, A6).</summary>
public sealed class VideoRenderer(MediaToolSettings settings)
{
    /// <summary>A representative frame as a PNG, near the start of the video (or its first frame, if shorter).</summary>
    public async Task<byte[]> ExtractFrameAsync(byte[] source, CancellationToken ct)
    {
        using var src = new TempSource(source, "bin");
        return await ExternalProcess.RunForBinaryStdoutAsync(settings.FfmpegPath,
            ["-y", "-ss", "0.5", "-i", src.Path, "-frames:v", "1", "-f", "image2pipe", "-vcodec", "png", "pipe:1"],
            settings.ToolTimeout, ct);
    }

    /// <param name="sourceWidth">The source's pixel width, already known (from its decoded poster frame) so this never needs to probe
    /// the input itself. Only used to compute the output width when <paramref name="profile"/> keeps the source's aspect ratio.</param>
    public async Task<RenderedVideo> TranscodeAsync(byte[] source, VideoProfile profile, int sourceWidth, int sourceHeight, CancellationToken ct)
    {
        using var src = new TempSource(source, "bin");
        using var outFile = new TempSource([], "mp4");
        var (outW, outH) = profile.Letterbox
            ? (profile.Width, profile.Height)
            : (EvenScaledWidth(sourceWidth, sourceHeight, profile.Height), profile.Height);
        var filter = profile.Letterbox
            ? $"scale={profile.Width}:{profile.Height}:force_original_aspect_ratio=decrease,pad={profile.Width}:{profile.Height}:(ow-iw)/2:(oh-ih)/2:color=black"
            : $"scale=-2:{profile.Height}:force_original_aspect_ratio=decrease";
        var args = new List<string> { "-y", "-i", src.Path, "-vf", filter, "-c:v", "libx264", "-preset", settings.Preset, "-pix_fmt", "yuv420p",
            "-b:v", profile.BitrateBps.ToString(CultureInfo.InvariantCulture), "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart" };
        if (profile.MaxDurationSeconds is { } d) { args.Add("-t"); args.Add(d.ToString(CultureInfo.InvariantCulture)); }
        args.Add(outFile.Path);

        var (code, _, err) = await ExternalProcess.RunAsync(settings.FfmpegPath, args, settings.ToolTimeout, ct);
        if (code != 0) throw new MediaToolFailedException("ffmpeg", err);
        var bytes = await File.ReadAllBytesAsync(outFile.Path, ct);
        return new RenderedVideo(bytes, outW, outH, await ProbeDurationAsync(outFile.Path, ct));
    }

    private static int EvenScaledWidth(int width, int height, int targetHeight) =>
        height <= 0 ? targetHeight : Math.Max(2, (int)Math.Round(width * (targetHeight / (double)height) / 2.0) * 2);

    /// <summary>Best-effort only: a missing ffprobe just means the rendition row has no duration recorded.</summary>
    private async Task<double?> ProbeDurationAsync(string path, CancellationToken ct)
    {
        try
        {
            var (code, stdout, _) = await ExternalProcess.RunAsync(settings.FfprobePath, ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path], settings.ToolTimeout, ct);
            return code == 0 && double.TryParse(stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        }
        catch (MediaToolMissingException) { return null; }
    }
}

/// <summary>Poppler-backed PDF page rasterisation (spec D8, A6 "pdf_pages").</summary>
public sealed class PdfRenderer(MediaToolSettings settings)
{
    public async Task<int> PageCountAsync(byte[] pdf, CancellationToken ct)
    {
        using var src = new TempSource(pdf, "pdf");
        var (code, stdout, err) = await ExternalProcess.RunAsync(settings.PdfinfoPath, [src.Path], settings.ToolTimeout, ct);
        if (code != 0) throw new MediaToolFailedException("pdfinfo", err);
        var line = stdout.Split('\n').FirstOrDefault(l => l.StartsWith("Pages:", StringComparison.Ordinal));
        return line is not null && int.TryParse(line.AsSpan(6).Trim(), out var n) ? n : 0;
    }

    /// <summary>One page as a PNG, at print resolution (150 DPI is plenty for a 1600px-wide rendition of an A4/Letter page).</summary>
    public async Task<byte[]> RasterizePageAsync(byte[] pdf, int pageNo, CancellationToken ct)
    {
        using var src = new TempSource(pdf, "pdf");
        var prefix = Path.Combine(TempSource.Dir, $"dam-{Guid.NewGuid():N}");
        try
        {
            var (code, _, err) = await ExternalProcess.RunAsync(settings.PdftoppmPath,
                ["-png", "-r", "150", "-f", pageNo.ToString(CultureInfo.InvariantCulture), "-l", pageNo.ToString(CultureInfo.InvariantCulture), src.Path, prefix],
                settings.ToolTimeout, ct);
            var produced = Directory.GetFiles(TempSource.Dir, Path.GetFileName(prefix) + "*.png");
            if (code != 0 || produced.Length == 0) throw new MediaToolFailedException("pdftoppm", err.Length > 0 ? err : $"page {pageNo} was not produced");
            return await File.ReadAllBytesAsync(produced[0], ct);
        }
        finally { foreach (var f in Directory.GetFiles(TempSource.Dir, Path.GetFileName(prefix) + "*.png")) { try { File.Delete(f); } catch (IOException) { } } }
    }
}
