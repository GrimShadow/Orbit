using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Dam.IntegrationTests;

/// <summary>
/// The four BRD KPIs (ADR-0037 decision 5) on hand-built data: publishing effort (median and 90th percentile hours, first-pass rate), duplicate reduction, reuse and workflow SLA
/// attainment, each against the previous period, with the period boundaries, empty answers (null ratios, never 0%), tenant isolation and who may ask.
/// </summary>
[Collection("pg")]
public sealed class KpiTests(PostgresFixture pg) : AnalyticsTestBase(pg)
{
    private static DateTimeOffset StartOfToday => new(DateTime.UtcNow.Date, TimeSpan.Zero);
    private static DateTimeOffset Ago(int days, int hours = 0) => DateTimeOffset.UtcNow.AddDays(-days).AddHours(-hours);

    private static string Sqlt(DateTimeOffset t) => $"'{t.UtcDateTime:yyyy-MM-dd HH:mm:ss.ffffff}+00'";

    private Guid InsertAsset(Guid tenant, string status = "published", DateTimeOffset? createdAt = null, DateTimeOffset? firstPublished = null, string? checksum = null)
    {
        var id = Guid.NewGuid();
        Sql("INSERT INTO assets (id, tenant_id, type, status, title, metadata, metadata_schema_version, taxonomy_terms, tags, attributes, metadata_attributes, rights_channels, rights_regions, legal_hold, status_reason, created_at, updated_at, first_published_at, checksum_sha256) "
            + $"VALUES ('{id}', '{tenant}', 'image', '{status}', 'Acme asset', '{{}}', 0, '{{}}', '{{}}', '{{}}', '{{}}', '{{}}', '{{}}', false, '', {Sqlt(createdAt ?? Ago(200))}, now(), "
            + $"{(firstPublished is { } f ? Sqlt(f) : "NULL")}, {(checksum is null ? "NULL" : $"repeat('{checksum}', 64)")})");
        return id;
    }

    private void InsertUpload(Guid tenant, Guid asset, string status, DateTimeOffset createdAt, long? size = null) => Sql(
        "INSERT INTO uploads (id, tenant_id, asset_id, status, file_name, declared_size, content_type, bucket, storage_key, s3upload_id, part_size, part_count, allow_duplicate, error, created_at, expires_at, size_bytes) "
        + $"VALUES (gen_random_uuid(), '{tenant}', '{asset}', '{status}', 'a.png', 1, 'image/png', 'dam-quarantine', gen_random_uuid()::text, 'x', 5242880, 1, false, '', {Sqlt(createdAt)}, {Sqlt(createdAt.AddDays(1))}, {(size is { } s ? s.ToString(CultureInfo.InvariantCulture) : "NULL")})");

    private Guid InsertInstance(Guid tenant, Guid asset) 
    {
        var id = Guid.NewGuid();
        Sql("INSERT INTO workflow_instances (id, tenant_id, asset_id, definition_name, definition_json, distinct_approvers, status, started_at, rounds) "
            + $"VALUES ('{id}', '{tenant}', '{asset}', 'Acme review', '{{}}', false, 'approved', now() - interval '90 days', 1)");
        return id;
    }

    private void InsertTask(Guid tenant, Guid instance, Guid asset, string status, DateTimeOffset? due, DateTimeOffset? decided, DateTimeOffset? breached = null, string step = "review") => Sql(
        "INSERT INTO workflow_tasks (id, tenant_id, instance_id, asset_id, step_id, step_name, round, assignee_roles, assignee_user_ids, escalated_roles, escalated_user_ids, status, due_at, sla_breached_at, decided_at, created_at) "
        + $"VALUES (gen_random_uuid(), '{tenant}', '{instance}', '{asset}', '{step}', 'Review', 1, '{{}}', '{{}}', '{{}}', '{{}}', '{status}', {Opt(due)}, {Opt(breached)}, {Opt(decided)}, now() - interval '100 days')");

