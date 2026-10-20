using Dam.Application.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Dam.Infrastructure.Persistence;

/// <summary>
/// The analytics reads (ADR-0037) as SQL on the request's own connection, so Postgres RLS applies underneath the explicit tenant predicate. Usage reads
/// combine <c>usage_daily</c> (finished days that are not waiting for a re-roll) with <c>usage_events</c> (today, and every day marked dirty) in ONE statement:
/// a single snapshot, so a rollup committing meanwhile cannot make a day appear twice or not at all. KPI percentiles use <c>percentile_cont</c>.
/// </summary>
public sealed class EfAnalyticsStore(DamDbContext db, ITenantContext tenant) : IAnalyticsStore
{
    // ---------------------------------------------------------------------------------------------------- usage

    private static string UsageCte(bool byAsset)
    {
        var rolledAsset = byAsset ? " AND asset_id = @asset" : "";
        var liveAsset = byAsset ? " AND e.asset_id = @asset" : "";
        return $"""
            WITH dirty AS (SELECT day FROM usage_rollup_dirty WHERE tenant_id = @tenant AND day >= @from AND day < @to),
            live_days AS (
                SELECT day FROM dirty
                UNION SELECT @today WHERE @today >= @from AND @today < @to
            ), rolled AS (
                SELECT day, asset_id, event_type, channel, uses FROM usage_daily
                WHERE tenant_id = @tenant AND day >= @from AND day < @to AND day < @today{rolledAsset}
                  AND day NOT IN (SELECT day FROM dirty)
            ), live AS (
                SELECT d.day, e.asset_id, e.event_type, coalesce(e.channel, '') AS channel, count(*) AS uses
                FROM live_days d
                JOIN usage_events e ON e.tenant_id = @tenant{liveAsset}
                     AND e.occurred_at >= (d.day::timestamp AT TIME ZONE 'UTC') AND e.occurred_at < ((d.day + 1)::timestamp AT TIME ZONE 'UTC')
                GROUP BY d.day, e.asset_id, e.event_type, coalesce(e.channel, '')
            ), u AS (
                SELECT * FROM rolled UNION ALL SELECT * FROM live
            )
            """;
    }

    private void BindUsage(NpgsqlParameterCollection p, DateOnly from, DateOnly to, DateOnly today, Guid? asset = null)
    {
        p.Bind("tenant", NpgsqlDbType.Uuid, tenant.TenantId);
        p.Bind("from", NpgsqlDbType.Date, from);
        p.Bind("to", NpgsqlDbType.Date, to);
        p.Bind("today", NpgsqlDbType.Date, today);
        if (asset is { } a) p.Bind("asset", NpgsqlDbType.Uuid, a);
    }

    public async Task<IReadOnlyList<UsageCell>> UsageCellsAsync(DateOnly from, DateOnly to, DateOnly today, Guid? assetId, CancellationToken ct) =>
        await RawSql.QueryAsync(db, UsageCte(assetId is not null) + " SELECT day, event_type, channel, sum(uses)::bigint FROM u GROUP BY day, event_type, channel",
            p => BindUsage(p, from, to, today, assetId),
            r => new UsageCell(DateOnly.FromDateTime(r.GetDateTime(0)), r.GetString(1), r.GetString(2), r.GetInt64(3)), ct);

    public async Task<IReadOnlyList<AssetUses>> TopAssetsAsync(DateOnly from, DateOnly to, DateOnly today, int limit, CancellationToken ct) =>
        await RawSql.QueryAsync(db, UsageCte(false) + " SELECT asset_id, sum(uses)::bigint AS n FROM u GROUP BY asset_id ORDER BY n DESC, asset_id LIMIT @limit",
            p => { BindUsage(p, from, to, today); p.Bind("limit", NpgsqlDbType.Integer, limit); },
            r => new AssetUses(r.GetGuid(0), r.GetInt64(1)), ct);

