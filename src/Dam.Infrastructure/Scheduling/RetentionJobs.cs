using Dam.Application.Abstractions;
using Dam.Domain.Assets;
using Dam.Infrastructure.Messaging;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dam.Infrastructure.Scheduling;

/// <summary>One asset a scheduled job found across every tenant (spec Step 1.6b polls the whole database, not one tenant).</summary>
public readonly record struct AssetLead(Guid TenantId, Guid AssetId);

/// <summary>
/// Shared plumbing for the two retention-family jobs: find candidates across every tenant through the system connection
/// (read-only, same pattern as the outbox relay), then apply each change through an ordinary tenant-scoped scope so RLS,
/// the tenant query filter, and the usual audit/outbox pipeline all apply exactly as they do to a user's own request.
/// </summary>
public abstract class CrossTenantJob(IServiceScopeFactory scopeFactory, NpgsqlDataSource systemDb, ILogger log)
{
    /// <summary>For subclasses that need to open their own tenant-scoped scopes.</summary>
    protected IServiceScopeFactory Scopes => scopeFactory;

    /// <summary>Runs <paramref name="sql"/>, which selects <c>(id, tenant_id)</c> rows.</summary>
    protected Task<IReadOnlyList<AssetLead>> FindAsync(string sql, CancellationToken ct) => FindAsync(sql, null, ct);

    /// <param name="parameters">Named parameters for <paramref name="sql"/> (for example the job's own clock reading).</param>
    protected async Task<IReadOnlyList<AssetLead>> FindAsync(string sql, IReadOnlyDictionary<string, object>? parameters, CancellationToken ct)
    {
        var leads = new List<AssetLead>();
        await using var conn = await systemDb.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in parameters ?? new Dictionary<string, object>()) cmd.Parameters.AddWithValue(name, value);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) leads.Add(new AssetLead(r.GetGuid(1), r.GetGuid(0)));
        return leads;
    }

    /// <summary>Like <see cref="ForEachAsync"/> but not tied to assets: runs <paramref name="apply"/> in a tenant-scoped scope for each
    /// lead (<c>AssetId</c> is simply the id of whatever row the discovery query selected). Events raised are persisted in the
    /// same transaction; one lead failing is logged and never stops the batch.</summary>
    protected async Task ForEachScopedAsync(IReadOnlyList<AssetLead> leads, Func<IServiceProvider, DamDbContext, AssetLead, CancellationToken, Task<bool>> apply, CancellationToken ct)
    {
        foreach (var lead in leads)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            try
            {
                sp.GetRequiredService<MutableTenantContext>().TenantId = lead.TenantId;
                var db = sp.GetRequiredService<DamDbContext>();
                var uow = sp.GetRequiredService<IUnitOfWork>();
                await uow.BeginAsync(ct);
                try
                {
                    if (!await apply(sp, db, lead, ct)) { await uow.RollbackAsync(ct); continue; }
                    var events = sp.GetRequiredService<IDomainEventSource>().Drain();
                    if (events.Count > 0) await sp.GetRequiredService<IOutbox>().EnqueueAsync(events, ct);
                    await db.SaveChangesAsync(ct);
                    await uow.CommitAsync(ct);
                }
                catch { await uow.RollbackAsync(ct); throw; }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Could not process {Row} in tenant {Tenant}", lead.AssetId, lead.TenantId);
            }
        }
    }

    /// <summary>Runs <paramref name="apply"/> against a fresh, tenant-scoped scope, then persists whatever domain events the
    /// asset raised, exactly as a command handler's own pipeline would. One asset's failure is logged and never stops
    /// the batch: a job that gives up entirely because of one bad row would silently stop maintaining every other tenant.</summary>
    protected async Task ForEachAsync(IReadOnlyList<AssetLead> leads, Func<IServiceProvider, DamDbContext, Asset, CancellationToken, Task<bool>> apply, CancellationToken ct)
    {
        foreach (var lead in leads)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            try
            {
                sp.GetRequiredService<MutableTenantContext>().TenantId = lead.TenantId;
                var db = sp.GetRequiredService<DamDbContext>();
                var uow = sp.GetRequiredService<IUnitOfWork>();
                var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == lead.AssetId, ct);
                if (asset is null) continue;   // gone or moved on since it was found

                await uow.BeginAsync(ct);
                try
                {
                    if (!await apply(sp, db, asset, ct)) { await uow.RollbackAsync(ct); continue; }
                    var events = sp.GetRequiredService<IDomainEventSource>().Drain();
                    if (events.Count > 0) await sp.GetRequiredService<IOutbox>().EnqueueAsync(events, ct);
                    await db.SaveChangesAsync(ct);
                    await uow.CommitAsync(ct);
                }
                catch { await uow.RollbackAsync(ct); throw; }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Could not process asset {Asset} in tenant {Tenant}", lead.AssetId, lead.TenantId);
            }
        }
    }
}

