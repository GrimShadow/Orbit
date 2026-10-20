using Dam.Application;
using Dam.Application.Abstractions;
using Dam.Application.Assets;
using Dam.Domain.Analytics;
using Dam.Domain.Assets;
using Dam.Infrastructure.Messaging;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Dam.Infrastructure.Scheduling;

/// <summary>Analytics settings (env: DAM_USAGE_RETENTION_MONTHS).</summary>
public sealed class AnalyticsOptions
{
    /// <summary>With a value above 0, usage events older than this many full months (before the current one) are dropped, a month at a time and only once rolled up.
    /// 0, the default, keeps every event.</summary>
    public int RetentionMonths { get; set; }

    public static AnalyticsOptions Read(IConfiguration cfg) => new()
    {
        RetentionMonths = int.TryParse(cfg["DAM_USAGE_RETENTION_MONTHS"], out var m) && m > 0 ? Math.Min(m, 1200) : 0,
    };
}

/// <summary>
/// Keeps the month partitions of <c>usage_events</c> in order (ADR-0037 decision 1), through the system connection and its security-definer functions (the app roles cannot run
/// DDL): the current month and the next three always exist; months that strays landed in the default partition for are given a partition of their own; and, when
/// DAM_USAGE_RETENTION_MONTHS is above 0, whole months older than the window are dropped, but only after every one of their days has been rolled up (a month with a day still
/// marked for rollup is kept until the next run). Old event keys and strays older than the window go too. Daily by default (DAM_ANALYTICS_PARTITIONS_CRON) and shortly after start.
/// </summary>
public sealed class UsagePartitionsJob(NpgsqlDataSource systemDb, AnalyticsOptions options, IClock clock, ILogger<UsagePartitionsJob> log) : IScheduledJob
{
    public const string JobKey = "analytics.partitions";
    public string Key => JobKey;

    private const int Chunk = 50_000, MaxChunks = 100;

    public async Task RunAsync(IReadOnlyDictionary<string, string> data, CancellationToken ct)
    {
        var today = UsagePartitions.DayOf(clock.UtcNow);
        var horizon = UsagePartitions.RetentionHorizon(today, options.RetentionMonths);
        await using var conn = await systemDb.OpenConnectionAsync(ct);

        var created = 0;
        foreach (var month in UsagePartitions.Needed(today)) if (await EnsureAsync(conn, month, ct)) created++;

        // Events no month covered (a back-dated batch older than the partitions that exist) wait in the default partition; give their months a partition.
        foreach (var month in await ListAsync(conn, "usage_events_stray_months", ct))
            if ((horizon is null || month >= horizon) && await EnsureAsync(conn, month, ct)) created++;

        var dropped = 0;
        if (horizon is { } cutoff)
        {
            foreach (var month in await ListAsync(conn, "usage_events_partition_months", ct))
            {
                var next = UsagePartitions.NextMonth(month);
                if (next > cutoff) continue;
                if (await HasUnrolledDaysAsync(conn, month, next, ct))
                {
                    log.LogInformation("Usage events of {Month} are older than the retention window but not fully rolled up yet; kept until the next run", month);
                    continue;
                }
                try
                {
                    await using var drop = new NpgsqlCommand("SELECT usage_events_drop_partition(@m)", conn);
                    drop.Parameters.Add(new NpgsqlParameter("m", NpgsqlDbType.Date) { Value = month });
                    if (await drop.ExecuteScalarAsync(ct) is true) dropped++;
                }
                catch (PostgresException ex) { log.LogWarning(ex, "Could not drop the usage events of {Month}", month); }
            }
            await DiscardAsync(conn, cutoff, ct);
            await PruneKeysAsync(conn, UsagePartitions.StartOf(cutoff), ct);
        }

        if (created > 0 || dropped > 0) log.LogInformation("Usage partitions: created {Created}, dropped {Dropped}", created, dropped);
    }

