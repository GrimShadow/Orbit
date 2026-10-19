using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Application.Assets;
using Dam.Application.Content;
using Dam.Application.Identity;
using Dam.Application.Messaging;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Domain.Content;
using Dam.Domain.Identity;
using Dam.Domain.Workflow;
using Dam.Shared.Content;
using Dam.Shared.Identity;
using FluentValidation;

namespace Dam.Application.Workflow;

// ----------------------------------------------------------------------------------------------------- inbox

/// <summary>
/// The task inbox. <c>assignee=me</c> (default) lists the tasks the caller could decide (assignee by user or role, holding
/// <c>workflow.tasks.decide</c>, with <c>assets.approve</c> reaching the asset) and the ones they decided themselves;
/// <c>all</c> lists every task and needs <c>workflow.manage</c>. Newest first.
/// </summary>
public sealed record ListTasksQuery(string? Assignee, string? Status, string? Cursor, int? Limit) : IQuery<Page<WorkflowTaskDto>>;

public sealed class ListTasksValidator : AbstractValidator<ListTasksQuery>
{
    public ListTasksValidator()
    {
        RuleFor(x => x.Assignee).Must(a => a is null or "me" or "all").WithMessage("Use 'me' or 'all'.");
        RuleFor(x => x.Status).Must(s => s is null or "pending" or "decided" or "all").WithMessage("Use 'pending', 'decided' or 'all'.");
    }
}

public sealed class ListTasksHandler(
    IRepository<WorkflowTask> tasks, IRepository<WorkflowInstance> instances, IAuthorizationService authz, WorkflowTaskAccess access,
    WorkflowTaskMapper mapper, IQueryExecutor q) : IRequestHandler<ListTasksQuery, Page<WorkflowTaskDto>>
{
    private const int MaxRowsScanned = 5000;

    public async Task<Result<Page<WorkflowTaskDto>>> Handle(ListTasksQuery r, CancellationToken ct)
    {
        var limit = Paging.Clamp(r.Limit);
        var wantAll = r.Assignee == "all";
        if (wantAll && !await authz.CanAsync(Permissions.WorkflowManage, null, ct)) return Error.Forbidden("Listing every task needs workflow.manage.");
        var status = r.Status ?? "pending";
        var profile = await authz.GetProfileAsync(ct);
        var me = profile.UserId;
        var roles = WorkflowTaskAccess.RoleNames(profile).ToList();

        IQueryable<WorkflowTask> query = tasks.Query();
        if (status == "pending") query = query.Where(t => t.Status == TaskStatuses.Pending);
        else if (status == "decided") query = query.Where(t => t.Status == TaskStatuses.Approved || t.Status == TaskStatuses.Rejected);
        else if (!wantAll) query = query.Where(t => t.Status != TaskStatuses.Cancelled);

        if (!wantAll)
        {
            // What the database can narrow: assigned to me (by id or role) while open, or decided by me. Whether my access rules
            // actually reach the asset is checked below, in memory, with the same policy engine as every other check.
            query = query.Where(t =>
                (t.Status == TaskStatuses.Pending && (t.AssigneeUserIds.Contains(me) || t.EscalatedUserIds.Contains(me)
                    || t.AssigneeRoles.Any(x => roles.Contains(x)) || t.EscalatedRoles.Any(x => roles.Contains(x))))
                || t.DecidedBy == me);
        }

        var cursor = Paging.ParseCursor(r.Cursor);
        var page = new List<WorkflowTask>();
        var scanned = 0;
        var more = false;
        while (page.Count <= limit && scanned < MaxRowsScanned)
        {
            var chunk = cursor is { } after ? query.Where(t => t.Id.CompareTo(after) < 0) : query;
            var rows = await q.ToListAsync(chunk.OrderByDescending(t => t.Id).Take(limit * 4), ct);
            if (rows.Count == 0) break;

            var visible = wantAll ? rows.Select(t => t.Id).ToHashSet() : await FilterAsync(rows, profile, me, ct);
            foreach (var t in rows)
            {
                cursor = t.Id;
                scanned++;
                if (!visible.Contains(t.Id)) continue;
                page.Add(t);
                if (page.Count > limit) { more = true; break; }
            }
            if (more || rows.Count < limit * 4) break;
        }
        if (page.Count > limit) page.RemoveAt(limit);
        return new Page<WorkflowTaskDto>(await mapper.ToDtosAsync(page, ct), more ? page[^1].Id.ToString() : null);
    }

    private async Task<HashSet<Guid>> FilterAsync(List<WorkflowTask> rows, AccessProfile profile, Guid me, CancellationToken ct)
    {
        var visible = rows.Where(t => t.DecidedBy == me && TaskStatuses.IsDecision(t.Status)).Select(t => t.Id).ToHashSet();
        var pending = rows.Where(t => t.IsPending).ToList();
        if (pending.Count == 0) return visible;

        var assets = await access.LoadAssetsAsync(pending.Select(t => t.AssetId), ct);
        var instanceIds = pending.Select(t => t.InstanceId).Distinct().ToList();
        var distinct = (await q.ToListAsync(instances.Query().Where(i => instanceIds.Contains(i.Id) && i.DistinctApprovers).Select(i => i.Id), ct)).ToHashSet();
        var approvals = await access.ApprovalsByAsync(me, distinct, ct);
        foreach (var t in pending)
        {
            if (!assets.TryGetValue(t.AssetId, out var a) || !WorkflowTaskAccess.CanDecide(profile, t, a.Asset, a.Path)) continue;
            if (distinct.Contains(t.InstanceId) && WorkflowEngine.ViolatesDistinctApprovers(true, t.StepId, t.Round, approvals.GetValueOrDefault(t.InstanceId) ?? [])) continue;
            visible.Add(t.Id);
        }
        return visible;
    }
}

