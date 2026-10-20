namespace Dam.Application.Abstractions;

/// <summary>Uses of assets on one UTC day by event type and channel (the empty channel means none was given).</summary>
public sealed record UsageCell(DateOnly Day, string EventType, string Channel, long Uses);

public sealed record AssetUses(Guid AssetId, long Uses);

/// <param name="UsedAssets">Live assets with at least one use in the period.</param>
/// <param name="ReusedAssets">Of those, the ones used at least twice.</param>
/// <param name="MultiChannelAssets">Of those, the ones used on two or more channels (uses without a channel do not count as one).</param>
public sealed record ReuseAggregate(long UsedAssets, long ReusedAssets, long TotalUses, long MultiChannelAssets);

/// <param name="FirstPass">Of the assets first published in the period, those with no workflow task rejected before that.</param>
public sealed record PublishingAggregate(long Published, double? MedianHours, double? P90Hours, long FirstPass);

public sealed record DuplicateAggregate(long Uploads, long Prevented, long BytesNotStored);

public sealed record SlaAggregate(long Decided, long OnTime, long Breached);

public sealed record StepSla(string Workflow, string Step, long Decided, long OnTime, long Breached, long OverdueOpen);

/// <summary>What the library holds in one folder and asset type (the size of each live asset's current version). A null folder is "no folder".</summary>
public sealed record FolderTypeStorage(Guid? FolderId, string Type, long Assets, long Bytes);

public sealed record RecentUse(Guid Id, Guid? ActorId, string EventType, string? Channel, DateTimeOffset OccurredAt);

public sealed record AssetUsageTotals(long UseCount, DateTimeOffset? FirstUsedAt, DateTimeOffset? LastUsedAt);

/// <summary>
/// Everything the analytics reads need that is not a plain entity query (ADR-0037). Usage figures combine the daily rollup for finished days
/// with the live events for today and for any day whose events changed since it was rolled up, in one statement, so they are exact whether or not
/// the rollup job has run. All calls are for the current tenant.
/// </summary>
public interface IAnalyticsStore
{
    /// <param name="to">Exclusive.</param>
    /// <param name="assetId">Only this asset's uses.</param>
    Task<IReadOnlyList<UsageCell>> UsageCellsAsync(DateOnly from, DateOnly to, DateOnly today, Guid? assetId, CancellationToken ct);

    Task<IReadOnlyList<AssetUses>> TopAssetsAsync(DateOnly from, DateOnly to, DateOnly today, int limit, CancellationToken ct);

    Task<ReuseAggregate> ReuseAsync(DateOnly from, DateOnly to, DateOnly today, CancellationToken ct);

    /// <summary>Assets first published in [from, to), the median and 90th percentile hours from creation to first publication (percentile_cont), and how many were first pass.</summary>
    Task<PublishingAggregate> PublishingAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    Task<DuplicateAggregate> DuplicatesAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    /// <summary>Live assets whose checksum another live asset shares; with <paramref name="createdBefore"/>, only assets created before it (and sharing with such assets).</summary>
    Task<long> DuplicateAssetsAsync(DateTimeOffset? createdBefore, CancellationToken ct);

    Task<SlaAggregate> SlaAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    Task<long> OverdueOpenTasksAsync(DateTimeOffset now, CancellationToken ct);

    Task<IReadOnlyList<StepSla>> SlaByStepAsync(DateTimeOffset from, DateTimeOffset to, DateTimeOffset now, CancellationToken ct);

    Task<IReadOnlyList<FolderTypeStorage>> StorageAsync(CancellationToken ct);

    Task<IReadOnlyList<RecentUse>> RecentUsesAsync(Guid assetId, int limit, CancellationToken ct);

    Task<AssetUsageTotals> AssetTotalsAsync(Guid assetId, CancellationToken ct);
}

/// <summary>Rebuilds the daily rollup (ADR-0037). Runs inside a tenant-scoped scope.</summary>
public interface IUsageRollup
{
    /// <summary>
    /// Recomputes one day from its events (delete and re-insert in one transaction, so running it twice, or after a back-dated batch, never double counts)
    /// and clears the dirty mark it covered. With retention on, a day before <paramref name="retentionHorizon"/> whose month has been dropped is sealed: its raw
    /// events are gone, so recomputing would erase its history; its mark is cleared and the rollup kept. Returns whether the day was recomputed.
    /// </summary>
    Task<bool> RollupDayAsync(DateOnly day, DateOnly? retentionHorizon, CancellationToken ct);
}
