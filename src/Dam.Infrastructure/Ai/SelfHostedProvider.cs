using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Domain.Ai;
using Microsoft.Extensions.Logging;

namespace Dam.Infrastructure.Ai;

/// <summary>
/// The AI that runs inside this installation: an HTTP sidecar serving CLIP, a multilingual text embedder, Whisper and an OCR engine (ADR-0035 documents the
/// contract). Pictures and audio go to <c>DAM_AI_SIDECAR_URL</c> and nowhere else. A busy sidecar (503 with Retry-After) is waited for, a few times; one that
/// stays down trips the cool-down so the worker does not queue up behind it. Uploads are streamed, never buffered, and nothing of the content is logged.
/// </summary>
public sealed class SelfHostedProvider : IAiProvider, IDisposable
{
    private readonly AiOptions _o;
    private readonly AiHttp _http;
    private readonly ILogger _log;

    public SelfHostedProvider(AiOptions options, ILogger<SelfHostedProvider> log, TimeProvider? time = null, HttpMessageHandler? handler = null)
    {
        _o = options;
        _log = log;
        _http = new AiHttp(Kind, options, log, time ?? TimeProvider.System, s => AiRedaction.Redact(s, options.SidecarToken), handler);
    }

    public string Kind => AiProviderKinds.SelfHosted;

    public void Dispose() => _http.Dispose();

    public AiCapabilities Capabilities { get; } = new(
        Tags: true, Ocr: true, Transcribe: true, TextEmbeddings: true, ImageEmbeddings: true, DetectLanguage: true, Translate: false, Summarise: true);

    private string Url(string path) =>
        (_o.SidecarUrl ?? throw new AiUnavailableException("The self-hosted AI sidecar is not configured (DAM_AI_SIDECAR_URL).")) + path;

