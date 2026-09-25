using System.Security.Claims;
using Dam.Application.Abstractions;
using Dam.Domain.Authorization;

namespace Dam.Api.Auth;

public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public string? Subject => Principal?.FindFirstValue("sub");

    public Guid? UserId => Guid.TryParse(Subject, out var id) ? id : null;

    public IReadOnlyCollection<string> Roles =>
        Principal?.FindAll("roles").Select(c => c.Value).ToArray() ?? [];

    public string? Email => Principal?.FindFirstValue("email");
    public string? DisplayName => Principal?.FindFirstValue("name") ?? Principal?.FindFirstValue("preferred_username");
    public string? ClientId => Principal?.FindFirstValue("azp");

    /// <summary>Group names from the token's `groups` claim (Keycloak group-membership mapper).</summary>
    public IReadOnlyList<string> Groups => Principal?.FindAll("groups").Select(c => c.Value).ToArray() ?? [];

    /// <summary>ABAC attributes (region, dealer, brand, channel) from same-named token claims; each may hold several values.</summary>
    public Dictionary<string, string[]> Attributes =>
        Dimensions.All.ToDictionary(d => d, d => Principal?.FindAll(d).Select(c => c.Value).ToArray() ?? []);
}

/// <summary>Lets startup code act inside a specific tenant when there is no HTTP request.</summary>
public sealed class TenantOverride { public Guid? Value { get; set; } }

/// <summary>Tenant comes only from the validated token's `tenant` claim (spec A3.3).</summary>
public sealed class ClaimsTenantContext(IHttpContextAccessor accessor, TenantOverride tenantOverride) : ITenantContext
{
    public Guid TenantId =>
        tenantOverride.Value
        ?? (Guid.TryParse(accessor.HttpContext?.User.FindFirstValue("tenant"), out var id) ? id : Guid.Empty);
}
