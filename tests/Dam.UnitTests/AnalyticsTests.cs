using System.Globalization;
using System.Text;
using Dam.Application.Assets;
using Dam.Domain.Analytics;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Shared.Content;

namespace Dam.UnitTests;

/// <summary>The pure rules of the KPIs, reports and scheduled reports (ADR-0037): periods, ratios, percentiles, schedule maths, partitions, CSV and email rendering, validators.</summary>
public sealed class AnalyticsTests
{
    private static DateTimeOffset At(int y, int mo, int d, int h = 0, int mi = 0) => new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    // ---- KPI periods -------------------------------------------------------------------------------------------

    [Fact]
    public void A_period_is_the_last_n_utc_days_ending_today_and_the_previous_one_is_as_long_and_adjacent()
    {
        var p = KpiPeriod.For(new DateOnly(2026, 10, 20), 30);
        Assert.Equal(new DateOnly(2026, 9, 21), p.From);
        Assert.Equal(new DateOnly(2026, 10, 20), p.To);
        Assert.Equal(new DateOnly(2026, 8, 22), p.PreviousFrom);
        Assert.Equal(new DateOnly(2026, 9, 20), p.PreviousTo);
        Assert.Equal(At(2026, 9, 21), p.Start);
        Assert.Equal(At(2026, 10, 21), p.End);              // exclusive: the first instant after today
        Assert.Equal(At(2026, 8, 22), p.PreviousStart);
    }

