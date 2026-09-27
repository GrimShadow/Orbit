using Dam.Domain.Authorization;

namespace Dam.Domain.Assets;

/// <summary>
/// The asset lifecycle state machine (spec A4.2):
/// <code>
/// draft --submit--> in_review --approve--> approved --publish--> published --expire--> expired --archive--> archived --purge--> deleted
///   ^                   |
///   +------reject-------+
/// Any state (except draft, blocked, deleted) --withdraw--> draft
/// </code>
/// Pure and side-effect free, so the whole transition table is unit-tested directly; a command handler resolves the
/// target status here, applies its own external guards (permission, required metadata, workflow), then applies it via
/// <see cref="Asset.ChangeStatus"/>.
/// </summary>
public static class AssetLifecycle
{
    public const string Submit = "submit";
    public const string Approve = "approve";
    public const string Reject = "reject";
    public const string Publish = "publish";
    public const string Withdraw = "withdraw";
    public const string Expire = "expire";
    public const string Archive = "archive";
    /// <summary>Retention's own terminal step (spec A4.2: "archived --purge (retention)--> deleted"). Reachable manually
    /// (an operator with retention.manage) or automatically by the retention job (Step 1.6b) once a policy's
    /// delete_after_days has passed and no legal hold blocks it.</summary>
    public const string Purge = "purge";

    public static readonly IReadOnlyList<string> Actions = [Submit, Approve, Reject, Publish, Withdraw, Expire, Archive, Purge];

    private static readonly Dictionary<(string From, string Action), string> Forward = new()
    {
        [(AssetStatuses.Draft, Submit)] = AssetStatuses.InReview,
        [(AssetStatuses.InReview, Approve)] = AssetStatuses.Approved,
        [(AssetStatuses.InReview, Reject)] = AssetStatuses.Draft,
        [(AssetStatuses.Approved, Publish)] = AssetStatuses.Published,
        [(AssetStatuses.Published, Expire)] = AssetStatuses.Expired,
        [(AssetStatuses.Expired, Archive)] = AssetStatuses.Archived,
        [(AssetStatuses.Archived, Purge)] = AssetStatuses.Deleted,
    };

    /// <summary>Withdrawing returns an asset to draft from anywhere past draft, short of blocked/deleted (spec A4.2:
    /// "Any state -> withdraw -> draft"). Resubmitting is then just the ordinary submit guard again.</summary>
    private static readonly HashSet<string> WithdrawableFrom =
        [AssetStatuses.InReview, AssetStatuses.Approved, AssetStatuses.Published, AssetStatuses.Expired, AssetStatuses.Archived];

    /// <summary>The status <paramref name="action"/> would move an asset in <paramref name="from"/> to, or false if that
    /// combination is not a legal transition.</summary>
    public static bool TryGetTarget(string from, string action, out string to)
    {
        if (action == Withdraw)
        {
            to = AssetStatuses.Draft;
            return WithdrawableFrom.Contains(from);
        }
        return Forward.TryGetValue((from, action), out to!);
    }
}
