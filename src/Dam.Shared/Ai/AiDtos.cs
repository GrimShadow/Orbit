namespace Dam.Shared.Ai;

/// <param name="Kind">tag, language or translation.</param>
/// <param name="Value">The tag, the language code, or (for a translation) the translated text.</param>
/// <param name="Status">pending, accepted or rejected.</param>
/// <param name="Confidence">0 to 1, as the model reported it. 0 for a translation (the provider states none).</param>
/// <param name="Field">Translations only: the multilingual metadata field the text is for.</param>
/// <param name="Language">Translations only: the target language.</param>
/// <param name="SourceLanguage">Translations only: the language it was translated from.</param>
/// <param name="SourceStale">Translations only: true while pending and the source text has changed since the translation was made.</param>
public sealed record AiSuggestionDto(
    Guid Id, string Kind, string Value, float Confidence, string Model, string Status, DateTimeOffset CreatedAt, Guid? DecidedBy, DateTimeOffset? DecidedAt,
    string? Field = null, string? Language = null, string? SourceLanguage = null, bool SourceStale = false);

/// <param name="Status">ok, skipped (the provider cannot do it, or there was nothing to do) or failed.</param>
public sealed record AiStepDto(string Name, string Status, int DurationMs, string? Model, string? Detail);

/// <param name="Status">pending (queued or waiting for a retry), running, succeeded, failed or skipped.</param>
public sealed record AiRunDto(
    Guid Id, Guid VersionId, string Status, IReadOnlyList<AiStepDto> Steps, string? Error, int Attempts, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt);

public sealed record TranscriptSegmentDto(long StartMs, long EndMs, string Text);

public sealed record AiTranscriptDto(string? Language, long? DurationMs, string? Model, IReadOnlyList<TranscriptSegmentDto> Segments);

/// <summary>A short summary of an asset's text, written by the AI. Show it as such.</summary>
/// <param name="Model">The model that wrote it; <c>extractive-v1</c> for the built-in summariser that picks sentences from the text itself.</param>
public sealed record AiSummaryDto(string Text, string? Language, string Model, DateTimeOffset CreatedAt);

/// <summary>What the AI found for an asset's current version.</summary>
/// <param name="Status">none (never enriched), pending, running, ready, failed or disabled (AI is switched off for the tenant and the asset has no results).</param>
/// <param name="Language">The detected language code of the text (OCR or transcript), the best proposal not rejected; null when none.</param>
/// <param name="OcrText">Text read from the picture or pages, cut at 20,000 characters (<paramref name="OcrTextTruncated"/>).</param>
/// <param name="EmbeddingsReady">The asset has the vectors that semantic search uses.</param>
/// <param name="Suggestions">Tag and language suggestions of the current version, and every translation suggestion of the asset (translations belong to its metadata, not to a file).</param>
/// <param name="Summary">The summary of the text, when there is one (documents longer than two pages, or when asked for).</param>
public sealed record AssetAiDto(
    string Status, AiRunDto? Run, IReadOnlyList<AiSuggestionDto> Suggestions, string? Language, string? OcrText, bool OcrTextTruncated,
    string? OcrLanguage, AiTranscriptDto? Transcript, bool EmbeddingsReady, AiSummaryDto? Summary = null);

/// <summary>Optional body of "accept suggestion": only translations use it.</summary>
/// <param name="Text">The translation as the reviewer edited it; omitted to accept the suggested text.</param>
/// <param name="Replace">Allow replacing text the target language already has (otherwise accepting is a 409 conflict).</param>
public sealed record AcceptSuggestionInput(string? Text = null, bool Replace = false);

/// <summary>The outcome of accepting or rejecting suggestions.</summary>
/// <param name="Changed">How many suggestions changed state.</param>
/// <param name="Tags">The asset's tags afterwards.</param>
/// <param name="Notes">Plain-language remarks (a language accepted but not applied because the schema has no language field; tags left pending because the asset is at its tag limit).</param>
public sealed record AiDecisionDto(IReadOnlyList<AiSuggestionDto> Suggestions, IReadOnlyList<string> Tags, int Changed, IReadOnlyList<string> Notes);

/// <param name="Summaries">Summaries of long documents. Optional when saving settings (defaults to on).</param>
public sealed record AiFeaturesDto(bool Tags, bool Ocr, bool Transcribe, bool Embeddings, bool Summaries = true);

