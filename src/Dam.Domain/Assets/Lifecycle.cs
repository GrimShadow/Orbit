using Dam.Domain.Authorization;

namespace Dam.Domain.Assets;

/// <summary>
/// The asset lifecycle state machine (spec A4.2):
/// <code>
/// draft --submit--> in_review --approve--> approved --publish--> published --expire--> expired --archive--> archived
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

    public static readonly IReadOnlyList<string> Actions = [Submit, Approve, Reject, Publish, Withdraw, Expire, Archive];

    private static readonly Dictionary<(string From, string Action), string> Forward = new()
    {
        [(AssetStatuses.Draft, Submit)] = AssetStatuses.InReview,
        [(AssetStatuses.InReview, Approve)] = AssetStatuses.Approved,
        [(AssetStatuses.InReview, Reject)] = AssetStatuses.Draft,
        [(AssetStatuses.Approved, Publish)] = AssetStatuses.Published,
        [(AssetStatuses.Published, Expire)] = AssetStatuses.Expired,
        [(AssetStatuses.Expired, Archive)] = AssetStatuses.Archived,
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
