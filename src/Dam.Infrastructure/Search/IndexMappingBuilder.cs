using Dam.Domain.Authorization;
using Dam.Domain.Content;

namespace Dam.Infrastructure.Search;

/// <summary>
/// Builds a tenant's OpenSearch index body (settings + mapping) from its current metadata schemas (spec Step 1.7:
/// "mapping generated from metadata schema (searchable/facet flags)"). Every asset type shares one index, so fields are
/// merged across schemas by name; a name reused by two asset types with different <see cref="FieldType"/>s keeps
/// whichever schema was seen first (logged) rather than failing the whole build.
/// </summary>
public static class IndexMappingBuilder
{
    /// <summary>Console languages plus a generic fallback for anything else (spec: "multilingual analyzers (en, hi, mr,
    /// ta, te + generic)"). Lucene ships stemmed analyzers for English and Hindi; Marathi, Tamil and Telugu get the
    /// generic tokenizer today — still correctly searchable, just without stemming until an ICU/Indic plugin is added.</summary>
    public static readonly IReadOnlyList<string> Languages = ["en", "hi", "mr", "ta", "te"];

    private static readonly Dictionary<string, string> BuiltInAnalyzerByLanguage = new() { ["en"] = "english", ["hi"] = "hindi" };

    public const string GenericAnalyzer = "dam_text_generic";
    public const string VectorTextField = "vector_text";
    public const string VectorImageField = "vector_image";
    private const int TextVectorDimension = 1024;
    private const int ImageVectorDimension = 512;

    public static string AnalyzerFor(string language) => $"dam_text_{language}";

    /// <summary>The full body for creating a brand-new backing index.</summary>
    public static Dictionary<string, object?> BuildIndexBody(IEnumerable<FieldDefinition> mergedFields) => new()
    {
        ["settings"] = new Dictionary<string, object?>
        {
            ["index"] = new Dictionary<string, object?> { ["knn"] = true },
            ["analysis"] = new Dictionary<string, object?> { ["analyzer"] = BuildAnalyzers() },
        },
        ["mappings"] = BuildMappings(mergedFields),
    };

    /// <summary>Just the "metadata" properties, for merging new fields into a live index (OpenSearch's put-mapping API
    /// accepts new fields; changing an existing field's type is rejected and needs a reindex instead).</summary>
    public static Dictionary<string, object?> BuildMetadataProperties(IEnumerable<FieldDefinition> mergedFields) =>
        mergedFields.ToDictionary(f => f.Name, MapField);

    private static Dictionary<string, object?> BuildAnalyzers()
    {
        var analyzers = new Dictionary<string, object?> { [GenericAnalyzer] = new Dictionary<string, object?> { ["tokenizer"] = "standard", ["filter"] = new[] { "lowercase" } } };
        foreach (var lang in Languages)
            analyzers[AnalyzerFor(lang)] = BuiltInAnalyzerByLanguage.TryGetValue(lang, out var builtIn)
                ? new Dictionary<string, object?> { ["type"] = builtIn }
                : new Dictionary<string, object?> { ["tokenizer"] = "standard", ["filter"] = new[] { "lowercase" } };
        return analyzers;
    }

