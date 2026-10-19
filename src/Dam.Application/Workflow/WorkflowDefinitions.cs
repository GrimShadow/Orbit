using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Application.Identity;
using Dam.Application.Messaging;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Content;
using Dam.Domain.Identity;
using Dam.Domain.Workflow;
using Dam.Shared.Content;
using FluentValidation;

namespace Dam.Application.Workflow;

internal static class WorkflowMapping
{
    public const int MaxDefinitionBytes = 64 * 1024;

    public static JsonElement ToElement(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    public static WorkflowErrorDto ToDto(WorkflowDefError e) => new(e.Path, e.Message);

    /// <summary>Field errors for a 422: <c>definition</c> or <c>definition.steps[1].id</c>.</summary>
    public static Dictionary<string, string[]> ToFieldErrors(IEnumerable<WorkflowDefError> errors) =>
        errors.GroupBy(e => e.Path.Length == 0 ? "definition" : "definition." + e.Path)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Message).Distinct().ToArray());

    /// <summary>Everything wrong with a definition: the structural rules, then (when those pass) that the named roles and users
    /// exist in this tenant, so a typo cannot create a step nobody can ever decide.</summary>
    public static async Task<(WorkflowDef? Definition, List<WorkflowDefError> Errors, string Json)> CheckAsync(
        JsonElement? definition, IRepository<Role> roles, IRepository<User> users, IQueryExecutor q, CancellationToken ct)
    {
        var present = definition is { ValueKind: not JsonValueKind.Undefined } ? definition : null;
        var json = present is { } d ? JsonSerializer.Serialize(d) : "";
        if (json.Length > MaxDefinitionBytes)
            return (null, [new WorkflowDefError("", $"The definition is too large (at most {MaxDefinitionBytes / 1024} KB).")], json);

        var parsed = WorkflowDefinitionParser.Parse(present);
        var errors = parsed.Errors.ToList();
        if (parsed.Definition is not { } def) return (null, errors, json);

        var roleNames = (await q.ToListAsync(roles.Query().Select(r => r.Name), ct)).ToHashSet(StringComparer.Ordinal);
        var userIds = def.Steps.SelectMany(s => s.Assignees.Users.Concat(s.Escalation?.To.Users ?? [])).Distinct().ToList();
        var knownUsers = userIds.Count == 0 ? [] : (await q.ToListAsync(users.Query().Where(u => userIds.Contains(u.Id)).Select(u => u.Id), ct)).ToHashSet();

        for (var i = 0; i < def.Steps.Count; i++)
        {
            CheckTargets(def.Steps[i].Assignees, $"steps[{i}].assignees", roleNames, knownUsers, errors);
            if (def.Steps[i].Escalation is { } e) CheckTargets(e.To, $"steps[{i}].escalation.to", roleNames, knownUsers, errors);
        }
        return (errors.Count == 0 ? def : null, errors, json);
    }

    private static void CheckTargets(AssigneeSet set, string path, HashSet<string> roleNames, HashSet<Guid> knownUsers, List<WorkflowDefError> errors)
    {
        for (var j = 0; j < set.Roles.Count; j++)
            if (!roleNames.Contains(set.Roles[j])) errors.Add(new WorkflowDefError($"{path}.roles[{j}]", $"There is no role named '{set.Roles[j]}'."));
        for (var j = 0; j < set.Users.Count; j++)
            if (!knownUsers.Contains(set.Users[j])) errors.Add(new WorkflowDefError($"{path}.users[{j}]", "There is no user with this id."));
    }

    public static WorkflowDto ToDto(WorkflowDefinition d, int folders, int bindings, int running) =>
        new(d.Id, d.Name, d.Description, ToElement(d.Json), folders, bindings, running, d.CreatedAt, d.UpdatedAt);
}

/// <summary>Reading definitions is open to those who manage workflows and to folder managers (who pick a folder's default).</summary>
internal static class WorkflowAccess
{
    public static async Task<bool> CanReadDefinitionsAsync(IAuthorizationService authz, CancellationToken ct) =>
        await authz.CanAsync(Permissions.WorkflowManage, null, ct) || await authz.CanAsync(Permissions.FoldersManage, null, ct);
}

// ------------------------------------------------------------------------------------------------------- read

public sealed record ListWorkflowsQuery : IQuery<IReadOnlyList<WorkflowDto>>;

