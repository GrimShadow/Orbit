using System.Text.Json;
using Dam.Domain.Content;

namespace Dam.UnitTests;

public sealed class PathTests
{
    [Theory]
    [InlineData("Roadster Hero Shots!", "roadster_hero_shots")]
    [InlineData("  Brand / Campaign 2026 ", "brand_campaign_2026")]
    [InlineData("Café Menü", "cafe_menu")]
    [InlineData("!!!", "item")]
    [InlineData("मानक", "item")]
    [InlineData("A--B__C", "a_b_c")]
    public void Names_become_valid_labels(string name, string expected)
    {
        Assert.Equal(expected, Paths.Slugify(name));
        Assert.True(Paths.IsValidLabel(Paths.Slugify(name)));
    }

    [Fact]
    public void Long_names_are_cut_to_the_label_limit()
    {
        var label = Paths.Slugify(new string('a', 200));
        Assert.Equal(Paths.MaxLabelLength, label.Length);
        Assert.True(Paths.IsValidLabel(label));
    }

    [Theory]
    [InlineData("brand", true)]
    [InlineData("brand.roadster.stills", true)]
    [InlineData("brand..roadster", false)]
    [InlineData("Brand", false)]
    [InlineData("brand.road ster", false)]
    [InlineData("brand.roadster;", false)]
    [InlineData("", false)]
    [InlineData("a.b.c.d.e.f.g.h.i.j.k.l.m", false)] // deeper than the maximum
    public void Path_syntax(string path, bool valid) => Assert.Equal(valid, Paths.IsValidPath(path));

    [Fact]
    public void Sibling_labels_are_made_unique()
    {
        var taken = new HashSet<string> { "reports", "reports_2" };
        Assert.Equal("reports_3", Paths.Unique("reports", taken));
        Assert.Equal("news", Paths.Unique("news", taken));
    }

    [Theory]
    [InlineData("a.b", "a.b", true)]
    [InlineData("a.b", "a.b.c", true)]
    [InlineData("a.b", "a.bc", false)]
    [InlineData("a.b", "a", false)]
    public void Subtree_membership_uses_label_boundaries(string ancestor, string path, bool expected) =>
        Assert.Equal(expected, Paths.IsSelfOrDescendant(ancestor, path));

    [Fact]
    public void Moving_a_folder_rewrites_its_own_and_descendant_paths()
    {
        var tenant = Guid.NewGuid();
        var brand = Folder.Create(tenant, null, "Brand", "brand", null, null, DateTimeOffset.UtcNow);
        var stills = Folder.Create(tenant, brand, "Stills", "stills", null, null, DateTimeOffset.UtcNow);
        var hero = Folder.Create(tenant, stills, "Hero", "hero", null, null, DateTimeOffset.UtcNow);
        var archive = Folder.Create(tenant, null, "Archive", "archive", null, null, DateTimeOffset.UtcNow);

        var old = stills.Path;
        stills.MoveTo(archive);
        hero.RewritePath(old, stills.Path);

        Assert.Equal("archive.stills", stills.Path);
        Assert.Equal("archive.stills.hero", hero.Path);
        Assert.Equal(archive.Id, stills.ParentId);
    }
}