// --------------------------------------------------------------------------------------------------- summary

public sealed record GetTaskSummaryQuery : IQuery<TaskSummaryDto>;

public sealed class GetTaskSummaryHandler(
    IRepository<WorkflowTask> tasks, IRepository<WorkflowInstance> instances, IAuthorizationService authz, WorkflowTaskAccess access,
    IClock clock, IQueryExecutor q) : IRequestHandler<GetTaskSummaryQuery, TaskSummaryDto>
{
    public async Task<Result<TaskSummaryDto>> Handle(GetTaskSummaryQuery r, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var profile = await authz.GetProfileAsync(ct);
        var me = profile.UserId;
        var roles = WorkflowTaskAccess.RoleNames(profile).ToList();

        var mine = await q.ToListAsync(tasks.Query().Where(t => t.Status == TaskStatuses.Pending
            && (t.AssigneeUserIds.Contains(me) || t.EscalatedUserIds.Contains(me)
                || t.AssigneeRoles.Any(x => roles.Contains(x)) || t.EscalatedRoles.Any(x => roles.Contains(x)))), ct);
        var assets = await access.LoadAssetsAsync(mine.Select(t => t.AssetId), ct);
        var distinct = (await q.ToListAsync(instances.Query().Where(i => i.DistinctApprovers).Select(i => i.Id), ct)).ToHashSet();
        var approvals = await access.ApprovalsByAsync(me, mine.Select(t => t.InstanceId).Where(distinct.Contains), ct);
        var decidable = mine.Where(t => assets.TryGetValue(t.AssetId, out var a) && WorkflowTaskAccess.CanDecide(profile, t, a.Asset, a.Path)
            && !(distinct.Contains(t.InstanceId) && WorkflowEngine.ViolatesDistinctApprovers(true, t.StepId, t.Round, approvals.GetValueOrDefault(t.InstanceId) ?? []))).ToList();

        int? pendingTotal = null, overdueTotal = null, completed = null, onTime = null;
        double? attainment = null;
        if (await authz.CanAsync(Permissions.WorkflowManage, null, ct) || await authz.CanAsync(Permissions.AnalyticsRead, null, ct))
        {
            var since = now.AddDays(-30);
            pendingTotal = await q.CountAsync(tasks.Query().Where(t => t.Status == TaskStatuses.Pending), ct);
            overdueTotal = await q.CountAsync(tasks.Query().Where(t => t.Status == TaskStatuses.Pending && (t.SlaBreachedAt != null || (t.DueAt != null && t.DueAt <= now))), ct);
            var done = tasks.Query().Where(t => (t.Status == TaskStatuses.Approved || t.Status == TaskStatuses.Rejected) && t.DecidedAt >= since);
            completed = await q.CountAsync(done, ct);
            onTime = await q.CountAsync(done.Where(t => t.DueAt == null || t.DecidedAt <= t.DueAt), ct);
            attainment = completed > 0 ? Math.Round(100.0 * onTime.Value / completed.Value, 1) : null;
        }
        return new TaskSummaryDto(decidable.Count, decidable.Count(t => t.IsOverdue(now)), pendingTotal, overdueTotal, completed, onTime, attainment);
    }
}

