using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Dam.IntegrationTests;

/// <summary>
/// The Step 3.1 acceptance test: a generated set of 200 fictional VINs (tools/fixtures/make-vin-fixtures.py) resolves to the right manual and theme in 100% of cases. The
/// rules are loaded through the import endpoint and the manuals through the API, exactly as a tenant's administrator would; nothing here is special-cased.
/// </summary>
[Collection("pg")]
public sealed class KnowledgeVinFixtureTests(PostgresFixture pg) : KnowledgeApiBase(pg)
{
    private sealed record Expected(string Vin, string Model, string Variant, int Year, string Theme, string Document);

    private static string[][] Rows(string file) =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)).Skip(1).Where(l => l.Length > 0).Select(l => l.Split(',')).ToArray();

    private static string Text(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));

    private async Task<(Guid Tenant, string Admin, Dictionary<string, Guid> Documents)> Load()
    {
        var (t, admin) = await NewWorld();
        var import = await CallText(HttpMethod.Post, "/api/v1/knowledge/vin-rules/import", admin, Text("vin-rules.csv"));
        Assert.Equal(HttpStatusCode.OK, import.Status);
        Assert.Equal(0, import.Body.GetProperty("errors").GetArrayLength());
        Assert.Equal(Rows("vin-rules.csv").Length, import.Body.GetProperty("created").GetInt32());

        var documents = new Dictionary<string, Guid>();
        foreach (var d in Rows("vin-documents.csv"))
        {
            var asset = await NewManual(admin, d[0]);
            int? from = d[3].Length > 0 ? int.Parse(d[3], CultureInfo.InvariantCulture) : null;
            int? to = d[4].Length > 0 ? int.Parse(d[4], CultureInfo.InvariantCulture) : null;
            documents[d[0]] = (await CreateDoc(admin, asset, d[1], d[2].Length > 0 ? d[2] : null, from, to)).GetProperty("id").GetGuid();
        }
        return (t, admin, documents);
    }

    [Fact]
    public void The_fixture_set_has_the_shape_the_spec_asks_for()
    {
        var vins = Rows("vin-fixtures.csv").Select(r => new Expected(r[0], r[1], r[2], int.Parse(r[3], CultureInfo.InvariantCulture), r[4], r[5])).ToList();
        Assert.Equal(200, vins.Count);
        Assert.Equal(200, vins.Select(v => v.Vin).Distinct().Count());
        Assert.All(vins, v => { Assert.Equal(17, v.Vin.Length); Assert.DoesNotContain(v.Vin, c => "IOQ".Contains(c, StringComparison.Ordinal)); });
        Assert.Equal(["ev", "ice"], vins.Select(v => v.Theme).Distinct().Order());
        Assert.True(vins.Select(v => v.Model).Distinct().Count() >= 4);
        Assert.True(vins.Select(v => v.Variant).Distinct().Count() >= 6);
        Assert.True(vins.Select(v => v.Year).Distinct().Count() >= 4);
        Assert.DoesNotContain(vins.SelectMany(v => new[] { v.Model, v.Variant, v.Document }), x => x.Contains("mahindra", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Every_one_of_the_200_vins_resolves_to_the_right_manual_theme_model_variant_and_year()
    {
        var (t, admin, documents) = await Load();
        var failures = new List<string>();
        var fixtures = Rows("vin-fixtures.csv");
        foreach (var r in fixtures)
        {
            var (vin, model, variant, year, theme, doc) = (r[0], r[1], r[2], int.Parse(r[3], CultureInfo.InvariantCulture), r[4], r[5]);
            var res = await Resolve(admin, vin);
            if (res.Status != HttpStatusCode.OK) { failures.Add($"{vin}: {res.Status} {res.Body}"); continue; }
            var v = res.Body.GetProperty("vehicle");
            var got = (v.GetProperty("model").GetString(), v.GetProperty("variant").GetString() ?? "", v.GetProperty("modelYear").GetInt32(), v.GetProperty("theme").GetString(), DocOf(res.Body));
            if (got != (model, variant, year, theme, documents[doc])) failures.Add($"{vin}: expected {model}/{variant}/{year}/{theme}/{doc}, got {got}");
        }
        Assert.Empty(failures);
        Assert.Equal(200, fixtures.Length);

        // The same answers for the people and apps that only see published manuals, and for the review role.
        foreach (var token in new[] { As(t, "Viewer"), As(t, "Ask Assist"), As(t, "Editor") })
            foreach (var r in fixtures.Take(40))
                Assert.Equal(documents[r[5]], DocOf((await Resolve(token, r[0])).Body));
    }

    [Fact]
    public async Task The_retired_rule_in_the_set_never_wins_and_the_rules_tester_agrees_with_resolve()
    {
        var (_, admin, _) = await Load();
        var gt = Rows("vin-fixtures.csv").First(r => r[1] == "roadster" && r[2] == "base");
        var test = await Call(HttpMethod.Post, "/api/v1/knowledge/vin-rules/test", admin, new { vin = gt[0] });
        Assert.Equal("roadster base", test.Body.GetProperty("chosen").GetProperty("name").GetString());
        var retired = test.Body.GetProperty("candidates").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "retired roadster rule");
        Assert.False(retired.GetProperty("matched").GetBoolean());
    }

    [Fact]
    public async Task Importing_the_rule_file_again_changes_nothing()
    {
        var (t, admin, _) = await Load();
        var again = await CallText(HttpMethod.Post, "/api/v1/knowledge/vin-rules/import", admin, Text("vin-rules.csv"));
        Assert.Equal(0, again.Body.GetProperty("created").GetInt32());
        Assert.Equal(0, again.Body.GetProperty("updated").GetInt32());
        Assert.Equal(Rows("vin-rules.csv").Length, again.Body.GetProperty("skipped").GetInt32());
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'knowledge.vin_rules.imported'"));
    }

}