public sealed class SchemaDefinitionTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static FieldDefinition F(string name, FieldType type = FieldType.Text, Action<FieldDefinition>? _ = null) =>
        new() { Name = name, Label = name, Type = type };

    [Fact]
    public void A_sound_schema_has_no_errors()
    {
        var fields = new List<FieldDefinition>
        {
            F("description", FieldType.MultilingualText), F("year", FieldType.Number) with { Min = 1990, Max = 2100, WholeNumbers = true },
            F("kind", FieldType.Enum) with { Options = ["a", "b"] }, F("vehicle", FieldType.TermTree) with { Vocabulary = "vehicle", Multi = true },
        };
        Assert.Empty(SchemaDefinitionValidator.Validate(fields));
    }

    [Theory]
    [InlineData("Title")]
    [InlineData("1abc")]
    [InlineData("has space")]
    [InlineData("has-dash")]
    [InlineData("")]
    public void Field_names_must_be_snake_case(string name) =>
        Assert.Contains("fields[0].name", SchemaDefinitionValidator.Validate([F(name)]).Keys);

    [Theory]
    [InlineData("title")]
    [InlineData("ai_suggestions")]
    [InlineData("status")]
    public void Reserved_names_are_refused(string name) =>
        Assert.Contains("fields[0].name", SchemaDefinitionValidator.Validate([F(name)]).Keys);

    [Fact]
    public void Duplicate_names_and_bad_options_are_reported()
    {
        var e = SchemaDefinitionValidator.Validate([F("a"), F("a"), F("b", FieldType.Enum), F("c", FieldType.Enum) with { Options = ["x", "x"] }]);
        Assert.Contains("fields[1].name", e.Keys);
        Assert.Contains("fields[2].options", e.Keys);
        Assert.Contains("fields[3].options", e.Keys);
    }

    [Fact]
    public void Vocabulary_fields_need_a_vocabulary_key_and_others_must_not_have_one()
    {
        var e = SchemaDefinitionValidator.Validate([F("a", FieldType.Vocabulary), F("b", FieldType.Text) with { Vocabulary = "x" }, F("c", FieldType.Text) with { Options = ["x"] }]);
        Assert.Contains("fields[0].vocabulary", e.Keys);
        Assert.Contains("fields[1].vocabulary", e.Keys);
        Assert.Contains("fields[2].options", e.Keys);
    }

    [Fact]
    public void Number_limits_only_apply_to_numbers_and_must_be_ordered()
    {
        var e = SchemaDefinitionValidator.Validate([F("a", FieldType.Text) with { Min = 1 }, F("b", FieldType.Number) with { Min = 5, Max = 1 }, F("c", FieldType.Boolean) with { Multi = true }]);
        Assert.Contains("fields[0].min", e.Keys);
        Assert.Contains("fields[1].min", e.Keys);
        Assert.Contains("fields[2].multi", e.Keys);
    }

    [Fact]
    public void Too_many_fields_are_refused()
    {
        var many = Enumerable.Range(0, SchemaDefinitionValidator.MaxFields + 1).Select(i => F($"f{i}")).ToList();
        Assert.Contains("fields", SchemaDefinitionValidator.Validate(many).Keys);
    }

    [Fact]
    public void Field_definitions_round_trip_through_json_with_friendly_type_names()
    {
        var json = JsonSerializer.Serialize(new FieldDefinition { Name = "x", Label = "X", Type = FieldType.MultilingualText }, Web);
        Assert.Contains("\"ml_text\"", json);
        var back = JsonSerializer.Deserialize<FieldDefinition>(json, Web)!;
        Assert.Equal(FieldType.MultilingualText, back.Type);
    }
}

public sealed class MetadataValidatorTests
{
    private static readonly IReadOnlyList<FieldDefinition> Fields =
    [
        new() { Name = "description", Label = "Description", Type = FieldType.MultilingualText, Required = true },
        new() { Name = "credit", Label = "Credit", Type = FieldType.Text, MaxLength = 10 },
        new() { Name = "year", Label = "Year", Type = FieldType.Number, Min = 1990, Max = 2100, WholeNumbers = true },
        new() { Name = "shot_on", Label = "Shot on", Type = FieldType.Date },
        new() { Name = "approved", Label = "Approved", Type = FieldType.Boolean },
        new() { Name = "kind", Label = "Kind", Type = FieldType.Enum, Options = ["still", "motion"] },
        new() { Name = "keywords", Label = "Keywords", Type = FieldType.Text, Multi = true },
        new() { Name = "language", Label = "Language", Type = FieldType.Vocabulary, Vocabulary = "language" },
        new() { Name = "vehicle", Label = "Vehicle", Type = FieldType.TermTree, Vocabulary = "vehicle", Multi = true },
        new() { Name = "related", Label = "Related", Type = FieldType.Reference },
    ];

    private static MetadataCheck Check(string json, IReadOnlySet<string>? langs = null) =>
        MetadataValidator.Check(Fields, JsonDocument.Parse(json).RootElement, langs);

    private const string Valid = """{"description":{"en":"A car"}}""";

    [Fact]
    public void A_minimal_valid_document_passes() => Assert.True(Check(Valid).IsValid);

    [Fact]
    public void A_missing_required_field_gives_a_field_level_error()
    {
        var r = Check("{}");
        Assert.Equal(["This field is required."], r.Errors["metadata.description"]);
    }

