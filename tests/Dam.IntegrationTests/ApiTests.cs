using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Dam.IntegrationTests;

[Collection("pg")]
public sealed class ApiTests(PostgresFixture fx)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private readonly ApiTestHost _api = new(fx);
    private WebApplicationFactory<Program> Factory(string? dbConnection = null, int? rateLimit = null) => _api.Factory(dbConnection, rateLimit);
    private static string Token(Guid? tenant = null, string audience = "dam-api", DateTime? expires = null, params string[] roles) => ApiTestHost.Token(tenant, audience, expires, roles);
    private static HttpRequestMessage Me(string? token, string? correlation = null) => ApiTestHost.Me(token, correlation);
    private static readonly Guid Tenant = ApiTestHost.DevTenant;

    [Fact]
    public async Task Health_endpoints_are_anonymous_and_readyz_checks_the_database()
    {
        await using var f = Factory();
        using var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/healthz")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/readyz")).StatusCode);

        await using var broken = Factory("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=2");
        using var c2 = broken.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await c2.GetAsync("/healthz")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await c2.GetAsync("/readyz")).StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_request_gets_401_problem_json_with_correlation_id()
    {
        await using var f = Factory();
        using var c = f.CreateClient();
        var res = await c.SendAsync(Me(null, "trace-abc_123"));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal("trace-abc_123", res.Headers.GetValues("X-Correlation-ID").Single());
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(401, body.GetProperty("status").GetInt32());
        Assert.Equal("trace-abc_123", body.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task Me_returns_user_tenant_and_roles_from_token()
    {
        await using var f = Factory();
        using var c = f.CreateClient();
        var res = await c.SendAsync(Me(Token(Tenant, roles: ["Editor", "Approver"])));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(Tenant, body.GetProperty("tenantId").GetGuid());
        Assert.Equal("u@dam.local", body.GetProperty("email").GetString());
        Assert.Equal(["Editor", "Approver"], body.GetProperty("roles").EnumerateArray().Select(x => x.GetString()!).ToArray());
    }

    [Fact]
    public async Task Token_without_tenant_claim_is_403_problem_json()
    {
        await using var f = Factory();
        using var c = f.CreateClient();
        var res = await c.SendAsync(Me(Token(tenant: null)));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Expired_or_wrong_audience_tokens_are_rejected()
    {
        await using var f = Factory();
        using var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await c.SendAsync(Me(Token(Tenant, expires: DateTime.UtcNow.AddMinutes(-5))))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await c.SendAsync(Me(Token(Tenant, audience: "someone-else")))).StatusCode);
    }

    [Fact]
    public async Task Rate_limit_returns_429_after_the_configured_budget()
    {
        await using var f = Factory(rateLimit: 3);
        using var c = f.CreateClient();
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++) codes.Add((await c.SendAsync(Me(Token(Tenant)))).StatusCode);
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], codes);
    }

    [Fact]
    public async Task Committed_openapi_document_matches_the_running_api()
    {
        await using var f = Factory();
        using var c = f.CreateClient();
        var live = JsonSerializer.Serialize(
            JsonSerializer.Deserialize<JsonElement>(await c.GetStringAsync("/openapi/v1.json")),
            Indented) + "\n";

        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "Dam.sln"))) dir = Path.GetDirectoryName(dir)!;
        var path = Path.Combine(dir, "docs", "api", "openapi.json");

        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI") == "1") File.WriteAllText(path, live);
        Assert.True(File.Exists(path), "Run: UPDATE_OPENAPI=1 dotnet test tests/Dam.IntegrationTests --filter openapi");
        Assert.Equal(File.ReadAllText(path), live);
    }
}
