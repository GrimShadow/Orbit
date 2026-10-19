using System.Text.Json;

namespace Dam.Shared.Content;

/// <param name="Definition">The definition document (version 1; see ADR-0030) exactly as saved.</param>
/// <param name="UsedByFolders">Folders that name this workflow as their default.</param>
/// <param name="Bindings">Type/region bindings that point at it.</param>
/// <param name="RunningInstances">Assets currently going through it.</param>
public sealed record WorkflowDto(
    Guid Id, string Name, string Description, JsonElement Definition, int UsedByFolders, int Bindings, int RunningInstances,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <param name="Path">Where in the definition the problem is, e.g. <c>steps[1].assignees.roles[0]</c>; empty for the document as a whole.</param>
public sealed record WorkflowErrorDto(string Path, string Message);

public sealed record ValidateWorkflowResultDto(bool Valid, IReadOnlyList<WorkflowErrorDto> Errors);

/// <param name="AssetType">Null matches every type.</param>
/// <param name="Region">Null matches every region.</param>
public sealed record WorkflowBindingDto(Guid Id, Guid WorkflowId, string WorkflowName, string? AssetType, string? Region);

public sealed record WorkflowBindingInput(Guid WorkflowId, string? AssetType, string? Region);

/// <param name="Status">pending, approved, rejected or cancelled.</param>
/// <param name="AssigneeRoles">Roles that may decide, including escalation roles once the SLA has passed.</param>
/// <param name="AssigneeUserIds">Users that may decide, including escalation users once the SLA has passed.</param>
/// <param name="Decision">approve or reject; null while pending (or when the task was cancelled).</param>
/// <param name="Escalated">True once the SLA passed and the step's escalation targets were added as deciders.</param>
public sealed record WorkflowTaskDto(
    Guid Id, Guid AssetId, string AssetTitle, string AssetType, string WorkflowName, string StepId, string StepName, string Status,
    IReadOnlyList<string> AssigneeRoles, IReadOnlyList<Guid> AssigneeUserIds, DateTimeOffset? DueAt, bool SlaBreached,
    DateTimeOffset? SlaBreachedAt, DateTimeOffset CreatedAt, string? Decision, Guid? DecidedBy, DateTimeOffset? DecidedAt, string? Comment,
    bool Escalated, string? DecidedByName, Guid InstanceId);

/// <param name="PendingForMe">Open tasks the caller could decide.</param>
/// <param name="OverdueForMe">Of those, the ones past their due time.</param>
/// <param name="PendingTotal">Open tasks in the tenant; null unless the caller holds workflow.manage or analytics.read.</param>
/// <param name="OverdueTotal">Of those, the overdue ones; same visibility as <paramref name="PendingTotal"/>.</param>
/// <param name="Completed30d">Tasks decided in the last 30 days; same visibility as <paramref name="PendingTotal"/>.</param>
/// <param name="CompletedOnTime30d">Of those, decided by their due time (a task without an SLA is never late).</param>
/// <param name="SlaAttainmentPercent">CompletedOnTime30d as a percentage of Completed30d, 0-100 with one decimal; null when none completed or not visible.</param>
public sealed record TaskSummaryDto(
    int PendingForMe, int OverdueForMe, int? PendingTotal, int? OverdueTotal, int? Completed30d, int? CompletedOnTime30d, double? SlaAttainmentPercent);

/// <param name="Decision">approve or reject.</param>
/// <param name="Comment">Required for reject (at most 1000 characters).</param>
public sealed record DecideTaskInput(string Decision, string? Comment);

/// <param name="State">waiting (not reached yet), active (has open tasks), done (decided) or skipped (never reached, or cancelled).</param>
public sealed record WorkflowStepViewDto(string Id, string Name, string State, string Mode, int? SlaHours, IReadOnlyList<WorkflowTaskDto> Tasks);

/// <param name="Status">running, approved, rejected or cancelled.</param>
public sealed record WorkflowInstanceViewDto(
    Guid Id, string WorkflowName, string Status, Guid? StartedBy, string? StartedByName, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt,
    string? OutcomeReason, string? OutcomeStatus, bool AutoPublish, IReadOnlyList<WorkflowStepViewDto> Steps);

/// <param name="Instance">The asset's latest workflow instance (running or finished); null when no workflow has ever applied to it.</param>
public sealed record AssetWorkflowDto(WorkflowInstanceViewDto? Instance);
