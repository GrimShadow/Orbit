using System.Text.Json;
using System.Text.Json.Serialization;
using Dam.Domain.Common;

namespace Dam.Domain.Content;

public static class AssetTypes
{
    public static readonly IReadOnlyList<string> All = ["image", "video", "audio", "document", "manual", "3d", "html5", "other"];
    public static bool IsKnown(string? type) => type is not null && All.Contains(type);
}

[JsonConverter(typeof(JsonStringEnumConverter<FieldType>))]
public enum FieldType
{
    [JsonStringEnumMemberName("text")] Text,
    /// <summary>Text per language: { "en": "…", "hi": "…" }.</summary>
    [JsonStringEnumMemberName("ml_text")] MultilingualText,
    [JsonStringEnumMemberName("number")] Number,
    [JsonStringEnumMemberName("date")] Date,
    [JsonStringEnumMemberName("boolean")] Boolean,
    /// <summary>One of a fixed list of values defined on the field itself.</summary>
    [JsonStringEnumMemberName("enum")] Enum,
    /// <summary>A term code from a flat controlled vocabulary.</summary>
    [JsonStringEnumMemberName("vocabulary")] Vocabulary,
    /// <summary>A term code from a hierarchical vocabulary (any level).</summary>
    [JsonStringEnumMemberName("term_tree")] TermTree,
    /// <summary>The id of another entity, e.g. a related asset.</summary>
    [JsonStringEnumMemberName("reference")] Reference,
}

/// <summary>One field of a metadata schema (spec A4.1 MetadataSchema.fields).</summary>
public sealed record FieldDefinition
{
    public string Name { get; init; } = "";
    public string Label { get; init; } = "";
    public FieldType Type { get; init; }
    public bool Required { get; init; }
    /// <summary>Accepts a list of values instead of one (not for ml_text/boolean).</summary>
    public bool Multi { get; init; }
    /// <summary>Key of the vocabulary that supplies the values (vocabulary and term_tree fields).</summary>
    public string? Vocabulary { get; init; }
    /// <summary>Allowed values (enum fields).</summary>
    public List<string>? Options { get; init; }
    public decimal? Min { get; init; }
    public decimal? Max { get; init; }
    public bool WholeNumbers { get; init; }
    public int? MaxLength { get; init; }
    public bool Searchable { get; init; } = true;
    public bool Facet { get; init; }
}

/// <summary>
/// A versioned set of fields for one asset type. Versions are immutable: a change creates the next version, and an
/// asset keeps validating against the version it was written with, so schema changes never invalidate existing assets.
/// </summary>
public sealed class MetadataSchema : TenantEntity
{
    private MetadataSchema() { }

    public string AssetType { get; private set; } = "";
    public int Version { get; private set; }
    public bool IsCurrent { get; private set; }
    public List<FieldDefinition> Fields { get; private set; } = [];
    public string ChangeNote { get; private set; } = "";
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static MetadataSchema Create(Guid tenantId, string assetType, int version, IEnumerable<FieldDefinition> fields,
        string? changeNote, Guid? createdBy, DateTimeOffset now) =>
        new()
        {
            TenantId = tenantId, AssetType = assetType, Version = version, IsCurrent = true, Fields = fields.ToList(),
            ChangeNote = changeNote ?? "", CreatedBy = createdBy, CreatedAt = now,
        };

    public void Retire() => IsCurrent = false;
}

/// <summary>Checks that a schema definition is itself sound, before it is saved.</summary>
public static class SchemaDefinitionValidator
{
    public const int MaxFields = 100;
    /// <summary>Names used by the asset itself or reserved for platform features.</summary>
    public static readonly IReadOnlySet<string> Reserved = new HashSet<string>
        { "id", "title", "status", "type", "folder", "ai_suggestions", "tenant_id", "version" };

