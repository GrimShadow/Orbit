using System.Diagnostics;
using System.Text;
using Dam.Application.Abstractions;
using Dam.Domain.Ai;
using Dam.Domain.Assets;
using Dam.Infrastructure.Delivery;
using UglyToad.PdfPig;

namespace Dam.Infrastructure.Ai;

/// <summary>Mono 16 kHz MP3 pulled from a video or recording, held in a scratch file until disposed.</summary>
public sealed class ExtractedAudio : IDisposable
{
    internal ExtractedAudio(string path, long size) { Path = path; Size = size; }

    public string Path { get; }
    public long Size { get; }
    public const string FileName = "audio.mp3";

    /// <summary>A fresh, seekable read stream (so a retry can send it again). Streaming from disk keeps a long recording out of memory.</summary>
    public Stream Open() => new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

    public void Dispose() { try { File.Delete(Path); } catch (IOException) { } }
}

/// <summary>
/// Extracts the audio track with ffmpeg (already in the worker image), reading the original straight from object storage through a short-lived signed
/// address so a long video is not copied to disk first. The length is capped (DAM_AI_MAX_AUDIO_MINUTES) and the encoding chosen so the result fits the
/// 25 MB limit of the providers; see <see cref="AudioPlan"/>.
/// </summary>
public sealed class AudioExtractor(MediaToolSettings tools, IObjectStore store)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(20);

    /// <returns>The audio, or null when the file has no audio track.</returns>
    public async Task<ExtractedAudio?> ExtractAsync(AssetVersion version, AudioPlan plan, CancellationToken ct)
    {
        var output = Path.Combine(TempSource.Dir, $"dam-audio-{Guid.NewGuid():N}.mp3");
        var source = store.PresignGet(version.Bucket, version.StorageKey, TimeSpan.FromHours(2));
        string[] args =
        [
            "-nostdin", "-y", "-v", "error", "-i", source, "-vn", "-map", "0:a:0?", "-ac", "1", "-ar", "16000",
            "-c:a", "libmp3lame", "-b:a", $"{plan.BitrateKbps}k", "-t", plan.Seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), output,
        ];
        try
        {
            var (code, _, err) = await ExternalProcess.RunAsync(tools.FfmpegPath, args, Timeout, ct);
            // "-map 0:a:0?" is optional, so a file with no audio track maps nothing and ffmpeg then says it has nothing to write: that is "no audio", not a failure.
            if (code != 0 && err.Contains("does not contain any stream", StringComparison.Ordinal)) { File.Delete(output); return null; }
            if (code != 0) throw new MediaToolFailedException("ffmpeg", Scrub(err, source));
            var size = File.Exists(output) ? new FileInfo(output).Length : 0;
            if (size < 2048)   // a silent or absent track makes an empty MP3
            {
                File.Delete(output);
                return null;
            }
            return new ExtractedAudio(output, size);
        }
        catch
        {
            try { File.Delete(output); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>The signed address is a credential: it never reaches an error message or a log.</summary>
    private static string Scrub(string text, string source) => text.Replace(source, "<source>", StringComparison.Ordinal);
}

/// <summary>Reads the text layer of a PDF (when it has one) so an ordinary document is not OCR'd needlessly.</summary>
public static class PdfTextLayer
{
    public const int MaxPages = 50;
    public const int MaxChars = 200_000;
    public const int MinUsefulChars = 40;

    /// <summary>The page texts joined, or null when the file has no real text layer (a scan) or cannot be read.</summary>
    public static string? TryRead(byte[] pdf)
    {
        try
        {
            using var doc = PdfDocument.Open(pdf);
            var sb = new StringBuilder();
            foreach (var page in doc.GetPages().Take(MaxPages))
            {
                var t = page.Text;
                if (!string.IsNullOrWhiteSpace(t)) sb.Append(t.Trim()).Append("\n\n");
                if (sb.Length >= MaxChars) break;
            }
            var text = sb.ToString().Trim();
            if (text.Length > MaxChars) text = text[..MaxChars];
            return text.Count(char.IsLetterOrDigit) >= MinUsefulChars ? text : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine($"PDF text layer unreadable: {ex.GetType().Name}");
            return null;
        }
    }
}