    [Fact]
    public void One_day_is_just_today_and_yesterday_is_the_previous_period()
    {
        var p = KpiPeriod.For(new DateOnly(2026, 3, 1), 1);
        Assert.Equal((new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 1)), (p.From, p.To));
        Assert.Equal((new DateOnly(2026, 2, 28), new DateOnly(2026, 2, 28)), (p.PreviousFrom, p.PreviousTo));
    }

    [Fact]
    public void An_instant_belongs_to_exactly_one_day_so_the_boundary_is_in_the_later_period()
    {
        var p = KpiPeriod.For(new DateOnly(2026, 10, 20), 7);
        var boundary = p.Start;
        Assert.True(boundary >= p.Start && boundary < p.End);
        Assert.False(boundary >= p.PreviousStart && boundary < p.Start);
        var justBefore = boundary.AddTicks(-1);
        Assert.True(justBefore >= p.PreviousStart && justBefore < p.Start);
    }

    // ---- ratios and percentiles --------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 0)]
    [InlineData(1, -1)]
    public void A_ratio_with_nothing_to_measure_is_null_never_zero(long numerator, long denominator) => Assert.Null(KpiMath.Ratio(numerator, denominator));

    [Fact]
    public void A_ratio_is_rounded_to_four_decimals_and_zero_over_something_is_zero()
    {
        Assert.Equal(0d, KpiMath.Ratio(0, 7));
        Assert.Equal(0.6667, KpiMath.Ratio(2, 3));
        Assert.Equal(1d, KpiMath.Ratio(9, 9));
        Assert.Equal(0.3333, KpiMath.Ratio(1, 3));
    }

    [Fact]
    public void Averages_and_hours_round_to_two_decimals_and_nothing_stays_null()
    {
        Assert.Null(KpiMath.Average(10, 0));
        Assert.Equal(1.67, KpiMath.Average(5, 3));
        Assert.Null(KpiMath.Hours(null));
        Assert.Equal(12.35, KpiMath.Hours(12.345));
    }

    [Fact]
    public void The_percentile_of_nothing_is_null_and_of_one_value_is_that_value()
    {
        Assert.Null(KpiMath.Percentile([], 0.5));
        Assert.Equal(7d, KpiMath.Percentile([7d], 0.5));
        Assert.Equal(7d, KpiMath.Percentile([7d], 0.9));
    }

    [Fact]
    public void The_median_of_an_even_count_is_the_mean_of_the_middle_two_whatever_the_input_order()
    {
        Assert.Equal(3d, KpiMath.Percentile([4d, 1d, 2d, 10d], 0.5));      // (2 + 4) / 2 = 3
        Assert.Equal(3d, KpiMath.Percentile([1d, 3d, 9d], 0.5));
    }

    [Fact]
    public void The_90th_percentile_interpolates_between_the_closest_ranks_like_percentile_cont()
    {
        // rank = 0.9 * (n - 1): for 2, 4, 10 that is 1.8, so 4 + 0.8 * (10 - 4) = 8.8; for 1..10 it is 8.1, so 9.1
        Assert.Equal(8.8, KpiMath.Percentile([2d, 4d, 10d], 0.9)!.Value, 9);
        Assert.Equal(9.1, KpiMath.Percentile(Enumerable.Range(1, 10).Select(i => (double)i).ToArray(), 0.9)!.Value, 9);
        Assert.Equal(10d, KpiMath.Percentile([2d, 4d, 10d], 1d));
        Assert.Equal(2d, KpiMath.Percentile([2d, 4d, 10d], 0d));
    }

    // ---- schedule maths ----------------------------------------------------------------------------------------

    [Fact]
    public void A_daily_schedule_is_due_today_if_the_hour_is_still_ahead_and_tomorrow_otherwise()
    {
        Assert.Equal(At(2026, 10, 20, 9), ScheduleRules.NextRun("daily", 9, null, null, At(2026, 10, 20, 8, 59)));
        Assert.Equal(At(2026, 10, 21, 9), ScheduleRules.NextRun("daily", 9, null, null, At(2026, 10, 20, 9)));       // strictly after
        Assert.Equal(At(2026, 10, 21, 9), ScheduleRules.NextRun("daily", 9, null, null, At(2026, 10, 20, 23, 59)));
        Assert.Equal(At(2027, 1, 1, 0), ScheduleRules.NextRun("daily", 0, null, null, At(2026, 12, 31, 12)));         // across a year
    }

    [Theory]
    [InlineData(1, "2026-10-20T10:00:00Z", "2026-10-26T06:00:00Z")]   // Tuesday morning -> next Monday
    [InlineData(2, "2026-10-20T05:00:00Z", "2026-10-20T06:00:00Z")]   // that very Tuesday, hour still ahead
    [InlineData(2, "2026-10-20T06:00:00Z", "2026-10-27T06:00:00Z")]   // exactly at the slot: the next one
    [InlineData(7, "2026-10-20T10:00:00Z", "2026-10-25T06:00:00Z")]   // Sunday is 7
    [InlineData(1, "2026-10-26T05:59:59Z", "2026-10-26T06:00:00Z")]
    public void A_weekly_schedule_uses_iso_weekdays_and_is_always_in_the_future(int dayOfWeek, string after, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected, CultureInfo.InvariantCulture), ScheduleRules.NextRun("weekly", 6, dayOfWeek, null, DateTimeOffset.Parse(after, CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData(15, "2026-10-10T00:00:00Z", "2026-10-15T08:00:00Z")]
    [InlineData(15, "2026-10-15T08:00:00Z", "2026-11-15T08:00:00Z")]
    [InlineData(15, "2026-12-20T00:00:00Z", "2027-01-15T08:00:00Z")]     // into the next year
    [InlineData(31, "2026-10-10T00:00:00Z", "2026-10-31T08:00:00Z")]
    [InlineData(31, "2026-10-31T08:00:00Z", "2026-11-30T08:00:00Z")]     // November has 30 days: its last day
    [InlineData(31, "2026-01-31T09:00:00Z", "2026-02-28T08:00:00Z")]     // February
    [InlineData(31, "2028-01-31T09:00:00Z", "2028-02-29T08:00:00Z")]     // ... in a leap year
    [InlineData(30, "2026-02-10T00:00:00Z", "2026-02-28T08:00:00Z")]
    [InlineData(29, "2027-02-28T09:00:00Z", "2027-03-29T08:00:00Z")]     // the 29th passed the (non-leap) February last day already
    [InlineData(1, "2026-10-31T23:59:59Z", "2026-11-01T08:00:00Z")]
    public void A_monthly_schedule_uses_the_last_day_of_a_month_that_is_too_short(int dayOfMonth, string after, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected, CultureInfo.InvariantCulture), ScheduleRules.NextRun("monthly", 8, null, dayOfMonth, DateTimeOffset.Parse(after, CultureInfo.InvariantCulture)));

    [Fact]
    public void An_offset_time_is_read_in_utc()
    {
        // 02:00 on the 21st at +05:00 is 21:00 UTC on the 20th: the daily 09:00 slot already passed on the 20th.
        Assert.Equal(At(2026, 10, 21, 9), ScheduleRules.NextRun("daily", 9, null, null, new DateTimeOffset(2026, 10, 21, 2, 0, 0, TimeSpan.FromHours(5))));
    }

    // ---- the schedule entity -----------------------------------------------------------------------------------

    private static ReportSchedule Schedule(string frequency = "weekly", bool enabled = true, params Guid[] recipients) =>
        ReportSchedule.Create(Guid.NewGuid(), "kpis", 30, frequency, 6, frequency == "weekly" ? 1 : null, frequency == "monthly" ? 31 : null,
            recipients.Length == 0 ? [Guid.NewGuid()] : recipients, enabled, null, At(2026, 10, 20, 10));

    [Fact]
    public void A_new_schedule_knows_when_it_is_next_due_and_a_disabled_one_is_never_due()
    {
        var s = Schedule();
        Assert.Equal(At(2026, 10, 26, 6), s.NextRunAt);
        Assert.False(s.IsDue(At(2026, 10, 26, 5, 59)));
        Assert.True(s.IsDue(At(2026, 10, 26, 6)));

        var off = Schedule(enabled: false);
        Assert.Null(off.NextRunAt);
        Assert.False(off.IsDue(At(2030, 1, 1)));
    }

    [Fact]
    public void Only_the_setting_that_matches_the_frequency_is_kept()
    {
        var weekly = ReportSchedule.Create(Guid.NewGuid(), "kpis", 7, "weekly", 6, 3, 15, [Guid.NewGuid()], true, null, At(2026, 10, 20));
        Assert.Equal((3, (int?)null), (weekly.DayOfWeek, weekly.DayOfMonth));
        var monthly = ReportSchedule.Create(Guid.NewGuid(), "kpis", 7, "monthly", 6, 3, 15, [Guid.NewGuid()], true, null, At(2026, 10, 20));
        Assert.Equal(((int?)null, 15), (monthly.DayOfWeek, monthly.DayOfMonth));
        var daily = ReportSchedule.Create(Guid.NewGuid(), "kpis", 7, "daily", 6, 3, 15, [Guid.NewGuid()], true, null, At(2026, 10, 20));
        Assert.Equal(((int?)null, (int?)null), (daily.DayOfWeek, daily.DayOfMonth));
    }

    [Fact]
    public void Recipients_are_deduplicated()
    {
        var a = Guid.NewGuid();
        Assert.Equal([a], Schedule("daily", true, a, a, a).RecipientUserIds);
    }

    [Fact]
    public void Finishing_a_run_moves_to_the_next_slot_clears_the_ledger_and_records_the_time()
    {
        var a = Guid.NewGuid();
        var s = Schedule("daily", true, a);
        s.RecordSentTo(a);
        s.RecordSentTo(a);
        Assert.Equal([a], s.SentTo);                       // recording twice records once

        s.CompleteRun(At(2026, 10, 21, 6, 1), "note");
        Assert.Equal(At(2026, 10, 21, 6, 1), s.LastSentAt);
        Assert.Equal("note", s.LastError);
        Assert.Empty(s.SentTo);
        Assert.Equal(At(2026, 10, 22, 6), s.NextRunAt);
    }

    [Fact]
    public void A_failed_run_stays_due_and_keeps_who_was_already_mailed()
    {
        var a = Guid.NewGuid();
        var s = Schedule("daily", true, a, Guid.NewGuid());
        var due = s.NextRunAt;
        s.RecordSentTo(a);
        s.RecordFailure("SMTP is down");
        Assert.Equal("SMTP is down", s.LastError);
        Assert.Equal(due, s.NextRunAt);
        Assert.Equal([a], s.SentTo);
        Assert.True(s.IsDue(due!.Value));
    }

    [Fact]
    public void A_slot_not_delivered_within_a_day_is_stale_and_giving_up_moves_on()
    {
        var s = Schedule("daily");
        var due = s.NextRunAt!.Value;
        Assert.False(s.IsStale(due.AddHours(24)));
        Assert.True(s.IsStale(due.AddHours(24).AddSeconds(1)));
        s.GiveUp(due.AddHours(25), "Email is not configured.");
        Assert.Contains("Gave up", s.LastError, StringComparison.Ordinal);
        Assert.Equal(due.AddDays(2), s.NextRunAt);          // 07:00 on the second day: the next 06:00 slot is the day after
    }

    [Fact]
    public void A_manual_send_does_not_move_the_schedule()
    {
        var s = Schedule();
        var due = s.NextRunAt;
        s.RecordManualSend(At(2026, 10, 20, 11), null);
        Assert.Equal(At(2026, 10, 20, 11), s.LastSentAt);
        Assert.Equal(due, s.NextRunAt);
    }

    [Fact]
    public void Editing_a_schedule_starts_again_from_now()
    {
        var a = Guid.NewGuid();
        var s = Schedule("daily", true, a);
        s.RecordSentTo(a);
        s.Update("kpis", 30, "daily", 8, null, null, [a], true, At(2026, 10, 20, 7));
        Assert.Empty(s.SentTo);
        Assert.Equal(At(2026, 10, 20, 8), s.NextRunAt);
        s.Update("kpis", 30, "daily", 8, null, null, [a], false, At(2026, 10, 20, 7));
        Assert.Null(s.NextRunAt);
    }

    // ---- first publication ------------------------------------------------------------------------------------

    [Fact]
    public void An_asset_remembers_the_first_time_it_was_published_and_never_moves_it()
    {
        var asset = Asset.CreateShell(Guid.NewGuid(), null, "image", "Acme hero", null, At(2026, 10, 1));
        Assert.Null(asset.FirstPublishedAt);

        asset.ChangeStatus(AssetStatuses.InReview, "submitted", At(2026, 10, 2));
        asset.ChangeStatus(AssetStatuses.Approved, "approved", At(2026, 10, 3));
        Assert.Null(asset.FirstPublishedAt);                      // nothing but publication sets it

        asset.ChangeStatus(AssetStatuses.Published, "published", At(2026, 10, 4));
        Assert.Equal(At(2026, 10, 4), asset.FirstPublishedAt);

        asset.ChangeStatus(AssetStatuses.Expired, "expired", At(2026, 10, 5));
        asset.ChangeStatus(AssetStatuses.Draft, "withdrawn", At(2026, 10, 6));
        asset.ChangeStatus(AssetStatuses.Published, "again", At(2026, 10, 9));
        Assert.Equal(At(2026, 10, 4), asset.FirstPublishedAt);    // a second publication is not the first

        asset.ChangeStatus(AssetStatuses.Published, "no change", At(2026, 10, 12));
        Assert.Equal(At(2026, 10, 4), asset.FirstPublishedAt);
    }

    // ---- the report catalogue and validators -------------------------------------------------------------------

    [Fact]
    public void The_catalogue_holds_the_five_reports_and_knows_which_name_assets()
    {
        Assert.Equal(["kpis", "top-assets", "unused-assets", "storage", "workflow-sla"], ReportCatalogue.All.Select(r => r.Name));
        Assert.Equal(["top-assets", "unused-assets"], ReportCatalogue.All.Where(r => r.NamesAssets).Select(r => r.Name));
        Assert.Equal(3650, ReportCatalogue.Find("unused-assets")!.MaxDays);
        Assert.Equal(ReportCatalogue.Kpis, ReportCatalogue.Find(" KPIs ")!.Name);
        Assert.Null(ReportCatalogue.Find("revenue"));
        Assert.Null(ReportCatalogue.Find(null));
    }

    [Fact]
    public void A_report_request_is_checked_for_its_name_and_period()
    {
        Assert.Null(ReportService.Check("kpis", null));
        Assert.Null(ReportService.Check("kpis", 365));
        Assert.Null(ReportService.Check("unused-assets", 3650));
        Assert.Equal(Dam.Domain.Common.ErrorType.NotFound, ReportService.Check("nope", 7)!.Type);
        Assert.Contains("days", ReportService.Check("kpis", 366)!.FieldErrors!.Keys);
        Assert.Contains("days", ReportService.Check("top-assets", 0)!.FieldErrors!.Keys);
        Assert.Contains("days", ReportService.Check("unused-assets", 3651)!.FieldErrors!.Keys);
    }

    private static ReportScheduleInput Input(string report = "kpis", int days = 30, string frequency = "weekly", int hour = 6, int? dow = 1, int? dom = null, Guid[]? to = null, bool enabled = true) =>
        new(report, days, frequency, hour, dow, dom, to ?? [Guid.NewGuid()], enabled);

    private static Dictionary<string, string[]> Errors(ReportScheduleInput i) =>
        new CreateReportScheduleValidator().Validate(new CreateReportScheduleCommand(i)).Errors
            .GroupBy(e => e.PropertyName).ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());

    [Fact]
    public void A_schedule_is_validated_field_by_field()
    {
        Assert.Empty(Errors(Input()));
        Assert.Empty(Errors(Input(frequency: "daily", dow: null)));
        Assert.Empty(Errors(Input(frequency: "monthly", dow: null, dom: 31)));
        Assert.Empty(Errors(Input(report: "unused-assets", days: 3650)));

        Assert.Contains("report", Errors(Input(report: "revenue")).Keys);
        Assert.Contains("days", Errors(Input(days: 0)).Keys);
        Assert.Contains("days", Errors(Input(days: 366)).Keys);
        Assert.Contains("frequency", Errors(Input(frequency: "hourly")).Keys);
        Assert.Contains("hourUtc", Errors(Input(hour: 24)).Keys);
        Assert.Contains("hourUtc", Errors(Input(hour: -1)).Keys);
        Assert.Contains("dayOfWeek", Errors(Input(dow: null)).Keys);
        Assert.Contains("dayOfWeek", Errors(Input(dow: 8)).Keys);
        Assert.Contains("dayOfMonth", Errors(Input(frequency: "monthly", dow: null, dom: 32)).Keys);
        Assert.Contains("dayOfMonth", Errors(Input(frequency: "monthly", dow: null, dom: null)).Keys);
        Assert.Contains("recipientUserIds", Errors(Input(to: [])).Keys);
        Assert.Contains("recipientUserIds", Errors(Input(to: [Guid.Empty])).Keys);
        Assert.Contains("recipientUserIds", Errors(Input(to: Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()).ToArray())).Keys);
        Assert.Empty(Errors(Input(to: Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToArray())));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("json", true)]
    [InlineData("CSV", true)]
    [InlineData("xml", false)]
    public void A_report_format_is_json_or_csv(string? format, bool valid) =>
        Assert.Equal(valid, new RunReportValidator().Validate(new RunReportQuery("kpis", null, format)).IsValid);

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(365, true)]
    [InlineData(366, false)]
    public void The_kpi_and_asset_analytics_period_is_one_to_365_days(int days, bool valid)
    {
        Assert.Equal(valid, new GetKpisValidator().Validate(new GetKpisQuery(days)).IsValid);
        Assert.Equal(valid, new GetAssetAnalyticsValidator().Validate(new GetAssetAnalyticsQuery(Guid.NewGuid(), days)).IsValid);
    }

    [Fact]
    public void Managing_scheduled_reports_is_for_admin_and_librarian_only()
    {
        var with = new[] { BuiltInRoles.Admin, BuiltInRoles.Librarian };
        foreach (var role in BuiltInRoles.All)
            Assert.True(with.Contains(role.Name) == role.Permissions.Contains(Permissions.AnalyticsManage), role.Name);
        Assert.Contains(Permissions.AnalyticsManage, Permissions.All);
    }

    // ---- partitions --------------------------------------------------------------------------------------------

    [Fact]
    public void Partitions_are_named_by_month_and_the_name_can_be_read_back()
    {
        Assert.Equal("usage_events_2026_10", UsagePartitions.NameFor(new DateOnly(2026, 10, 1)));
        Assert.Equal("usage_events_2027_01", UsagePartitions.NameFor(new DateOnly(2027, 1, 1)));
        Assert.True(UsagePartitions.TryParseMonth("usage_events_2026_10", out var m));
        Assert.Equal(new DateOnly(2026, 10, 1), m);
        foreach (var bad in new[] { "usage_events_default", "usage_events", "usage_events_2026_13", "usage_events_2026_00", "usage_events_26_10", "usage_events_2026-10", "other_2026_10", "usage_events_2026_10x" })
            Assert.False(UsagePartitions.TryParseMonth(bad, out _), bad);
    }

    [Fact]
    public void The_current_month_and_the_next_three_are_always_needed_even_across_a_year_end()
    {
        Assert.Equal([new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 1), new DateOnly(2027, 1, 1), new DateOnly(2027, 2, 1)], UsagePartitions.Needed(new DateOnly(2026, 11, 30)));
    }

    [Fact]
    public void Retention_keeps_the_configured_number_of_full_months_before_the_current_one()
    {
        Assert.Null(UsagePartitions.RetentionHorizon(new DateOnly(2026, 10, 20), 0));
        Assert.Equal(new DateOnly(2025, 10, 1), UsagePartitions.RetentionHorizon(new DateOnly(2026, 10, 20), 12));
        Assert.Equal(new DateOnly(2026, 9, 1), UsagePartitions.RetentionHorizon(new DateOnly(2026, 10, 1), 1));
        Assert.Equal(new DateOnly(2025, 12, 1), UsagePartitions.RetentionHorizon(new DateOnly(2026, 2, 3), 2));
        // a partition is droppable when it ends on or before the horizon
        Assert.True(UsagePartitions.NextMonth(new DateOnly(2025, 9, 1)) <= UsagePartitions.RetentionHorizon(new DateOnly(2026, 10, 20), 12));
        Assert.False(UsagePartitions.NextMonth(new DateOnly(2025, 10, 1)) <= UsagePartitions.RetentionHorizon(new DateOnly(2026, 10, 20), 12));
    }

    // ---- CSV ---------------------------------------------------------------------------------------------------

    private static Dictionary<string, object?> R(params (string Key, object? Value)[] cells) => cells.ToDictionary(c => c.Key, c => c.Value);

    private static ReportDto Report(IReadOnlyList<ReportColumnDto> columns, params Dictionary<string, object?>[] rows) =>
        new("test", "Test", At(2026, 10, 20), new AnalyticsPeriodDto("2026-09-21", "2026-10-20", 30), columns, rows, false);

    private static string Csv(ReportDto report)
    {
        var bytes = ReportCsv.Render(report);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);                      // the byte-order mark Excel needs for non-ASCII text
        return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
    }

    [Fact]
    public void Csv_starts_with_a_bom_then_the_labels_then_the_rows()
    {
        var report = Report(
            [new("title", "Asset", "text"), new("uses", "Uses", "count")],
            R(("title", "Hero"), ("uses", 12L)), R(("title", "Übung"), ("uses", 3L)));
        Assert.Equal("Asset,Uses\r\nHero,12\r\nÜbung,3\r\n", Csv(report));
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\")", "\"'=HYPERLINK(\"\"http://evil\"\")\"")]
    [InlineData("+1+1", "'+1+1")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tcmd", "'\tcmd")]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    public void Text_cells_a_spreadsheet_could_run_as_formulas_are_neutralised_and_quoted(string cell, string expected)
    {
        var report = Report([new("t", "Text", "text")], R(("t", cell)));
        Assert.Equal("Text\r\n" + expected + "\r\n", Csv(report));
    }

    [Fact]
    public void A_column_label_is_neutralised_too()
    {
        var report = Report([new("t", "=Label", "text")], R(("t", "x")));
        Assert.StartsWith("'=Label", Csv(report), StringComparison.Ordinal);
    }

    [Fact]
    public void Numbers_are_plain_and_culture_free_and_percentages_carry_a_sign()
    {
        var old = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var report = Report(
                [new("n", "N", "number"), new("p", "P", "percent"), new("b", "B", "bytes"), new("c", "C", "count"), new("h", "H", "hours")],
                R(("n", 1234.5), ("p", 0.8333), ("b", 1048576L), ("c", -4L), ("h", 12.345)),
                R(("n", null), ("p", null), ("b", null), ("c", null), ("h", null)));
            Assert.Equal("N,P,B,C,H\r\n1234.5,83.3%,1048576,-4,12.35\r\n,,,,\r\n", Csv(report));   // negative numbers are not "formulas": they are not text
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void A_text_value_that_is_missing_is_an_empty_cell()
    {
        var report = Report([new("a", "A", "text"), new("b", "B", "text")], R(("a", null), ("b", "x")));
        Assert.Equal("A,B\r\n,x\r\n", Csv(report));
    }

    // ---- email -------------------------------------------------------------------------------------------------

    private static ReportDto Many(int rows)
    {
        var list = Enumerable.Range(1, rows).Select(i => R(("title", $"Asset {i}"), ("bytes", 1536L * i))).ToList();
        return Report([new("title", "Asset", "text"), new("bytes", "Size", "bytes")], [.. list]);
    }

    [Fact]
    public void The_email_shows_the_first_fifty_rows_says_how_many_there_are_and_links_to_the_console()
    {
        var mail = ReportEmail.Render(Many(120), "Acme", 30, "https://orbit.example/insights/reports/test?days=30");
        Assert.Equal("[Acme] Test (2026-09-21 to 2026-10-20)", mail.Subject);
        Assert.Contains("Asset 50", mail.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("Asset 51", mail.Html, StringComparison.Ordinal);
        Assert.Contains("Showing the first 50 of 120 rows.", mail.Html, StringComparison.Ordinal);
        Assert.Contains("href=\"https://orbit.example/insights/reports/test?days=30\"", mail.Html, StringComparison.Ordinal);
        Assert.Contains("1.5 KB", mail.Html, StringComparison.Ordinal);
        Assert.Contains("75.0 KB", mail.Text, StringComparison.Ordinal);   // 50 x 1.5 KB
        Assert.Contains("Open in Orbit: https://orbit.example/insights/reports/test?days=30", mail.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Asset 51", mail.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Everything_that_came_from_data_is_escaped_in_the_email()
    {
        var report = Report([new("title", "Asset", "text")], R(("title", "<script>alert(1)</script> & co")));
        var mail = ReportEmail.Render(report, "A<b>cme", 30, null);
        Assert.DoesNotContain("<script>", mail.Html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; &amp; co", mail.Html, StringComparison.Ordinal);
        Assert.Contains("A&lt;b&gt;cme", mail.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("Open in Orbit", mail.Html, StringComparison.Ordinal);   // no public address, no link
    }

    [Fact]
    public void An_empty_report_says_so_and_a_storage_report_is_dated_as_of_a_day()
    {
        var empty = ReportEmail.Render(Many(0), "", 30, null);
        Assert.Contains("Nothing to report for this period.", empty.Html, StringComparison.Ordinal);
        Assert.StartsWith("Test (", empty.Subject, StringComparison.Ordinal);   // no tenant prefix when there is no tenant name

        var storage = Many(2) with { Name = ReportCatalogue.Storage, Period = new AnalyticsPeriodDto("2026-10-20", "2026-10-20", 1) };
        Assert.Contains("as of 2026-10-20", ReportEmail.Render(storage, "Acme", 30, null).Subject, StringComparison.Ordinal);
        Assert.Contains("not used for 90 days", ReportEmail.Render(Many(1) with { Name = ReportCatalogue.UnusedAssets }, "Acme", 90, null).Subject, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(1572864L, "1.5 MB")]
    [InlineData(107255865L, "102.3 MB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5.0 GB")]
    public void Sizes_are_shown_the_way_people_read_them(long bytes, string shown) => Assert.Equal(shown, ReportEmail.Bytes(bytes));

    private static readonly ReportColumnDto[] KpiColumns =
        [new("kpi", "KPI", "text"), new("metric", "Measure", "text"), new("value", "This period", "number"), new("unit", "Unit", "text"), new("previous", "Previous period", "number")];

    private static Dictionary<string, object?> Kpi(string metric, object? value, string unit, object? previous) =>
        R(("kpi", "K"), ("metric", metric), ("value", value), ("unit", unit), ("previous", previous));

    private static ReportDto KpiReport() => Report(KpiColumns,
        Kpi("Storage not used", 107255865L, "bytes", 1536L),
        Kpi("Median time to publish", 5.5, "hours", 0.75),
        Kpi("90th percentile time to publish", 72.0, "hours", 30.0),
        Kpi("Duplicate rate", 12.34, "%", null),
        Kpi("Uploads", 1234567L, "uploads", 980L),
        Kpi("Average uses per used asset", 1.37, "uses", 2.0)) with
    { Name = ReportCatalogue.Kpis };

    [Fact]
    public void The_kpi_email_formats_each_row_by_its_own_unit_in_the_table_and_the_text()
    {
        var mail = ReportEmail.Render(KpiReport(), "Acme", 30, null);
        foreach (var part in new[] { mail.Html, mail.Text })
        {
            Assert.DoesNotContain("107255865", part, StringComparison.Ordinal);
            Assert.DoesNotContain("1234567", part, StringComparison.Ordinal);   // counts get separators
            Assert.Contains("102.3 MB", part, StringComparison.Ordinal);
            Assert.Contains("1.5 KB", part, StringComparison.Ordinal);          // the previous period, too
            Assert.Contains("5.5 h", part, StringComparison.Ordinal);
            Assert.Contains("45 min", part, StringComparison.Ordinal);
            Assert.Contains("3 d", part, StringComparison.Ordinal);             // 72 h
            Assert.Contains("30 h", part, StringComparison.Ordinal);            // 30 h reads as hours, not days
            Assert.Contains("12.3%", part, StringComparison.Ordinal);
            Assert.Contains("1,234,567", part, StringComparison.Ordinal);
            Assert.Contains("1.4", part, StringComparison.Ordinal);
        }
        Assert.Contains("Storage not used | 102.3 MB |  | 1.5 KB", mail.Text, StringComparison.Ordinal);   // the unit cell is blank when the figure says it
        Assert.Contains("Uploads | 1,234,567 | uploads | 980", mail.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.0, "1 min")]
    [InlineData(0.75, "45 min")]
    [InlineData(1.0, "1 h")]
    [InlineData(5.5, "5.5 h")]
    [InlineData(30.0, "30 h")]
    [InlineData(47.9, "47.9 h")]
    [InlineData(48.0, "2 d")]
    [InlineData(72.0, "3 d")]
    [InlineData(100.0, "4.2 d")]
    public void Hours_read_as_minutes_hours_or_days_like_the_console(double hours, string shown)
    {
        var report = Report([new("h", "Time", "hours")], R(("h", hours)));
        Assert.Contains($">{shown}<", ReportEmail.Render(report, "", 30, null).Html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.943, "94.3%")]
    [InlineData(0.5, "50%")]
    [InlineData(1.0, "100%")]
    [InlineData(0.9996, "99.9%")]
    public void Percent_columns_hold_a_ratio_and_show_one_decimal_with_a_sign(double ratio, string shown)
    {
        var report = Report([new("p", "Rate", "percent")], R(("p", ratio)));
        Assert.Contains($">{shown}<", ReportEmail.Render(report, "", 30, null).Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Counts_and_sizes_in_typed_columns_are_formatted_and_a_missing_value_stays_blank()
    {
        var report = Report([new("c", "Uses", "count"), new("b", "Size", "bytes")], R(("c", 9876543L), ("b", 5L * 1024 * 1024 * 1024)), R(("c", null), ("b", null)));
        var html = ReportEmail.Render(report, "", 30, null).Html;
        Assert.Contains(">9,876,543<", html, StringComparison.Ordinal);
        Assert.Contains(">5.0 GB<", html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_csv_of_the_same_kpi_report_keeps_the_raw_numbers_for_machines()
    {
        var csv = Csv(KpiReport());
        Assert.Contains("K,Storage not used,107255865,bytes,1536", csv, StringComparison.Ordinal);
        Assert.Contains("K,Median time to publish,5.5,hours,0.75", csv, StringComparison.Ordinal);
        Assert.Contains("K,Uploads,1234567,uploads,980", csv, StringComparison.Ordinal);
        Assert.Contains("K,Duplicate rate,12.34,%,", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("MB", csv, StringComparison.Ordinal);
    }

    // ---- usage rules the rollup relies on ---------------------------------------------------------------------

    [Fact]
    public void Days_are_utc_days_whatever_the_offset_of_the_instant()
    {
        Assert.Equal(new DateOnly(2026, 10, 19), UsagePartitions.DayOf(new DateTimeOffset(2026, 10, 20, 2, 0, 0, TimeSpan.FromHours(5))));   // 21:00 UTC on the 19th
        Assert.Equal(new DateOnly(2026, 10, 20), UsagePartitions.DayOf(new DateTimeOffset(2026, 10, 19, 22, 0, 0, TimeSpan.FromHours(-5))));  // 03:00 UTC on the 20th
    }
}
