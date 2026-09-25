using Dam.Application.Abstractions;
using Dam.Application.Messaging;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Identity;
using Dam.Shared.Identity;

namespace Dam.Application.Identity;

/// <summary>The caller as DAM sees them. Needs no permission: everyone may see their own access.</summary>
public sealed record GetMeQuery : IQuery<MeDto>;

public sealed class GetMeHandler(
    IAuthorizationService authz, IRepository<User> users, IQueryExecutor q, IdentityReader reader, ITenantContext tenant)
    : IRequestHandler<GetMeQuery, MeDto>
{
    public async Task<Result<MeDto>> Handle(GetMeQuery request, CancellationToken ct)
    {
        var profile = await authz.GetProfileAsync(ct);
        var user = profile.UserId == Guid.Empty ? null : await q.FirstOrDefaultAsync(users.Query().Where(u => u.Id == profile.UserId), ct);
        var groups = user is null ? [] : (await reader.GroupNamesByUserAsync([user.Id], ct)).GetValueOrDefault(user.Id) ?? [];
        return new MeDto(
            user?.Id, user?.Email, user?.DisplayName, tenant.TenantId, user?.Status.ToString().ToLower(),
            profile.Roles.Select(r => r.Name).Order().ToList(), groups,
            PolicyEngine.RolesInEffect(profile).SelectMany(r => r.Permissions).Distinct().Order().ToList(),
            profile.Attributes, profile.Rules.Count, PolicyEngine.RolesBlocked(profile));
    }
}
