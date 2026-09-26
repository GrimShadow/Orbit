using System.Text.Json;
using Dam.Domain.Assets;
using Dam.Domain.Content;

namespace Dam.UnitTests;

public sealed class UploadPolicyTests
{
    [Theory]
    [InlineData("image", "photo.JPG", true)]
    [InlineData("image", "photo.png", true)]
    [InlineData("image", "photo.pdf", false)]
    [InlineData("image", "noextension", false)]
    [InlineData("image", "trailingdot.", false)]
    [InlineData("video", "clip.MOV", true)]
    [InlineData("video", "clip.jpg", false)]
    [InlineData("manual", "guide.pdf", true)]
    [InlineData("document", "deck.pptx", true)]
    [InlineData("html5", "banner.zip", true)]
    [InlineData("html5", "banner.html", false)]
    [InlineData("3d", "car.glb", true)]
    [InlineData("other", "anything.xyz", true)]
    [InlineData("other", "noext", false)]
    public void Type_and_extension_must_agree(string type, string file, bool expected) =>
        Assert.Equal(expected, UploadPolicy.IsAllowed(type, file));

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("payload.DLL")]
    [InlineData("run.bat")]
    [InlineData("script.sh")]
    [InlineData("installer.msi")]
    [InlineData("tool.jar")]
    [InlineData("image.png.exe")] // a double extension is judged by the last one
    public void Executables_are_refused_even_as_other(string file)
    {
        Assert.True(UploadPolicy.IsBlocked(file));
        Assert.False(UploadPolicy.IsAllowed("other", file));
        Assert.Null(UploadPolicy.InferType(file));
    }

    [Theory]
    [InlineData("a.jpg", "image")]
    [InlineData("a.mp4", "video")]
    [InlineData("a.wav", "audio")]
    [InlineData("a.docx", "document")]
    [InlineData("a.pdf", "document")]   // a PDF defaults to a document; the user can choose "manual"
    [InlineData("a.glb", "3d")]
    [InlineData("a.zip", "other")]      // could be html5, 3d or a manual: not guessed
    [InlineData("a.weird", "other")]
    public void Bulk_upload_infers_a_type_from_the_extension(string file, string expected) =>
        Assert.Equal(expected, UploadPolicy.InferType(file));

    [Theory]
    [InlineData(@"C:\Users\me\Pictures\holiday.jpg", "holiday.jpg")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("/var/tmp/x.png", "x.png")]
    [InlineData("  spaced name.png  ", "spaced name.png")]
    [InlineData("evil\u0000name\u0007.png", "evilname.png")]
    public void File_names_lose_paths_and_control_characters(string given, string expected) =>
        Assert.Equal(expected, UploadPolicy.CleanFileName(given));

    [Fact]
    public void Very_long_names_are_cut_to_255_characters() =>
        Assert.Equal(255, UploadPolicy.CleanFileName(new string('a', 400) + ".jpg").Length);

    [Fact]
    public void Size_limits_are_per_type_and_never_above_the_overall_ceiling()
    {
        foreach (var t in AssetTypes.All) Assert.InRange(UploadPolicy.MaxSize(t), 1, UploadPolicy.MaxFileBytes);
        Assert.Equal(UploadPolicy.MaxFileBytes, UploadPolicy.MaxSize("video")); // 20 GB
        Assert.True(UploadPolicy.MaxSize("image") < UploadPolicy.MaxSize("video"));
    }
}

public sealed class UploadMathTests
{
    [Theory]
    [InlineData(1L, 16, 1)]
    [InlineData(16L, 16, 1)]
    [InlineData(17L, 16, 2)]
    [InlineData(160L, 16, 10)]
    public void Parts_round_up(long size, int partSize, int expected) => Assert.Equal(expected, Upload.PartsFor(size, partSize));

    [Fact]
    public void A_20_GB_file_at_16_MB_parts_stays_inside_the_10000_part_limit() =>
        Assert.True(Upload.PartsFor(20L * 1024 * 1024 * 1024, 16 * 1024 * 1024) <= 10_000);

    [Fact]
    public void Storage_keys_come_from_ids_only_never_from_the_client()
    {
        var (t, a, u) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var key = Upload.KeyFor(t, a, u);
        Assert.Equal($"staging/{t:N}/{a:N}/{u:N}", key);
        Assert.DoesNotContain("..", key);
    }

    [Fact]
    public void An_upload_moves_through_its_states()
    {
        var u = Upload.Start(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "a.jpg", 10, "image/jpeg", "b", "s3id", 5, false, null, DateTimeOffset.UtcNow, TimeSpan.FromHours(1));
        Assert.True(u.IsOpen);
        Assert.Equal(2, u.PartCount);
        u.MarkProcessing(DateTimeOffset.UtcNow);
        Assert.False(u.IsOpen);
        u.MarkReady(new string('a', 64), 10);
        Assert.Equal(UploadStatus.Ready, u.Status);
    }
}

