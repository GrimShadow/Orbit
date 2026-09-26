using System.Threading.RateLimiting;
using Dam.Api.Auth;
using Dam.Api.Endpoints;
using Dam.Api.Middleware;
using Dam.Application;
using Dam.Application.Identity;
using Dam.Application.Messaging;
using Dam.Application.Abstractions;
using Dam.Infrastructure;
using Dam.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

string Required(string key) => cfg[key] is { Length: > 0 } v ? v : throw new InvalidOperationException($"{key} is not set.");

// ---- Kestrel: no file bytes pass through the API (spec A3.3), so keep bodies small.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 10 * 1024 * 1024);

// ---- Auth (Keycloak OIDC)
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.Authority = Required("DAM_OIDC_AUTHORITY");
    o.Audience = Required("DAM_OIDC_AUDIENCE");
    o.RequireHttpsMetadata = !string.Equals(cfg["DAM_OIDC_REQUIRE_HTTPS"], "false", StringComparison.OrdinalIgnoreCase);
    o.MapInboundClaims = false;
    o.TokenValidationParameters.NameClaimType = "preferred_username";
    o.TokenValidationParameters.RoleClaimType = "roles";
    o.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);
});
builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<HttpCurrentUser>();
builder.Services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<HttpCurrentUser>());
builder.Services.AddScoped<TenantOverride>();
builder.Services.AddScoped<ITenantContext, ClaimsTenantContext>();
builder.Services.AddMemoryCache();

// ---- Application + persistence
builder.Services.AddDamApplication();
builder.Services.AddDamInfrastructure(Required("DAM_DB_CONNECTION"));

// ---- Errors: RFC 9457 problem+json everywhere, including bare 401/403/404.
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
{
    ctx.ProblemDetails.Extensions["correlationId"] = ctx.HttpContext.Items[CorrelationIdMiddleware.Header];
    ctx.ProblemDetails.Extensions["traceId"] = System.Diagnostics.Activity.Current?.TraceId.ToString();
});

// ---- Rate limiting: per client (token azp/sub) else per IP.
var permit = int.TryParse(cfg["DAM_RATE_LIMIT_PER_MINUTE"], out var n) ? n : 600;
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        var key = ctx.User.FindFirst("azp")?.Value ?? ctx.User.FindFirst("sub")?.Value
                  ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            { PermitLimit = permit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
    });
});

// ---- Health
builder.Services.AddHealthChecks().AddCheck<DbReadyCheck>("postgres", tags: ["ready"]);

// ---- OpenAPI
builder.Services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
{
    doc.Info.Title = "Orbit API";
    doc.Info.Description = "Digital asset management: assets, metadata, taxonomy, access control and delivery.";
    return Task.CompletedTask;
}));

// ---- OpenTelemetry (traces + metrics + logs). OTLP export only when an endpoint is configured.
var otlp = cfg["OTEL_EXPORTER_OTLP_ENDPOINT"];
var resource = ResourceBuilder.CreateDefault().AddService("dam-api");
builder.Logging.AddOpenTelemetry(o =>
{
    o.SetResourceBuilder(resource); o.IncludeScopes = true; o.IncludeFormattedMessage = true;
    if (otlp is not null) o.AddOtlpExporter();
});
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("dam-api"))
    .WithTracing(t =>
    {
        t.AddAspNetCoreInstrumentation(x => x.Filter = c => !c.Request.Path.StartsWithSegments("/healthz")
                                                            && !c.Request.Path.StartsWithSegments("/readyz")
                                                            && !c.Request.Path.StartsWithSegments("/metrics"))
         .AddHttpClientInstrumentation();
        if (otlp is not null) t.AddOtlpExporter();
    })
    .WithMetrics(m =>
    {
        m.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddRuntimeInstrumentation().AddPrometheusExporter();
        if (otlp is not null) m.AddOtlpExporter();
    });

var app = builder.Build();

await BootstrapTenantAsync(app);

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<UserProvisioningMiddleware>();

app.MapHealthChecks("/healthz", new() { Predicate = _ => false }).AllowAnonymous();       // liveness
app.MapHealthChecks("/readyz", new() { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();
app.MapPrometheusScrapingEndpoint("/metrics").AllowAnonymous();
app.MapOpenApi().AllowAnonymous();
app.MapDamEndpoints();

app.Run();

/// <summary>
/// Creates the configured tenant and its built-in roles if missing (single-tenant / on-prem installs, dev).
/// Set DAM_BOOTSTRAP_TENANT_ID, DAM_BOOTSTRAP_TENANT_NAME and DAM_BOOTSTRAP_TENANT_SLUG. Failures are logged, not fatal.
/// </summary>
static async Task BootstrapTenantAsync(WebApplication app)
{
    var cfg = app.Configuration;
    if (!Guid.TryParse(cfg["DAM_BOOTSTRAP_TENANT_ID"], out var id)) return;
    try
    {
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantOverride>().Value = id;
        var result = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new EnsureTenantCommand(
            id, cfg["DAM_BOOTSTRAP_TENANT_NAME"] ?? "Default", cfg["DAM_BOOTSTRAP_TENANT_SLUG"] ?? "default"));
        if (result.IsFailure) app.Logger.LogError("Tenant bootstrap failed: {Error}", result.Error.Message);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Tenant bootstrap failed (is the database migrated?)");
    }
}

public sealed class DbReadyCheck(DamDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        await db.Database.CanConnectAsync(ct) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("database unreachable");
}

public partial class Program;
