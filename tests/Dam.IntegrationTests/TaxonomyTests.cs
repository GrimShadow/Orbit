using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Dam.IntegrationTests;

[Collection("pg")]
public sealed class VocabularyTests(PostgresFixture pg) : ApiTestBase(pg)
{
    private async Task<Guid> Vocab(string token, string key, bool hierarchical = false, string[]? levels = null)
    {
        var r = await Call(HttpMethod.Post, "/api/v1/vocabularies", token, new { key, name = key.ToUpperInvariant(), hierarchical, levels });
        Assert.Equal(HttpStatusCode.Created, r.Status);
        return Id(r.Body, "id");
    }

    private async Task<Guid> Term(string token, Guid vocab, string code, Guid? parent = null, string? label = null)
    {
        var r = await Call(HttpMethod.Post, $"/api/v1/vocabularies/{vocab}/terms", token, new { parentId = parent, code, labels = new Dictionary<string, string> { ["en"] = label ?? code } });
        Assert.Equal(HttpStatusCode.Created, r.Status);
        return Id(r.Body, "id");
    }

    [Fact]
    public async Task A_hierarchical_vocabulary_returns_a_nested_tree_ordered_by_sort_order_then_code()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var v = await Vocab(admin, "vehicle", hierarchical: true, levels: ["Brand", "Model", "Variant"]);
        var acme = await Term(admin, v, "acme");
        var trail = await Term(admin, v, "acme_trail", acme);
        var roadster = await Term(admin, v, "acme_roadster", acme);
        await Term(admin, v, "acme_trail_x", trail);
        await Call(HttpMethod.Patch, $"/api/v1/terms/{trail}", admin, new { sortOrder = 5 });   // trail last
        await Call(HttpMethod.Patch, $"/api/v1/terms/{roadster}", admin, new { sortOrder = 1 });

        var tree = (await Call(HttpMethod.Get, $"/api/v1/vocabularies/{v}/terms?tree=true", admin)).Body;
        Assert.Equal(1, tree.GetArrayLength());
        var kids = tree[0].GetProperty("children");
        Assert.Equal(["acme_roadster", "acme_trail"], kids.EnumerateArray().Select(k => k.GetProperty("term").GetProperty("code").GetString()!).ToArray());
        Assert.Equal("acme.acme_trail.acme_trail_x", kids[1].GetProperty("children")[0].GetProperty("term").GetProperty("path").GetString());

