using System.Net;
using System.Text.Json;

namespace Dam.IntegrationTests;

/// <summary>Knowledge documents on top of assets (ADR-0039): the mapping, who may make and read it, chapters entered by hand, the audit trail and tenant isolation.</summary>
[Collection("pg")]
public sealed class KnowledgeDocumentTests(PostgresFixture pg) : KnowledgeApiBase(pg)
{
    // ---- creating ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_manual_is_mapped_to_a_model_and_the_answer_is_the_document_with_its_asset_state()
    {
        var (t, admin) = await NewWorld();
        var asset = await NewManual(admin, "Roadster owner's manual");
        var r = await Call(HttpMethod.Post, "/api/v1/knowledge/documents", admin,
            new { assetId = asset, docType = "owner_manual", model = " Roadster ", variant = "GT", modelYearFrom = 2023, modelYearTo = 2025, language = "EN", theme = "ICE" });
        Assert.Equal(HttpStatusCode.Created, r.Status);
        var d = r.Body;
        Assert.Equal(asset, d.GetProperty("assetId").GetGuid());
        Assert.Equal("Roadster owner's manual", d.GetProperty("title").GetString());
        Assert.Equal("manual", d.GetProperty("assetType").GetString());
        Assert.Equal("published", d.GetProperty("assetStatus").GetString());
        Assert.Equal("owner_manual", d.GetProperty("docType").GetString());
        Assert.Equal("roadster", d.GetProperty("model").GetString());               // codes are stored in lower case
        Assert.Equal("gt", d.GetProperty("variant").GetString());
        Assert.Equal(2023, d.GetProperty("modelYearFrom").GetInt32());
        Assert.Equal(2025, d.GetProperty("modelYearTo").GetInt32());
        Assert.Equal("en", d.GetProperty("language").GetString());
        Assert.Equal("ice", d.GetProperty("theme").GetString());
        Assert.Equal(0, d.GetProperty("chapterCount").GetInt32());
        Assert.Equal("none", d.GetProperty("chaptersSource").GetString());
        Assert.Equal(JsonValueKind.Null, d.GetProperty("chaptersIndexedAt").ValueKind);
        Assert.Equal(0, d.GetProperty("chapters").GetArrayLength());
        Assert.NotEqual(Guid.Empty, d.GetProperty("id").GetGuid());

        var again = await Call(HttpMethod.Get, $"/api/v1/knowledge/documents/{d.GetProperty("id").GetGuid()}", admin);
        Assert.Equal(HttpStatusCode.OK, again.Status);
        Assert.Equal(d.GetProperty("id").GetGuid(), again.Body.GetProperty("id").GetGuid());
        Assert.Equal(1, Count(t, "SELECT count(*) FROM knowledge_documents"));
    }