public sealed class ListWorkflowsHandler(
    IRepository<WorkflowDefinition> definitions, IRepository<WorkflowBinding> bindings, IRepository<WorkflowInstance> instances,
    IRepository<Folder> folders, IAuthorizationService authz, IQueryExecutor q) : IRequestHandler<ListWorkflowsQuery, IReadOnlyList<WorkflowDto>>
{
    public async Task<Result<IReadOnlyList<WorkflowDto>>> Handle(ListWorkflowsQuery r, CancellationToken ct)
    {
        if (!await WorkflowAccess.CanReadDefinitionsAsync(authz, ct)) return Error.Forbidden();
        var all = await q.ToListAsync(definitions.Query().OrderBy(d => d.Name), ct);
        var byFolder = (await q.ToListAsync(folders.Query().Where(f => f.DefaultWorkflowId != null).Select(f => f.DefaultWorkflowId!.Value), ct)).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var byBinding = (await q.ToListAsync(bindings.Query().Select(b => b.WorkflowId), ct)).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var running = (await q.ToListAsync(instances.Query().Where(i => i.Status == WorkflowStatuses.Running && i.DefinitionId != null).Select(i => i.DefinitionId!.Value), ct))
            .GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        return all.Select(d => WorkflowMapping.ToDto(d, byFolder.GetValueOrDefault(d.Id), byBinding.GetValueOrDefault(d.Id), running.GetValueOrDefault(d.Id))).ToList();
    }
}

public sealed record GetWorkflowQuery(Guid Id) : IQuery<WorkflowDto>;

public sealed class GetWorkflowHandler(
    IRepository<WorkflowDefinition> definitions, IRepository<WorkflowBinding> bindings, IRepository<WorkflowInstance> instances,
    IRepository<Folder> folders, IAuthorizationService authz, IQueryExecutor q) : IRequestHandler<GetWorkflowQuery, WorkflowDto>
{
    public async Task<Result<WorkflowDto>> Handle(GetWorkflowQuery r, CancellationToken ct)
    {
        if (!await WorkflowAccess.CanReadDefinitionsAsync(authz, ct)) return Error.Forbidden();
        var d = await q.FirstOrDefaultAsync(definitions.Query().Where(x => x.Id == r.Id), ct);
        if (d is null) return Error.NotFound("Workflow not found.");
        return WorkflowMapping.ToDto(d,
            await q.CountAsync(folders.Query().Where(f => f.DefaultWorkflowId == d.Id), ct),
            await q.CountAsync(bindings.Query().Where(b => b.WorkflowId == d.Id), ct),
            await q.CountAsync(instances.Query().Where(i => i.DefinitionId == d.Id && i.Status == WorkflowStatuses.Running), ct));
    }
}

// ----------------------------------------------------------------------------------------------------- validate

/// <summary>Checks a definition without saving it. An invalid definition is an ordinary answer (<c>valid: false</c> with path
/// addressed errors), not an HTTP error: the designer calls this on every edit.</summary>
public sealed record ValidateWorkflowQuery(JsonElement? Definition) : IQuery<ValidateWorkflowResultDto>, IAuthorizedRequest
{
    public string Permission => Permissions.WorkflowManage;
}

public sealed class ValidateWorkflowHandler(IRepository<Role> roles, IRepository<User> users, IQueryExecutor q)
    : IRequestHandler<ValidateWorkflowQuery, ValidateWorkflowResultDto>
{
    public async Task<Result<ValidateWorkflowResultDto>> Handle(ValidateWorkflowQuery r, CancellationToken ct)
    {
        var (_, errors, _) = await WorkflowMapping.CheckAsync(r.Definition, roles, users, q, ct);
        return new ValidateWorkflowResultDto(errors.Count == 0, errors.Select(WorkflowMapping.ToDto).ToList());
    }
}

// ------------------------------------------------------------------------------------------------ create / update

public sealed record CreateWorkflowCommand(string Name, string? Description, JsonElement? Definition) : ICommand<Guid>, IAuthorizedRequest
{
    public string Permission => Permissions.WorkflowManage;
}

public sealed class CreateWorkflowValidator : AbstractValidator<CreateWorkflowCommand>
{
    public CreateWorkflowValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(1000);
    }
}