    private async Task<bool> EnsureAsync(NpgsqlConnection conn, DateOnly month, CancellationToken ct)
    {
        try
        {
            await using var cmd = new NpgsqlCommand("SELECT usage_events_ensure_partition(@m)", conn);
            cmd.Parameters.Add(new NpgsqlParameter("m", NpgsqlDbType.Date) { Value = month });
            return await cmd.ExecuteScalarAsync(ct) is true;
        }
        catch (PostgresException ex)
        {
            log.LogWarning(ex, "Could not create the usage events partition of {Month}; it is tried again at the next run", month);
            return false;
        }
    }

    /// <summary>The dates a set-returning maintenance function lists (<paramref name="function"/> is one of this job's own constants, never user input).</summary>
    private static async Task<List<DateOnly>> ListAsync(NpgsqlConnection conn, string function, CancellationToken ct)
    {
        var months = new List<DateOnly>();
        // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli -- the function name is one of this job's own constants.
        await using var cmd = new NpgsqlCommand($"SELECT * FROM {function}()", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) months.Add(DateOnly.FromDateTime(r.GetDateTime(0)));
        return months;
    }

    private static async Task<bool> HasUnrolledDaysAsync(NpgsqlConnection conn, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM usage_rollup_dirty WHERE day >= @from AND day < @to)", conn);
        cmd.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.Date) { Value = from });
        cmd.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.Date) { Value = to });
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    private static async Task DiscardAsync(NpgsqlConnection conn, DateOnly before, CancellationToken ct)
    {
        for (var i = 0; i < MaxChunks; i++)
        {
            await using var cmd = new NpgsqlCommand("SELECT usage_events_discard_before(@d, @n)", conn);
            cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Date) { Value = before });
            cmd.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Integer) { Value = Chunk });
            if (Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) < Chunk) return;
        }
    }

    private static async Task PruneKeysAsync(NpgsqlConnection conn, DateTimeOffset before, CancellationToken ct)
    {
        for (var i = 0; i < MaxChunks; i++)
        {
            await using var cmd = new NpgsqlCommand(
                "DELETE FROM usage_event_keys WHERE ctid = ANY (ARRAY (SELECT ctid FROM usage_event_keys WHERE created_at < @before LIMIT @n))", conn);
            cmd.Parameters.Add(new NpgsqlParameter("before", NpgsqlDbType.TimestampTz) { Value = before.UtcDateTime });
            cmd.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Integer) { Value = Chunk });
            if (await cmd.ExecuteNonQueryAsync(ct) < Chunk) return;
        }
    }
}

