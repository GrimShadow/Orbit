using System.Net;
using Npgsql;

namespace Dam.IntegrationTests;

/// <summary>
/// Rollups, partitions and retention (ADR-0037) against a real Postgres: the numbers the overview shows are the same before and after the rollup job has run, a back-dated
/// batch re-rolls its day without double counting, running the job twice changes nothing, today is read live, months get partitions and old ones are dropped only after
/// they were rolled up, and every new table is private to its tenant.
/// </summary>
[Collection("pg")]
public sealed class UsageRollupTests(PostgresFixture pg) : AnalyticsTestBase(pg)
{
    private static FakeClock Now() => new(DateTimeOffset.UtcNow);

    /// <summary>A library with a few assets and a spread of uses over today, yesterday, a week back and the period before.</summary>
    private async Task<(Guid Tenant, string Admin, Guid A, Guid B, Guid C)> Library()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var a = await Aged(admin, brand, 100, "Hero");
        var b = await Aged(admin, brand, 100, "Banner");
        var c = await Aged(admin, brand, 100, "Poster");
        var r = await Record(admin,
            Use(a, "download", 0, "console"), Use(a, "view", 0, "portal"),
            Use(a, "download", 1, "console"), Use(b, "play", 1, "signage-lobby"), Use(b, "play", 1, "signage-lobby"),
            Use(a, "share", 3), Use(c, "embed", 5, "portal"), Use(b, "play", 8, "signage-lobby"),
            Use(a, "download", 40, "console"), Use(b, "view", 35));
        Assert.Equal(HttpStatusCode.OK, r.Status);
        return (t, admin, a, b, c);
    }

    // ---- the same numbers before and after ---------------------------------------------------------------------

    [Fact]
    public async Task The_overview_is_the_same_before_and_after_the_rollup_runs()
    {
        var (t, admin, _, _, _) = await Library();
        var before = await UsageView(admin);
        var before7 = await UsageView(admin, 7);
        Assert.Equal(0, Count(t, "SELECT count(*) FROM usage_daily"));                 // nothing rolled up yet: the numbers come from the events
        Assert.Equal(7, Count(t, "SELECT count(*) FROM usage_rollup_dirty"));         // every day an event landed on: today, yesterday, and 3, 5, 8, 35 and 40 days back

        using var scheduler = AnalyticsScheduler(Now());
        await Rollup(scheduler);

        Assert.True(Count(t, "SELECT count(*) FROM usage_daily") > 0);
        Assert.Equal(1, Count(t, "SELECT count(*) FROM usage_rollup_dirty"));         // only today, which is read live and rolled up once it is over
        Assert.Equal(0, Count(t, $"SELECT count(*) FROM usage_daily WHERE day = {TodayUtc}"));
        Assert.Equal(before, await UsageView(admin));
        Assert.Equal(before7, await UsageView(admin, 7));
    }

    [Fact]
    public async Task The_rollup_has_the_right_rows_per_day_asset_type_and_channel()
    {
        var (t, admin, a, b, _) = await Library();
        using var scheduler = AnalyticsScheduler(Now());
        await Rollup(scheduler);

        Assert.Equal(2, Count(t, $"SELECT uses FROM usage_daily WHERE asset_id = '{b}' AND event_type = 'play' AND channel = 'signage-lobby' AND day = {TodayUtc} - 1"));
        Assert.Equal(1, Count(t, $"SELECT uses FROM usage_daily WHERE asset_id = '{a}' AND event_type = 'share' AND channel = '' AND day = {TodayUtc} - 3"));   // no channel is the empty string, never null
        Assert.Equal(0, Count(t, "SELECT count(*) FROM usage_daily WHERE channel IS NULL"));
        Assert.Equal(Count(t, $"SELECT count(*) FROM usage_events WHERE occurred_at < {TodayUtc}"), Count(t, "SELECT sum(uses) FROM usage_daily"));
        _ = admin;
    }

    [Fact]
    public async Task Running_the_rollup_again_changes_nothing()
    {
        var (t, admin, _, _, _) = await Library();
        using var scheduler = AnalyticsScheduler(Now());
        await Rollup(scheduler);
        var rows = Text($"SELECT string_agg(day || asset_id::text || event_type || channel || uses, ',' ORDER BY day, asset_id, event_type, channel) FROM usage_daily WHERE tenant_id = '{t}'");
        var view = await UsageView(admin);

        await Rollup(scheduler);
        await Recheck(scheduler);
        await Rollup(scheduler);

        Assert.Equal(rows, Text($"SELECT string_agg(day || asset_id::text || event_type || channel || uses, ',' ORDER BY day, asset_id, event_type, channel) FROM usage_daily WHERE tenant_id = '{t}'"));
        Assert.Equal(view, await UsageView(admin));
    }

    [Fact]
    public async Task A_back_dated_batch_re_rolls_its_day_without_counting_twice()
    {
        var (t, admin, a, b, _) = await Library();
        using var scheduler = AnalyticsScheduler(Now());
        await Rollup(scheduler);
        Assert.Equal(1, Count(t, $"SELECT sum(uses) FROM usage_daily WHERE day = {TodayUtc} - 3"));
        var totalBefore = (await GetOk($"{OverviewUrl}?days=30", admin)).GetProperty("usage").GetProperty("total").GetInt64();

        // a reporting system delivers its hour-old backlog for a day that has already been rolled up
        var late = await Record(admin, Use(a, "share", 3), Use(b, "download", 3, "portal"), Use(b, "download", 3, "portal"));
        Assert.Equal(3, late.Body.GetProperty("accepted").GetInt32());
        Assert.Equal(1, Count(t, $"SELECT count(*) FROM usage_rollup_dirty WHERE day = {TodayUtc} - 3"));      // marked again

        // the numbers are right at once, before the job runs: that day is read from its events
        Assert.Equal(totalBefore + 3, (await GetOk($"{OverviewUrl}?days=30", admin)).GetProperty("usage").GetProperty("total").GetInt64());
        var live = await UsageView(admin);

        await Rollup(scheduler);
        Assert.Equal(4, Count(t, $"SELECT sum(uses) FROM usage_daily WHERE day = {TodayUtc} - 3"));            // 1 + 3, not 1 + 4
        Assert.Equal(0, Count(t, $"SELECT count(*) FROM usage_rollup_dirty WHERE day = {TodayUtc} - 3"));
        Assert.Equal(live, await UsageView(admin));

        await Rollup(scheduler);
        Assert.Equal(4, Count(t, $"SELECT sum(uses) FROM usage_daily WHERE day = {TodayUtc} - 3"));
    }

    [Fact]
    public async Task Today_is_read_live_and_the_job_does_not_roll_it_up()
    {
        var (t, admin, a, _, _) = await Library();
        Assert.Equal(HttpStatusCode.OK, (await Record(admin, Use(a, "download", 0, "kiosk"))).Status);
        var live = await UsageView(admin);
        Assert.Contains("kiosk", live, StringComparison.Ordinal);

        using var scheduler = AnalyticsScheduler(Now());
        await Rollup(scheduler);

        Assert.Equal(0, Count(t, $"SELECT count(*) FROM usage_daily WHERE day = {TodayUtc}"));
        Assert.Equal(1, Count(t, $"SELECT count(*) FROM usage_rollup_dirty WHERE day = {TodayUtc}"));
        Assert.Equal(live, await UsageView(admin));
    }

    [Fact]
    public async Task Once_a_day_is_over_the_job_rolls_it_up_and_it_stops_being_read_live()
    {
        var (t, admin, _, _, _) = await Library();
        var view = await UsageView(admin);
        // the scheduler's clock is a day ahead: yesterday-from-its-point-of-view is today for the data
        using var tomorrow = AnalyticsScheduler(new FakeClock(DateTimeOffset.UtcNow.AddDays(1)));
        await Rollup(tomorrow);
        Assert.Equal(0, Count(t, "SELECT count(*) FROM usage_rollup_dirty"));
        Assert.True(Count(t, $"SELECT count(*) FROM usage_daily WHERE day = {TodayUtc}") > 0);
        Assert.Equal(view, await UsageView(admin));
    }

    [Fact]
    public async Task The_nightly_recheck_repairs_a_finished_day_nobody_marked()
    {
        var (t, admin, _, _, _) = await Library();
        using var scheduler = AnalyticsScheduler(Now());
        await Rollup(scheduler);
        var view = await UsageView(admin);

        // a rollup row goes wrong behind the system's back (a restored backup, a manual edit) and nothing marks the day
        Sql($"UPDATE usage_daily SET uses = uses + 50 WHERE tenant_id = '{t}' AND day = {TodayUtc} - 1");
        Assert.NotEqual(view, await UsageView(admin));

        await Rollup(scheduler);
        Assert.NotEqual(view, await UsageView(admin));            // the ordinary run trusts the rollup: only marked days are rebuilt
        await Recheck(scheduler);                                  // the 02:15 run rebuilds the last three finished days regardless
        Assert.Equal(view, await UsageView(admin));
        Sql($"UPDATE usage_daily SET uses = uses + 50 WHERE tenant_id = '{t}' AND day = {TodayUtc} - 8");
        await Recheck(scheduler);
        Assert.NotEqual(view, await UsageView(admin));            // ... but only those three days: older ones are left alone
    }

    [Fact]
    public async Task Rollups_that_run_while_events_keep_arriving_converge_on_the_events()
    {
        var (t, admin, a, b, _) = await Library();
        using var scheduler = AnalyticsScheduler(Now());
        using var stop = new CancellationTokenSource();
        var roller = Task.Run(async () => { while (!stop.IsCancellationRequested) await Rollup(scheduler); });

        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < 8; i++)
            {
                var r = await Record(admin, Use(w % 2 == 0 ? a : b, "download", 1, "wave-" + w), Use(a, "view", 2, "wave-" + w), Use(b, "play", 1 + i % 3));
                Assert.Equal(HttpStatusCode.OK, r.Status);
            }
        })).ToArray();
        await Task.WhenAll(writers);
        await stop.CancelAsync();
        await roller;

        await Rollup(scheduler);
        Assert.Equal(0, Count(t, $"SELECT count(*) FROM usage_rollup_dirty WHERE day < {TodayUtc}"));
        Assert.Equal(Count(t, $"SELECT count(*) FROM usage_events WHERE occurred_at < {TodayUtc}"), Count(t, "SELECT sum(uses) FROM usage_daily"));
        // and per day, not only in total
        Assert.Equal(0, Count(t, $"""
            SELECT count(*) FROM (
              SELECT (occurred_at AT TIME ZONE 'UTC')::date AS day, asset_id, event_type, coalesce(channel, '') AS channel, count(*) AS n FROM usage_events WHERE occurred_at < {TodayUtc} GROUP BY 1, 2, 3, 4
              EXCEPT SELECT day, asset_id, event_type, channel, uses FROM usage_daily) x
            """));
    }

    // ---- ingest bookkeeping ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_repeated_client_event_id_inside_one_batch_is_stored_once_and_its_key_is_kept()
    {
        var (t, admin, a, b, _) = await Library();
        var r = await Record(admin, Use(a, "download", 0, "x", "dup-1"), Use(b, "download", 0, "x", "dup-1"), Use(a, "download", 0, "x", "dup-2"), Use(a, "download", 0, "x"), Use(a, "download", 0, "x"));
        Assert.Equal((4, 1, 0), (r.Body.GetProperty("accepted").GetInt32(), r.Body.GetProperty("duplicates").GetInt32(), r.Body.GetProperty("rejected").GetInt32()));
        Assert.Equal(1, Count(t, "SELECT count(*) FROM usage_events WHERE client_event_id = 'dup-1'"));
        Assert.Equal(2, Count(t, "SELECT count(*) FROM usage_event_keys WHERE client_event_id IN ('dup-1', 'dup-2')"));

        var again = await Record(admin, Use(a, "download", 0, "x", "dup-1"), Use(a, "download", 0, "x", "dup-2"));
        Assert.Equal((0, 2), (again.Body.GetProperty("accepted").GetInt32(), again.Body.GetProperty("duplicates").GetInt32()));
        Assert.Equal(4, Count(t, "SELECT count(*) FROM usage_events WHERE channel = 'x'"));
    }

    [Fact]
    public async Task Concurrent_resends_of_the_same_batch_are_recorded_once()
    {
        var (t, admin, a, _, _) = await Library();
        var batch = Enumerable.Range(0, 40).Select(i => Use(a, "view", 0, "race", $"race-{i}")).ToArray();
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Record(admin, batch)));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.Status));
        Assert.Equal(40, results.Sum(r => r.Body.GetProperty("accepted").GetInt32()));
        Assert.Equal(200, results.Sum(r => r.Body.GetProperty("duplicates").GetInt32()));
        Assert.Equal(40, Count(t, "SELECT count(*) FROM usage_events WHERE channel = 'race'"));
        Assert.Equal(40, Count(t, "SELECT use_count FROM asset_usage WHERE asset_id = '" + a + "'") - 5);   // the library's own five uses of this asset are in the same summary
    }

    // ---- partitions --------------------------------------------------------------------------------------------

    private string PartitionOf(Guid tenant, string where) =>
        Text($"SELECT tableoid::regclass::text FROM usage_events WHERE tenant_id = '{tenant}' AND {where} LIMIT 1");

    [Fact]
    public async Task The_current_month_and_the_next_three_have_partitions_and_events_land_in_their_month()
    {
        var (t, admin, a, _, _) = await Library();
        var months = Text("SELECT string_agg(m::text, ',' ORDER BY m) FROM usage_events_partition_months() m");
        var thisMonth = DateTime.UtcNow;
        foreach (var i in Enumerable.Range(0, 4))
            Assert.Contains($"{thisMonth.AddMonths(i):yyyy-MM}-01", months, StringComparison.Ordinal);

        Assert.Equal($"usage_events_{thisMonth:yyyy_MM}", PartitionOf(t, $"occurred_at > now() - interval '1 minute' AND event_type = 'download' AND channel = 'console'"));
        var future = await Record(admin, UseAt(a, DateTimeOffset.UtcNow.AddMinutes(4), "view", "clock-skew"));
        Assert.Equal(HttpStatusCode.OK, future.Status);
        Assert.Equal(1, Count(t, "SELECT count(*) FROM usage_events WHERE channel = 'clock-skew'"));
    }

    [Fact]
    public async Task An_event_with_no_partition_is_kept_in_the_default_one_until_the_job_gives_its_month_a_partition()
    {
        var (t, admin, a, _, _) = await Library();
        var at = new DateTimeOffset(DateTime.UtcNow.AddYears(-4).AddDays(-40).Date.AddHours(10), TimeSpan.Zero);   // nobody has used this month
        var month = $"usage_events_{at:yyyy_MM}";
        Assert.Equal(0, Scalar($"SELECT count(*) FROM pg_class WHERE relname = '{month}'"));
        var r = await Record(admin, UseAt(a, at, "view", "archive"), UseAt(a, at.AddDays(1), "view", "archive"));
        Assert.Equal(2, r.Body.GetProperty("accepted").GetInt32());                                 // never refused, never lost
        Assert.Equal("usage_events_default", PartitionOf(t, "channel = 'archive'"));

        // the numbers include it all the same
        var asset = await GetOk($"/api/v1/analytics/assets/{a}?days=30", admin);
        Assert.Equal(2, Count(t, "SELECT count(*) FROM usage_events WHERE channel = 'archive'"));
        Assert.True(asset.GetProperty("allTimeTotal").GetInt64() >= 2);

        using var scheduler = AnalyticsScheduler(Now());
        await Partitions(scheduler);
        Assert.Equal(1, Scalar($"SELECT count(*) FROM pg_class WHERE relname = '{month}'"));
        Assert.Equal(month, PartitionOf(t, "occurred_at = '" + Iso(at) + "'"));                     // moved out of the default partition
        Assert.Equal(2, Count(t, "SELECT count(*) FROM usage_events WHERE channel = 'archive'"));   // none lost, none doubled
        Assert.Equal(0, Scalar($"SELECT count(*) FROM usage_events_default WHERE tenant_id = '{t}'"));

        await Partitions(scheduler);                                                                // idempotent
        Assert.Equal(2, Count(t, "SELECT count(*) FROM usage_events WHERE channel = 'archive'"));
    }

    [Fact]
    public async Task Old_months_are_dropped_only_after_they_were_rolled_up_and_their_history_stays()
    {
        var (t, admin, a, _, _) = await Library();
        var at = new DateTimeOffset(DateTime.UtcNow.AddMonths(-6).Date.AddHours(10), TimeSpan.Zero);
        var month = $"usage_events_{at:yyyy_MM}";
        using var scheduler = AnalyticsScheduler(Now());

        // the month exists (the job adopts what landed in the default partition) and holds three uses that have not been rolled up
        await Record(admin, UseAt(a, at, "play", "old-signage"), UseAt(a, at.AddHours(1), "play", "old-signage"), UseAt(a, at.AddHours(2), "download", "old-portal"));
        await Partitions(scheduler);
        Assert.Equal(1, Scalar($"SELECT count(*) FROM pg_class WHERE relname = '{month}'"));
        var dirtyDay = $"{at:yyyy-MM-dd}";
        Assert.Equal(1, Count(t, $"SELECT count(*) FROM usage_rollup_dirty WHERE day = '{dirtyDay}'"));

        // retention on (two months): the month is old enough to go, but a day in it still waits for its rollup, so it stays
        using var retaining = AnalyticsScheduler(Now(), retentionMonths: 2);
        await Partitions(retaining);
        Assert.Equal(1, Scalar($"SELECT count(*) FROM pg_class WHERE relname = '{month}'"));
        Assert.Equal(3, Count(t, "SELECT count(*) FROM usage_events WHERE channel LIKE 'old-%'"));

        // rolled up (every tenant's days: the job is global), the month is dropped and the history lives on in the rollup
        await Rollup(retaining);
        var before = await UsageView(admin, 365);
        Assert.Equal(3, Count(t, $"SELECT sum(uses) FROM usage_daily WHERE day = '{dirtyDay}'"));
        await Partitions(retaining);
        Assert.Equal(0, Scalar($"SELECT count(*) FROM pg_class WHERE relname = '{month}'"));
        Assert.Equal(0, Count(t, "SELECT count(*) FROM usage_events WHERE channel LIKE 'old-%'"));
        Assert.Equal(3, Count(t, $"SELECT sum(uses) FROM usage_daily WHERE day = '{dirtyDay}'"));
        Assert.Equal(before, await UsageView(admin, 365));                                          // the overview does not notice
        Assert.Equal(3, Count(t, $"SELECT use_count FROM asset_usage WHERE asset_id = '{a}'") - 5);  // the per-asset summary is not touched either

        // the recent months and the future ones are never dropped
        var thisMonth = DateTime.UtcNow;
        Assert.Equal(1, Scalar($"SELECT count(*) FROM pg_class WHERE relname = 'usage_events_{thisMonth:yyyy_MM}'"));
        Assert.Equal(1, Scalar($"SELECT count(*) FROM pg_class WHERE relname = 'usage_events_{thisMonth.AddMonths(3):yyyy_MM}'"));
    }

    [Fact]
    public async Task A_late_event_for_a_month_that_was_dropped_is_counted_but_does_not_erase_that_months_history()
    {
        var (t, admin, a, _, _) = await Library();
        var at = new DateTimeOffset(DateTime.UtcNow.AddMonths(-9).Date.AddHours(10), TimeSpan.Zero);
        using var scheduler = AnalyticsScheduler(Now());
        await Record(admin, UseAt(a, at, "view", "old-a"), UseAt(a, at, "view", "old-a"));
        await Partitions(scheduler);
        await Rollup(scheduler);
        using var retaining = AnalyticsScheduler(Now(), retentionMonths: 2);
        await Partitions(retaining);
        var day = $"{at:yyyy-MM-dd}";
        Assert.Equal(2, Count(t, $"SELECT sum(uses) FROM usage_daily WHERE day = '{day}'"));

        // an event for the dropped month turns up: it goes to the default partition, is marked, and the rollup must not rebuild the day from the one event it can see
        var late = await Record(admin, UseAt(a, at.AddHours(1), "view", "old-late"));
        Assert.Equal(1, late.Body.GetProperty("accepted").GetInt32());
        Assert.Equal(1, Count(t, $"SELECT count(*) FROM usage_rollup_dirty WHERE day = '{day}'"));
        await Rollup(retaining);
        Assert.Equal(2, Count(t, $"SELECT sum(uses) FROM usage_daily WHERE day = '{day}'"));       // history intact
        Assert.Equal(0, Count(t, $"SELECT count(*) FROM usage_rollup_dirty WHERE day = '{day}'"));  // and not stuck
        await Partitions(retaining);                                                                // the straggler is discarded with the rest of what is out of the window
        Assert.Equal(0, Scalar($"SELECT count(*) FROM usage_events_default WHERE tenant_id = '{t}' AND channel = 'old-late'"));
    }

    [Fact]
    public async Task Event_keys_older_than_the_retention_window_are_pruned_and_newer_ones_kept()
    {
        var (t, admin, a, _, _) = await Library();
        await Record(admin, Use(a, "view", 0, "keys", "fresh-key"), Use(a, "view", 0, "keys", "old-key"));
        Sql($"UPDATE usage_event_keys SET created_at = now() - interval '9 months' WHERE tenant_id = '{t}' AND client_event_id = 'old-key'");
        using var retaining = AnalyticsScheduler(Now(), retentionMonths: 2);
        await Partitions(retaining);
        Assert.Equal(0, Count(t, "SELECT count(*) FROM usage_event_keys WHERE client_event_id = 'old-key'"));
        Assert.Equal(1, Count(t, "SELECT count(*) FROM usage_event_keys WHERE client_event_id = 'fresh-key'"));
        // the events themselves are not touched by pruning keys
        Assert.Equal(2, Count(t, "SELECT count(*) FROM usage_events WHERE channel = 'keys'"));
    }

    [Fact]
    public async Task The_app_role_cannot_create_or_drop_partitions_or_read_them_directly()
    {
        var (t, _, _, _, _) = await Library();
        await using var conn = Pg.RawAppConnection(t);
        foreach (var sql in new[]
                 {
                     "SELECT usage_events_ensure_partition(current_date)", "SELECT usage_events_drop_partition(date '2020-01-01')",
                     "SELECT * FROM usage_events_default", $"SELECT * FROM usage_events_{DateTime.UtcNow:yyyy_MM}", "CREATE TABLE usage_events_2099_01 PARTITION OF usage_events FOR VALUES FROM ('2099-01-01') TO ('2099-02-01')",
                 })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            var ex = await Assert.ThrowsAnyAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Contains(ex.SqlState, new[] { "42501", "42809" });   // insufficient privilege (or not the owner)
        }
    }

    // ---- tenant isolation of the new tables -------------------------------------------------------------------

    [Fact]
    public async Task The_rollup_tables_are_private_to_their_tenant_even_for_raw_sql()
    {
        var (a, adminA, _, _, _) = await Library();
        var (b, _, _, _, _) = await Library();
        using var scheduler = AnalyticsScheduler(Now());
        await Rollup(scheduler);
        var assetOfA = Guid.Parse(Text($"SELECT asset_id::text FROM usage_daily WHERE tenant_id = '{a}' LIMIT 1"));

        foreach (var table in new[] { "usage_daily", "usage_rollup_dirty", "usage_event_keys", "usage_events", "asset_usage" })
        {
            Assert.True(Count(a, $"SELECT count(*) FROM {table}") > 0 || table == "usage_event_keys", table);
            using var conn = Pg.RawAppConnection(b);
            await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {table} WHERE tenant_id = '{a}'", conn);
            Assert.Equal(0L, await cmd.ExecuteScalarAsync());                                       // B's session cannot see A's rows
        }

        await using var other = Pg.RawAppConnection(b);
        foreach (var sql in new[]
                 {
                     $"INSERT INTO usage_daily (tenant_id, day, asset_id, event_type, channel, uses) VALUES ('{a}', current_date, '{assetOfA}', 'view', '', 1)",
                     $"INSERT INTO usage_rollup_dirty (tenant_id, day, marked_at) VALUES ('{a}', current_date - 400, now())",
                     $"INSERT INTO usage_event_keys (tenant_id, client_event_id, created_at) VALUES ('{a}', 'forged', now())",
                     $"INSERT INTO report_schedules (id, tenant_id, report, days, frequency, hour_utc, recipient_user_ids, enabled, sent_to, created_at, updated_at) VALUES (gen_random_uuid(), '{a}', 'kpis', 7, 'daily', 6, '{{}}', true, '{{}}', now(), now())",
                     $"DELETE FROM usage_daily WHERE tenant_id = '{a}'",
                 })
        {
            await using var cmd = new NpgsqlCommand(sql, other);
            if (sql.StartsWith("DELETE", StringComparison.Ordinal)) Assert.Equal(0, await cmd.ExecuteNonQueryAsync());   // RLS hides the rows: nothing is deleted
            else Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync())).SqlState);
        }
        Assert.True(Count(a, "SELECT count(*) FROM usage_daily") > 0);
        _ = adminA;
    }

    [Fact]
    public async Task Tenants_cannot_change_rollups_through_the_app_role_in_ways_the_app_never_does()
    {
        var (a, _, _, _, _) = await Library();
        using var scheduler = AnalyticsScheduler(Now());
        await Rollup(scheduler);
        await using var conn = Pg.RawAppConnection(a);
        // rollup rows are rebuilt by delete and insert, never edited; keys and events are append-only
        foreach (var sql in new[] { "UPDATE usage_daily SET uses = 0", "DELETE FROM usage_event_keys", "UPDATE usage_event_keys SET created_at = now()", "DELETE FROM usage_events", "UPDATE usage_events SET channel = 'x'" })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync())).SqlState);
        }
    }
}