    private static string Opt(DateTimeOffset? t) => t is { } v ? Sqlt(v) : "NULL";

    private async Task<(Guid Tenant, string Admin, Guid Folder)> Scene()
    {
        var (t, admin, folder) = await TenantWithPacks();
        return (t, admin, folder);
    }

    private static JsonElement Block(JsonElement kpis, string name) => kpis.GetProperty(name);
    private static double? Num(JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : e.GetProperty(name).GetDouble();

    // ---- shape and periods -------------------------------------------------------------------------------------

    [Fact]
    public async Task The_answer_names_the_period_and_the_one_before_it()
    {
        var (_, admin, _) = await Scene();
        var k = await GetOk($"{KpisUrl}?days=7", admin);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.Equal((today.AddDays(-6).ToString("yyyy-MM-dd"), today.ToString("yyyy-MM-dd"), 7),
            (k.GetProperty("period").GetProperty("from").GetString(), k.GetProperty("period").GetProperty("to").GetString(), k.GetProperty("period").GetProperty("days").GetInt32()));
        Assert.Equal((today.AddDays(-13).ToString("yyyy-MM-dd"), today.AddDays(-7).ToString("yyyy-MM-dd")),
            (k.GetProperty("previous").GetProperty("from").GetString(), k.GetProperty("previous").GetProperty("to").GetString()));
        Assert.True(k.TryGetProperty("generatedAt", out _));
        Assert.Equal(30, (await GetOk(KpisUrl, admin)).GetProperty("period").GetProperty("days").GetInt32());   // 30 by default
    }

    [Fact]
    public async Task The_period_is_one_to_365_days()
    {
        var (_, admin, _) = await Scene();
        foreach (var ok in new[] { "1", "365" }) Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, $"{KpisUrl}?days={ok}", admin)).Status);
        foreach (var bad in new[] { "0", "366", "-5" }) Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Get, $"{KpisUrl}?days={bad}", admin)).Status);
    }

    [Fact]
    public async Task With_nothing_to_measure_ratios_are_null_not_zero_and_counts_are_zero()
    {
        var (_, admin, _) = await Scene();
        var k = await GetOk(KpisUrl, admin);
        var p = Block(k, "publishingEffort");
        Assert.Equal(0, p.GetProperty("publishedCount").GetInt64());
        Assert.Null(Num(p, "medianHours")); Assert.Null(Num(p, "p90Hours")); Assert.Null(Num(p, "firstPassRate"));
        var d = Block(k, "duplicates");
        Assert.Equal((0, 0, 0, 0), (d.GetProperty("uploads").GetInt64(), d.GetProperty("duplicatesPrevented").GetInt64(), d.GetProperty("bytesNotStored").GetInt64(), d.GetProperty("duplicateAssetsInLibrary").GetInt64()));
        Assert.Null(Num(d, "rate"));
        var r = Block(k, "reuse");
        Assert.Equal((0, 0, 0), (r.GetProperty("usedAssets").GetInt64(), r.GetProperty("reusedAssets").GetInt64(), r.GetProperty("multiChannelAssets").GetInt64()));
        Assert.Null(Num(r, "rate")); Assert.Null(Num(r, "avgUsesPerUsedAsset"));
        var s = Block(k, "workflowSla");
        Assert.Equal((0, 0, 0, 0), (s.GetProperty("decided").GetInt64(), s.GetProperty("onTime").GetInt64(), s.GetProperty("breached").GetInt64(), s.GetProperty("overdueOpen").GetInt64()));
        Assert.Null(Num(s, "attainment"));
        // ... in the previous period's figures too
        foreach (var block in new[] { "publishingEffort", "duplicates", "reuse", "workflowSla" })
            Assert.Equal(JsonValueKind.Object, Block(k, block).GetProperty("previous").ValueKind);
        Assert.Null(Num(Block(k, "publishingEffort").GetProperty("previous"), "medianHours"));
        Assert.Null(Num(Block(k, "duplicates").GetProperty("previous"), "rate"));
        Assert.Null(Num(Block(k, "reuse").GetProperty("previous"), "rate"));
        Assert.Null(Num(Block(k, "workflowSla").GetProperty("previous"), "attainment"));
        Assert.Equal(JsonValueKind.Null, Block(k, "workflowSla").GetProperty("previous").GetProperty("overdueOpen").ValueKind);   // a snapshot has no "previous"
    }

    // ---- publishing effort -------------------------------------------------------------------------------------

    [Fact]
    public async Task Publishing_effort_is_the_median_and_90th_percentile_of_hours_to_first_publication_and_the_first_pass_rate()
    {
        var (t, admin, _) = await Scene();
        // hours from creation to first publication: 2, 4, 10 and 1 (at the very start of the period): sorted 1, 2, 4, 10
        InsertAsset(t, createdAt: Ago(5, 2), firstPublished: Ago(5));
        InsertAsset(t, createdAt: Ago(10, 4), firstPublished: Ago(10));
        var rejected = InsertAsset(t, createdAt: Ago(15, 10), firstPublished: Ago(15));
        InsertAsset(t, createdAt: StartOfToday.AddDays(-29).AddHours(-1), firstPublished: StartOfToday.AddDays(-29));   // exactly the first instant of the period: inside
        // one rejection before it was published, so it did not go through first time
        var instance = InsertInstance(t, rejected);
        InsertTask(t, instance, rejected, "rejected", Ago(15, 6), Ago(15, 5));
        // not counted: a rejection AFTER the first publication (a later round), deleted assets, never published, and outside both periods
        var later = InsertAsset(t, createdAt: Ago(8, 3), firstPublished: Ago(8));
        InsertTask(t, InsertInstance(t, later), later, "rejected", null, Ago(2));
        InsertAsset(t, "deleted", Ago(9, 2), Ago(9));
        InsertAsset(t, "draft", Ago(9, 2), null);
        InsertAsset(t, createdAt: Ago(120, 5), firstPublished: Ago(100));
        InsertAsset(t, createdAt: StartOfToday.AddDays(-29).AddMinutes(-1), firstPublished: StartOfToday.AddDays(-29).AddMinutes(-1));   // a minute before the period: the previous one
        // the previous period (days 31 to 60 back): one asset, 6 hours
        InsertAsset(t, createdAt: Ago(40, 6), firstPublished: Ago(40));

        var p = Block(await GetOk(KpisUrl, admin), "publishingEffort");
        // 2, 4, 10, 1 and the "later rejection" asset's 3: sorted 1, 2, 3, 4, 10; median 3; p90 = 4 + 0.6 * 6 = 7.6
        Assert.Equal(5, p.GetProperty("publishedCount").GetInt64());
        Assert.Equal(3d, Num(p, "medianHours"));
        Assert.Equal(7.6, Num(p, "p90Hours"));
        Assert.Equal(0.8, Num(p, "firstPassRate"));     // 4 of 5: the one rejected before it was published is the only one that did not pass first time
        var before = p.GetProperty("previous");
        Assert.Equal(2, before.GetProperty("publishedCount").GetInt64());     // the 40-days-ago one and the one a minute before the period
        Assert.Equal(1.0, Num(before, "firstPassRate"));
    }

    [Fact]
    public async Task One_published_asset_has_the_same_median_and_90th_percentile_and_a_hundred_percent_first_pass_without_a_workflow()
    {
        var (t, admin, _) = await Scene();
        InsertAsset(t, createdAt: Ago(3, 7), firstPublished: Ago(3));
        var p = Block(await GetOk(KpisUrl, admin), "publishingEffort");
        Assert.Equal((1L, 7d, 7d, 1d), (p.GetProperty("publishedCount").GetInt64(), Num(p, "medianHours"), Num(p, "p90Hours"), Num(p, "firstPassRate")));
    }

    [Fact]
    public async Task Hours_are_never_negative_for_an_asset_older_in_the_system_than_its_record()
    {
        var (t, admin, _) = await Scene();
        InsertAsset(t, createdAt: Ago(1), firstPublished: Ago(2));        // imported with a publication date before it was created
        Assert.Equal(0d, Num(Block(await GetOk(KpisUrl, admin), "publishingEffort"), "medianHours"));
    }

    // ---- duplicate reduction -----------------------------------------------------------------------------------

    [Fact]
    public async Task Duplicate_reduction_counts_finished_uploads_and_those_recognised_as_existing_files()
    {
        var (t, admin, _) = await Scene();
        var asset = InsertAsset(t);
        foreach (var n in Enumerable.Range(0, 3)) InsertUpload(t, asset, "Ingested", Ago(2 + n), 1000);
        InsertUpload(t, asset, "Duplicate", Ago(3), 100);
        InsertUpload(t, asset, "Duplicate", Ago(12), 250);
        InsertUpload(t, asset, "Failed", Ago(4), 5);          // did not finish
        InsertUpload(t, asset, "Aborted", Ago(4));
        InsertUpload(t, asset, "Initiated", Ago(1));
        InsertUpload(t, asset, "Blocked", Ago(1), 7);
        InsertUpload(t, asset, "Duplicate", StartOfToday.AddDays(-29), 25);                      // the first instant of the period: inside
        // previous period: four stored and one recognised; and one long ago that belongs to neither
        foreach (var n in Enumerable.Range(0, 4)) InsertUpload(t, asset, "Ingested", Ago(35 + n), 10);
        InsertUpload(t, asset, "Duplicate", Ago(45), 40);
        InsertUpload(t, asset, "Duplicate", Ago(90), 999);
        // duplicates in the library: three live assets with one file (a fourth is deleted), two with another, one on its own, two with no file yet
        foreach (var _ in Enumerable.Range(0, 3)) InsertAsset(t, checksum: "a", createdAt: Ago(100));
        InsertAsset(t, "deleted", checksum: "a", createdAt: Ago(100));
        InsertAsset(t, checksum: "b", createdAt: Ago(100)); InsertAsset(t, checksum: "b", createdAt: Ago(100));
        InsertAsset(t, checksum: "c", createdAt: Ago(100));
        InsertAsset(t, createdAt: Ago(100)); InsertAsset(t, createdAt: Ago(100));

        var d = Block(await GetOk(KpisUrl, admin), "duplicates");
        Assert.Equal(6, d.GetProperty("uploads").GetInt64());                  // 3 stored + 3 recognised
        Assert.Equal(3, d.GetProperty("duplicatesPrevented").GetInt64());
        Assert.Equal(0.5, Num(d, "rate"));
        Assert.Equal(375, d.GetProperty("bytesNotStored").GetInt64());         // 100 + 250 + 25
        Assert.Equal(5, d.GetProperty("duplicateAssetsInLibrary").GetInt64());
        var before = d.GetProperty("previous");
        Assert.Equal((5L, 1L, 0.2, 40L), (before.GetProperty("uploads").GetInt64(), before.GetProperty("duplicatesPrevented").GetInt64(), Num(before, "rate"), before.GetProperty("bytesNotStored").GetInt64()));
    }

    [Fact]
    public async Task Duplicate_assets_in_the_library_are_counted_as_they_stood_for_the_previous_period()
    {
        var (t, admin, _) = await Scene();
        InsertAsset(t, checksum: "a", createdAt: Ago(100)); InsertAsset(t, checksum: "a", createdAt: Ago(80));    // already duplicates before the period
        InsertAsset(t, checksum: "b", createdAt: Ago(100)); InsertAsset(t, checksum: "b", createdAt: Ago(5));      // a duplicate made during it
        var d = Block(await GetOk(KpisUrl, admin), "duplicates");
        Assert.Equal(4, d.GetProperty("duplicateAssetsInLibrary").GetInt64());
        Assert.Equal(2, d.GetProperty("previous").GetProperty("duplicateAssetsInLibrary").GetInt64());
    }

    // ---- reuse -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Reuse_is_how_many_of_the_used_assets_were_used_again_and_on_how_many_channels()
    {
        var (t, admin, brand) = await Scene();
        var a = await Aged(admin, brand, 100, "A"); var b = await Aged(admin, brand, 100, "B"); var c = await Aged(admin, brand, 100, "C"); await Aged(admin, brand, 100, "D");
        await Record(admin,
            Use(a, "download", 0, "portal"), Use(a, "view", 2, "kiosk"), Use(a, "view", 3, "portal"),     // three uses on two channels
            Use(b, "view", 1),                                                                          // once, and on no channel
            Use(c, "play", 4, "kiosk"), Use(c, "play", 5, "kiosk"),                                     // twice on one channel
            Use(a, "view", 31, "portal"));                                                              // 31 days back: the previous period

        var k = await GetOk(KpisUrl, admin);
        var r = Block(k, "reuse");
        Assert.Equal((3L, 2L, 1L), (r.GetProperty("usedAssets").GetInt64(), r.GetProperty("reusedAssets").GetInt64(), r.GetProperty("multiChannelAssets").GetInt64()));
        Assert.Equal(0.6667, Num(r, "rate"));
        Assert.Equal(2d, Num(r, "avgUsesPerUsedAsset"));
        var before = r.GetProperty("previous");
        Assert.Equal((1L, 0L, 0L), (before.GetProperty("usedAssets").GetInt64(), before.GetProperty("reusedAssets").GetInt64(), before.GetProperty("multiChannelAssets").GetInt64()));
        Assert.Equal(0d, Num(before, "rate"));        // used, but never twice: a real zero, not "no data"
        Assert.Equal(1d, Num(before, "avgUsesPerUsedAsset"));

        // the same once the days have been rolled up, and today (not rolled up) is still counted
        using var scheduler = AnalyticsScheduler(new FakeClock(DateTimeOffset.UtcNow));
        await Rollup(scheduler);
        Assert.Equal(Block(k, "reuse").GetRawText(), Block(await GetOk(KpisUrl, admin), "reuse").GetRawText());
        _ = t;
    }

    [Fact]
    public async Task A_deleted_asset_is_not_part_of_the_reuse_figures()
    {
        var (_, admin, brand) = await Scene();
        var a = await Aged(admin, brand, 100, "A"); var gone = await Aged(admin, brand, 100, "Gone");
        await Record(admin, Use(a, "view", 1), Use(gone, "view", 1), Use(gone, "view", 2));
        Sql($"UPDATE assets SET status = 'deleted' WHERE id = '{gone}'");
        var r = Block(await GetOk(KpisUrl, admin), "reuse");
        Assert.Equal((1L, 0L), (r.GetProperty("usedAssets").GetInt64(), r.GetProperty("reusedAssets").GetInt64()));
    }

    // ---- workflow SLA ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Sla_attainment_is_the_share_of_due_tasks_decided_on_time_with_breaches_and_overdue_tasks_beside_it()
    {
        var (t, admin, _) = await Scene();
        var asset = InsertAsset(t);
        var wf = InsertInstance(t, asset);
        InsertTask(t, wf, asset, "approved", due: Ago(5, -2), decided: Ago(5));                    // decided two hours before it was due
        InsertTask(t, wf, asset, "approved", due: Ago(6), decided: Ago(5), breached: Ago(6));      // a day late, and breached
        var exactly = Ago(7);
        InsertTask(t, wf, asset, "rejected", due: exactly, decided: exactly);                      // at the very moment it was due: on time
        InsertTask(t, wf, asset, "approved", due: null, decided: Ago(5));                          // no due time: nothing to attain
        InsertTask(t, wf, asset, "pending", due: Ago(0, 1), decided: null);                        // open and an hour overdue
        InsertTask(t, wf, asset, "pending", due: Ago(0, -5), decided: null);                       // open, not yet due
        InsertTask(t, wf, asset, "approved", due: Ago(20), decided: Ago(19), breached: Ago(20));   // late and breached
        InsertTask(t, wf, asset, "cancelled", due: Ago(10), decided: null);                        // cancelled tasks are not decisions
        // the previous period: one on time
        InsertTask(t, wf, asset, "approved", due: Ago(40, -3), decided: Ago(40));
        // and one decided long ago
        InsertTask(t, wf, asset, "approved", due: Ago(100), decided: Ago(101));

        var s = Block(await GetOk(KpisUrl, admin), "workflowSla");
        Assert.Equal((4L, 2L), (s.GetProperty("decided").GetInt64(), s.GetProperty("onTime").GetInt64()));
        Assert.Equal(0.5, Num(s, "attainment"));
        Assert.Equal(2, s.GetProperty("breached").GetInt64());
        Assert.Equal(1, s.GetProperty("overdueOpen").GetInt64());
        var before = s.GetProperty("previous");
        Assert.Equal((1L, 1L, 1d, 0L), (before.GetProperty("decided").GetInt64(), before.GetProperty("onTime").GetInt64(), Num(before, "attainment"), before.GetProperty("breached").GetInt64()));
    }

    // ---- isolation and permission ------------------------------------------------------------------------------

    [Fact]
    public async Task One_tenants_figures_never_include_anothers()
    {
        var (a, adminA, _) = await Scene();
        var (b, adminB, brandB) = await Scene();
        var asset = InsertAsset(b, createdAt: Ago(2, 5), firstPublished: Ago(2), checksum: "f");
        InsertAsset(b, checksum: "f");
        InsertUpload(b, asset, "Duplicate", Ago(1), 500);
        InsertTask(b, InsertInstance(b, asset), asset, "approved", Ago(3, -1), Ago(3));
        var used = await Aged(adminB, brandB, 50, "B's");
        await Record(adminB, Use(used, "view", 1), Use(used, "view", 2));

        var mine = await GetOk(KpisUrl, adminA);
        Assert.Equal(0, Block(mine, "publishingEffort").GetProperty("publishedCount").GetInt64());
        Assert.Equal(0, Block(mine, "duplicates").GetProperty("uploads").GetInt64());
        Assert.Equal(0, Block(mine, "duplicates").GetProperty("duplicateAssetsInLibrary").GetInt64());
        Assert.Equal(0, Block(mine, "reuse").GetProperty("usedAssets").GetInt64());
        Assert.Equal(0, Block(mine, "workflowSla").GetProperty("decided").GetInt64());
        var theirs = await GetOk(KpisUrl, adminB);
        Assert.Equal(1, Block(theirs, "publishingEffort").GetProperty("publishedCount").GetInt64());
        Assert.Equal(1, Block(theirs, "duplicates").GetProperty("duplicatesPrevented").GetInt64());
        Assert.Equal(1, Block(theirs, "reuse").GetProperty("reusedAssets").GetInt64());
        Assert.Equal(1, Block(theirs, "workflowSla").GetProperty("onTime").GetInt64());
        _ = a;
    }

    [Theory]
    [InlineData("Admin", HttpStatusCode.OK)]
    [InlineData("Librarian", HttpStatusCode.OK)]
    [InlineData("Editor", HttpStatusCode.OK)]
    [InlineData("Approver", HttpStatusCode.OK)]
    [InlineData("Contributor", HttpStatusCode.Forbidden)]
    [InlineData("Viewer", HttpStatusCode.Forbidden)]
    public async Task Only_roles_that_may_read_analytics_may_read_the_kpis(string role, HttpStatusCode expected)
    {
        var (t, _, _) = await Scene();
        Assert.Equal(expected, (await Call(HttpMethod.Get, KpisUrl, As(t, role))).Status);
    }
}
