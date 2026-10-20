using System.Net;
using System.Text.Json;

namespace Dam.IntegrationTests;

/// <summary>"Which document describes this vehicle?" (ADR-0039 decision 5): the rule, the ranking, languages, and what each role is allowed to see.</summary>
[Collection("pg")]
public sealed class KnowledgeResolveTests(PostgresFixture pg) : KnowledgeApiBase(pg)
{
    private static string Code(JsonElement problem) => problem.GetProperty("code").GetString()!;

    [Fact]
    public async Task A_vin_resolves_to_the_published_manual_with_the_rule_the_vehicle_and_the_asset_version()
    {
        var (_, admin) = await NewWorld();
        var asset = await NewManual(admin, "Roadster GT manual");
        var doc = (await CreateDoc(admin, asset, "roadster", "gt", 2023, 2025, theme: "ice")).GetProperty("id").GetGuid();
        var rule = (await CreateRule(admin, "roadster gt", "ACMRS1*", "roadster", "gt", priority: 5)).GetProperty("id").GetGuid();

        var r = await Resolve(admin, " acmrs1ab4ra000123 ");
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal(Vin, r.Body.GetProperty("vin").GetString());                          // normalised
        Assert.Equal(rule, r.Body.GetProperty("rule").GetProperty("id").GetGuid());
        Assert.Equal("roadster gt", r.Body.GetProperty("rule").GetProperty("name").GetString());
        Assert.Equal(5, r.Body.GetProperty("rule").GetProperty("priority").GetInt32());
        var v = r.Body.GetProperty("vehicle");
        Assert.Equal("roadster", v.GetProperty("model").GetString());
        Assert.Equal("gt", v.GetProperty("variant").GetString());
        Assert.Equal(2024, v.GetProperty("modelYear").GetInt32());                         // the tenth character, R
        Assert.Equal("ice", v.GetProperty("theme").GetString());                           // no theme on the rule: the document's
        var d = r.Body.GetProperty("document");
        Assert.Equal(doc, d.GetProperty("id").GetGuid());
        Assert.Equal(asset, d.GetProperty("assetId").GetGuid());
        Assert.Equal("Roadster GT manual", d.GetProperty("title").GetString());
        Assert.Equal("published", d.GetProperty("assetStatus").GetString());
        Assert.Equal(0, d.GetProperty("chapters").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, d.GetProperty("versionNumber").ValueKind);        // a shell has no file yet
        Assert.Equal(0, r.Body.GetProperty("otherLanguages").GetArrayLength());
    }

    [Fact]
    public async Task The_rule_theme_wins_over_the_documents_and_no_theme_at_all_is_null()
    {
        var (_, admin) = await NewWorld();
        await CreateDoc(admin, await NewManual(admin), "roadster", theme: "ice");
        var rule = (await CreateRule(admin, "themed", theme: "ev")).GetProperty("id").GetGuid();
        Assert.Equal("ev", (await Resolve(admin)).Body.GetProperty("vehicle").GetProperty("theme").GetString());
        await Call(HttpMethod.Put, $"/api/v1/knowledge/vin-rules/{rule}", admin, new { name = "themed", pattern = "ACM*", model = "roadster" });
        Assert.Equal("ice", (await Resolve(admin)).Body.GetProperty("vehicle").GetProperty("theme").GetString());
        var (_, admin2) = await NewWorld();
        await CreateDoc(admin2, await NewManual(admin2), "roadster");
        await CreateRule(admin2);
        Assert.Equal(JsonValueKind.Null, (await Resolve(admin2)).Body.GetProperty("vehicle").GetProperty("theme").ValueKind);
    }

