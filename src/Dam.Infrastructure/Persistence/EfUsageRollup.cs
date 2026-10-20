using Dam.Application.Abstractions;
using Dam.Domain.Assets;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace Dam.Infrastructure.Persistence;

/// <summary>
/// Rebuilds <c>usage_daily</c> for one tenant-day (ADR-0037). Delete and re-insert from the day's events in one transaction, so the result depends only on the events
/// (running it twice, or after a back-dated batch, never double counts), serialised per tenant-day by an advisory lock so two runs cannot interleave. The dirty
/// mark is removed only if it has not moved since it was read: an event that landed meanwhile bumps <c>mark_seq</c>, and the day stays marked for the next run.
/// </summary>
public sealed class EfUsageRollup(DamDbContext db, ITenantContext tenant) : IUsageRollup
{
    public async Task<bool> RollupDayAsync(DateOnly day, DateOnly? retentionHorizon, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await RawSql.ExecuteAsync(db, "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))",
            p => p.Bind("key", NpgsqlDbType.Text, $"usage_rollup:{tenant.TenantId:N}:{day:yyyy-MM-dd}"), ct);

        var seq = await RawSql.ScalarAsync(db, "SELECT mark_seq FROM usage_rollup_dirty WHERE tenant_id = @tenant AND day = @day",
            p => { p.Bind("tenant", NpgsqlDbType.Uuid, tenant.TenantId); p.Bind("day", NpgsqlDbType.Date, day); },
            v => v is null or DBNull ? (long?)null : Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture), ct);

        var recompute = true;
        if (retentionHorizon is { } horizon && day < horizon)
        {
            // Past the retention window: if its month's events have been dropped, recomputing from what is left would erase the day's history.
            var partition = "public." + UsagePartitions.NameFor(UsagePartitions.MonthStart(day));
            recompute = await RawSql.ScalarAsync(db, "SELECT to_regclass(@name) IS NOT NULL", p => p.Bind("name", NpgsqlDbType.Text, partition),
                v => v is true, ct);
        }

        if (recompute)
        {
            await RawSql.ExecuteAsync(db, "DELETE FROM usage_daily WHERE tenant_id = @tenant AND day = @day", Bind(day), ct);
            await RawSql.ExecuteAsync(db, """
                INSERT INTO usage_daily (tenant_id, day, asset_id, event_type, channel, uses)
                SELECT @tenant, @day, asset_id, event_type, coalesce(channel, ''), count(*)
                FROM usage_events
                WHERE tenant_id = @tenant AND occurred_at >= (@day::timestamp AT TIME ZONE 'UTC') AND occurred_at < ((@day + 1)::timestamp AT TIME ZONE 'UTC')
                GROUP BY asset_id, event_type, coalesce(channel, '')
                """, Bind(day), ct);
        }

        if (seq is { } covered)
            await RawSql.ExecuteAsync(db, "DELETE FROM usage_rollup_dirty WHERE tenant_id = @tenant AND day = @day AND mark_seq <= @seq",
                p => { Bind(day)(p); p.Bind("seq", NpgsqlDbType.Bigint, covered); }, ct);

        await tx.CommitAsync(ct);
        return recompute;
    }

    private Action<Npgsql.NpgsqlParameterCollection> Bind(DateOnly day) => p =>
    {
        p.Bind("tenant", NpgsqlDbType.Uuid, tenant.TenantId);
        p.Bind("day", NpgsqlDbType.Date, day);
    };
}