    private static Dictionary<string, object?> BuildMappings(IEnumerable<FieldDefinition> mergedFields) => new()
    {
        ["dynamic_templates"] = new object?[]
        {
            // Catches a multilingual-text field's sub-keys for any language not in Languages (spec: "+ generic").
            new Dictionary<string, object?>
            {
                ["ml_text_fallback"] = new Dictionary<string, object?>
                {
                    ["path_match"] = "metadata.*.*",
                    ["match_mapping_type"] = "string",
                    ["mapping"] = new Dictionary<string, object?> { ["type"] = "text", ["analyzer"] = GenericAnalyzer },
                },
            },
        },
        ["properties"] = new Dictionary<string, object?>
        {
            ["id"] = Keyword(),
            ["tenant_id"] = Keyword(),
            ["type"] = Keyword(),
            ["status"] = Keyword(),
            ["title"] = TextWithKeyword(),
            ["folder_id"] = Keyword(),
            ["folder_path"] = Keyword(),
            ["owner_id"] = Keyword(),
            ["collection_ids"] = Keyword(),
            ["tags"] = Keyword(),
            ["taxonomy_terms"] = Keyword(),
            ["attributes"] = new Dictionary<string, object?>
            {
                ["properties"] = Dimensions.All.ToDictionary(d => d, _ => Keyword()),
            },
            ["current_version_id"] = Keyword(),
            ["checksum_sha256"] = Keyword(),
            ["publish_at"] = DateField(),
            ["expire_at"] = DateField(),
            ["retention_policy_id"] = Keyword(),
            ["legal_hold"] = new Dictionary<string, object?> { ["type"] = "boolean" },
            ["created_at"] = DateField(),
            ["updated_at"] = DateField(),
            ["metadata"] = new Dictionary<string, object?> { ["properties"] = BuildMetadataProperties(mergedFields) },
            // Nested terms (spec: "nested terms"): one entry per selected vocabulary/term_tree value, resolved to its
            // hierarchy so a facet on `terms.path` or `terms.ancestor_codes` drills down the same way the taxonomy does.
            ["terms"] = new Dictionary<string, object?>
            {
                ["type"] = "nested",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["field"] = Keyword(),
                    ["vocabulary"] = Keyword(),
                    ["code"] = Keyword(),
                    ["path"] = Keyword(),
                    ["ancestor_codes"] = Keyword(),
                    ["label"] = new Dictionary<string, object?> { ["type"] = "text", ["analyzer"] = GenericAnalyzer },
                },
            },
            // k-NN vector placeholders (spec: "text 1024-d, image 512-d — populated in Step 2.1"). Declared now so the
            // mapping shape is stable and semantic search never needs a breaking reindex to show up.
            [VectorTextField] = KnnVector(TextVectorDimension),
            [VectorImageField] = KnnVector(ImageVectorDimension),
        },
    };

    private static object? MapField(FieldDefinition f) => f.Type switch
    {
        FieldType.Text => TextField(f),
        FieldType.MultilingualText => MultilingualTextField(f),
        FieldType.Number => new Dictionary<string, object?> { ["type"] = f.WholeNumbers ? "long" : "double" },
        FieldType.Date => DateField(),
        FieldType.Boolean => new Dictionary<string, object?> { ["type"] = "boolean" },
        // Enum/Vocabulary/TermTree/Reference are all exact-match codes; resolved hierarchy and labels for
        // vocabulary/term_tree fields live in the `terms` nested array instead, keyed by `field`.
        _ => Keyword(),
    };

    private static Dictionary<string, object?> TextField(FieldDefinition f)
    {
        var m = new Dictionary<string, object?> { ["type"] = "text", ["analyzer"] = GenericAnalyzer };
        if (!f.Searchable) m["index"] = false;
        if (f.Facet) m["fields"] = new Dictionary<string, object?> { ["keyword"] = Keyword() };
        return m;
    }

    private static Dictionary<string, object?> MultilingualTextField(FieldDefinition f)
    {
        var props = new Dictionary<string, object?>();
        foreach (var lang in Languages)
        {
            var m = new Dictionary<string, object?> { ["type"] = "text", ["analyzer"] = AnalyzerFor(lang) };
            if (!f.Searchable) m["index"] = false;
            if (f.Facet) m["fields"] = new Dictionary<string, object?> { ["keyword"] = Keyword() };
            props[lang] = m;
        }
        return new Dictionary<string, object?> { ["properties"] = props };
    }

    private static Dictionary<string, object?> Keyword() => new() { ["type"] = "keyword", ["ignore_above"] = 256 };
    private static Dictionary<string, object?> TextWithKeyword() =>
        new() { ["type"] = "text", ["analyzer"] = GenericAnalyzer, ["fields"] = new Dictionary<string, object?> { ["keyword"] = Keyword() } };
    private static Dictionary<string, object?> DateField() => new() { ["type"] = "date", ["format"] = "strict_date_optional_time||yyyy-MM-dd" };
    private static Dictionary<string, object?> KnnVector(int dimension) => new()
    {
        ["type"] = "knn_vector",
        ["dimension"] = dimension,
        ["method"] = new Dictionary<string, object?> { ["name"] = "hnsw", ["space_type"] = "cosinesimil", ["engine"] = "nmslib" },
    };
}
