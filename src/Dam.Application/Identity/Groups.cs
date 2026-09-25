using Dam.Application.Abstractions;
using Dam.Application.Messaging;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Identity;
using Dam.Shared.Identity;
using FluentValidation;

namespace Dam.Application.Identity;

public sealed record ListGroupsQuery : IQuery<IReadOnlyList<GroupDto>>, IAuthorizedRequest
{
    public string Permission => Permissions.GroupsManage;
}

public sealed class ListGroupsHandler(IRepository<Group> groups, IRepository<UserGroup> members, IQueryExecutor q, IdentityReader reader)
    : IRequestHandler<ListGroupsQuery, IReadOnlyList<GroupDto>>
{
    public async Task<Result<IReadOnlyList<GroupDto>>> Handle(ListGroupsQuery r, CancellationToken ct)
    {
        var list = await q.ToListAsync(groups.Query().OrderBy(g => g.Name), ct);
        var ids = list.Select(g => g.Id).ToArray();
        var roleNames = await reader.RoleNamesByGroupAsync(ids, ct);
        var counts = (await q.ToListAsync(members.Query().GroupBy(m => m.GroupId).Select(g => new { g.Key, N = g.Count() }), ct))
            .ToDictionary(x => x.Key, x => x.N);
        return list.Select(g => new GroupDto(g.Id, g.Name, g.Source.ToString().ToLower(),
            roleNames.GetValueOrDefault(g.Id) ?? [], counts.GetValueOrDefault(g.Id))).ToList();
    }
}

public sealed record GetGroupQuery(Guid Id) : IQuery<GroupDetailDto>, IAuthorizedRequest
{
    public string Permission => Permissions.GroupsManage;
}

public sealed class GetGroupHandler(
    IRepository<Group> groups, IRepository<UserGroup> members, IRepository<User> users, IQueryExecutor q, IdentityReader reader)
    : IRequestHandler<GetGroupQuery, GroupDetailDto>
{
    public async Task<Result<GroupDetailDto>> Handle(GetGroupQuery r, CancellationToken ct)
    {
        var g = await q.FirstOrDefaultAsync(groups.Query().Where(x => x.Id == r.Id), ct);
        if (g is null) return Error.NotFound("Group not found.");
        var people = await q.ToListAsync(
            from m in members.Query() where m.GroupId == g.Id join u in users.Query() on m.UserId equals u.Id
            orderby u.Email select new UserRef(u.Id, u.Email, u.DisplayName), ct);
        var roleNames = await reader.RoleNamesByGroupAsync([g.Id], ct);
        return new GroupDetailDto(new GroupDto(g.Id, g.Name, g.Source.ToString().ToLower(), roleNames.GetValueOrDefault(g.Id) ?? [], people.Count), people);
    }
}

public sealed record CreateGroupCommand(string Name) : ICommand<Guid>, IAuthorizedRequest
{
    public string Permission => Permissions.GroupsManage;
}

public sealed class CreateGroupValidator : AbstractValidator<CreateGroupCommand>
{
    public CreateGroupValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
}

public sealed class CreateGroupHandler(IRepository<Group> groups, IQueryExecutor q, ITenantContext tenant, IEventCollector events)
    : IRequestHandler<CreateGroupCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateGroupCommand c, CancellationToken ct)
    {
        var name = c.Name.Trim();
        if (await q.AnyAsync(groups.Query().Where(g => g.Name.ToLower() == name.ToLower()), ct))
            return Error.Conflict($"A group named '{name}' already exists.");
        var g = Group.Create(tenant.TenantId, name, GroupSource.Local);
        groups.Add(g);
        events.Raise(Events.Changed("group.created", "group", g.Id));
        return g.Id;
    }
}

/// <summary>Only local groups can be renamed or deleted; IdP groups mirror the identity provider and reappear at next login.</summary>
public sealed record UpdateGroupCommand(Guid Id, string Name) : ICommand<Guid>, IAuthorizedRequest
{
    public string Permission => Permissions.GroupsManage;
}

public sealed class UpdateGroupValidator : AbstractValidator<UpdateGroupCommand>
{
    public UpdateGroupValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
}

public sealed class UpdateGroupHandler(IRepository<Group> groups, IQueryExecutor q, IEventCollector events)
    : IRequestHandler<UpdateGroupCommand, Guid>
{
    public async Task<Result<Guid>> Handle(UpdateGroupCommand c, CancellationToken ct)
    {
        var g = await q.FirstOrDefaultAsync(groups.Query().Where(x => x.Id == c.Id), ct);
        if (g is null) return Error.NotFound("Group not found.");
        if (g.Source == GroupSource.Idp) return Error.Conflict("Groups from the identity provider cannot be renamed here.");
        var name = c.Name.Trim();
        if (await q.AnyAsync(groups.Query().Where(x => x.Id != g.Id && x.Name.ToLower() == name.ToLower()), ct))
            return Error.Conflict($"A group named '{name}' already exists.");
        g.Rename(name);
        events.Raise(Events.Changed("group.updated", "group", g.Id));
        return g.Id;
    }
}

