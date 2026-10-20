using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dam.Application.Abstractions;
using Dam.Domain.Ai;
using Microsoft.Extensions.Logging;

namespace Dam.Infrastructure.Ai;

/// <summary>
/// Any service that speaks the OpenAI REST API: OpenAI itself, Azure OpenAI, an India-region or other sovereign endpoint, vLLM, Ollama and the like
/// (ADR-0035). Tags and OCR are a vision chat call asked to answer in strict JSON, text embeddings use <c>/embeddings</c>, transcripts
/// <c>/audio/transcriptions</c> with timestamps. The standard API has no image embeddings, so <see cref="Capabilities"/> says so and semantic image search
/// falls back to the text vectors. The API key comes from the environment, travels only in a header, and is scrubbed from anything that is logged or thrown.
/// </summary>
public sealed partial class OpenAiCompatibleProvider : IAiProvider, IDisposable
{
    private readonly AiOptions _o;
    private readonly AiHttp _http;
    private readonly ILogger _log;

    public OpenAiCompatibleProvider(AiOptions options, ILogger<OpenAiCompatibleProvider> log, TimeProvider? time = null, HttpMessageHandler? handler = null)
    {
        _o = options;
        _log = log;
        _http = new AiHttp(Kind, options, log, time ?? TimeProvider.System, Redact, handler);
        Capabilities = new AiCapabilities(
            Tags: true, Ocr: true, Transcribe: !string.IsNullOrWhiteSpace(options.TranscriptionModel), TextEmbeddings: !string.IsNullOrWhiteSpace(options.EmbeddingModel),
            ImageEmbeddings: false, DetectLanguage: true, Translate: true, Summarise: true);
    }

    public string Kind => AiProviderKinds.OpenAiCompatible;

    public void Dispose() => _http.Dispose();
    public AiCapabilities Capabilities { get; }

