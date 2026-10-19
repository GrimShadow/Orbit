using Dam.Application.Abstractions;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Content;
using Dam.Domain.Identity;
using Dam.Domain.Workflow;
using Dam.Shared.Content;

namespace Dam.Application.Workflow;

/// <summary>
/// Starts, steps and cancels workflow instances (ADR-0030). It adds entities to the unit of work and mutates tracked ones;
/// the calling handler's transaction saves them together with the asset change and the outbox rows.
/// </summary>
public sealed class WorkflowRuntime(
    IRepository<WorkflowDefinition> definitions, IRepository<WorkflowBinding> bindings, IRepository<WorkflowInstance> instances,
    IRepository<WorkflowTask> tasks, IRepository<Folder> folders, IWorkflowStore store, ITenantContext tenant, IQueryExecutor q, IClock clock)
{
    // ---- which workflow applies --------------------------------------------------------------------------------

    /// <summary>
    /// The workflow an asset goes through when submitted, or null when none applies. First match wins: the nearest folder (the
    /// asset's own, then each ancestor) that names a default workflow; then a binding by type and region (type and region, then
    /// type only, then region only); otherwise none, and the plain approve/reject transitions apply.
    /// </summary>
    public async Task<WorkflowDefinition?> ResolveAsync(Asset asset, string? folderPath, CancellationToken ct)
    {
        if (folderPath is not null)
        {
            var labels = folderPath.Split('.');
            var prefixes = Enumerable.Range(1, labels.Length).Select(n => string.Join('.', labels.Take(n))).ToList();
            var withDefault = await q.ToListAsync(folders.Query().Where(f => prefixes.Contains(f.Path) && f.DefaultWorkflowId != null), ct);
            foreach (var folder in withDefault.OrderByDescending(f => f.Path.Length))
                if (await q.FirstOrDefaultAsync(definitions.Query().Where(d => d.Id == folder.DefaultWorkflowId), ct) is { } d) return d;
        }

        var all = await q.ToListAsync(bindings.Query(), ct);
        if (all.Count == 0) return null;
        var regions = asset.Attributes.GetValueOrDefault(Dimensions.Region) ?? [];
        bool RegionMatches(WorkflowBinding b) => b.Region is not null && regions.Contains(b.Region, StringComparer.OrdinalIgnoreCase);
        bool TypeMatches(WorkflowBinding b) => b.AssetType is not null && b.AssetType == asset.Type;

        var chosen =
            Pick(all.Where(b => TypeMatches(b) && RegionMatches(b)))
            ?? Pick(all.Where(b => TypeMatches(b) && b.Region is null))
            ?? Pick(all.Where(b => RegionMatches(b) && b.AssetType is null));
        return chosen is null ? null : await q.FirstOrDefaultAsync(definitions.Query().Where(d => d.Id == chosen.WorkflowId), ct);

        // An asset can carry several regions; the alphabetically first matching binding wins, so the choice is deterministic.
        static WorkflowBinding? Pick(IEnumerable<WorkflowBinding> candidates) =>
            candidates.OrderBy(b => b.Region, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Id).FirstOrDefault();
    }

    // ---- start ---------------------------------------------------------------------------------------------------

    /// <summary>Starts an instance for <paramref name="asset"/> if a workflow applies; returns that workflow's name, or null
    /// when none does. Fails if the applicable definition cannot be read (it never silently skips an approval).</summary>
    public async Task<Result<string?>> StartIfBoundAsync(Asset asset, string? folderPath, Guid? startedBy, CancellationToken ct)
    {
        var definition = await ResolveAsync(asset, folderPath, ct);
        if (definition is null) return Result<string?>.Success(null);

        var parsed = WorkflowDefinitionParser.Parse(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(definition.Json));
        if (parsed.Definition is not { } def)
            return Error.Conflict($"The workflow '{definition.Name}' is not valid and cannot be started. Ask an administrator to fix it.");

        var now = clock.UtcNow;
        var instance = WorkflowInstance.Start(tenant.TenantId, asset.Id, definition, def.DistinctApprovers, startedBy, now);
        instances.Add(instance);
        ActivateStep(instance, def, def.Find(def.Start)!, now);
        return Result<string?>.Success(definition.Name);
    }

    /// <summary>Creates the tasks of one activation of <paramref name="step"/>.</summary>
    public IReadOnlyList<WorkflowTask> ActivateStep(WorkflowInstance instance, WorkflowDef def, StepDef step, DateTimeOffset now)
    {
        var round = instance.BeginRound();
        var created = WorkflowEngine.Slots(step).Select(slot => WorkflowTask.Create(instance, step, slot, round, now)).ToList();
        tasks.AddRange(created);
        return created;
    }

    // ---- running instance ----------------------------------------------------------------------------------------

    public Task<WorkflowInstance?> RunningAsync(Guid assetId, CancellationToken ct) =>
        q.FirstOrDefaultAsync(instances.Query().Where(i => i.AssetId == assetId && i.Status == WorkflowStatuses.Running), ct);

    public Task<bool> HasRunningAsync(Guid assetId, CancellationToken ct) =>
        q.AnyAsync(instances.Query().Where(i => i.AssetId == assetId && i.Status == WorkflowStatuses.Running), ct);

    /// <summary>Cancels the asset's running instance (if any) and its open tasks: the asset was withdrawn or deleted.</summary>
    public async Task CancelRunningAsync(Guid assetId, string assetStatus, string reason, CancellationToken ct)
    {
        var id = await q.FirstOrDefaultAsync(instances.Query().Where(i => i.AssetId == assetId && i.Status == WorkflowStatuses.Running).Select(i => (Guid?)i.Id), ct);
        if (id is null) return;
        await store.LockInstanceAsync(id.Value, ct);
        // Read after the lock: a decision that committed while we waited may already have ended it.
        var instance = await q.FirstOrDefaultAsync(instances.Query().Where(i => i.Id == id.Value), ct);
        if (instance is not { IsRunning: true }) return;
        foreach (var task in await q.ToListAsync(tasks.Query().Where(t => t.InstanceId == instance.Id && t.Status == TaskStatuses.Pending), ct)) task.Cancel();
        instance.Finish(WorkflowStatuses.Cancelled, assetStatus, reason, clock.UtcNow);
    }
}

