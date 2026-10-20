namespace Dam.Shared.Ai;

/// <param name="Mode">locked (never translated) or mapped (a fixed rendering per language).</param>
/// <param name="Translations">Language code to rendering; empty for a locked term.</param>
public sealed record GlossaryTermDto(Guid Id, string Term, string Mode, IReadOnlyDictionary<string, string> Translations, string Note, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <param name="Term">1 to 200 characters, unique in the tenant (matched exactly, case included).</param>
/// <param name="Mode">locked or mapped. A mapped term needs at least one translation; a locked term's translations are ignored.</param>
/// <param name="Translations">Language code to rendering, each 1 to 200 characters.</param>
/// <param name="Note">Why it is here (up to 500 characters).</param>
public sealed record GlossaryTermInput(string? Term, string? Mode, Dictionary<string, string>? Translations, string? Note);

/// <param name="Fields">Multilingual (ml_text) fields of the asset's type to translate; at most 20.</param>
/// <param name="TargetLanguages">Language codes to translate into; at most 10.</param>
/// <param name="SourceLanguage">Translate from this language; omitted, each field's source is chosen (the tenant's default, English, then any other with text).</param>
/// <param name="Overwrite">Also translate languages that already have text (accepting a result still needs a decision). Default false: they are skipped.</param>
public sealed record TranslateInput(IReadOnlyList<string>? Fields, IReadOnlyList<string>? TargetLanguages, string? SourceLanguage = null, bool? Overwrite = null);

/// <param name="Status">ok, skipped (nothing to translate from, text already there, ...) or failed.</param>
/// <param name="Reason">Why it was skipped or failed.</param>
public sealed record TranslationItemDto(string Field, string Language, string Status, string? Reason);

/// <param name="Status">pending, running, succeeded, partial (some items failed), failed.</param>
/// <param name="SourceLanguage">As requested; null when left to be chosen per field.</param>
/// <param name="Error">Why the whole job could not run, when it could not.</param>
public sealed record TranslationJobDto(
    Guid Id, Guid AssetId, string Status, IReadOnlyList<string> Fields, IReadOnlyList<string> TargetLanguages, string? SourceLanguage, bool Overwrite, Guid? RequestedBy,
    DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt, IReadOnlyList<TranslationItemDto> Items, string? Error = null);

/// <param name="Jobs">The latest ten, newest first. The translations themselves are the suggestions in GET /assets/{id}/ai.</param>
public sealed record AssetTranslationsDto(IReadOnlyList<TranslationJobDto> Jobs);

/// <summary>An asset with translations waiting for a decision.</summary>
/// <param name="Pending">How many translation suggestions wait.</param>
/// <param name="Languages">Target languages among them.</param>
/// <param name="Fields">Fields among them.</param>
/// <param name="RequestedBy">Who asked for the latest translation job of the asset, when known.</param>
/// <param name="OldestAt">When the oldest waiting suggestion was made.</param>
public sealed record AiReviewItemDto(
    Guid AssetId, string Title, string AssetType, int Pending, IReadOnlyList<string> Languages, IReadOnlyList<string> Fields, Guid? RequestedBy, DateTimeOffset OldestAt);

/// <param name="Translation">How many assets have translations waiting for the caller to review (assets the caller can edit).</param>
public sealed record AiReviewSummaryDto(int Translation);
