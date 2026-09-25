using Dam.Application.Abstractions;
using Dam.Domain.Authorization;
using Dam.Domain.Identity;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dam.Infrastructure.Identity;

/// <summary>
/// Assembles the current user's <see cref="AccessProfile"/>: the DAM user row (status, attributes), effective roles
/// (token roles + direct + via groups) and the access rules that apply to the user, their groups or their roles.
/// Cached for the lifetime of the request scope.
/// </summary>
public sealed class AccessProfileProvider(DamDbContext db, ITenantContext tenant, ICurrentUser me) : IAccessProfileProvider
{
    private Task<AccessProfile>? _profile;

    public Task<AccessProfile> GetAsync(CancellationToken ct) => _profile ??= LoadAsync();

    private async Task<AccessProfile> LoadAsync()
    {
        var tenantId = tenant.TenantId;
        var none = new AccessProfile(tenantId, Guid.Empty, false, new Dictionary<string, string[]>(), [], []);
        if (!me.IsAuthenticated || me.Subject is null || tenantId == Guid.Empty) return none;

        var user = await db.Users.FirstOrDefaultAsync(u => u.ExternalSubject == me.Subject);
        if (user is null) return none; // not provisioned yet: no access until JIT provisioning has run
        var tenantRow = await db.Tenants.FirstOrDefaultAsync();
        var active = user.Status == UserStatus.Active && tenantRow is { Status: TenantStatus.Active };

        var groupIds = await db.UserGroups.Where(x => x.UserId == user.Id).Select(x => x.GroupId).ToListAsync();
        var directRoleIds = await db.UserRoles.Where(x => x.UserId == user.Id).Select(x => x.RoleId).ToListAsync();
        var viaGroupIds = await db.GroupRoles.Where(x => groupIds.Contains(x.GroupId)).Select(x => x.RoleId).ToListAsync();
        var roleIds = directRoleIds.Concat(viaGroupIds).Distinct().ToList();
        var tokenRoles = me.Roles.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A tenant has a handful of roles, so match the token's role names in memory (case-insensitively) instead of in SQL.
        var roles = (await db.Roles.ToListAsync())
            .Where(r => roleIds.Contains(r.Id) || tokenRoles.Contains(r.Name)).ToList();
        var effectiveRoleIds = roles.Select(r => r.Id).ToList();

        var rules = await db.AccessRules.Where(r =>
            (r.PrincipalType == PrincipalType.User && r.PrincipalId == user.Id)
            || (r.PrincipalType == PrincipalType.Group && groupIds.Contains(r.PrincipalId))
            || (r.PrincipalType == PrincipalType.Role && effectiveRoleIds.Contains(r.PrincipalId))).ToListAsync();

        return new AccessProfile(
            tenantId, user.Id, active, user.Attributes,
            roles.Select(r => new RoleGrant(r.Name, r.Permissions, r.AttributeRestricted, r.RequiredAttributes)).ToList(),
            rules.Select(r => r.ToGrant()).ToList());
    }
}
