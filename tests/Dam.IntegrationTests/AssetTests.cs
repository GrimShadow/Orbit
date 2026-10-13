using System.Net;
using System.Text.Json;
using Npgsql;

namespace Dam.IntegrationTests;

/// <summary>Shared helpers for tests that create tenants with packs, assets and direct database changes.</summary>
public abstract class AssetTestBase(PostgresFixture pg) : ApiTestBase(pg)
{
    protected async Task<(Guid Tenant, string Admin, Guid Brand)> TenantWithPacks()
    {
        var t = await NewTenantAsync("core", "automotive-sample"); var admin = Admin(t);
        var brand = Id((await Call(HttpMethod.Post, "/api/v1/folders", admin, new { name = "Brand", inheritedMetadata = new { credit = "HQ", regions = new[] { "north" } } })).Body, "folder", "id");
        return (t, admin, brand);
    }

    protected static object Meta(string en = "A car") => new { description = new { en } };

    protected void Sql(string sql)
    {
        using var c = new NpgsqlConnection(Pg.OwnerConnection);
        c.Open();
        using var cmd = new NpgsqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }

    protected async Task<Guid> NewAsset(string token, Guid? folder, string type = "image", string title = "Hero", object? metadata = null, string[]? tags = null, bool allowIncomplete = false)
    {
        var r = await Call(HttpMethod.Post, "/api/v1/assets", token, new { folderId = folder, type, title, metadata = metadata ?? Meta(), tags, allowIncomplete });
        Assert.Equal(HttpStatusCode.Created, r.Status);
        return Id(r.Body, "id");
    }
}

[Collection("pg")]
public sealed class AssetTests(PostgresFixture pg) : AssetTestBase(pg)
{
    [Fact]
    public async Task A_new_asset_is_a_draft_that_inherits_folder_metadata_and_derives_terms_and_access_attributes()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var r = await Call(HttpMethod.Post, "/api/v1/assets", admin, new
        {
            folderId = brand, type = "image", title = "Roadster hero", tags = new[] { "  Hero ", "hero", "Summer" },
            metadata = new { description = new { en = "Roadster GT" }, channels = new[] { "signage" }, vehicle = new[] { "acme_roadster_gt_2026" } },
        });
        Assert.Equal(HttpStatusCode.Created, r.Status);
        var a = r.Body;
        Assert.Equal("draft", a.GetProperty("status").GetString());
        Assert.Equal("image", a.GetProperty("type").GetString());
        Assert.Equal(["hero", "summer"], Strings(a.GetProperty("tags"))); // trimmed, lower-cased, de-duplicated
        Assert.Equal("HQ", a.GetProperty("metadata").GetProperty("credit").GetString());                       // inherited from the folder
        Assert.Equal(["north"], Strings(a.GetProperty("metadata").GetProperty("regions")));                     // inherited
        Assert.Equal("Roadster GT", a.GetProperty("metadata").GetProperty("description").GetProperty("en").GetString());
        Assert.Equal(["north"], Strings(a.GetProperty("attributes").GetProperty("region")));                    // derived: drives access control
        Assert.Equal(["signage"], Strings(a.GetProperty("attributes").GetProperty("channel")));
        Assert.Equal(["acme_roadster_gt_2026", "north", "signage"], Strings(a.GetProperty("taxonomyTerms")));
        Assert.Equal(2, a.GetProperty("metadataSchemaVersion").GetInt32());
        Assert.NotEqual(Guid.Empty, a.GetProperty("ownerId").GetGuid());
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'asset.created'"));
        Assert.Equal(1, Count(t, "SELECT count(*) FROM outbox WHERE event_type = 'asset.created'"));

