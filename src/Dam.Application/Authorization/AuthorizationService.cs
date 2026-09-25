using Dam.Application.Abstractions;
using Dam.Domain.Authorization;

namespace Dam.Application.Authorization;

/// <summary>Thin adapter: loads the user's profile once per request scope, then delegates to the pure <see cref="PolicyEngine"/>.</summary>
public sealed class AuthorizationService(IAccessProfileProvider profiles) : IAuthorizationService
{
    public async Task<bool> CanAsync(string permission, ResourceContext? resource = null, CancellationToken ct = default) =>
        PolicyEngine.Can(await profiles.GetAsync(ct), permission, resource);

    public async Task<IReadOnlyList<ScopeClause>> GetScopeAsync(string permission, CancellationToken ct = default) =>
        PolicyEngine.GetScope(await profiles.GetAsync(ct), permission);

    public Task<AccessProfile> GetProfileAsync(CancellationToken ct = default) => profiles.GetAsync(ct);
}
