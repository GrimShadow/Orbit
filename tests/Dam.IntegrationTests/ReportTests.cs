using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Dam.IntegrationTests;

/// <summary>
/// The report API and per-asset analytics (ADR-0037 decisions 6 and 7): the five reports as JSON and CSV, rows that name assets limited to what the reader may see, storage by
/// folder (with subtree totals) and type, the workflow SLA table, per-asset usage with a zero-filled series, and who may ask.
/// </summary>
[Collection("pg")]
public sealed class ReportTests(PostgresFixture pg) : AnalyticsTestBase(pg)
{
    private sealed record Scene(Guid Tenant, string Admin, Guid Brand, Guid Cars, Guid Other, Guid Hero, Guid Banner, Guid Secret, Guid IdleBrand, Guid IdleOther, Guid Loose);

    /// <summary>
    /// Brand holds Hero (used 3 times, 1,000 bytes) and an idle asset; Brand/Cars holds Banner (used once, 5,000 bytes); Other holds Secret (used 5 times, 2,000 bytes, the busiest
    /// of all) and an idle asset; Loose is in no folder.
    /// </summary>
    private async Task<Scene> NewScene()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var cars = await Folder(admin, "Cars", brand);
        var other = await Folder(admin, "Other");
        var hero = await Aged(admin, brand, 100, "=HYPERLINK(\"http://evil.example\")");     // a title a spreadsheet would run
        var banner = await Aged(admin, cars, 100, "Banner, \"large\"");
        var secret = await Aged(admin, other, 100, "Secret plans");
        var idleBrand = await Aged(admin, brand, 90, "Idle brand asset");
        var idleOther = await Aged(admin, other, 95, "Idle other asset");
        var loose = await Aged(admin, null, 100, "No folder", type: "video");
        AddVersion(hero, 1000); AddVersion(banner, 5000); AddVersion(secret, 2000); AddVersion(idleBrand, 300); AddVersion(loose, 7000);
        await Record(admin,
            Use(hero, "download", 0, "portal"), Use(hero, "view", 2, "kiosk"), Use(hero, "view", 4),
            Use(banner, "play", 1, "kiosk"),
            Use(secret, "view", 1), Use(secret, "view", 2), Use(secret, "view", 3), Use(secret, "view", 4), Use(secret, "view", 5));
        return new Scene(t, admin, brand, cars, other, hero, banner, secret, idleBrand, idleOther, loose);
    }

    private async Task<JsonElement> Run(string name, string token, string query = "")
    {
        var r = await Call(HttpMethod.Get, $"{ReportsUrl}/{name}{query}", token);
        Assert.True(r.Status == HttpStatusCode.OK, $"{name}{query} answered {(int)r.Status}: {r.Body}");
        return r.Body;
    }

    private static List<JsonElement> Rows(JsonElement report) => report.GetProperty("rows").EnumerateArray().ToList();
    private static string[] Keys(JsonElement report) => report.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("key").GetString()!).ToArray();

    private async Task<(HttpStatusCode Status, string ContentType, string Disposition, byte[] Body)> RawGet(string path, string token)
    {
        using var http = Api.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, path) { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) } };
        using var res = await http.SendAsync(req);
        return (res.StatusCode, res.Content.Headers.ContentType?.ToString() ?? "", res.Content.Headers.ContentDisposition?.ToString() ?? "", await res.Content.ReadAsByteArrayAsync());
    }

    // ---- the catalogue -----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_report_list_names_the_five_reports_and_says_which_name_assets()
    {
        var s = await NewScene();
        var list = (await GetOk(ReportsUrl, s.Admin)).EnumerateArray().ToList();
        Assert.Equal(["kpis", "top-assets", "unused-assets", "storage", "workflow-sla"], list.Select(r => r.GetProperty("name").GetString()));
        Assert.Equal([false, true, true, false, false], list.Select(r => r.GetProperty("namesAssets").GetBoolean()));
        var unused = list.Single(r => r.GetProperty("name").GetString() == "unused-assets");
        Assert.Equal((30, 3650), (unused.GetProperty("defaultDays").GetInt32(), unused.GetProperty("maxDays").GetInt32()));
        Assert.All(list, r => Assert.False(string.IsNullOrWhiteSpace(r.GetProperty("title").GetString())));
    }

    [Theory]
    [InlineData("Admin", HttpStatusCode.OK)]
    [InlineData("Librarian", HttpStatusCode.OK)]
    [InlineData("Editor", HttpStatusCode.OK)]
    [InlineData("Approver", HttpStatusCode.OK)]
    [InlineData("Contributor", HttpStatusCode.Forbidden)]
    [InlineData("Viewer", HttpStatusCode.Forbidden)]
    [InlineData("Dealer", HttpStatusCode.Forbidden)]
    public async Task Only_roles_that_may_read_analytics_may_list_and_run_reports(string role, HttpStatusCode expected)
    {
        var (t, _, _) = await TenantWithPacks();
        var token = role == "Dealer" ? ApiTestHost.TokenFor(t, Guid.NewGuid(), ["Dealer"], attributes: new() { ["region"] = ["North"] }) : As(t, role);
        Assert.Equal(expected, (await Call(HttpMethod.Get, ReportsUrl, token)).Status);
        foreach (var name in new[] { "kpis", "top-assets", "unused-assets", "storage", "workflow-sla" })
            Assert.Equal(expected, (await Call(HttpMethod.Get, $"{ReportsUrl}/{name}", token)).Status);
        Assert.Equal(expected, (await RawGet($"{ReportsUrl}/kpis?format=csv", token)).Status);
    }

    [Fact]
    public async Task Reports_need_a_token()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await RawGet(ReportsUrl, "not-a-token")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RawGet(ReportsUrl + "/kpis?format=csv", "not-a-token")).Status);
    }

    [Fact]
    public async Task An_unknown_report_a_bad_period_or_a_bad_format_is_refused()
    {
        var s = await NewScene();
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"{ReportsUrl}/revenue", s.Admin)).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Get, $"{ReportsUrl}/kpis?days=0", s.Admin)).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Get, $"{ReportsUrl}/kpis?days=366", s.Admin)).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Get, $"{ReportsUrl}/top-assets?days=366", s.Admin)).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Get, $"{ReportsUrl}/unused-assets?days=3651", s.Admin)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, $"{ReportsUrl}/unused-assets?days=3650", s.Admin)).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Get, $"{ReportsUrl}/kpis?format=xml", s.Admin)).Status);
        var bad = await Call(HttpMethod.Get, $"{ReportsUrl}/kpis?days=0", s.Admin);
        Assert.True(bad.Body.GetProperty("errors").TryGetProperty("days", out _));
    }

    // ---- kpis --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_kpi_report_lists_each_measure_with_this_period_and_the_previous()
    {
        var s = await NewScene();
        var report = await Run("kpis", s.Admin, "?days=30");
        Assert.Equal(["kpi", "metric", "value", "unit", "previous"], Keys(report));
        Assert.Equal("kpis", report.GetProperty("name").GetString());
        Assert.Equal(30, report.GetProperty("period").GetProperty("days").GetInt32());
        var rows = Rows(report);
        Assert.Equal(["Publishing effort", "Duplicate reduction", "Reuse", "Workflow SLA"], rows.Select(r => r.GetProperty("kpi").GetString()).Distinct());
        var used = rows.Single(r => r.GetProperty("metric").GetString() == "Assets used");
        Assert.Equal(3, used.GetProperty("value").GetInt64());                    // Hero, Banner and Secret
        Assert.Equal("assets", used.GetProperty("unit").GetString());
        var rate = rows.Single(r => r.GetProperty("metric").GetString() == "Reuse rate");
        Assert.Equal(66.7, rate.GetProperty("value").GetDouble());                 // percentages are shown as percentages, 0 to 100
        Assert.Equal("%", rate.GetProperty("unit").GetString());
        var median = rows.Single(r => r.GetProperty("metric").GetString() == "Median time to publish");
        Assert.Equal(JsonValueKind.Null, median.GetProperty("value").ValueKind);   // nothing published: no number, not zero
    }

    // ---- top assets and scope ----------------------------------------------------------------------------------

    [Fact]
    public async Task Top_assets_are_ranked_by_uses_and_a_reader_sees_only_assets_they_may_read()
    {
        var s = await NewScene();
        var all = Rows(await Run("top-assets", s.Admin));
        Assert.Equal(["Secret plans", "=HYPERLINK(\"http://evil.example\")", "Banner, \"large\""], all.Select(r => r.GetProperty("title").GetString()));
        Assert.Equal([1, 2, 3], all.Select(r => r.GetProperty("rank").GetInt32()));
        Assert.Equal([5, 3, 1], all.Select(r => r.GetProperty("uses").GetInt64()));
        Assert.Equal(s.Secret, all[0].GetProperty("assetId").GetGuid());

        var reporter = await ScopedReporter(s.Tenant, s.Admin, "brand");
        var mine = await Run("top-assets", reporter);
        Assert.Equal(["=HYPERLINK(\"http://evil.example\")", "Banner, \"large\""], Rows(mine).Select(r => r.GetProperty("title").GetString()));   // Secret plans (the busiest) is outside their folders
        Assert.Equal([1, 2], Rows(mine).Select(r => r.GetProperty("rank").GetInt32()));
        Assert.DoesNotContain("Secret", mine.GetRawText(), StringComparison.Ordinal);
        Assert.False(mine.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Top_assets_respect_the_period()
    {
        var s = await NewScene();
        var rows = Rows(await Run("top-assets", s.Admin, "?days=2"));       // today and yesterday
        Assert.Equal(new[] { "Secret plans", "Banner, \"large\"", "=HYPERLINK(\"http://evil.example\")" }.Order(), rows.Select(r => r.GetProperty("title").GetString()).Order());
        Assert.Equal(1, rows.Single(r => r.GetProperty("title").GetString() == "Secret plans").GetProperty("uses").GetInt64());
    }

    [Fact]
    public async Task Unused_assets_are_judged_from_their_last_use_and_a_reader_sees_only_assets_they_may_read()
    {
        var s = await NewScene();
        var report = await Run("unused-assets", s.Admin, "?days=60");
        Assert.Equal(["title", "type", "status", "folder", "lastUsedAt", "idleDays", "sizeBytes", "createdAt", "assetId"], Keys(report));
        var titles = Rows(report).Select(r => r.GetProperty("title").GetString()!).Order().ToList();
        Assert.Equal(new[] { "Idle brand asset", "Idle other asset", "No folder" }.Order(), titles);   // the three never used (all older than 60 days); the used ones are not listed
        var brandIdle = Rows(report).Single(r => r.GetProperty("title").GetString() == "Idle brand asset");
        Assert.Equal("Brand", brandIdle.GetProperty("folder").GetString());
        Assert.Equal(300, brandIdle.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(JsonValueKind.Null, brandIdle.GetProperty("lastUsedAt").ValueKind);
        Assert.InRange(brandIdle.GetProperty("idleDays").GetInt32(), 89, 91);

        var reporter = await ScopedReporter(s.Tenant, s.Admin, "brand");
        var mine = await Run("unused-assets", reporter, "?days=60");
        Assert.Equal(["Idle brand asset"], Rows(mine).Select(r => r.GetProperty("title").GetString()));
        Assert.DoesNotContain("Idle other", mine.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("No folder", mine.GetRawText(), StringComparison.Ordinal);

        // a longer window than the assets' age lists nothing
        Assert.Empty(Rows(await Run("unused-assets", s.Admin, "?days=365")));
    }

    // ---- storage -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Storage_is_reported_by_folder_with_subtree_totals_and_by_type()
    {
        var s = await NewScene();
        var report = await Run("storage", s.Admin);
        Assert.Equal(["group", "name", "assets", "bytes", "subtreeAssets", "subtreeBytes"], Keys(report));
        var rows = Rows(report);

        JsonElement Folder(string name) => rows.Single(r => r.GetProperty("group").GetString() == "folder" && r.GetProperty("name").GetString() == name);
        var brand = Folder("Brand");
        Assert.Equal((2, 1300L, 3, 6300L), (brand.GetProperty("assets").GetInt32(), brand.GetProperty("bytes").GetInt64(), brand.GetProperty("subtreeAssets").GetInt32(), brand.GetProperty("subtreeBytes").GetInt64()));
        var cars = Folder("Brand / Cars");
        Assert.Equal((1, 5000L, 1, 5000L), (cars.GetProperty("assets").GetInt32(), cars.GetProperty("bytes").GetInt64(), cars.GetProperty("subtreeAssets").GetInt32(), cars.GetProperty("subtreeBytes").GetInt64()));
        var other = Folder("Other");
        Assert.Equal((2, 2000L), (other.GetProperty("assets").GetInt32(), other.GetProperty("bytes").GetInt64()));
        var loose = Folder("(no folder)");
        Assert.Equal((1, 7000L), (loose.GetProperty("assets").GetInt32(), loose.GetProperty("bytes").GetInt64()));

        // parents come before their children
        var order = rows.Where(r => r.GetProperty("group").GetString() == "folder").Select(r => r.GetProperty("name").GetString()).ToList();
        Assert.True(order.IndexOf("Brand") < order.IndexOf("Brand / Cars"));

        var types = rows.Where(r => r.GetProperty("group").GetString() == "type").ToList();
        Assert.Equal(["image", "video"], types.Select(r => r.GetProperty("name").GetString()));            // largest first
        Assert.Equal((5, 8300L), (types[0].GetProperty("assets").GetInt32(), types[0].GetProperty("bytes").GetInt64()));
        Assert.Equal((1, 7000L), (types[1].GetProperty("assets").GetInt32(), types[1].GetProperty("bytes").GetInt64()));
        Assert.Equal(JsonValueKind.Null, types[0].GetProperty("subtreeAssets").ValueKind);
        // the two ways of adding the library up agree: by type, and by folder (a folder's own assets, plus the ones in no folder)
        Assert.Equal(15300L, types.Sum(r => r.GetProperty("bytes").GetInt64()));
        Assert.Equal(15300L, rows.Where(r => r.GetProperty("group").GetString() == "folder").Sum(r => r.GetProperty("bytes").GetInt64()));
    }

    [Fact]
    public async Task Deleted_assets_are_not_part_of_storage()
    {
        var s = await NewScene();
        Sql($"UPDATE assets SET status = 'deleted' WHERE id = '{s.Loose}'");
        var rows = Rows(await Run("storage", s.Admin));
        Assert.DoesNotContain(rows, r => r.GetProperty("name").GetString() == "(no folder)");
        Assert.DoesNotContain(rows, r => r.GetProperty("name").GetString() == "video");
    }

    // ---- workflow sla ------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_workflow_sla_report_has_a_row_per_workflow_step()
    {
        var s = await NewScene();
        var instance = Guid.NewGuid();
        Sql("INSERT INTO workflow_instances (id, tenant_id, asset_id, definition_name, definition_json, distinct_approvers, status, started_at, rounds) "
            + $"VALUES ('{instance}', '{s.Tenant}', '{s.Hero}', 'Acme review', '{{}}', false, 'approved', now() - interval '10 days', 1)");
        void Task(string step, string status, string due, string? decided, string? breached = null) => Sql(
            "INSERT INTO workflow_tasks (id, tenant_id, instance_id, asset_id, step_id, step_name, round, assignee_roles, assignee_user_ids, escalated_roles, escalated_user_ids, status, due_at, sla_breached_at, decided_at, created_at) "
            + $"VALUES (gen_random_uuid(), '{s.Tenant}', '{instance}', '{s.Hero}', '{step}', '{step}', 1, '{{}}', '{{}}', '{{}}', '{{}}', '{status}', now() + interval '{due}', {(breached is null ? "NULL" : $"now() + interval '{breached}'")}, {(decided is null ? "NULL" : $"now() + interval '{decided}'")}, now() - interval '10 days')");
        Task("Review", "approved", "-5 days", "-5 days -2 hours");
        Task("Review", "approved", "-4 days", "-3 days", "-4 days");
        Task("Final", "approved", "-2 days", "-2 days");
        Task("Final", "pending", "-1 hour", null);

        var report = await Run("workflow-sla", s.Admin);
        Assert.Equal(["workflow", "step", "decided", "onTime", "attainment", "breached", "overdueNow"], Keys(report));
        var rows = Rows(report);
        Assert.Equal(["Final", "Review"], rows.Select(r => r.GetProperty("step").GetString()));
        var review = rows.Single(r => r.GetProperty("step").GetString() == "Review");
        Assert.Equal((2, 1, 0.5, 1, 0), (review.GetProperty("decided").GetInt32(), review.GetProperty("onTime").GetInt32(), review.GetProperty("attainment").GetDouble(), review.GetProperty("breached").GetInt32(), review.GetProperty("overdueNow").GetInt32()));
        var final = rows.Single(r => r.GetProperty("step").GetString() == "Final");
        Assert.Equal((1, 1, 1d, 0, 1), (final.GetProperty("decided").GetInt32(), final.GetProperty("onTime").GetInt32(), final.GetProperty("attainment").GetDouble(), final.GetProperty("breached").GetInt32(), final.GetProperty("overdueNow").GetInt32()));
        Assert.All(rows, r => Assert.Equal("Acme review", r.GetProperty("workflow").GetString()));
    }

    // ---- csv ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Csv_is_spreadsheet_ready_with_a_byte_order_mark_labels_and_neutralised_formulas()
    {
        var s = await NewScene();
        var (status, contentType, disposition, body) = await RawGet($"{ReportsUrl}/top-assets?format=csv", s.Admin);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.StartsWith("text/csv", contentType, StringComparison.Ordinal);
        Assert.Contains("attachment", disposition, StringComparison.Ordinal);
        Assert.Contains("orbit-top-assets-", disposition, StringComparison.Ordinal);
        Assert.Equal([0xEF, 0xBB, 0xBF], body[..3]);
        var lines = Encoding.UTF8.GetString(body, 3, body.Length - 3).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("#,Asset,Type,Uses,Asset id", lines[0]);
        Assert.Equal($"1,Secret plans,image,5,{s.Secret}", lines[1]);
        Assert.StartsWith("2,\"'=HYPERLINK(\"\"http://evil.example\"\")\",image,3,", lines[2], StringComparison.Ordinal);   // neutralised with an apostrophe, then quoted
        Assert.StartsWith("3,\"Banner, \"\"large\"\"\",image,1,", lines[3], StringComparison.Ordinal);                         // commas and quotes survive
    }

    [Fact]
    public async Task Csv_honours_the_readers_scope_like_json_does()
    {
        var s = await NewScene();
        var reporter = await ScopedReporter(s.Tenant, s.Admin, "brand");
        foreach (var name in new[] { "top-assets", "unused-assets" })
        {
            var (status, _, _, body) = await RawGet($"{ReportsUrl}/{name}?format=csv&days=60", reporter);
            Assert.Equal(HttpStatusCode.OK, status);
            var text = Encoding.UTF8.GetString(body);
            Assert.DoesNotContain("Secret plans", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Idle other", text, StringComparison.Ordinal);
            Assert.DoesNotContain(s.Secret.ToString(), text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("kpis")]
    [InlineData("top-assets")]
    [InlineData("unused-assets")]
    [InlineData("storage")]
    [InlineData("workflow-sla")]
    public async Task Every_report_can_be_downloaded_as_csv_with_the_same_columns_as_json(string name)
    {
        var s = await NewScene();
        var json = await Run(name, s.Admin, "?days=60");
        var (status, contentType, _, body) = await RawGet($"{ReportsUrl}/{name}?format=csv&days=60", s.Admin);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.StartsWith("text/csv", contentType, StringComparison.Ordinal);
        var labels = json.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("label").GetString()!);
        Assert.StartsWith(string.Join(',', labels) + "\r\n", Encoding.UTF8.GetString(body, 3, body.Length - 3), StringComparison.Ordinal);
    }

    // ---- isolation ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Another_tenants_assets_never_appear()
    {
        var s = await NewScene();
        var (_, adminB, brandB) = await TenantWithPacks();
        var b = await Aged(adminB, brandB, 100, "B's own");
        AddVersion(b, 123);
        await Record(adminB, Use(b, "view", 1));
        foreach (var name in new[] { "top-assets", "unused-assets", "storage" })
            Assert.DoesNotContain("B's own", (await Run(name, s.Admin, "?days=60")).GetRawText(), StringComparison.Ordinal);
        Assert.Contains("B's own", (await Run("top-assets", adminB)).GetRawText(), StringComparison.Ordinal);
    }

    // ---- per-asset analytics -----------------------------------------------------------------------------------

    private string AssetUrl(Guid asset, string query = "") => $"/api/v1/analytics/assets/{asset}{query}";

    [Fact]
    public async Task An_assets_analytics_hold_its_totals_a_zero_filled_series_and_its_uses_by_type_and_channel()
    {
        var s = await NewScene();
        var a = await GetOk(AssetUrl(s.Hero, "?days=7"), s.Admin);
        Assert.Equal((s.Hero, "image"), (a.GetProperty("assetId").GetGuid(), a.GetProperty("type").GetString()));
        Assert.Equal(7, a.GetProperty("period").GetProperty("days").GetInt32());
        Assert.Equal(3, a.GetProperty("total").GetInt64());                            // today, 2 days and 4 days ago
        Assert.Equal(0, a.GetProperty("previousTotal").GetInt64());
        Assert.Equal(3, a.GetProperty("allTimeTotal").GetInt64());
        Assert.NotEqual(JsonValueKind.Null, a.GetProperty("firstUsedAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, a.GetProperty("lastUsedAt").ValueKind);

        var series = a.GetProperty("byDay").EnumerateArray().ToList();
        Assert.Equal(7, series.Count);
        Assert.Equal(3, series.Sum(d => d.GetProperty("count").GetInt64()));
        Assert.Equal(DateTime.UtcNow.ToString("yyyy-MM-dd"), series[^1].GetProperty("date").GetString());
        Assert.Equal([0, 0, 1, 0, 1, 0, 1], series.Select(d => d.GetProperty("count").GetInt32()));      // 6, 5, 4, 3, 2, 1 days ago and today

        Assert.Equal([("view", 2L), ("download", 1L)], a.GetProperty("byType").EnumerateArray().Select(x => (x.GetProperty("eventType").GetString()!, x.GetProperty("count").GetInt64())));
        Assert.Equal(new[] { ("unspecified", 1L), ("kiosk", 1L), ("portal", 1L) }.Order(), a.GetProperty("byChannel").EnumerateArray().Select(x => (x.GetProperty("channel").GetString()!, x.GetProperty("count").GetInt64())).Order());
    }

    [Fact]
    public async Task An_assets_previous_period_and_its_boundary_day_are_counted_where_they_belong()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var asset = await Aged(admin, brand, 100);
        var startOfPeriod = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-6), TimeSpan.Zero);          // days=7: the first instant of the period
        await Record(admin, UseAt(asset, startOfPeriod), UseAt(asset, startOfPeriod.AddTicks(-TimeSpan.TicksPerSecond)), UseAt(asset, startOfPeriod.AddDays(-3)), UseAt(asset, startOfPeriod.AddDays(-8)));
        var a = await GetOk(AssetUrl(asset, "?days=7"), admin);
        Assert.Equal(1, a.GetProperty("total").GetInt64());
        Assert.Equal(2, a.GetProperty("previousTotal").GetInt64());                                       // the second before the period and three days before; 8 days before is older
        Assert.Equal(4, a.GetProperty("allTimeTotal").GetInt64());
        _ = t;
    }

    [Fact]
    public async Task The_twenty_most_recent_uses_come_newest_first_with_who_reported_them()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var subject = Guid.NewGuid();
        var me = ApiTestHost.TokenFor(t, subject, ["Admin"], name: "Reporter One");
        var myId = (await Call(HttpMethod.Get, "/api/v1/me", me)).Body.GetProperty("userId").GetGuid();
        var asset = await Aged(admin, brand, 100);
        await Record(me, Enumerable.Range(1, 25).Select(i => (object)Use(asset, "view", 0, "wall-" + i, null)).ToArray());
        var otherDay = await Record(admin, Use(asset, "play", 9, "old-signage"));
        Assert.Equal(HttpStatusCode.OK, otherDay.Status);

        var a = await GetOk(AssetUrl(asset), admin);
        var recent = a.GetProperty("recent").EnumerateArray().ToList();
        Assert.Equal(20, recent.Count);
        var times = recent.Select(r => r.GetProperty("occurredAt").GetDateTimeOffset()).ToList();
        Assert.Equal(times.OrderByDescending(x => x), times);
        Assert.DoesNotContain(recent, r => r.GetProperty("channel").GetString() == "old-signage");     // 9 days old: past the 20 newest
        Assert.All(recent, r => Assert.Equal(myId, r.GetProperty("actorId").GetGuid()));
        Assert.All(recent, r => Assert.Equal("view", r.GetProperty("eventType").GetString()));
        Assert.Equal(26, a.GetProperty("allTimeTotal").GetInt64());
    }

    [Fact]
    public async Task An_asset_never_used_has_empty_analytics_not_an_error()
    {
        var s = await NewScene();
        var a = await GetOk(AssetUrl(s.IdleBrand, "?days=14"), s.Admin);
        Assert.Equal((0L, 0L, 0L), (a.GetProperty("total").GetInt64(), a.GetProperty("previousTotal").GetInt64(), a.GetProperty("allTimeTotal").GetInt64()));
        Assert.Equal(JsonValueKind.Null, a.GetProperty("firstUsedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, a.GetProperty("lastUsedAt").ValueKind);
        Assert.Equal(14, a.GetProperty("byDay").GetArrayLength());
        Assert.Equal(0, a.GetProperty("byType").GetArrayLength());
        Assert.Equal(0, a.GetProperty("recent").GetArrayLength());
    }

    [Fact]
    public async Task An_assets_analytics_are_the_same_before_and_after_the_rollup()
    {
        var s = await NewScene();
        var before = (await GetOk(AssetUrl(s.Hero, "?days=30"), s.Admin)).GetRawText();
        using var scheduler = AnalyticsScheduler(new FakeClock(DateTimeOffset.UtcNow));
        await Rollup(scheduler);
        Assert.Equal(before, (await GetOk(AssetUrl(s.Hero, "?days=30"), s.Admin)).GetRawText());
    }

    [Fact]
    public async Task Asset_analytics_need_analytics_read_and_read_access_to_the_asset_and_do_not_reveal_other_assets()
    {
        var s = await NewScene();
        var reporter = await ScopedReporter(s.Tenant, s.Admin, "brand");
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, AssetUrl(s.Hero), reporter)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, AssetUrl(s.Banner), reporter)).Status);       // in a subfolder of their scope
        var hidden = await Call(HttpMethod.Get, AssetUrl(s.Secret), reporter);
        var missing = await Call(HttpMethod.Get, AssetUrl(Guid.NewGuid()), reporter);
        Assert.Equal(HttpStatusCode.NotFound, hidden.Status);
        Assert.Equal(HttpStatusCode.NotFound, missing.Status);
        Assert.Equal(missing.Body.GetProperty("title").GetString(), hidden.Body.GetProperty("title").GetString());   // the two answers cannot be told apart

        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, AssetUrl(s.Hero), As(s.Tenant, "Viewer"))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, AssetUrl(s.Hero), As(s.Tenant, "Contributor"))).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, AssetUrl(s.Hero), As(s.Tenant, "Editor"))).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, AssetUrl(s.Hero), As(s.Tenant, "Approver"))).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Get, AssetUrl(s.Hero, "?days=0"), s.Admin)).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Get, AssetUrl(s.Hero, "?days=366"), s.Admin)).Status);

        var (_, adminB, _) = await TenantWithPacks();
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, AssetUrl(s.Hero), adminB)).Status);          // another tenant's asset
    }
}