    private HttpRequestMessage Request(HttpMethod method, string path, HttpContent? content = null)
    {
        var r = new HttpRequestMessage(method, Url(path)) { Content = content };
        if (!string.IsNullOrEmpty(_o.SidecarToken)) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _o.SidecarToken);
        return r;
    }

    // ---------------------------------------------------------------------------------------------------- health

    public async Task<AiHealth> CheckHealthAsync(CancellationToken ct)
    {
        if (!_o.SelfHostedConfigured) return new AiHealth("unconfigured", "DAM_AI_SIDECAR_URL is not set.");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (response, error) = await _http.ProbeAsync(() => Request(HttpMethod.Get, "/healthz"), _o.HealthTimeout, ct);
        if (response is null) return new AiHealth("down", error);
        using (response)
        {
            if (!response.IsSuccessStatusCode) return new AiHealth("down", $"The sidecar answered {(int)response.StatusCode}.", (int)sw.ElapsedMilliseconds);
            try
            {
                using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                var root = doc.RootElement;
                var status = Str(root, "status") is "ok" or "loading" or "degraded" ? Str(root, "status")! : "degraded";
                var models = new Dictionary<string, string>();
                if (root.TryGetProperty("models", out var m) && m.ValueKind == JsonValueKind.Object)
                    foreach (var p in m.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object))
                    {
                        var loaded = p.Value.TryGetProperty("loaded", out var l) && l.ValueKind == JsonValueKind.True;
                        models[p.Name] = $"{Str(p.Value, "name") ?? "?"}{(loaded ? "" : " (not loaded)")}";
                    }
                return new AiHealth(status, Str(root, "version") is { } v ? $"sidecar {v}" : null, (int)sw.ElapsedMilliseconds, models);
            }
            catch (JsonException) { return new AiHealth("degraded", "The sidecar's health answer was not understood.", (int)sw.ElapsedMilliseconds); }
        }
    }

    // ------------------------------------------------------------------------------------------------- features

    public async Task<TagResult> TagImageAsync(Stream image, TagOptions options, CancellationToken ct)
    {
        using var doc = await PostFileAsync("/v1/tag", image, "image", _o.TagTimeout, form =>
        {
            form.Add(new StringContent(options.TopK.ToString(System.Globalization.CultureInfo.InvariantCulture)), "top_k");
            if (options.Threshold is { } threshold) form.Add(new StringContent(threshold.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)), "threshold");
            if (options.Labels is { Count: > 0 }) form.Add(new StringContent(JsonSerializer.Serialize(options.Labels)), "labels");
        }, ct);
        var root = doc.RootElement;
        var tags = new List<TagCandidate>();
        if (root.TryGetProperty("tags", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var t in arr.EnumerateArray())
                if (Str(t, "label") is { } label) tags.Add(new TagCandidate(label, Float(t, "score")));
        return new TagResult(Str(root, "model") ?? "sidecar", tags);
    }

    public async Task<OcrResult> OcrAsync(Stream image, OcrOptions options, CancellationToken ct)
    {
        using var doc = await PostFileAsync("/v1/ocr", image, "image", _o.OcrTimeout,
            form => { if (!string.IsNullOrWhiteSpace(options.Language)) form.Add(new StringContent(options.Language), "lang"); }, ct);
        var root = doc.RootElement;
        var lines = new List<OcrLine>();
        if (root.TryGetProperty("lines", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var l in arr.EnumerateArray())
                if (Str(l, "text") is { } text) lines.Add(new OcrLine(text, Float(l, "score")));
        var full = Str(root, "text") ?? string.Join('\n', lines.Select(l => l.Text));
        return new OcrResult(Str(root, "model") ?? "sidecar", LanguageCodes.Normalize(Str(root, "language")), full, lines,
            lines.Count == 0 ? null : lines.Average(l => l.Score));
    }

    public async Task<TranscriptResult> TranscribeAsync(Stream audio, TranscribeOptions options, CancellationToken ct)
    {
        using var doc = await PostFileAsync("/v1/transcribe", audio, options.FileName, _o.TranscribeTimeout, form =>
        {
            if (!string.IsNullOrWhiteSpace(options.Language)) form.Add(new StringContent(options.Language), "lang");
            if (options.MaxSeconds is { } max) form.Add(new StringContent(max.ToString(System.Globalization.CultureInfo.InvariantCulture)), "max_seconds");
        }, ct);
        var root = doc.RootElement;
        var segments = new List<TranscriptSegment>();
        if (root.TryGetProperty("segments", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var s in arr.EnumerateArray())
            {
                var text = Str(s, "text")?.Trim();
                if (string.IsNullOrEmpty(text)) continue;
                var start = Long(s, "startMs");
                segments.Add(new TranscriptSegment(start, Math.Max(start, Long(s, "endMs")), text));
            }
        var duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? (long)Math.Round(d.GetDouble() * 1000) : segments.LastOrDefault()?.EndMs ?? 0;
        return new TranscriptResult(Str(root, "model") ?? "sidecar", LanguageCodes.Normalize(Str(root, "language")), duration, segments);
    }

    public async Task<EmbeddingResult> EmbedImageAsync(Stream image, CancellationToken ct)
    {
        using var doc = await PostFileAsync("/v1/embed/image", image, "image", _o.EmbedTimeout, null, ct);
        var vector = Vector(doc.RootElement, "embedding") ?? throw new AiRequestException("The sidecar's image embedding answer had no vector.");
        return new EmbeddingResult(Str(doc.RootElement, "model") ?? "sidecar", vector);
    }

    public async Task<EmbeddingResult> EmbedTextAsync(string text, TextEmbedOptions options, CancellationToken ct)
    {
        var batch = await EmbedTextsAsync([text], options, ct);
        return new EmbeddingResult(batch.Model, batch.Vectors[0]);
    }

    public async Task<EmbeddingBatchResult> EmbedTextsAsync(IReadOnlyList<string> texts, TextEmbedOptions options, CancellationToken ct)
    {
        if (texts.Count == 0) return new EmbeddingBatchResult("", []);
        var all = new List<float[]>();
        var model = "sidecar";
        foreach (var chunk in texts.Chunk(64))
        {
            var body = new Dictionary<string, object?>
            {
                ["texts"] = chunk,
                ["kind"] = options.Kind == TextEmbeddingKind.Clip ? "clip" : "text",
            };
            if (options.Kind == TextEmbeddingKind.Text) body["role"] = options.Role == TextEmbeddingRole.Query ? "query" : "passage";
            using var response = await _http.SendAsync("text embedding", () => Request(HttpMethod.Post, "/v1/embed/text", JsonContent.Create(body)), _o.EmbedTimeout, canResend: true, ct);
            using var doc = await AiHttp.ReadJsonAsync(response, Kind, ct);
            model = Str(doc.RootElement, "model") ?? model;
            if (!doc.RootElement.TryGetProperty("embeddings", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() != chunk.Length)
                throw new AiRequestException("The sidecar returned a different number of embeddings than texts sent.");
            foreach (var e in arr.EnumerateArray()) all.Add(ToVector(e) ?? throw new AiRequestException("The sidecar returned an embedding that is not a list of numbers."));
        }
        return new EmbeddingBatchResult(model, all);
    }

    public async Task<LanguageResult> DetectLanguageAsync(string text, CancellationToken ct)
    {
        using var response = await _http.SendAsync("language detection", () => Request(HttpMethod.Post, "/v1/detect-language", JsonContent.Create(new { text })), _o.EmbedTimeout, canResend: true, ct);
        using var doc = await AiHttp.ReadJsonAsync(response, Kind, ct);
        var code = LanguageCodes.Normalize(Str(doc.RootElement, "language")) ?? throw new AiRequestException("The sidecar did not name a language.");
        return new LanguageResult(Str(doc.RootElement, "model") ?? "sidecar", code, Float(doc.RootElement, "confidence"));
    }

    public Task<TextResult> TranslateAsync(string text, string from, string to, Glossary glossary, CancellationToken ct) =>
        throw new AiNotSupportedException("translation", Kind);

    /// <summary>Summaries are made here, not by the sidecar: the most representative sentences of the text itself (<see cref="ExtractiveSummary"/>). Offline, deterministic, free.</summary>
    public Task<TextResult> SummariseAsync(string text, SummaryOptions options, CancellationToken ct) =>
        Task.FromResult(new TextResult(ExtractiveSummary.ModelName, ExtractiveSummary.Summarise(text, options.MaxWords)));

    // ------------------------------------------------------------------------------------------------- plumbing

    /// <summary>Streams the file as multipart field "file". A stream that can be rewound is retried on a busy sidecar; one that cannot is tried once.</summary>
    private async Task<JsonDocument> PostFileAsync(string path, Stream file, string fileName, TimeSpan timeout, Action<MultipartFormDataContent>? more, CancellationToken ct)
    {
        var start = file.CanSeek ? file.Position : 0;
        HttpRequestMessage Build()
        {
            if (file.CanSeek) file.Position = start;
            var form = new MultipartFormDataContent();
            var part = new StreamContent(file, 81920);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(part, "file", SafeFileName(fileName));
            more?.Invoke(form);
            return Request(HttpMethod.Post, path, form);
        }
        using var response = await _http.SendAsync(path, Build, timeout, file.CanSeek, ct);
        return await AiHttp.ReadJsonAsync(response, Kind, ct);
    }

    private static string SafeFileName(string name) => new string(name.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_').ToArray()) is { Length: > 0 } s ? s : "file";

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static float Float(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (float)v.GetDouble() : 0f;

    internal static long Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : 0L;

    internal static float[]? Vector(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? ToVector(v) : null;

    internal static float[]? ToVector(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Array) return null;
        var vector = new float[e.GetArrayLength()];
        var i = 0;
        foreach (var x in e.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Number) return null;
            vector[i++] = (float)x.GetDouble();
        }
        return vector;
    }
}

internal static class AiRedaction
{
    /// <summary>Removes a secret (and any bearer token) from text that might echo it back, such as a provider's error message.</summary>
    public static string Redact(string text, params string?[] secrets)
    {
        foreach (var s in secrets)
            if (!string.IsNullOrEmpty(s) && s.Length >= 4) text = text.Replace(s, "***", StringComparison.Ordinal);
        return text;
    }
}
