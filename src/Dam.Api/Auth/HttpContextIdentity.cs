using System.Security.Claims;
using Dam.Application.Abstractions;

namespace Dam.Api.Auth;

public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid? UserId => Guid.TryParse(Principal?.FindFirstValue("sub"), out var id) ? id : null;

    public IReadOnlyCollection<string> Roles =>
        Principal?.FindAll("roles").Select(c => c.Value).ToArray() ?? [];

    public string? Email => Principal?.FindFirstValue("email");
    public string? DisplayName => Principal?.FindFirstValue("name") ?? Principal?.FindFirstValue("preferred_username");
    public string? ClientId => Principal?.FindFirstValue("azp");
}

/// <summary>Tenant comes only from the validated token's `tenant` claim (spec A3.3).</summary>
public sealed class ClaimsTenantContext(IHttpContextAccessor accessor) : ITenantContext
{
    public Guid TenantId =>
        Guid.TryParse(accessor.HttpContext?.User.FindFirstValue("tenant"), out var id) ? id : Guid.Empty;
}

/// <summary>Placeholder until the RBAC + ABAC engine in Step 1.1: Admin may do everything.</summary>
public sealed class AdminOnlyAuthorizationService : IAuthorizationService
{
    public bool Can(ICurrentUser user, string permission, object? resource = null) =>
        user.IsAuthenticated && user.Roles.Contains("Admin");
}
