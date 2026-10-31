using Dam.Application.Abstractions;
using Dam.Domain.Integrations;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dam.Infrastructure.Scheduling;

/// <summary>
/// Keeps the latest <see cref="IntegrationCall.KeepPerConnection"/> calls of each connection and deletes the rest (ADR-0040 decision 5). Daily by default (DAM_INTEGRATIONS_CLEANUP_CRON).
/// Finds the connections with more than that across every tenant through the read-only system connection, then deletes through an ordinary tenant-scoped scope, so row-level
/// security applies to the delete exactly as it does to a request.
/// </summary>
public sealed class IntegrationActivityCleanupJob(IServiceScopeFactory scopes, NpgsqlDataSource systemDb, ILogger<IntegrationActivityCleanupJob> log)
    : CrossTenantJob(scopes, systemDb, log), IScheduledJob
{
    public const string JobKey = "integrations.cleanup";
    public string Key => JobKey;

    private static readonly string SurplusSql =
        $"SELECT connection_id, tenant_id FROM integration_calls GROUP BY connection_id, tenant_id HAVING count(*) > {IntegrationCall.KeepPerConnection}";

    public async Task RunAsync(IReadOnlyDictionary<string, string> data, CancellationToken ct)
    {
        var leads = await FindAsync(SurplusSql, ct);   // AssetId here is the connection's id
        if (leads.Count == 0) return;
        await ForEachScopedAsync(leads, async (_, db, lead, token) =>
        {
            var connectionId = lead.AssetId;
            var tenantId = lead.TenantId;
            var keep = IntegrationCall.KeepPerConnection;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM integration_calls
                WHERE connection_id = {connectionId} AND tenant_id = {tenantId}
                  AND id NOT IN (SELECT id FROM integration_calls WHERE connection_id = {connectionId} ORDER BY id DESC LIMIT {keep})
                """, token);
            return true;
        }, ct);
        log.LogInformation("Integration activity cleanup trimmed {Connections} connection(s)", leads.Count);
    }
}

public static class IntegrationSchedulingRegistration
{
    /// <summary>Registers the activity cleanup job. Needs <c>AddDamRetentionScheduling</c> (the read-only system connection).</summary>
    public static IServiceCollection AddDamIntegrationJobs(this IServiceCollection services) => services.AddScheduledJob<IntegrationActivityCleanupJob>();
}
