using Dam.Domain.Assets;
using Dam.Domain.Authorization;

namespace Dam.UnitTests;

public sealed class AssetLifecycleTests
{
    public static IEnumerable<object[]> LegalTransitions() =>
    [
        [AssetStatuses.Draft, AssetLifecycle.Submit, AssetStatuses.InReview],
        [AssetStatuses.InReview, AssetLifecycle.Approve, AssetStatuses.Approved],
        [AssetStatuses.InReview, AssetLifecycle.Reject, AssetStatuses.Draft],
        [AssetStatuses.Approved, AssetLifecycle.Publish, AssetStatuses.Published],
        [AssetStatuses.Published, AssetLifecycle.Expire, AssetStatuses.Expired],
        [AssetStatuses.Expired, AssetLifecycle.Archive, AssetStatuses.Archived],
    ];

    [Theory]
    [MemberData(nameof(LegalTransitions))]
    public void Every_step_of_the_happy_path_moves_to_the_expected_status(string from, string action, string to)
    {
        Assert.True(AssetLifecycle.TryGetTarget(from, action, out var target));
        Assert.Equal(to, target);
    }

    [Theory]
    [InlineData(AssetStatuses.InReview)]
    [InlineData(AssetStatuses.Approved)]
    [InlineData(AssetStatuses.Published)]
    [InlineData(AssetStatuses.Expired)]
    [InlineData(AssetStatuses.Archived)]
    public void Withdraw_returns_any_state_past_draft_to_draft(string from)
    {
        Assert.True(AssetLifecycle.TryGetTarget(from, AssetLifecycle.Withdraw, out var to));
        Assert.Equal(AssetStatuses.Draft, to);
    }

    [Theory]
    [InlineData(AssetStatuses.Draft)]
    [InlineData(AssetStatuses.Deleted)]
    [InlineData(AssetStatuses.Blocked)]
    public void Withdraw_does_not_apply_to_a_state_that_is_already_draft_or_terminal(string from) =>
        Assert.False(AssetLifecycle.TryGetTarget(from, AssetLifecycle.Withdraw, out _));

    public static IEnumerable<object[]> IllegalCombinations() =>
    [
        [AssetStatuses.Draft, AssetLifecycle.Approve],       // must be submitted first
        [AssetStatuses.Draft, AssetLifecycle.Publish],       // must be approved first
        [AssetStatuses.InReview, AssetLifecycle.Publish],    // must be approved first
        [AssetStatuses.Approved, AssetLifecycle.Approve],    // already approved
        [AssetStatuses.Published, AssetLifecycle.Submit],    // already published
        [AssetStatuses.Published, AssetLifecycle.Archive],   // must expire first
        [AssetStatuses.Archived, AssetLifecycle.Expire],     // already past that point
        [AssetStatuses.Deleted, AssetLifecycle.Submit],
        [AssetStatuses.Blocked, AssetLifecycle.Submit],
    ];

    [Theory]
    [MemberData(nameof(IllegalCombinations))]
    public void Every_other_combination_is_refused(string from, string action) =>
        Assert.False(AssetLifecycle.TryGetTarget(from, action, out _));

    [Fact]
    public void An_unknown_action_is_refused_from_every_status()
    {
        foreach (var status in new[] { AssetStatuses.Draft, AssetStatuses.InReview, AssetStatuses.Approved, AssetStatuses.Published, AssetStatuses.Expired, AssetStatuses.Archived })
            Assert.False(AssetLifecycle.TryGetTarget(status, "teleport", out _));
    }

    [Fact]
    public void The_full_round_trip_returns_to_where_it_started()
    {
        var status = AssetStatuses.Draft;
        foreach (var action in new[] { AssetLifecycle.Submit, AssetLifecycle.Approve, AssetLifecycle.Publish, AssetLifecycle.Expire, AssetLifecycle.Archive })
        {
            Assert.True(AssetLifecycle.TryGetTarget(status, action, out var next), $"{status} --{action}--> ?");
            status = next;
        }
        Assert.Equal(AssetStatuses.Archived, status);
        Assert.True(AssetLifecycle.TryGetTarget(status, AssetLifecycle.Withdraw, out var backToDraft));
        Assert.Equal(AssetStatuses.Draft, backToDraft);
    }
}
