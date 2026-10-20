using Dam.Domain.Ai;

namespace Dam.Application.Abstractions;

// ----------------------------------------------------------------------------------------------------- errors

/// <summary>Base of everything an AI provider can throw on purpose. Anything else is a bug.</summary>
public abstract class AiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The provider cannot do this (a capability it does not have). Callers skip the step rather than failing.</summary>
public sealed class AiNotSupportedException(string feature, string provider)
    : AiException($"The '{provider}' AI provider does not support {feature}.")
{
    public string Feature { get; } = feature;
    public string Provider { get; } = provider;
}

/// <summary>The provider could not be reached, timed out, was busy or is cooling down after repeated failures. Worth trying again later.</summary>
public sealed class AiUnavailableException(string message, Exception? inner = null) : AiException(message, inner);

/// <summary>The provider answered and refused or could not handle this input, or answered with something unusable. Trying again will not help.</summary>
public sealed class AiRequestException(string message, int? statusCode = null, Exception? inner = null) : AiException(message, inner)
{
    public int? StatusCode { get; } = statusCode;
}

// -------------------------------------------------------------------------------------------------- inputs / outputs

/// <param name="TopK">At most this many tags.</param>
/// <param name="Threshold">Tags scoring below this (0 to 1) are not worth proposing; null leaves it to the provider (its scores mean different things: a zero-shot
/// classifier spreads probability over a long vocabulary, a language model states a confidence).</param>
/// <param name="Labels">A closed vocabulary to choose from (the sidecar scores each); null for the model's own words.</param>
public sealed record TagOptions(int TopK = 12, float? Threshold = null, IReadOnlyList<string>? Labels = null);

public sealed record OcrOptions(string? Language = null);

/// <param name="MaxSeconds">Ignore audio beyond this many seconds.</param>
/// <param name="FileName">What the upload is called (cloud providers decide the audio format from the extension).</param>
public sealed record TranscribeOptions(string? Language = null, int? MaxSeconds = null, string FileName = "audio.mp3");

public enum TextEmbeddingKind
{
    /// <summary>The meaning of text in a text-only space (multilingual-e5): for searching words by meaning.</summary>
    Text,
    /// <summary>The CLIP text tower: lands in the same space as image vectors, so words can be compared with pictures.</summary>
    Clip,
}

public enum TextEmbeddingRole { Passage, Query }

public sealed record TextEmbedOptions(TextEmbeddingKind Kind = TextEmbeddingKind.Text, TextEmbeddingRole Role = TextEmbeddingRole.Passage);

public sealed record Glossary(IReadOnlyDictionary<string, string>? Terms = null)
{
    public static readonly Glossary Empty = new();
}

public sealed record SummaryOptions(int MaxWords = 120, string? Language = null);

public sealed record TagCandidate(string Label, float Score);
public sealed record TagResult(string Model, IReadOnlyList<TagCandidate> Tags);

public sealed record OcrLine(string Text, float Score);
/// <param name="Confidence">Mean line confidence when the provider gives one.</param>
public sealed record OcrResult(string Model, string? Language, string Text, IReadOnlyList<OcrLine> Lines, float? Confidence);

public sealed record TranscriptResult(string Model, string? Language, long DurationMs, IReadOnlyList<TranscriptSegment> Segments);

public sealed record EmbeddingResult(string Model, float[] Vector)
{
    public int Dim => Vector.Length;
}

public sealed record EmbeddingBatchResult(string Model, IReadOnlyList<float[]> Vectors);

public sealed record LanguageResult(string Model, string Language, float Confidence);

public sealed record TextResult(string Model, string Text);

/// <summary>What a provider can do. A step the provider cannot do is skipped, never failed.</summary>
public sealed record AiCapabilities(
    bool Tags, bool Ocr, bool Transcribe, bool TextEmbeddings, bool ImageEmbeddings, bool DetectLanguage, bool Translate, bool Summarise)
{
    public static readonly AiCapabilities None = new(false, false, false, false, false, false, false, false);
}

/// <param name="Status">ok, loading, degraded, down, unconfigured or disabled.</param>
public sealed record AiHealth(string Status, string? Detail = null, int? LatencyMs = null, IReadOnlyDictionary<string, string>? Models = null)
{
    public bool IsUsable => Status is "ok" or "degraded";
}

// ------------------------------------------------------------------------------------------------------ provider