        var flat = (await Call(HttpMethod.Get, $"/api/v1/vocabularies/{v}/terms", admin)).Body;
        Assert.Equal(4, flat.GetArrayLength());
        var voc = (await Call(HttpMethod.Get, $"/api/v1/vocabularies/{v}", admin)).Body;
        Assert.Equal(4, voc.GetProperty("termCount").GetInt32());
        Assert.Equal(["Brand", "Model", "Variant"], Strings(voc.GetProperty("levels")));
    }

    [Fact]
    public async Task Vocabulary_and_term_rules_are_enforced()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var flat = await Vocab(admin, "channel");
        var tree = await Vocab(admin, "region", hierarchical: true);
        var root = await Term(admin, tree, "global");

        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, "/api/v1/vocabularies", admin, new { key = "channel", name = "dupe", hierarchical = false })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/api/v1/vocabularies", admin, new { key = "Bad Key", name = "x" })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/api/v1/vocabularies", admin, new { key = "flat", name = "x", hierarchical = false, levels = new[] { "A" } })).Status);

        // a flat vocabulary has no parents; codes are unique per vocabulary and syntactically strict
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, $"/api/v1/vocabularies/{flat}/terms", admin, new { parentId = root, code = "x", labels = new { en = "X" } })).Status);
        await Term(admin, flat, "signage");
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"/api/v1/vocabularies/{flat}/terms", admin, new { code = "signage", labels = new { en = "Again" } })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, $"/api/v1/vocabularies/{flat}/terms", admin, new { code = "Has Space", labels = new { en = "X" } })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, $"/api/v1/vocabularies/{flat}/terms", admin, new { code = "ok", labels = new Dictionary<string, string>() })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, $"/api/v1/vocabularies/{flat}/terms", admin, new { code = "ok", labels = new { e = "x" } })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/vocabularies/{Guid.NewGuid()}/terms", admin)).Status);
    }

    [Fact]
    public async Task Terms_can_be_relabelled_deprecated_and_moved_and_the_subtree_paths_follow()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var v = await Vocab(admin, "vehicle", hierarchical: true);
        var a = await Term(admin, v, "a"); var b = await Term(admin, v, "b");
        var a1 = await Term(admin, v, "a1", a); var a11 = await Term(admin, v, "a11", a1);

        var upd = await Call(HttpMethod.Patch, $"/api/v1/terms/{a1}", admin, new { labels = new { en = "Renamed", hi = "नाम" }, status = "deprecated", attributes = new { modelYear = 2026 } });
        Assert.Equal("deprecated", upd.Body.GetProperty("status").GetString());
        Assert.Equal("Renamed", upd.Body.GetProperty("labels").GetProperty("en").GetString());
        Assert.Equal("a1", upd.Body.GetProperty("code").GetString()); // the code never changes
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Patch, $"/api/v1/terms/{a1}", admin, new { status = "gone" })).Status);

        var active = (await Call(HttpMethod.Get, $"/api/v1/vocabularies/{v}/terms?includeDeprecated=false", admin)).Body;
        Assert.DoesNotContain("a1", active.EnumerateArray().Select(x => x.GetProperty("code").GetString()));

        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"/api/v1/terms/{a}/move", admin, new { newParentId = a11 })).Status); // beneath itself
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"/api/v1/terms/{a}/move", admin, new { newParentId = a })).Status);
        var moved = await Call(HttpMethod.Post, $"/api/v1/terms/{a1}/move", admin, new { newParentId = b });
        Assert.Equal("b.a1", moved.Body.GetProperty("path").GetString());
        Assert.Equal("b.a1.a11", (await Call(HttpMethod.Get, $"/api/v1/terms/{a11}", admin)).Body.GetProperty("path").GetString());

        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Delete, $"/api/v1/terms/{b}", admin)).Status); // has children
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"/api/v1/terms/{a11}", admin)).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"/api/v1/terms/{a1}", admin)).Status);
    }

    [Fact]
    public async Task Only_taxonomy_managers_can_change_vocabularies_but_readers_can_look()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var v = await Vocab(admin, "channel");
        foreach (var role in new[] { "Editor", "Viewer", "Dealer" })
        {
            var token = ApiTestHost.TokenFor(t, Guid.NewGuid(), [role], attributes: new() { ["region"] = ["North"] });
            Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, "/api/v1/vocabularies", token)).Status);
            Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, $"/api/v1/vocabularies/{v}/terms", token)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "/api/v1/vocabularies", token, new { key = "x", name = "x" })).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, $"/api/v1/vocabularies/{v}/terms", token, new { code = "x", labels = new { en = "x" } })).Status);
        }
        Assert.Equal(HttpStatusCode.Created, (await Call(HttpMethod.Post, "/api/v1/vocabularies", As(t, "Librarian"), new { key = "made_by_librarian", name = "L" })).Status);
    }

    [Fact]
    public async Task Vocabularies_are_per_tenant_and_the_same_key_can_exist_in_two_tenants()
    {
        var a = await NewTenantAsync(); var b = await NewTenantAsync();
        var va = await Vocab(Admin(a), "region", hierarchical: true);
        await Vocab(Admin(b), "region", hierarchical: true);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/vocabularies/{va}", Admin(b))).Status);
        Assert.Equal(1, (await Call(HttpMethod.Get, "/api/v1/vocabularies", Admin(b))).Body.GetArrayLength());
    }
}

