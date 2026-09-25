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
    private const string Issuer = "https://test-idp";
    private static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes("test-signing-key-test-signing-key-32b!"));
    private static readonly Guid Tenant = Guid.Parse("0197a000-0000-7000-8000-000000000001");

    private static readonly object EnvLock = new();
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Program reads config at startup, so set env vars and force the host to build inside the lock.</summary>
    private WebApplicationFactory<Program> Factory(string? dbConnection = null, int? rateLimit = null)
    {
        lock (EnvLock)
        {
            Environment.SetEnvironmentVariable("DAM_OIDC_AUTHORITY", Issuer);
            Environment.SetEnvironmentVariable("DAM_OIDC_AUDIENCE", "dam-api");
            Environment.SetEnvironmentVariable("DAM_DB_CONNECTION", dbConnection ?? fx.AppConnection);
            Environment.SetEnvironmentVariable("DAM_RATE_LIMIT_PER_MINUTE", rateLimit?.ToString());
            var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(s =>
                s.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
                {
                    o.Authority = null;
                    o.MetadataAddress = null!;
                    o.Configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                    o.TokenValidationParameters.IssuerSigningKey = Key;
                    o.TokenValidationParameters.ValidIssuer = Issuer;
                })));
            f.CreateClient().Dispose(); // build now, while env vars are ours
            return f;
        }
    }

    private static string Token(Guid? tenant = null, string audience = "dam-api", DateTime? expires = null, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new("sub", Guid.NewGuid().ToString()), new("email", "u@dam.local"), new("preferred_username", "u@dam.local"),
        };
        if (tenant is not null) claims.Add(new Claim("tenant", tenant.Value.ToString()));
        claims.AddRange(roles.Select(r => new Claim("roles", r)));
        var now = DateTime.UtcNow;
        var jwt = new JwtSecurityToken(Issuer, audience, claims, now.AddMinutes(-10), expires ?? now.AddMinutes(10),
            new SigningCredentials(Key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler { SetDefaultTimesOnTokenCreation = false }.WriteToken(jwt);
    }

    private static HttpRequestMessage Me(string? token, string? correlation = null)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        if (token is not null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (correlation is not null) r.Headers.Add("X-Correlation-ID", correlation);
        return r;
    }

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
