namespace Dam.Domain.Workflow;

public static class WorkflowStatuses
{
    public const string Running = "running";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Cancelled = "cancelled";
}

public static class TaskStatuses
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Cancelled = "cancelled";

    public static bool IsDecision(string status) => status is Approved or Rejected;
}

/// <summary>Who one task is addressed to. <c>any</c> steps produce one slot holding every assignee; <c>all</c> steps produce one slot per assignee.</summary>
public sealed record TaskSlot(IReadOnlyList<string> Roles, IReadOnlyList<Guid> Users);

public enum AdvanceKind
{
    /// <summary>The step still has tasks to decide.</summary>
    Wait,
    /// <summary>Move to <see cref="Advance.StepId"/> (a new set of tasks).</summary>
    Goto,
    /// <summary>The workflow ends approved.</summary>
    Complete,
    /// <summary>The workflow ends rejected.</summary>
    Reject,
}

public sealed record Advance(AdvanceKind Kind, string? StepId = null);

/// <summary>
/// The pure rules of the engine (ADR-0030): which tasks a step creates, what a set of decisions means for the step, and the
/// segregation-of-duties and publish-gate checks. Entities and handlers apply the answers; nothing here touches storage,
/// so the transitions are unit-tested directly.
/// </summary>
public static class WorkflowEngine
{
    public static IReadOnlyList<TaskSlot> Slots(StepDef step) =>
        step.Mode == WorkflowDef.ModeAll
            ? step.Assignees.Roles.Select(r => new TaskSlot([r], [])).Concat(step.Assignees.Users.Select(u => new TaskSlot([], [u]))).ToList()
            : [new TaskSlot(step.Assignees.Roles, step.Assignees.Users)];

    public static DateTimeOffset? DueAt(StepDef step, DateTimeOffset now) => step.SlaHours is { } h ? now.AddHours(h) : null;

    /// <summary>What the tasks of one activation of a step add up to. Any rejection rejects the step; otherwise an
    /// <c>any</c> step is approved by its first approval and an <c>all</c> step once none is left pending.</summary>
    /// <param name="statuses">Statuses (<see cref="TaskStatuses"/>) of every task created for this activation of the step.</param>
    public static Advance Resolve(StepDef step, IReadOnlyCollection<string> statuses)
    {
        if (statuses.Contains(TaskStatuses.Rejected)) return Route(step.OnReject, WorkflowDef.Reject, AdvanceKind.Reject);
        var approved = step.Mode == WorkflowDef.ModeAll
            ? statuses.Count > 0 && statuses.All(s => s == TaskStatuses.Approved)
            : statuses.Contains(TaskStatuses.Approved);
        return approved ? Route(step.OnApprove, WorkflowDef.Complete, AdvanceKind.Complete) : new Advance(AdvanceKind.Wait);
    }

    private static Advance Route(string target, string reserved, AdvanceKind whenReserved) =>
        target == reserved ? new Advance(whenReserved) : new Advance(AdvanceKind.Goto, target);

    /// <summary>
    /// Segregation of duties: with <c>distinctApprovers</c>, one person cannot approve two different steps of an instance, nor
    /// two of the parallel tasks of one activation of an <c>all</c> step (that would let one person stand in for several).
    /// Approving the same step again after a rejection sent the instance back to it is allowed.
    /// </summary>
    /// <param name="priorApprovals">Steps and activations the deciding user has already approved in this instance.</param>
    public static bool ViolatesDistinctApprovers(bool distinctApprovers, string stepId, int round, IEnumerable<(string StepId, int Round)> priorApprovals) =>
        distinctApprovers && priorApprovals.Any(a => a.StepId != stepId || a.Round == round);

    /// <summary>The publish guard (<c>IWorkflowGate</c>): an asset is held back only while its latest workflow is still running.
    /// No instance means no workflow applied. A rejected or cancelled instance returns the asset to draft, and the only way
    /// back to <c>approved</c> after that is a fresh submit, which either starts a new instance (and is judged by that one)
    /// or has no workflow and is approved directly.</summary>
    public static bool AllowsPublish(string? latestInstanceStatus) => latestInstanceStatus != WorkflowStatuses.Running;
}