/// <summary>The publish guard (<see cref="IWorkflowGate"/>), backed by the asset's latest workflow instance.</summary>
public sealed class WorkflowGate(IRepository<WorkflowInstance> instances, IQueryExecutor q) : IWorkflowGate
{
    public async Task<bool> IsApprovedAsync(Guid assetId, CancellationToken ct)
    {
        var latest = await q.FirstOrDefaultAsync(
            instances.Query().Where(i => i.AssetId == assetId).OrderByDescending(i => i.StartedAt).ThenByDescending(i => i.Id).Select(i => i.Status), ct);
        return WorkflowEngine.AllowsPublish(latest);
    }
}

/// <summary>Who may see and decide which tasks. The pure checks take an already-loaded <see cref="AccessProfile"/>.</summary>
public sealed class WorkflowTaskAccess(IRepository<Asset> assets, IRepository<Folder> folders, IRepository<WorkflowTask> tasks, IQueryExecutor q)
{
    public static IReadOnlyCollection<string> RoleNames(AccessProfile profile) => PolicyEngine.RolesInEffect(profile).Select(r => r.Name).ToList();

    /// <summary>The assets (and their folder paths) behind a set of tasks.</summary>
    public async Task<Dictionary<Guid, (Asset Asset, string? Path)>> LoadAssetsAsync(IEnumerable<Guid> assetIds, CancellationToken ct)
    {
        var ids = assetIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        var found = await q.ToListAsync(assets.Query().Where(a => ids.Contains(a.Id)), ct);
        var folderIds = found.Where(a => a.FolderId != null).Select(a => a.FolderId!.Value).Distinct().ToList();
        var paths = folderIds.Count == 0 ? [] : (await q.ToListAsync(folders.Query().Where(f => folderIds.Contains(f.Id)).Select(f => new { f.Id, f.Path }), ct)).ToDictionary(f => f.Id, f => f.Path);
        return found.ToDictionary(a => a.Id, a => (a, a.FolderId is { } f && paths.TryGetValue(f, out var p) ? p : (string?)null));
    }