    [Theory]
    [InlineData("""{"description":{}}""")]
    [InlineData("""{"description":{"en":"  "}}""")]
    [InlineData("""{"description":null}""")]
    public void Empty_values_count_as_missing(string json) => Assert.Contains("metadata.description", Check(json).Errors.Keys);

    [Fact]
    public void Non_objects_are_refused() => Assert.Contains("metadata", Check("[1]").Errors.Keys);

    [Fact]
    public void Unknown_fields_are_refused_but_ai_suggestions_are_reserved()
    {
        Assert.Contains("metadata.colour", Check("""{"description":{"en":"x"},"colour":"red"}""").Errors.Keys);
        Assert.True(Check("""{"description":{"en":"x"},"ai_suggestions":{"tags":["a"]}}""").IsValid);
    }

    public static IEnumerable<object[]> BadValues() =>
    [
        ["credit", "\"way too long for the field\""],
        ["credit", "5"],
        ["year", "\"2020\""],
        ["year", "2020.5"],
        ["year", "1800"],
        ["year", "2200"],
        ["shot_on", "\"yesterday\""],
        ["shot_on", "20240101"],
        ["approved", "\"yes\""],
        ["kind", "\"panorama\""],
        ["keywords", "\"single\""],
        ["keywords", "[\"a\",\"\"]"],
        ["language", "[\"en\"]"],
        ["language", "\"English!\""],
        ["related", "\"not-a-guid\""],
        ["vehicle", "\"acme\""],
    ];

    [Theory]
    [MemberData(nameof(BadValues))]
    public void Wrong_types_and_out_of_range_values_are_reported_against_the_field(string field, string rawValue)
    {
        var r = Check($$"""{"description":{"en":"x"},"{{field}}":{{rawValue}}}""");
        Assert.Contains("metadata." + field, r.Errors.Keys);
        Assert.Single(r.Errors); // and nothing else
    }

    [Fact]
    public void Good_values_of_every_type_pass_and_vocabulary_codes_are_collected()
    {
        var r = Check("""
            {"description":{"en":"x","hi":"य"},"credit":"Studio","year":2026,"shot_on":"2026-03-04","approved":true,"kind":"still",
             "keywords":["a","b"],"language":"en","vehicle":["acme_roadster","acme_trail"],"related":"0197a000-0000-7000-8000-000000000001"}
            """);
        Assert.True(r.IsValid, string.Join("; ", r.Errors.Select(e => $"{e.Key}: {string.Join(",", e.Value)}")));
        Assert.Equal(2, r.References.Count);
        Assert.Contains(r.References, x => x.Field == "language" && x.VocabularyKey == "language" && x.Codes.SequenceEqual(["en"]));
        Assert.Contains(r.References, x => x.Field == "vehicle" && x.Codes.SequenceEqual(["acme_roadster", "acme_trail"]));
    }

    [Fact]
    public void Duplicate_term_codes_are_refused() =>
        Assert.Contains("metadata.vehicle", Check("""{"description":{"en":"x"},"vehicle":["a","a"]}""").Errors.Keys);

    [Fact]
    public void Multilingual_text_checks_language_codes_and_the_tenants_enabled_languages()
    {
        Assert.Contains("metadata.description", Check("""{"description":{"e n":"x"}}""").Errors.Keys);
        Assert.Contains("metadata.description", Check("""{"description":{"en":5}}""").Errors.Keys);
        Assert.Contains("metadata.description", Check("""{"description":"plain string"}""").Errors.Keys);
        var enabled = new HashSet<string> { "en", "hi" };
        Assert.True(Check("""{"description":{"hi":"य","EN":"x"}}""", enabled).IsValid);
        Assert.Contains("metadata.description", Check("""{"description":{"fr":"x"}}""", enabled).Errors.Keys);
    }

    [Fact]
    public void Several_problems_are_reported_together()
    {
        var r = Check("""{"credit":5,"year":"x","kind":"nope"}""");
        Assert.Equal(["metadata.credit", "metadata.description", "metadata.kind", "metadata.year"], r.Errors.Keys.Order());
    }

    [Fact]
    public void Optional_fields_may_be_omitted_or_empty()
    {
        Assert.True(Check("""{"description":{"en":"x"},"credit":"","keywords":[],"vehicle":null}""").IsValid);
    }
}
