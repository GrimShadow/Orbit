using System.Text.RegularExpressions;
using Group = Dam.Domain.Identity.Group;
using Dam.Application.Abstractions;
using Dam.Application.Messaging;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Identity;
using Dam.Shared.Identity;
using FluentValidation;

namespace Dam.Application.Identity;

// ---------------------------------------------------------------- roles

public sealed record ListRolesQuery : IQuery<IReadOnlyList<RoleDto>>, IAuthorizedRequest
{
    public string Permission => Permissions.RolesManage;
}

public sealed class ListRolesHandler(IRepository<Role> roles, IQueryExecutor q) : IRequestHandler<ListRolesQuery, IReadOnlyList<RoleDto>>
{
    public async Task<Result<IReadOnlyList<RoleDto>>> Handle(ListRolesQuery r, CancellationToken ct) =>
        (await q.ToListAsync(roles.Query().OrderByDescending(x => x.IsBuiltIn).ThenBy(x => x.Name), ct)).Select(IdentityReader.ToDto).ToList();
}

public sealed record GetRoleQuery(Guid Id) : IQuery<RoleDto>, IAuthorizedRequest
{
    public string Permission => Permissions.RolesManage;
}

public sealed class GetRoleHandler(IRepository<Role> roles, IQueryExecutor q) : IRequestHandler<GetRoleQuery, RoleDto>
{
    public async Task<Result<RoleDto>> Handle(GetRoleQuery r, CancellationToken ct) =>
        await q.FirstOrDefaultAsync(roles.Query().Where(x => x.Id == r.Id), ct) is { } role ? IdentityReader.ToDto(role) : Error.NotFound("Role not found.");
}

public sealed record CreateRoleCommand(
    string Name, string? Description, IReadOnlyList<string> Permissions, bool AttributeRestricted, IReadOnlyList<string>? RequiredAttributes)
    : ICommand<Guid>, IAuthorizedRequest
{
    string IAuthorizedRequest.Permission => Domain.Authorization.Permissions.RolesManage;
}

public sealed class CreateRoleValidator : AbstractValidator<CreateRoleCommand>
{
    public CreateRoleValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.Permissions).KnownPermissions();
        RuleFor(x => x.RequiredAttributes).Must(a => a is null || a.All(Dimensions.All.Contains)).WithMessage("Unknown attribute.");
    }
}

public sealed class CreateRoleHandler(IRepository<Role> roles, IQueryExecutor q, ITenantContext tenant, IEventCollector events)
    : IRequestHandler<CreateRoleCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateRoleCommand c, CancellationToken ct)
    {
        var name = c.Name.Trim();
        if (await q.AnyAsync(roles.Query().Where(r => r.Name.ToLower() == name.ToLower()), ct))
            return Error.Conflict($"A role named '{name}' already exists.");
        var role = Role.Create(tenant.TenantId, name, c.Description ?? "", c.Permissions, c.AttributeRestricted, c.RequiredAttributes);
        roles.Add(role);
        events.Raise(Events.Changed("role.created", "role", role.Id));
        return role.Id;
    }
}

public sealed record UpdateRoleCommand(
    Guid Id, string? Description, IReadOnlyList<string>? Permissions, bool? AttributeRestricted, IReadOnlyList<string>? RequiredAttributes)
    : ICommand<Guid>, IAuthorizedRequest
{
    string IAuthorizedRequest.Permission => Domain.Authorization.Permissions.RolesManage;
}

public sealed class UpdateRoleValidator : AbstractValidator<UpdateRoleCommand>
{
    public UpdateRoleValidator()
    {
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.Permissions!).KnownPermissions().When(x => x.Permissions is not null);
        RuleFor(x => x.RequiredAttributes).Must(a => a is null || a.All(Dimensions.All.Contains)).WithMessage("Unknown attribute.");
    }
}

public sealed class UpdateRoleHandler(IRepository<Role> roles, IQueryExecutor q, IEventCollector events) : IRequestHandler<UpdateRoleCommand, Guid>
{
    public async Task<Result<Guid>> Handle(UpdateRoleCommand c, CancellationToken ct)
    {
        var role = await q.FirstOrDefaultAsync(roles.Query().Where(r => r.Id == c.Id), ct);
        if (role is null) return Error.NotFound("Role not found.");
        if (role.IsBuiltIn && (c.Permissions is not null || c.AttributeRestricted is not null || c.RequiredAttributes is not null))
            return Error.Conflict("Built-in roles are defined by the platform; only their description can change. Create a custom role instead.");
        role.Update(c.Description, c.Permissions, c.AttributeRestricted, c.RequiredAttributes);
        events.Raise(Events.Changed("role.updated", "role", role.Id));
        return role.Id;
    }
}

public sealed record DeleteRoleCommand(Guid Id) : ICommand<Guid>, IAuthorizedRequest
{
    public string Permission => Permissions.RolesManage;
}