public sealed record AiProviderInfoDto(string Kind, bool Configured, bool SendsDataOffSite, string Description);

/// <param name="Provider">The tenant's own choice, or null to follow the deployment default.</param>
/// <param name="EffectiveProvider">What actually runs: the choice, else the default.</param>
/// <param name="SendsDataOffSite">True when enabling this would send pictures and audio to a service outside this installation (a cloud provider).</param>
public sealed record AiSettingsDto(
    bool Enabled, string? Provider, string DefaultProvider, string EffectiveProvider, bool SendsDataOffSite, AiFeaturesDto Features, string? OcrLanguage,
    IReadOnlyList<AiProviderInfoDto> Providers, Guid? UpdatedBy, DateTimeOffset? UpdatedAt);

public sealed record AiSettingsInput(bool Enabled, string? Provider, AiFeaturesDto? Features, string? OcrLanguage);

public sealed record AiProviderCapabilitiesDto(
    bool Tags, bool Ocr, bool Transcribe, bool TextEmbeddings, bool ImageEmbeddings, bool DetectLanguage, bool Translate, bool Summarise);

/// <param name="Status">ok, loading, degraded, down, unconfigured or disabled.</param>
public sealed record AiHealthDto(string Status, string? Detail, int? LatencyMs, IReadOnlyDictionary<string, string> Models);

public sealed record AiCountsDto(int Eligible, int Enriched, int Pending, int Running, int Failed, int Skipped, int NotStarted);

/// <param name="Text">The configured text vector size, and the size the live index was built with (null: no index yet).</param>
public sealed record AiVectorIndexDto(int TextDim, int ImageDim, int? LiveTextDim, int? LiveImageDim, bool NeedsReindex);

/// <param name="QueueDepth">Enrichment work waiting: queued or retrying runs, plus requests not yet handed to the message bus.</param>
public sealed record AiStatusDto(
    bool Enabled, string Provider, bool ProviderConfigured, AiHealthDto Health, AiProviderCapabilitiesDto Capabilities, AiCountsDto Counts, int QueueDepth,
    AiVectorIndexDto VectorIndex);

public sealed record AiBackfillInput(IReadOnlyList<string>? Types, Guid? FolderId, bool? IncludeSubfolders, bool? OnlyMissing, int? Limit);

/// <param name="Matched">Assets that fit the filter (and still lacked results when OnlyMissing).</param>
/// <param name="Enqueued">Queued by this call (at most the limit).</param>
/// <param name="Remaining">Matched but left for another call.</param>
public sealed record AiBackfillResultDto(int Matched, int Enqueued, int Remaining);

public sealed record AiTestStepDto(string Name, bool Ok, int DurationMs, string? Detail);

public sealed record AiTestResultDto(string Provider, bool Ok, int TotalMs, IReadOnlyList<AiTestStepDto> Steps);

public sealed record AiReindexDto(bool Accepted, string Message);

/// <summary>What the signed-in user's tenant can do with AI right now, for any user (no ai.manage needed): whether to offer Smart search and show AI panels. Never carries URLs, keys or models.</summary>
/// <param name="Enabled">AI is switched on for the tenant and its provider is configured.</param>
/// <param name="SemanticSearch">Enabled, embeddings on, the provider offers at least one kind of vector, and the tenant's search index is built for the configured vector sizes (not awaiting a reindex).</param>
/// <param name="Enrichment">Enabled and at least one feature (tags, OCR, transcripts, embeddings) is on.</param>
/// <param name="TextVectors">Words are compared by meaning (text embeddings are produced and searched).</param>
/// <param name="ImageVectors">Pictures are compared with words (image embeddings; the OpenAI-compatible provider has none).</param>
/// <param name="Provider">The provider kind in force (self-hosted, openai-compatible, none).</param>
/// <param name="Translate">Enabled and the provider can translate (the self-hosted provider cannot yet: it needs the OpenAI-compatible provider).</param>
/// <param name="Summarise">Enabled, summaries switched on and the provider can summarise (the self-hosted provider does it offline, by picking sentences).</param>
public sealed record AiCapabilitiesDto(
    bool Enabled, bool SemanticSearch, bool Enrichment, bool TextVectors, bool ImageVectors, string Provider, bool SendsDataOffSite, bool Translate = false, bool Summarise = false);
