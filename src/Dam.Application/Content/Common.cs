using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Content;
using Dam.Shared.Content;

namespace Dam.Application.Content;

internal static class ContentMapping
{
    public static FolderDto ToDto(Folder f) =>
        new(f.Id, f.ParentId, f.Path, f.Label, f.Name, f.InheritedMetadata, f.DefaultWorkflowId, f.CreatedAt);

    public static TermDto ToDto(Term t) =>
        new(t.Id, t.VocabularyId, t.ParentId, t.Code, t.Path, t.Labels, t.SortOrder, t.Status.ToString().ToLowerInvariant(), t.Attributes);

    public static FieldDto ToDto(FieldDefinition f) =>
        new(f.Name, f.Label, ToWire(f.Type), f.Required, f.Multi, f.Vocabulary, f.Options, f.Min, f.Max, f.WholeNumbers, f.MaxLength, f.Searchable, f.Facet);

    public static FieldDefinition FromDto(FieldDto f) =>
        new()
        {
            Name = f.Name, Label = f.Label, Type = FromWire(f.Type), Required = f.Required, Multi = f.Multi, Vocabulary = f.Vocabulary,
            Options = f.Options?.ToList(), Min = f.Min, Max = f.Max, WholeNumbers = f.WholeNumbers, MaxLength = f.MaxLength,
            Searchable = f.Searchable, Facet = f.Facet,
        };

    public static SchemaDto ToDto(MetadataSchema s) =>
        new(s.AssetType, s.Version, s.IsCurrent, s.Fields.Select(ToDto).ToList(), s.ChangeNote, s.CreatedAt);

    private static readonly Dictionary<string, FieldType> Wire = new()
    {
        ["text"] = FieldType.Text, ["ml_text"] = FieldType.MultilingualText, ["number"] = FieldType.Number, ["date"] = FieldType.Date,
        ["boolean"] = FieldType.Boolean, ["enum"] = FieldType.Enum, ["vocabulary"] = FieldType.Vocabulary,
        ["term_tree"] = FieldType.TermTree, ["reference"] = FieldType.Reference,
    };

    public static string ToWire(FieldType t) => Wire.First(kv => kv.Value == t).Key;
    public static bool TryFromWire(string? t, out FieldType type) { type = default; return t is not null && Wire.TryGetValue(t, out type); }
    public static FieldType FromWire(string t) => Wire[t];
    public static IReadOnlyCollection<string> WireNames => Wire.Keys;

    public static Dictionary<string, string[]> Fail(string field, string message) => new() { [field] = [message] };

    /// <summary>Turns a JSON object into a dictionary; returns an error message when it is not an acceptable object.</summary>
    public static bool TryObject(JsonElement? e, out Dictionary<string, JsonElement> dict, out string? error)
    {
        dict = [];
        error = null;
        if (e is null || e.Value.ValueKind == JsonValueKind.Null) return true;
        if (e.Value.ValueKind != JsonValueKind.Object) { error = "Must be a JSON object."; return false; }
        if (e.Value.GetRawText().Length > 32_000) { error = "Too large (max 32 KB)."; return false; }
        foreach (var p in e.Value.EnumerateObject())
        {
            if (p.Name.Length is 0 or > 64) { error = "Keys must be 1-64 characters."; return false; }
            dict[p.Name] = p.Value.Clone();
        }
        return true;
    }

    public static Dictionary<string, string[]>? LabelErrors(Dictionary<string, string>? labels, string field = "labels")
    {
        if (labels is null || labels.Count == 0) return Fail(field, "At least one label is required.");
        if (labels.Count > 30) return Fail(field, "At most 30 languages.");
        foreach (var (lang, text) in labels)
        {
            if (lang.Length is < 2 or > 12 || !lang.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return Fail(field, $"'{lang}' is not a language code.");
            if (string.IsNullOrWhiteSpace(text) || text.Length > 200) return Fail(field, $"Label for '{lang}' must be 1-200 characters.");
        }
        return null;
    }
}

internal static class TreeBuilder
{
    /// <summary>Nests items under their parents. Items whose parent is not in the list become roots.</summary>
    public static List<TNode> Build<TItem, TNode>(IReadOnlyList<TItem> items, Func<TItem, Guid> id, Func<TItem, Guid?> parent,
        Func<TItem, IReadOnlyList<TNode>, TNode> node, Comparison<TItem>? order = null)
    {
        var ids = items.Select(id).ToHashSet();
        var byParent = items.Where(i => parent(i) is { } p && ids.Contains(p)).GroupBy(i => parent(i)!.Value).ToDictionary(g => g.Key, g => g.ToList());
        List<TNode> Make(IEnumerable<TItem> level)
        {
            var list = level.ToList();
            if (order is not null) list.Sort(order);
            return list.Select(i => node(i, byParent.TryGetValue(id(i), out var kids) ? Make(kids) : [])).ToList();
        }
        return Make(items.Where(i => parent(i) is not { } p || !ids.Contains(p)));
    }
}

/// <summary>Scoped permission check against a folder, so access rules assigned to a subtree work for structure edits too.</summary>
internal static class FolderGuard
{
    public static async Task<bool> CanAsync(IAuthorizationService authz, string permission, string? folderPath, CancellationToken ct) =>
        await authz.CanAsync(permission, new ResourceContext { Type = "folder", FolderPath = folderPath }, ct);
}
