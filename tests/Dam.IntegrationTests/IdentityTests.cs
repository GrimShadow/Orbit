using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Application.Identity;
using Dam.Application.Messaging;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Infrastructure.Identity;
using Dam.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Dam.IntegrationTests;

[Collection("pg")]
public sealed class IdentityTests(PostgresFixture pg) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _api = null!;
    private HttpClient _http = null!;

    public Task InitializeAsync()
    {
        _api = new ApiTestHost(pg).Factory();
        _http = _api.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _api.DisposeAsync();
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    private async Task<Guid> NewTenantAsync(string? slug = null)
    {
        var id = Uuid7.NewGuid();
        await using var scope = _api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<Dam.Api.Auth.TenantOverride>().Value = id;
        var r = await scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .Send(new EnsureTenantCommand(id, "Tenant " + id.ToString("N")[..6], slug ?? "t-" + id.ToString("N")[..12]));
        Assert.True(r.IsSuccess, r.IsFailure ? r.Error.Message : "");
        return id;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Call(HttpMethod method, string path, string token, object? body = null)
    {
        using var req = new HttpRequestMessage(method, path) { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) } };
        if (body is not null) req.Content = JsonContent.Create(body);
        using var res = await _http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, text.Length > 0 ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    private static string Admin(Guid tenant, Guid? subject = null) =>
        ApiTestHost.TokenFor(tenant, subject ?? Guid.NewGuid(), roles: ["Admin"], name: "Admin");

    private static string[] Strings(JsonElement e) => e.EnumerateArray().Select(x => x.GetString()!).ToArray();

    private static Dictionary<string, string[]> Attrs(string? region = null, string? dealer = null) =>
        new[] { (Dimensions.Region, region), (Dimensions.Dealer, dealer) }
            .Where(x => x.Item2 is not null).ToDictionary(x => x.Item1, x => new[] { x.Item2! });

    /// <summary>Counts rows as the least-privileged app role bound to <paramref name="tenant"/> (a superuser would bypass RLS).</summary>
    private Task<long> Count(Guid tenant, string sql)
    {
        using var c = pg.RawAppConnection(tenant);
        using var cmd = new NpgsqlCommand(sql, c);
        return Task.FromResult(Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    // ---- 1. tenants ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_token_for_an_unknown_tenant_is_rejected_with_403_problem_json()
    {
        var res = await Call(HttpMethod.Get, "/api/v1/me", Admin(Uuid7.NewGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, res.Status);
        Assert.Equal("Unknown tenant", res.Body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Admin_reads_and_updates_the_current_tenant_and_a_viewer_cannot()
    {
        var t = await NewTenantAsync("acme-" + Guid.NewGuid().ToString("N")[..6]);
        var read = await Call(HttpMethod.Get, "/api/v1/tenants/current", Admin(t));
        Assert.Equal(HttpStatusCode.OK, read.Status);
        Assert.Equal(t, read.Body.GetProperty("id").GetGuid());

        var upd = await Call(HttpMethod.Patch, "/api/v1/tenants/current", Admin(t), new { name = "Acme Motors", settings = new { locale = "en-IN" } });
        Assert.Equal(HttpStatusCode.OK, upd.Status);
        Assert.Equal("Acme Motors", upd.Body.GetProperty("name").GetString());
        Assert.Equal("en-IN", upd.Body.GetProperty("settings").GetProperty("locale").GetString());

        var viewer = ApiTestHost.TokenFor(t, Guid.NewGuid(), roles: ["Viewer"]);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, "/api/v1/tenants/current", viewer)).Status); // anyone may read
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Patch, "/api/v1/tenants/current", viewer, new { name = "x" })).Status);
    }

    // ---- 2. JIT provisioning ---------------------------------------------------------------------------------

    [Fact]
    public async Task First_login_provisions_the_user_their_idp_groups_and_attributes()
    {
        var t = await NewTenantAsync();
        var sub = Guid.NewGuid();
        var token = ApiTestHost.TokenFor(t, sub, ["Dealer"], ["dealers"], Attrs("North", "D-N-001"), "Nikhil North");

        var me = await Call(HttpMethod.Get, "/api/v1/me", token);
        Assert.Equal(HttpStatusCode.OK, me.Status);
        Assert.Equal("active", me.Body.GetProperty("status").GetString());
        Assert.Equal("Nikhil North", me.Body.GetProperty("displayName").GetString());
        Assert.Equal(["Dealer"], Strings(me.Body.GetProperty("roles")));
        Assert.Equal(["dealers"], Strings(me.Body.GetProperty("groups")));
        Assert.Equal(["assets.read"], Strings(me.Body.GetProperty("permissions")));
        Assert.Equal(["North"], Strings(me.Body.GetProperty("attributes").GetProperty("region")));

        Assert.Equal(1, await Count(t, "SELECT count(*) FROM users"));
        Assert.Equal(1, await Count(t, "SELECT count(*) FROM groups WHERE source = 'Idp'"));
        Assert.Equal(1, await Count(t, "SELECT count(*) FROM audit_log WHERE action = 'user.provisioned'"));
        Assert.Equal(8, await Count(t, "SELECT count(*) FROM roles WHERE is_built_in"));

        await Call(HttpMethod.Get, "/api/v1/me", token); // again: no duplicates
        Assert.Equal(1, await Count(t, "SELECT count(*) FROM users"));
        Assert.Equal(1, await Count(t, "SELECT count(*) FROM audit_log WHERE action = 'user.provisioned'"));
    }

    [Fact]
    public async Task Changes_in_the_token_resync_groups_and_attributes_but_keep_local_group_memberships()
    {
        var t = await NewTenantAsync();
        var sub = Guid.NewGuid();
        var admin = Admin(t);
        var first = ApiTestHost.TokenFor(t, sub, ["Viewer"], ["dealers", "regional"], Attrs("North"));
        await Call(HttpMethod.Get, "/api/v1/me", first);

        // An admin puts the user into a LOCAL group; the IdP knows nothing about it.
        var userId = (await Call(HttpMethod.Get, "/api/v1/me", first)).Body.GetProperty("userId").GetGuid();
        var local = await Call(HttpMethod.Post, "/api/v1/groups", admin, new { name = "pilot-testers" });
        var localId = local.Body.GetProperty("group").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, $"/api/v1/groups/{localId}/members", admin, new { ids = new[] { userId } })).Status);

        // Next token: dropped "regional", added "press", region moved South.
        var second = ApiTestHost.TokenFor(t, sub, ["Viewer"], ["dealers", "press"], Attrs("South"));
        var me = await Call(HttpMethod.Get, "/api/v1/me", second);
        Assert.Equal(["dealers", "pilot-testers", "press"], Strings(me.Body.GetProperty("groups")));
        Assert.Equal(["South"], Strings(me.Body.GetProperty("attributes").GetProperty("region")));
        Assert.Equal(1, await Count(t, $"SELECT count(*) FROM users WHERE external_subject = '{sub}'")); // re-synced, not duplicated
    }

    [Fact]
    public async Task Concurrent_first_requests_from_a_new_user_provision_exactly_one_user()
    {
        var t = await NewTenantAsync();
        var token = ApiTestHost.TokenFor(t, Guid.NewGuid(), ["Viewer"]);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Call(HttpMethod.Get, "/api/v1/me", token)));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.Status));
        Assert.Equal(1, await Count(t, "SELECT count(*) FROM users"));
    }

    [Fact]
    public async Task The_same_subject_in_two_tenants_is_two_separate_users()
    {
        var a = await NewTenantAsync(); var b = await NewTenantAsync();
        var sub = Guid.NewGuid();
        await Call(HttpMethod.Get, "/api/v1/me", ApiTestHost.TokenFor(a, sub, ["Viewer"]));
        await Call(HttpMethod.Get, "/api/v1/me", ApiTestHost.TokenFor(b, sub, ["Viewer"]));
        Assert.Equal(1, await Count(a, "SELECT count(*) FROM users"));
        Assert.Equal(1, await Count(b, "SELECT count(*) FROM users"));
    }

    // ---- 3. authorization on the admin API -------------------------------------------------------------------

    [Theory]
    [InlineData("Editor")]
    [InlineData("Viewer")]
    [InlineData("Dealer")]
    [InlineData("Approver")]
    [InlineData("Librarian")]
    public async Task Only_admins_manage_users_groups_roles_and_rules(string role)
    {
        var t = await NewTenantAsync();
        var token = ApiTestHost.TokenFor(t, Guid.NewGuid(), [role], attributes: Attrs("North"));
        foreach (var path in new[] { "/api/v1/users", "/api/v1/groups", "/api/v1/roles", "/api/v1/access-rules" })
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, path, token)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "/api/v1/roles", token,
            new { name = "x", permissions = new[] { "assets.read" } })).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, "/api/v1/me", token)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, "/api/v1/permissions", token)).Status);
    }

    [Fact]
    public async Task Requests_without_a_provisioned_role_or_with_an_unknown_role_name_have_no_permissions()
    {
        var t = await NewTenantAsync();
        var token = ApiTestHost.TokenFor(t, Guid.NewGuid(), ["NoSuchRole"]);
        var me = await Call(HttpMethod.Get, "/api/v1/me", token);
        Assert.Empty(Strings(me.Body.GetProperty("roles")));
        Assert.Empty(Strings(me.Body.GetProperty("permissions")));
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, "/api/v1/users", token)).Status);
    }

    // ---- 4. roles --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Roles_crud_with_validation_and_built_in_protection()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var list = await Call(HttpMethod.Get, "/api/v1/roles", admin);
        Assert.Equal(8, list.Body.GetArrayLength());
        var builtInAdmin = list.Body.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Admin");
        Assert.True(builtInAdmin.GetProperty("isBuiltIn").GetBoolean());

        var created = await Call(HttpMethod.Post, "/api/v1/roles", admin, new
            { name = "Reviewer", description = "Reviews", permissions = new[] { "assets.read", "assets.read.in_review" }, attributeRestricted = false });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var id = created.Body.GetProperty("id").GetGuid();

        var dupe = await Call(HttpMethod.Post, "/api/v1/roles", admin, new { name = "reviewer", permissions = new[] { "assets.read" } });
        Assert.Equal(HttpStatusCode.Conflict, dupe.Status); // case-insensitive

        var bad = await Call(HttpMethod.Post, "/api/v1/roles", admin, new { name = "Bad", permissions = new[] { "assets.fly" } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.Status);
        Assert.True(bad.Body.GetProperty("errors").TryGetProperty("Permissions", out _));

        var upd = await Call(HttpMethod.Patch, $"/api/v1/roles/{id}", admin, new { permissions = new[] { "assets.read" } });
        Assert.Equal(["assets.read"], Strings(upd.Body.GetProperty("permissions")));

        var adminId = builtInAdmin.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Patch, $"/api/v1/roles/{adminId}", admin, new { permissions = new[] { "assets.read" } })).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Delete, $"/api/v1/roles/{adminId}", admin)).Status);

        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"/api/v1/roles/{id}", admin)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/roles/{id}", admin)).Status);
    }

    [Fact]
    public async Task Effective_permissions_combine_token_roles_direct_roles_and_group_roles()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var sub = Guid.NewGuid();
        var token = ApiTestHost.TokenFor(t, sub, ["Viewer"], ["press"]);
        var userId = (await Call(HttpMethod.Get, "/api/v1/me", token)).Body.GetProperty("userId").GetGuid();

        var direct = (await Call(HttpMethod.Post, "/api/v1/roles", admin, new { name = "Reporter", permissions = new[] { "analytics.read" } })).Body.GetProperty("id").GetGuid();
        var viaGroup = (await Call(HttpMethod.Post, "/api/v1/roles", admin, new { name = "Sharer", permissions = new[] { "share.manage" } })).Body.GetProperty("id").GetGuid();
        var press = (await Call(HttpMethod.Get, "/api/v1/groups", admin)).Body.EnumerateArray().First(g => g.GetProperty("name").GetString() == "press").GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, $"/api/v1/users/{userId}/roles", admin, new { ids = new[] { direct } })).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, $"/api/v1/groups/{press}/roles", admin, new { ids = new[] { viaGroup } })).Status);

        var me = await Call(HttpMethod.Get, "/api/v1/me", token);
        Assert.Equal(["Reporter", "Sharer", "Viewer"], Strings(me.Body.GetProperty("roles")));
        Assert.Equal(["analytics.read", "assets.read", "share.manage"], Strings(me.Body.GetProperty("permissions")));

        var missing = await Call(HttpMethod.Put, $"/api/v1/users/{userId}/roles", admin, new { ids = new[] { Guid.NewGuid() } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.Status);

        // Removing a role removes what it granted.
        await Call(HttpMethod.Delete, $"/api/v1/roles/{direct}", admin);
        Assert.DoesNotContain("analytics.read", Strings((await Call(HttpMethod.Get, "/api/v1/me", token)).Body.GetProperty("permissions")));
    }

    // ---- 5. groups & users -----------------------------------------------------------------------------------

    [Fact]
    public async Task Groups_crud_idp_groups_are_protected_and_deleting_cleans_up_rules_and_memberships()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await Call(HttpMethod.Get, "/api/v1/me", ApiTestHost.TokenFor(t, Guid.NewGuid(), ["Viewer"], ["from-idp"]));
        var idp = (await Call(HttpMethod.Get, "/api/v1/groups", admin)).Body.EnumerateArray().First(g => g.GetProperty("source").GetString() == "idp");
        var idpId = idp.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Patch, $"/api/v1/groups/{idpId}", admin, new { name = "renamed" })).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Delete, $"/api/v1/groups/{idpId}", admin)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Put, $"/api/v1/groups/{idpId}/members", admin, new { ids = Array.Empty<Guid>() })).Status);

        var created = await Call(HttpMethod.Post, "/api/v1/groups", admin, new { name = "Roadster approvers" });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var gid = created.Body.GetProperty("group").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, "/api/v1/groups", admin, new { name = "roadster APPROVERS" })).Status);

        var rule = await Call(HttpMethod.Post, "/api/v1/access-rules", admin, new
            { principalType = "group", principalId = gid, scopeType = "folder", scopeValue = "brand.roadster", permissions = new[] { "assets.approve" } });
        Assert.Equal(HttpStatusCode.Created, rule.Status);

        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"/api/v1/groups/{gid}", admin)).Status);
        Assert.Equal(0, await Count(t, $"SELECT count(*) FROM access_rules WHERE principal_id = '{gid}'"));
    }

    [Fact]
    public async Task Users_are_listed_with_cursor_pagination_status_filter_and_search()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        for (var i = 0; i < 5; i++)
            await Call(HttpMethod.Get, "/api/v1/me", ApiTestHost.TokenFor(t, Guid.NewGuid(), ["Viewer"], email: $"user{i}@dam.local", name: $"User {i}"));

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var res = await Call(HttpMethod.Get, "/api/v1/users?limit=2" + (cursor is null ? "" : $"&cursor={cursor}"), admin);
            Assert.Equal(HttpStatusCode.OK, res.Status);
            seen.AddRange(res.Body.GetProperty("items").EnumerateArray().Select(u => u.GetProperty("id").GetString()!));
            cursor = res.Body.GetProperty("nextCursor").GetString();
            pages++;
        } while (cursor is not null && pages < 10);

        Assert.Equal(3, pages); // 5 users + the admin who is listing them = 6, two per page
        Assert.Equal(seen.Distinct().Count(), seen.Count);
        Assert.Equal(seen.Order().ToList(), seen); // time-ordered UUID v7 ids

        var search = await Call(HttpMethod.Get, "/api/v1/users?filter[q]=user3", admin);
        Assert.Equal(1, search.Body.GetProperty("items").GetArrayLength());
        var none = await Call(HttpMethod.Get, "/api/v1/users?filter[status]=disabled", admin);
        Assert.Equal(0, none.Body.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Disabling_a_user_removes_their_access_at_once_and_you_cannot_disable_yourself()
    {
        var t = await NewTenantAsync();
        var boss = Guid.NewGuid(); var other = Guid.NewGuid();
        var bossToken = Admin(t, boss); var otherToken = Admin(t, other);
        var bossId = (await Call(HttpMethod.Get, "/api/v1/me", bossToken)).Body.GetProperty("userId").GetGuid();
        var otherId = (await Call(HttpMethod.Get, "/api/v1/me", otherToken)).Body.GetProperty("userId").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, "/api/v1/users", otherToken)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Patch, $"/api/v1/users/{bossId}", bossToken, new { status = "disabled" })).Status);

        var res = await Call(HttpMethod.Patch, $"/api/v1/users/{otherId}", bossToken, new { status = "disabled" });
        Assert.Equal("disabled", res.Body.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, "/api/v1/users", otherToken)).Status); // same, still-valid token

        await Call(HttpMethod.Patch, $"/api/v1/users/{otherId}", bossToken, new { status = "active" });
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, "/api/v1/users", otherToken)).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Patch, $"/api/v1/users/{otherId}", bossToken, new { status = "gone" })).Status);
    }

    // ---- 6. access rules -------------------------------------------------------------------------------------

    [Fact]
    public async Task Access_rule_validation_and_crud()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var uid = (await Call(HttpMethod.Get, "/api/v1/me", ApiTestHost.TokenFor(t, Guid.NewGuid(), ["Approver"]))).Body.GetProperty("userId").GetGuid();

        object Rule(string principalType = "user", Guid? principal = null, string scopeType = "folder", string? scopeValue = "brand.roadster",
            string[]? perms = null, object? attrs = null) =>
            new { principalType, principalId = principal ?? uid, scopeType, scopeValue, permissions = perms ?? ["assets.approve"], attributes = attrs };

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/api/v1/access-rules", admin, Rule(perms: ["nope"]))).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/api/v1/access-rules", admin, Rule(perms: []))).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/api/v1/access-rules", admin, Rule(scopeValue: "brand..roadster"))).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/api/v1/access-rules", admin, Rule(scopeValue: "brand/roadster; drop"))).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/api/v1/access-rules", admin, Rule(scopeType: "collection", scopeValue: "not-a-guid"))).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/api/v1/access-rules", admin, Rule(scopeType: "all", scopeValue: "x"))).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/api/v1/access-rules", admin, Rule(scopeType: "galaxy"))).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/api/v1/access-rules", admin, Rule(attrs: new { planet = new[] { "Mars" } }))).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/api/v1/access-rules", admin, Rule(principal: Guid.NewGuid()))).Status);

        var ok = await Call(HttpMethod.Post, "/api/v1/access-rules", admin, Rule(attrs: new { region = new[] { "North" } }));
        Assert.Equal(HttpStatusCode.Created, ok.Status);
        var id = ok.Body.GetProperty("id").GetGuid();
        Assert.Equal("folder", ok.Body.GetProperty("scopeType").GetString());

        var upd = await Call(HttpMethod.Put, $"/api/v1/access-rules/{id}", admin, new { scopeType = "asset_type", scopeValue = "video", permissions = new[] { "assets.publish" } });
        Assert.Equal("assettype", upd.Body.GetProperty("scopeType").GetString());

        var filtered = await Call(HttpMethod.Get, $"/api/v1/access-rules?filter[principalType]=user&filter[principalId]={uid}", admin);
        Assert.Equal(1, filtered.Body.GetArrayLength());
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"/api/v1/access-rules/{id}", admin)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/access-rules/{id}", admin)).Status);
    }

    // ---- 7. the acceptance scenarios, end to end through the database ----------------------------------------

    /// <summary>Loads a user's profile exactly as the API does, without an HTTP request.</summary>
    private async Task<AccessProfile> ProfileOf(Guid tenant, Guid subject, params string[] tokenRoles)
    {
        await using var db = pg.AppContext(new TestTenant(tenant));
        var provider = new AccessProfileProvider(db, new TestTenant(tenant), new FakeUser(subject.ToString(), tokenRoles));
        return await provider.GetAsync(default);
    }

    private sealed class FakeUser(string subject, string[] roles) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid? UserId => Guid.Parse(subject);
        public string? Subject => subject;
        public IReadOnlyCollection<string> Roles => roles;
    }

    [Fact]
    public async Task Dealer_in_region_north_sees_published_north_or_global_assets_and_not_south()
    {
        var t = await NewTenantAsync();
        var north = Guid.NewGuid(); var south = Guid.NewGuid(); var noRegion = Guid.NewGuid();
        await Call(HttpMethod.Get, "/api/v1/me", ApiTestHost.TokenFor(t, north, ["Dealer"], attributes: Attrs("North", "D-N-001")));
        await Call(HttpMethod.Get, "/api/v1/me", ApiTestHost.TokenFor(t, south, ["Dealer"], attributes: Attrs("South", "D-S-001")));
        var stranded = await Call(HttpMethod.Get, "/api/v1/me", ApiTestHost.TokenFor(t, noRegion, ["Dealer"]));
        Assert.Equal(["Dealer"], Strings(stranded.Body.GetProperty("inactiveRoles")));
        Assert.Empty(Strings(stranded.Body.GetProperty("permissions"))); // /me tells the truth: nothing is effective

        var n = await ProfileOf(t, north, "Dealer");
        var s = await ProfileOf(t, south, "Dealer");
        var x = await ProfileOf(t, noRegion, "Dealer");

        var northAsset = ResourceContext.Asset(AssetStatuses.Published, region: ["North"]);
        var southAsset = ResourceContext.Asset(AssetStatuses.Published, region: ["South"]);
        var globalAsset = ResourceContext.Asset(AssetStatuses.Published, region: ["Global"]);
        var draftNorth = ResourceContext.Asset(AssetStatuses.Draft, region: ["North"]);

        Assert.True(PolicyEngine.Can(n, Permissions.AssetsRead, northAsset));
        Assert.True(PolicyEngine.Can(n, Permissions.AssetsRead, globalAsset));
        Assert.False(PolicyEngine.Can(n, Permissions.AssetsRead, southAsset));
        Assert.False(PolicyEngine.Can(n, Permissions.AssetsRead, draftNorth));
        Assert.True(PolicyEngine.Can(s, Permissions.AssetsRead, southAsset));
        Assert.False(PolicyEngine.Can(s, Permissions.AssetsRead, northAsset));
        Assert.False(PolicyEngine.Can(x, Permissions.AssetsRead, globalAsset)); // no region attribute: fail closed
    }

    [Fact]
    public async Task Approver_can_approve_only_in_folders_assigned_to_them_directly_or_via_a_group()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var sub = Guid.NewGuid();
        var token = ApiTestHost.TokenFor(t, sub, ["Approver"], ["brand-approvers"]);
        var userId = (await Call(HttpMethod.Get, "/api/v1/me", token)).Body.GetProperty("userId").GetGuid();
        var groupId = (await Call(HttpMethod.Get, "/api/v1/groups", admin)).Body.EnumerateArray().First().GetProperty("id").GetGuid();

        var roadster = ResourceContext.Asset(AssetStatuses.InReview, "brand.roadster.hero");
        var trail = ResourceContext.Asset(AssetStatuses.InReview, "brand.trail");
        var sedan = ResourceContext.Asset(AssetStatuses.InReview, "brand.sedan.launch");

        Assert.False(PolicyEngine.Can(await ProfileOf(t, sub, "Approver"), Permissions.AssetsApprove, roadster)); // role alone approves nothing

        await Call(HttpMethod.Post, "/api/v1/access-rules", admin, new
            { principalType = "user", principalId = userId, scopeType = "folder", scopeValue = "brand.roadster", permissions = new[] { "assets.approve" } });
        await Call(HttpMethod.Post, "/api/v1/access-rules", admin, new
            { principalType = "group", principalId = groupId, scopeType = "folder", scopeValue = "brand.sedan", permissions = new[] { "assets.approve" } });

        var p = await ProfileOf(t, sub, "Approver");
        Assert.True(PolicyEngine.Can(p, Permissions.AssetsApprove, roadster));      // assigned to the user
        Assert.True(PolicyEngine.Can(p, Permissions.AssetsApprove, sedan));   // assigned to their group
        Assert.False(PolicyEngine.Can(p, Permissions.AssetsApprove, trail));      // not assigned
        Assert.True(PolicyEngine.Can(p, Permissions.AssetsRead, trail));          // but they can still read it for review
    }

    [Fact]
    public async Task Tenants_never_see_each_others_identity_data_through_the_api()
    {
        var a = await NewTenantAsync(); var b = await NewTenantAsync();
        var adminA = Admin(a); var adminB = Admin(b);
        await Call(HttpMethod.Get, "/api/v1/me", adminA);
        await Call(HttpMethod.Get, "/api/v1/me", adminB);
        var bUser = (await Call(HttpMethod.Get, "/api/v1/users", adminB)).Body.GetProperty("items")[0].GetProperty("id").GetGuid();
        await Call(HttpMethod.Post, "/api/v1/roles", adminB, new { name = "B-only", permissions = new[] { "assets.read" } });
        await Call(HttpMethod.Post, "/api/v1/groups", adminB, new { name = "B-group" });

        var usersA = (await Call(HttpMethod.Get, "/api/v1/users", adminA)).Body.GetProperty("items");
        Assert.Equal(1, usersA.GetArrayLength());
        Assert.NotEqual(bUser, usersA[0].GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/users/{bUser}", adminA)).Status);
        Assert.DoesNotContain(
            (await Call(HttpMethod.Get, "/api/v1/roles", adminA)).Body.EnumerateArray().Select(r => r.GetProperty("name").GetString()), n => n == "B-only");
        Assert.Equal(0, (await Call(HttpMethod.Get, "/api/v1/groups", adminA)).Body.GetArrayLength());

        // A rule for tenant B's user cannot be created from tenant A, and a disable attempt is a 404.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/api/v1/access-rules", adminA, new
            { principalType = "user", principalId = bUser, scopeType = "all", permissions = new[] { "assets.read" } })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Patch, $"/api/v1/users/{bUser}", adminA, new { status = "disabled" })).Status);
    }

    // ---- 8. row-level security on the tenants table ----------------------------------------------------------

    [Fact]
    public async Task The_tenants_table_is_isolated_by_row_level_security()
    {
        var a = await NewTenantAsync(); var b = await NewTenantAsync();
        using var conn = pg.RawAppConnection(a);
        long Scalar(string sql) { using var c = new NpgsqlCommand(sql, conn); return Convert.ToInt64(c.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture); }

        Assert.Equal(1, Scalar("SELECT count(*) FROM tenants"));
        Assert.Equal(0, Scalar($"SELECT count(*) FROM tenants WHERE id = '{b}'"));
        Assert.Equal(0, Scalar($"WITH u AS (UPDATE tenants SET name = 'hijacked' WHERE id = '{b}' RETURNING 1) SELECT count(*) FROM u"));

        await using var insert = new NpgsqlCommand(
            $"INSERT INTO tenants (id, name, slug, status, settings_json, storage_config_json, created_at) VALUES ('{Guid.NewGuid()}', 'x', 'x-{Guid.NewGuid():N}', 'Active', '{{}}', '{{}}', now())", conn);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState);
    }
}
