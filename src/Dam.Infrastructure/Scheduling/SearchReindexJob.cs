using Dam.Application.Abstractions;
using Dam.Infrastructure.Messaging;
using Dam.Infrastructure.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dam.Infrastructure.Scheduling;

/// <summary>
/// Rebuilds one tenant's search index from scratch, or every tenant's when none is named (spec Step 1.7: "reindex job").
/// Not on a recurring cron - a schema change or an index-shape bug is what triggers this, not a clock - so it is fired
/// on demand through <see cref="IJobScheduler.ScheduleOnceAsync"/> with an optional "tenantId" datum.
/// </summary>
public sealed class SearchReindexJob(IServiceScopeFactory scopeFactory, NpgsqlDataSource systemDb, ILogger<SearchReindexJob> log) : IScheduledJob
{
    public const string JobKey = "search.reindex";
    public string Key => JobKey;

    public async Task RunAsync(IReadOnlyDictionary<string, string> data, CancellationToken ct)
    {
        var tenantIds = data.TryGetValue("tenantId", out var one) && Guid.TryParse(one, out var single)
            ? (IReadOnlyList<Guid>)[single] : await AllTenantsAsync(ct);

        foreach (var tenantId in tenantIds)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            try
            {
                scope.ServiceProvider.GetRequiredService<MutableTenantContext>().TenantId = tenantId;
                var count = await scope.ServiceProvider.GetRequiredService<ISearchIndexAdmin>().ReindexAsync(ct);
                log.LogInformation("Reindexed {Count} assets for tenant {Tenant}", count, tenantId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Reindex failed for tenant {Tenant}", tenantId);
            }
        }
    }

    private async Task<IReadOnlyList<Guid>> AllTenantsAsync(CancellationToken ct)
    {
        var ids = new List<Guid>();
        await using var conn = await systemDb.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT id FROM tenants", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) ids.Add(r.GetGuid(0));
        return ids;
    }
}