    public async Task<ReuseAggregate> ReuseAsync(DateOnly from, DateOnly to, DateOnly today, CancellationToken ct)
    {
        // Live assets only: a purged asset is no longer part of the library whose reuse is being measured.
        var rows = await RawSql.QueryAsync(db, UsageCte(false) + """
            , per_asset AS (
                SELECT u.asset_id, sum(u.uses) AS uses, count(DISTINCT nullif(u.channel, '')) AS channels
                FROM u JOIN assets a ON a.id = u.asset_id AND a.status <> 'deleted'
                GROUP BY u.asset_id)
            SELECT count(*)::bigint, (count(*) FILTER (WHERE uses >= 2))::bigint, coalesce(sum(uses), 0)::bigint, (count(*) FILTER (WHERE channels >= 2))::bigint
            FROM per_asset
            """, p => BindUsage(p, from, to, today), r => new ReuseAggregate(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3)), ct);
        return rows[0];
    }

    public async Task<IReadOnlyList<RecentUse>> RecentUsesAsync(Guid assetId, int limit, CancellationToken ct) =>
        await RawSql.QueryAsync(db, """
            SELECT id, actor_id, event_type, channel, occurred_at FROM usage_events
            WHERE tenant_id = @tenant AND asset_id = @asset ORDER BY occurred_at DESC, id DESC LIMIT @limit
            """, p =>
        {
            p.Bind("tenant", NpgsqlDbType.Uuid, tenant.TenantId);
            p.Bind("asset", NpgsqlDbType.Uuid, assetId);
            p.Bind("limit", NpgsqlDbType.Integer, limit);
        }, r => new RecentUse(r.GetGuid(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetFieldValue<DateTimeOffset>(4)), ct);

    public async Task<AssetUsageTotals> AssetTotalsAsync(Guid assetId, CancellationToken ct)
    {
        var rows = await RawSql.QueryAsync(db, "SELECT use_count, first_used_at, last_used_at FROM asset_usage WHERE tenant_id = @tenant AND asset_id = @asset",
            p => { p.Bind("tenant", NpgsqlDbType.Uuid, tenant.TenantId); p.Bind("asset", NpgsqlDbType.Uuid, assetId); },
            r => new AssetUsageTotals(r.GetInt64(0), r.GetFieldValue<DateTimeOffset>(1), r.GetFieldValue<DateTimeOffset>(2)), ct);
        return rows.Count == 0 ? new AssetUsageTotals(0, null, null) : rows[0];
    }

    // ---------------------------------------------------------------------------------------------------- KPIs

    public async Task<PublishingAggregate> PublishingAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        // Hours from creation to first publication (never negative: an imported asset can be older than its record). First pass: no workflow task of the asset
        // was rejected before it was first published; an asset published without a workflow has no tasks, so it counts as first pass.
        var rows = await RawSql.QueryAsync(db, """
            SELECT count(*)::bigint,
                   percentile_cont(0.5) WITHIN GROUP (ORDER BY h),
                   percentile_cont(0.9) WITHIN GROUP (ORDER BY h),
                   (count(*) FILTER (WHERE first_pass))::bigint
            FROM (
                SELECT greatest(extract(epoch FROM (a.first_published_at - a.created_at)), 0)::float8 / 3600.0 AS h,
                       NOT EXISTS (SELECT 1 FROM workflow_tasks t WHERE t.tenant_id = a.tenant_id AND t.asset_id = a.id
                                   AND t.status = 'rejected' AND t.decided_at <= a.first_published_at) AS first_pass
                FROM assets a
                WHERE a.tenant_id = @tenant AND a.status <> 'deleted' AND a.first_published_at >= @from AND a.first_published_at < @to
            ) x
            """, p => BindRange(p, from, to),
            r => new PublishingAggregate(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetDouble(1), r.IsDBNull(2) ? null : r.GetDouble(2), r.GetInt64(3)), ct);
        return rows[0];
    }

    public async Task<DuplicateAggregate> DuplicatesAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        // Uploads that finished: stored (ingested) or recognised as an existing file and not stored again (duplicate).
        var rows = await RawSql.QueryAsync(db, """
            SELECT (count(*) FILTER (WHERE status IN ('Ingested', 'Duplicate')))::bigint,
                   (count(*) FILTER (WHERE status = 'Duplicate'))::bigint,
                   coalesce(sum(size_bytes) FILTER (WHERE status = 'Duplicate'), 0)::bigint
            FROM uploads WHERE tenant_id = @tenant AND created_at >= @from AND created_at < @to
            """, p => BindRange(p, from, to), r => new DuplicateAggregate(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2)), ct);
        return rows[0];
    }

    public async Task<long> DuplicateAssetsAsync(DateTimeOffset? createdBefore, CancellationToken ct) =>
        await RawSql.ScalarAsync(db, $"""
            SELECT coalesce(sum(n), 0)::bigint FROM (
                SELECT count(*) AS n FROM assets
                WHERE tenant_id = @tenant AND status <> 'deleted' AND checksum_sha256 IS NOT NULL{(createdBefore is null ? "" : " AND created_at < @before")}
                GROUP BY checksum_sha256 HAVING count(*) > 1) g
            """, p =>
        {
            p.Bind("tenant", NpgsqlDbType.Uuid, tenant.TenantId);
            if (createdBefore is { } b) p.Bind("before", NpgsqlDbType.TimestampTz, b.UtcDateTime);
        }, RawSql.Long, ct);

    public async Task<SlaAggregate> SlaAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var rows = await RawSql.QueryAsync(db, """
            SELECT (count(*) FILTER (WHERE status IN ('approved', 'rejected') AND due_at IS NOT NULL AND decided_at >= @from AND decided_at < @to))::bigint,
                   (count(*) FILTER (WHERE status IN ('approved', 'rejected') AND due_at IS NOT NULL AND decided_at >= @from AND decided_at < @to AND decided_at <= due_at))::bigint,
                   (count(*) FILTER (WHERE sla_breached_at >= @from AND sla_breached_at < @to))::bigint
            FROM workflow_tasks
            WHERE tenant_id = @tenant AND ((decided_at >= @from AND decided_at < @to) OR (sla_breached_at >= @from AND sla_breached_at < @to))
            """, p => BindRange(p, from, to), r => new SlaAggregate(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2)), ct);
        return rows[0];
    }

    public async Task<long> OverdueOpenTasksAsync(DateTimeOffset now, CancellationToken ct) =>
        await RawSql.ScalarAsync(db, "SELECT count(*)::bigint FROM workflow_tasks WHERE tenant_id = @tenant AND status = 'pending' AND due_at IS NOT NULL AND due_at < @now",
            p => { p.Bind("tenant", NpgsqlDbType.Uuid, tenant.TenantId); p.Bind("now", NpgsqlDbType.TimestampTz, now.UtcDateTime); }, RawSql.Long, ct);

    public async Task<IReadOnlyList<StepSla>> SlaByStepAsync(DateTimeOffset from, DateTimeOffset to, DateTimeOffset now, CancellationToken ct) =>
        await RawSql.QueryAsync(db, """
            SELECT i.definition_name, t.step_name,
                   (count(*) FILTER (WHERE t.status IN ('approved', 'rejected') AND t.due_at IS NOT NULL AND t.decided_at >= @from AND t.decided_at < @to))::bigint,
                   (count(*) FILTER (WHERE t.status IN ('approved', 'rejected') AND t.due_at IS NOT NULL AND t.decided_at >= @from AND t.decided_at < @to AND t.decided_at <= t.due_at))::bigint,
                   (count(*) FILTER (WHERE t.sla_breached_at >= @from AND t.sla_breached_at < @to))::bigint,
                   (count(*) FILTER (WHERE t.status = 'pending' AND t.due_at IS NOT NULL AND t.due_at < @now))::bigint
            FROM workflow_tasks t JOIN workflow_instances i ON i.id = t.instance_id
            WHERE t.tenant_id = @tenant
              AND ((t.decided_at >= @from AND t.decided_at < @to) OR (t.sla_breached_at >= @from AND t.sla_breached_at < @to) OR (t.status = 'pending' AND t.due_at < @now))
            GROUP BY i.definition_name, t.step_name
            HAVING count(*) FILTER (WHERE t.status IN ('approved', 'rejected') AND t.due_at IS NOT NULL AND t.decided_at >= @from AND t.decided_at < @to) > 0
                OR count(*) FILTER (WHERE t.sla_breached_at >= @from AND t.sla_breached_at < @to) > 0
                OR count(*) FILTER (WHERE t.status = 'pending' AND t.due_at IS NOT NULL AND t.due_at < @now) > 0
            ORDER BY i.definition_name, t.step_name
            """, p => { BindRange(p, from, to); p.Bind("now", NpgsqlDbType.TimestampTz, now.UtcDateTime); },
            r => new StepSla(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5)), ct);

    // ---------------------------------------------------------------------------------------------------- storage

    public async Task<IReadOnlyList<FolderTypeStorage>> StorageAsync(CancellationToken ct) =>
        await RawSql.QueryAsync(db, """
            SELECT a.folder_id, a.type, count(*)::bigint, coalesce(sum(v.size_bytes), 0)::bigint
            FROM assets a LEFT JOIN asset_versions v ON v.id = a.current_version_id
            WHERE a.tenant_id = @tenant AND a.status <> 'deleted'
            GROUP BY a.folder_id, a.type
            """, p => p.Bind("tenant", NpgsqlDbType.Uuid, tenant.TenantId),
            r => new FolderTypeStorage(r.IsDBNull(0) ? null : r.GetGuid(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3)), ct);

    private void BindRange(NpgsqlParameterCollection p, DateTimeOffset from, DateTimeOffset to)
    {
        p.Bind("tenant", NpgsqlDbType.Uuid, tenant.TenantId);
        p.Bind("from", NpgsqlDbType.TimestampTz, from.UtcDateTime);
        p.Bind("to", NpgsqlDbType.TimestampTz, to.UtcDateTime);
    }
}
