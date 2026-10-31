using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dam.Domain.Integrations;
using Dam.Infrastructure.Integrations;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Dam.IntegrationTests;

/// <summary>
/// The integrations framework (ADR-0040) through the API: connectors, connections (validation, secrets, limits, names, permissions, isolation), and the calls to a pretend
/// third-party system for every authentication strategy, including token reuse, expiry, the one-time 401 retry, single-flight sign-in, test results, the rate limit and what is
/// (not) written to the audit trail and the activity log. Every test uses its own tenant.
/// </summary>
[Collection("pg")]
public sealed class IntegrationsApiTests(PostgresFixture pg) : ApiTestBase(pg)
{
    private const string Root = "/api/v1/integrations";
    private const string Connections = Root + "/connections";

    // ---- helpers -------------------------------------------------------------------------------------------------

    private void Sql(string sql)
    {
        using var c = new NpgsqlConnection(Pg.OwnerConnection);
        c.Open();
        using var cmd = new NpgsqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql)
    {
        using var c = new NpgsqlConnection(Pg.OwnerConnection);
        c.Open();
        using var cmd = new NpgsqlCommand(sql, c);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? default! : (T)value;
    }

    private async Task<JsonElement> Create(string admin, object body, HttpStatusCode expected = HttpStatusCode.Created)
    {
        var r = await Call(HttpMethod.Post, Connections, admin, body);
        Assert.True(r.Status == expected, $"{(int)r.Status}: {r.Body}");
        return r.Body;
    }

    private static Dictionary<string, object?> Input(string connector, string name, Dictionary<string, object?> config, Dictionary<string, string>? secrets = null, bool enabled = true) =>
        new() { ["connector"] = connector, ["name"] = name, ["enabled"] = enabled, ["config"] = config, ["secrets"] = secrets ?? [] };

    private static Dictionary<string, object?> ApiKeyHeaderConnection(FakeThirdParty f, string name = "Acme orders", string? testPath = "/health") => Input("http", name,
        new() { ["baseUrl"] = f.BaseUrl, ["authStrategy"] = "api_key", ["apiKeyName"] = "X-API-Key", ["testPath"] = testPath }, new() { ["apiKey"] = FakeThirdParty.ApiKey });

    private static Dictionary<string, object?> LoginConnection(FakeThirdParty f, string name = "Acme sign-in", string connector = "http", string loginUrl = "/login",
        Dictionary<string, object?>? more = null)
    {
        var config = new Dictionary<string, object?>
        {
            ["baseUrl"] = f.BaseUrl, ["authStrategy"] = "login_call", ["loginUrl"] = loginUrl, ["username"] = FakeThirdParty.User, ["testPath"] = "/health",
        };
        foreach (var (k, v) in more ?? []) config[k] = v;
        return Input(connector, name, config, new() { ["password"] = FakeThirdParty.Password });
    }

    private async Task<(JsonElement Body, Guid Id)> CreateLogin(string admin, FakeThirdParty f, string name = "Acme sign-in", string connector = "http", string loginUrl = "/login",
        Dictionary<string, object?>? more = null)
    {
        var body = await Create(admin, LoginConnection(f, name, connector, loginUrl, more));
        return (body, body.GetProperty("id").GetGuid());
    }

    private void FreeTheTestLimit(Guid id) => Sql($"UPDATE integration_connections SET test_claimed_at = NULL WHERE id = '{id}'");

    private async Task<JsonElement> Test(string admin, Guid id)
    {
        FreeTheTestLimit(id);
        var r = await Call(HttpMethod.Post, $"{Connections}/{id}/test", admin);
        Assert.True(r.Status == HttpStatusCode.OK, $"{(int)r.Status}: {r.Body}");
        return r.Body;
    }

    private async Task<JsonElement[]> Activity(string admin, Guid id, string query = "")
    {
        var r = await Call(HttpMethod.Get, $"{Connections}/{id}/activity{query}", admin);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        return r.Body.GetProperty("items").EnumerateArray().ToArray();
    }

    /// <summary>One authenticated call made the way a connector action will make it: straight through the runtime, in the connection's tenant.</summary>
    private async Task<CallResult> Send(Guid tenant, Guid id, string path = "/things")
    {
        await using var scope = Api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<Dam.Api.Auth.TenantOverride>().Value = tenant;
        var connection = await scope.ServiceProvider.GetRequiredService<DamDbContext>().IntegrationConnections.FirstAsync(c => c.Id == id);
        return await scope.ServiceProvider.GetRequiredService<ConnectionRuntime>().SendAsync(connection, CallKinds.Action, HttpMethod.Get, path, default);
    }

    private static string Steps(JsonElement result) => string.Join(" | ", result.GetProperty("steps").EnumerateArray().Select(s => $"{s.GetProperty("name").GetString()}:{s.GetProperty("ok").GetBoolean()}:{s.GetProperty("detail").GetString()}"));

    /// <summary>Everything stored about the tenant's integrations and events as text, to look for secrets in.</summary>
    private string Everything(Guid tenant) => string.Join("\n", new[]
    {
        "SELECT coalesce(string_agg(x::text, E'\\n'), '') FROM audit_log x WHERE tenant_id = '" + tenant + "'",
        "SELECT coalesce(string_agg(x::text, E'\\n'), '') FROM outbox x WHERE (x.payload::text) LIKE '%" + tenant + "%' OR x.payload::text LIKE '%integration%'",
        "SELECT coalesce(string_agg(x::text, E'\\n'), '') FROM integration_calls x WHERE tenant_id = '" + tenant + "'",
    }.Select(sql => Scalar<string>(sql)));

    private static void AssertNone(string haystack, params string[] needles)
    {
        foreach (var needle in needles) Assert.DoesNotContain(needle, haystack, StringComparison.Ordinal);
    }

    // ---- the catalogue -------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_catalogue_lists_http_available_pads4_preview_and_canva_planned_with_the_fields_each_needs()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var r = await Call(HttpMethod.Get, $"{Root}/connectors", admin);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        var list = r.Body.EnumerateArray().ToArray();
        Assert.Equal(["http", "pads4", "canva"], list.Select(c => c.GetProperty("key").GetString()!).ToArray());
        Assert.Equal(["available", "preview", "planned"], list.Select(c => c.GetProperty("status").GetString()!).ToArray());

        var http = list[0];
        Assert.Equal("api_key", http.GetProperty("defaultAuthStrategy").GetString());
        Assert.Equal(["none", "api_key", "bearer", "basic", "oauth2_client_credentials", "login_call"], Strings(http.GetProperty("authStrategies")));
        var fields = http.GetProperty("fields").EnumerateArray().ToDictionary(f => f.GetProperty("key").GetString()!);
        Assert.True(fields["baseUrl"].GetProperty("required").GetBoolean());
        Assert.Equal("url", fields["baseUrl"].GetProperty("type").GetString());
        Assert.Equal("secret", fields["password"].GetProperty("type").GetString());
        Assert.Equal("authentication", fields["password"].GetProperty("group").GetString());
        Assert.Equal(["basic", "login_call"], Strings(fields["password"].GetProperty("showWhen").GetProperty("equals")));
        Assert.Equal("authStrategy", fields["password"].GetProperty("showWhen").GetProperty("field").GetString());
        Assert.Equal("advanced", fields["tokenHeader"].GetProperty("group").GetString());
        Assert.Equal("Authorization", fields["tokenHeader"].GetProperty("default").GetString());
        Assert.Equal(["POST", "PUT"], fields["loginMethod"].GetProperty("options").EnumerateArray().Select(o => o.GetProperty("value").GetString()!).ToArray());

        // PADS4 only stores four values for now: no strategy, no capability.
        var pads4 = list[1];
        Assert.Equal(JsonValueKind.Null, pads4.GetProperty("defaultAuthStrategy").ValueKind);
        Assert.Equal(0, pads4.GetProperty("authStrategies").GetArrayLength());
        Assert.Equal(0, pads4.GetProperty("capabilities").GetArrayLength());
        Assert.Equal(["test"], Strings(http.GetProperty("capabilities")));
        Assert.Equal(["baseUrl:url:True", "domain:text:True", "username:text:True", "password:secret:True"],
            pads4.GetProperty("fields").EnumerateArray().Select(f => $"{f.GetProperty("key").GetString()}:{f.GetProperty("type").GetString()}:{f.GetProperty("required").GetBoolean()}").ToArray());
        Assert.All(pads4.GetProperty("fields").EnumerateArray(), f => Assert.Equal("connection", f.GetProperty("group").GetString()));
        Assert.Equal("PADS4 server URL", pads4.GetProperty("fields")[0].GetProperty("label").GetString());

