using System.Text.Json;
using Dam.Application.Identity;
using Dam.Application.Messaging;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Shared.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Dam.Api.Endpoints;

public sealed record UpdateTenantRequest(string? Name, JsonElement? Settings);
public sealed record SetStatusRequest(string Status);
public sealed record IdsRequest(IReadOnlyList<Guid> Ids);
public sealed record NameRequest(string Name);
public sealed record CreateRoleRequest(string Name, string? Description, IReadOnlyList<string> Permissions, bool AttributeRestricted, IReadOnlyList<string>? RequiredAttributes);
public sealed record UpdateRoleRequest(string? Description, IReadOnlyList<string>? Permissions, bool? AttributeRestricted, IReadOnlyList<string>? RequiredAttributes);
public sealed record CreateAccessRuleRequest(string PrincipalType, Guid PrincipalId, string ScopeType, string? ScopeValue, Dictionary<string, string[]>? Attributes, IReadOnlyList<string> Permissions);
public sealed record UpdateAccessRuleRequest(string ScopeType, string? ScopeValue, Dictionary<string, string[]>? Attributes, IReadOnlyList<string> Permissions);

public static class IdentityEndpoints
{
    /// <summary>Run a request; on success hand its value to <paramref name="ok"/>, otherwise return problem+json.</summary>
    internal static async Task<IResult> Run<T>(IDispatcher d, IRequest<T> request, Func<T, Task<IResult>> ok, CancellationToken ct)
    {
        var r = await d.Send(request, ct);
        return r.IsSuccess ? await ok(r.Value) : r.Error.ToProblem();
    }

    /// <summary>Run a command that returns an id, then answer with the freshly committed state of that entity.</summary>
    internal static Task<IResult> Then<TDto>(IDispatcher d, IRequest<Guid> command, Func<Guid, IRequest<TDto>> read, CancellationToken ct,
        string? createdAt = null) =>
        Run(d, command, async id =>
        {
            var r = await d.Send(read(id), ct);
            if (r.IsFailure) return r.Error.ToProblem();
            return createdAt is null ? Results.Ok(r.Value) : Results.Created($"{createdAt}/{id}", r.Value);
        }, ct);

