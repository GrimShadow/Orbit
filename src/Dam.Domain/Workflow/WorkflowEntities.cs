using Dam.Domain.Common;

namespace Dam.Domain.Workflow;

public sealed record WorkflowTaskCreated(
    Guid TaskId, Guid InstanceId, Guid AssetId, string StepId, string StepName,
    IReadOnlyList<string> AssigneeRoles, IReadOnlyList<Guid> AssigneeUserIds, DateTimeOffset? DueAt) : DomainEvent, IEntityEvent
{
    public override string Name => "workflow.task.created";
    public string EntityType => "workflow_task";
    public Guid EntityId => TaskId;
}

public sealed record WorkflowTaskCompleted(
    Guid TaskId, Guid InstanceId, Guid AssetId, string StepId, string Decision, Guid? DecidedBy, string? Comment, bool OnTime) : DomainEvent, IEntityEvent
{
    public override string Name => "workflow.task.completed";
    public string EntityType => "workflow_task";
    public Guid EntityId => TaskId;
}

public sealed record WorkflowSlaBreached(
    Guid TaskId, Guid InstanceId, Guid AssetId, string StepId, DateTimeOffset DueAt,
    IReadOnlyList<string> EscalatedRoles, IReadOnlyList<Guid> EscalatedUserIds) : DomainEvent, IEntityEvent
{
    public override string Name => "workflow.sla.breached";
    public string EntityType => "workflow_task";
    public Guid EntityId => TaskId;
}

/// <summary>A reusable, JSON-defined approval process (spec D9, ADR-0030). Editing it never changes a running instance: each
/// instance keeps the snapshot it started with.</summary>
public sealed class WorkflowDefinition : TenantEntity
{
    private WorkflowDefinition() { }

    public string Name { get; private set; } = "";
    public string Description { get; private set; } = "";
    /// <summary>The definition document as the author wrote it (including any layout data the designer keeps), validated by <see cref="WorkflowDefinitionParser"/>.</summary>
    public string Json { get; private set; } = "{}";
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static WorkflowDefinition Create(Guid tenantId, string name, string? description, string json, Guid? createdBy, DateTimeOffset now) =>
        new() { TenantId = tenantId, Name = name.Trim(), Description = description?.Trim() ?? "", Json = json, CreatedBy = createdBy, CreatedAt = now, UpdatedAt = now };

    public void Update(string name, string? description, string json, DateTimeOffset now)
    {
        Name = name.Trim();
        Description = description?.Trim() ?? "";
        Json = json;
        UpdatedAt = now;
    }
}

/// <summary>Picks a workflow for assets by type and/or region when no folder on the asset's path names a default.</summary>
public sealed class WorkflowBinding : TenantEntity
{
    private WorkflowBinding() { }

    public Guid WorkflowId { get; private set; }
    public string? AssetType { get; private set; }
    public string? Region { get; private set; }

    public static WorkflowBinding Create(Guid tenantId, Guid workflowId, string? assetType, string? region) =>
        new() { TenantId = tenantId, WorkflowId = workflowId, AssetType = assetType, Region = region };

    public void Retarget(Guid workflowId) => WorkflowId = workflowId;
}

/// <summary>One submission of one asset through a workflow. Holds a frozen copy of the definition it started with.</summary>
public sealed class WorkflowInstance : TenantEntity
{
    private WorkflowInstance() { }

    public Guid AssetId { get; private set; }
    /// <summary>The definition it came from; null once that definition has been deleted (the snapshot below is all that is needed).</summary>
    public Guid? DefinitionId { get; private set; }
    public string DefinitionName { get; private set; } = "";
    public string DefinitionJson { get; private set; } = "{}";
    public bool DistinctApprovers { get; private set; }
    public string Status { get; private set; } = WorkflowStatuses.Running;
    public Guid? StartedBy { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    /// <summary>Why it ended: the reviewer's comment on a rejection, or a note such as why an automatic publish did not happen.</summary>
    public string? OutcomeReason { get; private set; }
    /// <summary>The asset's status when the workflow ended.</summary>
    public string? OutcomeStatus { get; private set; }
    /// <summary>How many times a step has been activated; each activation's tasks carry its number.</summary>
    public int Rounds { get; private set; }

    public bool IsRunning => Status == WorkflowStatuses.Running;

    public static WorkflowInstance Start(Guid tenantId, Guid assetId, WorkflowDefinition definition, bool distinctApprovers, Guid? startedBy, DateTimeOffset now) =>
        new()
        {
            TenantId = tenantId,
            AssetId = assetId,
            DefinitionId = definition.Id,
            DefinitionName = definition.Name,
            DefinitionJson = definition.Json,
            DistinctApprovers = distinctApprovers,
            StartedBy = startedBy,
            StartedAt = now,
        };

    public int BeginRound() => ++Rounds;

    public void Finish(string status, string? assetStatus, string? reason, DateTimeOffset now)
    {
        Status = status;
        OutcomeStatus = assetStatus;
        OutcomeReason = reason is { Length: > 500 } ? reason[..500] : reason;
        CompletedAt = now;
    }

    /// <summary>Records a note on a finished instance (for example that an automatic publish was skipped).</summary>
    public void Note(string reason, string? assetStatus)
    {
        OutcomeReason = reason.Length > 500 ? reason[..500] : reason;
        if (assetStatus is not null) OutcomeStatus = assetStatus;
    }
}

/// <summary>
/// One thing a person must decide. An <c>any</c> step creates one task for every assignee together; an <c>all</c> step one per
/// assignee. When the SLA passes, the step's escalation targets are added as further eligible deciders.
/// </summary>
public sealed class WorkflowTask : AggregateRoot
{
    private WorkflowTask() { }

