using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Application.Assets;
using Dam.Domain.Assets;
using Dam.Domain.Authorization;
using Dam.Shared.Content;

namespace Dam.UnitTests;

/// <summary>The pure rules behind usage tracking and "unused assets" (ADR-0029): no database, no clock but the one passed in.</summary>
public sealed class UsageTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow => now; }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ---- occurredAt ------------------------------------------------------------------------------------------

    [Fact]
    public void A_missing_occurredAt_means_now()
    {
        Assert.Equal(Now, UsageRules.ResolveOccurredAt(null, Now, out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData(-10)]            // ten minutes ago
    [InlineData(-60 * 24 * 365 * 5 + 60)] // just inside five years
    [InlineData(4)]              // clock drift ahead is tolerated
    [InlineData(5)]
    public void Times_from_the_recent_past_up_to_five_minutes_ahead_are_accepted(int minutesFromNow)
    {
        var at = Now.AddMinutes(minutesFromNow);
        Assert.Equal(at, UsageRules.ResolveOccurredAt(at, Now, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void A_time_more_than_five_minutes_ahead_is_refused()
    {
        Assert.Null(UsageRules.ResolveOccurredAt(Now.AddMinutes(5).AddSeconds(1), Now, out var error));
        Assert.Contains("future", error);
    }

    [Fact]
    public void A_time_more_than_five_years_back_is_refused()
    {
        Assert.Null(UsageRules.ResolveOccurredAt(Now.AddDays(-365 * 5 - 1), Now, out var error));
        Assert.Contains("past", error);
    }

    // ---- context ---------------------------------------------------------------------------------------------

    [Fact]
    public void Context_may_be_absent_null_or_a_small_object()
    {
        Assert.Null(UsageRules.ContextError(null));
        Assert.Null(UsageRules.ContextError(Json("null")));
        Assert.Null(UsageRules.ContextError(Json("""{"screen":"lobby-2","durationSeconds":12}""")));
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("5")]
    public void Context_that_is_not_an_object_is_refused(string json) => Assert.NotNull(UsageRules.ContextError(Json(json)));

    [Fact]
    public void Context_over_four_kilobytes_is_refused()
    {
        var small = Json($$"""{"note":"{{new string('x', 4000)}}"}""");
        var big = Json($$"""{"note":"{{new string('x', 4100)}}"}""");
        Assert.Null(UsageRules.ContextError(small));
        Assert.Contains("4 KB", UsageRules.ContextError(big));
    }

    [Fact]
    public void Blank_text_is_stored_as_null_and_a_created_event_is_normalised()
    {
        Assert.Null(UsageRules.Blank("  "));
        Assert.Equal("signage", UsageRules.Blank(" signage "));
        var e = UsageEvent.Create(Guid.NewGuid(), Guid.NewGuid(), "play", " ", null, null, Now.ToOffset(TimeSpan.FromHours(2)), "", null, Now);
        Assert.Null(e.Channel);
        Assert.Null(e.ClientEventId);
        Assert.Equal(TimeSpan.Zero, e.OccurredAt.Offset);
        Assert.Equal(Now, e.OccurredAt);
    }

    [Fact]
    public void Event_types_are_the_six_known_ones()
    {
        Assert.Equal(["view", "download", "share", "embed", "place", "play"], UsageEventTypes.All);
        Assert.True(UsageEventTypes.IsKnown("play"));
        Assert.False(UsageEventTypes.IsKnown("Play"));
        Assert.False(UsageEventTypes.IsKnown("preview"));
        Assert.False(UsageEventTypes.IsKnown(null));
    }

    // ---- the request validator -------------------------------------------------------------------------------

    private static UsageEventInput Event(string type = "download", DateTimeOffset? at = null, string? channel = null, string? clientId = null,
        string? rendition = null, JsonElement? context = null) => new(clientId, Guid.NewGuid(), type, channel, rendition, at, context);

    private static Dictionary<string, string[]> Errors(params UsageEventInput[] events)
    {
        var result = new RecordUsageEventsValidator(new FixedClock(Now)).Validate(new RecordUsageEventsCommand(events));
        return result.Errors.GroupBy(e => e.PropertyName).ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
    }

    [Fact]
    public void A_good_batch_has_no_errors() =>
        Assert.Empty(Errors(Event(), Event("play", Now.AddDays(-3), "signage", "pop-1", "web", Json("""{"screen":"a"}"""))));

    [Fact]
    public void An_empty_batch_or_one_over_500_is_refused()
    {
        Assert.Contains("Events", Errors().Keys);
        Assert.Contains("Events", Errors(Enumerable.Range(0, 501).Select(_ => Event()).ToArray()).Keys);
        Assert.Empty(Errors(Enumerable.Range(0, 500).Select(_ => Event()).ToArray()));
    }

    [Fact]
    public void Each_bad_event_is_reported_against_its_position()
    {
        var errors = Errors(
            Event(),
            Event("preview"),
            Event(at: Now.AddHours(1)),
            Event(channel: new string('c', 101)),
            Event(rendition: new string('r', 51)),
            Event(clientId: new string('i', 101)),
            Event(context: Json("[1]")));
        Assert.Equal(
            ["Events[1].EventType", "Events[2].OccurredAt", "Events[3].Channel", "Events[4].Rendition", "Events[5].ClientEventId", "Events[6].Context"],
            errors.Keys.Order());
    }

    // ---- the idle window -------------------------------------------------------------------------------------

    [Fact]
    public void With_neither_days_nor_before_the_window_is_the_last_thirty_days()
    {
        Assert.Equal(Now.AddDays(-30), IdleWindow.ResolveCutoff(null, null, Now, out var errors));
        Assert.Null(errors);
    }

    [Fact]
    public void Days_sets_the_cutoff_that_far_back()
    {
        Assert.Equal(Now.AddDays(-180), IdleWindow.ResolveCutoff(180, null, Now, out _));
        Assert.Equal(Now.AddDays(-3650), IdleWindow.ResolveCutoff(3650, " ", Now, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(3651)]
    public void Days_outside_one_to_3650_is_refused(int days)
    {
        Assert.Null(IdleWindow.ResolveCutoff(days, null, Now, out var errors));
        Assert.Contains("days", errors!.Keys);
    }

    [Fact]
    public void Giving_both_days_and_before_is_refused()
    {
        Assert.Null(IdleWindow.ResolveCutoff(30, "2026-01-01", Now, out var errors));
        Assert.Equal(["before", "days"], errors!.Keys.Order());
    }

    [Theory]
    [InlineData("2026-03-01", "2026-03-01T00:00:00+00:00")]               // a bare date is the start of that day, UTC
    [InlineData("2026-03-01T12:30:00Z", "2026-03-01T12:30:00+00:00")]
    [InlineData("2026-03-01T12:30:00", "2026-03-01T12:30:00+00:00")]      // no offset: read as UTC
    [InlineData("2026-03-01T14:30:00+02:00", "2026-03-01T12:30:00+00:00")]
    public void Before_accepts_a_date_or_a_date_and_time(string before, string expected)
    {
        var cutoff = IdleWindow.ResolveCutoff(null, before, Now, out var errors);
        Assert.Null(errors);
        Assert.Equal(DateTimeOffset.Parse(expected), cutoff);
        Assert.Equal(TimeSpan.Zero, cutoff!.Value.Offset);
    }

    [Theory]
    [InlineData("yesterday")]
    [InlineData("2026-13-45")]
    [InlineData("30")]
    public void A_before_that_is_not_a_date_is_refused(string before)
    {
        Assert.Null(IdleWindow.ResolveCutoff(null, before, Now, out var errors));
        Assert.Contains("before", errors!.Keys);
    }

    // ---- idle since ------------------------------------------------------------------------------------------

    [Fact]
    public void An_asset_that_was_never_used_is_judged_from_its_creation_date()
    {
        var cutoff = Now.AddDays(-30);
        Assert.Equal(Now.AddDays(-90), IdleWindow.IdleSince(Now.AddDays(-90), null));
        Assert.True(IdleWindow.IsIdle(Now.AddDays(-90), null, cutoff));
        Assert.False(IdleWindow.IsIdle(Now.AddDays(-2), null, cutoff)); // brand new: never flagged
        Assert.False(IdleWindow.IsIdle(cutoff, null, cutoff));           // exactly at the cutoff is not before it
    }

    [Fact]
    public void A_used_asset_is_judged_from_its_last_use_not_its_creation()
    {
        var cutoff = Now.AddDays(-30);
        Assert.False(IdleWindow.IsIdle(Now.AddDays(-400), Now.AddDays(-5), cutoff));  // old asset, used lately
        Assert.True(IdleWindow.IsIdle(Now.AddDays(-400), Now.AddDays(-45), cutoff));  // old asset, last used long ago
        Assert.True(IdleWindow.IsIdle(Now.AddDays(-10), Now.AddDays(-45), cutoff));   // imported history older than the asset
    }

    [Fact]
    public void Idle_days_are_whole_days_and_never_negative()
    {
        Assert.Equal(0, IdleWindow.IdleDays(Now.AddHours(-23), Now));
        Assert.Equal(1, IdleWindow.IdleDays(Now.AddHours(-25), Now));
        Assert.Equal(400, IdleWindow.IdleDays(Now.AddDays(-400).AddMinutes(-1), Now));
        Assert.Equal(0, IdleWindow.IdleDays(Now.AddDays(1), Now));
    }

    // ---- status filter ---------------------------------------------------------------------------------------

    [Fact]
    public void The_status_filter_is_all_or_a_real_status_and_all_leaves_out_deleted_and_blocked()
    {
        Assert.True(IdleWindow.IsKnownStatusFilter("all"));
        Assert.True(IdleWindow.IsKnownStatusFilter(AssetStatuses.InReview));
        Assert.False(IdleWindow.IsKnownStatusFilter("live"));

        foreach (var s in new[] { AssetStatuses.Draft, AssetStatuses.InReview, AssetStatuses.Approved, AssetStatuses.Published, AssetStatuses.Expired, AssetStatuses.Archived })
            Assert.True(IdleWindow.Includes("all", s), s);
        Assert.False(IdleWindow.Includes("all", AssetStatuses.Deleted));
        Assert.False(IdleWindow.Includes("all", AssetStatuses.Blocked));
        Assert.True(IdleWindow.Includes(AssetStatuses.Deleted, AssetStatuses.Deleted));   // asked for by name
        Assert.False(IdleWindow.Includes(AssetStatuses.Published, AssetStatuses.Draft));
    }

    // ---- query validation ------------------------------------------------------------------------------------

    private static Dictionary<string, string[]> QueryErrors(ListUnusedAssetsQuery q) =>
        new ListUnusedAssetsValidator(new FixedClock(Now)).Validate(q).Errors
            .GroupBy(e => e.PropertyName).ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());

    private static ListUnusedAssetsQuery Query(int? days = null, string? before = null, string? status = null, string? type = null, string? cursor = null) =>
        new(days, before, status, type, null, true, cursor, null);

    [Fact]
    public void The_unused_query_validates_window_status_type_and_cursor()
    {
        Assert.Empty(QueryErrors(Query()));
        Assert.Empty(QueryErrors(Query(days: 365, status: "all", type: "image")));
        Assert.Contains("days", QueryErrors(Query(days: 0)).Keys);
        Assert.Contains("days", QueryErrors(Query(days: 5, before: "2026-01-01")).Keys);
        Assert.Contains("before", QueryErrors(Query(before: "soon")).Keys);
        Assert.Contains("Status", QueryErrors(Query(status: "live")).Keys);
        Assert.Contains("Type", QueryErrors(Query(type: "gif")).Keys);
        Assert.Contains("Cursor", QueryErrors(Query(cursor: "garbage")).Keys);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(365, true)]
    [InlineData(366, false)]
    public void The_overview_period_is_one_to_365_days(int days, bool valid) =>
        Assert.Equal(valid, new GetAnalyticsOverviewValidator().Validate(new GetAnalyticsOverviewQuery(days)).IsValid);

    // ---- calendar --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("2026-10-19T12:00:00Z", "2026-10-19T00:00:00Z")]   // a Monday
    [InlineData("2026-10-25T23:59:59Z", "2026-10-19T00:00:00Z")]   // Sunday still belongs to the week before the next Monday
    [InlineData("2026-10-26T00:00:00Z", "2026-10-26T00:00:00Z")]
    [InlineData("2026-10-21T08:00:00+05:00", "2026-10-19T00:00:00Z")]
    public void Weeks_start_on_monday_utc(string at, string weekStart) =>
        Assert.Equal(DateTimeOffset.Parse(weekStart), AnalyticsPeriods.StartOfWeek(DateTimeOffset.Parse(at)));

    [Fact]
    public void A_day_starts_at_midnight_utc() =>
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero), AnalyticsPeriods.StartOfDay(Now.ToOffset(TimeSpan.FromHours(-8))));

    // ---- permissions -----------------------------------------------------------------------------------------

    [Fact]
    public void Analytics_read_is_held_by_the_curating_roles_and_not_by_the_rest()
    {
        var with = new[] { BuiltInRoles.Admin, BuiltInRoles.Librarian, BuiltInRoles.Editor, BuiltInRoles.Approver };
        foreach (var role in BuiltInRoles.All)
            Assert.True(with.Contains(role.Name) == role.Permissions.Contains(Permissions.AnalyticsRead), role.Name);
    }
}