public sealed class DeleteRoleHandler(
    IRepository<Role> roles, IRepository<UserRole> userRoles, IRepository<GroupRole> groupRoles, IRepository<AccessRule> rules,
    IQueryExecutor q, IEventCollector events) : IRequestHandler<DeleteRoleCommand, Guid>
{
    public async Task<Result<Guid>> Handle(DeleteRoleCommand c, CancellationToken ct)
    {
        var role = await q.FirstOrDefaultAsync(roles.Query().Where(r => r.Id == c.Id), ct);
        if (role is null) return Error.NotFound("Role not found.");
        if (role.IsBuiltIn) return Error.Conflict("Built-in roles cannot be deleted.");
        userRoles.RemoveRange(await q.ToListAsync(userRoles.Query().Where(x => x.RoleId == role.Id), ct));
        groupRoles.RemoveRange(await q.ToListAsync(groupRoles.Query().Where(x => x.RoleId == role.Id), ct));
        rules.RemoveRange(await q.ToListAsync(rules.Query().Where(r => r.PrincipalType == PrincipalType.Role && r.PrincipalId == role.Id), ct));
        roles.Remove(role);
        events.Raise(Events.Changed("role.deleted", "role", role.Id));
        return role.Id;
    }
}

// ---------------------------------------------------------------- access rules

public sealed record ListAccessRulesQuery(string? PrincipalType, Guid? PrincipalId) : IQuery<IReadOnlyList<AccessRuleDto>>, IAuthorizedRequest
{
    public string Permission => Permissions.AccessRulesManage;
}

public sealed class ListAccessRulesHandler(IRepository<AccessRule> rules, IQueryExecutor q)
    : IRequestHandler<ListAccessRulesQuery, IReadOnlyList<AccessRuleDto>>
{
    public async Task<Result<IReadOnlyList<AccessRuleDto>>> Handle(ListAccessRulesQuery r, CancellationToken ct)
    {
        var query = rules.Query();
        if (Enum.TryParse<PrincipalType>(r.PrincipalType, true, out var type)) query = query.Where(x => x.PrincipalType == type);
        if (r.PrincipalId is { } pid) query = query.Where(x => x.PrincipalId == pid);
        return (await q.ToListAsync(query.OrderBy(x => x.Id), ct)).Select(IdentityReader.ToDto).ToList();
    }
}

public sealed record GetAccessRuleQuery(Guid Id) : IQuery<AccessRuleDto>, IAuthorizedRequest
{
    public string Permission => Permissions.AccessRulesManage;
}

public sealed class GetAccessRuleHandler(IRepository<AccessRule> rules, IQueryExecutor q) : IRequestHandler<GetAccessRuleQuery, AccessRuleDto>
{
    public async Task<Result<AccessRuleDto>> Handle(GetAccessRuleQuery r, CancellationToken ct) =>
        await q.FirstOrDefaultAsync(rules.Query().Where(x => x.Id == r.Id), ct) is { } rule ? IdentityReader.ToDto(rule) : Error.NotFound("Access rule not found.");
}

public sealed record CreateAccessRuleCommand(
    string PrincipalType, Guid PrincipalId, string ScopeType, string? ScopeValue,
    IReadOnlyDictionary<string, string[]>? Attributes, IReadOnlyList<string> Permissions) : ICommand<Guid>, IAuthorizedRequest
{
    string IAuthorizedRequest.Permission => Domain.Authorization.Permissions.AccessRulesManage;
}

public sealed record UpdateAccessRuleCommand(
    Guid Id, string ScopeType, string? ScopeValue, IReadOnlyDictionary<string, string[]>? Attributes, IReadOnlyList<string> Permissions)
    : ICommand<Guid>, IAuthorizedRequest
{
    string IAuthorizedRequest.Permission => Domain.Authorization.Permissions.AccessRulesManage;
}

internal static partial class ScopeRules
{
    /// <summary>Dotted lowercase labels, like an ltree path: brand.thar.hero</summary>
    [GeneratedRegex("^[A-Za-z0-9_]+(\\.[A-Za-z0-9_]+)*$")]
    private static partial Regex FolderPath();

    public static IRuleBuilderOptions<T, string?> ValidScope<T>(this IRuleBuilder<T, string?> valueRule, Func<T, string> type) =>
        valueRule.Must((cmd, value) => Check(type(cmd), value)).WithMessage(
            "Scope value: folder = dotted path (brand.thar), collection = id, asset_type = name, all = leave empty.");

    private static bool Check(string type, string? value) => Enum.TryParse<ScopeType>(type.Replace("_", ""), true, out var t) && t switch
    {
        ScopeType.All => string.IsNullOrEmpty(value),
        ScopeType.Folder => value is not null && FolderPath().IsMatch(value) && value.Length <= 500,
        ScopeType.Collection => Guid.TryParse(value, out _),
        ScopeType.AssetType => !string.IsNullOrWhiteSpace(value) && value.Length <= 50,
        _ => false,
    };