    public Guid InstanceId { get; private set; }
    public Guid AssetId { get; private set; }
    public string StepId { get; private set; } = "";
    public string StepName { get; private set; } = "";
    /// <summary>Which activation of the step this task belongs to (it grows when a rejection sends the instance back).</summary>
    public int Round { get; private set; }
    public List<string> AssigneeRoles { get; private set; } = [];
    public List<Guid> AssigneeUserIds { get; private set; } = [];
    public List<string> EscalatedRoles { get; private set; } = [];
    public List<Guid> EscalatedUserIds { get; private set; } = [];
    public string Status { get; private set; } = TaskStatuses.Pending;
    public DateTimeOffset? DueAt { get; private set; }
    public DateTimeOffset? SlaBreachedAt { get; private set; }
    public Guid? DecidedBy { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }
    public string? Comment { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsPending => Status == TaskStatuses.Pending;
    public bool IsEscalated => EscalatedRoles.Count > 0 || EscalatedUserIds.Count > 0;

    public static WorkflowTask Create(WorkflowInstance instance, StepDef step, TaskSlot slot, int round, DateTimeOffset now)
    {
        var t = new WorkflowTask
        {
            TenantId = instance.TenantId,
            InstanceId = instance.Id,
            AssetId = instance.AssetId,
            StepId = step.Id,
            StepName = step.Name,
            Round = round,
            AssigneeRoles = [.. slot.Roles],
            AssigneeUserIds = [.. slot.Users],
            DueAt = WorkflowEngine.DueAt(step, now),
            CreatedAt = now,
        };
        t.Raise(new WorkflowTaskCreated(t.Id, instance.Id, instance.AssetId, step.Id, step.Name, t.AssigneeRoles, t.AssigneeUserIds, t.DueAt));
        return t;
    }

    /// <summary>Is this person one of the assignees (or, once escalated, an escalation target)?</summary>
    public bool IsAssignedTo(Guid userId, IReadOnlyCollection<string> roleNames) =>
        AssigneeUserIds.Contains(userId) || EscalatedUserIds.Contains(userId)
        || AssigneeRoles.Concat(EscalatedRoles).Any(roleNames.Contains);

    public bool IsOverdue(DateTimeOffset now) => IsPending && (SlaBreachedAt is not null || (DueAt is { } due && due <= now));

    public void Decide(Guid userId, bool approve, string? comment, DateTimeOffset now)
    {
        Status = approve ? TaskStatuses.Approved : TaskStatuses.Rejected;
        DecidedBy = userId;
        DecidedAt = now;
        Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        Raise(new WorkflowTaskCompleted(Id, InstanceId, AssetId, StepId, approve ? "approve" : "reject", userId, Comment, DueAt is null || now <= DueAt));
    }

    /// <summary>Closes a task nobody needs to decide any more (its step was settled, or the workflow was withdrawn).</summary>
    public void Cancel()
    {
        if (IsPending) Status = TaskStatuses.Cancelled;
    }

    /// <summary>The SLA passed: record it once and let the escalation targets act on the task too.</summary>
    public bool MarkSlaBreached(DateTimeOffset now, IEnumerable<string> escalateRoles, IEnumerable<Guid> escalateUsers)
    {
        if (!IsPending || SlaBreachedAt is not null || DueAt is not { } due) return false;
        SlaBreachedAt = now;
        EscalatedRoles = escalateRoles.Where(r => !AssigneeRoles.Contains(r)).ToList();
        EscalatedUserIds = escalateUsers.Where(u => !AssigneeUserIds.Contains(u)).ToList();
        Raise(new WorkflowSlaBreached(Id, InstanceId, AssetId, StepId, due, EscalatedRoles, EscalatedUserIds));
        return true;
    }
}
