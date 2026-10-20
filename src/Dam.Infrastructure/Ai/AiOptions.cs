using System.Globalization;
using Dam.Domain.Ai;
using Microsoft.Extensions.Configuration;

namespace Dam.Infrastructure.Ai;

/// <summary>
/// Everything configurable about AI (ADR-0035, env: DAM_AI_*). Secrets (<see cref="ApiKey"/>, <see cref="SidecarToken"/>) come from the environment only and are
/// never logged: this type has no ToString that prints them and nothing logs it.
/// </summary>
public sealed class AiOptions
{
    /// <summary>The deployment default; a tenant may choose another (DAM_AI_PROVIDER: self-hosted, openai-compatible, none).</summary>
    public string DefaultProvider { get; set; } = AiProviderKinds.None;

    // ---- self-hosted sidecar
    public string? SidecarUrl { get; set; }
    public string? SidecarToken { get; set; }

    // ---- openai-compatible
    /// <summary>For example https://api.openai.com/v1. For Azure OpenAI use .../openai/deployments/{model}, where {model} is replaced by each feature's model (deployment) name.</summary>
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    /// <summary>bearer (Authorization: Bearer ...) or api-key (the header Azure OpenAI uses).</summary>
    public string AuthStyle { get; set; } = "bearer";
    /// <summary>Appended as ?api-version= when set (Azure OpenAI).</summary>
    public string? ApiVersion { get; set; }
    /// <summary>Tagging and OCR (a chat model that accepts images).</summary>
    public string VisionModel { get; set; } = "gpt-4o-mini";
    /// <summary>Language detection, translation, summaries.</summary>
    public string ChatModel { get; set; } = "gpt-4o-mini";
    public string EmbeddingModel { get; set; } = "text-embedding-3-small";
    public string TranscriptionModel { get; set; } = "whisper-1";
    /// <summary>Ask the embeddings endpoint for exactly <see cref="TextDim"/> dimensions. Default: only for OpenAI's text-embedding-3 models, which support it.</summary>
    public bool? SendDimensions { get; set; }
    /// <summary>Ask chat completions for response_format json_object. Turn off for servers that reject it.</summary>
    public bool JsonMode { get; set; } = true;

    // ---- vectors
    /// <summary>Size of the text (meaning) vectors. A change needs a reindex.</summary>
    public int TextDim { get; set; } = 384;
    /// <summary>Size of the image (and CLIP text) vectors. A change needs a reindex.</summary>
    public int ImageDim { get; set; } = 512;

    // ---- enrichment
    public int MaxAudioMinutes { get; set; } = 60;
    public int MaxOcrPages { get; set; } = 5;
    public int Concurrency { get; set; } = 2;
    public int TagTopK { get; set; } = 12;
    /// <summary>Tags the provider scores below this are dropped. Unset by default: each provider applies its own (the sidecar's softmax scores over a long vocabulary are small by nature).</summary>
    public float? TagThreshold { get; set; }
    /// <summary>A transcript or document is embedded as the mean of its first chunks (the title and tags are one more).</summary>
    public int MaxEmbedChunks { get; set; } = 16;
    public int ChunkChars { get; set; } = 1000;

    // ---- semantic search
    public int SemanticDepth { get; set; } = 200;
    public int RrfK { get; set; } = 60;
    /// <summary>Picture matches below this cosine similarity to the query are not results. Unset: 0.22, right for the sidecar's CLIP (relevant pictures score about 0.25 to 0.33,
    /// unrelated ones 0.10 to 0.20). Similarity scales are model-specific: measure before changing the model.</summary>
    public double? ImageMinCosine { get; set; }
    /// <summary>Text matches below this cosine similarity are not results. Unset: 0.80 for the sidecar's multilingual-e5 (its similarities are compressed: unrelated text scores
    /// 0.72 to 0.78, relevant 0.82 to 0.90), 0.30 for OpenAI-style embeddings (unrelated near 0, relevant above 0.35).</summary>
    public double? TextMinCosine { get; set; }
    /// <summary>A vector leg also drops hits more than this far (in cosine) below its own best hit.</summary>
    public double KnnMargin { get; set; } = 0.04;

