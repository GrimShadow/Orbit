using System.Text.Json;
using Dam.Application.Messaging;
using Dam.Application.Workflow;
using Dam.Shared.Content;
using static Dam.Api.Endpoints.IdentityEndpoints;

namespace Dam.Api.Endpoints;

public sealed record CreateWorkflowRequest(string Name, string? Description, JsonElement? Definition);
public sealed record UpdateWorkflowRequest(string Name, string? Description, JsonElement? Definition);
public sealed record ValidateWorkflowRequest(JsonElement? Definition);
public sealed record ReplaceWorkflowBindingsRequest(IReadOnlyList<WorkflowBindingInput> Bindings);

/// <summary>Workflow definitions, bindings and the task inbox (ADR-0030).</summary>
public static class WorkflowEndpoints
{
    public static void MapWorkflowEndpoints(this RouteGroupBuilder v1)
    {
        var workflows = v1.MapGroup("/workflows").WithTags("Workflow");

        workflows.MapGet("", (IDispatcher d, CancellationToken ct) => Run(d, new ListWorkflowsQuery(), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListWorkflows").WithSummary("Workflow definitions with how many folders and bindings use each and how many assets are running through it. Needs workflow.manage or folders.manage")
            .Produces<IReadOnlyList<WorkflowDto>>();
        workflows.MapPost("", (CreateWorkflowRequest r, IDispatcher d, CancellationToken ct) =>
                Then(d, new CreateWorkflowCommand(r.Name, r.Description, r.Definition), id => new GetWorkflowQuery(id), ct, createdAt: "/api/v1/workflows"))
            .WithName("CreateWorkflow").WithSummary("Create a workflow from a definition (version 1: steps, assignees, any/all, SLA, escalation, routing). An invalid definition is 422 with errors addressed by path")
            .Produces<WorkflowDto>(StatusCodes.Status201Created);
        workflows.MapPost("/validate", (ValidateWorkflowRequest r, IDispatcher d, CancellationToken ct) =>
                Run(d, new ValidateWorkflowQuery(r.Definition), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ValidateWorkflow").WithSummary("Checks a definition without saving it. Always 200: valid, or a list of errors addressed by path (for the designer's live feedback)")
            .Produces<ValidateWorkflowResultDto>();
        workflows.MapGet("/bindings", (IDispatcher d, CancellationToken ct) => Run(d, new ListWorkflowBindingsQuery(), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListWorkflowBindings").WithSummary("Which workflow applies to assets of a type and/or region when no folder names a default").Produces<IReadOnlyList<WorkflowBindingDto>>();
        workflows.MapPut("/bindings", (ReplaceWorkflowBindingsRequest r, IDispatcher d, CancellationToken ct) =>
                Run(d, new ReplaceWorkflowBindingsCommand(r.Bindings ?? []), _ => ReadBindings(d, ct), ct))
            .WithName("ReplaceWorkflowBindings").WithSummary("Replaces the whole list of type/region bindings. Needs workflow.manage").Produces<IReadOnlyList<WorkflowBindingDto>>();
        workflows.MapGet("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) => Run(d, new GetWorkflowQuery(id), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetWorkflow").Produces<WorkflowDto>();
        workflows.MapPut("/{id:guid}", (Guid id, UpdateWorkflowRequest r, IDispatcher d, CancellationToken ct) =>
                Then(d, new UpdateWorkflowCommand(id, r.Name, r.Description, r.Definition), wid => new GetWorkflowQuery(wid), ct))
            .WithName("UpdateWorkflow").WithSummary("Replaces a workflow. Assets already going through it keep the definition they started with").Produces<WorkflowDto>();
        workflows.MapDelete("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) =>
                Run(d, new DeleteWorkflowCommand(id), _ => Task.FromResult(Results.NoContent()), ct))
            .WithName("DeleteWorkflow").WithSummary("409 while a folder uses it as default, a binding points at it, or an asset is running through it");

        var tasks = v1.MapGroup("/tasks").WithTags("Workflow");
        tasks.MapGet("", (IDispatcher d, CancellationToken ct, string? assignee, string? status, string? cursor, int? limit) =>
                Run(d, new ListTasksQuery(assignee, status, cursor, limit), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("ListTasks")
            .WithSummary("The task inbox: tasks the caller can decide (assignee=me, default) or has decided; assignee=all lists every task and needs workflow.manage. status=pending (default), decided or all")
            .Produces<Dam.Shared.Identity.Page<WorkflowTaskDto>>();
        tasks.MapGet("/summary", (IDispatcher d, CancellationToken ct) => Run(d, new GetTaskSummaryQuery(), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("GetTaskSummary").WithSummary("Counts for the caller's inbox, plus tenant totals and 30-day SLA attainment for workflow.manage / analytics.read holders (null for others)")
            .Produces<TaskSummaryDto>();
        tasks.MapPost("/{id:guid}/decision", (Guid id, DecideTaskInput r, IDispatcher d, CancellationToken ct) =>
                Run(d, new DecideTaskCommand(id, r.Decision, r.Comment), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("DecideTask")
            .WithSummary("Approve or reject a task (a comment is required to reject). Needs to be an assignee, workflow.tasks.decide and assets.approve on the asset. 409 if already decided")
            .Produces<WorkflowTaskDto>();

        v1.MapGet("/assets/{id:guid}/workflow", (Guid id, IDispatcher d, CancellationToken ct) => Run(d, new GetAssetWorkflowQuery(id), v => Task.FromResult(Results.Ok(v)), ct))
            .WithTags("Workflow").WithName("GetAssetWorkflow")
            .WithSummary("The asset's latest workflow as a timeline of steps and their tasks; instance is null when none ever applied")
            .Produces<AssetWorkflowDto>();
    }

    private static async Task<IResult> ReadBindings(IDispatcher d, CancellationToken ct)
    {
        var r = await d.Send(new ListWorkflowBindingsQuery(), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.Error.ToProblem();
    }
}