    // ---- errors -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("TOO-SHORT")]
    [InlineData("ACMRS1AB4RA00012")]
    [InlineData("ACMRS1AB4RA0001234")]
    [InlineData("ACMRS1AB4RA00012I")]
    [InlineData("ACMRS1AB4RA00012O")]
    [InlineData("ACMRS1AB4RA00012Q")]
    [InlineData("ACMRS1AB4RA 00123")]
    public async Task A_malformed_vin_is_a_422_with_a_stable_code_and_a_reason(string? vin)
    {
        var (_, admin) = await NewWorld();
        await CreateRule(admin);
        var r = await Resolve(admin, vin);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status);
        Assert.Equal("knowledge.invalid_vin", Code(r.Body));
        Assert.NotEmpty(r.Body.GetProperty("errors").GetProperty("vin")[0].GetString()!);
    }

    [Fact]
    public async Task An_unknown_vin_is_a_404_with_the_no_rule_code_and_a_matched_vin_without_a_document_is_a_404_with_the_no_document_code_and_the_vehicle()
    {
        var (_, admin) = await NewWorld();
        var none = await Resolve(admin);
        Assert.Equal(HttpStatusCode.NotFound, none.Status);
        Assert.Equal("knowledge.no_rule", Code(none.Body));
        Assert.Equal("No rule matches this VIN.", none.Body.GetProperty("title").GetString());

        await CreateRule(admin, "gt", "ACMRS1*", "roadster", "gt", theme: "ice");
        var unmatched = await Resolve(admin, "XYZRS1AB4RA000123");
        Assert.Equal("knowledge.no_rule", Code(unmatched.Body));

        var noDoc = await Resolve(admin);
        Assert.Equal(HttpStatusCode.NotFound, noDoc.Status);
        Assert.Equal("knowledge.no_document", Code(noDoc.Body));
        Assert.DoesNotContain("rule", noDoc.Body.GetProperty("title").GetString()!, StringComparison.OrdinalIgnoreCase);
        var vehicle = noDoc.Body.GetProperty("vehicle");
        Assert.Equal("roadster", vehicle.GetProperty("model").GetString());
        Assert.Equal("gt", vehicle.GetProperty("variant").GetString());
        Assert.Equal(2024, vehicle.GetProperty("modelYear").GetInt32());
        Assert.Equal("ice", vehicle.GetProperty("theme").GetString());

        // a document of another type, model or variant does not count
        await CreateDoc(admin, await NewManual(admin), "roadster", "gt", type: "faq");
        await CreateDoc(admin, await NewManual(admin), "spark", "gt");
        await CreateDoc(admin, await NewManual(admin), "roadster", "base");
        Assert.Equal("knowledge.no_document", Code((await Resolve(admin)).Body));
        Assert.Equal(HttpStatusCode.OK, (await Resolve(admin, type: "faq")).Status);
    }

    [Fact]
    public async Task An_unknown_type_is_a_422_and_the_type_defaults_to_the_owner_manual()
    {
        var (_, admin) = await NewWorld();
        await CreateRule(admin);
        await CreateDoc(admin, await NewManual(admin, "FAQ"), "roadster", type: "faq");
        var manual = await CreateDoc(admin, await NewManual(admin, "Manual"), "roadster");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Resolve(admin, type: "leaflet")).Status);
        Assert.Equal(manual.GetProperty("id").GetGuid(), DocOf((await Resolve(admin)).Body));
        Assert.Equal("faq", (await Resolve(admin, type: "faq")).Body.GetProperty("document").GetProperty("docType").GetString());
    }

    // ---- the rule ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_highest_priority_rule_wins_then_the_most_specific_and_a_disabled_rule_never_does()
    {
        var (_, admin) = await NewWorld();
        await CreateDoc(admin, await NewManual(admin, "Roadster"), "roadster");
        await CreateDoc(admin, await NewManual(admin, "Voyager"), "voyager");
        await CreateDoc(admin, await NewManual(admin, "Spark"), "spark");

        await CreateRule(admin, "broad", "ACM*", "roadster");
        Assert.Equal("roadster", (await Resolve(admin)).Body.GetProperty("vehicle").GetProperty("model").GetString());

        await CreateRule(admin, "more specific", "ACMRS1*", "voyager");
        Assert.Equal("voyager", (await Resolve(admin)).Body.GetProperty("vehicle").GetProperty("model").GetString());

        await CreateRule(admin, "higher priority", "AC*", "spark", priority: 1);
        Assert.Equal("spark", (await Resolve(admin)).Body.GetProperty("vehicle").GetProperty("model").GetString());

        var off = await CreateRule(admin, "disabled but highest", "A*", "roadster", priority: 99, enabled: false);
        Assert.Equal("spark", (await Resolve(admin)).Body.GetProperty("vehicle").GetProperty("model").GetString());
        Assert.False(off.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task The_model_year_comes_from_the_tenth_character_a_fixed_year_or_is_unknown()
    {
        var (_, admin) = await NewWorld();
        await CreateDoc(admin, await NewManual(admin, "Any year"), "roadster");
        var ranged = await CreateDoc(admin, await NewManual(admin, "2020 to 2022"), "roadster", from: 2020, to: 2022);

        var rule = (await CreateRule(admin, "from vin", "ACM*", "roadster")).GetProperty("id").GetGuid();
        Assert.Equal(2024, (await Resolve(admin)).Body.GetProperty("vehicle").GetProperty("modelYear").GetInt32());
        Assert.Equal(2025, (await Resolve(admin, "ACMRS1AB4SA000123")).Body.GetProperty("vehicle").GetProperty("modelYear").GetInt32());   // S
        Assert.Equal(2022, (await Resolve(admin, "ACMRS1AB4NA000123")).Body.GetProperty("vehicle").GetProperty("modelYear").GetInt32());   // N

        // the ranged manual applies to a 2022 vehicle only
        Assert.Equal(ranged.GetProperty("id").GetGuid(), DocOf((await Resolve(admin, "ACMRS1AB4NA000123")).Body));
        Assert.NotEqual(ranged.GetProperty("id").GetGuid(), DocOf((await Resolve(admin)).Body));

        // a character that is not a year code: the year is unknown and only a manual for no particular year can answer
        var unknown = await Resolve(admin, "ACMRS1AB4ZA000123");
        Assert.Equal(HttpStatusCode.OK, unknown.Status);
        Assert.Equal(JsonValueKind.Null, unknown.Body.GetProperty("vehicle").GetProperty("modelYear").ValueKind);
        Assert.NotEqual(ranged.GetProperty("id").GetGuid(), DocOf(unknown.Body));

        // a fixed year wins over the VIN
        await Call(HttpMethod.Put, $"/api/v1/knowledge/vin-rules/{rule}", admin, new { name = "from vin", pattern = "ACM*", model = "roadster", modelYear = 2021 });
        var fixedYear = await Resolve(admin);
        Assert.Equal(2021, fixedYear.Body.GetProperty("vehicle").GetProperty("modelYear").GetInt32());
        Assert.Equal(ranged.GetProperty("id").GetGuid(), DocOf(fixedYear.Body));

        // no year at all: the rule neither fixes it nor reads it
        await Call(HttpMethod.Put, $"/api/v1/knowledge/vin-rules/{rule}", admin, new { name = "from vin", pattern = "ACM*", model = "roadster", yearFromVin = false });
        var none = await Resolve(admin);
        Assert.Equal(JsonValueKind.Null, none.Body.GetProperty("vehicle").GetProperty("modelYear").ValueKind);
        Assert.NotEqual(ranged.GetProperty("id").GetGuid(), DocOf(none.Body));
    }

    // ---- ranking ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_published_manual_beats_one_in_review_whatever_else_differs()
    {
        var (_, admin) = await NewWorld();
        await CreateRule(admin, "gt", "ACM*", "roadster", "gt");
        var published = await CreateDoc(admin, await NewManual(admin, "Published, generic"), "roadster");
        var review = await CreateDoc(admin, await NewManual(admin, "In review, exact", status: "in_review"), "roadster", "gt", 2024, 2024);
        Assert.Equal(published.GetProperty("id").GetGuid(), DocOf((await Resolve(admin)).Body));
        _ = review;
    }

    [Fact]
    public async Task An_exact_variant_beats_an_empty_one_and_a_document_for_another_variant_is_not_a_candidate()
    {
        var (_, admin) = await NewWorld();
        await CreateRule(admin, "gt", "ACM*", "roadster", "gt");
        var generic = await CreateDoc(admin, await NewManual(admin, "Generic"), "roadster");
        Assert.Equal(generic.GetProperty("id").GetGuid(), DocOf((await Resolve(admin)).Body));
        await CreateDoc(admin, await NewManual(admin, "Base only"), "roadster", "base");
        Assert.Equal(generic.GetProperty("id").GetGuid(), DocOf((await Resolve(admin)).Body));
        var exact = await CreateDoc(admin, await NewManual(admin, "GT"), "roadster", "gt");
        Assert.Equal(exact.GetProperty("id").GetGuid(), DocOf((await Resolve(admin)).Body));
    }

    [Fact]
    public async Task A_rule_without_a_variant_only_finds_documents_for_the_whole_model()
    {
        var (_, admin) = await NewWorld();
        await CreateRule(admin, "any", "ACM*", "roadster");
        await CreateDoc(admin, await NewManual(admin, "GT only"), "roadster", "gt");
        Assert.Equal("knowledge.no_document", Code((await Resolve(admin)).Body));
        var generic = await CreateDoc(admin, await NewManual(admin, "Generic"), "roadster");
        Assert.Equal(generic.GetProperty("id").GetGuid(), DocOf((await Resolve(admin)).Body));
    }

    [Fact]
    public async Task A_narrower_year_range_beats_a_wider_one_beats_none()
    {
        var (_, admin) = await NewWorld();
        await CreateRule(admin);
        var none = await CreateDoc(admin, await NewManual(admin, "No years"), "roadster");
        var wide = await CreateDoc(admin, await NewManual(admin, "2020-2030"), "roadster", from: 2020, to: 2030);
        Assert.Equal(wide.GetProperty("id").GetGuid(), DocOf((await Resolve(admin)).Body));
        var narrow = await CreateDoc(admin, await NewManual(admin, "2023-2025"), "roadster", from: 2023, to: 2025);
        Assert.Equal(narrow.GetProperty("id").GetGuid(), DocOf((await Resolve(admin)).Body));
        var single = await CreateDoc(admin, await NewManual(admin, "2024"), "roadster", from: 2024, to: 2024);
        Assert.Equal(single.GetProperty("id").GetGuid(), DocOf((await Resolve(admin)).Body));
        await CreateDoc(admin, await NewManual(admin, "2026 only"), "roadster", from: 2026, to: 2026);              // not this vehicle's year
        Assert.Equal(single.GetProperty("id").GetGuid(), DocOf((await Resolve(admin)).Body));
        Assert.Equal(none.GetProperty("id").GetGuid(), DocOf((await Resolve(admin, "ACMRS1AB4YA000123")).Body));        // Y is 2000: no range holds it
    }

    [Fact]
    public async Task The_more_recently_updated_manual_wins_when_everything_else_ties()
    {
        var (_, admin) = await NewWorld();
        await CreateRule(admin);
        var older = await NewManual(admin, "Older");
        var newer = await NewManual(admin, "Newer");
        var olderDoc = (await CreateDoc(admin, older, "roadster")).GetProperty("id").GetGuid();
        var newerDoc = (await CreateDoc(admin, newer, "roadster")).GetProperty("id").GetGuid();
        Sql($"UPDATE assets SET updated_at = now() - interval '30 days' WHERE id IN ('{older}', '{newer}'); UPDATE assets SET updated_at = now() - interval '1 day' WHERE id = '{newer}'");
        Sql($"UPDATE knowledge_documents SET updated_at = now() - interval '40 days' WHERE id IN ('{olderDoc}', '{newerDoc}')");
        Assert.Equal(newerDoc, DocOf((await Resolve(admin)).Body));

        // a new version of the older manual (the asset is touched) makes it the newer one
        Sql($"UPDATE assets SET updated_at = now() WHERE id = '{older}'");
        Assert.Equal(olderDoc, DocOf((await Resolve(admin)).Body));
    }

    [Fact]
    public async Task The_language_is_the_one_asked_for_else_the_tenants_default_else_english_else_any_and_the_others_are_listed()
    {
        var (t, admin) = await NewWorld();
        await CreateRule(admin);
        var en = await CreateDoc(admin, await NewManual(admin, "English"), "roadster", lang: "en");
        var hi = await CreateDoc(admin, await NewManual(admin, "Hindi"), "roadster", lang: "hi");
        var ta = await CreateDoc(admin, await NewManual(admin, "Tamil"), "roadster", lang: "ta");
        var ids = new Dictionary<string, Guid> { ["en"] = en.GetProperty("id").GetGuid(), ["hi"] = hi.GetProperty("id").GetGuid(), ["ta"] = ta.GetProperty("id").GetGuid() };

        var asked = await Resolve(admin, lang: "hi");
        Assert.Equal(ids["hi"], DocOf(asked.Body));
        Assert.Equal(["en", "ta"], asked.Body.GetProperty("otherLanguages").EnumerateArray().Select(o => o.GetProperty("language").GetString()!).ToArray());
        var other = asked.Body.GetProperty("otherLanguages")[0];
        Assert.Equal(ids["en"], other.GetProperty("documentId").GetGuid());
        Assert.NotEqual(Guid.Empty, other.GetProperty("assetId").GetGuid());

        Assert.Equal(ids["en"], DocOf((await Resolve(admin)).Body));                       // nothing asked: English
        Assert.Equal(ids["en"], DocOf((await Resolve(admin, lang: "fr")).Body));           // not available: English
        Assert.Equal(ids["ta"], DocOf((await Resolve(admin, lang: "TA")).Body));           // case does not matter

        Sql($"UPDATE tenants SET settings_json = '{{\"defaultLanguage\":\"ta\"}}'::jsonb WHERE id = '{t}'");
        Assert.Equal(ids["ta"], DocOf((await Resolve(admin, lang: "fr")).Body));           // not available: the tenant's default
        Assert.Equal(ids["hi"], DocOf((await Resolve(admin, lang: "hi")).Body));

        Sql($"UPDATE knowledge_documents SET language = 'de' WHERE id = '{ids["en"]}'");
        Sql($"UPDATE tenants SET settings_json = '{{}}'::jsonb WHERE id = '{t}'");
        var any = await Resolve(admin, lang: "fr");                                        // no English and no default: the best-ranked document
        Assert.Equal(HttpStatusCode.OK, any.Status);
        Assert.Contains(DocOf(any.Body), ids.Values);
        Assert.Equal(2, any.Body.GetProperty("otherLanguages").GetArrayLength());
    }

    // ---- who sees what ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ask_assist_sees_manuals_in_review_and_published_while_everyone_else_sees_published_only()
    {
        var (t, admin) = await NewWorld();
        await CreateRule(admin);
        var review = (await CreateDoc(admin, await NewManual(admin, "In review", status: "in_review"), "roadster")).GetProperty("id").GetGuid();

        var assist = As(t, "Ask Assist");
        Assert.Equal(review, DocOf((await Resolve(assist)).Body));                         // nothing is published yet: the one in review
        Assert.Equal("in_review", (await Resolve(assist)).Body.GetProperty("document").GetProperty("assetStatus").GetString());
        foreach (var role in new[] { "Viewer", "Contributor" })
        {
            var r = await Resolve(As(t, role));
            Assert.Equal(HttpStatusCode.NotFound, r.Status);
            Assert.Equal("knowledge.no_document", Code(r.Body));                           // an unreadable document is simply not there
        }
        Assert.Equal(HttpStatusCode.NotFound, (await Resolve(Dealer(t, "North"))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Resolve(ApiTestHost.TokenFor(t, Guid.NewGuid(), ["ApiClient"], attributes: new() { ["channel"] = ["qr"] }))).Status);

        // the rest of the roles that can read unpublished work get it too
        foreach (var role in new[] { "Admin", "Librarian", "Editor", "Approver" })
            Assert.Equal(review, DocOf((await Resolve(role == "Admin" ? admin : As(t, role))).Body));

        // once a published one exists everyone gets that, Ask Assist included
        var published = (await CreateDoc(admin, await NewManual(admin, "Published", regions: null), "roadster")).GetProperty("id").GetGuid();
        foreach (var token in new[] { assist, As(t, "Viewer"), Dealer(t, "North"), admin })
            Assert.Equal(published, DocOf((await Resolve(token)).Body));
    }

    [Fact]
    public async Task Only_published_and_in_review_ever_resolve_whoever_asks()
    {
        var (t, admin) = await NewWorld();
        await CreateRule(admin);
        foreach (var status in new[] { "draft", "approved", "expired", "archived", "deleted", "blocked" })
        {
            var asset = await NewManual(admin, status);
            await CreateDoc(admin, asset, "roadster");
            Sql($"UPDATE assets SET status = '{status}' WHERE id = '{asset}'");       // mapped first (a deleted asset cannot be), then moved on
        }
        foreach (var token in new[] { admin, As(t, "Librarian"), As(t, "Ask Assist"), As(t, "Viewer") })
            Assert.Equal("knowledge.no_document", Code((await Resolve(token)).Body));
    }

    [Fact]
    public async Task A_dealer_gets_what_their_region_may_see_and_a_better_ranked_document_they_cannot_read_never_shows_through()
    {
        var (t, admin) = await NewWorld();
        await CreateRule(admin, "gt", "ACM*", "roadster", "gt");
        var generic = (await CreateDoc(admin, await NewManual(admin, "Generic, everywhere"), "roadster")).GetProperty("id").GetGuid();
        var southGt = (await CreateDoc(admin, await NewManual(admin, "GT, south only", regions: ["south"]), "roadster", "gt")).GetProperty("id").GetGuid();

        Assert.Equal(southGt, DocOf((await Resolve(admin)).Body));                          // best ranked for someone who may read it
        Assert.Equal(southGt, DocOf((await Resolve(As(t, "Viewer"))).Body));                // a viewer is not limited by region
        var north = await Resolve(Dealer(t, "North"));
        Assert.Equal(generic, DocOf(north.Body));                                           // the south manual is invisible: the next one answers
        Assert.Equal(0, north.Body.GetProperty("otherLanguages").GetArrayLength());
        Assert.Equal(southGt, DocOf((await Resolve(Dealer(t, "South"))).Body));

        var northGt = (await CreateDoc(admin, await NewManual(admin, "GT, north only", regions: ["north"]), "roadster", "gt")).GetProperty("id").GetGuid();
        var globalGt = (await CreateDoc(admin, await NewManual(admin, "GT, global", regions: ["global"]), "roadster", "gt", lang: "hi")).GetProperty("id").GetGuid();
        var n = await Resolve(Dealer(t, "North"));
        Assert.Equal(northGt, DocOf(n.Body));
        Assert.Equal(["hi"], n.Body.GetProperty("otherLanguages").EnumerateArray().Select(o => o.GetProperty("language").GetString()!).ToArray());
        Assert.Equal(globalGt, n.Body.GetProperty("otherLanguages")[0].GetProperty("documentId").GetGuid());

        // a dealer with no region has no read access at all (fail closed)
        var none = await Resolve(ApiTestHost.TokenFor(t, Guid.NewGuid(), ["Dealer"]));
        Assert.Equal(HttpStatusCode.Forbidden, none.Status);
    }

    [Fact]
    public async Task A_document_that_exists_only_in_a_region_the_dealer_cannot_read_gives_the_same_answer_as_no_document()
    {
        var (t, admin) = await NewWorld();
        await CreateRule(admin);
        await CreateDoc(admin, await NewManual(admin, "South only", regions: ["south"]), "roadster");
        var withDoc = await Resolve(Dealer(t, "North"));
        var (t2, admin2) = await NewWorld();
        await CreateRule(admin2);
        var without = await Resolve(Dealer(t2, "North"));
        Assert.Equal(without.Status, withDoc.Status);
        Assert.Equal(without.Body.GetProperty("title").GetString(), withDoc.Body.GetProperty("title").GetString());
        Assert.Equal(Code(without.Body), Code(withDoc.Body));
    }

    [Fact]
    public async Task Another_tenants_documents_and_rules_are_invisible()
    {
        var (_, adminA) = await NewWorld();
        var (_, adminB) = await NewWorld();
        await CreateRule(adminA);
        await CreateDoc(adminA, await NewManual(adminA), "roadster");
        Assert.Equal(HttpStatusCode.OK, (await Resolve(adminA)).Status);
        Assert.Equal("knowledge.no_rule", Code((await Resolve(adminB)).Body));             // B has neither the rule...
        await CreateRule(adminB);
        Assert.Equal("knowledge.no_document", Code((await Resolve(adminB)).Body));         // ...nor, once it has its own rule, A's document
    }

    [Fact]
    public async Task Resolving_is_stable_under_repetition_and_does_not_depend_on_creation_order()
    {
        var (_, admin) = await NewWorld();
        await CreateRule(admin, "two", "ACM*", "roadster", priority: 1);
        await CreateRule(admin, "one", "ACM*", "roadster", priority: 1);          // same priority and pattern: the smallest id (the first created) wins
        await CreateDoc(admin, await NewManual(admin), "roadster");
        var first = await Resolve(admin);
        for (var i = 0; i < 3; i++)
            Assert.Equal(first.Body.GetProperty("rule").GetProperty("id").GetGuid(), (await Resolve(admin)).Body.GetProperty("rule").GetProperty("id").GetGuid());
        Assert.Equal("two", first.Body.GetProperty("rule").GetProperty("name").GetString());
    }
}