public sealed class CreateWorkflowHandler(
    IRepository<WorkflowDefinition> definitions, IRepository<Role> roles, IRepository<User> users, IQueryExecutor q,
    ITenantContext tenant, IAuthorizationService authz, IClock clock, IEventCollector events) : IRequestHandler<CreateWorkflowCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateWorkflowCommand c, CancellationToken ct)
    {
        var (def, errors, json) = await WorkflowMapping.CheckAsync(c.Definition, roles, users, q, ct);
        if (def is null) return Error.Validation(WorkflowMapping.ToFieldErrors(errors));
        var name = c.Name.Trim();
        if (await q.AnyAsync(definitions.Query().Where(d => d.Name.ToLower() == name.ToLower()), ct)) return Error.Conflict($"A workflow named '{name}' already exists.");

        var profile = await authz.GetProfileAsync(ct);
        var created = WorkflowDefinition.Create(tenant.TenantId, name, c.Description, json, profile.UserId, clock.UtcNow);
        definitions.Add(created);
        events.Raise(Events.Changed("workflow.created", "workflow", created.Id));
        return created.Id;
    }
}

public sealed record UpdateWorkflowCommand(Guid Id, string Name, string? Description, JsonElement? Definition) : ICommand<Guid>, IAuthorizedRequest
{
    public string Permission => Permissions.WorkflowManage;
}

public sealed class UpdateWorkflowValidator : AbstractValidator<UpdateWorkflowCommand>
{
    public UpdateWorkflowValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(1000);
    }
}

/// <summary>Replaces a definition. Instances already running keep the snapshot they started with; only later submissions use the new one.</summary>
public sealed class UpdateWorkflowHandler(
    IRepository<WorkflowDefinition> definitions, IRepository<Role> roles, IRepository<User> users, IQueryExecutor q,
    IClock clock, IEventCollector events) : IRequestHandler<UpdateWorkflowCommand, Guid>
{
    public async Task<Result<Guid>> Handle(UpdateWorkflowCommand c, CancellationToken ct)
    {
        var existing = await q.FirstOrDefaultAsync(definitions.Query().Where(d => d.Id == c.Id), ct);
        if (existing is null) return Error.NotFound("Workflow not found.");
        var (def, errors, json) = await WorkflowMapping.CheckAsync(c.Definition, roles, users, q, ct);
        if (def is null) return Error.Validation(WorkflowMapping.ToFieldErrors(errors));
        var name = c.Name.Trim();
        if (await q.AnyAsync(definitions.Query().Where(d => d.Id != c.Id && d.Name.ToLower() == name.ToLower()), ct)) return Error.Conflict($"A workflow named '{name}' already exists.");

        existing.Update(name, c.Description, json, clock.UtcNow);
        events.Raise(Events.Changed("workflow.updated", "workflow", existing.Id));
        return existing.Id;
    }
}

public sealed record DeleteWorkflowCommand(Guid Id) : ICommand<Guid>, IAuthorizedRequest
{
    public string Permission => Permissions.WorkflowManage;
}

public sealed class DeleteWorkflowHandler(
    IRepository<WorkflowDefinition> definitions, IRepository<WorkflowBinding> bindings, IRepository<WorkflowInstance> instances,
    IRepository<Folder> folders, IQueryExecutor q, IEventCollector events) : IRequestHandler<DeleteWorkflowCommand, Guid>
{
    public async Task<Result<Guid>> Handle(DeleteWorkflowCommand c, CancellationToken ct)
    {
        var d = await q.FirstOrDefaultAsync(definitions.Query().Where(x => x.Id == c.Id), ct);
        if (d is null) return Error.NotFound("Workflow not found.");
        var inFolders = await q.CountAsync(folders.Query().Where(f => f.DefaultWorkflowId == d.Id), ct);
        if (inFolders > 0) return Error.Conflict($"{inFolders} folder(s) use this workflow as their default. Change them first.");
        var inBindings = await q.CountAsync(bindings.Query().Where(b => b.WorkflowId == d.Id), ct);
        if (inBindings > 0) return Error.Conflict($"{inBindings} type/region binding(s) use this workflow. Remove them first.");
        var running = await q.CountAsync(instances.Query().Where(i => i.DefinitionId == d.Id && i.Status == WorkflowStatuses.Running), ct);
        if (running > 0) return Error.Conflict($"{running} asset(s) are going through this workflow right now. Wait for them to finish.");

        definitions.Remove(d);
        events.Raise(Events.Changed("workflow.deleted", "workflow", d.Id));
        return d.Id;
    }
}

// ---------------------------------------------------------------------------------------------------- bindings

public sealed record ListWorkflowBindingsQuery : IQuery<IReadOnlyList<WorkflowBindingDto>>;

