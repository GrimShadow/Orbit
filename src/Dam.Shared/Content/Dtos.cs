using System.Text.Json;

namespace Dam.Shared.Content;

public sealed record FolderDto(
    Guid Id, Guid? ParentId, string Path, string Label, string Name,
    IReadOnlyDictionary<string, JsonElement> InheritedMetadata, Guid? DefaultWorkflowId, DateTimeOffset CreatedAt);

public sealed record FolderNodeDto(FolderDto Folder, IReadOnlyList<FolderNodeDto> Children);

public sealed record FolderRefDto(Guid Id, string Path, string Name);

/// <summary>A folder with its ancestors and the metadata assets inside it inherit (deeper folders win, key by key).</summary>
public sealed record FolderDetailDto(
    FolderDto Folder, IReadOnlyList<FolderRefDto> Ancestors, IReadOnlyDictionary<string, JsonElement> EffectiveMetadata, int ChildCount);

public sealed record VocabularyDto(
    Guid Id, string Key, string Name, string Description, bool Hierarchical, IReadOnlyList<string> Levels, int TermCount);

public sealed record TermDto(
    Guid Id, Guid VocabularyId, Guid? ParentId, string Code, string Path, IReadOnlyDictionary<string, string> Labels,
    int SortOrder, string Status, IReadOnlyDictionary<string, JsonElement> Attributes);

public sealed record TermNodeDto(TermDto Term, IReadOnlyList<TermNodeDto> Children);

public sealed record FieldDto(
    string Name, string Label, string Type, bool Required, bool Multi, string? Vocabulary, IReadOnlyList<string>? Options,
    decimal? Min, decimal? Max, bool WholeNumbers, int? MaxLength, bool Searchable, bool Facet);

public sealed record SchemaDto(
    string AssetType, int Version, bool IsCurrent, IReadOnlyList<FieldDto> Fields, string ChangeNote, DateTimeOffset CreatedAt);

public sealed record SchemaSummaryDto(string AssetType, int CurrentVersion, int FieldCount);

/// <summary>Result of saving a schema. When nothing changed, no new version is created.</summary>
public sealed record SchemaUpdateResultDto(
    SchemaDto Schema, bool NewVersion, IReadOnlyList<string> Added, IReadOnlyList<string> Removed, IReadOnlyList<string> Changed);

public sealed record ValidationResultDto(bool Valid, int SchemaVersion);

public sealed record TemplateDto(string Id, string Name, string Description, IReadOnlyList<string> Requires,
    int Vocabularies, int Terms, int Schemas, int Folders);

public sealed record TemplateApplyResultDto(
    IReadOnlyList<string> Applied, int VocabulariesCreated, int TermsCreated, int SchemasCreated, int SchemasExtended, int FoldersCreated);