        var canva = list[2];
        Assert.Equal(0, canva.GetProperty("fields").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, canva.GetProperty("defaultAuthStrategy").ValueKind);
    }

    // ---- create, read, secrets -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_connection_is_created_with_its_secrets_stored_encrypted_and_only_ever_shown_as_set_with_a_hint()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var third = await FakeThirdParty.StartAsync();
        var created = await Call(HttpMethod.Post, Connections, admin, ApiKeyHeaderConnection(third));
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var id = created.Body.GetProperty("id").GetGuid();
        Assert.Equal(("http", "Acme orders", true, "new"), (created.Body.GetProperty("connector").GetString(), created.Body.GetProperty("name").GetString(),
            created.Body.GetProperty("enabled").GetBoolean(), created.Body.GetProperty("status").GetString()));
        Assert.Equal(third.BaseUrl, created.Body.GetProperty("config").GetProperty("baseUrl").GetString());
        Assert.Equal("X-API-Key", created.Body.GetProperty("config").GetProperty("apiKeyName").GetString());
        Assert.Equal("header", created.Body.GetProperty("config").GetProperty("apiKeyIn").GetString());   // a default

        var secrets = created.Body.GetProperty("secrets");
        Assert.True(secrets.GetProperty("apiKey").GetProperty("set").GetBoolean());
        Assert.Equal(FakeThirdParty.ApiKey[^4..], secrets.GetProperty("apiKey").GetProperty("hint").GetString());
        foreach (var other in new[] { "bearerToken", "password", "clientSecret" })
        {
            Assert.False(secrets.GetProperty(other).GetProperty("set").GetBoolean());
            Assert.Equal(JsonValueKind.Null, secrets.GetProperty(other).GetProperty("hint").ValueKind);
        }

        foreach (var response in new[] { created.Body, (await Call(HttpMethod.Get, $"{Connections}/{id}", admin)).Body, (await Call(HttpMethod.Get, Connections, admin)).Body[0] })
            AssertNone(response.ToString(), FakeThirdParty.ApiKey);

        // At rest it is ciphertext bound to this connection, not the key; nothing else holds it either.
        var stored = Scalar<string>($"SELECT secrets::text FROM integration_connections WHERE id = '{id}'");
        Assert.Contains("v1.", stored, StringComparison.Ordinal);
        AssertNone(stored + Scalar<string>($"SELECT config::text FROM integration_connections WHERE id = '{id}'"), FakeThirdParty.ApiKey);
        AssertNone(Everything(t), FakeThirdParty.ApiKey);
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'integration.connection.created'"));
        Assert.Equal(1, (await Call(HttpMethod.Get, Connections, admin)).Body.GetArrayLength());
    }

    [Fact]
    public async Task A_short_secret_has_no_hint_and_a_connection_without_a_test_path_is_fine()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var third = await FakeThirdParty.StartAsync();
        var body = await Create(admin, Input("http", "Short key", new() { ["baseUrl"] = third.BaseUrl, ["authStrategy"] = "bearer" }, new() { ["bearerToken"] = "short" }, enabled: false));
        Assert.False(body.GetProperty("enabled").GetBoolean());
        Assert.True(body.GetProperty("secrets").GetProperty("bearerToken").GetProperty("set").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("secrets").GetProperty("bearerToken").GetProperty("hint").ValueKind);
        Assert.False(body.GetProperty("config").TryGetProperty("testPath", out _));
    }

    [Fact]
    public async Task Updating_replaces_a_secret_that_is_sent_keeps_one_that_is_left_out_and_clears_the_named_ones()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var third = await FakeThirdParty.StartAsync();
        var created = await Create(admin, Input("http", "Keys", new() { ["baseUrl"] = third.BaseUrl, ["authStrategy"] = "basic", ["username"] = "ann" },
            new() { ["password"] = "first-password-1111", ["clientSecret"] = "client-secret-2222" }));
        var id = created.GetProperty("id").GetGuid();
        var path = $"{Connections}/{id}";
        object Put(Dictionary<string, string>? secrets = null, string[]? clear = null, string strategy = "basic") => new
        {
            name = "Keys", enabled = true, config = new Dictionary<string, object?> { ["baseUrl"] = third.BaseUrl, ["authStrategy"] = strategy, ["username"] = "ann" },
            secrets = secrets ?? [], clearSecrets = clear ?? [],
        };
        string Hint(JsonElement c, string secret) => c.GetProperty("secrets").GetProperty(secret).GetProperty("hint").GetString()!;

        // Left out: kept.
        var kept = await Call(HttpMethod.Put, path, admin, Put());
        Assert.Equal(HttpStatusCode.OK, kept.Status);
        Assert.Equal(("1111", "2222"), (Hint(kept.Body, "password"), Hint(kept.Body, "clientSecret")));
        // Sent as empty text: also kept (a form that was not touched).
        var empty = await Call(HttpMethod.Put, path, admin, Put(new() { ["password"] = "" }));
        Assert.Equal("1111", Hint(empty.Body, "password"));
        // Sent: replaced.
        var replaced = await Call(HttpMethod.Put, path, admin, Put(new() { ["password"] = "second-password-3333" }));
        Assert.Equal(("3333", "2222"), (Hint(replaced.Body, "password"), Hint(replaced.Body, "clientSecret")));
        // Cleared: gone.
        var cleared = await Call(HttpMethod.Put, path, admin, Put(clear: ["clientSecret"]));
        Assert.Equal(HttpStatusCode.OK, cleared.Status);
        Assert.False(cleared.Body.GetProperty("secrets").GetProperty("clientSecret").GetProperty("set").GetBoolean());
        Assert.True(cleared.Body.GetProperty("secrets").GetProperty("password").GetProperty("set").GetBoolean());
        // A secret the strategy needs cannot be cleared; one cannot be both sent and cleared.
        var needed = await Call(HttpMethod.Put, path, admin, Put(clear: ["password"]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, needed.Status);
        Assert.True(needed.Body.GetProperty("errors").TryGetProperty("secrets.password", out _));
        var both = await Call(HttpMethod.Put, path, admin, Put(new() { ["password"] = "third-password-4444" }, ["password"]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, both.Status);

        // The audit rows name what changed, never the values.
        var changed = Scalar<string>("SELECT string_agg(after::text, ' ') FROM audit_log WHERE action = 'integration.connection.updated' AND entity_id = '" + id + "'");
        Assert.Contains("secrets.password", changed, StringComparison.Ordinal);
        Assert.Contains("secrets.clientSecret", changed, StringComparison.Ordinal);
        AssertNone(Everything(t), "first-password-1111", "second-password-3333", "third-password-4444", "client-secret-2222");
    }

    [Fact]
    public async Task Changing_the_settings_or_secrets_drops_the_saved_token_and_the_last_test_result_but_renaming_does_not()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var third = await FakeThirdParty.StartAsync();
        var (_, id) = await CreateLogin(admin, third);
        Assert.True((await Test(admin, id)).GetProperty("ok").GetBoolean());
        var tested = (await Call(HttpMethod.Get, $"{Connections}/{id}", admin)).Body;
        Assert.Equal("ok", tested.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, tested.GetProperty("tokenExpiresAt").ValueKind);

        object Put(string name, string testPath) => new
        {
            name, enabled = true, config = new Dictionary<string, object?>
            { ["baseUrl"] = third.BaseUrl, ["authStrategy"] = "login_call", ["loginUrl"] = "/login", ["username"] = FakeThirdParty.User, ["testPath"] = testPath },
        };
        var renamed = (await Call(HttpMethod.Put, $"{Connections}/{id}", admin, Put("Renamed", "/health"))).Body;
        Assert.Equal(("ok", "Renamed"), (renamed.GetProperty("status").GetString(), renamed.GetProperty("name").GetString()));
        Assert.NotEqual(JsonValueKind.Null, renamed.GetProperty("tokenExpiresAt").ValueKind);

        var moved = (await Call(HttpMethod.Put, $"{Connections}/{id}", admin, Put("Renamed", "/things"))).Body;
        Assert.Equal("new", moved.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, moved.GetProperty("tokenExpiresAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, moved.GetProperty("lastOkAt").ValueKind);   // the history of the last good test stays
    }

    // ---- validation ----------------------------------------------------------------------------------------------

    private async Task<JsonElement> Errors(string admin, object body)
    {
        var r = await Call(HttpMethod.Post, Connections, admin, body);
        Assert.True(r.Status == HttpStatusCode.UnprocessableEntity, $"{(int)r.Status}: {r.Body}");
        return r.Body.GetProperty("errors");
    }

    [Fact]
    public async Task Bad_input_is_a_422_that_names_each_field_as_config_or_secrets_dot_field()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var ok = new Dictionary<string, object?> { ["baseUrl"] = "https://api.example.test", ["authStrategy"] = "none" };

        Assert.True((await Errors(admin, Input("http", "", ok))).TryGetProperty("name", out _));
        Assert.True((await Errors(admin, Input("nope", "x", ok))).TryGetProperty("connector", out _));
        Assert.True((await Errors(admin, new { name = "x", config = ok })).TryGetProperty("connector", out _));

        var errors = await Errors(admin, Input("http", "x", new()
        {
            ["baseUrl"] = "ftp://api.example.test", ["authStrategy"] = "magic", ["bogus"] = "1", ["apiKey"] = "leaked-in-config", ["testPath"] = "https://elsewhere.example.test/x",
        }, new() { ["nope"] = "x", ["baseUrl"] = "x" }));
        foreach (var key in new[] { "config.baseUrl", "config.authStrategy", "config.bogus", "config.apiKey", "secrets.nope", "secrets.baseUrl" }) Assert.True(errors.TryGetProperty(key, out _), key);
        AssertNone(errors.ToString(), "leaked-in-config");

        var missing = await Errors(admin, Input("http", "x", new() { ["authStrategy"] = "basic" }));
        foreach (var key in new[] { "config.baseUrl", "config.username", "secrets.password" }) Assert.True(missing.TryGetProperty(key, out _), key);

        var header = await Errors(admin, Input("http", "x", new() { ["baseUrl"] = "https://api.example.test", ["authStrategy"] = "api_key", ["apiKeyName"] = "Host" }, new() { ["apiKey"] = "key-12345678" }));
        Assert.True(header.TryGetProperty("config.apiKeyName", out _));

        var login = await Errors(admin, Input("http", "x", new()
        {
            ["baseUrl"] = "https://api.example.test", ["authStrategy"] = "login_call", ["loginUrl"] = "/login", ["username"] = "ann", ["loginBody"] = """{"p":"{{nope}}"}""", ["tokenPointer"] = "access_token",
            ["tokenHeader"] = "Bad Header", ["expirySeconds"] = 5,
        }, new() { ["password"] = "pw-1234567" }));
        foreach (var key in new[] { "config.loginBody", "config.tokenPointer", "config.tokenHeader", "config.expirySeconds" }) Assert.True(login.TryGetProperty(key, out _), key);

        var secret = await Errors(admin, Input("http", "x", ok, new() { ["apiKey"] = "line1\nline2" }));
        Assert.True(secret.TryGetProperty("secrets.apiKey", out _));
        Assert.Equal(0, Count(t, "SELECT count(*) FROM integration_connections"));
    }

    [Fact]
    public async Task A_planned_connector_cannot_be_connected()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var errors = await Errors(admin, Input("canva", "Design tool", []));
        Assert.Contains("planned", errors.GetProperty("connector")[0].GetString(), StringComparison.Ordinal);
        Assert.Equal(0, Count(t, "SELECT count(*) FROM integration_connections"));
    }

    [Fact]
    public async Task A_name_is_unique_per_tenant_ignoring_case_but_two_tenants_may_share_one()
    {
        var a = await NewTenantAsync(); var b = await NewTenantAsync();
        var adminA = Admin(a); var adminB = Admin(b);
        var body = Input("http", "Acme Orders", new() { ["baseUrl"] = "https://api.example.test", ["authStrategy"] = "none" });
        var first = await Create(adminA, body);
        var duplicate = await Call(HttpMethod.Post, Connections, adminA, Input("http", "acme orders", new() { ["baseUrl"] = "https://api.example.test", ["authStrategy"] = "none" }));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.Status);
        Assert.Contains("already exists", duplicate.Body.GetProperty("title").GetString(), StringComparison.Ordinal);
        await Create(adminB, body);   // another tenant: no clash

        var second = await Create(adminA, Input("http", "Second", new() { ["baseUrl"] = "https://api.example.test", ["authStrategy"] = "none" }));
        object Put(string name) => new { name, enabled = true, config = new Dictionary<string, object?> { ["baseUrl"] = "https://api.example.test", ["authStrategy"] = "none" } };
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Put, $"{Connections}/{second.GetProperty("id").GetGuid()}", adminA, Put("ACME ORDERS"))).Status);
        // Keeping its own name (even in other case) is not a clash.
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, $"{Connections}/{first.GetProperty("id").GetGuid()}", adminA, Put("ACME orders"))).Status);
        Assert.Equal(2, Count(a, "SELECT count(*) FROM integration_connections"));
    }

    [Fact]
    public async Task A_tenant_may_have_at_most_fifty_connections()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        object Body(int i) => Input("http", $"Connection {i:00}", new() { ["baseUrl"] = "https://api.example.test", ["authStrategy"] = "none" });
        await Create(admin, Body(0));
        Sql($$"""
            INSERT INTO integration_connections (id, tenant_id, connector, name, enabled, config, secrets, status, created_at, updated_at)
            SELECT gen_random_uuid(), '{{t}}', 'http', 'Filler ' || g, true, '{}', '{}', 'new', now(), now() FROM generate_series(1, 48) g
            """);
        await Create(admin, Body(1));   // the fiftieth
        var over = await Call(HttpMethod.Post, Connections, admin, Body(50));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, over.Status);
        Assert.Contains("50", over.Body.GetProperty("errors").GetProperty("name")[0].GetString(), StringComparison.Ordinal);
        Assert.Equal(50, Count(t, "SELECT count(*) FROM integration_connections"));
        // Deleting one makes room.
        var first = (await Call(HttpMethod.Get, Connections, admin)).Body[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"{Connections}/{first}", admin)).Status);
        await Create(admin, Body(50));
    }

    [Fact]
    public async Task A_connections_connector_cannot_be_changed()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var created = await Create(admin, Input("http", "Fixed", new() { ["baseUrl"] = "https://api.example.test", ["authStrategy"] = "none" }));
        var put = await Call(HttpMethod.Put, $"{Connections}/{created.GetProperty("id").GetGuid()}", admin,
            new { connector = "pads4", name = "Fixed", enabled = true, config = new Dictionary<string, object?> { ["baseUrl"] = "https://api.example.test", ["authStrategy"] = "none" } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.Status);
        Assert.True(put.Body.GetProperty("errors").TryGetProperty("connector", out _));
    }

    [Fact]
    public async Task Deleting_a_connection_removes_it_its_secrets_and_its_activity_and_is_audited_without_values()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var third = await FakeThirdParty.StartAsync();
        var created = await Create(admin, ApiKeyHeaderConnection(third));
        var id = created.GetProperty("id").GetGuid();
        await Test(admin, id);
        Assert.Equal(1, Count(t, "SELECT count(*) FROM integration_calls"));

        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"{Connections}/{id}", admin)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"{Connections}/{id}", admin)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Delete, $"{Connections}/{id}", admin)).Status);
        Assert.Equal(0, Count(t, "SELECT count(*) FROM integration_connections"));
        Assert.Equal(0, Count(t, "SELECT count(*) FROM integration_calls"));   // the cascade works for the restricted app role
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'integration.connection.deleted'"));
        AssertNone(Everything(t), FakeThirdParty.ApiKey);
    }

    // ---- permissions and isolation -------------------------------------------------------------------------------

    [Fact]
    public async Task Only_the_admin_can_use_the_integrations_surface_and_a_stranger_is_turned_away()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var created = await Create(admin, Input("http", "Guarded", new() { ["baseUrl"] = "https://api.example.test", ["authStrategy"] = "none" }));
        var id = created.GetProperty("id").GetGuid();
        var input = Input("http", "Another", new() { ["baseUrl"] = "https://api.example.test", ["authStrategy"] = "none" });

        foreach (var role in new[] { "Librarian", "Editor", "Approver", "Contributor", "Viewer", "Dealer", "ApiClient" })
        {
            var token = As(t, role);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, $"{Root}/connectors", token)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, Connections, token)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, $"{Connections}/{id}", token)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, Connections, token, input)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Put, $"{Connections}/{id}", token, input)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Delete, $"{Connections}/{id}", token)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, $"{Connections}/{id}/test", token)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, $"{Connections}/{id}/activity", token)).Status);
        }
        Assert.Equal(1, Count(t, "SELECT count(*) FROM integration_connections"));

        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, $"{Root}/connectors", admin)).Status);
        using var anonymous = Api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Root}/connectors")).StatusCode);
    }

    [Fact]
    public async Task A_custom_role_with_the_permission_can_manage_integrations()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var role = (await Call(HttpMethod.Post, "/api/v1/roles", admin, new { name = "Integrator", permissions = new[] { "integrations.manage" }, attributeRestricted = false })).Body;
        var subject = Guid.NewGuid();
        var token = ApiTestHost.TokenFor(t, subject, roles: []);
        var me = (await Call(HttpMethod.Get, "/api/v1/me", token)).Body;
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, Connections, token)).Status);
        var assigned = await Call(HttpMethod.Put, $"/api/v1/users/{me.GetProperty("userId").GetGuid()}/roles", admin, new { ids = new[] { role.GetProperty("id").GetGuid() } });
        Assert.Equal(HttpStatusCode.OK, assigned.Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, Connections, token)).Status);
    }

    [Fact]
    public async Task Another_tenants_connection_does_not_exist_for_you()
    {
        var a = await NewTenantAsync(); var b = await NewTenantAsync();
        var adminA = Admin(a); var adminB = Admin(b);
        await using var third = await FakeThirdParty.StartAsync();
        var theirs = (await Create(adminA, ApiKeyHeaderConnection(third))).GetProperty("id").GetGuid();
        await Test(adminA, theirs);

        var input = ApiKeyHeaderConnection(third, "Mine");
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"{Connections}/{theirs}", adminB)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Put, $"{Connections}/{theirs}", adminB, input)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Delete, $"{Connections}/{theirs}", adminB)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"{Connections}/{theirs}/test", adminB)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"{Connections}/{theirs}/activity", adminB)).Status);
        Assert.Equal(0, (await Call(HttpMethod.Get, Connections, adminB)).Body.GetArrayLength());
        Assert.Equal(1, Count(a, "SELECT count(*) FROM integration_connections"));
        Assert.Single(third.Api);   // B's attempt to test A's connection reached nobody
    }

    [Fact]
    public async Task Row_level_security_holds_on_both_tables_for_the_app_role()
    {
        var a = await NewTenantAsync(); var b = await NewTenantAsync();
        var adminA = Admin(a);
        await using var third = await FakeThirdParty.StartAsync();
        var id = (await Create(adminA, ApiKeyHeaderConnection(third))).GetProperty("id").GetGuid();
        await Test(adminA, id);
        Assert.Equal(1, Count(a, "SELECT count(*) FROM integration_connections"));
        Assert.Equal(1, Count(a, "SELECT count(*) FROM integration_calls"));

        Assert.Equal(0, Count(b, "SELECT count(*) FROM integration_connections"));
        Assert.Equal(0, Count(b, "SELECT count(*) FROM integration_calls"));
        using var none = Pg.RawAppConnection(null);
        using (var cmd = new NpgsqlCommand("SELECT (SELECT count(*) FROM integration_connections) + (SELECT count(*) FROM integration_calls)", none))
            Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));

        // B cannot write into A, nor change or remove A's rows.
        using var asB = Pg.RawAppConnection(b);
        using (var insert = new NpgsqlCommand($"INSERT INTO integration_calls (id, tenant_id, connection_id, kind, method, target, ok, duration_ms, created_at) VALUES (gen_random_uuid(), '{a}', '{id}', 'test', 'GET', 'x', true, 1, now())", asB))
            Assert.ThrowsAny<PostgresException>(() => insert.ExecuteNonQuery());
        using (var update = new NpgsqlCommand($"UPDATE integration_connections SET name = 'hijacked' WHERE id = '{id}'", asB)) Assert.Equal(0, update.ExecuteNonQuery());
        using (var delete = new NpgsqlCommand($"DELETE FROM integration_calls WHERE connection_id = '{id}'", asB)) Assert.Equal(0, delete.ExecuteNonQuery());
        Assert.Equal(1, Count(a, "SELECT count(*) FROM integration_calls"));
        Assert.Equal("Acme orders", Scalar<string>($"SELECT name FROM integration_connections WHERE id = '{id}'"));
    }

    [Fact]
    public async Task Calls_are_history_the_app_role_can_add_and_trim_but_never_edit()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var third = await FakeThirdParty.StartAsync();
        var id = (await Create(admin, ApiKeyHeaderConnection(third))).GetProperty("id").GetGuid();
        await Test(admin, id);
        using var app = Pg.RawAppConnection(t);
        using var edit = new NpgsqlCommand("UPDATE integration_calls SET ok = false", app);
        Assert.ThrowsAny<PostgresException>(() => edit.ExecuteNonQuery());   // history is append-only for the app role (insert and trim only)
    }

    // ---- every strategy against a pretend third party ------------------------------------------------------------

    [Fact]
    public async Task An_api_key_in_a_header_is_sent_with_the_test_request_and_the_test_updates_the_connection()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var third = await FakeThirdParty.StartAsync();
        var id = (await Create(admin, ApiKeyHeaderConnection(third))).GetProperty("id").GetGuid();

        var result = await Test(admin, id);
        Assert.True(result.GetProperty("ok").GetBoolean(), Steps(result));
        var steps = result.GetProperty("steps").EnumerateArray().ToArray();
        Assert.Equal(["Authenticate", "Test request"], steps.Select(s => s.GetProperty("name").GetString()!).ToArray());
        Assert.All(steps, s => Assert.True(s.GetProperty("ok").GetBoolean()));
        Assert.Contains("GET /health answered 200", steps[1].GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.True(result.GetProperty("durationMs").GetInt32() >= 0);

        var hit = Assert.Single(third.Api);
        Assert.Equal(("GET", "/health", FakeThirdParty.ApiKey), (hit.Method, hit.Path, hit.Headers["X-API-Key"]));
        Assert.Equal(0, third.Logins);

        var connection = (await Call(HttpMethod.Get, $"{Connections}/{id}", admin)).Body;
        Assert.Equal("ok", connection.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, connection.GetProperty("lastTestedAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, connection.GetProperty("lastOkAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, connection.GetProperty("statusDetail").ValueKind);
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'integration.connection.tested' AND after->>'Ok' = 'true'"));

        var activity = Assert.Single(await Activity(admin, id));
        Assert.Equal(("test", "GET", true, 200), (activity.GetProperty("kind").GetString(), activity.GetProperty("method").GetString(), activity.GetProperty("ok").GetBoolean(), activity.GetProperty("statusCode").GetInt32()));
        Assert.Equal(new Uri(third.BaseUrl).Authority + "/health", activity.GetProperty("target").GetString());
        Assert.Equal(JsonValueKind.Null, activity.GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task An_api_key_in_the_query_reaches_the_server_but_is_never_recorded()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var third = await FakeThirdParty.StartAsync();
        var id = (await Create(admin, Input("http", "Query key", new()
            { ["baseUrl"] = third.BaseUrl, ["authStrategy"] = "api_key", ["apiKeyIn"] = "query", ["apiKeyName"] = "api_key", ["testPath"] = "/health?verbose=1" },
            new() { ["apiKey"] = FakeThirdParty.ApiKey }))).GetProperty("id").GetGuid();

        var result = await Test(admin, id);
        Assert.True(result.GetProperty("ok").GetBoolean(), Steps(result));
        var hit = Assert.Single(third.Api);
        Assert.Contains("verbose=1", hit.Query, StringComparison.Ordinal);
        Assert.Contains("api_key=" + FakeThirdParty.ApiKey, hit.Query, StringComparison.Ordinal);
        Assert.False(hit.Headers.ContainsKey("X-API-Key"));
        AssertNone(Steps(result), FakeThirdParty.ApiKey, "verbose");
        var activity = Assert.Single(await Activity(admin, id));
        Assert.EndsWith("/health", activity.GetProperty("target").GetString(), StringComparison.Ordinal);
        AssertNone(Everything(t), FakeThirdParty.ApiKey, "verbose=1");
    }

    [Fact]
    public async Task A_bearer_token_and_basic_authentication_are_sent_as_the_authorization_header()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var third = await FakeThirdParty.StartAsync();
        var bearer = (await Create(admin, Input("http", "Bearer", new() { ["baseUrl"] = third.BaseUrl, ["authStrategy"] = "bearer", ["testPath"] = "/things" }, new() { ["bearerToken"] = FakeThirdParty.BearerToken }))).GetProperty("id").GetGuid();
        var basic = (await Create(admin, Input("http", "Basic", new() { ["baseUrl"] = third.BaseUrl, ["authStrategy"] = "basic", ["username"] = FakeThirdParty.User, ["testPath"] = "/things" }, new() { ["password"] = FakeThirdParty.Password }))).GetProperty("id").GetGuid();

        Assert.True((await Test(admin, bearer)).GetProperty("ok").GetBoolean());
        Assert.True((await Test(admin, basic)).GetProperty("ok").GetBoolean());
        Assert.Equal("Bearer " + FakeThirdParty.BearerToken, third.Api[0].Headers["Authorization"]);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{FakeThirdParty.User}:{FakeThirdParty.Password}")), third.Api[1].Headers["Authorization"]);
        AssertNone(Everything(t), FakeThirdParty.BearerToken, FakeThirdParty.Password);
    }

    [Fact]
    public async Task Oauth_client_credentials_fetches_a_token_with_the_client_and_uses_it_and_the_token_is_saved_encrypted()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var third = await FakeThirdParty.StartAsync();
        third.ExpiresIn = 1800;
        var id = (await Create(admin, Input("http", "OAuth", new()
            { ["baseUrl"] = third.BaseUrl, ["authStrategy"] = "oauth2_client_credentials", ["tokenUrl"] = third.BaseUrl + "/oauth/token", ["clientId"] = FakeThirdParty.ClientId, ["scope"] = "read", ["testPath"] = "/things" },
            new() { ["clientSecret"] = FakeThirdParty.ClientSecret }))).GetProperty("id").GetGuid();

        var result = await Test(admin, id);
        Assert.True(result.GetProperty("ok").GetBoolean(), Steps(result));
        Assert.Contains("Signed in", result.GetProperty("steps")[0].GetProperty("detail").GetString(), StringComparison.Ordinal);
        var login = third.Hits.Single(h => h.Path == "/oauth/token");
        Assert.Contains($"client_id={FakeThirdParty.ClientId}", login.Body, StringComparison.Ordinal);
        Assert.Contains("scope=read", login.Body, StringComparison.Ordinal);
        var token = third.Api.Single().Headers["Authorization"][7..];
        Assert.StartsWith("Bearer ", third.Api.Single().Headers["Authorization"], StringComparison.Ordinal);

        var row = Scalar<string>($"SELECT token_cipher FROM integration_connections WHERE id = '{id}'");
        Assert.StartsWith("v1.", row, StringComparison.Ordinal);
        AssertNone(row, token);
        var expires = (await Call(HttpMethod.Get, $"{Connections}/{id}", admin)).Body.GetProperty("tokenExpiresAt").GetDateTimeOffset();
        Assert.InRange((expires - DateTimeOffset.UtcNow).TotalSeconds, 1700, 1800);
        AssertNone(Everything(t), FakeThirdParty.ClientSecret, token);
        Assert.Equal(["auth", "test"], (await Activity(admin, id)).Select(a => a.GetProperty("kind").GetString()!).Order().ToArray());
    }

    [Theory]
    [InlineData("http", "/login", false)]
    [InlineData("http", "/login-form", true)]
    public async Task A_login_call_signs_in_with_the_template_and_sends_the_token_in_the_header(string connector, string loginUrl, bool form)
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var third = await FakeThirdParty.StartAsync();
        var more = form
            ? new Dictionary<string, object?> { ["loginBodyType"] = "form", ["loginBody"] = "username={{username}}&password={{password}}" }
            : null;
        var (created, id) = await CreateLogin(admin, third, connector: connector, loginUrl: loginUrl, more: more);
        Assert.Equal(connector, created.GetProperty("connector").GetString());

        var result = await Test(admin, id);
        Assert.True(result.GetProperty("ok").GetBoolean(), Steps(result));
        var login = third.Hits.Single(h => h.Path == loginUrl);
        Assert.Equal("POST", login.Method);
        Assert.Equal(form ? "application/x-www-form-urlencoded; charset=utf-8" : "application/json; charset=utf-8", login.Headers["Content-Type"]);
        Assert.Contains(form ? Uri.EscapeDataString(FakeThirdParty.User) : FakeThirdParty.User, login.Body, StringComparison.Ordinal);
        var call = Assert.Single(third.Api);
        Assert.StartsWith("Bearer tok-", call.Headers["Authorization"], StringComparison.Ordinal);
        Assert.Equal(["auth", "test"], (await Activity(admin, id)).Select(a => a.GetProperty("kind").GetString()!).Order().ToArray());
        AssertNone(Everything(t), FakeThirdParty.Password, call.Headers["Authorization"][7..]);
    }

    [Fact]
    public async Task A_login_call_can_find_the_token_and_lifetime_anywhere_and_send_the_token_in_another_header()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var third = await FakeThirdParty.StartAsync();
        third.ExpiresIn = 900;
        var (_, id) = await CreateLogin(admin, third, loginUrl: "/nested-login", more: new()
            { ["tokenPointer"] = "/data/jwt", ["expiryPointer"] = "/ttl", ["tokenHeader"] = "X-Auth", ["tokenPrefix"] = "Token " });

        var result = await Test(admin, id);
        Assert.True(result.GetProperty("ok").GetBoolean(), Steps(result));
        Assert.StartsWith("Token tok-", Assert.Single(third.Api).Headers["X-Auth"], StringComparison.Ordinal);
        Assert.False(third.Api[0].Headers.ContainsKey("Authorization"));
        var expires = (await Call(HttpMethod.Get, $"{Connections}/{id}", admin)).Body.GetProperty("tokenExpiresAt").GetDateTimeOffset();
        Assert.InRange((expires - DateTimeOffset.UtcNow).TotalSeconds, 800, 900);
    }

    [Fact]
    public async Task A_fixed_lifetime_applies_when_the_response_gives_none_and_a_default_when_nothing_is_set()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var third = await FakeThirdParty.StartAsync();
        third.LoginBody = """{"access_token":"{TOKEN}"}""";
        var (_, fixedId) = await CreateLogin(admin, third, "Fixed", more: new() { ["expirySeconds"] = 7200 });
        var (_, defaultId) = await CreateLogin(admin, third, "Default");
        await Test(admin, fixedId);
        await Test(admin, defaultId);
        async Task<double> Left(Guid id) => ((await Call(HttpMethod.Get, $"{Connections}/{id}", admin)).Body.GetProperty("tokenExpiresAt").GetDateTimeOffset() - DateTimeOffset.UtcNow).TotalSeconds;
        Assert.InRange(await Left(fixedId), 7100, 7200);
        Assert.InRange(await Left(defaultId), 200, 300);
    }

    // ---- the token lifecycle -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_token_is_reused_and_a_second_call_does_not_sign_in_again()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var third = await FakeThirdParty.StartAsync();
        var (_, id) = await CreateLogin(admin, third);

        Assert.True((await Send(t, id)).Ok);
        Assert.True((await Send(t, id)).Ok);
        Assert.True((await Send(t, id)).Ok);
        Assert.Equal(1, third.Logins);
        Assert.Equal(3, third.Api.Count);
        Assert.Single(third.Api.Select(h => h.Headers["Authorization"]).Distinct());
        var kinds = (await Activity(admin, id)).Select(a => a.GetProperty("kind").GetString()!).ToArray();
        Assert.Equal((1, 3), (kinds.Count(k => k == "auth"), kinds.Count(k => k == "action")));
    }

    [Fact]
    public async Task A_token_that_is_expired_or_within_a_minute_of_its_end_causes_a_new_sign_in()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var third = await FakeThirdParty.StartAsync();
        var (_, id) = await CreateLogin(admin, third);
        Assert.True((await Send(t, id)).Ok);
        Assert.Equal(1, third.Logins);

        Sql($"UPDATE integration_connections SET token_expires_at = now() + interval '90 seconds' WHERE id = '{id}'");
        Assert.True((await Send(t, id)).Ok);
        Assert.Equal(1, third.Logins);                       // 90 s left: still reused

        Sql($"UPDATE integration_connections SET token_expires_at = now() + interval '50 seconds' WHERE id = '{id}'");
        Assert.True((await Send(t, id)).Ok);
        Assert.Equal(2, third.Logins);                       // inside the last minute: a new one

        Sql($"UPDATE integration_connections SET token_expires_at = now() - interval '1 hour' WHERE id = '{id}'");
        Assert.True((await Send(t, id)).Ok);
        Assert.Equal(3, third.Logins);                       // expired

        Assert.True((await Send(t, id)).Ok);
        Assert.Equal(3, third.Logins);                       // and the new one lasts
    }

    [Fact]
    public async Task A_401_causes_exactly_one_new_sign_in_and_one_retry_which_then_succeeds()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var third = await FakeThirdParty.StartAsync();
        var (_, id) = await CreateLogin(admin, third);
        Assert.True((await Send(t, id)).Ok);
        third.RevokeTokens();   // the server forgot every token it handed out

        var result = await Send(t, id);
        Assert.True(result.Ok, result.Error);
        Assert.Equal(2, third.Logins);
        Assert.Equal(new[] { 200, 401, 200 }, third.Api.Select(h => h.Status).ToArray());
        Assert.NotEqual(third.Api[0].Headers["Authorization"], third.Api[2].Headers["Authorization"]);
    }

    [Fact]
    public async Task A_401_that_persists_after_the_new_sign_in_fails_clearly_and_never_loops()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var third = await FakeThirdParty.StartAsync();
        third.RejectApiCalls = true;
        var (_, id) = await CreateLogin(admin, third);

        var result = await Send(t, id);
        Assert.False(result.Ok);
        Assert.Equal(401, result.StatusCode);
        Assert.Contains("still rejected the token (401) after signing in again", result.Error, StringComparison.Ordinal);
        Assert.Equal(2, third.Logins);          // the first, and the one retry's
        Assert.Equal(2, third.Api.Count);       // the call and its one retry
        await Task.Delay(300);
        Assert.Equal((2, 2), (third.Logins, third.Api.Count));

        var test = await Test(admin, id);       // through the API: one more sign-in (a test always signs in afresh), then the request and its one retry
        Assert.False(test.GetProperty("ok").GetBoolean());
        Assert.Contains("401", Steps(test), StringComparison.Ordinal);
        var connection = (await Call(HttpMethod.Get, $"{Connections}/{id}", admin)).Body;
        Assert.Equal("error", connection.GetProperty("status").GetString());
        Assert.Contains("401", connection.GetProperty("statusDetail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stored_credential_that_is_refused_fails_clearly_without_any_retry()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var third = await FakeThirdParty.StartAsync();
        third.RejectApiCalls = true;
        var id = (await Create(admin, ApiKeyHeaderConnection(third))).GetProperty("id").GetGuid();
        var result = await Send(t, id, "/health");
        Assert.False(result.Ok);
        Assert.Contains("rejected the credentials (401)", result.Error, StringComparison.Ordinal);
        Assert.Single(third.Api);
    }

    [Fact]
    public async Task Many_calls_at_once_sign_in_once_and_all_use_the_same_token()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var third = await FakeThirdParty.StartAsync();
        third.LoginDelayMs = 400;   // long enough for every caller to arrive while the first is still signing in
        var (_, id) = await CreateLogin(admin, third);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Send(t, id)));
        Assert.All(results, r => Assert.True(r.Ok, r.Error));
        Assert.Equal(1, third.Logins);
        Assert.Equal(8, third.Api.Count);
        Assert.Single(third.Api.Select(h => h.Headers["Authorization"]).Distinct());
        Assert.Null(Scalar<DateTime?>($"SELECT login_lease_until FROM integration_connections WHERE id = '{id}'"));   // nothing is left held
    }

    [Fact]
    public async Task A_sign_in_that_fails_leaves_no_lease_behind_so_the_next_caller_can_try_at_once()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var third = await FakeThirdParty.StartAsync();
        third.LoginStatus = 503;
        var (_, id) = await CreateLogin(admin, third);
        var first = await Send(t, id);
        Assert.False(first.Ok);
        Assert.Contains("Sign-in failed", first.Error, StringComparison.Ordinal);
        Assert.Null(Scalar<DateTime?>($"SELECT login_lease_until FROM integration_connections WHERE id = '{id}'"));

        third.LoginStatus = 200;
        Assert.True((await Send(t, id)).Ok);
        Assert.Equal(2, third.Logins);
    }

    [Fact]
    public async Task No_database_transaction_is_held_while_a_call_to_the_other_system_is_in_flight()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var third = await FakeThirdParty.StartAsync();
        third.LoginDelayMs = 4000;
        var (_, id) = await CreateLogin(admin, third);

        var running = Send(t, id);
        while (third.LoginsStarted == 0) await Task.Delay(20);   // the sign-in is now under way, and stays so for four seconds

        // An audited write for the tenant (it takes the audit-chain lock) goes straight through while the sign-in is still in flight...
        Assert.Equal(HttpStatusCode.Created, (await Call(HttpMethod.Post, "/api/v1/folders", admin, new { name = "Elsewhere" })).Status);
        Assert.False(running.IsCompleted, "the audited write had to wait for the sign-in");
        // ...and nothing of ours sits "idle in transaction" waiting for the other system.
        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM pg_stat_activity WHERE state = 'idle in transaction' AND usename = 'app_login'"));
        Assert.True((await running).Ok);
    }

    // ---- failures are explained, and never leak ------------------------------------------------------------------

    [Fact]
    public async Task A_refused_sign_in_fails_the_authenticate_step_and_skips_the_request()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var third = await FakeThirdParty.StartAsync();
        var created = await Create(admin, Input("http", "Wrong password", new()
            { ["baseUrl"] = third.BaseUrl, ["authStrategy"] = "login_call", ["loginUrl"] = "/login", ["username"] = FakeThirdParty.User, ["testPath"] = "/health" }, new() { ["password"] = "not-the-right-one" }));
        var id = created.GetProperty("id").GetGuid();

        var result = await Test(admin, id);
        Assert.False(result.GetProperty("ok").GetBoolean());
        var steps = result.GetProperty("steps").EnumerateArray().ToArray();
        var only = Assert.Single(steps);
        Assert.Equal("Authenticate", only.GetProperty("name").GetString());
        Assert.False(only.GetProperty("ok").GetBoolean());
        Assert.Contains("Sign-in failed: The server answered 401.", only.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Empty(third.Api);

        var connection = (await Call(HttpMethod.Get, $"{Connections}/{id}", admin)).Body;
        Assert.Equal("error", connection.GetProperty("status").GetString());
        Assert.Contains("Sign-in failed", connection.GetProperty("statusDetail").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, connection.GetProperty("lastOkAt").ValueKind);
        var activity = Assert.Single(await Activity(admin, id));
        Assert.Equal(("auth", false, 401), (activity.GetProperty("kind").GetString(), activity.GetProperty("ok").GetBoolean(), activity.GetProperty("statusCode").GetInt32()));
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'integration.connection.tested' AND after->>'Ok' = 'false'"));
    }

    [Theory]
    [InlineData("""{"other":"x"}""", "has no value at /access_token")]
    [InlineData("""{"access_token":42}""", "is not text")]
    [InlineData("""{"access_token":""}""", "is empty")]
    [InlineData("not json at all", "not valid JSON")]
    public async Task A_sign_in_response_without_a_usable_token_is_reported_as_such(string body, string expected)
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var third = await FakeThirdParty.StartAsync();
        third.LoginBody = body;
        var (_, id) = await CreateLogin(admin, third);
        var result = await Test(admin, id);
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Contains(expected, Steps(result), StringComparison.Ordinal);
        Assert.Empty(third.Api);
        Assert.Equal(JsonValueKind.Null, (await Call(HttpMethod.Get, $"{Connections}/{id}", admin)).Body.GetProperty("tokenExpiresAt").ValueKind);   // no token was kept
    }

    [Fact]
    public async Task A_server_that_echoes_credentials_in_its_error_pages_cannot_make_them_appear_in_a_result_the_status_or_the_activity_log()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var third = await FakeThirdParty.StartAsync();
        third.EchoInErrors = true;
        const string wrongPassword = "wrong-password-9876";

        // A sign-in that is refused (the server repeats the whole request in its error page)...
        var refused = (await Create(admin, Input("http", "Refused", new()
            { ["baseUrl"] = third.BaseUrl, ["authStrategy"] = "login_call", ["loginUrl"] = "/login", ["username"] = FakeThirdParty.User }, new() { ["password"] = wrongPassword }))).GetProperty("id").GetGuid();
        // ...an API key and a bearer token that are refused, in a header and in the query...
        var badKey = (await Create(admin, Input("http", "Bad key", new() { ["baseUrl"] = third.BaseUrl, ["authStrategy"] = "api_key", ["apiKeyName"] = "X-Wrong", ["testPath"] = "/health" }, new() { ["apiKey"] = "bad-api-key-123456" }))).GetProperty("id").GetGuid();
        var badQuery = (await Create(admin, Input("http", "Bad query", new() { ["baseUrl"] = third.BaseUrl, ["authStrategy"] = "api_key", ["apiKeyIn"] = "query", ["apiKeyName"] = "k", ["testPath"] = "/health" }, new() { ["apiKey"] = "bad-query-key-654321" }))).GetProperty("id").GetGuid();
        var badBasic = (await Create(admin, Input("http", "Bad basic", new() { ["baseUrl"] = third.BaseUrl, ["authStrategy"] = "basic", ["username"] = "mallory", ["testPath"] = "/health" }, new() { ["password"] = "mallory-password-42" }))).GetProperty("id").GetGuid();
        // ...and a token that works but whose server later fails with a 500 that repeats the Authorization header.
        var (_, working) = await CreateLogin(admin, third, "Working", more: new() { ["testPath"] = "/boom" });

        var texts = new List<string>();
        foreach (var id in new[] { refused, badKey, badQuery, badBasic, working })
        {
            texts.Add((await Test(admin, id)).ToString());
            texts.Add((await Call(HttpMethod.Get, $"{Connections}/{id}", admin)).Body.ToString());
            texts.Add(string.Join("\n", (await Activity(admin, id)).Select(a => a.ToString())));
        }
        var token = third.Api.Where(h => h.Path == "/boom").Select(h => h.Headers["Authorization"][7..]).First();

        // The server did get everything it was echoing back...
        Assert.Contains(wrongPassword, third.Hits.First(h => h.Path == "/login").Body, StringComparison.Ordinal);
        var all = string.Join("\n", texts) + Everything(t);
        AssertNone(all, wrongPassword, "bad-api-key-123456", "bad-query-key-654321", "mallory-password-42", FakeThirdParty.Password, token,
            Convert.ToBase64String(Encoding.UTF8.GetBytes("mallory:mallory-password-42")));
        AssertNone(all, "rejected /");   // none of the server's error text is kept at all
        Assert.Contains("500", all, StringComparison.Ordinal);   // what is kept is the status
    }

    [Fact]
    public async Task Other_statuses_redirects_and_unreachable_servers_are_explained_in_plain_words()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var third = await FakeThirdParty.StartAsync();
        string Detail(JsonElement r) => r.GetProperty("steps").EnumerateArray().Last().GetProperty("detail").GetString()!;
        async Task<Guid> Make(string name, string baseUrl, string testPath) => (await Create(admin, Input("http", name,
            new() { ["baseUrl"] = baseUrl, ["authStrategy"] = "none", ["testPath"] = testPath }))).GetProperty("id").GetGuid();

        Assert.Contains("answered 404", Detail(await Test(admin, await Make("Missing", third.BaseUrl, "/nowhere"))), StringComparison.Ordinal);
        Assert.Contains("answered 500", Detail(await Test(admin, await Make("Broken", third.BaseUrl, "/boom"))), StringComparison.Ordinal);
        Assert.Contains("Redirects are not followed", Detail(await Test(admin, await Make("Moved", third.BaseUrl, "/moved"))), StringComparison.Ordinal);
        Assert.DoesNotContain(third.Hits, h => h.Path == "/things");   // the redirect was not followed
        var closed = Sockets.FreePort();
        Assert.Contains("Could not connect", Detail(await Test(admin, await Make("Down", $"http://127.0.0.1:{closed}", "/health"))), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_connection_without_a_test_path_tests_only_the_sign_in()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var third = await FakeThirdParty.StartAsync();
        var (_, id) = await CreateLogin(admin, third, more: new() { ["testPath"] = null });
        var result = await Test(admin, id);
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal("Authenticate", Assert.Single(result.GetProperty("steps").EnumerateArray()).GetProperty("name").GetString());
        Assert.Empty(third.Api);
        Assert.Equal(1, third.Logins);
    }

    [Fact]
    public async Task A_test_always_signs_in_afresh_so_it_proves_the_credentials_as_they_are_now()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var third = await FakeThirdParty.StartAsync();
        var (_, id) = await CreateLogin(admin, third);
        await Test(admin, id);
        await Test(admin, id);
        Assert.Equal(2, third.Logins);
        Assert.Equal(2, third.Api.Count);
    }

    // ---- the test rate limit -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_connection_can_be_tested_at_most_once_every_five_seconds()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var third = await FakeThirdParty.StartAsync();
        var id = (await Create(admin, ApiKeyHeaderConnection(third))).GetProperty("id").GetGuid();

        var first = await Call(HttpMethod.Post, $"{Connections}/{id}/test", admin);
        Assert.Equal(HttpStatusCode.OK, first.Status);

        using var client = Api.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Connections}/{id}/test") { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", admin) } };
        using var second = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.InRange(int.Parse(second.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture), 1, 5);
        var body = JsonDocument.Parse(await second.Content.ReadAsStringAsync()).RootElement;
        Assert.Contains("Wait", body.GetProperty("title").GetString(), StringComparison.Ordinal);
        Assert.Single(third.Api);                       // the refused test reached nobody
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'integration.connection.tested'"));

        // Another connection is not held back by this one; and after the interval this one can be tested again.
        var other = (await Create(admin, ApiKeyHeaderConnection(third, "Other"))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"{Connections}/{other}/test", admin)).Status);
        Sql($"UPDATE integration_connections SET test_claimed_at = now() - interval '6 seconds' WHERE id = '{id}'");
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"{Connections}/{id}/test", admin)).Status);
        Assert.Equal(3, third.Api.Count);
    }

    [Fact]
    public async Task Two_tests_at_the_same_moment_let_exactly_one_through()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var third = await FakeThirdParty.StartAsync();
        var id = (await Create(admin, ApiKeyHeaderConnection(third))).GetProperty("id").GetGuid();
        var statuses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Call(HttpMethod.Post, $"{Connections}/{id}/test", admin)));
        Assert.Equal(1, statuses.Count(s => s.Status == HttpStatusCode.OK));
        Assert.Equal(3, statuses.Count(s => s.Status == HttpStatusCode.TooManyRequests));
        Assert.Single(third.Api);
    }

    // ---- the outbound address rules ------------------------------------------------------------------------------

    [Fact]
    public async Task A_private_address_is_refused_unless_the_deployment_allows_private_targets()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var third = await FakeThirdParty.StartAsync();
        await using var strict = new ApiTestHost(Pg).Factory(allowPrivateWebhooks: false);
        using var client = strict.CreateClient();
        async Task<(HttpStatusCode Status, JsonElement Body)> Strict(HttpMethod m, string path, object? body = null)
        {
            using var req = new HttpRequestMessage(m, path) { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", admin) } };
            if (body is not null) req.Content = JsonContent.Create(body);
            using var res = await client.SendAsync(req);
            var text = await res.Content.ReadAsStringAsync();
            return (res.StatusCode, text.Length > 0 ? JsonDocument.Parse(text).RootElement.Clone() : default);
        }

        // Saving a private address (the base URL, the token URL or an absolute sign-in address) is refused...
        foreach (var url in new[] { "http://127.0.0.1:9/api", "http://169.254.169.254/latest", "http://10.0.0.5/x", "http://[::1]/x", "http://localhost/x" })
        {
            var r = await Strict(HttpMethod.Post, Connections, Input("http", "Private", new() { ["baseUrl"] = url, ["authStrategy"] = "none" }));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status);
            Assert.True(r.Body.GetProperty("errors").TryGetProperty("config.baseUrl", out _), url);
        }
        var oauth = await Strict(HttpMethod.Post, Connections, Input("http", "Private token url", new()
            { ["baseUrl"] = "https://api.example.test", ["authStrategy"] = "oauth2_client_credentials", ["tokenUrl"] = "http://10.1.2.3/token", ["clientId"] = "c" }, new() { ["clientSecret"] = "secret-123456" }));
        Assert.True(oauth.Body.GetProperty("errors").TryGetProperty("config.tokenUrl", out _));
        var login = await Strict(HttpMethod.Post, Connections, Input("http", "Private login", new()
            { ["baseUrl"] = "https://api.example.test", ["authStrategy"] = "login_call", ["loginUrl"] = "http://192.168.1.10/login", ["username"] = "a" }, new() { ["password"] = "secret-123456" }));
        Assert.True(login.Body.GetProperty("errors").TryGetProperty("config.loginUrl", out _));
        Assert.Equal(0, Count(t, "SELECT count(*) FROM integration_connections"));

        // ...and one saved while private targets were allowed is still never called when the deployment is strict.
        var apiKey = (await Create(admin, ApiKeyHeaderConnection(third))).GetProperty("id").GetGuid();
        var (_, loginId) = await CreateLogin(admin, third);
        foreach (var id in new[] { apiKey, loginId })
        {
            var result = await Strict(HttpMethod.Post, $"{Connections}/{id}/test");
            Assert.Equal(HttpStatusCode.OK, result.Status);
            Assert.False(result.Body.GetProperty("ok").GetBoolean());
            Assert.Contains("private", Steps(result.Body), StringComparison.Ordinal);
            Sql($"UPDATE integration_connections SET test_claimed_at = NULL WHERE id = '{id}'");
        }
        Assert.Empty(third.Hits);
    }

    // ---- stored credentials are bound to their connection and key ------------------------------------------------

    [Fact]
    public async Task Credentials_that_cannot_be_decrypted_fail_the_test_with_an_explanation_and_stay_bound_to_their_connection()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var third = await FakeThirdParty.StartAsync();
        var one = (await Create(admin, ApiKeyHeaderConnection(third, "One"))).GetProperty("id").GetGuid();
        var two = (await Create(admin, Input("http", "Two", new() { ["baseUrl"] = third.BaseUrl, ["authStrategy"] = "api_key", ["apiKeyName"] = "X-API-Key", ["testPath"] = "/health" }, new() { ["apiKey"] = "another-api-key-777" }))).GetProperty("id").GetGuid();

        // Copying one connection's ciphertext onto the other does not work: the ciphertext is bound to its connection.
        Sql($"UPDATE integration_connections SET secrets = (SELECT secrets FROM integration_connections WHERE id = '{one}') WHERE id = '{two}'");
        var swapped = await Test(admin, two);
        Assert.False(swapped.GetProperty("ok").GetBoolean());
        Assert.Contains("cannot be read", Steps(swapped), StringComparison.Ordinal);
        Assert.Empty(third.Api);

        // A different server key cannot read what another key wrote.
        await using var other = new ApiTestHost(Pg).Factory(extraEnvironment: new Dictionary<string, string?> { ["DAM_SECRETS_KEY"] = "a-different-secrets-key-0123456789" });
        using var client = other.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Connections}/{one}/test") { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", admin) } };
        using var res = await client.SendAsync(req);
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Contains("DAM_SECRETS_KEY", Steps(body), StringComparison.Ordinal);
        Assert.Empty(third.Api);
    }

    // ---- activity ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Activity_is_listed_newest_first_with_keyset_paging()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var third = await FakeThirdParty.StartAsync();
        var id = (await Create(admin, ApiKeyHeaderConnection(third))).GetProperty("id").GetGuid();
        for (var i = 0; i < 7; i++) Assert.True((await Send(t, id, "/things")).Ok);

        var all = await Activity(admin, id);
        Assert.Equal(7, all.Length);
        var ids = all.Select(a => a.GetProperty("id").GetGuid()).ToArray();
        Assert.Equal(ids.OrderByDescending(x => x).ToArray(), ids);   // newest first

        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var r = await Call(HttpMethod.Get, $"{Connections}/{id}/activity?limit=3" + (cursor is null ? "" : "&cursor=" + cursor), admin);
            Assert.Equal(HttpStatusCode.OK, r.Status);
            var items = r.Body.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("id").GetGuid()).ToArray();
            Assert.InRange(items.Length, 1, 3);
            seen.AddRange(items);
            cursor = r.Body.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : r.Body.GetProperty("nextCursor").GetString();
            pages++;
        } while (cursor is not null && pages < 10);
        Assert.Equal(ids, seen);
        Assert.Equal(3, pages);

        var clamped = await Call(HttpMethod.Get, $"{Connections}/{id}/activity?limit=100000", admin);
        Assert.Equal(7, clamped.Body.GetProperty("items").GetArrayLength());
        // One connection's activity never shows another's.
        var other = (await Create(admin, ApiKeyHeaderConnection(third, "Other"))).GetProperty("id").GetGuid();
        Assert.Empty(await Activity(admin, other));
    }

    [Fact]
    public async Task The_daily_cleanup_keeps_the_latest_five_hundred_calls_of_each_connection()
    {
        var a = await NewTenantAsync(); var b = await NewTenantAsync();
        var adminA = Admin(a); var adminB = Admin(b);
        var input = Input("http", "Busy", new() { ["baseUrl"] = "https://api.example.test", ["authStrategy"] = "none" });
        var busy = (await Create(adminA, input)).GetProperty("id").GetGuid();
        var quiet = (await Create(adminA, Input("http", "Quiet", new() { ["baseUrl"] = "https://api.example.test", ["authStrategy"] = "none" }))).GetProperty("id").GetGuid();
        var theirs = (await Create(adminB, input)).GetProperty("id").GetGuid();

        void Fill(Guid tenant, Guid connection, int n) => Sql($"""
            INSERT INTO integration_calls (id, tenant_id, connection_id, kind, method, target, status_code, ok, duration_ms, created_at)
            SELECT gen_random_uuid(), '{tenant}', '{connection}', 'action', 'GET', 'api.example.test/n' || g, 200, true, 5, now() - make_interval(secs => {n} - g)
            FROM generate_series(1, {n}) g
            """);
        Fill(a, busy, 620);
        Fill(a, quiet, 40);
        Fill(b, theirs, 501);
        // Give the busy connection's rows ids in time order, so "latest" is unambiguous: v7-style ids sort by time, these random ones do not, so renumber them by age.
        Sql($"""
            WITH ranked AS (SELECT id, row_number() OVER (ORDER BY created_at) rn FROM integration_calls WHERE connection_id IN ('{busy}', '{quiet}', '{theirs}'))
            UPDATE integration_calls c SET id = ('00000000-0000-7000-8000-' || lpad(to_hex(r.rn), 12, '0'))::uuid FROM ranked r WHERE c.id = r.id
            """);
        var newestBefore = Scalar<string>($"SELECT target FROM integration_calls WHERE connection_id = '{busy}' ORDER BY created_at DESC LIMIT 1");

        var clock = new FakeClock(DateTimeOffset.UtcNow);
        using var scheduler = NotifyHosts.Scheduler(Pg, clock, configure: s => Dam.Infrastructure.Scheduling.IntegrationSchedulingRegistration.AddDamIntegrationJobs(s));
        await NotifyHosts.RunJobAsync(scheduler, Dam.Infrastructure.Scheduling.IntegrationActivityCleanupJob.JobKey);

        Assert.Equal(500, Count(a, $"SELECT count(*) FROM integration_calls WHERE connection_id = '{busy}'"));
        Assert.Equal(40, Count(a, $"SELECT count(*) FROM integration_calls WHERE connection_id = '{quiet}'"));
        Assert.Equal(500, Count(b, $"SELECT count(*) FROM integration_calls WHERE connection_id = '{theirs}'"));
        // What survives is the newest 500, and the oldest were the ones deleted.
        Assert.Equal(newestBefore, Scalar<string>($"SELECT target FROM integration_calls WHERE connection_id = '{busy}' ORDER BY created_at DESC LIMIT 1"));
        Assert.Equal(0, Count(a, $"SELECT count(*) FROM integration_calls WHERE connection_id = '{busy}' AND target IN ('api.example.test/n1', 'api.example.test/n120')"));
        Assert.Equal(1, Count(a, $"SELECT count(*) FROM integration_calls WHERE connection_id = '{busy}' AND target = 'api.example.test/n121'"));

        await NotifyHosts.RunJobAsync(scheduler, Dam.Infrastructure.Scheduling.IntegrationActivityCleanupJob.JobKey);   // idempotent
        Assert.Equal(500, Count(a, $"SELECT count(*) FROM integration_calls WHERE connection_id = '{busy}'"));
    }

    [Fact]
    public async Task The_cleanup_is_registered_with_the_scheduler_like_the_other_jobs()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        using var scheduler = NotifyHosts.Scheduler(Pg, clock, configure: s => Dam.Infrastructure.Scheduling.IntegrationSchedulingRegistration.AddDamIntegrationJobs(s));
        await using var scope = scheduler.Services.CreateAsyncScope();
        Assert.Contains(scope.ServiceProvider.GetServices<Dam.Application.Abstractions.IScheduledJob>(), j => j.Key == "integrations.cleanup");
    }

    // ---- PADS4: a connector that only stores its settings --------------------------------------------------------

    private const string Pads4Password = "pads4-pa55word-0042";

    private static Dictionary<string, object?> Pads4Connection(string baseUrl = "http://192.168.1.20:8080", string name = "PADS4", string password = Pads4Password) =>
        Input("pads4", name, new() { ["baseUrl"] = baseUrl, ["domain"] = "acme", ["username"] = "ann" }, new() { ["password"] = password });

    private static object Pads4Put(string baseUrl = "http://192.168.1.20:8080", string name = "PADS4", string domain = "acme", Dictionary<string, string>? secrets = null, string[]? clear = null)
    {
        var config = new Dictionary<string, object?> { ["baseUrl"] = baseUrl, ["domain"] = domain, ["username"] = "ann" };
        return new { name, enabled = true, config, secrets = secrets ?? [], clearSecrets = clear ?? [] };
    }

    [Fact]
    public async Task A_pads4_connection_is_saved_with_the_password_as_a_write_only_secret_and_nothing_is_called()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var third = await FakeThirdParty.StartAsync();
        var created = await Call(HttpMethod.Post, Connections, admin, Pads4Connection(third.BaseUrl));
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var id = created.Body.GetProperty("id").GetGuid();
        Assert.Equal(("pads4", "PADS4", true, "new", "Saved. Orbit does not connect to PADS4 yet."),
            (created.Body.GetProperty("connector").GetString(), created.Body.GetProperty("name").GetString(), created.Body.GetProperty("enabled").GetBoolean(),
             created.Body.GetProperty("status").GetString(), created.Body.GetProperty("statusDetail").GetString()));
        var config = created.Body.GetProperty("config");
        Assert.Equal((third.BaseUrl, "acme", "ann"), (config.GetProperty("baseUrl").GetString(), config.GetProperty("domain").GetString(), config.GetProperty("username").GetString()));
        Assert.False(config.TryGetProperty("password", out _));
        Assert.False(config.TryGetProperty("authStrategy", out _));
        var secret = created.Body.GetProperty("secrets").GetProperty("password");
        Assert.True(secret.GetProperty("set").GetBoolean());
        Assert.Equal(Pads4Password[^4..], secret.GetProperty("hint").GetString());
        Assert.Single(created.Body.GetProperty("secrets").EnumerateObject());

        // Never returned, whatever way it is read.
        foreach (var response in new[] { created.Body, (await Call(HttpMethod.Get, $"{Connections}/{id}", admin)).Body, (await Call(HttpMethod.Get, Connections, admin)).Body[0] })
            AssertNone(response.ToString(), Pads4Password);
        // At rest: ciphertext only; and in the audit trail, outbox and activity.
        Assert.Contains("v1.", Scalar<string>($"SELECT secrets::text FROM integration_connections WHERE id = '{id}'"), StringComparison.Ordinal);
        AssertNone(Scalar<string>($"SELECT secrets::text || config::text FROM integration_connections WHERE id = '{id}'"), Pads4Password);
        AssertNone(Everything(t), Pads4Password);
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'integration.connection.created'"));
        Assert.Equal(0, Count(t, "SELECT count(*) FROM integration_calls"));
        Assert.Empty(third.Hits);   // not even a look at the address
        Assert.Empty(await Activity(admin, id));
    }

    [Fact]
    public async Task A_pads4_connection_keeps_its_password_unless_it_is_replaced_and_keeps_its_saved_detail()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var third = await FakeThirdParty.StartAsync();
        var id = (await Create(admin, Pads4Connection(third.BaseUrl))).GetProperty("id").GetGuid();
        var path = $"{Connections}/{id}";
        const string saved = "Saved. Orbit does not connect to PADS4 yet.";
        string Hint(JsonElement c) => c.GetProperty("secrets").GetProperty("password").GetProperty("hint").GetString()!;

        // Left out and sent empty: kept.
        var kept = await Call(HttpMethod.Put, path, admin, Pads4Put(third.BaseUrl, domain: "acme-eu"));
        Assert.Equal(HttpStatusCode.OK, kept.Status);
        Assert.Equal(("0042", "acme-eu", "new", saved), (Hint(kept.Body), kept.Body.GetProperty("config").GetProperty("domain").GetString(), kept.Body.GetProperty("status").GetString(), kept.Body.GetProperty("statusDetail").GetString()));
        Assert.Equal("0042", Hint((await Call(HttpMethod.Put, path, admin, Pads4Put(third.BaseUrl, secrets: new() { ["password"] = "" }))).Body));
        // Replaced.
        var replaced = await Call(HttpMethod.Put, path, admin, Pads4Put(third.BaseUrl, name: "PADS4 prod", secrets: new() { ["password"] = "second-pads4-pa55-7777" }));
        Assert.Equal(("7777", "PADS4 prod", saved), (Hint(replaced.Body), replaced.Body.GetProperty("name").GetString(), replaced.Body.GetProperty("statusDetail").GetString()));
        // It is required, so it cannot be removed.
        var cleared = await Call(HttpMethod.Put, path, admin, Pads4Put(third.BaseUrl, name: "PADS4 prod", clear: ["password"]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, cleared.Status);
        Assert.True(cleared.Body.GetProperty("errors").TryGetProperty("secrets.password", out _));
        // Missing values are named.
        var missing = await Call(HttpMethod.Put, path, admin, new { name = "PADS4 prod", enabled = true, config = new Dictionary<string, object?> { ["baseUrl"] = third.BaseUrl } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.Status);
        Assert.Equal(["config.domain", "config.username"], missing.Body.GetProperty("errors").EnumerateObject().Select(e => e.Name).Order(StringComparer.Ordinal).ToArray());

        var changed = Scalar<string>("SELECT string_agg(after::text, ' ') FROM audit_log WHERE action = 'integration.connection.updated' AND entity_id = '" + id + "'");
        Assert.Contains("secrets.password", changed, StringComparison.Ordinal);
        Assert.Contains("config.domain", changed, StringComparison.Ordinal);
        AssertNone(Everything(t), Pads4Password, "second-pads4-pa55-7777");
        Assert.Equal(0, Count(t, "SELECT count(*) FROM integration_calls"));
        Assert.Empty(third.Hits);
    }

    [Fact]
    public async Task The_four_pads4_values_are_required_and_named_as_config_or_secrets()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var errors = await Errors(admin, Input("pads4", "PADS4", []));
        Assert.Equal(["config.baseUrl", "config.domain", "config.username", "secrets.password"], errors.EnumerateObject().Select(e => e.Name).Order(StringComparer.Ordinal).ToArray());

        var bad = await Errors(admin, Input("pads4", "PADS4", new() { ["baseUrl"] = "ftp://pads4.example.test", ["domain"] = "acme", ["username"] = "ann", ["authStrategy"] = "login_call" },
            new() { ["password"] = "pw-12345678" }));
        Assert.True(bad.TryGetProperty("config.baseUrl", out _));
        Assert.True(bad.TryGetProperty("config.authStrategy", out _));   // PADS4 has no authentication setting
        Assert.Equal(0, Count(t, "SELECT count(*) FROM integration_connections"));
    }

    [Theory]
    [InlineData("http://192.168.1.20:8080")]
    [InlineData("http://pads4.internal.example.test")]
    [InlineData("https://10.1.2.3/pads4")]
    public async Task A_pads4_server_on_a_private_network_is_accepted_at_save_without_the_outbound_url_check(string baseUrl)
    {
        // A guard that refuses every address (as the real one does for a private or unresolvable host when private targets are off) and counts the questions put to it.
        var guard = new RefusingGuard();
        using var strict = Api.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<Dam.Application.Abstractions.IOutboundUrlGuard>(guard)));
        using var client = strict.CreateClient();
        var t = await NewTenantAsync(); var admin = Admin(t);
        async Task<(HttpStatusCode Status, JsonElement Body)> Post(object body)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Connections) { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", admin) }, Content = JsonContent.Create(body) };
            using var res = await client.SendAsync(req);
            var text = await res.Content.ReadAsStringAsync();
            return (res.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
        }

        var created = await Post(Pads4Connection(baseUrl));
        Assert.True(created.Status == HttpStatusCode.Created, $"{(int)created.Status}: {created.Body}");
        Assert.Equal(baseUrl, created.Body.GetProperty("config").GetProperty("baseUrl").GetString());
        Assert.Equal(0, guard.Asked);

        // The same address on a connector that makes calls is still guarded.
        var http = await Post(Input("http", "Guarded", new() { ["baseUrl"] = baseUrl, ["authStrategy"] = "none" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, http.Status);
        Assert.True(http.Body.GetProperty("errors").TryGetProperty("config.baseUrl", out _));
        Assert.Equal(1, guard.Asked);

        // Updating a PADS4 address is not guarded either.
        var id = created.Body.GetProperty("id").GetGuid();
        using var put = new HttpRequestMessage(HttpMethod.Put, $"{Connections}/{id}") { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", admin) }, Content = JsonContent.Create(Pads4Put("http://172.16.0.9:9090")) };
        using var updated = await client.SendAsync(put);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(1, guard.Asked);
    }

    private sealed class RefusingGuard : Dam.Application.Abstractions.IOutboundUrlGuard
    {
        private int _asked;
        public int Asked => Volatile.Read(ref _asked);
        public Task<string?> CheckAsync(string url, CancellationToken ct)
        {
            Interlocked.Increment(ref _asked);
            return Task.FromResult<string?>("This URL cannot be used: it points to a private address.");
        }
    }

    [Fact]
    public async Task Testing_a_pads4_connection_is_refused_and_records_nothing()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var third = await FakeThirdParty.StartAsync();
        var id = (await Create(admin, Pads4Connection(third.BaseUrl))).GetProperty("id").GetGuid();

        for (var i = 0; i < 2; i++)   // the second is the same answer, not a rate-limit one: nothing was claimed
        {
            var r = await Call(HttpMethod.Post, $"{Connections}/{id}/test", admin);
            Assert.Equal(HttpStatusCode.Conflict, r.Status);
            Assert.Equal("Testing is not available for this connector yet.", r.Body.GetProperty("title").GetString());
        }
        Assert.Equal(0, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'integration.connection.tested'"));
        Assert.Equal(0, Count(t, "SELECT count(*) FROM integration_calls"));
        Assert.Equal(0, Count(t, $"SELECT count(*) FROM integration_connections WHERE id = '{id}' AND test_claimed_at IS NOT NULL"));
        var after = (await Call(HttpMethod.Get, $"{Connections}/{id}", admin)).Body;
        Assert.Equal(("new", "Saved. Orbit does not connect to PADS4 yet."), (after.GetProperty("status").GetString(), after.GetProperty("statusDetail").GetString()));
        Assert.Equal(JsonValueKind.Null, after.GetProperty("lastTestedAt").ValueKind);
        Assert.Empty(third.Hits);
    }

    [Fact]
    public async Task Pads4_names_stay_unique_the_limit_stays_fifty_and_a_connection_can_be_deleted()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var first = await Create(admin, Pads4Connection());
        var duplicate = await Call(HttpMethod.Post, Connections, admin, Pads4Connection(name: "pads4"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.Status);
        var second = await Create(admin, Pads4Connection(name: "PADS4 branch"));
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Put, $"{Connections}/{second.GetProperty("id").GetGuid()}", admin, Pads4Put(name: "Pads4"))).Status);

        Sql($$"""
            INSERT INTO integration_connections (id, tenant_id, connector, name, enabled, config, secrets, status, created_at, updated_at)
            SELECT gen_random_uuid(), '{{t}}', 'pads4', 'Filler ' || g, true, '{}', '{}', 'new', now(), now() FROM generate_series(1, 48) g
            """);
        var over = await Call(HttpMethod.Post, Connections, admin, Pads4Connection(name: "One too many"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, over.Status);
        Assert.Contains("50", over.Body.GetProperty("errors").GetProperty("name")[0].GetString(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"{Connections}/{first.GetProperty("id").GetGuid()}", admin)).Status);
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'integration.connection.deleted'"));
        AssertNone(Everything(t), Pads4Password);
    }

    private static class Sockets
    {
        public static int FreePort() => SyslogSink.FreePort();
    }
}