    [GeneratedRegex("(sk|key|tok)-[A-Za-z0-9_\\-]{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex KeyLike();

    private string Redact(string text) => KeyLike().Replace(AiRedaction.Redact(text, _o.ApiKey), "***");

    // ------------------------------------------------------------------------------------------------ requests

    private string Url(string model, string path)
    {
        var baseUrl = _o.BaseUrl ?? throw new AiUnavailableException("The OpenAI-compatible AI provider is not configured (DAM_AI_BASE_URL).");
        var url = baseUrl.Replace("{model}", Uri.EscapeDataString(model), StringComparison.Ordinal).TrimEnd('/') + path;
        return string.IsNullOrEmpty(_o.ApiVersion) ? url : url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "api-version=" + Uri.EscapeDataString(_o.ApiVersion);
    }

    private HttpRequestMessage Request(string model, string path, HttpContent content, HttpMethod? method = null)
    {
        var r = new HttpRequestMessage(method ?? HttpMethod.Post, Url(model, path)) { Content = content };
        if (!string.IsNullOrEmpty(_o.ApiKey))
        {
            if (_o.AuthStyle == "api-key") r.Headers.TryAddWithoutValidation("api-key", _o.ApiKey);
            else r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _o.ApiKey);
        }
        return r;
    }

    public async Task<AiHealth> CheckHealthAsync(CancellationToken ct)
    {
        if (!_o.OpenAiConfigured) return new AiHealth("unconfigured", "DAM_AI_BASE_URL is not set.");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var models = new Dictionary<string, string> { ["vision"] = _o.VisionModel, ["chat"] = _o.ChatModel, ["embeddings"] = _o.EmbeddingModel, ["transcription"] = _o.TranscriptionModel };
        // The cheapest call that proves the address, the key and the model name: a one-word embedding. (Listing models is not offered everywhere.)
        var (response, error) = await _http.ProbeAsync(() => Request(_o.EmbeddingModel, "/embeddings", JsonContent.Create(EmbeddingBody(["ping"]))), _o.HealthTimeout + TimeSpan.FromSeconds(10), ct);
        if (response is null) return new AiHealth("down", error, null, models);
        using (response)
        {
            var ms = (int)sw.ElapsedMilliseconds;
            if (response.IsSuccessStatusCode) return new AiHealth("ok", null, ms, models);
            return new AiHealth("down", $"The service answered {(int)response.StatusCode}{(response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden ? " (check the API key)" : "")}.", ms, models);
        }
    }

    // --------------------------------------------------------------------------------------------- vision / chat

    public async Task<TagResult> TagImageAsync(Stream image, TagOptions options, CancellationToken ct)
    {
        var labels = options.Labels is { Count: > 0 } l ? $" Choose only from these labels: {JsonSerializer.Serialize(l)}." : "";
        var system = "You label pictures for a digital asset manager. Reply with JSON only, shaped {\"tags\":[{\"label\":string,\"score\":number}]}. "
            + "A label is a short lowercase noun or noun phrase of one to three words naming a main subject, the setting, a dominant colour or the style. "
            + $"score is your confidence from 0 to 1. Give at most {options.TopK} labels, most confident first.{labels}";
        var content = await ChatAsync(_o.VisionModel, system, new object[] { ImagePart(await ReadImageAsync(image, ct)), TextPart("Label this picture.") }, json: true, _o.TagTimeout, 400, ct);
        using var doc = ParseModelJson(content);
        var tags = new List<TagCandidate>();
        var floor = options.Threshold ?? 0.3f;   // a language model states a confidence; below this it is guessing
        if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("tags", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var t in arr.EnumerateArray())
            {
                if (t.ValueKind == JsonValueKind.String) { tags.Add(new TagCandidate(t.GetString()!, 0.5f)); continue; }
                if (SelfHostedProvider.Str(t, "label") is { } label) tags.Add(new TagCandidate(label, t.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number ? (float)s.GetDouble() : 0.5f));
            }
        return new TagResult(_o.VisionModel, tags.Where(t => t.Score >= floor).ToList());
    }

    public async Task<OcrResult> OcrAsync(Stream image, OcrOptions options, CancellationToken ct)
    {
        var hint = string.IsNullOrWhiteSpace(options.Language) ? "" : $" The text is probably in the language with ISO 639-1 code '{options.Language}'.";
        var system = "You read text from pictures. Transcribe all legible text exactly as written, in reading order, keeping line breaks. "
            + "Reply with JSON only, shaped {\"language\": string or null, \"text\": string}, where language is the ISO 639-1 code of the main language. "
            + "If there is no text use an empty string. Never describe the picture." + hint;
        var content = await ChatAsync(_o.VisionModel, system, new object[] { ImagePart(await ReadImageAsync(image, ct)), TextPart("Transcribe the text.") }, json: true, _o.OcrTimeout, 4000, ct);
        using var doc = ParseModelJson(content);
        var text = SelfHostedProvider.Str(doc.RootElement, "text") ?? "";
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => new OcrLine(t, 1f)).ToList();
        return new OcrResult(_o.VisionModel, LanguageCodes.Normalize(SelfHostedProvider.Str(doc.RootElement, "language")), text.Trim(), lines, null);
    }

    public async Task<LanguageResult> DetectLanguageAsync(string text, CancellationToken ct)
    {
        var system = "Identify the language of the text. Reply with JSON only, shaped {\"language\": ISO 639-1 code, \"confidence\": number from 0 to 1}.";
        var content = await ChatAsync(_o.ChatModel, system, text.Length > 2000 ? text[..2000] : text, json: true, _o.EmbedTimeout, 60, ct);
        using var doc = ParseModelJson(content);
        var code = LanguageCodes.Normalize(SelfHostedProvider.Str(doc.RootElement, "language")) ?? throw new AiRequestException("The model did not name a language.");
        var confidence = doc.RootElement.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? (float)Math.Clamp(c.GetDouble(), 0, 1) : 0.7f;
        return new LanguageResult(_o.ChatModel, code, confidence);
    }

    public async Task<TextResult> TranslateAsync(string text, string from, string to, Glossary glossary, CancellationToken ct)
    {
        var locked = glossary.Terms is { Count: > 0 } t
            ? " Keep these terms exactly as written, untranslated: " + string.Join("; ", t.Select(kv => string.IsNullOrEmpty(kv.Value) ? kv.Key : $"{kv.Key} (use '{kv.Value}')")) + "."
            : "";
        var system = $"Translate the user's text from {from} to {to}. Reply with the translation only: no quotes, no notes.{locked}";
        return new TextResult(_o.ChatModel, (await ChatAsync(_o.ChatModel, system, text, json: false, _o.ChatTimeout, 4000, ct)).Trim());
    }