    [Fact]
    public async Task A_document_may_sit_on_a_manual_a_document_or_an_html5_asset_and_nothing_else()
    {
        var (_, admin) = await NewWorld();
        foreach (var type in new[] { "manual", "document", "html5" })
            Assert.NotEqual(Guid.Empty, (await CreateDoc(admin, await NewManual(admin, type, type: type))).GetProperty("id").GetGuid());
        foreach (var type in new[] { "image", "video", "audio", "3d", "other" })
        {
            var r = await Call(HttpMethod.Post, "/api/v1/knowledge/documents", admin, new { assetId = await NewManual(admin, type, type: type), docType = "faq", model = "roadster" });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status);
            Assert.Contains("manual, document, html5", r.Body.GetProperty("errors").GetProperty("assetId")[0].GetString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Every_document_type_is_accepted_and_each_bad_field_is_reported_by_name()
    {
        var (_, admin) = await NewWorld();
        foreach (var type in new[] { "owner_manual", "faq", "service", "training", "product_info" })
            await CreateDoc(admin, await NewManual(admin, type), type: type);

        var asset = await NewManual(admin);
        async Task<JsonElement> Errors(object body)
        {
            var r = await Call(HttpMethod.Post, "/api/v1/knowledge/documents", admin, body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status);
            return r.Body.GetProperty("errors");
        }
        Assert.True((await Errors(new { assetId = asset, docType = "brochure", model = "roadster" })).TryGetProperty("docType", out _));
        Assert.True((await Errors(new { assetId = asset, docType = "faq" })).TryGetProperty("model", out _));
        Assert.True((await Errors(new { assetId = asset, docType = "faq", model = "Road Ster!" })).TryGetProperty("model", out _));
        Assert.True((await Errors(new { assetId = asset, docType = "faq", model = new string('a', 101) })).TryGetProperty("model", out _));
        Assert.True((await Errors(new { assetId = asset, docType = "faq", model = "roadster", variant = "has space" })).TryGetProperty("variant", out _));
        Assert.True((await Errors(new { assetId = asset, docType = "faq", model = "roadster", theme = "no/slash" })).TryGetProperty("theme", out _));
        Assert.True((await Errors(new { assetId = asset, docType = "faq", model = "roadster", modelYearFrom = 2024 })).TryGetProperty("modelYearTo", out _));
        Assert.True((await Errors(new { assetId = asset, docType = "faq", model = "roadster", modelYearTo = 2024 })).TryGetProperty("modelYearFrom", out _));
        Assert.True((await Errors(new { assetId = asset, docType = "faq", model = "roadster", modelYearFrom = 2026, modelYearTo = 2024 })).TryGetProperty("modelYearTo", out _));
        Assert.True((await Errors(new { assetId = asset, docType = "faq", model = "roadster", modelYearFrom = 1900, modelYearTo = 2024 })).TryGetProperty("modelYearFrom", out _));
        Assert.True((await Errors(new { assetId = asset, docType = "faq", model = "roadster", language = "x" })).TryGetProperty("language", out _));
        Assert.True((await Errors(new { docType = "faq", model = "roadster" })).TryGetProperty("assetId", out _));
        Assert.True((await Errors(new { assetId = Guid.NewGuid(), docType = "faq", model = "roadster" })).TryGetProperty("assetId", out _));
    }

    [Fact]
    public async Task One_document_per_asset_and_a_deleted_asset_cannot_be_mapped()
    {
        var (_, admin) = await NewWorld();
        var asset = await NewManual(admin);
        await CreateDoc(admin, asset);
        var dup = await Call(HttpMethod.Post, "/api/v1/knowledge/documents", admin, new { assetId = asset, docType = "faq", model = "other" });
        Assert.Equal(HttpStatusCode.Conflict, dup.Status);
        Assert.Contains("already has a knowledge document", dup.Body.GetProperty("title").GetString(), StringComparison.Ordinal);

        var gone = await NewManual(admin, status: "deleted");
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, "/api/v1/knowledge/documents", admin, new { assetId = gone, docType = "faq", model = "roadster" })).Status);
    }

    [Fact]
    public async Task The_language_defaults_to_the_tenants_default_and_must_be_one_the_tenant_has_enabled()
    {
        var (t, admin) = await NewWorld();
        Sql($"UPDATE tenants SET settings_json = '{{\"languages\":[\"en\",\"hi\"],\"defaultLanguage\":\"hi\"}}'::jsonb WHERE id = '{t}'");
        var d = await Call(HttpMethod.Post, "/api/v1/knowledge/documents", admin, new { assetId = await NewManual(admin), docType = "faq", model = "roadster" });
        Assert.Equal(HttpStatusCode.Created, d.Status);
        Assert.Equal("hi", d.Body.GetProperty("language").GetString());

        var bad = await Call(HttpMethod.Post, "/api/v1/knowledge/documents", admin, new { assetId = await NewManual(admin), docType = "faq", model = "roadster", language = "ta" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.Status);
        Assert.Contains("not enabled", bad.Body.GetProperty("errors").GetProperty("language")[0].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_any_tenant_settings_the_language_defaults_to_english()
    {
        var (_, admin) = await NewWorld();
        var d = await Call(HttpMethod.Post, "/api/v1/knowledge/documents", admin, new { assetId = await NewManual(admin), docType = "faq", model = "roadster" });
        Assert.Equal("en", d.Body.GetProperty("language").GetString());
    }

    // ---- listing, updating, deleting -------------------------------------------------------------------------------

    [Fact]
    public async Task The_list_filters_by_type_model_variant_year_language_and_text_and_pages_with_a_cursor()
    {
        var (_, admin) = await NewWorld();
        var gt = await CreateDoc(admin, await NewManual(admin, "Roadster GT manual"), "roadster", "gt", 2023, 2025);
        var all = await CreateDoc(admin, await NewManual(admin, "Roadster general manual"), "roadster");
        var hindi = await CreateDoc(admin, await NewManual(admin, "Roadster Hindi manual"), "roadster", null, lang: "hi");
        var faq = await CreateDoc(admin, await NewManual(admin, "Spark FAQ"), "spark", type: "faq");
        Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

        async Task<Guid[]> Ids(string query)
        {
            var r = await Call(HttpMethod.Get, "/api/v1/knowledge/documents" + query, admin);
            Assert.Equal(HttpStatusCode.OK, r.Status);
            return r.Body.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToArray();
        }
        Assert.Equal(4, (await Ids("")).Length);
        Assert.Equal([Id(faq)], await Ids("?type=faq"));
        Assert.Equal(3, (await Ids("?model=roadster")).Length);
        Assert.Equal([Id(gt)], await Ids("?model=roadster&variant=gt"));
        Assert.Equal([Id(hindi)], await Ids("?lang=hi"));
        Assert.Contains(Id(gt), await Ids("?year=2024"));
        Assert.Contains(Id(all), await Ids("?year=2024"));                          // a document for no year in particular applies to any year
        Assert.DoesNotContain(Id(gt), await Ids("?year=2030"));
        Assert.Equal([Id(faq)], await Ids("?q=spark"));
        Assert.Equal([Id(gt)], await Ids("?q=GT%20manual"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Get, "/api/v1/knowledge/documents?type=nope", admin)).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Get, "/api/v1/knowledge/documents?cursor=garbage", admin)).Status);

        // paging: newest first, one at a time, until the end
        var seen = new List<Guid>();
        string? cursor = null;
        for (var i = 0; i < 6; i++)
        {
            var page = (await Call(HttpMethod.Get, "/api/v1/knowledge/documents?limit=1" + (cursor is null ? "" : "&cursor=" + cursor), admin)).Body;
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()));
            cursor = page.GetProperty("nextCursor").GetString();
            if (cursor is null) break;
        }
        Assert.Equal([Id(faq), Id(hindi), Id(all), Id(gt)], seen);
    }

    [Fact]
    public async Task The_list_can_be_asked_for_the_mapping_of_one_asset_and_honours_the_read_scope()
    {
        var (t, admin) = await NewWorld();
        var north = await NewManual(admin, "North", regions: ["north"]);
        var south = await NewManual(admin, "South", regions: ["south"]);
        var unmapped = await NewManual(admin, "Unmapped");
        var northDoc = (await CreateDoc(admin, north, "roadster")).GetProperty("id").GetGuid();
        await CreateDoc(admin, south, "roadster");

        async Task<Guid[]> For(string token, Guid asset)
        {
            var r = await Call(HttpMethod.Get, $"/api/v1/knowledge/documents?assetId={asset}&limit=1", token);
            Assert.Equal(HttpStatusCode.OK, r.Status);
            return r.Body.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToArray();
        }
        Assert.Equal([northDoc], await For(admin, north));
        Assert.Empty(await For(admin, unmapped));                                    // no mapping: an empty list, not an error
        Assert.Empty(await For(admin, Guid.NewGuid()));
        Assert.Equal([northDoc], await For(Dealer(t, "North"), north));
        Assert.Empty(await For(Dealer(t, "North"), south));                         // an asset the caller cannot read has no visible mapping
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Get, "/api/v1/knowledge/documents?assetId=not-a-guid", admin)).Status);
    }

    [Fact]
    public async Task A_deleted_assets_document_is_left_out_of_the_list()
    {
        var (_, admin) = await NewWorld();
        var live = await CreateDoc(admin, await NewManual(admin));
        var gone = await NewManual(admin);
        await CreateDoc(admin, gone, "spark");
        Sql($"UPDATE assets SET status = 'deleted' WHERE id = '{gone}'");
        var ids = (await Call(HttpMethod.Get, "/api/v1/knowledge/documents", admin)).Body.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToArray();
        Assert.Equal([live.GetProperty("id").GetGuid()], ids);
    }

    [Fact]
    public async Task A_document_can_be_changed_and_removed_without_touching_its_asset()
    {
        var (t, admin) = await NewWorld();
        var asset = await NewManual(admin);
        var id = (await CreateDoc(admin, asset, "roadster", "gt", 2023, 2024)).GetProperty("id").GetGuid();

        var put = await Call(HttpMethod.Put, $"/api/v1/knowledge/documents/{id}", admin,
            new { docType = "service", model = "voyager", variant = (string?)null, modelYearFrom = (int?)null, modelYearTo = (int?)null, language = "hi", theme = "ev" });
        Assert.Equal(HttpStatusCode.OK, put.Status);
        Assert.Equal("voyager", put.Body.GetProperty("model").GetString());
        Assert.Equal("service", put.Body.GetProperty("docType").GetString());
        Assert.Equal(JsonValueKind.Null, put.Body.GetProperty("variant").ValueKind);
        Assert.Equal(JsonValueKind.Null, put.Body.GetProperty("modelYearFrom").ValueKind);
        Assert.Equal("hi", put.Body.GetProperty("language").GetString());
        Assert.Equal(asset, put.Body.GetProperty("assetId").GetGuid());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Put, $"/api/v1/knowledge/documents/{id}", admin, new { docType = "nope", model = "voyager" })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Put, $"/api/v1/knowledge/documents/{Guid.NewGuid()}", admin, new { docType = "faq", model = "x" })).Status);

        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"/api/v1/knowledge/documents/{id}", admin)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/knowledge/documents/{id}", admin)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Delete, $"/api/v1/knowledge/documents/{id}", admin)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}", admin)).Status);          // the asset is untouched
        Assert.Equal(0, Count(t, "SELECT count(*) FROM knowledge_documents"));
        // and it can be mapped again
        await CreateDoc(admin, asset, "spark");
    }

    [Fact]
    public async Task Deleting_an_asset_row_takes_its_mapping_with_it()
    {
        var (t, admin) = await NewWorld();
        var asset = await NewManual(admin);
        await CreateDoc(admin, asset);
        Sql($"DELETE FROM assets WHERE id = '{asset}'");
        Assert.Equal(0, Count(t, "SELECT count(*) FROM knowledge_documents"));
    }

    // ---- chapters entered by hand ----------------------------------------------------------------------------------

    [Fact]
    public async Task Chapters_can_be_entered_by_hand_and_cleared_to_hand_the_document_back_to_automatic_indexing()
    {
        var (t, admin) = await NewWorld();
        var id = (await CreateDoc(admin, await NewManual(admin))).GetProperty("id").GetGuid();

        var put = await Call(HttpMethod.Put, $"/api/v1/knowledge/documents/{id}/chapters", admin, new
        {
            chapters = new object[]
            {
                new { title = "  Welcome ", anchor = "welcome", level = 1, pageFrom = 1, pageTo = 4 },
                new { title = "Charging", level = 2, pageFrom = 5 },
            },
        });
        Assert.Equal(HttpStatusCode.OK, put.Status);
        Assert.Equal("manual", put.Body.GetProperty("source").GetString());
        Assert.NotEqual(JsonValueKind.Null, put.Body.GetProperty("indexedAt").ValueKind);
        Assert.Equal(["Welcome", "Charging"], put.Body.GetProperty("chapters").EnumerateArray().Select(c => c.GetProperty("title").GetString()!).ToArray());

        var get = await Call(HttpMethod.Get, $"/api/v1/knowledge/documents/{id}/chapters", admin);
        Assert.Equal("manual", get.Body.GetProperty("source").GetString());
        var detail = (await Call(HttpMethod.Get, $"/api/v1/knowledge/documents/{id}", admin)).Body;
        Assert.Equal(2, detail.GetProperty("chapterCount").GetInt32());
        Assert.Equal("manual", detail.GetProperty("chaptersSource").GetString());
        Assert.Equal("welcome", detail.GetProperty("chapters")[0].GetProperty("anchor").GetString());
        Assert.Equal(4, detail.GetProperty("chapters")[0].GetProperty("pageTo").GetInt32());

        Sql("UPDATE outbox SET published_at = now() WHERE published_at IS NULL");
        var cleared = await Call(HttpMethod.Put, $"/api/v1/knowledge/documents/{id}/chapters", admin, new { chapters = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.OK, cleared.Status);
        Assert.Equal("none", cleared.Body.GetProperty("source").GetString());
        Assert.Equal(0, cleared.Body.GetProperty("chapters").GetArrayLength());
        Assert.Equal(1, Count(t, "SELECT count(*) FROM outbox WHERE event_type = 'knowledge.document.reindex_requested'"));
    }

    [Fact]
    public async Task Entered_chapters_are_checked_field_by_field()
    {
        var (_, admin) = await NewWorld();
        var id = (await CreateDoc(admin, await NewManual(admin))).GetProperty("id").GetGuid();
        async Task<JsonElement> Errors(object body)
        {
            var r = await Call(HttpMethod.Put, $"/api/v1/knowledge/documents/{id}/chapters", admin, body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status);
            return r.Body.GetProperty("errors");
        }
        Assert.True((await Errors(new { chapters = Enumerable.Range(0, 501).Select(i => new { title = $"c{i}", level = 1 }).ToArray() })).TryGetProperty("chapters", out _));
        Assert.True((await Errors(new { chapters = new[] { new { title = "  ", level = 1 } } })).TryGetProperty("chapters[0].title", out _));
        Assert.True((await Errors(new { chapters = new[] { new { title = new string('t', 301), level = 1 } } })).TryGetProperty("chapters[0].title", out _));
        Assert.True((await Errors(new { chapters = new[] { new { title = "ok", level = 0 } } })).TryGetProperty("chapters[0].level", out _));
        Assert.True((await Errors(new { chapters = new[] { new { title = "ok", level = 1, pageFrom = 5, pageTo = 4 } } })).TryGetProperty("chapters[0].pageTo", out _));
        Assert.True((await Errors(new { chapters = new[] { new { title = "ok", level = 1, pageFrom = 0 } } })).TryGetProperty("chapters[0].pageFrom", out _));

        // exactly the limits are fine
        var ok = await Call(HttpMethod.Put, $"/api/v1/knowledge/documents/{id}/chapters", admin,
            new { chapters = Enumerable.Range(0, 500).Select(i => new { title = i == 0 ? new string('t', 300) : $"c{i}", level = 1 }).ToArray() });
        Assert.Equal(HttpStatusCode.OK, ok.Status);
        Assert.Equal(500, ok.Body.GetProperty("chapters").GetArrayLength());
    }

    [Fact]
    public async Task A_reindex_is_asked_for_with_or_without_force_and_answers_accepted()
    {
        var (t, admin) = await NewWorld();
        var id = (await CreateDoc(admin, await NewManual(admin))).GetProperty("id").GetGuid();
        Sql("UPDATE outbox SET published_at = now() WHERE published_at IS NULL");

        var plain = await Call(HttpMethod.Post, $"/api/v1/knowledge/documents/{id}/reindex", admin);
        Assert.Equal(HttpStatusCode.Accepted, plain.Status);
        Assert.Equal(id, plain.Body.GetProperty("id").GetGuid());
        var forced = await Call(HttpMethod.Post, $"/api/v1/knowledge/documents/{id}/reindex?force=true", admin);
        Assert.Equal(HttpStatusCode.Accepted, forced.Status);

        using var c = Pg.RawAppConnection(t);
        using var cmd = new Npgsql.NpgsqlCommand("SELECT payload->>'Force' FROM outbox WHERE event_type = 'knowledge.document.reindex_requested' ORDER BY created_at, id", c);
        using var rd = cmd.ExecuteReader();
        var flags = new List<string>();
        while (rd.Read()) flags.Add(rd.GetString(0));
        Assert.Equal(["false", "true"], flags.Select(f => f.ToLowerInvariant()));
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"/api/v1/knowledge/documents/{Guid.NewGuid()}/reindex", admin)).Status);
    }

    // ---- who may do what -------------------------------------------------------------------------------------------

    /// <summary>Every route against every built-in role. Managing needs knowledge.manage (Admin, Librarian); reading needs assets.read; the rule list is open to whoever may edit assets too.</summary>
    [Fact]
    public async Task The_permission_matrix_holds_for_every_built_in_role_and_every_route()
    {
        var (t, admin) = await NewWorld();
        var asset = await NewManual(admin);
        var doc = (await CreateDoc(admin, asset)).GetProperty("id").GetGuid();
        var rule = (await CreateRule(admin, "matrix rule")).GetProperty("id").GetGuid();
        await CreateRule(admin, "other rule", "XYZ*", "other");

        var managers = new HashSet<string> { "Admin", "Librarian" };
        var ruleReaders = new HashSet<string> { "Admin", "Librarian", "Editor" };           // knowledge.manage, or assets.update
        var failures = new List<string>();

        async Task Check(string role, string token, string name, HttpMethod method, string path, object? body, bool allowed, HttpStatusCode ok)
        {
            var r = await Call(method, path, token, body);
            var expected = allowed ? ok : HttpStatusCode.Forbidden;
            if (r.Status != expected) failures.Add($"{role} {name}: expected {expected}, got {r.Status} {r.Body}");
        }

        foreach (var (role, token) in TokensByRole(t))
        {
            var manager = managers.Contains(role);
            await Check(role, token, "list documents", HttpMethod.Get, "/api/v1/knowledge/documents", null, true, HttpStatusCode.OK);
            await Check(role, token, "get document", HttpMethod.Get, $"/api/v1/knowledge/documents/{doc}", null, true, HttpStatusCode.OK);
            await Check(role, token, "get chapters", HttpMethod.Get, $"/api/v1/knowledge/documents/{doc}/chapters", null, true, HttpStatusCode.OK);
            await Check(role, token, "resolve", HttpMethod.Get, "/api/v1/knowledge/resolve?vin=" + Vin, null, true, HttpStatusCode.OK);
            await Check(role, token, "create document", HttpMethod.Post, "/api/v1/knowledge/documents", new { assetId = await NewManual(admin), docType = "faq", model = "m" }, manager, HttpStatusCode.Created);
            await Check(role, token, "update document", HttpMethod.Put, $"/api/v1/knowledge/documents/{doc}", new { docType = "owner_manual", model = "roadster" }, manager, HttpStatusCode.OK);
            await Check(role, token, "set chapters", HttpMethod.Put, $"/api/v1/knowledge/documents/{doc}/chapters", new { chapters = new[] { new { title = "A", level = 1 } } }, manager, HttpStatusCode.OK);
            await Check(role, token, "reindex", HttpMethod.Post, $"/api/v1/knowledge/documents/{doc}/reindex", null, manager, HttpStatusCode.Accepted);
            var disposable = (await CreateDoc(admin, await NewManual(admin), "spare")).GetProperty("id").GetGuid();
            await Check(role, token, "delete document", HttpMethod.Delete, $"/api/v1/knowledge/documents/{disposable}", null, manager, HttpStatusCode.NoContent);

            await Check(role, token, "list rules", HttpMethod.Get, "/api/v1/knowledge/vin-rules", null, ruleReaders.Contains(role), HttpStatusCode.OK);
            await Check(role, token, "create rule", HttpMethod.Post, "/api/v1/knowledge/vin-rules", new { name = "by " + role, pattern = "ABC*", model = "m" }, manager, HttpStatusCode.Created);
            await Check(role, token, "update rule", HttpMethod.Put, $"/api/v1/knowledge/vin-rules/{rule}", new { name = "matrix rule", pattern = "ACM*", model = "roadster" }, manager, HttpStatusCode.OK);
            var spare = (await CreateRule(admin, "spare " + role, "SPR*", "spare")).GetProperty("id").GetGuid();
            await Check(role, token, "delete rule", HttpMethod.Delete, $"/api/v1/knowledge/vin-rules/{spare}", null, manager, HttpStatusCode.NoContent);
            await Check(role, token, "test rules", HttpMethod.Post, "/api/v1/knowledge/vin-rules/test", new { vin = Vin }, manager, HttpStatusCode.OK);
            await Check(role, token, "import rules", HttpMethod.Post, "/api/v1/knowledge/vin-rules/import?dryRun=true", null, manager, HttpStatusCode.UnprocessableEntity);   // no body: reached the handler, which refuses it
        }
        Assert.Empty(failures);
    }

    [Fact]
    public async Task Unauthenticated_calls_are_refused()
    {
        var (_, _) = await NewWorld();
        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get, "/api/v1/knowledge/documents"), (HttpMethod.Get, "/api/v1/knowledge/resolve?vin=" + Vin), (HttpMethod.Get, "/api/v1/knowledge/vin-rules"),
            (HttpMethod.Post, "/api/v1/knowledge/vin-rules/test"),
        })
            Assert.Equal(HttpStatusCode.Unauthorized, (await Call(method, path, "not-a-token")).Status);
    }

    [Fact]
    public async Task Changing_a_document_needs_the_right_to_edit_its_asset_not_just_the_knowledge_permission()
    {
        var (t, admin) = await NewWorld();
        var asset = await NewManual(admin);
        var doc = (await CreateDoc(admin, asset)).GetProperty("id").GetGuid();

        // A custom role that holds knowledge.manage and may read assets, but not edit them.
        Assert.Equal(HttpStatusCode.Created, (await Call(HttpMethod.Post, "/api/v1/roles", admin, new { name = "Knowledge curator", permissions = new[] { "assets.read", "knowledge.manage" } })).Status);
        var token = As(t, "Knowledge curator");

        var create = await Call(HttpMethod.Post, "/api/v1/knowledge/documents", token, new { assetId = await NewManual(admin), docType = "faq", model = "m" });
        Assert.Equal(HttpStatusCode.Forbidden, create.Status);
        Assert.Contains("may not edit", create.Body.GetProperty("title").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Put, $"/api/v1/knowledge/documents/{doc}", token, new { docType = "faq", model = "m" })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Delete, $"/api/v1/knowledge/documents/{doc}", token)).Status);
        // chapters and re-indexing are knowledge work, not asset editing
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, $"/api/v1/knowledge/documents/{doc}/chapters", token, new { chapters = new[] { new { title = "A", level = 1 } } })).Status);
        Assert.Equal(HttpStatusCode.Accepted, (await Call(HttpMethod.Post, $"/api/v1/knowledge/documents/{doc}/reindex", token)).Status);
    }

    [Fact]
    public async Task A_document_on_an_asset_the_caller_cannot_read_does_not_exist_for_them()
    {
        var (t, admin) = await NewWorld();
        var draft = await NewManual(admin, "Draft manual", status: "draft");
        var north = await NewManual(admin, "North manual", regions: ["north"]);
        var south = await NewManual(admin, "South manual", regions: ["south"]);
        var draftDoc = (await CreateDoc(admin, draft)).GetProperty("id").GetGuid();
        var northDoc = (await CreateDoc(admin, north, "spark")).GetProperty("id").GetGuid();
        var southDoc = (await CreateDoc(admin, south, "voyager")).GetProperty("id").GetGuid();

        var viewer = As(t, "Viewer");
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/knowledge/documents/{draftDoc}", viewer)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/knowledge/documents/{draftDoc}/chapters", viewer)).Status);
        var listed = (await Call(HttpMethod.Get, "/api/v1/knowledge/documents", viewer)).Body.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToHashSet();
        Assert.DoesNotContain(draftDoc, listed);
        Assert.Contains(northDoc, listed);                                                 // a viewer is not limited by region

        var dealer = Dealer(t, "North");
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, $"/api/v1/knowledge/documents/{northDoc}", dealer)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/knowledge/documents/{southDoc}", dealer)).Status);
        var dealerList = (await Call(HttpMethod.Get, "/api/v1/knowledge/documents", dealer)).Body.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([northDoc], dealerList);
    }

    // ---- the audit trail ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_change_is_audited_through_the_normal_event_path()
    {
        var (t, admin) = await NewWorld();
        var asset = await NewManual(admin);
        var id = (await CreateDoc(admin, asset)).GetProperty("id").GetGuid();
        await Call(HttpMethod.Put, $"/api/v1/knowledge/documents/{id}", admin, new { docType = "faq", model = "roadster" });
        await Call(HttpMethod.Put, $"/api/v1/knowledge/documents/{id}/chapters", admin, new { chapters = new[] { new { title = "A", level = 1 } } });
        await Call(HttpMethod.Post, $"/api/v1/knowledge/documents/{id}/reindex?force=true", admin);
        var rule = (await CreateRule(admin, "audited")).GetProperty("id").GetGuid();
        await Call(HttpMethod.Put, $"/api/v1/knowledge/vin-rules/{rule}", admin, new { name = "audited", pattern = "ACN*", model = "roadster" });
        await CallText(HttpMethod.Post, "/api/v1/knowledge/vin-rules/import", admin, Csv("name,pattern,model", "imported one,ABC*,m", "imported two,ABD*,m"));
        await CallText(HttpMethod.Post, "/api/v1/knowledge/vin-rules/import?dryRun=true", admin, Csv("name,pattern,model", "dry run only,ABE*,m"));
        await Call(HttpMethod.Delete, $"/api/v1/knowledge/vin-rules/{rule}", admin);
        await Call(HttpMethod.Delete, $"/api/v1/knowledge/documents/{id}", admin);

        long Audited(string action) => Count(t, $"SELECT count(*) FROM audit_log WHERE action = '{action}'");
        Assert.Equal(1, Audited("knowledge.document.created"));
        Assert.Equal(1, Audited("knowledge.document.updated"));
        Assert.Equal(1, Audited("knowledge.chapters.indexed"));
        Assert.Equal(1, Audited("knowledge.document.reindex_requested"));
        Assert.Equal(1, Audited("knowledge.document.deleted"));
        Assert.Equal(1, Audited("knowledge.vin_rule.created"));                      // the import does not add one per rule
        Assert.Equal(1, Audited("knowledge.vin_rule.updated"));
        Assert.Equal(1, Audited("knowledge.vin_rule.deleted"));
        Assert.Equal(1, Audited("knowledge.vin_rules.imported"));                    // and the dry run wrote nothing
        Assert.Equal(1, Count(t, $"SELECT count(*) FROM audit_log WHERE action = 'knowledge.document.created' AND entity_type = 'knowledge_document' AND entity_id = '{id}'"));
        Assert.Equal(1, Count(t, $"SELECT count(*) FROM audit_log WHERE action = 'knowledge.vin_rule.created' AND entity_type = 'vin_rule' AND entity_id = '{rule}'"));
        Assert.Null(AuditChainBreak(t));
    }

    private long? AuditChainBreak(Guid tenant)
    {
        using var c = Pg.RawAppConnection(tenant);
        using var cmd = new Npgsql.NpgsqlCommand("SELECT audit_verify(@t)", c);
        cmd.Parameters.AddWithValue("t", tenant);
        return cmd.ExecuteScalar() is long l ? l : null;
    }

    [Fact]
    public async Task Reading_and_resolving_write_nothing()
    {
        var (t, admin) = await NewWorld();
        await CreateDoc(admin, await NewManual(admin));
        await CreateRule(admin);
        var before = Count(t, "SELECT count(*) FROM audit_log");
        var outboxBefore = Count(t, "SELECT count(*) FROM outbox");
        await Call(HttpMethod.Get, "/api/v1/knowledge/documents", admin);
        await Call(HttpMethod.Get, "/api/v1/knowledge/vin-rules", admin);
        await Call(HttpMethod.Post, "/api/v1/knowledge/vin-rules/test", admin, new { vin = Vin });
        Assert.Equal(HttpStatusCode.OK, (await Resolve(admin)).Status);
        Assert.Equal(before, Count(t, "SELECT count(*) FROM audit_log"));
        Assert.Equal(outboxBefore, Count(t, "SELECT count(*) FROM outbox"));
    }

    // ---- tenants --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Another_tenant_sees_none_of_it_through_the_api_and_the_database_enforces_the_same()
    {
        var (a, adminA) = await NewWorld();
        var (b, adminB) = await NewWorld();
        var asset = await NewManual(adminA);
        var doc = (await CreateDoc(adminA, asset)).GetProperty("id").GetGuid();
        var rule = (await CreateRule(adminA)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/knowledge/documents/{doc}", adminB)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/knowledge/documents/{doc}/chapters", adminB)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Put, $"/api/v1/knowledge/documents/{doc}", adminB, new { docType = "faq", model = "x" })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Delete, $"/api/v1/knowledge/documents/{doc}", adminB)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Put, $"/api/v1/knowledge/vin-rules/{rule}", adminB, new { name = "x", pattern = "ACM*", model = "m" })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Delete, $"/api/v1/knowledge/vin-rules/{rule}", adminB)).Status);
        Assert.Empty((await Call(HttpMethod.Get, "/api/v1/knowledge/documents", adminB)).Body.GetProperty("items").EnumerateArray());
        Assert.Empty((await Call(HttpMethod.Get, "/api/v1/knowledge/vin-rules", adminB)).Body.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await Resolve(adminB)).Status);
        var cross = await Call(HttpMethod.Post, "/api/v1/knowledge/documents", adminB, new { assetId = asset, docType = "faq", model = "x" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, cross.Status);                              // tenant A's asset is not found

        // the same name and the same document key are free in the other tenant
        Assert.NotEqual(Guid.Empty, (await CreateRule(adminB)).GetProperty("id").GetGuid());

        // Row-level security, below the application: B's session sees no row of A's, with no filter at all, and cannot write one for A.
        Assert.Equal(1, Count(a, "SELECT count(*) FROM knowledge_documents"));
        Assert.Equal(0, Count(b, "SELECT count(*) FROM knowledge_documents"));
        Assert.Equal(1, Count(a, "SELECT count(*) FROM vin_rules"));
        Assert.Equal(1, Count(b, "SELECT count(*) FROM vin_rules"));                                 // B's own
        using (var none = Pg.RawAppConnection(null))
        {
            foreach (var table in new[] { "knowledge_documents", "vin_rules" })
            {
                using var cmd = new Npgsql.NpgsqlCommand($"SELECT count(*) FROM {table}", none);
                Assert.Equal(0L, cmd.ExecuteScalar());
            }
        }
        using var asB = Pg.RawAppConnection(b);
        using var insert = new Npgsql.NpgsqlCommand(
            "INSERT INTO vin_rules (id, tenant_id, name, pattern, model, year_from_vin, priority, enabled, created_at, updated_at) " +
            "VALUES (gen_random_uuid(), @a, 'smuggled', 'ABC*', 'm', true, 0, true, now(), now())", asB);
        insert.Parameters.AddWithValue("a", a);
        Assert.Throws<Npgsql.PostgresException>(() => insert.ExecuteNonQuery());
        using var update = new Npgsql.NpgsqlCommand("UPDATE knowledge_documents SET model = 'hijacked'", asB);
        Assert.Equal(0, update.ExecuteNonQuery());
        Assert.Equal(1, Count(a, "SELECT count(*) FROM knowledge_documents WHERE model = 'roadster'"));
    }
}
