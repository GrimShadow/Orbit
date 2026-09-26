using System.Text.Json;
using Dam.Domain.Common;

namespace Dam.Domain.Content;

/// <summary>A node in the governance hierarchy. Access rules can be scoped to a folder subtree by its path.</summary>
public sealed class Folder : TenantEntity
{
    private Folder() { }

    public Guid? ParentId { get; private set; }
    public string Path { get; private set; } = "";
    public string Label { get; private set; } = "";
    public string Name { get; private set; } = "";
    /// <summary>Metadata that assets in this folder inherit. Deeper folders override shallower ones, key by key.</summary>
    public Dictionary<string, JsonElement> InheritedMetadata { get; private set; } = [];
    public Guid? DefaultWorkflowId { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static Folder Create(Guid tenantId, Folder? parent, string name, string label,
        Dictionary<string, JsonElement>? inherited, Guid? createdBy, DateTimeOffset now) =>
        new()
        {
            TenantId = tenantId, ParentId = parent?.Id, Path = Paths.Join(parent?.Path, label), Label = label,
            Name = name.Trim(), InheritedMetadata = inherited ?? [], CreatedBy = createdBy, CreatedAt = now,
        };

    public void Rename(string name) => Name = name.Trim();
    public void SetInheritedMetadata(Dictionary<string, JsonElement> metadata) => InheritedMetadata = metadata;
    public void SetDefaultWorkflow(Guid? workflowId) => DefaultWorkflowId = workflowId;

    /// <summary>Re-parents the folder. Descendant paths are rewritten by the caller via <see cref="RewritePath"/>.</summary>
    public void MoveTo(Folder? newParent) { ParentId = newParent?.Id; Path = Paths.Join(newParent?.Path, Label); }

    public void RewritePath(string oldPrefix, string newPrefix) => Path = newPrefix + Path[oldPrefix.Length..];
}

public sealed class Vocabulary : TenantEntity
{
    private Vocabulary() { }

    public string Key { get; private set; } = "";
    public string Name { get; private set; } = "";
    public string Description { get; private set; } = "";
    public bool Hierarchical { get; private set; }
    /// <summary>Names of the hierarchy levels, top first (e.g. Brand, Model, Variant, Model year). Display only.</summary>
    public List<string> Levels { get; private set; } = [];
    public DateTimeOffset CreatedAt { get; private set; }

    public static Vocabulary Create(Guid tenantId, string key, string name, string? description, bool hierarchical,
        IEnumerable<string>? levels, DateTimeOffset now) =>
        new()
        {
            TenantId = tenantId, Key = key, Name = name.Trim(), Description = description ?? "", Hierarchical = hierarchical,
            Levels = (levels ?? []).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToList(), CreatedAt = now,
        };

    public void Update(string? name, string? description, IEnumerable<string>? levels)
    {
        if (!string.IsNullOrWhiteSpace(name)) Name = name.Trim();
        if (description is not null) Description = description;
        if (levels is not null) Levels = levels.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToList();
    }
}

public enum TermStatus { Active, Deprecated }

/// <summary>
/// A controlled value. Metadata stores the immutable <see cref="Code"/>, so renaming a label never breaks assets, and
/// codes are portable between environments and tenants. Labels are per language.
/// </summary>
public sealed class Term : TenantEntity
{
    private Term() { }

    public Guid VocabularyId { get; private set; }
    public Guid? ParentId { get; private set; }
    public string Code { get; private set; } = "";
    public string Path { get; private set; } = "";
    public Dictionary<string, string> Labels { get; private set; } = [];
    public int SortOrder { get; private set; }
    public TermStatus Status { get; private set; } = TermStatus.Active;
    /// <summary>Free-form facts about the term (e.g. a model year, an EV flag).</summary>
    public Dictionary<string, JsonElement> Attributes { get; private set; } = [];
    public DateTimeOffset CreatedAt { get; private set; }

    public static Term Create(Guid tenantId, Guid vocabularyId, Term? parent, string code, Dictionary<string, string> labels,
        int sortOrder, Dictionary<string, JsonElement>? attributes, DateTimeOffset now) =>
        new()
        {
            TenantId = tenantId, VocabularyId = vocabularyId, ParentId = parent?.Id, Code = code, Path = Paths.Join(parent?.Path, code),
            Labels = labels, SortOrder = sortOrder, Attributes = attributes ?? [], CreatedAt = now,
        };

    public void Update(Dictionary<string, string>? labels, int? sortOrder, TermStatus? status, Dictionary<string, JsonElement>? attributes)
    {
        if (labels is not null) Labels = labels;
        if (sortOrder is not null) SortOrder = sortOrder.Value;
        if (status is not null) Status = status.Value;
        if (attributes is not null) Attributes = attributes;
    }

    public void MoveTo(Term? newParent) { ParentId = newParent?.Id; Path = Paths.Join(newParent?.Path, Code); }
    public void RewritePath(string oldPrefix, string newPrefix) => Path = newPrefix + Path[oldPrefix.Length..];

    /// <summary>Best label for a language, falling back to English, then any label, then the code.</summary>
    public string LabelFor(string language) =>
        Labels.GetValueOrDefault(language) ?? Labels.GetValueOrDefault("en") ?? Labels.Values.FirstOrDefault() ?? Code;
}
