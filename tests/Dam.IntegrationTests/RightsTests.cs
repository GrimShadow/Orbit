using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace Dam.IntegrationTests;

/// <summary>Rights and licences over the real HTTP API and database (ADR-0033): records, assignment and the attributes it derives, the check, the publish guard, documents, scope and isolation.</summary>
[Collection("pg")]
public sealed class RightsTests(PostgresFixture pg) : AssetTestBase(pg)
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);
    private static readonly HttpClient S3Http = new();

    private static object Body(string name = "Acme stock licence", string licenceType = "licensed", string[]? channels = null, string[]? regions = null, DateOnly? validFrom = null,
        DateOnly? validTo = null, bool watermarkRequired = false, string? licensor = "Acme Photos", string? notes = "Internal note") =>
        new { name, licenceType, licensor, channels = channels ?? [], regions = regions ?? [], validFrom, validTo, watermarkRequired, notes };

    private async Task<Guid> NewRights(string token, string name = "Acme stock licence", string[]? channels = null, string[]? regions = null, DateOnly? validFrom = null,
        DateOnly? validTo = null, bool watermarkRequired = false)
    {
        var r = await Call(HttpMethod.Post, "/api/v1/rights", token, Body(name, channels: channels, regions: regions, validFrom: validFrom, validTo: validTo, watermarkRequired: watermarkRequired));
        Assert.True(r.Status == HttpStatusCode.Created, r.Body.ToString());
        return r.Body.GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> GetAsset(string token, Guid asset) => (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}", token)).Body;

    private static string[] Attr(JsonElement asset, string dimension) =>
        asset.GetProperty("attributes").TryGetProperty(dimension, out var v) ? Strings(v) : [];

    private async Task<Guid> ChannelAsset(string token, string[] channels, string[]? regions = null, Guid? folder = null, string title = "Acme hero") =>
        await NewAsset(token, folder, title: title, metadata: new { description = new { en = "A car" }, channels, regions = regions ?? [] });

    private async Task Publish(string token, Guid asset)
    {
        foreach (var action in new[] { "submit", "approve", "publish" })
        {
            var r = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/transitions/{action}", token, new { });
            Assert.True(r.Status == HttpStatusCode.OK, $"{action}: {r.Body}");
        }
    }

    // ---- records -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_rights_record_can_be_created_read_listed_updated_and_deleted()
    {
        var (t, admin, _) = await TenantWithPacks();
        var from = Today.AddDays(-5);
        var to = Today.AddDays(100);
        var create = await Call(HttpMethod.Post, "/api/v1/rights", admin, Body(channels: ["web", "signage"], regions: ["north"], validFrom: from, validTo: to, watermarkRequired: true));
        Assert.Equal(HttpStatusCode.Created, create.Status);
        var r = create.Body;
        var id = r.GetProperty("id").GetGuid();
        Assert.Equal(("Acme stock licence", "licensed", "Acme Photos", "Internal note", true), (r.GetProperty("name").GetString(), r.GetProperty("licenceType").GetString(),
            r.GetProperty("licensor").GetString(), r.GetProperty("notes").GetString(), r.GetProperty("watermarkRequired").GetBoolean()));
        Assert.Equal(["signage", "web"], Strings(r.GetProperty("channels")));   // sorted, stored with the vocabulary's own codes
        Assert.Equal(["north"], Strings(r.GetProperty("regions")));
        Assert.Equal((from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd")), (r.GetProperty("validFrom").GetString(), r.GetProperty("validTo").GetString()));
        Assert.Equal(("valid", 100, 0, 0), (r.GetProperty("status").GetString(), r.GetProperty("daysRemaining").GetInt32(), r.GetProperty("assetCount").GetInt32(), r.GetProperty("documentCount").GetInt32()));

        var got = await Call(HttpMethod.Get, $"/api/v1/rights/{id}", admin);
        Assert.Equal(HttpStatusCode.OK, got.Status);
        Assert.Equal("Acme stock licence", got.Body.GetProperty("name").GetString());

        var updated = await Call(HttpMethod.Put, $"/api/v1/rights/{id}", admin, Body("Acme stock licence 2027", "royalty_free", ["web"], [], null, null, false, "Acme", ""));
        Assert.Equal(HttpStatusCode.OK, updated.Status);
        Assert.Equal(("Acme stock licence 2027", "royalty_free", false), (updated.Body.GetProperty("name").GetString(), updated.Body.GetProperty("licenceType").GetString(), updated.Body.GetProperty("watermarkRequired").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, updated.Body.GetProperty("validTo").ValueKind);
        Assert.Equal(JsonValueKind.Null, updated.Body.GetProperty("daysRemaining").ValueKind);
        Assert.Empty(updated.Body.GetProperty("regions").EnumerateArray());

        var list = await Call(HttpMethod.Get, "/api/v1/rights", admin);
        Assert.Equal(id, Assert.Single(list.Body.EnumerateArray()).GetProperty("id").GetGuid());

        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"/api/v1/rights/{id}", admin)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/rights/{id}", admin)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Put, $"/api/v1/rights/{id}", admin, Body())).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Delete, $"/api/v1/rights/{id}", admin)).Status);

        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'rights.created'"));
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'rights.updated'"));
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'rights.deleted'"));
    }

    [Fact]
    public async Task The_list_shows_status_and_counts_and_can_be_narrowed_by_status_and_name()
    {
        var (_, admin, _) = await TenantWithPacks();
        await NewRights(admin, "Open ended");
        await NewRights(admin, "Ends next week", validTo: Today.AddDays(7));
        await NewRights(admin, "Ended last week", validTo: Today.AddDays(-7));
        await NewRights(admin, "Starts next week", validFrom: Today.AddDays(7));
        await NewRights(admin, "Ends next year", validTo: Today.AddDays(365));

        var all = (await Call(HttpMethod.Get, "/api/v1/rights", admin)).Body.EnumerateArray().ToDictionary(x => x.GetProperty("name").GetString()!, x => x.GetProperty("status").GetString());
        Assert.Equal(("valid", "expiring", "expired", "not_yet_valid", "valid"), (all["Open ended"], all["Ends next week"], all["Ended last week"], all["Starts next week"], all["Ends next year"]));

        async Task<string[]> Names(string query) => (await Call(HttpMethod.Get, "/api/v1/rights" + query, admin)).Body.EnumerateArray().Select(x => x.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(["Ended last week"], await Names("?status=expired"));
        Assert.Equal(["Ends next week"], await Names("?status=expiring"));
        Assert.Equal(["Starts next week"], await Names("?status=not_yet_valid"));
        Assert.Equal(["Ends next week", "Ends next year"], await Names("?q=ends"));   // by name, case-insensitive substring
        Assert.Equal(["Ends next week"], await Names("?status=expiring&q=NEXT"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Get, "/api/v1/rights?status=old", admin)).Status);
    }

    [Fact]
    public async Task Problems_come_back_as_422_keyed_by_field()
    {
        var (_, admin, _) = await TenantWithPacks();
        var bad = await Call(HttpMethod.Post, "/api/v1/rights", admin, Body(name: "", licenceType: "free", validFrom: Today, validTo: Today.AddDays(-1), channels: ["tv"], regions: ["mars"]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.Status);
        var errors = bad.Body.GetProperty("errors");
        foreach (var field in new[] { "name", "licenceType", "validTo", "channels", "regions" }) Assert.True(errors.TryGetProperty(field, out _), field);
        Assert.Contains("Known:", errors.GetProperty("channels")[0].GetString(), StringComparison.Ordinal);   // says what the tenant's vocabulary allows
        Assert.Contains("signage", errors.GetProperty("channels")[0].GetString(), StringComparison.Ordinal);

        Assert.Contains("channels", (await Call(HttpMethod.Post, "/api/v1/rights", admin, Body(channels: ["global"]))).Body.GetProperty("errors").EnumerateObject().Select(p => p.Name));
        Assert.Contains("name", (await Call(HttpMethod.Post, "/api/v1/rights", admin, Body(name: new string('x', 201)))).Body.GetProperty("errors").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Channel_and_region_values_are_the_tenants_vocabulary_codes_in_the_vocabularys_own_spelling()
    {
        var (_, admin, _) = await TenantWithPacks();
        var id = await NewRights(admin, channels: ["WEB", " Signage "], regions: ["North"]);
        var r = (await Call(HttpMethod.Get, $"/api/v1/rights/{id}", admin)).Body;
        Assert.Equal(["signage", "web"], Strings(r.GetProperty("channels")));
        Assert.Equal(["north"], Strings(r.GetProperty("regions")));
    }

    [Fact]
    public async Task A_tenant_without_vocabularies_accepts_lower_case_slugs()
    {
        var t = await NewTenantAsync();   // no template pack: no channel or region vocabulary
        var admin = Admin(t);
        var ok = await Call(HttpMethod.Post, "/api/v1/rights", admin, Body(channels: ["Kiosk", "in-store_2"], regions: ["north-east"]));
        Assert.Equal(HttpStatusCode.Created, ok.Status);
        Assert.Equal(["in-store_2", "kiosk"], Strings(ok.Body.GetProperty("channels")));
        var bad = await Call(HttpMethod.Post, "/api/v1/rights", admin, Body(name: "Other", channels: ["two words"], regions: ["a/b"]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.Status);
        Assert.True(bad.Body.GetProperty("errors").TryGetProperty("channels", out _));
        Assert.True(bad.Body.GetProperty("errors").TryGetProperty("regions", out _));
    }

    [Fact]
    public async Task Names_are_unique_per_tenant_ignoring_case_but_not_across_tenants()
    {
        var (_, admin, _) = await TenantWithPacks();
        var first = await NewRights(admin, "Acme Licence");
        var dup = await Call(HttpMethod.Post, "/api/v1/rights", admin, Body("ACME LICENCE"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, dup.Status);
        Assert.True(dup.Body.GetProperty("errors").TryGetProperty("name", out _));

        var second = await NewRights(admin, "Another");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Put, $"/api/v1/rights/{second}", admin, Body("acme licence"))).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, $"/api/v1/rights/{first}", admin, Body("Acme Licence", validTo: Today.AddDays(5)))).Status);   // keeping its own name is fine

        var (_, otherAdmin, _) = await TenantWithPacks();
        Assert.Equal(HttpStatusCode.Created, (await Call(HttpMethod.Post, "/api/v1/rights", otherAdmin, Body("Acme Licence"))).Status);
    }

    [Fact]
    public async Task Only_those_with_rights_manage_can_use_the_records_and_the_built_in_holders_are_admin_and_librarian()
    {
        var (t, admin, _) = await TenantWithPacks();
        var id = await NewRights(admin);
        foreach (var role in new[] { "Librarian" })
        {
            Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, "/api/v1/rights", As(t, role))).Status);
            Assert.Equal(HttpStatusCode.Created, (await Call(HttpMethod.Post, "/api/v1/rights", As(t, role), Body("By " + role))).Status);
        }
        foreach (var role in new[] { "Editor", "Approver", "Contributor", "Viewer", "Dealer", "ApiClient" })
        {
            var token = As(t, role);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, "/api/v1/rights", token)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, $"/api/v1/rights/{id}", token)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "/api/v1/rights", token, Body("By " + role))).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Put, $"/api/v1/rights/{id}", token, Body())).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Delete, $"/api/v1/rights/{id}", token)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, $"/api/v1/rights/{id}/assign", token, new { assetIds = new[] { Guid.NewGuid() } })).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, $"/api/v1/rights/{id}/documents", token)).Status);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(HttpMethod.Get, "/api/v1/rights", "not-a-token")).Status);
    }

    [Fact]
    public async Task A_record_that_is_still_assigned_cannot_be_deleted_and_the_answer_says_how_many_assets_use_it()
    {
        var (_, admin, folder) = await TenantWithPacks();
        var id = await NewRights(admin);
        var a = await ChannelAsset(admin, ["web"], folder: folder);
        var b = await ChannelAsset(admin, ["web"], folder: folder, title: "Second");
        foreach (var asset in new[] { a, b }) Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, $"/api/v1/assets/{asset}/rights", admin, new { rightsId = id })).Status);

        var refused = await Call(HttpMethod.Delete, $"/api/v1/rights/{id}", admin);
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Equal(2, refused.Body.GetProperty("assetCount").GetInt32());
        Assert.Contains("2 asset", refused.Body.GetProperty("title").GetString(), StringComparison.Ordinal);
        Assert.Equal(2, (await Call(HttpMethod.Get, $"/api/v1/rights/{id}", admin)).Body.GetProperty("assetCount").GetInt32());

        foreach (var asset in new[] { a, b }) await Call(HttpMethod.Put, $"/api/v1/assets/{asset}/rights", admin, new { rightsId = (Guid?)null });
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"/api/v1/rights/{id}", admin)).Status);
    }

    [Fact]
    public async Task A_deleted_asset_does_not_keep_a_record_alive()
    {
        var (_, admin, folder) = await TenantWithPacks();
        var id = await NewRights(admin);
        var asset = await ChannelAsset(admin, ["web"], folder: folder);
        await Call(HttpMethod.Put, $"/api/v1/assets/{asset}/rights", admin, new { rightsId = id });
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"/api/v1/assets/{asset}", admin)).Status);
        Assert.Equal(0, (await Call(HttpMethod.Get, $"/api/v1/rights/{id}", admin)).Body.GetProperty("assetCount").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"/api/v1/rights/{id}", admin)).Status);
    }

    // ---- assignment and the attributes it derives -----------------------------------------------------------------

    [Fact]
    public async Task Assigning_rights_narrows_the_assets_channel_and_region_to_the_intersection_and_clearing_restores_them()
    {
        var (t, admin, folder) = await TenantWithPacks();
        var asset = await ChannelAsset(admin, ["web", "signage"], ["north", "south"], folder);
        Assert.Equal(["signage", "web"], Attr(await GetAsset(admin, asset), "channel").Order());
        var id = await NewRights(admin, channels: ["web", "print"], regions: ["north"]);

        var set = await Call(HttpMethod.Put, $"/api/v1/assets/{asset}/rights", admin, new { rightsId = id });
        Assert.Equal(HttpStatusCode.OK, set.Status);
        Assert.Equal(asset, set.Body.GetProperty("assetId").GetGuid());
        Assert.Equal(id, set.Body.GetProperty("rights").GetProperty("id").GetGuid());

        var narrowed = await GetAsset(admin, asset);
        Assert.Equal(["web"], Attr(narrowed, "channel"));
        Assert.Equal(["north"], Attr(narrowed, "region"));
        Assert.Equal(id, narrowed.GetProperty("rightsId").GetGuid());
        Assert.Equal(1, Count(t, $"SELECT count(*) FROM audit_log WHERE action = 'asset.rights.changed' AND entity_id = '{asset}'"));

        // the same assignment again is a no-op and says nothing more in the audit trail
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, $"/api/v1/assets/{asset}/rights", admin, new { rightsId = id })).Status);
        Assert.Equal(1, Count(t, $"SELECT count(*) FROM audit_log WHERE action = 'asset.rights.changed' AND entity_id = '{asset}'"));

        var cleared = await Call(HttpMethod.Put, $"/api/v1/assets/{asset}/rights", admin, new { rightsId = (Guid?)null });
        Assert.Equal(HttpStatusCode.OK, cleared.Status);
        Assert.Equal(JsonValueKind.Null, cleared.Body.GetProperty("rights").ValueKind);
        var restored = await GetAsset(admin, asset);
        Assert.Equal(["signage", "web"], Attr(restored, "channel").Order());
        Assert.Equal(["north", "south"], Attr(restored, "region").Order());
        Assert.Equal(JsonValueKind.Null, restored.GetProperty("rightsId").ValueKind);
    }

    [Fact]
    public async Task Rights_that_share_nothing_with_the_metadata_restrict_the_asset_to_nothing()
    {
        var (_, admin, folder) = await TenantWithPacks();
        var asset = await ChannelAsset(admin, ["web"], folder: folder);
        var id = await NewRights(admin, channels: ["print"]);
        await Call(HttpMethod.Put, $"/api/v1/assets/{asset}/rights", admin, new { rightsId = id });
        Assert.Equal(["~restricted"], Attr(await GetAsset(admin, asset), "channel"));

        var check = (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/rights/check?channel=web", admin)).Body;
        Assert.False(check.GetProperty("allowed").GetBoolean());
        Assert.True(check.GetProperty("noChannelsAllowed").GetBoolean());
        Assert.Empty(check.GetProperty("effectiveChannels").EnumerateArray());
    }

    [Fact]
    public async Task A_global_asset_takes_the_rights_channels_and_an_unrestricted_licence_changes_nothing()
    {
        var (_, admin, folder) = await TenantWithPacks();
        var open = await ChannelAsset(admin, [], [], folder);
        Assert.Empty(Attr(await GetAsset(admin, open), "channel"));
        await Call(HttpMethod.Put, $"/api/v1/assets/{open}/rights", admin, new { rightsId = await NewRights(admin, "Web only", channels: ["web"]) });
        Assert.Equal(["web"], Attr(await GetAsset(admin, open), "channel"));

        var free = await ChannelAsset(admin, ["signage"], [], folder, "Signage asset");
        await Call(HttpMethod.Put, $"/api/v1/assets/{free}/rights", admin, new { rightsId = await NewRights(admin, "Anywhere") });
        Assert.Equal(["signage"], Attr(await GetAsset(admin, free), "channel"));
    }

    [Fact]
    public async Task Updating_a_record_re_derives_every_assigned_asset()
    {
        var (t, admin, folder) = await TenantWithPacks();
        var id = await NewRights(admin, channels: ["web"]);
        var a = await ChannelAsset(admin, ["web", "signage"], folder: folder);
        var b = await ChannelAsset(admin, ["signage", "print"], folder: folder, title: "Second");
        var c = await ChannelAsset(admin, ["web", "signage"], folder: folder, title: "Unassigned");
        foreach (var asset in new[] { a, b }) await Call(HttpMethod.Put, $"/api/v1/assets/{asset}/rights", admin, new { rightsId = id });
        Assert.Equal(["web"], Attr(await GetAsset(admin, a), "channel"));
        Assert.Equal(["~restricted"], Attr(await GetAsset(admin, b), "channel"));

        var updated = await Call(HttpMethod.Put, $"/api/v1/rights/{id}", admin, Body(channels: ["signage", "web"]));
        Assert.Equal(HttpStatusCode.OK, updated.Status);
        Assert.Equal(0, updated.Body.GetProperty("pendingAssets").GetInt32());
        Assert.Equal(["signage", "web"], Attr(await GetAsset(admin, a), "channel").Order());
        Assert.Equal(["signage"], Attr(await GetAsset(admin, b), "channel"));
        Assert.Equal(["signage", "web"], Attr(await GetAsset(admin, c), "channel").Order());   // never assigned: untouched
        Assert.Equal(2, Count(t, $"SELECT count(*) FROM audit_log WHERE action = 'asset.rights.changed' AND entity_id IN ('{a}', '{b}') AND after->>'PreviousRightsId' = '{id}'"));

        // an update that does not touch channels or regions re-derives nothing
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, $"/api/v1/rights/{id}", admin, Body(channels: ["signage", "web"], validTo: Today.AddDays(90)))).Status);
        Assert.Equal(2, Count(t, $"SELECT count(*) FROM audit_log WHERE action = 'asset.rights.changed' AND entity_id IN ('{a}', '{b}') AND after->>'PreviousRightsId' = '{id}'"));

        // widening to every channel gives each asset its own metadata attributes back
        await Call(HttpMethod.Put, $"/api/v1/rights/{id}", admin, Body(channels: []));
        Assert.Equal(["signage", "web"], Attr(await GetAsset(admin, a), "channel").Order());
        Assert.Equal(["print", "signage"], Attr(await GetAsset(admin, b), "channel").Order());
    }

    [Fact]
    public async Task Editing_the_metadata_of_an_asset_with_rights_keeps_the_licence_narrowing()
    {
        var (_, admin, folder) = await TenantWithPacks();
        var asset = await ChannelAsset(admin, ["web", "signage"], folder: folder);
        await Call(HttpMethod.Put, $"/api/v1/assets/{asset}/rights", admin, new { rightsId = await NewRights(admin, channels: ["web"]) });

        var edit = await Call(HttpMethod.Patch, $"/api/v1/assets/{asset}", admin, new { metadata = new { description = new { en = "A car" }, channels = new[] { "web", "mobile" } } });
        Assert.Equal(HttpStatusCode.OK, edit.Status);
        Assert.Equal(["web"], Attr(await GetAsset(admin, asset), "channel"));

        await Call(HttpMethod.Patch, $"/api/v1/assets/{asset}", admin, new { metadata = new { description = new { en = "A car" }, channels = new[] { "signage" } } });
        Assert.Equal(["~restricted"], Attr(await GetAsset(admin, asset), "channel"));
    }

    [Fact]
    public async Task Assigning_needs_rights_manage_on_the_asset_and_an_asset_the_caller_cannot_see_is_not_found()
    {
        var (t, admin, folder) = await TenantWithPacks();
        var id = await NewRights(admin);
        var draft = await ChannelAsset(admin, ["web"], folder: folder);

        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Put, $"/api/v1/assets/{draft}/rights", As(t, "Editor"), new { rightsId = id })).Status);   // can read the draft, may not manage rights
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Put, $"/api/v1/assets/{draft}/rights", As(t, "Viewer"), new { rightsId = id })).Status);      // cannot even see the draft
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Put, $"/api/v1/assets/{Guid.NewGuid()}/rights", admin, new { rightsId = id })).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, $"/api/v1/assets/{draft}/rights", As(t, "Librarian"), new { rightsId = id })).Status);

        var unknown = await Call(HttpMethod.Put, $"/api/v1/assets/{draft}/rights", admin, new { rightsId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.Status);
        Assert.True(unknown.Body.GetProperty("errors").TryGetProperty("rightsId", out _));
    }

    [Fact]
    public async Task A_dealer_for_another_region_cannot_see_the_rights_of_an_asset_outside_their_reach()
    {
        var (t, admin, folder) = await TenantWithPacks();
        var id = await NewRights(admin);
        var asset = await ChannelAsset(admin, ["web"], ["north"], folder);
        // A Dealer for the south cannot see a north asset at all, even a published one: not found, whatever they are allowed elsewhere.
        await Publish(admin, asset);
        var southDealer = ApiTestHost.TokenFor(t, Guid.NewGuid(), roles: ["Dealer"], attributes: new() { ["region"] = ["south"] });
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Put, $"/api/v1/assets/{asset}/rights", southDealer, new { rightsId = id })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/rights", southDealer)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/rights/check?channel=web", southDealer)).Status);
    }

    [Fact]
    public async Task A_deleted_or_blocked_asset_cannot_be_assigned_rights()
    {
        var (_, admin, folder) = await TenantWithPacks();
        var id = await NewRights(admin);
        var blocked = await ChannelAsset(admin, ["web"], folder: folder);
        Sql($"UPDATE assets SET status = 'blocked' WHERE id = '{blocked}'");
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Put, $"/api/v1/assets/{blocked}/rights", admin, new { rightsId = id })).Status);
    }

    [Fact]
    public async Task A_published_asset_cannot_be_put_under_a_licence_that_is_not_in_force()
    {
        var (_, admin, folder) = await TenantWithPacks();
        var asset = await ChannelAsset(admin, ["web"], folder: folder);
        await Publish(admin, asset);
        var ended = await NewRights(admin, "Ended", validTo: Today.AddDays(-1));
        var future = await NewRights(admin, "Future", validFrom: Today.AddDays(2));
        var current = await NewRights(admin, "Current", validFrom: Today.AddDays(-2), validTo: Today.AddDays(2));

        var r1 = await Call(HttpMethod.Put, $"/api/v1/assets/{asset}/rights", admin, new { rightsId = ended });
        Assert.Equal(HttpStatusCode.Conflict, r1.Status);
        Assert.Contains("'Ended'", r1.Body.GetProperty("title").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Put, $"/api/v1/assets/{asset}/rights", admin, new { rightsId = future })).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, $"/api/v1/assets/{asset}/rights", admin, new { rightsId = current })).Status);

        // an unpublished asset may be put under any of them; the publish guard decides later
        var draft = await ChannelAsset(admin, ["web"], folder: folder, title: "Draft");
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, $"/api/v1/assets/{draft}/rights", admin, new { rightsId = ended })).Status);
    }

    // ---- reading an asset's rights ------------------------------------------------------------------------------

    [Fact]
    public async Task Anyone_who_can_read_the_asset_sees_a_summary_and_only_managers_see_the_licensor_and_notes()
    {
        var (t, admin, folder) = await TenantWithPacks();
        var id = await NewRights(admin, channels: ["web"], regions: ["north"], validTo: Today.AddDays(10), watermarkRequired: true);
        var asset = await ChannelAsset(admin, ["web"], ["north"], folder);
        await Publish(admin, asset);
        await Call(HttpMethod.Put, $"/api/v1/assets/{asset}/rights", admin, new { rightsId = id });

        var viewer = (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/rights", As(t, "Viewer"))).Body.GetProperty("rights");
        Assert.Equal(("Acme stock licence", "licensed", "expiring", 10, true), (viewer.GetProperty("name").GetString(), viewer.GetProperty("licenceType").GetString(),
            viewer.GetProperty("status").GetString(), viewer.GetProperty("daysRemaining").GetInt32(), viewer.GetProperty("watermarkRequired").GetBoolean()));
        Assert.Equal(["web"], Strings(viewer.GetProperty("channels")));
        Assert.Equal(JsonValueKind.Null, viewer.GetProperty("licensor").ValueKind);
        Assert.Equal(JsonValueKind.Null, viewer.GetProperty("notes").ValueKind);
        Assert.Equal(0, viewer.GetProperty("documentsCount").GetInt32());

        var manager = (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/rights", As(t, "Librarian"))).Body.GetProperty("rights");
        Assert.Equal(("Acme Photos", "Internal note"), (manager.GetProperty("licensor").GetString(), manager.GetProperty("notes").GetString()));

        var none = await ChannelAsset(admin, ["web"], folder: folder, title: "No rights");
        var empty = await Call(HttpMethod.Get, $"/api/v1/assets/{none}/rights", admin);
        Assert.Equal(HttpStatusCode.OK, empty.Status);
        Assert.Equal(JsonValueKind.Null, empty.Body.GetProperty("rights").ValueKind);
        Assert.Equal(none, empty.Body.GetProperty("assetId").GetGuid());
    }

    [Fact]
    public async Task The_check_answers_for_a_channel_and_region_with_reasons_and_the_effective_values()
    {
        var (t, admin, folder) = await TenantWithPacks();
        var id = await NewRights(admin, channels: ["web", "print"], regions: ["north"], validTo: Today.AddDays(5), watermarkRequired: true);
        var asset = await ChannelAsset(admin, ["web", "signage"], ["north", "south"], folder);
        await Publish(admin, asset);
        await Call(HttpMethod.Put, $"/api/v1/assets/{asset}/rights", admin, new { rightsId = id });
        async Task<JsonElement> Check(string query, string? token = null) => (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/rights/check{query}", token ?? admin)).Body;

        var ok = await Check("?channel=web&region=north");
        Assert.True(ok.GetProperty("allowed").GetBoolean());
        Assert.Empty(ok.GetProperty("reasons").EnumerateArray());
        Assert.Equal(("expiring", true, Today.AddDays(5).ToString("yyyy-MM-dd")), (ok.GetProperty("status").GetString(), ok.GetProperty("watermarkRequired").GetBoolean(), ok.GetProperty("validTo").GetString()));
        Assert.Equal(["web"], Strings(ok.GetProperty("effectiveChannels")));
        Assert.Equal(["north"], Strings(ok.GetProperty("effectiveRegions")));
        Assert.False(ok.GetProperty("noChannelsAllowed").GetBoolean());

        var signage = await Check("?channel=signage");
        Assert.False(signage.GetProperty("allowed").GetBoolean());
        Assert.Contains("does not cover the 'signage' channel", Assert.Single(signage.GetProperty("reasons").EnumerateArray()).GetString(), StringComparison.Ordinal);

        var south = await Check("?channel=web&region=south");
        Assert.False(south.GetProperty("allowed").GetBoolean());
        Assert.Contains("'south' region", south.GetProperty("reasons")[0].GetString(), StringComparison.Ordinal);

        var both = await Check("?channel=print&region=south");   // print is licensed but the asset's own metadata never offered it
        Assert.False(both.GetProperty("allowed").GetBoolean());
        Assert.Equal(2, both.GetProperty("reasons").GetArrayLength());

        Assert.True((await Check("")).GetProperty("allowed").GetBoolean());                // nothing asked about: only the licence's validity counts
        Assert.True((await Check("?channel=WEB", As(t, "Viewer"))).GetProperty("allowed").GetBoolean());   // needs only read access; case does not matter

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/rights/check?channel=two%20words", admin)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/assets/{Guid.NewGuid()}/rights/check", admin)).Status);
    }

    [Fact]
    public async Task The_check_reports_an_ended_licence_and_a_licence_not_yet_begun()
    {
        var (_, admin, folder) = await TenantWithPacks();
        var asset = await ChannelAsset(admin, ["web"], folder: folder);
        await Call(HttpMethod.Put, $"/api/v1/assets/{asset}/rights", admin, new { rightsId = await NewRights(admin, "Ended", validTo: Today.AddDays(-3)) });
        var ended = (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/rights/check", admin)).Body;
        Assert.False(ended.GetProperty("allowed").GetBoolean());
        Assert.Equal("expired", ended.GetProperty("status").GetString());
        Assert.Contains("ended on", ended.GetProperty("reasons")[0].GetString(), StringComparison.Ordinal);

        await Call(HttpMethod.Put, $"/api/v1/assets/{asset}/rights", admin, new { rightsId = await NewRights(admin, "Future", validFrom: Today.AddDays(4)) });
        var future = (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/rights/check", admin)).Body;
        Assert.Equal("not_yet_valid", future.GetProperty("status").GetString());
        Assert.Contains("starts on", future.GetProperty("reasons")[0].GetString(), StringComparison.Ordinal);

        var none = await ChannelAsset(admin, ["web"], folder: folder, title: "No rights");
        var open = (await Call(HttpMethod.Get, $"/api/v1/assets/{none}/rights/check?channel=web", admin)).Body;
        Assert.True(open.GetProperty("allowed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, open.GetProperty("status").ValueKind);
    }

    // ---- bulk assignment ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Bulk_assignment_reports_every_asset_as_assigned_skipped_or_failed_with_a_reason()
    {
        var (t, admin, folder) = await TenantWithPacks();
        var (_, otherAdmin, otherFolder) = await TenantWithPacks();
        var id = await NewRights(admin, "Bulk licence", channels: ["web"]);
        var other = await NewRights(admin, "Existing licence");
        var fresh = await ChannelAsset(admin, ["web", "signage"], folder: folder, title: "Fresh");
        var already = await ChannelAsset(admin, ["web"], folder: folder, title: "Already");
        var taken = await ChannelAsset(admin, ["web"], folder: folder, title: "Taken");
        var deleted = await ChannelAsset(admin, ["web"], folder: folder, title: "Deleted");
        var foreign = await ChannelAsset(otherAdmin, ["web"], folder: otherFolder, title: "Foreign");
        await Call(HttpMethod.Put, $"/api/v1/assets/{already}/rights", admin, new { rightsId = id });
        await Call(HttpMethod.Put, $"/api/v1/assets/{taken}/rights", admin, new { rightsId = other });
        await Call(HttpMethod.Delete, $"/api/v1/assets/{deleted}", admin);

        var r = await Call(HttpMethod.Post, $"/api/v1/rights/{id}/assign", admin, new { assetIds = new[] { fresh, already, taken, deleted, foreign, Guid.NewGuid(), fresh } });
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal((1, 3, 2), (r.Body.GetProperty("assigned").GetInt32(), r.Body.GetProperty("skipped").GetInt32(), r.Body.GetProperty("failed").GetInt32()));
        var items = r.Body.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("assetId").GetGuid());
        Assert.Equal(6, items.Count);   // the repeated id is reported once
        Assert.Equal("assigned", items[fresh].GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, items[fresh].GetProperty("reason").ValueKind);
        Assert.Equal("skipped", items[already].GetProperty("outcome").GetString());
        Assert.Contains("Already assigned", items[already].GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Equal("skipped", items[taken].GetProperty("outcome").GetString());
        Assert.Contains("'Existing licence'", items[taken].GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Equal("skipped", items[deleted].GetProperty("outcome").GetString());
        Assert.Equal("failed", items[foreign].GetProperty("outcome").GetString());   // another tenant's asset looks absent
        Assert.Equal("Asset not found.", items[foreign].GetProperty("reason").GetString());
        Assert.Equal(["web"], Attr(await GetAsset(admin, fresh), "channel"));

        var replace = await Call(HttpMethod.Post, $"/api/v1/rights/{id}/assign", admin, new { assetIds = new[] { taken }, replace = true });
        Assert.Equal((1, 0, 0), (replace.Body.GetProperty("assigned").GetInt32(), replace.Body.GetProperty("skipped").GetInt32(), replace.Body.GetProperty("failed").GetInt32()));
        Assert.Equal(id, (await GetAsset(admin, taken)).GetProperty("rightsId").GetGuid());
        Assert.Equal(3, (await Call(HttpMethod.Get, $"/api/v1/rights/{id}", admin)).Body.GetProperty("assetCount").GetInt32());
        Assert.Equal(1, Count(t, $"SELECT count(*) FROM audit_log WHERE action = 'asset.rights.changed' AND entity_id = '{taken}' AND after->>'PreviousRightsId' = '{other}'"));
    }

    [Fact]
    public async Task Bulk_assignment_is_capped_and_needs_something_to_assign()
    {
        var (_, admin, _) = await TenantWithPacks();
        var id = await NewRights(admin);
        var empty = await Call(HttpMethod.Post, $"/api/v1/rights/{id}/assign", admin, new { assetIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.Status);
        Assert.True(empty.Body.GetProperty("errors").TryGetProperty("assetIds", out _));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, $"/api/v1/rights/{id}/assign", admin, new { assetIds = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToArray() })).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/rights/{id}/assign", admin, new { assetIds = Enumerable.Range(0, 500).Select(_ => Guid.NewGuid()).ToArray() })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"/api/v1/rights/{Guid.NewGuid()}/assign", admin, new { assetIds = new[] { Guid.NewGuid() } })).Status);
    }

    [Fact]
    public async Task Bulk_assignment_skips_a_published_asset_when_the_licence_is_not_in_force()
    {
        var (_, admin, folder) = await TenantWithPacks();
        var asset = await ChannelAsset(admin, ["web"], folder: folder);
        await Publish(admin, asset);
        var ended = await NewRights(admin, "Ended", validTo: Today.AddDays(-1));
        var r = await Call(HttpMethod.Post, $"/api/v1/rights/{ended}/assign", admin, new { assetIds = new[] { asset } });
        Assert.Equal((0, 1), (r.Body.GetProperty("assigned").GetInt32(), r.Body.GetProperty("skipped").GetInt32()));
        Assert.Contains("ended on", r.Body.GetProperty("items")[0].GetProperty("reason").GetString(), StringComparison.Ordinal);
    }

    // ---- the lifecycle guard --------------------------------------------------------------------------------------

    [Fact]
    public async Task Publishing_is_refused_while_the_licence_has_ended_or_not_begun_and_allowed_once_it_is_in_force()
    {
        var (_, admin, folder) = await TenantWithPacks();
        var asset = await ChannelAsset(admin, ["web"], folder: folder);
        var id = await NewRights(admin, "Acme stock licence", validTo: Today.AddDays(-2));
        await Call(HttpMethod.Put, $"/api/v1/assets/{asset}/rights", admin, new { rightsId = id });
        foreach (var action in new[] { "submit", "approve" }) Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/transitions/{action}", admin, new { })).Status);

        var ended = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/transitions/publish", admin, new { });
        Assert.Equal(HttpStatusCode.Conflict, ended.Status);
        var message = ended.Body.GetProperty("title").GetString()!;
        Assert.Contains("'Acme stock licence'", message, StringComparison.Ordinal);
        Assert.Contains(Today.AddDays(-2).ToString("yyyy-MM-dd"), message, StringComparison.Ordinal);
        Assert.Equal("approved", (await GetAsset(admin, asset)).GetProperty("status").GetString());

        await Call(HttpMethod.Put, $"/api/v1/rights/{id}", admin, Body(validFrom: Today.AddDays(3), validTo: Today.AddDays(30)));
        var future = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/transitions/publish", admin, new { });
        Assert.Equal(HttpStatusCode.Conflict, future.Status);
        Assert.Contains("starts on", future.Body.GetProperty("title").GetString(), StringComparison.Ordinal);

        await Call(HttpMethod.Put, $"/api/v1/rights/{id}", admin, Body(validFrom: Today.AddDays(-3), validTo: Today));   // the last day counts
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/transitions/publish", admin, new { })).Status);
        Assert.Equal("published", (await GetAsset(admin, asset)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Other_transitions_do_not_care_about_the_licence_and_an_asset_without_rights_publishes_as_before()
    {
        var (_, admin, folder) = await TenantWithPacks();
        var asset = await ChannelAsset(admin, ["web"], folder: folder);
        await Call(HttpMethod.Put, $"/api/v1/assets/{asset}/rights", admin, new { rightsId = await NewRights(admin, validTo: Today.AddDays(-2)) });
        foreach (var action in new[] { "submit", "approve", "withdraw" }) Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/transitions/{action}", admin, new { })).Status);
        var plain = await ChannelAsset(admin, ["web"], folder: folder, title: "Plain");
        await Publish(admin, plain);
    }

    // ---- documents ------------------------------------------------------------------------------------------------

    private static readonly byte[] PdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj << /Type /Catalog >> endobj\ntrailer << /Root 1 0 R >>\n%%EOF\n");

    private async Task<(Guid Id, JsonElement Start)> Upload(string token, Guid rights, byte[] bytes, string fileName = "release.pdf", string contentType = "application/pdf", long? declaredSize = null)
    {
        var start = await Call(HttpMethod.Post, $"/api/v1/rights/{rights}/documents", token, new { fileName, contentType, size = declaredSize ?? bytes.Length });
        Assert.True(start.Status == HttpStatusCode.Created, start.Body.ToString());
        var url = start.Body.GetProperty("uploadUrl").GetString()!;
        Assert.Equal("PUT", start.Body.GetProperty("method").GetString());
        using var put = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(bytes) };
        put.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(start.Body.GetProperty("headers").GetProperty("Content-Type").GetString()!);
        using var res = await S3Http.SendAsync(put);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        return (start.Body.GetProperty("documentId").GetGuid(), start.Body);
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> Complete(string token, Guid rights, Guid doc, string fileName = "release.pdf", string contentType = "application/pdf", long size = -1) =>
        Call(HttpMethod.Post, $"/api/v1/rights/{rights}/documents/{doc}/complete", token, new { fileName, contentType, size = size < 0 ? PdfBytes.Length : size });

    [Fact]
    public async Task A_document_goes_straight_to_storage_is_recorded_listed_downloaded_as_an_attachment_and_deleted()
    {
        var (t, admin, _) = await TenantWithPacks();
        var id = await NewRights(admin);
        var (doc, start) = await Upload(admin, id, PdfBytes);
        Assert.Contains($"rights/{t:N}/{id:N}/{doc:N}", start.GetProperty("uploadUrl").GetString(), StringComparison.Ordinal);   // under the originals bucket's rights prefix, built from ids only
        Assert.Contains("dam-originals", start.GetProperty("uploadUrl").GetString(), StringComparison.Ordinal);
        Assert.Equal(25L * 1024 * 1024, start.GetProperty("maxBytes").GetInt64());
        Assert.Empty(await ListDocs(admin, id));                  // nothing is recorded before complete

        var done = await Complete(admin, id, doc);
        Assert.Equal(HttpStatusCode.Created, done.Status);
        Assert.Equal(("release.pdf", "application/pdf", (long)PdfBytes.Length, id), (done.Body.GetProperty("name").GetString(), done.Body.GetProperty("contentType").GetString(), done.Body.GetProperty("sizeBytes").GetInt64(), done.Body.GetProperty("rightsId").GetGuid()));
        var listed = Assert.Single(await ListDocs(admin, id));
        Assert.Equal(doc, listed.GetProperty("id").GetGuid());
        Assert.Equal((1, 1), ((await Call(HttpMethod.Get, $"/api/v1/rights/{id}", admin)).Body.GetProperty("documentCount").GetInt32(), Count(t, "SELECT count(*) FROM rights_documents")));
        Assert.Equal(1, Count(t, $"SELECT count(*) FROM audit_log WHERE action = 'rights.document.added' AND entity_id = '{id}'"));

        var link = await Call(HttpMethod.Get, $"/api/v1/rights/{id}/documents/{doc}/link", admin);
        Assert.Equal(HttpStatusCode.OK, link.Status);
        using var download = await S3Http.GetAsync(link.Body.GetProperty("url").GetString()!);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(PdfBytes, await download.Content.ReadAsByteArrayAsync());
        Assert.StartsWith("attachment", download.Content.Headers.ContentDisposition!.ToString(), StringComparison.Ordinal);
        Assert.Equal("release.pdf", download.Content.Headers.ContentDisposition!.FileName?.Trim('"'));
        Assert.Equal("application/octet-stream", download.Content.Headers.ContentType!.MediaType);   // never a type the browser would render

        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"/api/v1/rights/{id}/documents/{doc}", admin)).Status);
        Assert.Empty(await ListDocs(admin, id));
        Assert.False(await InStore(t, id, doc));
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Delete, $"/api/v1/rights/{id}/documents/{doc}", admin)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/rights/{id}/documents/{doc}/link", admin)).Status);
    }

    private async Task<List<JsonElement>> ListDocs(string token, Guid rights) =>
        (await Call(HttpMethod.Get, $"/api/v1/rights/{rights}/documents", token)).Body.EnumerateArray().ToList();

    private async Task<bool> InStore(Guid tenant, Guid rights, Guid doc)
    {
        using var s3 = Pg.S3Client();
        try { await s3.GetObjectMetadataAsync("dam-originals", $"rights/{tenant:N}/{rights:N}/{doc:N}"); return true; }
        catch (Amazon.S3.AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound) { return false; }
    }

    [Fact]
    public async Task Types_extensions_and_sizes_are_checked_when_the_upload_starts()
    {
        var (_, admin, _) = await TenantWithPacks();
        var id = await NewRights(admin);
        async Task<JsonElement> Start(object body) { var r = await Call(HttpMethod.Post, $"/api/v1/rights/{id}/documents", admin, body); Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status); return r.Body.GetProperty("errors"); }

        Assert.True((await Start(new { fileName = "page.html", contentType = "text/html", size = 10 })).TryGetProperty("contentType", out _));
        Assert.True((await Start(new { fileName = "form.exe", contentType = "application/pdf", size = 10 })).TryGetProperty("fileName", out _));
        Assert.True((await Start(new { fileName = "form.pdf", contentType = "application/pdf", size = 25 * 1024 * 1024 + 1 })).TryGetProperty("size", out _));
        Assert.True((await Start(new { fileName = "form.pdf", contentType = "application/pdf", size = 0 })).TryGetProperty("size", out _));
        Assert.True((await Start(new { fileName = "", contentType = "application/pdf", size = 5 })).TryGetProperty("fileName", out _));

        foreach (var (name, type) in new[] { ("a.pdf", "application/pdf"), ("a.png", "image/png"), ("a.jpg", "image/jpeg"), ("a.jpeg", "image/jpeg"),
                     ("a.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document"), ("a.txt", "text/plain") })
            Assert.Equal(HttpStatusCode.Created, (await Call(HttpMethod.Post, $"/api/v1/rights/{id}/documents", admin, new { fileName = name, contentType = type, size = 25 * 1024 * 1024 })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"/api/v1/rights/{Guid.NewGuid()}/documents", admin, new { fileName = "a.pdf", contentType = "application/pdf", size = 5 })).Status);
    }

    [Fact]
    public async Task Completing_checks_that_the_object_exists_has_the_declared_size_and_looks_like_its_type()
    {
        var (t, admin, _) = await TenantWithPacks();
        var id = await NewRights(admin);

        // nothing was uploaded at the address
        var start = await Call(HttpMethod.Post, $"/api/v1/rights/{id}/documents", admin, new { fileName = "release.pdf", contentType = "application/pdf", size = PdfBytes.Length });
        var ghost = start.Body.GetProperty("documentId").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await Complete(admin, id, ghost)).Status);

        // the bytes are not the size that was declared: refused, and the stray object is removed
        var (wrongSize, _) = await Upload(admin, id, PdfBytes, declaredSize: PdfBytes.Length + 7);
        var sizeProblem = await Complete(admin, id, wrongSize, size: PdfBytes.Length + 7);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, sizeProblem.Status);
        Assert.True(sizeProblem.Body.GetProperty("errors").TryGetProperty("size", out _));
        Assert.False(await InStore(t, id, wrongSize));

        // an HTML page uploaded as a pdf
        var (liar, _) = await Upload(admin, id, Encoding.ASCII.GetBytes("<html><script>alert(1)</script></html>"), "evil.pdf", "application/pdf");
        var lying = await Complete(admin, id, liar, "evil.pdf", "application/pdf", size: 38);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, lying.Status);
        Assert.True(lying.Body.GetProperty("errors").TryGetProperty("contentType", out _));
        Assert.False(await InStore(t, id, liar));

        // a good one, and then the same document cannot be recorded twice
        var (good, _) = await Upload(admin, id, PdfBytes);
        Assert.Equal(HttpStatusCode.Created, (await Complete(admin, id, good)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Complete(admin, id, good)).Status);
        Assert.Single(await ListDocs(admin, id));
    }

    [Fact]
    public async Task Deleting_a_record_deletes_its_documents_from_storage_too()
    {
        var (t, admin, _) = await TenantWithPacks();
        var id = await NewRights(admin);
        var (doc, _) = await Upload(admin, id, PdfBytes);
        await Complete(admin, id, doc);
        Assert.True(await InStore(t, id, doc));
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"/api/v1/rights/{id}", admin)).Status);
        Assert.False(await InStore(t, id, doc));
        Assert.Equal(0, Count(t, "SELECT count(*) FROM rights_documents"));
    }

    [Fact]
    public async Task The_asset_summary_counts_the_documents_but_only_managers_can_open_them()
    {
        var (t, admin, folder) = await TenantWithPacks();
        var id = await NewRights(admin);
        var (doc, _) = await Upload(admin, id, PdfBytes);
        await Complete(admin, id, doc);
        var asset = await ChannelAsset(admin, ["web"], folder: folder);
        await Publish(admin, asset);
        await Call(HttpMethod.Put, $"/api/v1/assets/{asset}/rights", admin, new { rightsId = id });
        Assert.Equal(1, (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/rights", As(t, "Viewer"))).Body.GetProperty("rights").GetProperty("documentsCount").GetInt32());

        var viewer = As(t, "Viewer");
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, $"/api/v1/rights/{id}/documents", viewer)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, $"/api/v1/rights/{id}/documents/{doc}/link", viewer)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, $"/api/v1/rights/{id}/documents", viewer, new { fileName = "a.pdf", contentType = "application/pdf", size = 5 })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, $"/api/v1/rights/{id}/documents/{doc}/complete", viewer, new { fileName = "a.pdf", contentType = "application/pdf", size = 5 })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Delete, $"/api/v1/rights/{id}/documents/{doc}", viewer)).Status);
    }

    // ---- tenants are separate -------------------------------------------------------------------------------------

    [Fact]
    public async Task Another_tenant_cannot_see_change_or_download_a_record_its_documents_or_its_assignments()
    {
        var (a, adminA, folderA) = await TenantWithPacks();
        var (_, adminB, _) = await TenantWithPacks();
        var id = await NewRights(adminA);
        var (doc, _) = await Upload(adminA, id, PdfBytes);
        await Complete(adminA, id, doc);
        var asset = await ChannelAsset(adminA, ["web"], folder: folderA);

        Assert.Empty((await Call(HttpMethod.Get, "/api/v1/rights", adminB)).Body.EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/rights/{id}", adminB)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Put, $"/api/v1/rights/{id}", adminB, Body())).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Delete, $"/api/v1/rights/{id}", adminB)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/rights/{id}/documents", adminB)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/rights/{id}/documents/{doc}/link", adminB)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Delete, $"/api/v1/rights/{id}/documents/{doc}", adminB)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"/api/v1/rights/{id}/documents", adminB, new { fileName = "a.pdf", contentType = "application/pdf", size = 5 })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"/api/v1/rights/{id}/documents/{doc}/complete", adminB, new { fileName = "a.pdf", contentType = "application/pdf", size = 5 })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"/api/v1/rights/{id}/assign", adminB, new { assetIds = new[] { asset } })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Put, $"/api/v1/assets/{asset}/rights", adminB, new { rightsId = id })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/rights", adminB)).Status);

        // B cannot attach A's record to B's own asset either: the record is invisible to it
        var (_, adminB2, folderB2) = await TenantWithPacks();
        var bAsset = await ChannelAsset(adminB2, ["web"], folder: folderB2);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Put, $"/api/v1/assets/{bAsset}/rights", adminB2, new { rightsId = id })).Status);
        Assert.Equal(1, Count(a, "SELECT count(*) FROM rights"));
    }

    [Fact]
    public async Task Row_level_security_isolates_all_three_tables_and_the_application_role_cannot_rewrite_the_alert_ledger()
    {
        var (a, adminA, folderA) = await TenantWithPacks();
        var (b, _, _) = await TenantWithPacks();
        var id = await NewRights(adminA);
        var (doc, _) = await Upload(adminA, id, PdfBytes);
        await Complete(adminA, id, doc);
        var asset = await ChannelAsset(adminA, ["web"], folder: folderA);
        Sql($"INSERT INTO rights_alerts (id, tenant_id, rights_id, asset_id, threshold_days, valid_to, created_at) VALUES (gen_random_uuid(), '{a}', '{id}', '{asset}', 7, '2026-12-01', now())");

        foreach (var table in new[] { "rights", "rights_documents", "rights_alerts" })
        {
            Assert.Equal(1, Count(a, $"SELECT count(*) FROM {table}"));
            Assert.Equal(0, Count(b, $"SELECT count(*) FROM {table}"));
        }
        using (var conn = Pg.RawAppConnection(null))   // no tenant at all
            foreach (var table in new[] { "rights", "rights_documents", "rights_alerts" })
            {
                using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {table}", conn);
                Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
            }
        using (var conn = Pg.RawAppConnection(b))   // B cannot write A's rows
        {
            using var insert = new NpgsqlCommand($"INSERT INTO rights (id, tenant_id, name, licence_type, licensor, channels, regions, watermark_required, notes, created_at, updated_at) VALUES (gen_random_uuid(), '{a}', 'x', 'owned', '', '{{}}', '{{}}', false, '', now(), now())", conn);
            Assert.Throws<PostgresException>(() => insert.ExecuteNonQuery());
        }
        using (var conn = Pg.RawAppConnection(a))   // the ledger is append-only for the application role
        {
            using var update = new NpgsqlCommand("UPDATE rights_alerts SET threshold_days = 1", conn);
            Assert.Throws<PostgresException>(() => update.ExecuteNonQuery());
            using var delete = new NpgsqlCommand("DELETE FROM rights_alerts", conn);
            Assert.Throws<PostgresException>(() => delete.ExecuteNonQuery());
        }
    }

    [Fact]
    public async Task The_database_itself_refuses_to_drop_rights_that_an_asset_still_uses_and_keeps_names_unique_ignoring_case()
    {
        var (t, admin, folder) = await TenantWithPacks();
        var id = await NewRights(admin, "Unique name");
        var asset = await ChannelAsset(admin, ["web"], folder: folder);
        await Call(HttpMethod.Put, $"/api/v1/assets/{asset}/rights", admin, new { rightsId = id });
        using var conn = Pg.RawAppConnection(t);
        using (var del = new NpgsqlCommand($"DELETE FROM rights WHERE id = '{id}'", conn)) Assert.Throws<PostgresException>(() => del.ExecuteNonQuery());
        using var dup = new NpgsqlCommand($"INSERT INTO rights (id, tenant_id, name, licence_type, licensor, channels, regions, watermark_required, notes, created_at, updated_at) VALUES (gen_random_uuid(), '{t}', 'UNIQUE NAME', 'owned', '', '{{}}', '{{}}', false, '', now(), now())", conn);
        Assert.Throws<PostgresException>(() => dup.ExecuteNonQuery());
    }

    [Fact]
    public async Task The_asset_in_a_listing_carries_the_rights_id_and_the_effective_attributes()
    {
        var (_, admin, folder) = await TenantWithPacks();
        var id = await NewRights(admin, channels: ["web"]);
        var asset = await ChannelAsset(admin, ["web", "signage"], folder: folder);
        await Call(HttpMethod.Put, $"/api/v1/assets/{asset}/rights", admin, new { rightsId = id });
        var listed = (await Call(HttpMethod.Get, "/api/v1/assets", admin)).Body.GetProperty("items").EnumerateArray().Single(a => a.GetProperty("id").GetGuid() == asset);
        Assert.Equal(id, listed.GetProperty("rightsId").GetGuid());
        Assert.Equal(["web"], Attr(listed, "channel"));
    }

    [Fact]
    public async Task A_channel_restricted_api_client_stops_seeing_the_asset_when_rights_restrict_it_and_sees_it_again_as_they_change()
    {
        var (t, admin, folder) = await TenantWithPacks();
        var signage = ApiTestHost.TokenFor(t, Guid.NewGuid(), roles: ["ApiClient"], attributes: new() { ["channel"] = ["signage"] });
        var asset = await ChannelAsset(admin, ["web", "signage"], folder: folder);
        await Publish(admin, asset);
        async Task<bool> Sees() => (await Call(HttpMethod.Get, "/api/v1/assets", signage)).Body.GetProperty("items").EnumerateArray().Any(a => a.GetProperty("id").GetGuid() == asset);
        async Task<HttpStatusCode> Direct() => (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}", signage)).Status;

        Assert.True(await Sees());
        var id = await NewRights(admin, channels: ["web"]);    // "Asset restricted to web channel is rejected by the PADS4 plugin listing" (spec Step 2.5)
        await Call(HttpMethod.Put, $"/api/v1/assets/{asset}/rights", admin, new { rightsId = id });
        Assert.False(await Sees());
        Assert.Equal(HttpStatusCode.NotFound, await Direct());
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/rights", signage)).Status);

        await Call(HttpMethod.Put, $"/api/v1/rights/{id}", admin, Body(channels: ["web", "signage"]));
        Assert.True(await Sees());
        await Call(HttpMethod.Put, $"/api/v1/rights/{id}", admin, Body(channels: ["web"]));
        Assert.False(await Sees());
        await Call(HttpMethod.Put, $"/api/v1/assets/{asset}/rights", admin, new { rightsId = (Guid?)null });
        Assert.True(await Sees());
        Assert.Equal(HttpStatusCode.OK, await Direct());

        // a licence that shares nothing with the metadata hides it from every channel-restricted client, but not from an admin
        await Call(HttpMethod.Put, $"/api/v1/assets/{asset}/rights", admin, new { rightsId = await NewRights(admin, "Print only", channels: ["print"]) });
        Assert.False(await Sees());
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}", admin)).Status);
    }

    [Fact]
    public async Task A_region_restricted_dealer_is_kept_away_by_the_licences_regions_too()
    {
        var (t, admin, folder) = await TenantWithPacks();
        var north = ApiTestHost.TokenFor(t, Guid.NewGuid(), roles: ["Dealer"], attributes: new() { ["region"] = ["north"] });
        var asset = await ChannelAsset(admin, [], ["north", "south"], folder);   // a restricted role must also match every other dimension, so the asset names no channel
        await Publish(admin, asset);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}", north)).Status);
        await Call(HttpMethod.Put, $"/api/v1/assets/{asset}/rights", admin, new { rightsId = await NewRights(admin, regions: ["south"]) });
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}", north)).Status);
    }
}
