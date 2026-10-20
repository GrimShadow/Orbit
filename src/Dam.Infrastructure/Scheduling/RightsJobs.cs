using Dam.Application.Abstractions;
using Dam.Domain.Assets;
using Dam.Domain.Common;
using Dam.Domain.Licensing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dam.Infrastructure.Scheduling;

/// <summary>
/// The licence housekeeping job (spec Step 2.5, ADR-0033). It runs once at start and then hourly (DAM_RIGHTS_POLL_CRON), looking across every tenant through
/// the read-only system connection and changing things through ordinary tenant-scoped scopes, like the other cross-tenant jobs. Three steps, in this order:
/// <list type="number">
/// <item><b>Catch up</b> assets whose copy of their rights' channels or regions has fallen behind (a rights update re-derives at most 500 assets inline; this
/// finishes the rest and heals any other drift).</item>
/// <item><b>Alert</b>: for each asset whose licence ends in 30, 7 or 1 days, or ended within the last 30 days, raise <c>rights.expiring</c> / <c>rights.expired</c> (the
/// owner is told by the notification consumer, webhooks and chat channels by theirs). A ledger row per (asset, licence, threshold, end date) makes each alert fire
/// once however often the job runs or restarts; extending the licence starts the sequence again.</item>
/// <item><b>Expire</b>: a <i>published</i> asset whose licence has ended is moved to <c>expired</c> as the system, with the reason "Licence expired: name"; assets in
/// other statuses are left alone, and an asset whose licence was extended is not touched.</item>
/// </list>
/// Dates are UTC calendar dates from <see cref="IClock"/>, so tests drive it with a fake clock. Every step is idempotent.
/// </summary>
public sealed class RightsExpiryJob(IServiceScopeFactory scopeFactory, NpgsqlDataSource systemDb, IClock clock, ILogger<RightsExpiryJob> log)
    : CrossTenantJob(scopeFactory, systemDb, log), IScheduledJob
{
    public const string JobKey = "rights.expiry";
    public string Key => JobKey;

    /// <summary>An ended licence is only announced for this long afterwards, so switching the job on does not announce ancient history.</summary>
    private const int ExpiredAlertWindowDays = 30;

    private const string DriftSql = """
        SELECT a.id, a.tenant_id FROM assets a JOIN rights r ON r.id = a.rights_id
        WHERE a.status <> 'deleted' AND (a.rights_channels IS DISTINCT FROM r.channels OR a.rights_regions IS DISTINCT FROM r.regions)
        LIMIT 500
        """;

    private const string AlertSql = """
        SELECT a.id, a.tenant_id FROM assets a JOIN rights r ON r.id = a.rights_id
        WHERE a.status IN ('draft', 'in_review', 'approved', 'published') AND r.valid_to IS NOT NULL
          AND r.valid_to >= @since AND r.valid_to <= @horizon
        ORDER BY r.valid_to LIMIT 1000
        """;

    private const string ExpireSql = """
        SELECT a.id, a.tenant_id FROM assets a JOIN rights r ON r.id = a.rights_id
        WHERE a.status = 'published' AND r.valid_to IS NOT NULL AND r.valid_to < @today
        ORDER BY r.valid_to LIMIT 500
        """;

    public async Task RunAsync(IReadOnlyDictionary<string, string> data, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var today = RightsRules.Today(now);

        var drifted = await FindAsync(DriftSql, ct);
        var caughtUp = 0;
        await ForEachAsync(drifted, async (_, db, asset, c) =>
        {
            var rights = await db.Rights.AsNoTracking().FirstOrDefaultAsync(r => r.Id == asset.RightsId, c);
            if (rights is null || !asset.AssignRights(rights.Id, rights.Channels, rights.Regions, now)) return false;
            caughtUp++;
            return true;
        }, ct);

        var alertable = await FindAsync(AlertSql, new Dictionary<string, object>
        {
            ["since"] = today.AddDays(-ExpiredAlertWindowDays), ["horizon"] = today.AddDays(RightsRules.AlertThresholds[0]),
        }, ct);
        var alerted = 0;
        await ForEachAsync(alertable, async (sp, db, asset, c) =>
        {
            var rights = await db.Rights.AsNoTracking().FirstOrDefaultAsync(r => r.Id == asset.RightsId, c);
            if (rights?.ValidTo is not { } validTo) return false;
            var days = validTo.DayNumber - today.DayNumber;
            var threshold = days < 0 ? RightsRules.ExpiredThreshold : RightsRules.AlertThreshold(days);
            if (threshold is not { } due) return false;

            // The ledger row is the claim: whoever inserts it raises the event, so two runs (or two schedulers) cannot both alert.
            var claimed = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO rights_alerts (id, tenant_id, rights_id, asset_id, threshold_days, valid_to, created_at)
                VALUES ({Uuid7.NewGuid()}, {asset.TenantId}, {rights.Id}, {asset.Id}, {due}, {validTo}, {now}) ON CONFLICT DO NOTHING
                """, c);
            if (claimed == 0) return false;
            sp.GetRequiredService<IEventCollector>().Raise(days < 0
                ? new RightsExpired(rights.Id, asset.Id, rights.Name, validTo)
                : new RightsExpiring(rights.Id, asset.Id, rights.Name, validTo, days, due));
            alerted++;
            return true;
        }, ct);

        var overdue = await FindAsync(ExpireSql, new Dictionary<string, object> { ["today"] = today }, ct);
        var expired = 0;
        await ForEachAsync(overdue, async (_, db, asset, c) =>
        {
            if (!AssetLifecycle.TryGetTarget(asset.Status, AssetLifecycle.Expire, out var to)) return false;   // moved on since it was found
            var rights = await db.Rights.AsNoTracking().FirstOrDefaultAsync(r => r.Id == asset.RightsId, c);
            if (rights?.ValidTo is not { } validTo || validTo >= today) return false;                            // extended or reassigned since it was found
            asset.ChangeStatus(to, $"Licence expired: {rights.Name}", now);
            expired++;
            return true;
        }, ct);

        if (caughtUp + alerted + expired > 0)
            log.LogInformation("Rights: re-derived {CaughtUp} asset(s), raised {Alerted} alert(s), expired {Expired} asset(s)", caughtUp, alerted, expired);
    }
}