    public static Dictionary<string, string[]> Validate(IReadOnlyList<FieldDefinition> fields)
    {
        var errors = new Dictionary<string, List<string>>();
        void Add(string key, string message) { if (!errors.TryGetValue(key, out var l)) errors[key] = l = []; l.Add(message); }

        if (fields.Count > MaxFields) Add("fields", $"At most {MaxFields} fields per schema.");
        var seen = new HashSet<string>();
        for (var i = 0; i < fields.Count; i++)
        {
            var f = fields[i];
            var key = $"fields[{i}]";
            if (!IsFieldName(f.Name)) Add($"{key}.name", "Use lowercase letters, digits and underscores, starting with a letter (max 64).");
            else if (Reserved.Contains(f.Name)) Add($"{key}.name", $"'{f.Name}' is reserved.");
            else if (!seen.Add(f.Name)) Add($"{key}.name", $"Duplicate field name '{f.Name}'.");
            if (string.IsNullOrWhiteSpace(f.Label)) Add($"{key}.label", "A label is required.");
            if (f.Label.Length > 200) Add($"{key}.label", "Label is too long.");

            switch (f.Type)
            {
                case FieldType.Enum when f.Options is not { Count: > 0 }:
                    Add($"{key}.options", "An enum field needs at least one option."); break;
                case FieldType.Enum when f.Options!.Count != f.Options.Distinct().Count() || f.Options.Any(string.IsNullOrWhiteSpace):
                    Add($"{key}.options", "Options must be non-empty and unique."); break;
                case FieldType.Vocabulary or FieldType.TermTree when !Paths.IsValidLabel(f.Vocabulary):
                    Add($"{key}.vocabulary", "Name the vocabulary key that supplies the values."); break;
            }
            if (f.Type is not (FieldType.Enum) && f.Options is not null) Add($"{key}.options", "Options apply to enum fields only.");
            if (f.Type is not (FieldType.Vocabulary or FieldType.TermTree) && f.Vocabulary is not null) Add($"{key}.vocabulary", "Vocabulary applies to vocabulary and term_tree fields only.");
            if (f.Multi && f.Type is FieldType.MultilingualText or FieldType.Boolean) Add($"{key}.multi", "This field type cannot hold several values.");
            if (f.Min > f.Max) Add($"{key}.min", "Min cannot exceed max.");
            if ((f.Min is not null || f.Max is not null || f.WholeNumbers) && f.Type != FieldType.Number) Add($"{key}.min", "Min, max and wholeNumbers apply to number fields only.");
            if (f.MaxLength is <= 0 or > 100_000) Add($"{key}.maxLength", "Max length must be between 1 and 100000.");
        }
        return errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
    }

    private static bool IsFieldName(string? name) =>
        name is { Length: > 0 and <= 64 } && char.IsAsciiLetterLower(name[0]) && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');
}

/// <summary>A vocabulary reference found while validating; resolved against the database by the application layer.</summary>
public sealed record VocabularyReference(string Field, string VocabularyKey, IReadOnlyList<string> Codes);

public sealed record MetadataCheck(Dictionary<string, List<string>> Errors, List<VocabularyReference> References)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Pure, structural validation of a metadata document against a schema's fields. Vocabulary membership needs the
/// database, so those codes are returned as <see cref="VocabularyReference"/>s. Errors are keyed "metadata.&lt;field&gt;".
/// </summary>
public static class MetadataValidator
{
    public const string AiSuggestions = "ai_suggestions"; // reserved key: AI output awaiting an editor's decision
    public const int MaxTextLength = 5_000;

    public static MetadataCheck Check(IReadOnlyList<FieldDefinition> fields, JsonElement metadata, IReadOnlySet<string>? allowedLanguages = null)
    {
        var errors = new Dictionary<string, List<string>>();
        var refs = new List<VocabularyReference>();
        void Add(string field, string message) { var k = "metadata." + field; if (!errors.TryGetValue(k, out var l)) errors[k] = l = []; l.Add(message); }

        if (metadata.ValueKind != JsonValueKind.Object)
        {
            errors["metadata"] = ["Metadata must be a JSON object."];
            return new MetadataCheck(errors, refs);
        }

        var byName = fields.ToDictionary(f => f.Name);
        foreach (var prop in metadata.EnumerateObject())
            if (prop.Name != AiSuggestions && !byName.ContainsKey(prop.Name)) Add(prop.Name, "Unknown field.");

        foreach (var f in fields)
        {
            if (!metadata.TryGetProperty(f.Name, out var value) || IsEmpty(value))
            {
                if (f.Required) Add(f.Name, "This field is required.");
                continue;
            }
            CheckField(f, value, allowedLanguages, Add, refs);
        }
        return new MetadataCheck(errors, refs);
    }