/// <summary>
/// The AI abstraction (spec A11, decision D10): the same features whether the models run in this installation or in a cloud. Implementations
/// throw <see cref="AiNotSupportedException"/> for what they cannot do (see <see cref="Capabilities"/>), <see cref="AiUnavailableException"/> when
/// the service is down or busy, and <see cref="AiRequestException"/> when it refuses an input. Streams must be readable from the start; a seekable
/// stream is rewound for a retry. Implementations never log content, and cloud ones never log credentials.
/// </summary>
public interface IAiProvider
{
    /// <summary>One of <see cref="AiProviderKinds"/>.</summary>
    string Kind { get; }
    AiCapabilities Capabilities { get; }
    Task<AiHealth> CheckHealthAsync(CancellationToken ct);

    Task<TagResult> TagImageAsync(Stream image, TagOptions options, CancellationToken ct);
    Task<OcrResult> OcrAsync(Stream image, OcrOptions options, CancellationToken ct);
    Task<TranscriptResult> TranscribeAsync(Stream audio, TranscribeOptions options, CancellationToken ct);
    Task<EmbeddingResult> EmbedTextAsync(string text, TextEmbedOptions options, CancellationToken ct);
    /// <summary>Several texts at once (at most 64). Order is preserved.</summary>
    Task<EmbeddingBatchResult> EmbedTextsAsync(IReadOnlyList<string> texts, TextEmbedOptions options, CancellationToken ct);
    Task<EmbeddingResult> EmbedImageAsync(Stream image, CancellationToken ct);
    Task<LanguageResult> DetectLanguageAsync(string text, CancellationToken ct);
    /// <summary>Step 2.2: declared now so providers are complete; nothing calls it yet.</summary>
    Task<TextResult> TranslateAsync(string text, string from, string to, Glossary glossary, CancellationToken ct);
    /// <summary>Step 2.2: declared now; nothing calls it yet.</summary>
    Task<TextResult> SummariseAsync(string text, SummaryOptions options, CancellationToken ct);
}

// ------------------------------------------------------------------------------------------------------ resolution

/// <summary>
/// The AI configuration in force for the current tenant: the provider (a no-op provider when AI is off for the tenant, so nothing can leave the
/// building by accident), the feature flags and the OCR language hint.
/// </summary>
/// <param name="ProviderKind">The tenant's choice, else the deployment default.</param>
/// <param name="Enabled">The tenant's switch. When false, <see cref="Provider"/> is a no-op provider and no external call is ever made.</param>
/// <param name="Configured">The chosen provider has the settings it needs (a URL, a key).</param>
/// <param name="DisabledReason">Why nothing will run, when nothing will.</param>
public sealed record AiTenantContext(
    string ProviderKind, bool Enabled, bool Configured, AiFeatureFlags Features, string? OcrLanguage, IAiProvider Provider, string? DisabledReason, bool SendsDataOffSite)
{
    public bool IsActive => Enabled && Configured && Provider.Kind != AiProviderKinds.None;
}

public interface IAiResolver
{
    /// <summary>The configuration for the ambient tenant. Provider is a no-op when the tenant has not enabled AI or the provider is not configured.</summary>
    Task<AiTenantContext> ResolveAsync(CancellationToken ct);

    /// <summary>The tenant's chosen provider even when AI is switched off: for health checks and the administrator's test, which send nothing of the tenant's.</summary>
    Task<AiTenantContext> ResolveForTestAsync(CancellationToken ct);

    /// <summary>The deployment-wide default provider (DAM_AI_PROVIDER).</summary>
    string DefaultProvider { get; }

    IReadOnlyList<(string Kind, bool Configured, bool SendsDataOffSite)> Providers();
}

/// <param name="Clip">The query in the image-vector space (null: not available).</param>
/// <param name="Text">The query in the text-vector space (null: not available).</param>
/// <param name="Fallback">Why no semantic search can run at all; null when at least one vector is available.</param>
/// <param name="ImageMinCosine">The cosine similarity below which a picture is not a match for this provider's models (similarity scales differ by model).</param>
/// <param name="TextMinCosine">The same for the meaning of text.</param>
public sealed record SemanticQueryVectors(float[]? Clip, float[]? Text, string? Fallback, double? ImageMinCosine = null, double? TextMinCosine = null)
{
    public static SemanticQueryVectors Unavailable(string reason) => new(null, null, reason);
}

/// <summary>Turns a search phrase into the vectors semantic search compares against. Never throws for provider trouble: it reports a fallback reason.</summary>
public interface ISemanticQueryEmbedder
{
    Task<SemanticQueryVectors> EmbedAsync(string query, CancellationToken ct);
}