[Collection("pg")]
public sealed class SchemaTests(PostgresFixture pg) : ApiTestBase(pg)
{
    private async Task<(Guid Tenant, string Admin)> TenantWithVocabs()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var lang = Id((await Call(HttpMethod.Post, "/api/v1/vocabularies", admin, new { key = "language", name = "Language" })).Body, "id");
        foreach (var c in new[] { "en", "hi" }) await Call(HttpMethod.Post, $"/api/v1/vocabularies/{lang}/terms", admin, new { code = c, labels = new { en = c } });
        var veh = Id((await Call(HttpMethod.Post, "/api/v1/vocabularies", admin, new { key = "vehicle", name = "Vehicle", hierarchical = true })).Body, "id");
        var acme = Id((await Call(HttpMethod.Post, $"/api/v1/vocabularies/{veh}/terms", admin, new { code = "acme", labels = new { en = "Acme" } })).Body, "id");
        await Call(HttpMethod.Post, $"/api/v1/vocabularies/{veh}/terms", admin, new { parentId = acme, code = "acme_trail", labels = new { en = "Trail" } });
        var old = Id((await Call(HttpMethod.Post, $"/api/v1/vocabularies/{veh}/terms", admin, new { parentId = acme, code = "acme_old", labels = new { en = "Old" } })).Body, "id");
        await Call(HttpMethod.Patch, $"/api/v1/terms/{old}", admin, new { status = "deprecated" });
        return (t, admin);
    }

    private static object[] BasicFields(bool requireDescription = true) =>
    [
        Field("description", "ml_text", d => d["required"] = requireDescription),
        Field("year", "number", d => { d["min"] = 1990; d["max"] = 2100; d["wholeNumbers"] = true; }),
        Field("language", "vocabulary", d => d["vocabulary"] = "language"),
        Field("vehicle", "term_tree", d => { d["vocabulary"] = "vehicle"; d["multi"] = true; }),
    ];

    [Fact]
    public async Task Saving_a_schema_creates_versions_and_an_unchanged_save_creates_none()
    {
        var (t, admin) = await TenantWithVocabs();
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, "/api/v1/schemas/image", admin)).Status); // none yet

        var v1 = await Call(HttpMethod.Put, "/api/v1/schemas/image", admin, new { fields = BasicFields(), changeNote = "first" });
        Assert.Equal(HttpStatusCode.OK, v1.Status);
        Assert.True(v1.Body.GetProperty("newVersion").GetBoolean());
        Assert.Equal(1, v1.Body.GetProperty("schema").GetProperty("version").GetInt32());

        var same = await Call(HttpMethod.Put, "/api/v1/schemas/image", admin, new { fields = BasicFields() });
        Assert.False(same.Body.GetProperty("newVersion").GetBoolean());
        Assert.Equal(1, same.Body.GetProperty("schema").GetProperty("version").GetInt32());

        var more = BasicFields().Append(Field("credit", "text")).ToArray();
        more[1] = Field("year", "number", d => { d["min"] = 1980; d["max"] = 2100; d["wholeNumbers"] = true; });   // widened range
        var v2 = await Call(HttpMethod.Put, "/api/v1/schemas/image", admin, new { fields = more, changeNote = "add credit" });
        Assert.Equal(2, v2.Body.GetProperty("schema").GetProperty("version").GetInt32());
        Assert.Equal(["credit"], Strings(v2.Body.GetProperty("added")));
        Assert.Equal(["year"], Strings(v2.Body.GetProperty("changed")));
        Assert.Empty(Strings(v2.Body.GetProperty("removed")));

        Assert.Equal(2, (await Call(HttpMethod.Get, "/api/v1/schemas/image", admin)).Body.GetProperty("version").GetInt32());
        var old = (await Call(HttpMethod.Get, "/api/v1/schemas/image?version=1", admin)).Body;
        Assert.Equal(4, old.GetProperty("fields").GetArrayLength());
        Assert.False(old.GetProperty("isCurrent").GetBoolean());
        var versions = (await Call(HttpMethod.Get, "/api/v1/schemas/image/versions", admin)).Body;
        Assert.Equal([2, 1], versions.EnumerateArray().Select(v => v.GetProperty("version").GetInt32()).ToArray());
        Assert.Equal(1, Count(t, "SELECT count(*) FROM metadata_schemas WHERE asset_type = 'image' AND is_current"));

        var list = (await Call(HttpMethod.Get, "/api/v1/schemas", admin)).Body;
        Assert.Equal(2, list[0].GetProperty("currentVersion").GetInt32());
    }

    [Fact]
    public async Task Bad_schema_definitions_are_refused_with_field_level_errors()
    {
        var (_, admin) = await TenantWithVocabs();
        async Task<JsonElement> Put(params object[] fields)
        {
            var r = await Call(HttpMethod.Put, "/api/v1/schemas/image", admin, new { fields });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status);
            return r.Body.GetProperty("errors");
        }
        Assert.True((await Put(Field("Bad-Name", "text"))).TryGetProperty("fields[0].name", out _));
        Assert.True((await Put(Field("title", "text"))).TryGetProperty("fields[0].name", out _)); // reserved
        Assert.True((await Put(Field("a", "text"), Field("a", "text"))).TryGetProperty("fields[1].name", out _));
        Assert.True((await Put(Field("e", "enum"))).TryGetProperty("fields[0].options", out _));
        Assert.True((await Put(Field("v", "vocabulary"))).TryGetProperty("fields[0].vocabulary", out _));
        Assert.True((await Put(Field("v", "vocabulary", d => d["vocabulary"] = "missing"))).TryGetProperty("fields[0].vocabulary", out _));
        Assert.True((await Put(Field("v", "term_tree", d => d["vocabulary"] = "language"))).TryGetProperty("fields[0].vocabulary", out _)); // flat vocabulary
        Assert.True((await Put(Field("x", "hologram"))).TryGetProperty("fields[0].type", out _));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Put, "/api/v1/schemas/blueprint", admin, new { fields = Array.Empty<object>() })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, "/api/v1/schemas/blueprint", admin)).Status);
    }

    [Fact]
    public async Task A_missing_required_field_fails_with_field_level_problem_details_and_old_versions_stay_valid()
    {
        var (_, admin) = await TenantWithVocabs();
        await Call(HttpMethod.Put, "/api/v1/schemas/image", admin, new { fields = BasicFields(requireDescription: false) }); // v1: nothing required
        await Call(HttpMethod.Put, "/api/v1/schemas/image", admin, new { fields = BasicFields(requireDescription: true) });  // v2: description required

        var empty = new { metadata = new { } };
        var current = await Call(HttpMethod.Post, "/api/v1/schemas/image/validate", admin, empty);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, current.Status);
        Assert.Equal("application/json", "application/json");
        Assert.Equal("This field is required.", current.Body.GetProperty("errors").GetProperty("metadata.description")[0].GetString());

        // An asset written under v1 is still valid against v1, even though v2 would reject it.
        var v1 = await Call(HttpMethod.Post, "/api/v1/schemas/image/validate", admin, new { metadata = new { }, version = 1 });
        Assert.Equal(HttpStatusCode.OK, v1.Status);
        Assert.Equal(1, v1.Body.GetProperty("schemaVersion").GetInt32());

        var ok = await Call(HttpMethod.Post, "/api/v1/schemas/image/validate", admin, new { metadata = new { description = new { en = "A car" } } });
        Assert.Equal(HttpStatusCode.OK, ok.Status);
        Assert.Equal(2, ok.Body.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, "/api/v1/schemas/image/validate", admin, new { metadata = new { }, version = 9 })).Status);
    }

    [Fact]
    public async Task Vocabulary_values_are_checked_against_the_tenants_real_terms()
    {
        var (_, admin) = await TenantWithVocabs();
        await Call(HttpMethod.Put, "/api/v1/schemas/image", admin, new { fields = BasicFields(requireDescription: false) });
        async Task<(HttpStatusCode, JsonElement)> V(object metadata) => await Call(HttpMethod.Post, "/api/v1/schemas/image/validate", admin, new { metadata });

        Assert.Equal(HttpStatusCode.OK, (await V(new { language = "en", vehicle = new[] { "acme", "acme_trail" } })).Item1);   // any level of the tree
        var unknown = await V(new { language = "fr", vehicle = new[] { "acme_bogus" } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.Item1);
        Assert.Contains("Unknown term 'fr'", unknown.Item2.GetProperty("errors").GetProperty("metadata.language")[0].GetString());
        Assert.Contains("acme_bogus", unknown.Item2.GetProperty("errors").GetProperty("metadata.vehicle")[0].GetString());
        var deprecated = await V(new { vehicle = new[] { "acme_old" } });
        Assert.Contains("deprecated", deprecated.Item2.GetProperty("errors").GetProperty("metadata.vehicle")[0].GetString());

        // types and ranges are reported per field, several at once
        var many = await V(new { year = 1500, language = 5, extra = "x" });
        var errors = many.Item2.GetProperty("errors");
        Assert.True(errors.TryGetProperty("metadata.year", out _));
        Assert.True(errors.TryGetProperty("metadata.language", out _));
        Assert.True(errors.TryGetProperty("metadata.extra", out _));
    }

    [Fact]
    public async Task Multilingual_text_only_accepts_the_languages_enabled_for_the_tenant()
    {
        var (_, admin) = await TenantWithVocabs();
        await Call(HttpMethod.Put, "/api/v1/schemas/image", admin, new { fields = BasicFields(requireDescription: false) });
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, "/api/v1/schemas/image/validate", admin, new { metadata = new { description = new { fr = "Une voiture" } } })).Status);

        await Call(HttpMethod.Patch, "/api/v1/tenants/current", admin, new { settings = new { languages = new[] { "en", "hi" } } });
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, "/api/v1/schemas/image/validate", admin, new { metadata = new { description = new { hi = "गाड़ी" } } })).Status);
        var fr = await Call(HttpMethod.Post, "/api/v1/schemas/image/validate", admin, new { metadata = new { description = new { fr = "Une voiture" } } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, fr.Status);
        Assert.Contains("not enabled", fr.Body.GetProperty("errors").GetProperty("metadata.description")[0].GetString());
    }

    [Fact]
    public async Task A_vocabulary_used_by_a_schema_cannot_be_deleted_until_the_field_is_removed()
    {
        var (_, admin) = await TenantWithVocabs();
        await Call(HttpMethod.Put, "/api/v1/schemas/image", admin, new { fields = BasicFields() });
        var lang = (await Call(HttpMethod.Get, "/api/v1/vocabularies", admin)).Body.EnumerateArray().First(v => v.GetProperty("key").GetString() == "language").GetProperty("id").GetGuid();
        var blocked = await Call(HttpMethod.Delete, $"/api/v1/vocabularies/{lang}", admin);
        Assert.Equal(HttpStatusCode.Conflict, blocked.Status);
        Assert.Contains("image", blocked.Body.GetProperty("title").GetString());

        await Call(HttpMethod.Put, "/api/v1/schemas/image", admin, new { fields = BasicFields().Where(f => ((Dictionary<string, object?>)f)["name"] as string != "language").ToArray() });
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"/api/v1/vocabularies/{lang}", admin)).Status);
    }

    [Fact]
    public async Task Only_schema_managers_can_change_schemas_and_readers_can_validate()
    {
        var (t, admin) = await TenantWithVocabs();
        await Call(HttpMethod.Put, "/api/v1/schemas/image", admin, new { fields = BasicFields(requireDescription: false) });
        foreach (var role in new[] { "Editor", "Viewer", "Approver" })
        {
            var token = As(t, role);
            Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, "/api/v1/schemas/image", token)).Status);
            Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, "/api/v1/schemas/image/validate", token, new { metadata = new { } })).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Put, "/api/v1/schemas/image", token, new { fields = Array.Empty<object>() })).Status);
        }
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, "/api/v1/schemas/video", As(t, "Librarian"), new { fields = Array.Empty<object>() })).Status);
    }
}

