using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Domain.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dam.Infrastructure.Scheduling;

/// <summary>
/// Marks workflow tasks whose SLA has passed (spec Step 2.3, ADR-0030): sets <c>sla_breached_at</c>, adds the step's escalation
/// targets as further eligible deciders and raises <c>workflow.sla.breached</c>. Polls every 30 seconds by default
/// (DAM_WORKFLOW_SLA_POLL_CRON), so a breach shows in the API well within a minute of the due time. Candidates are found across
/// every tenant through the read-only system connection; each is then changed through an ordinary tenant-scoped scope.
/// </summary>
public sealed class WorkflowSlaJob(IServiceScopeFactory scopeFactory, NpgsqlDataSource systemDb, IClock clock, ILogger<WorkflowSlaJob> log)
    : CrossTenantJob(scopeFactory, systemDb, log), IScheduledJob
{
    public const string JobKey = "workflow.sla";
    public string Key => JobKey;

    private const string OverdueSql = """
        SELECT id, tenant_id FROM workflow_tasks
        WHERE status = 'pending' AND sla_breached_at IS NULL AND due_at IS NOT NULL AND due_at <= @now
        ORDER BY due_at LIMIT 500
        """;

    public async Task RunAsync(IReadOnlyDictionary<string, string> data, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var overdue = await FindAsync(OverdueSql, new Dictionary<string, object> { ["now"] = now.ToUniversalTime() }, ct);
        var breached = 0;
        await ForEachScopedAsync(overdue, async (_, db, lead, c) =>
        {
            var task = await db.WorkflowTasks.FirstOrDefaultAsync(t => t.Id == lead.AssetId, c);
            if (task is not { IsPending: true }) return false;   // decided or cancelled since it was found
            var instance = await db.WorkflowInstances.FirstOrDefaultAsync(i => i.Id == task.InstanceId, c);
            var escalation = instance is null ? null
                : WorkflowDefinitionParser.Parse(JsonSerializer.Deserialize<JsonElement>(instance.DefinitionJson)).Definition?.Find(task.StepId)?.Escalation?.To;
            if (!task.MarkSlaBreached(now, escalation?.Roles ?? [], escalation?.Users ?? [])) return false;
            breached++;
            return true;
        }, ct);
        if (breached > 0) log.LogInformation("Workflow SLA: {Breached} task(s) breached", breached);
    }
}
