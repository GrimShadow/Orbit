using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Dam.IntegrationTests;

/// <summary>In-process Dam.Api with a test signing key instead of Keycloak, backed by the fixture database.</summary>
public sealed class ApiTestHost(PostgresFixture fx)
{
    public const string Issuer = "https://test-idp";
    public static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes("test-signing-key-test-signing-key-32b!"));
    public static readonly Guid DevTenant = Guid.Parse("0197a000-0000-7000-8000-000000000001");

    private static readonly object EnvLock = new();

    /// <summary>Program reads config at startup, so set env vars and force the host to build inside the lock.</summary>
    public WebApplicationFactory<Program> Factory(string? dbConnection = null, int? rateLimit = null)
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

    public static string Token(Guid? tenant = null, string audience = "dam-api", DateTime? expires = null, params string[] roles)
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

    public static HttpRequestMessage Me(string? token, string? correlation = null)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        if (token is not null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (correlation is not null) r.Headers.Add("X-Correlation-ID", correlation);
        return r;
    }

}
