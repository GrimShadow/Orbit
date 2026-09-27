using Dam.Domain.Content;
using Dam.Infrastructure.Search;

namespace Dam.UnitTests;

public sealed class SearchMappingTests
{
    private static Dictionary<string, object?> Properties(IEnumerable<FieldDefinition> fields) =>
        (Dictionary<string, object?>)((Dictionary<string, object?>)IndexMappingBuilder.BuildIndexBody(fields)["mappings"]!)["properties"]!;

    private static Dictionary<string, object?> MetadataProperties(IEnumerable<FieldDefinition> fields) =>
        (Dictionary<string, object?>)((Dictionary<string, object?>)Properties(fields)["metadata"]!)["properties"]!;

    private static FieldDefinition Field(string name, FieldType type, bool facet = false, bool searchable = true, bool wholeNumbers = false, string? vocabulary = null) =>
        new() { Name = name, Label = name, Type = type, Searchable = searchable, Facet = facet, WholeNumbers = wholeNumbers, Vocabulary = vocabulary };

    [Fact]
    public void A_plain_text_field_gets_a_keyword_subfield_only_when_marked_facet()
    {
        var withoutFacet = (Dictionary<string, object?>)MetadataProperties([Field("headline", FieldType.Text)])["headline"]!;
        Assert.Equal("text", withoutFacet["type"]);
        Assert.False(withoutFacet.ContainsKey("fields"));

        var withFacet = (Dictionary<string, object?>)MetadataProperties([Field("category", FieldType.Text, facet: true)])["category"]!;
        Assert.True(withFacet.ContainsKey("fields"));
    }

    [Fact]
    public void A_non_searchable_text_field_is_stored_but_not_indexed()
    {
        var field = (Dictionary<string, object?>)MetadataProperties([Field("internal_note", FieldType.Text, searchable: false)])["internal_note"]!;
        Assert.Equal(false, field["index"]);
    }

    [Fact]
    public void A_multilingual_text_field_gets_one_analyzed_subfield_per_console_language()
    {
        var field = (Dictionary<string, object?>)MetadataProperties([Field("description", FieldType.MultilingualText)])["description"]!;
        var props = (Dictionary<string, object?>)field["properties"]!;
        foreach (var lang in IndexMappingBuilder.Languages)
        {
            var sub = (Dictionary<string, object?>)props[lang]!;
            Assert.Equal("text", sub["type"]);
            Assert.Equal(IndexMappingBuilder.AnalyzerFor(lang), sub["analyzer"]);
        }
    }

    [Theory]
    [InlineData(false, "double")]
    [InlineData(true, "long")]
    public void A_number_field_maps_by_whole_numbers(bool wholeNumbers, string expectedType)
    {
        var field = (Dictionary<string, object?>)MetadataProperties([Field("weight", FieldType.Number, wholeNumbers: wholeNumbers)])["weight"]!;
        Assert.Equal(expectedType, field["type"]);
    }

    [Theory]
    [InlineData(FieldType.Enum)]
    [InlineData(FieldType.Vocabulary)]
    [InlineData(FieldType.TermTree)]
    [InlineData(FieldType.Reference)]
    public void Code_like_fields_map_to_keyword(FieldType type)
    {
        var field = (Dictionary<string, object?>)MetadataProperties([Field("f", type)])["f"]!;
        Assert.Equal("keyword", field["type"]);
    }

    [Fact]
    public void A_boolean_field_maps_to_boolean_and_a_date_field_accepts_both_date_formats()
    {
        Assert.Equal("boolean", ((Dictionary<string, object?>)MetadataProperties([Field("live", FieldType.Boolean)])["live"]!)["type"]);
        var date = (Dictionary<string, object?>)MetadataProperties([Field("shot_on", FieldType.Date)])["shot_on"]!;
        Assert.Equal("date", date["type"]);
        Assert.Contains("yyyy-MM-dd", (string)date["format"]!);
    }

    [Fact]
    public void The_fixed_top_level_shape_carries_nested_terms_and_the_two_vector_placeholders()
    {
        var props = Properties([]);
        Assert.Equal("nested", ((Dictionary<string, object?>)props["terms"]!)["type"]);
        var vectorText = (Dictionary<string, object?>)props[IndexMappingBuilder.VectorTextField]!;
        Assert.Equal("knn_vector", vectorText["type"]);
        Assert.Equal(1024, vectorText["dimension"]);
        var vectorImage = (Dictionary<string, object?>)props[IndexMappingBuilder.VectorImageField]!;
        Assert.Equal(512, vectorImage["dimension"]);
    }

    [Fact]
    public void Index_settings_enable_knn_and_declare_one_analyzer_per_language_plus_generic()
    {
        var body = IndexMappingBuilder.BuildIndexBody([]);
        var settings = (Dictionary<string, object?>)body["settings"]!;
        var index = (Dictionary<string, object?>)settings["index"]!;
        Assert.Equal(true, index["knn"]);
        var analyzers = (Dictionary<string, object?>)((Dictionary<string, object?>)settings["analysis"]!)["analyzer"]!;
        Assert.True(analyzers.ContainsKey(IndexMappingBuilder.GenericAnalyzer));
        foreach (var lang in IndexMappingBuilder.Languages) Assert.True(analyzers.ContainsKey(IndexMappingBuilder.AnalyzerFor(lang)));
    }
}