    public static ScopeType Parse(string type) => Enum.Parse<ScopeType>(type.Replace("_", ""), true);

    public static Dictionary<string, string[]> Clean(IReadOnlyDictionary<string, string[]>? a) =>
        (a ?? new Dictionary<string, string[]>()).Where(kv => kv.Value.Length > 0)
            .ToDictionary(kv => kv.Key, kv => kv.Value.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct().ToArray());
}

public sealed class CreateAccessRuleValidator : AbstractValidator<CreateAccessRuleCommand>
{
    public CreateAccessRuleValidator()
    {
        RuleFor(x => x.PrincipalType).Must(t => Enum.TryParse<PrincipalType>(t, true, out _)).WithMessage("Principal type must be user, group or role.");
        RuleFor(x => x.PrincipalId).NotEmpty();
        RuleFor(x => x.ScopeType).Must(t => Enum.TryParse<ScopeType>(t.Replace("_", ""), true, out _)).WithMessage("Scope must be all, folder, collection or asset_type.");
        RuleFor(x => x.ScopeValue).ValidScope(x => x.ScopeType);
        RuleFor(x => x.Attributes).KnownDimensions();
        RuleFor(x => x.Permissions).KnownPermissions();
    }
}

public sealed class UpdateAccessRuleValidator : AbstractValidator<UpdateAccessRuleCommand>
{
    public UpdateAccessRuleValidator()
    {
        RuleFor(x => x.ScopeType).Must(t => Enum.TryParse<ScopeType>(t.Replace("_", ""), true, out _)).WithMessage("Scope must be all, folder, collection or asset_type.");
        RuleFor(x => x.ScopeValue).ValidScope(x => x.ScopeType);
        RuleFor(x => x.Attributes).KnownDimensions();
        RuleFor(x => x.Permissions).KnownPermissions();
    }
}

public sealed class CreateAccessRuleHandler(
    IRepository<AccessRule> rules, IRepository<User> users, IRepository<Group> groups, IRepository<Role> roles,
    IQueryExecutor q, ITenantContext tenant, ICurrentUser me, IClock clock, IEventCollector events)
    : IRequestHandler<CreateAccessRuleCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateAccessRuleCommand c, CancellationToken ct)
    {
        var type = Enum.Parse<PrincipalType>(c.PrincipalType, true);
        var exists = type switch
        {
            PrincipalType.User => await q.AnyAsync(users.Query().Where(x => x.Id == c.PrincipalId), ct),
            PrincipalType.Group => await q.AnyAsync(groups.Query().Where(x => x.Id == c.PrincipalId), ct),
            _ => await q.AnyAsync(roles.Query().Where(x => x.Id == c.PrincipalId), ct),
        };
        if (!exists) return Error.Validation(new Dictionary<string, string[]> { ["principalId"] = [$"No such {type.ToString().ToLower()}."] });

        var rule = AccessRule.Create(tenant.TenantId, type, c.PrincipalId, ScopeRules.Parse(c.ScopeType), string.IsNullOrEmpty(c.ScopeValue) ? null : c.ScopeValue,
            ScopeRules.Clean(c.Attributes), c.Permissions, me.UserId, clock.UtcNow);
        rules.Add(rule);
        events.Raise(Events.Changed("access_rule.created", "access_rule", rule.Id));
        return rule.Id;
    }
}

public sealed class UpdateAccessRuleHandler(IRepository<AccessRule> rules, IQueryExecutor q, IEventCollector events)
    : IRequestHandler<UpdateAccessRuleCommand, Guid>
{
    public async Task<Result<Guid>> Handle(UpdateAccessRuleCommand c, CancellationToken ct)
    {
        var rule = await q.FirstOrDefaultAsync(rules.Query().Where(r => r.Id == c.Id), ct);
        if (rule is null) return Error.NotFound("Access rule not found.");
        rule.Update(ScopeRules.Parse(c.ScopeType), string.IsNullOrEmpty(c.ScopeValue) ? null : c.ScopeValue, ScopeRules.Clean(c.Attributes), c.Permissions);
        events.Raise(Events.Changed("access_rule.updated", "access_rule", rule.Id));
        return rule.Id;
    }
}

public sealed record DeleteAccessRuleCommand(Guid Id) : ICommand<Guid>, IAuthorizedRequest
{
    public string Permission => Permissions.AccessRulesManage;
}

public sealed class DeleteAccessRuleHandler(IRepository<AccessRule> rules, IQueryExecutor q, IEventCollector events)
    : IRequestHandler<DeleteAccessRuleCommand, Guid>
{
    public async Task<Result<Guid>> Handle(DeleteAccessRuleCommand c, CancellationToken ct)
    {
        var rule = await q.FirstOrDefaultAsync(rules.Query().Where(r => r.Id == c.Id), ct);
        if (rule is null) return Error.NotFound("Access rule not found.");
        rules.Remove(rule);
        events.Raise(Events.Changed("access_rule.deleted", "access_rule", rule.Id));
        return rule.Id;
    }
}