// ---------------------------------------------------------------------------------------------------- decision

/// <summary>
/// Approves or rejects one task. Decisions on an instance are serialised by a row lock, so two people deciding the same
/// <c>any</c> task get one winner (the other a 409), and the last two parallel approvals of an <c>all</c> step cannot each
/// believe the other is still pending. The step then resolves by the rules in <see cref="WorkflowEngine"/>: wait, move to the
/// next step (new tasks), finish approved, or finish rejected.
/// </summary>
public sealed record DecideTaskCommand(Guid TaskId, string Decision, string? Comment) : ICommand<WorkflowTaskDto>;

public sealed class DecideTaskValidator : AbstractValidator<DecideTaskCommand>
{
    public const int MaxCommentLength = 1000;

    public DecideTaskValidator()
    {
        RuleFor(x => x.Decision).Must(d => d is "approve" or "reject").WithMessage("Use 'approve' or 'reject'.");
        RuleFor(x => x.Comment).NotEmpty().When(x => x.Decision == "reject").WithMessage("A comment is required to reject.");
        RuleFor(x => x.Comment).MaximumLength(MaxCommentLength);
    }
}

public sealed class DecideTaskHandler(
    IRepository<WorkflowTask> tasks, IRepository<WorkflowInstance> instances, IRepository<Asset> assets, IRepository<Folder> folders,
    IWorkflowStore store, WorkflowRuntime runtime, WorkflowTaskAccess access, WorkflowTaskMapper mapper, AssetMetadataBuilder builder,
    IAuthorizationService authz, IQueryExecutor q, IClock clock) : IRequestHandler<DecideTaskCommand, WorkflowTaskDto>
{
    public async Task<Result<WorkflowTaskDto>> Handle(DecideTaskCommand c, CancellationToken ct)
    {
        // Find the instance first (without tracking the task), take its lock, and only then read the state to decide on.
        var instanceId = await q.FirstOrDefaultAsync(tasks.Query().Where(t => t.Id == c.TaskId).Select(t => (Guid?)t.InstanceId), ct);
        if (instanceId is null) return Error.NotFound("Task not found.");
        await store.LockInstanceAsync(instanceId.Value, ct);

        var task = await q.FirstOrDefaultAsync(tasks.Query().Where(t => t.Id == c.TaskId), ct);
        var instance = await q.FirstOrDefaultAsync(instances.Query().Where(i => i.Id == instanceId.Value), ct);
        if (task is null || instance is null) return Error.NotFound("Task not found.");

        var profile = await authz.GetProfileAsync(ct);
        var (asset, path) = await AssetAccess.LoadAsync(assets, folders, q, task.AssetId, ct);
        if (asset is null) return Error.NotFound("Task not found.");

        // Who may decide comes before what state it is in, so a stranger learns nothing about the task.
        if (!task.IsAssignedTo(profile.UserId, WorkflowTaskAccess.RoleNames(profile))) return Error.Forbidden("This task is not assigned to you.");
        var resource = asset.ToResource(path);
        if (!PolicyEngine.Can(profile, Permissions.WorkflowTasksDecide, resource)) return Error.Forbidden("Deciding workflow tasks needs the workflow.tasks.decide permission.");
        if (!PolicyEngine.Can(profile, Permissions.AssetsApprove, resource)) return Error.Forbidden("You are not assigned to approve assets here.");

        if (!task.IsPending) return Error.Conflict(task.Status == TaskStatuses.Cancelled ? "This task is no longer open." : "This task has already been decided.");
        if (!instance.IsRunning || asset.Status != AssetStatuses.InReview) return Error.Conflict("This workflow is no longer running.");

        var approve = c.Decision == "approve";
        if (approve && instance.DistinctApprovers)
        {
            var prior = (await access.ApprovalsByAsync(profile.UserId, [instance.Id], ct)).GetValueOrDefault(instance.Id) ?? [];
            if (WorkflowEngine.ViolatesDistinctApprovers(true, task.StepId, task.Round, prior))
                return Error.Forbidden("You already approved an earlier part of this workflow. A different person must approve this step.");
        }

        var parsed = WorkflowDefinitionParser.Parse(JsonSerializer.Deserialize<JsonElement>(instance.DefinitionJson));
        if (parsed.Definition is not { } def || def.Find(task.StepId) is not { } step)
            return Error.Conflict("This workflow's definition can no longer be read.");

        var now = clock.UtcNow;
        task.Decide(profile.UserId, approve, c.Comment, now);

        var roundTasks = await q.ToListAsync(tasks.Query().Where(t => t.InstanceId == instance.Id && t.StepId == task.StepId && t.Round == task.Round), ct);
        var advance = WorkflowEngine.Resolve(step, roundTasks.Select(t => t.Status).ToList());
        if (advance.Kind != AdvanceKind.Wait)
            foreach (var other in roundTasks) other.Cancel();   // anything still open in this activation is moot now

        switch (advance.Kind)
        {
            case AdvanceKind.Goto:
                runtime.ActivateStep(instance, def, def.Find(advance.StepId!)!, now);
                break;
            case AdvanceKind.Reject:
                instance.Finish(WorkflowStatuses.Rejected, AssetStatuses.Draft, c.Comment ?? "Rejected.", now);
                asset.ChangeStatus(AssetStatuses.Draft, Limit(c.Comment ?? $"Rejected by workflow '{instance.DefinitionName}' (step '{step.Name}')."), now);
                break;
            case AdvanceKind.Complete:
                await CompleteAsync(instance, def, step, asset, now, ct);
                break;
        }

        return (await mapper.ToDtosAsync([task], ct))[0];
    }

    private async Task CompleteAsync(WorkflowInstance instance, WorkflowDef def, StepDef step, Asset asset, DateTimeOffset now, CancellationToken ct)
    {
        asset.ChangeStatus(AssetStatuses.Approved, Limit($"Approved by workflow '{instance.DefinitionName}' (step '{step.Name}')."), now);
        instance.Finish(WorkflowStatuses.Approved, AssetStatuses.Approved, $"Approved by workflow '{instance.DefinitionName}'.", now);
        if (!def.AutoPublish) return;

        // Automatic publishing is still subject to the guards a manual publish has. If one fails the asset simply stays
        // approved, and the instance says why, so someone can fix it and publish by hand.
        var built = await builder.BuildAsync(asset.Type, null, JsonSerializer.SerializeToElement(asset.Metadata), requireFields: true, ct);
        if (built.IsFailure)
        {
            instance.Note("Approved, but not published automatically: required metadata is incomplete.", AssetStatuses.Approved);
            return;
        }
        asset.ChangeStatus(AssetStatuses.Published, Limit($"Published automatically by workflow '{instance.DefinitionName}'."), now);
        instance.Finish(WorkflowStatuses.Approved, AssetStatuses.Published, $"Approved and published by workflow '{instance.DefinitionName}'.", now);
    }

    private static string Limit(string reason) => reason.Length <= 500 ? reason : reason[..497] + "...";
}