/// <summary>Fires publish_at and expire_at (spec Step 1.6, "Scheduled publish fires within 60s of publish_at"). Polls
/// frequently (default every 15s, DAM_SCHEDULE_POLL_CRON) since the SLA is tight; each hit is one small, indexed query.</summary>
public sealed class PublishExpireJob(IServiceScopeFactory scopeFactory, NpgsqlDataSource systemDb, IClock clock, ILogger<PublishExpireJob> log)
    : CrossTenantJob(scopeFactory, systemDb, log), IScheduledJob
{
    public const string JobKey = "assets.publish_expire";
    public string Key => JobKey;

    public async Task RunAsync(IReadOnlyDictionary<string, string> data, CancellationToken ct)
    {
        // An asset whose licence has ended or not begun is not published on schedule (ADR-0033): it stays approved, as a person's publish would be refused.
        var toPublish = await FindAsync("""
            SELECT a.id, a.tenant_id FROM assets a
            WHERE a.status = 'approved' AND a.publish_at IS NOT NULL AND a.publish_at <= now()
              AND NOT EXISTS (SELECT 1 FROM rights r WHERE r.id = a.rights_id
                              AND (r.valid_to < (now() AT TIME ZONE 'UTC')::date OR r.valid_from > (now() AT TIME ZONE 'UTC')::date))
            LIMIT 500
            """, ct);
        await ForEachAsync(toPublish, async (_, db, asset, c) =>
        {
            // Checked again with the job's own clock, in the tenant's scope: the SQL above uses the database's.
            if (asset.RightsId is { } rightsId && await db.Rights.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rightsId, c) is { } rights
                && rights.Validity(Dam.Domain.Licensing.RightsRules.Today(clock.UtcNow)).Status is Dam.Domain.Licensing.RightsStatuses.Expired or Dam.Domain.Licensing.RightsStatuses.NotYetValid)
                return false;
            return Fire(asset, AssetLifecycle.Publish, "Published automatically (scheduled).");
        }, ct);

        var toExpire = await FindAsync("SELECT id, tenant_id FROM assets WHERE status = 'published' AND expire_at IS NOT NULL AND expire_at <= now() LIMIT 500", ct);
        await ForEachAsync(toExpire, (_, _, asset, _) => Task.FromResult(Fire(asset, AssetLifecycle.Expire, "Expired automatically (scheduled).")), ct);

        if (toPublish.Count > 0 || toExpire.Count > 0) log.LogInformation("Scheduled {Published} publish and {Expired} expire", toPublish.Count, toExpire.Count);
    }

    private bool Fire(Asset asset, string action, string reason)
    {
        if (!AssetLifecycle.TryGetTarget(asset.Status, action, out var to)) return false;   // status moved on since it was found
        var now = clock.UtcNow;
        asset.ChangeStatus(to, reason, now);
        // The timer that just fired is spent; the other one (if any) is left exactly as it was.
        asset.SetSchedule(action == AssetLifecycle.Publish ? null : asset.PublishAt, action == AssetLifecycle.Expire ? null : asset.ExpireAt, now);
        return true;
    }
}