public sealed class ListWorkflowBindingsHandler(IRepository<WorkflowBinding> bindings, IRepository<WorkflowDefinition> definitions, IAuthorizationService authz, IQueryExecutor q)
    : IRequestHandler<ListWorkflowBindingsQuery, IReadOnlyList<WorkflowBindingDto>>
{
    public async Task<Result<IReadOnlyList<WorkflowBindingDto>>> Handle(ListWorkflowBindingsQuery r, CancellationToken ct)
    {
        if (!await WorkflowAccess.CanReadDefinitionsAsync(authz, ct)) return Error.Forbidden();
        var rows = await q.ToListAsync(bindings.Query(), ct);
        var names = (await q.ToListAsync(definitions.Query().Select(d => new { d.Id, d.Name }), ct)).ToDictionary(d => d.Id, d => d.Name);
        return rows.OrderBy(b => b.AssetType ?? "~", StringComparer.Ordinal).ThenBy(b => b.Region ?? "~", StringComparer.OrdinalIgnoreCase)
            .Select(b => new WorkflowBindingDto(b.Id, b.WorkflowId, names.GetValueOrDefault(b.WorkflowId) ?? "", b.AssetType, b.Region)).ToList();
    }
}

/// <summary>Replaces the whole list of type/region bindings (ADR-0030). A binding that keeps its type and region keeps its id.</summary>
public sealed record ReplaceWorkflowBindingsCommand(IReadOnlyList<WorkflowBindingInput> Bindings) : ICommand<int>, IAuthorizedRequest
{
    public string Permission => Permissions.WorkflowManage;
}

public sealed class ReplaceWorkflowBindingsValidator : AbstractValidator<ReplaceWorkflowBindingsCommand>
{
    public ReplaceWorkflowBindingsValidator()
    {
        RuleFor(x => x.Bindings).NotNull().Must(b => b is null || b.Count <= 200).WithMessage("At most 200 bindings.");
        RuleForEach(x => x.Bindings).ChildRules(b =>
        {
            b.RuleFor(x => x.WorkflowId).NotEmpty();
            b.RuleFor(x => x.AssetType).Must(t => string.IsNullOrWhiteSpace(t) || AssetTypes.IsKnown(t.Trim()))
                .WithMessage($"Must be one of: {string.Join(", ", AssetTypes.All)}.");
            b.RuleFor(x => x.Region).MaximumLength(64);
            b.RuleFor(x => x).Must(x => !string.IsNullOrWhiteSpace(x.AssetType) || !string.IsNullOrWhiteSpace(x.Region))
                .WithMessage("Choose an asset type, a region, or both.").WithName("AssetType");
        });
    }
}

public sealed class ReplaceWorkflowBindingsHandler(
    IRepository<WorkflowBinding> bindings, IRepository<WorkflowDefinition> definitions, IQueryExecutor q, ITenantContext tenant, IEventCollector events)
    : IRequestHandler<ReplaceWorkflowBindingsCommand, int>
{
    public async Task<Result<int>> Handle(ReplaceWorkflowBindingsCommand c, CancellationToken ct)
    {
        static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        var wanted = c.Bindings.Select(b => (b.WorkflowId, Type: Blank(b.AssetType), Region: Blank(b.Region))).ToList();

        var errors = new Dictionary<string, string[]>();
        var seen = new HashSet<(string?, string?)>();
        var knownWorkflows = (await q.ToListAsync(definitions.Query().Select(d => d.Id), ct)).ToHashSet();
        for (var i = 0; i < wanted.Count; i++)
        {
            if (!knownWorkflows.Contains(wanted[i].WorkflowId)) errors[$"Bindings[{i}].WorkflowId"] = ["Workflow not found."];
            if (!seen.Add((wanted[i].Type, wanted[i].Region?.ToLowerInvariant()))) errors[$"Bindings[{i}]"] = ["Another binding already uses this type and region."];
        }
        if (errors.Count > 0) return Error.Validation(errors);

        var remaining = await q.ToListAsync(bindings.Query(), ct);
        foreach (var w in wanted)
        {
            var match = remaining.FirstOrDefault(b => b.AssetType == w.Type && string.Equals(b.Region, w.Region, StringComparison.OrdinalIgnoreCase));
            if (match is null) bindings.Add(WorkflowBinding.Create(tenant.TenantId, w.WorkflowId, w.Type, w.Region));
            else { match.Retarget(w.WorkflowId); remaining.Remove(match); }
        }
        bindings.RemoveRange(remaining);
        events.Raise(Events.Changed("workflow.bindings.updated", "workflow_binding", Guid.Empty));
        return wanted.Count;
    }
}