public sealed class MetadataProjectionTests
{
    private static readonly IReadOnlyList<FieldDefinition> Fields =
    [
        new() { Name = "regions", Label = "Regions", Type = FieldType.TermTree, Vocabulary = "region", Multi = true, AccessDimension = "region" },
        new() { Name = "channels", Label = "Channels", Type = FieldType.Vocabulary, Vocabulary = "channel", Multi = true, AccessDimension = "channel" },
        new() { Name = "vehicle", Label = "Vehicle", Type = FieldType.TermTree, Vocabulary = "vehicle", Multi = true },
        new() { Name = "campaign", Label = "Campaign", Type = FieldType.Vocabulary, Vocabulary = "campaign" },
        new() { Name = "credit", Label = "Credit", Type = FieldType.Text },
    ];

    private static Dictionary<string, JsonElement> Meta(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    [Fact]
    public void Term_codes_are_collected_and_only_mapped_fields_become_access_attributes()
    {
        var p = MetadataProjector.Project(Fields, Meta("""
            {"regions":["north","global"],"channels":["signage"],"vehicle":["acme_trail"],"campaign":"spring_launch","credit":"HQ"}
            """));
        Assert.Equal(["acme_trail", "global", "north", "signage", "spring_launch"], p.TaxonomyTerms);
        Assert.Equal(["global", "north"], p.Attributes["region"]);
        Assert.Equal(["signage"], p.Attributes["channel"]);
        Assert.False(p.Attributes.ContainsKey("brand")); // not mapped
        Assert.DoesNotContain("HQ", p.TaxonomyTerms);    // text is not a term
    }

    [Fact]
    public void Missing_or_empty_metadata_projects_to_nothing()
    {
        var p = MetadataProjector.Project(Fields, Meta("{}"));
        Assert.Empty(p.TaxonomyTerms);
        Assert.Empty(p.Attributes);
    }

    [Fact]
    public void Two_fields_mapped_to_one_dimension_are_merged_without_duplicates()
    {
        var fields = new List<FieldDefinition>
        {
            new() { Name = "a", Label = "A", Type = FieldType.Vocabulary, Vocabulary = "v", AccessDimension = "brand" },
            new() { Name = "b", Label = "B", Type = FieldType.Vocabulary, Vocabulary = "v", AccessDimension = "brand" },
        };
        var p = MetadataProjector.Project(fields, Meta("""{"a":"x","b":"x"}"""));
        Assert.Equal(["x"], p.Attributes["brand"]);
    }

    [Fact]
    public void The_access_dimension_must_be_a_known_dimension_on_a_vocabulary_field()
    {
        var bad = SchemaDefinitionValidator.Validate([
            new FieldDefinition { Name = "a", Label = "A", Type = FieldType.Vocabulary, Vocabulary = "v", AccessDimension = "planet" },
            new FieldDefinition { Name = "b", Label = "B", Type = FieldType.Text, AccessDimension = "region" },
        ]);
        Assert.Contains("fields[0].accessDimension", bad.Keys);
        Assert.Contains("fields[1].accessDimension", bad.Keys);
        Assert.Empty(SchemaDefinitionValidator.Validate([new FieldDefinition { Name = "a", Label = "A", Type = FieldType.TermTree, Vocabulary = "v", AccessDimension = "region" }]));
    }

    [Fact]
    public void Required_fields_can_be_waived_while_an_asset_is_being_described()
    {
        var fields = new List<FieldDefinition> { new() { Name = "credit", Label = "Credit", Type = FieldType.Text, Required = true } };
        var doc = JsonDocument.Parse("{}").RootElement;
        Assert.False(MetadataValidator.Check(fields, doc).IsValid);
        Assert.True(MetadataValidator.Check(fields, doc, requireFields: false).IsValid);
        // ...but wrong values are still wrong
        Assert.False(MetadataValidator.Check(fields, JsonDocument.Parse("""{"credit":5}""").RootElement, requireFields: false).IsValid);
    }

    [Fact]
    public void The_shipped_packs_map_channels_and_regions_to_access_dimensions()
    {
        var core = Dam.Application.Content.TemplateCatalog.All["core"].Schemas!.First(s => s.AssetType == "image").Fields;
        Assert.Equal("channel", core.First(f => f.Name == "channels").AccessDimension);
        Assert.Equal("region", core.First(f => f.Name == "regions").AccessDimension);
    }
}