    /// <summary>
    /// May this person decide this task: they are an assignee (by user id or role, or an escalation target once the SLA passed),
    /// they hold <c>workflow.tasks.decide</c>, and <c>assets.approve</c> reaches this asset. The last is the "regional review"
    /// part: approving is only ever granted through an access rule that names the folders, types or regions a reviewer decides in.
    /// </summary>
    public static bool CanDecide(AccessProfile profile, WorkflowTask task, Asset asset, string? folderPath)
    {
        if (!task.IsPending || !task.IsAssignedTo(profile.UserId, RoleNames(profile))) return false;
        var resource = asset.ToResource(folderPath);
        return PolicyEngine.Can(profile, Permissions.WorkflowTasksDecide, resource) && PolicyEngine.Can(profile, Permissions.AssetsApprove, resource);
    }

    /// <summary>What this person has already approved in each of the given instances, for the segregation-of-duties rule.</summary>
    public async Task<Dictionary<Guid, List<(string StepId, int Round)>>> ApprovalsByAsync(Guid userId, IEnumerable<Guid> instanceIds, CancellationToken ct)
    {
        var ids = instanceIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        var rows = await q.ToListAsync(tasks.Query().Where(t => ids.Contains(t.InstanceId) && t.DecidedBy == userId && t.Status == TaskStatuses.Approved)
            .Select(t => new { t.InstanceId, t.StepId, t.Round }), ct);
        return rows.GroupBy(r => r.InstanceId).ToDictionary(g => g.Key, g => g.Select(r => (r.StepId, r.Round)).ToList());
    }
}

/// <summary>Turns tasks into <see cref="WorkflowTaskDto"/>s, resolving the asset, workflow and people names they show.</summary>
public sealed class WorkflowTaskMapper(IRepository<Asset> assets, IRepository<WorkflowInstance> instances, IRepository<User> users, IQueryExecutor q)
{
    public async Task<List<WorkflowTaskDto>> ToDtosAsync(IReadOnlyList<WorkflowTask> list, CancellationToken ct)
    {
        if (list.Count == 0) return [];
        var assetIds = list.Select(t => t.AssetId).Distinct().ToList();
        var instanceIds = list.Select(t => t.InstanceId).Distinct().ToList();
        var userIds = list.Where(t => t.DecidedBy != null).Select(t => t.DecidedBy!.Value).Distinct().ToList();
        var assetRows = (await q.ToListAsync(assets.Query().Where(a => assetIds.Contains(a.Id)).Select(a => new { a.Id, a.Title, a.Type }), ct)).ToDictionary(a => a.Id);
        var instanceNames = (await q.ToListAsync(instances.Query().Where(i => instanceIds.Contains(i.Id)).Select(i => new { i.Id, i.DefinitionName }), ct)).ToDictionary(i => i.Id, i => i.DefinitionName);
        var names = userIds.Count == 0 ? [] : (await q.ToListAsync(users.Query().Where(u => userIds.Contains(u.Id)).Select(u => new { u.Id, u.DisplayName, u.Email }), ct))
            .ToDictionary(u => u.Id, u => string.IsNullOrWhiteSpace(u.DisplayName) ? u.Email : u.DisplayName);
        return list.Select(t => ToDto(t, assetRows.GetValueOrDefault(t.AssetId)?.Title ?? "", assetRows.GetValueOrDefault(t.AssetId)?.Type ?? "",
            instanceNames.GetValueOrDefault(t.InstanceId) ?? "", t.DecidedBy is { } d ? names.GetValueOrDefault(d) : null)).ToList();
    }

    public static WorkflowTaskDto ToDto(WorkflowTask t, string assetTitle, string assetType, string workflowName, string? decidedByName) =>
        new(t.Id, t.AssetId, assetTitle, assetType, workflowName, t.StepId, t.StepName, t.Status,
            t.AssigneeRoles.Concat(t.EscalatedRoles).ToList(), t.AssigneeUserIds.Concat(t.EscalatedUserIds).ToList(),
            t.DueAt, t.SlaBreachedAt is not null, t.SlaBreachedAt, t.CreatedAt,
            t.Status switch { TaskStatuses.Approved => "approve", TaskStatuses.Rejected => "reject", _ => null },
            TaskStatuses.IsDecision(t.Status) ? t.DecidedBy : null, TaskStatuses.IsDecision(t.Status) ? t.DecidedAt : null, t.Comment,
            t.IsEscalated, TaskStatuses.IsDecision(t.Status) ? decidedByName : null, t.InstanceId);
}
