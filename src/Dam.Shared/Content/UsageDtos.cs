using System.Text.Json;

namespace Dam.Shared.Content;

/// <param name="ClientEventId">The reporter's own id for this event (at most 100 characters). Sending the same one again is
/// counted as a duplicate instead of being recorded twice, so a retry is safe.</param>
/// <param name="EventType">view, download, share, embed, place or play.</param>
/// <param name="Channel">Where it happened: "console", a player, a portal, an app (at most 100 characters).</param>
/// <param name="Rendition">Which rendition or file was used, e.g. "original" or "web" (at most 50 characters).</param>
/// <param name="OccurredAt">When it happened. Defaults to now; at most 5 minutes ahead and 5 years back.</param>
/// <param name="Context">Free-form detail about the use, a JSON object of at most 4 KB.</param>
public sealed record UsageEventInput(
    string? ClientEventId, Guid AssetId, string EventType, string? Channel, string? Rendition, DateTimeOffset? OccurredAt, JsonElement? Context);

/// <param name="Accepted">Events recorded.</param>
/// <param name="Duplicates">Events whose clientEventId had already been recorded.</param>
/// <param name="Rejected">Events for an asset that does not exist or that the caller may not read (the two are not told apart).</param>
public sealed record RecordUsageResultDto(int Accepted, int Duplicates, int Rejected);

/// <param name="FolderPath">Display names of the folder and its ancestors, e.g. "Brand / Cars"; null for an asset outside any folder.</param>
/// <param name="LastUsedAt">Null when the asset has never been used.</param>
/// <param name="IdleSince">The last use, or the creation date when it has never been used.</param>
/// <param name="IdleDays">Whole days since <paramref name="IdleSince"/>.</param>
/// <param name="SizeBytes">Size of the current version's file; null for an asset without one yet.</param>
public sealed record UnusedAssetDto(
    Guid Id, string Title, string Type, string Status, Guid? FolderId, string? FolderPath, DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt, int UseCount, DateTimeOffset IdleSince, int IdleDays, long? SizeBytes);

/// <param name="Total">Assets matching, among those the caller may read.</param>
/// <param name="TotalIsExact">False when the caller's access is narrow enough that counting stopped at the scan limit; Total is then the count found so far.</param>
/// <param name="Cutoff">Assets last used (or created, if never used) before this moment are listed.</param>
public sealed record UnusedAssetsDto(IReadOnlyList<UnusedAssetDto> Items, int Total, bool TotalIsExact, string? NextCursor, DateTimeOffset Cutoff);

public sealed record UsageDayDto(string Date, long Count);
public sealed record UsageTypeDto(string EventType, long Count);
public sealed record UsageChannelDto(string Channel, long Count);

/// <param name="Total">Events in the period.</param>
/// <param name="PreviousTotal">Events in the same-length period immediately before, for a trend.</param>
/// <param name="ByDay">One entry per day of the period (UTC), zero-filled.</param>
/// <param name="ByChannel">The six busiest channels; events without a channel are reported as "unspecified".</param>
public sealed record UsageSummaryDto(
    long Total, long PreviousTotal, IReadOnlyList<UsageDayDto> ByDay, IReadOnlyList<UsageTypeDto> ByType, IReadOnlyList<UsageChannelDto> ByChannel);

public sealed record TopAssetDto(Guid AssetId, string Title, string Type, long Count);

/// <param name="WeekStart">The Monday (UTC) the week starts.</param>
public sealed record UploadWeekDto(string WeekStart, long Count);

public sealed record StorageTypeDto(string Type, long Count, long Bytes);

/// <param name="TotalBytes">Size of every non-deleted asset's current version.</param>
public sealed record StorageSummaryDto(long TotalBytes, IReadOnlyList<StorageTypeDto> ByType);

/// <summary>Library-wide figures behind the dashboard. Usage, uploads and storage are tenant-wide; only <see cref="TopAssets"/>
/// is limited to assets the caller may read.</summary>
public sealed record AnalyticsOverviewDto(
    DateTimeOffset GeneratedAt, int PeriodDays, UsageSummaryDto Usage, IReadOnlyList<TopAssetDto> TopAssets,
    IReadOnlyList<UploadWeekDto> Uploads, StorageSummaryDto Storage);
