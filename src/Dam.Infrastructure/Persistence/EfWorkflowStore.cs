using Dam.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Dam.Infrastructure.Persistence;

/// <summary>Serialises work on one workflow instance with a row lock held until the surrounding transaction ends (ADR-0030).</summary>
public sealed class EfWorkflowStore(DamDbContext db) : IWorkflowStore
{
    public async Task LockInstanceAsync(Guid instanceId, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM workflow_instances WHERE id = {instanceId} FOR UPDATE", ct);
}