        // a value the caller supplies beats the folder's default
        var over = await Call(HttpMethod.Post, "/api/v1/assets", admin, new { folderId = brand, type = "image", title = "x", metadata = new { description = new { en = "x" }, credit = "Agency" } });
        Assert.Equal("Agency", over.Body.GetProperty("metadata").GetProperty("credit").GetString());
    }

    [Fact]
    public async Task Metadata_problems_are_reported_per_field_and_required_fields_can_be_deferred()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var schema = (await Call(HttpMethod.Get, "/api/v1/schemas/image", admin)).Body.GetProperty("fields").EnumerateArray()
            .Select(f => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(f.GetRawText())!).ToList();
        foreach (var f in schema.Where(f => f["name"].GetString() == "description")) f["required"] = JsonDocument.Parse("true").RootElement;
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, "/api/v1/schemas/image", admin, new { fields = schema })).Status);

        var missing = await Call(HttpMethod.Post, "/api/v1/assets", admin, new { folderId = brand, type = "image", title = "x", metadata = new { } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.Status);
        Assert.Equal("This field is required.", missing.Body.GetProperty("errors").GetProperty("metadata.description")[0].GetString());
        Assert.Equal(0, Count(t, "SELECT count(*) FROM assets")); // nothing was created

        var bad = await Call(HttpMethod.Post, "/api/v1/assets", admin, new { folderId = brand, type = "image", title = "x", metadata = new { description = new { en = "ok" }, vehicle = new[] { "acme_nope" }, colour = "red" } });
        var errors = bad.Body.GetProperty("errors");
        Assert.True(errors.TryGetProperty("metadata.vehicle", out _));
        Assert.True(errors.TryGetProperty("metadata.colour", out _));

        // "upload now, describe later": incomplete is allowed, but wrong values are still refused
        Assert.Equal(HttpStatusCode.Created, (await Call(HttpMethod.Post, "/api/v1/assets", admin, new { folderId = brand, type = "image", title = "x", metadata = new { }, allowIncomplete = true })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/api/v1/assets", admin, new { folderId = brand, type = "image", title = "x", metadata = new { vehicle = new[] { "acme_nope" } }, allowIncomplete = true })).Status);
    }

    [Fact]
    public async Task Basic_validation_of_type_title_tags_and_folder()
    {
        var (_, admin, brand) = await TenantWithPacks();
        async Task<HttpStatusCode> Post(object body) => (await Call(HttpMethod.Post, "/api/v1/assets", admin, body)).Status;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Post(new { type = "blueprint", title = "x" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Post(new { type = "image", title = "" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Post(new { type = "image", title = new string('x', 301) }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Post(new { type = "image", title = "x", folderId = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Post(new { type = "image", title = "x", tags = Enumerable.Range(0, 51).Select(i => $"t{i}").ToArray() }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Post(new { type = "image", title = "x", metadata = "not an object" }));
        Assert.Equal(HttpStatusCode.Created, await Post(new { type = "image", title = "no folder needed", folderId = (Guid?)null, metadata = Meta() }));
        _ = brand;
    }

    [Fact]
    public async Task Without_a_schema_an_asset_can_still_be_created_but_only_with_no_metadata()
    {
        var t = await NewTenantAsync(); var admin = Admin(t); // no template applied: no schemas
        Assert.Equal(HttpStatusCode.Created, (await Call(HttpMethod.Post, "/api/v1/assets", admin, new { type = "image", title = "bare" })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/api/v1/assets", admin, new { type = "image", title = "x", metadata = new { anything = 1 } })).Status);
    }

    [Fact]
    public async Task Who_may_create_assets_depends_on_role_and_on_the_folder()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var campaigns = Id((await Call(HttpMethod.Post, "/api/v1/folders", admin, new { name = "Campaigns" })).Body, "folder", "id");
        async Task<HttpStatusCode> Create(string token, Guid folder) => (await Call(HttpMethod.Post, "/api/v1/assets", token, new { folderId = folder, type = "image", title = "x", metadata = Meta() })).Status;

        Assert.Equal(HttpStatusCode.Forbidden, await Create(As(t, "Viewer"), brand));
        Assert.Equal(HttpStatusCode.Forbidden, await Create(As(t, "Approver"), brand));
        Assert.Equal(HttpStatusCode.Forbidden, await Create(ApiTestHost.TokenFor(t, Guid.NewGuid(), ["Dealer"], attributes: new() { ["region"] = ["North"] }), brand));
        foreach (var role in new[] { "Contributor", "Editor", "Librarian" }) Assert.Equal(HttpStatusCode.Created, await Create(As(t, role), brand));

        // a Viewer with an access rule for one folder subtree can create there and nowhere else
        var sub = Guid.NewGuid();
        var token = ApiTestHost.TokenFor(t, sub, ["Viewer"]);
        var userId = (await Call(HttpMethod.Get, "/api/v1/me", token)).Body.GetProperty("userId").GetGuid();
        await Call(HttpMethod.Post, "/api/v1/access-rules", admin, new { principalType = "user", principalId = userId, scopeType = "folder", scopeValue = "brand", permissions = new[] { "assets.create" } });
        Assert.Equal(HttpStatusCode.Created, await Create(token, brand));
        Assert.Equal(HttpStatusCode.Forbidden, await Create(token, campaigns));
    }

    // ---- reading is governed by the access policy ---------------------------------------------------------------

    [Fact]
    public async Task A_dealer_sees_only_published_assets_for_their_region_or_global_and_everyone_else_gets_a_404()
    {
        var (t, admin, brand) = await TenantWithPacks();
        Guid north = await NewAsset(admin, brand, metadata: new { description = new { en = "n" }, regions = new[] { "north" } });
        Guid south = await NewAsset(admin, brand, metadata: new { description = new { en = "s" }, regions = new[] { "south" } });
        Guid global = await NewAsset(admin, brand, metadata: new { description = new { en = "g" }, regions = new[] { "global" } });
        Guid draftNorth = await NewAsset(admin, brand, metadata: new { description = new { en = "d" }, regions = new[] { "north" } });
        Sql($"UPDATE assets SET status = 'published' WHERE id IN ('{north}','{south}','{global}')");

        string Dealer(string region) => ApiTestHost.TokenFor(t, Guid.NewGuid(), ["Dealer"], attributes: new() { ["region"] = [region] });
        async Task<HttpStatusCode> Get(string token, Guid id) => (await Call(HttpMethod.Get, $"/api/v1/assets/{id}", token)).Status;

        var n = Dealer("North");
        Assert.Equal(HttpStatusCode.OK, await Get(n, north));
        Assert.Equal(HttpStatusCode.OK, await Get(n, global));
        Assert.Equal(HttpStatusCode.NotFound, await Get(n, south));       // another region
        Assert.Equal(HttpStatusCode.NotFound, await Get(n, draftNorth));  // not published
        var s = Dealer("South");
        Assert.Equal(HttpStatusCode.OK, await Get(s, south));
        Assert.Equal(HttpStatusCode.NotFound, await Get(s, north));
        // a dealer without a region attribute is locked out entirely
        var none = ApiTestHost.TokenFor(t, Guid.NewGuid(), ["Dealer"]);
        Assert.Equal(HttpStatusCode.NotFound, await Get(none, global));

        // the list applies the same rules
        var list = (await Call(HttpMethod.Get, "/api/v1/assets", n)).Body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Order().ToList();
        Assert.Equal(new[] { north, global }.Order().ToList(), list);
        Assert.Equal(4, (await Call(HttpMethod.Get, "/api/v1/assets", admin)).Body.GetProperty("items").GetArrayLength());
        Assert.Empty((await Call(HttpMethod.Get, "/api/v1/assets", none)).Body.GetProperty("items").EnumerateArray());
        // tenants never see each other's assets
        var other = await NewTenantAsync("core");
        Assert.Equal(HttpStatusCode.NotFound, await Get(Admin(other), north));
        Assert.Empty((await Call(HttpMethod.Get, "/api/v1/assets", Admin(other))).Body.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Contributors_see_their_own_drafts_but_not_other_peoples_and_can_change_only_their_own()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var alice = ApiTestHost.TokenFor(t, Guid.NewGuid(), ["Contributor"]);
        var bob = ApiTestHost.TokenFor(t, Guid.NewGuid(), ["Contributor"]);
        var mine = await NewAsset(alice, brand, title: "Alice's");
        var theirs = await NewAsset(bob, brand, title: "Bob's");

        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, $"/api/v1/assets/{mine}", alice)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/assets/{theirs}", alice)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Patch, $"/api/v1/assets/{mine}", alice, new { title = "Renamed" })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Patch, $"/api/v1/assets/{theirs}", alice, new { title = "Hijack" })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Delete, $"/api/v1/assets/{mine}", alice)).Status); // no delete right at all
        var visible = (await Call(HttpMethod.Get, "/api/v1/assets", alice)).Body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([mine], visible);
        Assert.Equal(2, (await Call(HttpMethod.Get, "/api/v1/assets", admin)).Body.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Listing_filters_by_folder_type_and_status_and_pages_with_a_cursor()
    {
        var (_, admin, brand) = await TenantWithPacks();
        var trail = Id((await Call(HttpMethod.Post, "/api/v1/folders", admin, new { parentId = brand, name = "Trail" })).Body, "folder", "id");
        var other = Id((await Call(HttpMethod.Post, "/api/v1/folders", admin, new { name = "Elsewhere" })).Body, "folder", "id");
        for (var i = 0; i < 5; i++) await NewAsset(admin, brand, title: $"brand {i}");
        for (var i = 0; i < 3; i++) await NewAsset(admin, trail, title: $"trail {i}");
        await NewAsset(admin, other, title: "elsewhere");
        await NewAsset(admin, brand, "video", "a video", new { description = new { en = "v" } });

        var seen = new List<string>(); string? cursor = null; var pages = 0;
        do
        {
            var page = (await Call(HttpMethod.Get, $"/api/v1/assets?folderId={brand}&limit=4" + (cursor is null ? "" : $"&cursor={cursor}"), admin)).Body;
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!));
            cursor = page.GetProperty("nextCursor").GetString(); pages++;
        } while (cursor is not null && pages < 10);
        Assert.Equal(9, seen.Count);            // 5 + 3 in the sub-folder + 1 video, and not "elsewhere"
        Assert.Equal(3, pages);
        Assert.Equal(seen.Distinct().Count(), seen.Count);
        Assert.Equal(seen.OrderDescending().ToList(), seen);          // newest first

        Assert.Equal(5, (await Call(HttpMethod.Get, $"/api/v1/assets?folderId={brand}&includeSubfolders=false&type=image", admin)).Body.GetProperty("items").GetArrayLength());
        Assert.Equal(1, (await Call(HttpMethod.Get, "/api/v1/assets?type=video", admin)).Body.GetProperty("items").GetArrayLength());
        Assert.Equal(0, (await Call(HttpMethod.Get, "/api/v1/assets?status=published", admin)).Body.GetProperty("items").GetArrayLength());
        Assert.Equal(10, (await Call(HttpMethod.Get, "/api/v1/assets?status=draft&limit=200", admin)).Body.GetProperty("items").GetArrayLength());
    }

    // ---- change, move, delete ----------------------------------------------------------------------------------

    [Fact]
    public async Task Assets_can_be_edited_moved_and_soft_deleted()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var other = Id((await Call(HttpMethod.Post, "/api/v1/folders", admin, new { name = "Other" })).Body, "folder", "id");
        var id = await NewAsset(admin, brand, metadata: new { description = new { en = "old" }, regions = new[] { "north" } });

        var upd = await Call(HttpMethod.Patch, $"/api/v1/assets/{id}", admin, new { title = "New title", tags = new[] { "b", "A" }, metadata = new { description = new { en = "new" }, regions = new[] { "south" } } });
        Assert.Equal(HttpStatusCode.OK, upd.Status);
        Assert.Equal("New title", upd.Body.GetProperty("title").GetString());
        Assert.Equal(["a", "b"], Strings(upd.Body.GetProperty("tags")));
        Assert.Equal(["south"], Strings(upd.Body.GetProperty("attributes").GetProperty("region"))); // access attributes follow the metadata
        Assert.False(upd.Body.GetProperty("metadata").TryGetProperty("credit", out _));            // full replacement: folder defaults not re-applied
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Patch, $"/api/v1/assets/{id}", admin, new { metadata = new { vehicle = new[] { "acme_nope" } } })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Patch, $"/api/v1/assets/{id}", admin, new { title = "" })).Status);

        var moved = await Call(HttpMethod.Post, $"/api/v1/assets/{id}/move", admin, new { folderId = other });
        Assert.Equal(other, moved.Body.GetProperty("folderId").GetGuid());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, $"/api/v1/assets/{id}/move", admin, new { folderId = Guid.NewGuid() })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, $"/api/v1/assets/{id}/move", As(t, "Editor"), new { folderId = brand })).Status); // no assets.move

        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"/api/v1/assets/{id}", admin)).Status);
        var gone = await Call(HttpMethod.Get, $"/api/v1/assets/{id}", admin);
        Assert.Equal("deleted", gone.Body.GetProperty("status").GetString()); // still readable by those who may see non-published assets
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/assets/{id}", ApiTestHost.TokenFor(t, Guid.NewGuid(), ["Viewer"]))).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Patch, $"/api/v1/assets/{id}", admin, new { title = "x" })).Status);
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'asset.status.changed'"));
    }

    [Fact]
    public async Task A_folder_or_term_in_use_by_assets_cannot_be_deleted()
    {
        var (t, admin, brand) = await TenantWithPacks();
        await NewAsset(admin, brand, metadata: new { description = new { en = "x" }, vehicle = new[] { "acme_roadster_gt_2026" } });

        var blocked = await Call(HttpMethod.Delete, $"/api/v1/folders/{brand}", admin);
        Assert.Equal(HttpStatusCode.Conflict, blocked.Status);
        Assert.Contains("assets", blocked.Body.GetProperty("title").GetString());

        var vocab = (await Call(HttpMethod.Get, "/api/v1/vocabularies", admin)).Body.EnumerateArray().First(v => v.GetProperty("key").GetString() == "vehicle").GetProperty("id").GetGuid();
        var term = (await Call(HttpMethod.Get, $"/api/v1/vocabularies/{vocab}/terms", admin)).Body.EnumerateArray().First(x => x.GetProperty("code").GetString() == "acme_roadster_gt_2026").GetProperty("id").GetGuid();
        var termBlocked = await Call(HttpMethod.Delete, $"/api/v1/terms/{term}", admin);
        Assert.Equal(HttpStatusCode.Conflict, termBlocked.Status);
        Assert.Contains("Deprecate", termBlocked.Body.GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Patch, $"/api/v1/terms/{term}", admin, new { status = "deprecated" })).Status); // the safe alternative
        _ = t;
    }
}