// ----------------------------------------------------------------------------------------------- asset view

/// <summary>The asset's latest workflow (running or finished) as a timeline of its steps and their tasks.</summary>
public sealed record GetAssetWorkflowQuery(Guid AssetId) : IQuery<AssetWorkflowDto>;

public sealed class GetAssetWorkflowHandler(
    IRepository<Asset> assets, IRepository<Folder> folders, IRepository<WorkflowInstance> instances, IRepository<WorkflowTask> tasks,
    IRepository<User> users, WorkflowTaskMapper mapper, IAuthorizationService authz, IQueryExecutor q) : IRequestHandler<GetAssetWorkflowQuery, AssetWorkflowDto>
{
    public async Task<Result<AssetWorkflowDto>> Handle(GetAssetWorkflowQuery r, CancellationToken ct)
    {
        var (asset, path) = await AssetAccess.LoadAsync(assets, folders, q, r.AssetId, ct);
        if (asset is null || !await authz.CanAsync(Permissions.AssetsRead, asset.ToResource(path), ct)) return Error.NotFound("Asset not found.");

        var instance = await q.FirstOrDefaultAsync(instances.Query().Where(i => i.AssetId == r.AssetId).OrderByDescending(i => i.StartedAt).ThenByDescending(i => i.Id), ct);
        if (instance is null) return new AssetWorkflowDto(null);

        var parsed = WorkflowDefinitionParser.Parse(JsonSerializer.Deserialize<JsonElement>(instance.DefinitionJson));
        var def = parsed.Definition;
        var all = await q.ToListAsync(tasks.Query().Where(t => t.InstanceId == instance.Id).OrderBy(t => t.Round).ThenBy(t => t.Id), ct);
        var dtos = (await mapper.ToDtosAsync(all, ct)).ToDictionary(d => d.Id);

        var steps = new List<WorkflowStepViewDto>();
        foreach (var step in def?.FlowOrder() ?? [])
        {
            var mine = all.Where(t => t.StepId == step.Id).ToList();
            steps.Add(new WorkflowStepViewDto(step.Id, step.Name, StateOf(instance, mine), step.Mode, step.SlaHours, mine.Select(t => dtos[t.Id]).ToList()));
        }

        string? startedByName = null;
        if (instance.StartedBy is { } sb)
        {
            var u = await q.FirstOrDefaultAsync(users.Query().Where(x => x.Id == sb).Select(x => new { x.DisplayName, x.Email }), ct);
            startedByName = u is null ? null : string.IsNullOrWhiteSpace(u.DisplayName) ? u.Email : u.DisplayName;
        }
        return new AssetWorkflowDto(new WorkflowInstanceViewDto(instance.Id, instance.DefinitionName, instance.Status, instance.StartedBy, startedByName,
            instance.StartedAt, instance.CompletedAt, instance.OutcomeReason, instance.OutcomeStatus, def?.AutoPublish ?? false, steps));
    }

    /// <summary>active: has open tasks. done: was decided and nothing is open. waiting: a running instance that has not got there.
    /// skipped: never reached (the instance ended first) or its tasks were all cancelled.</summary>
    internal static string StateOf(WorkflowInstance instance, List<WorkflowTask> stepTasks)
    {
        if (stepTasks.Any(t => t.IsPending)) return "active";
        if (stepTasks.Any(t => TaskStatuses.IsDecision(t.Status))) return "done";
        return stepTasks.Count == 0 && instance.IsRunning ? "waiting" : "skipped";
    }
}