    public static void MapIdentityEndpoints(this RouteGroupBuilder v1)
    {
        v1.MapGet("/me", (IDispatcher d, CancellationToken ct) => Run(d, new GetMeQuery(), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetMe").WithSummary("The caller: DAM user, effective roles, groups, permissions and attributes").Produces<MeDto>();

        v1.MapGet("/permissions", () => Results.Ok(Permissions.All))
            .WithName("ListPermissions").WithSummary("Every permission a role may hold").Produces<IReadOnlyList<string>>();

        // ---- tenant
        var tenant = v1.MapGroup("/tenants/current").WithTags("Tenant");
        tenant.MapGet("", (IDispatcher d, CancellationToken ct) => Run(d, new GetCurrentTenantQuery(), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetCurrentTenant").Produces<TenantDto>();
        tenant.MapPatch("", (UpdateTenantRequest r, IDispatcher d, CancellationToken ct) =>
                Then(d, new UpdateTenantCommand(r.Name, r.Settings), _ => new GetCurrentTenantQuery(), ct))
            .WithName("UpdateCurrentTenant").Produces<TenantDto>();

        // ---- users
        var users = v1.MapGroup("/users").WithTags("Users");
        users.MapGet("", (IDispatcher d, CancellationToken ct, string? cursor, int? limit,
                [FromQuery(Name = "filter[status]")] string? status, [FromQuery(Name = "filter[q]")] string? q) =>
                Run(d, new ListUsersQuery(cursor, limit, status, q), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListUsers").Produces<Page<UserDto>>();
        users.MapGet("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) =>
                Run(d, new GetUserQuery(id), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetUser").Produces<UserDto>();
        users.MapPatch("/{id:guid}", (Guid id, SetStatusRequest r, IDispatcher d, CancellationToken ct) =>
                Then(d, new SetUserStatusCommand(id, r.Status), uid => new GetUserQuery(uid), ct))
            .WithName("SetUserStatus").Produces<UserDto>();
        users.MapPut("/{id:guid}/roles", (Guid id, IdsRequest r, IDispatcher d, CancellationToken ct) =>
                Then(d, new SetUserRolesCommand(id, r.Ids), uid => new GetUserQuery(uid), ct))
            .WithName("SetUserRoles").Produces<UserDto>();

        // ---- groups
        var groups = v1.MapGroup("/groups").WithTags("Groups");
        groups.MapGet("", (IDispatcher d, CancellationToken ct) => Run(d, new ListGroupsQuery(), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListGroups").Produces<IReadOnlyList<GroupDto>>();
        groups.MapGet("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) =>
                Run(d, new GetGroupQuery(id), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetGroup").Produces<GroupDetailDto>();
        groups.MapPost("", (NameRequest r, IDispatcher d, CancellationToken ct) =>
                Then(d, new CreateGroupCommand(r.Name), id => new GetGroupQuery(id), ct, "/api/v1/groups"))
            .WithName("CreateGroup").Produces<GroupDetailDto>(StatusCodes.Status201Created);
        groups.MapPatch("/{id:guid}", (Guid id, NameRequest r, IDispatcher d, CancellationToken ct) =>
                Then(d, new UpdateGroupCommand(id, r.Name), gid => new GetGroupQuery(gid), ct))
            .WithName("UpdateGroup").Produces<GroupDetailDto>();
        groups.MapDelete("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) =>
                Run(d, new DeleteGroupCommand(id), _ => Task.FromResult(Results.NoContent()), ct))
            .WithName("DeleteGroup");
        groups.MapPut("/{id:guid}/roles", (Guid id, IdsRequest r, IDispatcher d, CancellationToken ct) =>
                Then(d, new SetGroupRolesCommand(id, r.Ids), gid => new GetGroupQuery(gid), ct))
            .WithName("SetGroupRoles").Produces<GroupDetailDto>();
        groups.MapPut("/{id:guid}/members", (Guid id, IdsRequest r, IDispatcher d, CancellationToken ct) =>
                Then(d, new SetGroupMembersCommand(id, r.Ids), gid => new GetGroupQuery(gid), ct))
            .WithName("SetGroupMembers").Produces<GroupDetailDto>();

        // ---- roles
        var roles = v1.MapGroup("/roles").WithTags("Roles");
        roles.MapGet("", (IDispatcher d, CancellationToken ct) => Run(d, new ListRolesQuery(), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListRoles").Produces<IReadOnlyList<RoleDto>>();
        roles.MapGet("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) =>
                Run(d, new GetRoleQuery(id), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetRole").Produces<RoleDto>();
        roles.MapPost("", (CreateRoleRequest r, IDispatcher d, CancellationToken ct) =>
                Then(d, new CreateRoleCommand(r.Name, r.Description, r.Permissions, r.AttributeRestricted, r.RequiredAttributes), id => new GetRoleQuery(id), ct, "/api/v1/roles"))
            .WithName("CreateRole").Produces<RoleDto>(StatusCodes.Status201Created);
        roles.MapPatch("/{id:guid}", (Guid id, UpdateRoleRequest r, IDispatcher d, CancellationToken ct) =>
                Then(d, new UpdateRoleCommand(id, r.Description, r.Permissions, r.AttributeRestricted, r.RequiredAttributes), rid => new GetRoleQuery(rid), ct))
            .WithName("UpdateRole").Produces<RoleDto>();
        roles.MapDelete("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) =>
                Run(d, new DeleteRoleCommand(id), _ => Task.FromResult(Results.NoContent()), ct))
            .WithName("DeleteRole");

        // ---- access rules
        var rules = v1.MapGroup("/access-rules").WithTags("Access rules");
        rules.MapGet("", (IDispatcher d, CancellationToken ct, [FromQuery(Name = "filter[principalType]")] string? type,
                [FromQuery(Name = "filter[principalId]")] Guid? principalId) =>
                Run(d, new ListAccessRulesQuery(type, principalId), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListAccessRules").Produces<IReadOnlyList<AccessRuleDto>>();
        rules.MapGet("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) =>
                Run(d, new GetAccessRuleQuery(id), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetAccessRule").Produces<AccessRuleDto>();
        rules.MapPost("", (CreateAccessRuleRequest r, IDispatcher d, CancellationToken ct) =>
                Then(d, new CreateAccessRuleCommand(r.PrincipalType, r.PrincipalId, r.ScopeType, r.ScopeValue, r.Attributes, r.Permissions),
                    id => new GetAccessRuleQuery(id), ct, "/api/v1/access-rules"))
            .WithName("CreateAccessRule").Produces<AccessRuleDto>(StatusCodes.Status201Created);
        rules.MapPut("/{id:guid}", (Guid id, UpdateAccessRuleRequest r, IDispatcher d, CancellationToken ct) =>
                Then(d, new UpdateAccessRuleCommand(id, r.ScopeType, r.ScopeValue, r.Attributes, r.Permissions), rid => new GetAccessRuleQuery(rid), ct))
            .WithName("UpdateAccessRule").Produces<AccessRuleDto>();
        rules.MapDelete("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) =>
                Run(d, new DeleteAccessRuleCommand(id), _ => Task.FromResult(Results.NoContent()), ct))
            .WithName("DeleteAccessRule");
    }
}