/// <summary>Auto-archives expired assets and purges archived ones, per each asset's retention policy (spec Step 1.6:
/// "archive -> cold tier... purge with audit and legal-hold check"). Runs hourly by default (DAM_RETENTION_POLL_CRON):
/// unlike publish/expire there is no tight SLA, and every check here involves a policy join.</summary>
public sealed class RetentionJob(IServiceScopeFactory scopeFactory, NpgsqlDataSource systemDb, IObjectStore store, IClock clock, ILogger<RetentionJob> log)
    : CrossTenantJob(scopeFactory, systemDb, log), IScheduledJob
{
    public const string JobKey = "assets.retention";
    public string Key => JobKey;

    private const string ArchiveSql = """
        SELECT a.id, a.tenant_id FROM assets a JOIN retention_policies p ON p.id = a.retention_policy_id
        WHERE a.status = 'expired' AND p.archive_after_days IS NOT NULL
          AND a.updated_at <= now() - (p.archive_after_days || ' days')::interval
        LIMIT 500
        """;

    private const string PurgeSql = """
        SELECT a.id, a.tenant_id FROM assets a JOIN retention_policies p ON p.id = a.retention_policy_id
        WHERE a.status = 'archived' AND p.delete_after_days IS NOT NULL
          AND a.updated_at <= now() - (p.delete_after_days || ' days')::interval
          AND (a.legal_hold = false OR p.legal_hold_exempt = true)
        LIMIT 500
        """;

    public async Task RunAsync(IReadOnlyDictionary<string, string> data, CancellationToken ct)
    {
        var toArchive = await FindAsync(ArchiveSql, ct);
        await ForEachAsync(toArchive, (_, _, asset, _) => Task.FromResult(Archive(asset)), ct);

        // Re-checked here, not just by the SQL above: a legal hold placed between discovery and now must still block it.
        var toPurge = await FindAsync(PurgeSql, ct);
        var purged = 0;
        var filesToDelete = new List<(string Bucket, string StorageKey)>();
        await ForEachAsync(toPurge, async (sp, db, asset, c) =>
        {
            if (!AssetLifecycle.TryGetTarget(asset.Status, AssetLifecycle.Purge, out var to)) return false;
            if (asset.LegalHold)
            {
                // A hold blocks purge unless the asset's own policy is itself marked to purge through one.
                var exempt = asset.RetentionPolicyId is { } pid && await db.RetentionPolicies.AnyAsync(p => p.Id == pid && p.LegalHoldExempt, c);
                if (!exempt) return false;
            }
            var versions = await db.AssetVersions.Where(v => v.AssetId == asset.Id).Select(v => new { v.Bucket, v.StorageKey }).ToListAsync(c);
            var renditions = await db.Renditions.Where(r => r.AssetId == asset.Id).Select(r => new { r.Bucket, r.StorageKey }).ToListAsync(c);
            asset.ChangeStatus(to, "Purged by retention policy.", clock.UtcNow);
            // What the AI derived from the files goes with them (vectors are large); the rows would also follow the asset through their foreign keys.
            await db.AssetEmbeddings.Where(e => e.AssetId == asset.Id).ExecuteDeleteAsync(c);
            await db.AssetExtracts.Where(e => e.AssetId == asset.Id).ExecuteDeleteAsync(c);
            await db.AiSuggestions.Where(e => e.AssetId == asset.Id).ExecuteDeleteAsync(c);
            await db.AiRuns.Where(e => e.AssetId == asset.Id).ExecuteDeleteAsync(c);
            filesToDelete.AddRange(versions.Select(v => (v.Bucket, v.StorageKey)));
            filesToDelete.AddRange(renditions.Select(r => (r.Bucket, r.StorageKey)));
            purged++;
            return true;
        }, ct);

        // File bytes are removed only after each asset's own transition already committed: a storage failure here leaves
        // an orphaned object behind, never a "purged" asset whose bytes are still reachable.
        foreach (var (bucket, key) in filesToDelete)
            try { await store.DeleteAsync(bucket, key, ct); }
            catch (Exception ex) { log.LogWarning(ex, "Purge left an object behind: {Bucket}/{Key}", bucket, key); }

        if (toArchive.Count > 0 || purged > 0) log.LogInformation("Retention: archived {Archived}, purged {Purged}", toArchive.Count, purged);
    }

    private bool Archive(Asset asset)
    {
        if (!AssetLifecycle.TryGetTarget(asset.Status, AssetLifecycle.Archive, out var to)) return false;
        asset.ChangeStatus(to, "Archived by retention policy.", clock.UtcNow);
        return true;
    }
}

public static class RetentionSchedulingRegistration
{
    /// <summary>Registers the cross-tenant jobs (publish/expire, retention, workflow SLA, rights expiry) and the read-only system connection they discover candidates through
    /// (env: DAM_DB_SYSTEM_CONNECTION) — not the full outbox relay, since a scheduler host relays nothing itself.</summary>
    public static IServiceCollection AddDamRetentionScheduling(this IServiceCollection services, string systemConnectionString)
    {
        services.AddSingleton(new NpgsqlDataSourceBuilder(systemConnectionString).Build());
        services.AddScheduledJob<PublishExpireJob>();
        services.AddScheduledJob<RetentionJob>();
        services.AddScheduledJob<WorkflowSlaJob>();
        services.AddScheduledJob<RightsExpiryJob>();
        return services;
    }
}