[Collection("pg")]
public sealed class TemplateTests(PostgresFixture pg) : ApiTestBase(pg)
{
    [Fact]
    public async Task The_catalog_lists_the_shipped_packs_with_what_they_contain()
    {
        var t = await NewTenantAsync();
        var list = (await Call(HttpMethod.Get, "/api/v1/templates", Admin(t))).Body;
        var ids = list.EnumerateArray().Select(p => p.GetProperty("id").GetString()!).ToArray();
        Assert.Contains("core", ids);
        Assert.Contains("automotive-sample", ids);
        var auto = list.EnumerateArray().First(p => p.GetProperty("id").GetString() == "automotive-sample");
        Assert.Equal(["core"], Strings(auto.GetProperty("requires")));
        Assert.True(auto.GetProperty("terms").GetInt32() > 20);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, "/api/v1/templates", As(t, "Librarian"))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, "/api/v1/templates/nope/apply", Admin(t))).Status);
    }

    [Fact]
    public async Task Applying_core_adds_the_base_taxonomy_and_a_schema_for_every_asset_type_and_repeating_it_changes_nothing()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var first = (await Call(HttpMethod.Post, "/api/v1/templates/core/apply", admin)).Body;
        Assert.Equal(["core"], Strings(first.GetProperty("applied")));
        Assert.Equal(4, first.GetProperty("vocabulariesCreated").GetInt32());
        Assert.Equal(27, first.GetProperty("termsCreated").GetInt32());
        Assert.Equal(8, first.GetProperty("schemasCreated").GetInt32());
        Assert.Equal(8, (await Call(HttpMethod.Get, "/api/v1/schemas", admin)).Body.GetArrayLength());

        var second = (await Call(HttpMethod.Post, "/api/v1/templates/core/apply", admin)).Body;
        Assert.Equal(0, second.GetProperty("vocabulariesCreated").GetInt32());
        Assert.Equal(0, second.GetProperty("termsCreated").GetInt32());
        Assert.Equal(0, second.GetProperty("schemasCreated").GetInt32());
        Assert.Equal(0, second.GetProperty("schemasExtended").GetInt32());
        Assert.Equal(1, Count(t, "SELECT max(version) FROM metadata_schemas WHERE asset_type = 'image'"));
    }

    [Fact]
    public async Task A_pack_pulls_in_what_it_requires_and_extends_existing_schemas_with_a_new_version()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var r = (await Call(HttpMethod.Post, "/api/v1/templates/automotive-sample/apply", admin)).Body;
        Assert.Equal(["core", "automotive-sample"], Strings(r.GetProperty("applied"))); // dependency first
        Assert.Equal(4, r.GetProperty("schemasExtended").GetInt32()); // image, video, document, manual got extra fields
        Assert.True(r.GetProperty("foldersCreated").GetInt32() >= 10);

        var image = (await Call(HttpMethod.Get, "/api/v1/schemas/image", admin)).Body;
        Assert.Equal(2, image.GetProperty("version").GetInt32());
        var names = image.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()!).ToArray();
        Assert.Contains("description", names); // from core
        Assert.Contains("vehicle", names);     // from the sample
        Assert.Equal(1, (await Call(HttpMethod.Get, "/api/v1/schemas/image?version=1", admin)).Body.GetProperty("version").GetInt32());

        // the merged region tree has the sample's areas under core's Global
        var region = (await Call(HttpMethod.Get, "/api/v1/vocabularies", admin)).Body.EnumerateArray().First(v => v.GetProperty("key").GetString() == "region").GetProperty("id").GetGuid();
        var tree = (await Call(HttpMethod.Get, $"/api/v1/vocabularies/{region}/terms?tree=true", admin)).Body;
        Assert.Equal(1, tree.GetArrayLength());
        Assert.Equal(["east", "north", "south", "west"], tree[0].GetProperty("children").EnumerateArray().Select(c => c.GetProperty("term").GetProperty("code").GetString()!).Order().ToArray());
    }

    [Fact]
    public async Task Reapplying_a_pack_never_overwrites_what_the_tenant_changed()
    {
        var t = await NewTenantAsync("core"); var admin = Admin(t);
        var channel = (await Call(HttpMethod.Get, "/api/v1/vocabularies", admin)).Body.EnumerateArray().First(v => v.GetProperty("key").GetString() == "channel").GetProperty("id").GetGuid();
        var web = (await Call(HttpMethod.Get, $"/api/v1/vocabularies/{channel}/terms", admin)).Body.EnumerateArray().First(x => x.GetProperty("code").GetString() == "web").GetProperty("id").GetGuid();
        await Call(HttpMethod.Patch, $"/api/v1/terms/{web}", admin, new { labels = new { en = "Our website" } });
        await Call(HttpMethod.Delete, $"/api/v1/terms/{(await Call(HttpMethod.Get, $"/api/v1/vocabularies/{channel}/terms", admin)).Body.EnumerateArray().First(x => x.GetProperty("code").GetString() == "print").GetProperty("id").GetGuid()}", admin);

        // The tenant also adds its own field and removes a stock one.
        var image = (await Call(HttpMethod.Get, "/api/v1/schemas/image", admin)).Body.GetProperty("fields").EnumerateArray().ToList();
        var edited = image.Where(f => f.GetProperty("name").GetString() != "tags").Select(f => (object)JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(f.GetRawText())!).Append(Field("house_style", "text")).ToArray();
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, "/api/v1/schemas/image", admin, new { fields = edited })).Status);

        var again = (await Call(HttpMethod.Post, "/api/v1/templates/core/apply", admin)).Body;
        Assert.Equal(1, again.GetProperty("termsCreated").GetInt32());      // only the deleted 'print' term comes back
        Assert.Equal(1, again.GetProperty("schemasExtended").GetInt32());   // 'tags' comes back as a new field; nothing else changes

        var terms = (await Call(HttpMethod.Get, $"/api/v1/vocabularies/{channel}/terms", admin)).Body;
        Assert.Equal("Our website", terms.EnumerateArray().First(x => x.GetProperty("code").GetString() == "web").GetProperty("labels").GetProperty("en").GetString());
        var names = (await Call(HttpMethod.Get, "/api/v1/schemas/image", admin)).Body.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()!).ToArray();
        Assert.Contains("house_style", names); // the tenant's own field survives
    }

    [Fact]
    public async Task Templates_named_at_tenant_setup_are_applied_and_a_sample_pack_validates_real_metadata()
    {
        var t = await NewTenantAsync("core", "automotive-sample"); var admin = Admin(t);
        Assert.Equal(3, (await Call(HttpMethod.Get, "/api/v1/vocabularies", admin)).Body.EnumerateArray().Count(v => v.GetProperty("key").GetString() is "vehicle" or "campaign" or "region"));

        var good = await Call(HttpMethod.Post, "/api/v1/schemas/image/validate", admin, new { metadata = new
            { description = new { en = "Roadster GT on the coast" }, language = new[] { "en" }, channels = new[] { "signage", "web" },
              regions = new[] { "north", "global" }, vehicle = new[] { "acme_roadster_gt_2026" }, campaign = new[] { "spring_launch" } } });
        Assert.Equal(HttpStatusCode.OK, good.Status);

        var bad = await Call(HttpMethod.Post, "/api/v1/schemas/image/validate", admin, new { metadata = new { vehicle = new[] { "acme_roadster_gt_1999" }, channels = new[] { "fax" } } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.Status);
        Assert.True(bad.Body.GetProperty("errors").TryGetProperty("metadata.vehicle", out _));
        Assert.True(bad.Body.GetProperty("errors").TryGetProperty("metadata.channels", out _));
    }

    [Fact]
    public async Task Tenant_setup_with_an_unknown_template_is_refused_and_creates_nothing()
    {
        var id = Dam.Domain.Common.Uuid7.NewGuid();
        await using var scope = Api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<Dam.Api.Auth.TenantOverride>().Value = id;
        var r = await scope.ServiceProvider.GetRequiredService<Dam.Application.Messaging.IDispatcher>()
            .Send(new Dam.Application.Identity.EnsureTenantCommand(id, "Bad", "bad-" + id.ToString("N")[..8], ["core", "does-not-exist"]));
        Assert.True(r.IsFailure);
        Assert.Contains("does-not-exist", r.Error.FieldErrors!["templates"][0]);
        Assert.Equal(0, Count(id, "SELECT count(*) FROM tenants")); // the transaction rolled back
    }
}
