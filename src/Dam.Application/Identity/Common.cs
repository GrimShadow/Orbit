using Dam.Application.Abstractions;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Identity;
using Dam.Shared.Identity;
using FluentValidation;

namespace Dam.Application.Identity;

internal static class Paging
{
    public const int DefaultLimit = 50, MaxLimit = 200;
    public static int Clamp(int? limit) => Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
    public static Guid? ParseCursor(string? cursor) => Guid.TryParse(cursor, out var g) ? g : null;
}

internal static class Rules
{
    public static IRuleBuilderOptions<T, IEnumerable<string>> KnownPermissions<T>(this IRuleBuilder<T, IEnumerable<string>> b) =>
        b.NotNull().Must(ps => ps.All(Permissions.IsKnown)).WithMessage("Unknown permission in list.")
         .Must(ps => ps.Any()).WithMessage("At least one permission is required.");

    public static IRuleBuilderOptions<T, IReadOnlyDictionary<string, string[]>?> KnownDimensions<T>(this IRuleBuilder<T, IReadOnlyDictionary<string, string[]>?> b) =>
        b.Must(a => a is null || a.Keys.All(Dimensions.All.Contains)).WithMessage($"Attribute keys must be one of: {string.Join(", ", Dimensions.All)}.");
}

/// <summary>Resolves the human names shown next to ids (roles and groups of users, roles of groups).</summary>
public sealed class IdentityReader(
    IRepository<Role> roles, IRepository<Group> groups, IRepository<UserRole> userRoles, IRepository<UserGroup> userGroups,
    IRepository<GroupRole> groupRoles, IQueryExecutor q)
{
    public async Task<Dictionary<Guid, List<string>>> RoleNamesByUserAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        var rows = await q.ToListAsync(
            from ur in userRoles.Query() where userIds.Contains(ur.UserId)
            join r in roles.Query() on ur.RoleId equals r.Id select new { ur.UserId, r.Name }, ct);
        return rows.GroupBy(x => x.UserId).ToDictionary(g => g.Key, g => g.Select(x => x.Name).Order().ToList());
    }

    public async Task<Dictionary<Guid, List<string>>> GroupNamesByUserAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        var rows = await q.ToListAsync(
            from ug in userGroups.Query() where userIds.Contains(ug.UserId)
            join g in groups.Query() on ug.GroupId equals g.Id select new { ug.UserId, g.Name }, ct);
        return rows.GroupBy(x => x.UserId).ToDictionary(g => g.Key, g => g.Select(x => x.Name).Order().ToList());
    }

    public async Task<Dictionary<Guid, List<string>>> RoleNamesByGroupAsync(IReadOnlyCollection<Guid> groupIds, CancellationToken ct)
    {
        var rows = await q.ToListAsync(
            from gr in groupRoles.Query() where groupIds.Contains(gr.GroupId)
            join r in roles.Query() on gr.RoleId equals r.Id select new { gr.GroupId, r.Name }, ct);
        return rows.GroupBy(x => x.GroupId).ToDictionary(g => g.Key, g => g.Select(x => x.Name).Order().ToList());
    }

    public async Task<UserDto[]> ToDtosAsync(IReadOnlyList<User> users, CancellationToken ct)
    {
        var ids = users.Select(u => u.Id).ToArray();
        var rn = await RoleNamesByUserAsync(ids, ct);
        var gn = await GroupNamesByUserAsync(ids, ct);
        return users.Select(u => new UserDto(u.Id, u.ExternalSubject, u.Email, u.DisplayName, u.Status.ToString().ToLower(),
            u.Attributes, rn.GetValueOrDefault(u.Id) ?? [], gn.GetValueOrDefault(u.Id) ?? [], u.CreatedAt, u.LastSeenAt)).ToArray();
    }

    public static RoleDto ToDto(Role r) =>
        new(r.Id, r.Name, r.Description, r.IsBuiltIn, r.Permissions, r.AttributeRestricted, r.RequiredAttributes);

    public static AccessRuleDto ToDto(AccessRule r) =>
        new(r.Id, r.PrincipalType.ToString().ToLower(), r.PrincipalId, r.ScopeType.ToString().ToLower(),
            r.ScopeValue, r.Attributes, r.Permissions, r.CreatedAt);
}

internal static class Events
{
    public static EntityChanged Changed(string name, string type, Guid id) => new(name, type, id);
}