public sealed record DeleteGroupCommand(Guid Id) : ICommand<Guid>, IAuthorizedRequest
{
    public string Permission => Permissions.GroupsManage;
}

public sealed class DeleteGroupHandler(
    IRepository<Group> groups, IRepository<UserGroup> members, IRepository<GroupRole> groupRoles, IRepository<AccessRule> rules,
    IQueryExecutor q, IEventCollector events) : IRequestHandler<DeleteGroupCommand, Guid>
{
    public async Task<Result<Guid>> Handle(DeleteGroupCommand c, CancellationToken ct)
    {
        var g = await q.FirstOrDefaultAsync(groups.Query().Where(x => x.Id == c.Id), ct);
        if (g is null) return Error.NotFound("Group not found.");
        if (g.Source == GroupSource.Idp) return Error.Conflict("Groups from the identity provider cannot be deleted here.");
        members.RemoveRange(await q.ToListAsync(members.Query().Where(m => m.GroupId == g.Id), ct));
        groupRoles.RemoveRange(await q.ToListAsync(groupRoles.Query().Where(m => m.GroupId == g.Id), ct));
        rules.RemoveRange(await q.ToListAsync(rules.Query().Where(r => r.PrincipalType == PrincipalType.Group && r.PrincipalId == g.Id), ct));
        groups.Remove(g);
        events.Raise(Events.Changed("group.deleted", "group", g.Id));
        return g.Id;
    }
}

/// <summary>Replaces the roles a group grants to its members (works for IdP and local groups).</summary>
public sealed record SetGroupRolesCommand(Guid Id, IReadOnlyList<Guid> RoleIds) : ICommand<Guid>, IAuthorizedRequest
{
    public string Permission => Permissions.GroupsManage;
}

public sealed class SetGroupRolesHandler(
    IRepository<Group> groups, IRepository<Role> roles, IRepository<GroupRole> groupRoles, IQueryExecutor q,
    ITenantContext tenant, IEventCollector events) : IRequestHandler<SetGroupRolesCommand, Guid>
{
    public async Task<Result<Guid>> Handle(SetGroupRolesCommand c, CancellationToken ct)
    {
        var g = await q.FirstOrDefaultAsync(groups.Query().Where(x => x.Id == c.Id), ct);
        if (g is null) return Error.NotFound("Group not found.");
        var wanted = c.RoleIds.Distinct().ToList();
        if (await q.CountAsync(roles.Query().Where(r => wanted.Contains(r.Id)), ct) != wanted.Count)
            return Error.Validation(new Dictionary<string, string[]> { ["roleIds"] = ["One or more roles do not exist."] });

        var current = await q.ToListAsync(groupRoles.Query().Where(x => x.GroupId == g.Id), ct);
        groupRoles.RemoveRange(current.Where(x => !wanted.Contains(x.RoleId)));
        groupRoles.AddRange(wanted.Where(id => current.All(x => x.RoleId != id))
            .Select(id => new GroupRole { TenantId = tenant.TenantId, GroupId = g.Id, RoleId = id }));
        events.Raise(Events.Changed("group.roles.changed", "group", g.Id));
        return g.Id;
    }
}

/// <summary>Replaces the members of a LOCAL group. IdP groups take their members from the token.</summary>
public sealed record SetGroupMembersCommand(Guid Id, IReadOnlyList<Guid> UserIds) : ICommand<Guid>, IAuthorizedRequest
{
    public string Permission => Permissions.GroupsManage;
}

public sealed class SetGroupMembersHandler(
    IRepository<Group> groups, IRepository<User> users, IRepository<UserGroup> members, IQueryExecutor q,
    ITenantContext tenant, IEventCollector events) : IRequestHandler<SetGroupMembersCommand, Guid>
{
    public async Task<Result<Guid>> Handle(SetGroupMembersCommand c, CancellationToken ct)
    {
        var g = await q.FirstOrDefaultAsync(groups.Query().Where(x => x.Id == c.Id), ct);
        if (g is null) return Error.NotFound("Group not found.");
        if (g.Source == GroupSource.Idp) return Error.Conflict("Members of identity-provider groups come from the token.");
        var wanted = c.UserIds.Distinct().ToList();
        if (await q.CountAsync(users.Query().Where(u => wanted.Contains(u.Id)), ct) != wanted.Count)
            return Error.Validation(new Dictionary<string, string[]> { ["userIds"] = ["One or more users do not exist."] });

        var current = await q.ToListAsync(members.Query().Where(x => x.GroupId == g.Id), ct);
        members.RemoveRange(current.Where(x => !wanted.Contains(x.UserId)));
        members.AddRange(wanted.Where(id => current.All(x => x.UserId != id))
            .Select(id => new UserGroup { TenantId = tenant.TenantId, GroupId = g.Id, UserId = id }));
        events.Raise(Events.Changed("group.members.changed", "group", g.Id));
        return g.Id;
    }
}