/// <summary>
/// Rolls usage events up into <c>usage_daily</c> (ADR-0037 decision 3). Every ten minutes (DAM_ANALYTICS_ROLLUP_CRON) it rebuilds each finished tenant-day whose events changed
/// since it was last rolled up (a mark ingest leaves, including back-dated batches), one transaction per tenant-day. At 02:15 UTC (DAM_ANALYTICS_RECHECK_CRON, data mode=recheck)
/// it rebuilds the last three finished days of every tenant whether marked or not, as a safety net. Today is never rolled up: reads take it live.
/// </summary>
public sealed class UsageRollupJob(IServiceScopeFactory scopes, NpgsqlDataSource systemDb, AnalyticsOptions options, IClock clock, ILogger<UsageRollupJob> log)
    : CrossTenantJob(scopes, systemDb, log), IScheduledJob
{
    public const string JobKey = "analytics.rollup";
    public const string RecheckMode = "recheck";
    public string Key => JobKey;

    private const int Batch = 500, MaxPasses = 20;
    private const int RecheckDays = 3;

    private sealed record TenantDay(Guid TenantId, DateOnly Day);

    public async Task RunAsync(IReadOnlyDictionary<string, string> data, CancellationToken ct)
    {
        var today = UsagePartitions.DayOf(clock.UtcNow);
        var horizon = UsagePartitions.RetentionHorizon(today, options.RetentionMonths);
        var rolled = 0;

        if (data.GetValueOrDefault("mode") == RecheckMode)
        {
            foreach (var tenant in await FindAsync("SELECT id, id FROM tenants", ct))
                for (var back = RecheckDays; back >= 1; back--)
                    if (await RollupAsync(new TenantDay(tenant.TenantId, today.AddDays(-back)), horizon, ct)) rolled++;
        }
        else
        {
            for (var pass = 0; pass < MaxPasses; pass++)
            {
                var dirty = await QueryAsync("SELECT tenant_id, day FROM usage_rollup_dirty WHERE day < @today ORDER BY day, tenant_id LIMIT " + Batch,
                    new Dictionary<string, object> { ["today"] = today }, r => new TenantDay(r.GetGuid(0), DateOnly.FromDateTime(r.GetDateTime(1))), ct);
                foreach (var item in dirty) if (await RollupAsync(item, horizon, ct)) rolled++;
                if (dirty.Count < Batch) break;
            }
        }
        if (rolled > 0) log.LogInformation("Usage rollup: rebuilt {Days} tenant-day(s)", rolled);
    }

    private async Task<bool> RollupAsync(TenantDay item, DateOnly? horizon, CancellationToken ct)
    {
        try
        {
            await using var scope = Scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<MutableTenantContext>().TenantId = item.TenantId;
            return await scope.ServiceProvider.GetRequiredService<IUsageRollup>().RollupDayAsync(item.Day, horizon, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Could not roll up {Day} for tenant {Tenant}; it is tried again at the next run", item.Day, item.TenantId);
            return false;
        }
    }
}

/// <summary>
/// Emails the scheduled reports that have come due (ADR-0037 decision 8), every fifteen minutes by default (DAM_ANALYTICS_REPORTS_CRON). Each schedule runs in its tenant's scope;
/// every recipient gets the report as they may see it. A recipient is recorded on the schedule as soon as their email was accepted, so a run that fails halfway, or a
/// scheduler that restarts, picks up with the others and never mails anyone twice for the same slot. A slot that still cannot be delivered 24 hours after it was due
/// is given up and the schedule moves on. With no SMTP server configured nothing is sent and the schedule says so.
/// </summary>
public sealed class ReportSchedulesJob(IServiceScopeFactory scopes, NpgsqlDataSource systemDb, IClock clock, ILogger<ReportSchedulesJob> log)
    : CrossTenantJob(scopes, systemDb, log), IScheduledJob
{
    public const string JobKey = "analytics.reports";
    public string Key => JobKey;

    public async Task RunAsync(IReadOnlyDictionary<string, string> data, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var due = await FindAsync("SELECT id, tenant_id FROM report_schedules WHERE enabled AND next_run_at IS NOT NULL AND next_run_at <= @now ORDER BY next_run_at LIMIT 200",
            new Dictionary<string, object> { ["now"] = now.UtcDateTime }, ct);
        foreach (var lead in due)
        {
            try { await RunScheduleAsync(lead.TenantId, lead.AssetId, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Scheduled report {Schedule} of tenant {Tenant} failed", lead.AssetId, lead.TenantId);
            }
        }
    }

    /// <summary>Runs one schedule if it is still due. Public so tests (and an operator's console) can drive a single schedule.</summary>
    public async Task RunScheduleAsync(Guid tenantId, Guid scheduleId, CancellationToken ct)
    {
        await using var scope = Scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<MutableTenantContext>().TenantId = tenantId;
        var db = sp.GetRequiredService<DamDbContext>();

        // Hold a session lock on this schedule while it is being sent, so two scheduler hosts cannot both send it.
        await db.Database.OpenConnectionAsync(ct);
        var locked = false;
        try
        {
            locked = await RawSql.ScalarAsync(db, "SELECT pg_try_advisory_lock(hashtextextended(@key, 0))", p => p.Bind("key", NpgsqlDbType.Text, "report_schedule:" + scheduleId.ToString("N")),
                v => v is true, ct);
            if (!locked) return;

            var now = clock.UtcNow;
            var schedule = await db.ReportSchedules.FirstOrDefaultAsync(s => s.Id == scheduleId, ct);
            if (schedule is null || !schedule.IsDue(now)) return;

            if (schedule.IsStale(now))
            {
                schedule.GiveUp(now, schedule.LastError ?? "The report could not be delivered in time.");
                await db.SaveChangesAsync(ct);
                return;
            }

            var mailer = sp.GetRequiredService<ReportMailer>();
            var outcome = await mailer.SendAsync(schedule, schedule.SentTo, async (id, c) =>
            {
                schedule.RecordSentTo(id);
                await db.SaveChangesAsync(c);
            }, ct);

            if (outcome.Failed > 0 || (outcome.Error is not null && outcome.Sent == 0)) schedule.RecordFailure(outcome.Error ?? "The report could not be sent.");
            else schedule.CompleteRun(now, outcome.Note);
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            if (locked)
                await RawSql.ExecuteAsync(db, "SELECT pg_advisory_unlock(hashtextextended(@key, 0))", p => p.Bind("key", NpgsqlDbType.Text, "report_schedule:" + scheduleId.ToString("N")), CancellationToken.None);
            await db.Database.CloseConnectionAsync();
        }
    }
}

public static class AnalyticsSchedulingRegistration
{
    /// <summary>Registers the partition, rollup and report jobs and what they need. Needs <c>AddDamRetentionScheduling</c> (the system connection) and <c>AddDamNotifications</c> (email).</summary>
    public static IServiceCollection AddDamAnalyticsJobs(this IServiceCollection services, IConfiguration cfg)
    {
        services.AddSingleton(AnalyticsOptions.Read(cfg));
        services.AddDamAnalytics();
        services.AddScheduledJob<UsagePartitionsJob>();
        services.AddScheduledJob<UsageRollupJob>();
        services.AddScheduledJob<ReportSchedulesJob>();
        return services;
    }

    /// <summary>Schedules the three jobs: partitions daily at 01:30 UTC (and shortly after start), rollup every ten minutes plus a re-check at 02:15 UTC, scheduled reports every
    /// fifteen minutes. Each cron can be overridden (DAM_ANALYTICS_PARTITIONS_CRON, DAM_ANALYTICS_ROLLUP_CRON, DAM_ANALYTICS_RECHECK_CRON, DAM_ANALYTICS_REPORTS_CRON; Quartz cron).</summary>
    public static async Task ScheduleAnalyticsJobsAsync(this IJobScheduler scheduler, IConfiguration cfg)
    {
        await scheduler.ScheduleCronAsync("analytics.partitions", UsagePartitionsJob.JobKey, cfg["DAM_ANALYTICS_PARTITIONS_CRON"] ?? "0 30 1 * * ?");
        await scheduler.ScheduleOnceAsync("analytics.partitions.start", UsagePartitionsJob.JobKey, DateTimeOffset.UtcNow.AddSeconds(30));
        await scheduler.ScheduleCronAsync("analytics.rollup", UsageRollupJob.JobKey, cfg["DAM_ANALYTICS_ROLLUP_CRON"] ?? "0 0/10 * * * ?");
        await scheduler.ScheduleCronAsync("analytics.rollup.recheck", UsageRollupJob.JobKey, cfg["DAM_ANALYTICS_RECHECK_CRON"] ?? "0 15 2 * * ?",
            new Dictionary<string, string> { ["mode"] = UsageRollupJob.RecheckMode });
        await scheduler.ScheduleCronAsync("analytics.reports", ReportSchedulesJob.JobKey, cfg["DAM_ANALYTICS_REPORTS_CRON"] ?? "0 0/15 * * * ?");
    }
}
