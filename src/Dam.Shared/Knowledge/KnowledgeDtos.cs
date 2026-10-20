namespace Dam.Shared.Knowledge;

/// <summary>One entry of a document's table of contents. A PDF chapter has pages, an HTML chapter an anchor (the heading's id); either may be null.</summary>
/// <param name="Level">1 for a top-level chapter, 2 for a section of it, and so on.</param>
/// <param name="PageFrom">First page (1-based). PageTo is the page before the next chapter of the same or a higher level, or the last page.</param>
public sealed record ChapterDto(string Title, string? Anchor, int Level, int? PageFrom, int? PageTo);

/// <param name="DocType">owner_manual, faq, service, training or product_info.</param>
/// <param name="Model">A code (lower case letters, digits, '.', '_' and '-'), as VIN rules name it.</param>
/// <param name="Variant">Null: the document fits every variant of the model.</param>
/// <param name="ModelYearFrom">With ModelYearTo: the model years the document is for. Both or neither; a single year is from = to.</param>
/// <param name="ChaptersSource">none, pdf, html or manual.</param>
/// <param name="ChaptersIndexedAt">When the chapters were last read or entered.</param>
/// <param name="CurrentVersionId">The asset's current version: a new version replaces the old one without changing the document.</param>
public sealed record KnowledgeDocumentDto(
    Guid Id, Guid AssetId, string Title, string AssetType, string AssetStatus, string DocType, string Model, string? Variant, int? ModelYearFrom, int? ModelYearTo,
    string Language, string? Theme, int ChapterCount, string ChaptersSource, DateTimeOffset? ChaptersIndexedAt, Guid? CurrentVersionId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record KnowledgeDocumentDetailDto(
    Guid Id, Guid AssetId, string Title, string AssetType, string AssetStatus, string DocType, string Model, string? Variant, int? ModelYearFrom, int? ModelYearTo,
    string Language, string? Theme, int ChapterCount, string ChaptersSource, DateTimeOffset? ChaptersIndexedAt, Guid? CurrentVersionId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<ChapterDto> Chapters);

/// <param name="AssetId">A manual, document or html5 asset the caller may update. One document per asset.</param>
/// <param name="Language">A language tag (see multilingual metadata). Omitted: the tenant's default language, else en.</param>
public sealed record CreateKnowledgeDocumentInput(
    Guid? AssetId, string? DocType, string? Model, string? Variant, int? ModelYearFrom, int? ModelYearTo, string? Language, string? Theme);

/// <summary>Replaces the document's mapping; the asset cannot be changed (delete the document and make another).</summary>
public sealed record UpdateKnowledgeDocumentInput(
    string? DocType, string? Model, string? Variant, int? ModelYearFrom, int? ModelYearTo, string? Language, string? Theme);

/// <param name="Chapters">At most 500. An empty list clears the chapters and hands the document back to automatic indexing.</param>
public sealed record ChaptersInput(IReadOnlyList<ChapterDto>? Chapters);

public sealed record ChaptersDto(string Source, DateTimeOffset? IndexedAt, IReadOnlyList<ChapterDto> Chapters);

/// <param name="Pattern">A VIN pattern: letters, digits, ? for any character, [ABC] or [A-C] for a set, and a final * for "and anything after". Without the * it has to describe all 17 characters.</param>
/// <param name="Name">Unique in the tenant, ignoring case.</param>
/// <param name="ModelYear">A fixed model year (it wins over the VIN). Omitted: read it from the VIN's tenth character when YearFromVin is true.</param>
/// <param name="YearFromVin">Default true unless ModelYear is given.</param>
/// <param name="Priority">Higher wins when several rules match. Default 0.</param>
/// <param name="Enabled">Default true.</param>
public sealed record VinRuleInput(
    string? Name, string? Pattern, string? Model, string? Variant, int? ModelYear, bool? YearFromVin, string? Theme, int? Priority, bool? Enabled);

public sealed record VinRuleDto(
    Guid Id, string Name, string Pattern, string Model, string? Variant, int? ModelYear, bool YearFromVin, string? Theme, int Priority, bool Enabled,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record VinTestInput(string? Vin);

public sealed record VinRuleRefDto(Guid Id, string Name, int Priority);

public sealed record VehicleDto(string Model, string? Variant, int? ModelYear, string? Theme);

/// <param name="Specificity">The number of plain letters and digits in the pattern.</param>
/// <param name="Reason">Why the rule matched, or the first character that disagrees.</param>
public sealed record VinCandidateDto(Guid RuleId, string Name, int Priority, int Specificity, bool Matched, string Reason);

public sealed record VinChosenDto(Guid RuleId, string Name);

/// <param name="Valid">False when the VIN is not well formed (still a 200); Reason says why and nothing else is filled in.</param>
public sealed record VinTestDto(string Vin, bool Valid, VinChosenDto? Chosen, VehicleDto? Vehicle, IReadOnlyList<VinCandidateDto> Candidates, string? Reason = null);

public sealed record VinImportErrorDto(int Line, string Message);

/// <param name="Skipped">Valid lines that would change nothing, because the stored rule is identical.</param>
/// <param name="Errors">Invalid lines, by line number (the header is line 1). With any of them, and without partial, nothing was created or changed.</param>
public sealed record VinImportResultDto(int Created, int Updated, int Skipped, IReadOnlyList<VinImportErrorDto> Errors, bool DryRun);

/// <summary>The document a VIN resolved to: the mapping, the asset's state and its current version.</summary>
public sealed record ResolvedDocumentDto(
    Guid Id, Guid AssetId, string Title, string AssetType, string AssetStatus, string DocType, string Model, string? Variant, int? ModelYearFrom, int? ModelYearTo,
    string Language, string? Theme, int ChapterCount, string ChaptersSource, DateTimeOffset? ChaptersIndexedAt, Guid? CurrentVersionId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<ChapterDto> Chapters, Guid? VersionId, int? VersionNumber);

public sealed record OtherLanguageDto(string Language, Guid DocumentId, Guid AssetId);

public sealed record ResolveDto(string Vin, VinRuleRefDto Rule, VehicleDto Vehicle, ResolvedDocumentDto Document, IReadOnlyList<OtherLanguageDto> OtherLanguages);
