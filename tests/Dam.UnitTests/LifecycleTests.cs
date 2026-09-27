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
        [AssetStatuses.Archived, AssetLifecycle.Purge, AssetStatuses.Deleted],
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
        [AssetStatuses.Expired, AssetLifecycle.Purge],       // must be archived first
        [AssetStatuses.Deleted, AssetLifecycle.Submit],
        [AssetStatuses.Deleted, AssetLifecycle.Purge],        // already deleted
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
    public void The_full_lifecycle_ends_in_deletion_and_withdraw_still_works_one_step_short_of_that()
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

        Assert.True(AssetLifecycle.TryGetTarget(status, AssetLifecycle.Purge, out var deleted));
        Assert.Equal(AssetStatuses.Deleted, deleted);
        Assert.False(AssetLifecycle.TryGetTarget(deleted, AssetLifecycle.Withdraw, out _));   // deleted is truly terminal
    }
}

public sealed class AssetRetentionFieldsTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Asset NewDraft() => Asset.CreateShell(Guid.NewGuid(), null, "image", "Hero", null, Now);

    [Fact]
    public void Setting_legal_hold_raises_an_event_only_when_it_actually_changes()
    {
        var asset = NewDraft();
        asset.PullEvents();   // drop AssetCreated
        Assert.False(asset.LegalHold);

        asset.SetLegalHold(false, Now);   // no-op: already off
        Assert.Empty(asset.PullEvents());

        asset.SetLegalHold(true, Now.AddMinutes(1));
        Assert.True(asset.LegalHold);
        var e = Assert.Single(asset.PullEvents());
        var changed = Assert.IsType<AssetLegalHoldChanged>(e);
        Assert.True(changed.Hold);

        asset.SetLegalHold(true, Now.AddMinutes(2));   // no-op: already on
        Assert.Empty(asset.PullEvents());

        asset.SetLegalHold(false, Now.AddMinutes(3));
        Assert.False(asset.LegalHold);
        Assert.False(Assert.IsType<AssetLegalHoldChanged>(Assert.Single(asset.PullEvents())).Hold);
    }

    [Fact]
    public void Scheduling_sets_both_timers_independently_and_either_can_be_cleared()
    {
        var asset = NewDraft();
        var publishAt = Now.AddDays(1);
        var expireAt = Now.AddDays(30);
        asset.SetSchedule(publishAt, expireAt, Now);
        Assert.Equal(publishAt, asset.PublishAt);
        Assert.Equal(expireAt, asset.ExpireAt);

        asset.SetSchedule(null, expireAt, Now);   // cancel just the publish timer
        Assert.Null(asset.PublishAt);
        Assert.Equal(expireAt, asset.ExpireAt);

        asset.SetSchedule(null, null, Now);
        Assert.Null(asset.PublishAt);
        Assert.Null(asset.ExpireAt);
    }

    [Fact]
    public void A_retention_policy_can_be_assigned_and_cleared()
    {
        var asset = NewDraft();
        var policyId = Guid.NewGuid();
        asset.SetRetentionPolicy(policyId, Now);
        Assert.Equal(policyId, asset.RetentionPolicyId);
        asset.SetRetentionPolicy(null, Now);
        Assert.Null(asset.RetentionPolicyId);
    }
}

public sealed class RetentionPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_policy_can_leave_either_day_count_unset_so_that_step_never_fires()
    {
        var tenant = Guid.NewGuid();
        var policy = RetentionPolicy.Create(tenant, "  Standard  ", archiveAfterDays: 30, deleteAfterDays: null, legalHoldExempt: false, null, Now);
        Assert.Equal("Standard", policy.Name);   // trimmed
        Assert.Equal(30, policy.ArchiveAfterDays);
        Assert.Null(policy.DeleteAfterDays);
        Assert.False(policy.LegalHoldExempt);
    }

    [Fact]
    public void Updating_a_policy_replaces_every_field()
    {
        var policy = RetentionPolicy.Create(Guid.NewGuid(), "Draft policy", 10, 20, false, null, Now);
        policy.Update("Final policy", 90, 365, true);
        Assert.Equal("Final policy", policy.Name);
        Assert.Equal(90, policy.ArchiveAfterDays);
        Assert.Equal(365, policy.DeleteAfterDays);
        Assert.True(policy.LegalHoldExempt);
    }
}