    public double ImageFloor => ImageMinCosine ?? 0.22;
    public double TextFloor(string providerKind) => TextMinCosine ?? (providerKind == AiProviderKinds.OpenAiCompatible ? 0.30 : 0.80);

    // ---- resilience
    /// <summary>Attempts per call on a busy or unreachable provider (1 means no retry).</summary>
    public int Retries { get; set; } = 3;
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(400);
    /// <summary>After this many calls in a row fail for transport reasons, further calls fail at once for <see cref="CoolDown"/>.</summary>
    public int FailureThreshold { get; set; } = 3;
    public TimeSpan CoolDown { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan EmbedTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan TagTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan OcrTimeout { get; set; } = TimeSpan.FromSeconds(120);
    public TimeSpan ChatTimeout { get; set; } = TimeSpan.FromSeconds(90);
    public TimeSpan TranscribeTimeout { get; set; } = TimeSpan.FromMinutes(20);
    public TimeSpan HealthTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public int BackfillChunk { get; set; } = 200;

    /// <summary>How long enrichment waits for the rendition consumer to make the rendition it needs before making it itself.</summary>
    public TimeSpan RenditionWait { get; set; } = TimeSpan.FromSeconds(60);

    public bool SelfHostedConfigured => !string.IsNullOrWhiteSpace(SidecarUrl);
    public bool OpenAiConfigured => !string.IsNullOrWhiteSpace(BaseUrl);

    public bool IsConfigured(string kind) => kind switch
    {
        AiProviderKinds.SelfHosted => SelfHostedConfigured,
        AiProviderKinds.OpenAiCompatible => OpenAiConfigured,
        _ => true,
    };

    /// <summary>Does using this provider send pictures and audio outside this installation? A cloud service does; a server on this machine or a private network does not.</summary>
    public bool SendsDataOffSite(string kind) => kind == AiProviderKinds.OpenAiCompatible && !IsLocal(BaseUrl);

    internal static bool IsLocal(string? url)
    {
        if (!Uri.TryCreate((url ?? "").Replace("{model}", "m", StringComparison.Ordinal), UriKind.Absolute, out var uri)) return false;
        var host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)) return true;
        if (!System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var ip)) return !host.Contains('.', StringComparison.Ordinal);   // a single-label name is a private network name
        if (System.Net.IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    public static AiOptions Read(IConfiguration cfg) => Read(k => cfg[k]);

    public static AiOptions Read(Func<string, string?> get)
    {
        string? S(string k) => get(k) is { Length: > 0 } v && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        int I(string k, int d, int min, int max) => int.TryParse(S(k), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, min, max) : d;
        double D(string k, double d) => double.TryParse(S(k), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;
        TimeSpan Secs(string k, TimeSpan d) => int.TryParse(S(k), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? TimeSpan.FromSeconds(v) : d;

        var o = new AiOptions
        {
            DefaultProvider = AiProviderKinds.Parse(S("DAM_AI_PROVIDER")),
            SidecarUrl = S("DAM_AI_SIDECAR_URL")?.TrimEnd('/'),
            SidecarToken = S("DAM_AI_SIDECAR_TOKEN"),
            BaseUrl = S("DAM_AI_BASE_URL")?.TrimEnd('/'),
            ApiKey = S("DAM_AI_API_KEY"),
            AuthStyle = string.Equals(S("DAM_AI_AUTH_STYLE"), "api-key", StringComparison.OrdinalIgnoreCase) ? "api-key" : "bearer",
            ApiVersion = S("DAM_AI_API_VERSION"),
            TextDim = I("DAM_AI_TEXT_DIM", 384, 8, 4096),
            ImageDim = I("DAM_AI_IMAGE_DIM", 512, 8, 4096),
            MaxAudioMinutes = I("DAM_AI_MAX_AUDIO_MINUTES", 60, 1, AudioPlan.HardCapMinutes),
            MaxOcrPages = I("DAM_AI_MAX_OCR_PAGES", 5, 1, 100),
            Concurrency = I("DAM_AI_CONCURRENCY", 2, 1, 32),
            TagTopK = I("DAM_AI_TAG_TOP_K", 12, 1, 50),
            MaxEmbedChunks = I("DAM_AI_MAX_EMBED_CHUNKS", 16, 1, 64),
            SemanticDepth = I("DAM_AI_SEMANTIC_DEPTH", 200, 10, 1000),
            RrfK = I("DAM_AI_RRF_K", RankFusion.DefaultK, 1, 1000),
            KnnMargin = Math.Clamp(D("DAM_AI_KNN_MARGIN", 0.04), 0, 2),
            Retries = I("DAM_AI_RETRIES", 3, 1, 10),
            FailureThreshold = I("DAM_AI_FAILURE_THRESHOLD", 3, 1, 100),
            CoolDown = Secs("DAM_AI_COOLDOWN_SECONDS", TimeSpan.FromSeconds(30)),
            RenditionWait = TimeSpan.FromSeconds(I("DAM_AI_RENDITION_WAIT_SECONDS", 60, 0, 600)),
            RetryBaseDelay = TimeSpan.FromMilliseconds(I("DAM_AI_RETRY_BASE_MS", 400, 1, 60_000)),
            BackfillChunk = I("DAM_AI_BACKFILL_CHUNK", 200, 10, 2000),
        };
        // One model named for either role serves both unless the other is named too.
        var visionModel = S("DAM_AI_VISION_MODEL");
        var chatModel = S("DAM_AI_CHAT_MODEL");
        if (visionModel is not null || chatModel is not null)
        {
            o.VisionModel = visionModel ?? chatModel!;
            o.ChatModel = chatModel ?? visionModel!;
        }
        if (S("DAM_AI_EMBEDDING_MODEL") is { } emb) o.EmbeddingModel = emb;
        if (S("DAM_AI_TRANSCRIPTION_MODEL") is { } tr) o.TranscriptionModel = tr;
        if (double.TryParse(S("DAM_AI_TAG_THRESHOLD"), NumberStyles.Float, CultureInfo.InvariantCulture, out var tagThreshold)) o.TagThreshold = (float)Math.Clamp(tagThreshold, 0, 1);
        if (bool.TryParse(S("DAM_AI_SEND_DIMENSIONS"), out var sd)) o.SendDimensions = sd;
        if (bool.TryParse(S("DAM_AI_JSON_MODE"), out var jm)) o.JsonMode = jm;
        if (double.TryParse(S("DAM_AI_IMAGE_MIN_COSINE"), NumberStyles.Float, CultureInfo.InvariantCulture, out var imageFloor)) o.ImageMinCosine = Math.Clamp(imageFloor, -1, 1);
        if (double.TryParse(S("DAM_AI_TEXT_MIN_COSINE"), NumberStyles.Float, CultureInfo.InvariantCulture, out var textFloor)) o.TextMinCosine = Math.Clamp(textFloor, -1, 1);
        o.EmbedTimeout = Secs("DAM_AI_EMBED_TIMEOUT_SECONDS", o.EmbedTimeout);
        o.TagTimeout = Secs("DAM_AI_TAG_TIMEOUT_SECONDS", o.TagTimeout);
        o.OcrTimeout = Secs("DAM_AI_OCR_TIMEOUT_SECONDS", o.OcrTimeout);
        o.TranscribeTimeout = Secs("DAM_AI_TRANSCRIBE_TIMEOUT_SECONDS", o.TranscribeTimeout);
        return o;
    }
}