    private static bool IsEmpty(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()),
        JsonValueKind.Array => v.GetArrayLength() == 0,
        JsonValueKind.Object => !v.EnumerateObject().Any(p => !IsEmpty(p.Value)),
        _ => false,
    };

    private static void CheckField(FieldDefinition f, JsonElement value, IReadOnlySet<string>? languages, Action<string, string> add, List<VocabularyReference> refs)
    {
        if (f.Type == FieldType.MultilingualText) { CheckMultilingual(f, value, languages, add); return; }

        IEnumerable<JsonElement> items;
        if (f.Multi)
        {
            if (value.ValueKind != JsonValueKind.Array) { add(f.Name, "Expected a list of values."); return; }
            items = value.EnumerateArray().ToList();
            if (items.Any(IsEmpty)) { add(f.Name, "The list contains an empty value."); return; }
        }
        else
        {
            if (value.ValueKind == JsonValueKind.Array) { add(f.Name, "Expected a single value, not a list."); return; }
            items = [value];
        }

        var codes = new List<string>();
        foreach (var item in items)
        {
            if (!CheckScalar(f, item, add, codes)) return; // one message per field is enough
        }
        if (f.Multi && codes.Count == 0 && f.Type is not (FieldType.Vocabulary or FieldType.TermTree)) { /* nothing more */ }
        if (f.Type is FieldType.Vocabulary or FieldType.TermTree && codes.Count > 0)
        {
            if (codes.Count != codes.Distinct().Count()) { add(f.Name, "The list contains duplicates."); return; }
            refs.Add(new VocabularyReference(f.Name, f.Vocabulary!, codes));
        }
    }

    private static bool CheckScalar(FieldDefinition f, JsonElement v, Action<string, string> add, List<string> codes)
    {
        switch (f.Type)
        {
            case FieldType.Text:
                if (v.ValueKind != JsonValueKind.String) return Fail(add, f, "Expected text.");
                if (v.GetString()!.Length > (f.MaxLength ?? MaxTextLength)) return Fail(add, f, $"Too long (max {f.MaxLength ?? MaxTextLength} characters).");
                return true;
            case FieldType.Number:
                if (v.ValueKind != JsonValueKind.Number || !v.TryGetDecimal(out var n)) return Fail(add, f, "Expected a number.");
                if (f.WholeNumbers && n != decimal.Truncate(n)) return Fail(add, f, "Expected a whole number.");
                if (f.Min is { } min && n < min) return Fail(add, f, $"Must be at least {min}.");
                if (f.Max is { } max && n > max) return Fail(add, f, $"Must be at most {max}.");
                return true;
            case FieldType.Date:
                if (v.ValueKind != JsonValueKind.String || !(DateOnly.TryParseExact(v.GetString(), "yyyy-MM-dd", out _) || DateTimeOffset.TryParse(v.GetString(), out _)))
                    return Fail(add, f, "Expected a date (yyyy-MM-dd) or an ISO 8601 date-time.");
                return true;
            case FieldType.Boolean:
                return v.ValueKind is JsonValueKind.True or JsonValueKind.False || Fail(add, f, "Expected true or false.");
            case FieldType.Enum:
                if (v.ValueKind != JsonValueKind.String || !(f.Options ?? []).Contains(v.GetString()!))
                    return Fail(add, f, $"Must be one of: {string.Join(", ", f.Options ?? [])}.");
                return true;
            case FieldType.Vocabulary or FieldType.TermTree:
                if (v.ValueKind != JsonValueKind.String || !Paths.IsValidLabel(v.GetString())) return Fail(add, f, "Expected a term code.");
                codes.Add(v.GetString()!);
                return true;
            case FieldType.Reference:
                return (v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out _)) || Fail(add, f, "Expected an id (UUID).");
            default:
                return Fail(add, f, "Unsupported field type.");
        }
    }

    private static bool Fail(Action<string, string> add, FieldDefinition f, string message) { add(f.Name, message); return false; }

    private static void CheckMultilingual(FieldDefinition f, JsonElement value, IReadOnlySet<string>? languages, Action<string, string> add)
    {
        if (value.ValueKind != JsonValueKind.Object) { add(f.Name, "Expected an object of language -> text, e.g. {\"en\": \"…\"}."); return; }
        foreach (var p in value.EnumerateObject())
        {
            if (!IsLanguageTag(p.Name)) { add(f.Name, $"'{p.Name}' is not a language code."); return; }
            if (languages is { Count: > 0 } && !languages.Contains(p.Name.ToLowerInvariant())) { add(f.Name, $"Language '{p.Name}' is not enabled for this tenant."); return; }
            if (p.Value.ValueKind != JsonValueKind.String) { add(f.Name, $"Text for '{p.Name}' must be a string."); return; }
            if (p.Value.GetString()!.Length > (f.MaxLength ?? MaxTextLength)) { add(f.Name, $"Text for '{p.Name}' is too long."); return; }
        }
    }

    private static bool IsLanguageTag(string tag) =>
        tag.Length is >= 2 and <= 12 && char.IsAsciiLetter(tag[0]) && tag.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
}