    public async Task<TextResult> SummariseAsync(string text, SummaryOptions options, CancellationToken ct)
    {
        var language = string.IsNullOrWhiteSpace(options.Language) ? "the language of the text" : options.Language;
        var system = $"Summarise the user's text in at most {options.MaxWords} words, in {language}. Reply with the summary only.";
        return new TextResult(_o.ChatModel, (await ChatAsync(_o.ChatModel, system, text, json: false, _o.ChatTimeout, 1500, ct)).Trim());
    }

    private static object TextPart(string text) => new { type = "text", text };

    private static object ImagePart((byte[] Bytes, string Mime) image) =>
        new { type = "image_url", image_url = new { url = $"data:{image.Mime};base64,{Convert.ToBase64String(image.Bytes)}", detail = "auto" } };

    /// <summary>One chat completion; returns the model's text. The body is rebuilt for each attempt, so a retry is safe.</summary>
    private async Task<string> ChatAsync(string model, string system, object userContent, bool json, TimeSpan timeout, int maxTokens, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = new object[] { new { role = "system", content = system }, new { role = "user", content = userContent } },
            ["temperature"] = 0,
            ["max_tokens"] = maxTokens,
        };
        if (json && _o.JsonMode) body["response_format"] = new { type = "json_object" };
        using var response = await _http.SendAsync("chat completion", () => Request(model, "/chat/completions", JsonContent.Create(body)), timeout, canResend: true, ct);
        using var doc = await AiHttp.ReadJsonAsync(response, Kind, ct);
        if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var message) && SelfHostedProvider.Str(message, "content") is { } content)
            return content;
        throw new AiRequestException("The model's answer had no content.");
    }

    /// <summary>The model's JSON, tolerating a markdown code fence or a sentence around it.</summary>
    private static JsonDocument ParseModelJson(string content)
    {
        var t = content.Trim();
        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = t.IndexOf('\n', StringComparison.Ordinal);
            t = firstNewline >= 0 ? t[(firstNewline + 1)..] : t;
            var close = t.LastIndexOf("```", StringComparison.Ordinal);
            if (close >= 0) t = t[..close];
        }
        var start = t.IndexOf('{', StringComparison.Ordinal);
        var end = t.LastIndexOf('}');
        if (start >= 0 && end > start) t = t[start..(end + 1)];
        try { return JsonDocument.Parse(t); }
        catch (JsonException ex) { throw new AiRequestException("The model did not answer in the JSON shape that was asked for.", null, ex); }
    }

    private static async Task<(byte[] Bytes, string Mime)> ReadImageAsync(Stream image, CancellationToken ct)
    {
        if (image.CanSeek) image.Position = 0;
        using var ms = new MemoryStream();
        await image.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        if (image.CanSeek) image.Position = 0;
        return (bytes, SniffImageMime(bytes));
    }

    internal static string SniffImageMime(byte[] b)
    {
        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "image/webp";
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G') return "image/png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8) return "image/jpeg";
        if (b.Length >= 4 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F') return "image/gif";
        return "image/jpeg";
    }

    // ------------------------------------------------------------------------------------------------ embeddings

    private bool WantsDimensions => _o.SendDimensions ?? _o.EmbeddingModel.StartsWith("text-embedding-3", StringComparison.OrdinalIgnoreCase);

    private Dictionary<string, object?> EmbeddingBody(IReadOnlyList<string> input)
    {
        var body = new Dictionary<string, object?> { ["model"] = _o.EmbeddingModel, ["input"] = input };
        if (WantsDimensions) body["dimensions"] = _o.TextDim;
        return body;
    }

    public async Task<EmbeddingResult> EmbedTextAsync(string text, TextEmbedOptions options, CancellationToken ct)
    {
        var batch = await EmbedTextsAsync([text], options, ct);
        return new EmbeddingResult(batch.Model, batch.Vectors[0]);
    }

    public async Task<EmbeddingBatchResult> EmbedTextsAsync(IReadOnlyList<string> texts, TextEmbedOptions options, CancellationToken ct)
    {
        if (options.Kind == TextEmbeddingKind.Clip) throw new AiNotSupportedException("embeddings in the image space (CLIP)", Kind);
        if (texts.Count == 0) return new EmbeddingBatchResult(_o.EmbeddingModel, []);
        var all = new List<float[]>();
        var model = _o.EmbeddingModel;
        foreach (var chunk in texts.Chunk(64))
        {
            var body = EmbeddingBody(chunk);
            using var response = await _http.SendAsync("embeddings", () => Request(_o.EmbeddingModel, "/embeddings", JsonContent.Create(body)), _o.EmbedTimeout, canResend: true, ct);
            using var doc = await AiHttp.ReadJsonAsync(response, Kind, ct);
            model = SelfHostedProvider.Str(doc.RootElement, "model") ?? model;
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() != chunk.Length)
                throw new AiRequestException("The embeddings answer had a different number of vectors than texts sent.");
            var ordered = data.EnumerateArray().OrderBy(d => d.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : 0).ToList();
            foreach (var d in ordered)
            {
                var v = SelfHostedProvider.Vector(d, "embedding") ?? throw new AiRequestException("The embeddings answer had a vector that is not a list of numbers.");
                if (v.Length != _o.TextDim)
                    throw new AiRequestException($"The embedding model returned {v.Length}-dimensional vectors but DAM_AI_TEXT_DIM is {_o.TextDim}. Set DAM_AI_TEXT_DIM to {v.Length} (and reindex), or pick a model that supports the 'dimensions' option.");
                all.Add(VectorMath.Normalize(v));
            }
        }
        return new EmbeddingBatchResult(model, all);
    }

    public Task<EmbeddingResult> EmbedImageAsync(Stream image, CancellationToken ct) =>
        throw new AiNotSupportedException("image embeddings (the OpenAI API has no endpoint for them; semantic search uses text vectors only)", Kind);

    // ------------------------------------------------------------------------------------------------ transcripts

    public async Task<TranscriptResult> TranscribeAsync(Stream audio, TranscribeOptions options, CancellationToken ct)
    {
        var start = audio.CanSeek ? audio.Position : 0;
        var mime = MimeOfAudio(options.FileName);
        HttpRequestMessage Build()
        {
            if (audio.CanSeek) audio.Position = start;
            var form = new MultipartFormDataContent();
            var part = new StreamContent(audio, 81920);
            part.Headers.ContentType = new MediaTypeHeaderValue(mime);
            form.Add(part, "file", new string(options.FileName.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_').ToArray()) is { Length: > 0 } n ? n : "audio.mp3");
            form.Add(new StringContent(_o.TranscriptionModel), "model");
            form.Add(new StringContent("verbose_json"), "response_format");
            form.Add(new StringContent("segment"), "timestamp_granularities[]");
            if (!string.IsNullOrWhiteSpace(options.Language)) form.Add(new StringContent(options.Language), "language");
            return Request(_o.TranscriptionModel, "/audio/transcriptions", form);
        }
        using var response = await _http.SendAsync("transcription", Build, _o.TranscribeTimeout, audio.CanSeek, ct);
        using var doc = await AiHttp.ReadJsonAsync(response, Kind, ct);
        var root = doc.RootElement;
        var segments = new List<TranscriptSegment>();
        if (root.TryGetProperty("segments", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var s in arr.EnumerateArray())
            {
                var text = SelfHostedProvider.Str(s, "text")?.Trim();
                if (string.IsNullOrEmpty(text)) continue;
                var startMs = Seconds(s, "start");
                segments.Add(new TranscriptSegment(startMs, Math.Max(startMs, Seconds(s, "end")), text));
            }
        var duration = Seconds(root, "duration");
        if (segments.Count == 0 && SelfHostedProvider.Str(root, "text")?.Trim() is { Length: > 0 } whole) segments.Add(new TranscriptSegment(0, duration, whole));
        if (duration == 0 && segments.Count > 0) duration = segments[^1].EndMs;
        return new TranscriptResult(_o.TranscriptionModel, LanguageCodes.Normalize(SelfHostedProvider.Str(root, "language") ?? options.Language), duration, segments);
    }

    private static long Seconds(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (long)Math.Round(v.GetDouble() * 1000) : 0L;

    private static string MimeOfAudio(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".wav" => "audio/wav", ".ogg" or ".opus" => "audio/ogg", ".flac" => "audio/flac", ".m4a" => "audio/mp4", ".webm" => "audio/webm", _ => "audio/mpeg",
    };
}
