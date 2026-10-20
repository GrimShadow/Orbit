namespace Dam.Shared.Content;

// ----------------------------------------------------------------------------------------------------------------- KPIs (ADR-0037)

/// <param name="From">First UTC day of the period (yyyy-MM-dd).</param>
/// <param name="To">Last UTC day of the period: today, for a KPI request.</param>
public sealed record AnalyticsPeriodDto(string From, string To, int Days);

/// <summary>The equal-length period immediately before.</summary>
public sealed record AnalyticsPreviousPeriodDto(string From, string To);

/// <param name="PublishedCount">Assets first published in the period.</param>
/// <param name="MedianHours">Median hours from an asset's creation to its first publication; null when nothing was published.</param>
/// <param name="P90Hours">90th percentile of the same hours.</param>
/// <param name="FirstPassRate">Share of those assets with no workflow task rejected before they were first published (an asset published without a workflow counts); 0 to 1.</param>
public sealed record PublishingEffortMetricsDto(long PublishedCount, double? MedianHours, double? P90Hours, double? FirstPassRate);

public sealed record PublishingEffortKpiDto(long PublishedCount, double? MedianHours, double? P90Hours, double? FirstPassRate, PublishingEffortMetricsDto Previous);

/// <param name="Uploads">Uploads created in the period that finished (stored, or recognised as an existing file).</param>
/// <param name="DuplicatesPrevented">Of those, the ones recognised as an existing file and not stored again.</param>
/// <param name="Rate">DuplicatesPrevented / Uploads, 0 to 1; null when there were no uploads.</param>
/// <param name="BytesNotStored">Size of the files not stored again.</param>
/// <param name="DuplicateAssetsInLibrary">Live assets whose file another live asset has too (duplicates allowed through, or from before the check). A snapshot: in the previous
/// period's figures it is estimated from the assets that existed then and are still live.</param>
public sealed record DuplicateMetricsDto(long Uploads, long DuplicatesPrevented, double? Rate, long BytesNotStored, long DuplicateAssetsInLibrary);

public sealed record DuplicateKpiDto(long Uploads, long DuplicatesPrevented, double? Rate, long BytesNotStored, long DuplicateAssetsInLibrary, DuplicateMetricsDto Previous);

/// <param name="UsedAssets">Live assets with at least one use in the period.</param>
/// <param name="ReusedAssets">Of those, the ones used at least twice.</param>
/// <param name="Rate">ReusedAssets / UsedAssets, 0 to 1; null when nothing was used.</param>
/// <param name="AvgUsesPerUsedAsset">Uses divided by UsedAssets.</param>
/// <param name="MultiChannelAssets">Used assets that were used on two or more channels.</param>
public sealed record ReuseMetricsDto(long UsedAssets, long ReusedAssets, double? Rate, double? AvgUsesPerUsedAsset, long MultiChannelAssets);

public sealed record ReuseKpiDto(long UsedAssets, long ReusedAssets, double? Rate, double? AvgUsesPerUsedAsset, long MultiChannelAssets, ReuseMetricsDto Previous);

/// <param name="Decided">Workflow tasks that had a due time and were decided in the period.</param>
/// <param name="OnTime">Of those, the ones decided on or before the due time.</param>
/// <param name="Attainment">OnTime / Decided, 0 to 1; null when nothing was decided.</param>
/// <param name="Breached">Tasks whose SLA breach was recorded in the period.</param>
/// <param name="OverdueOpen">Pending tasks past their due time right now. A snapshot, so null in the previous period's figures.</param>
public sealed record WorkflowSlaMetricsDto(long Decided, long OnTime, double? Attainment, long Breached, long? OverdueOpen);

public sealed record WorkflowSlaKpiDto(long Decided, long OnTime, double? Attainment, long Breached, long? OverdueOpen, WorkflowSlaMetricsDto Previous);

/// <summary>The four BRD key performance indicators for the last <c>Period.Days</c> UTC days, each with the equal period before it. Tenant-wide aggregates; no asset is named.</summary>
public sealed record KpiDto(
    AnalyticsPeriodDto Period, AnalyticsPreviousPeriodDto Previous, DateTimeOffset GeneratedAt, PublishingEffortKpiDto PublishingEffort,
    DuplicateKpiDto Duplicates, ReuseKpiDto Reuse, WorkflowSlaKpiDto WorkflowSla);

// ----------------------------------------------------------------------------------------------------------- per-asset analytics

/// <param name="ActorId">The Orbit user who reported the use; null when the system recorded it.</param>
public sealed record RecentUsageDto(Guid Id, Guid? ActorId, string EventType, string? Channel, DateTimeOffset OccurredAt);

/// <param name="Total">Uses in the period.</param>
/// <param name="PreviousTotal">Uses in the equal period before it.</param>
/// <param name="AllTimeTotal">Uses ever recorded.</param>
/// <param name="FirstUsedAt">First use ever; null when never used.</param>
/// <param name="ByDay">One entry per day of the period (UTC), zero-filled.</param>
/// <param name="ByChannel">The 20 busiest channels in the period; uses without a channel are "unspecified".</param>
/// <param name="Recent">The 20 most recent uses, whatever the period.</param>
public sealed record AssetAnalyticsDto(
    Guid AssetId, string Title, string Type, AnalyticsPeriodDto Period, long Total, long PreviousTotal, long AllTimeTotal, DateTimeOffset? FirstUsedAt,
    DateTimeOffset? LastUsedAt, IReadOnlyList<UsageDayDto> ByDay, IReadOnlyList<UsageTypeDto> ByType, IReadOnlyList<UsageChannelDto> ByChannel,
    IReadOnlyList<RecentUsageDto> Recent);

// ---------------------------------------------------------------------------------------------------------------------- reports

/// <param name="NamesAssets">Rows name individual assets, so only assets the reader may see appear in them.</param>
/// <param name="MaxDays">Longest period; for unused-assets, the longest idle window.</param>
public sealed record ReportInfoDto(string Name, string Title, string Description, bool NamesAssets, int DefaultDays, int MaxDays);

/// <param name="Type">text, count, number, percent (a ratio from 0 to 1), bytes, hours or datetime (ISO 8601).</param>
public sealed record ReportColumnDto(string Key, string Label, string Type);

/// <param name="Period">The days the report covers; for storage, today.</param>
/// <param name="Rows">One object per row, keyed by column key; a value is a string, a number, or null.</param>
/// <param name="Truncated">The report has more rows than are listed (unused and top assets are capped).</param>
public sealed record ReportDto(
    string Name, string Title, DateTimeOffset GeneratedAt, AnalyticsPeriodDto Period, IReadOnlyList<ReportColumnDto> Columns,
    IReadOnlyList<Dictionary<string, object?>> Rows, bool Truncated);

// ---------------------------------------------------------------------------------------------------------- scheduled reports

/// <param name="Report">kpis, top-assets, unused-assets, storage or workflow-sla.</param>
/// <param name="Days">Period in days (1 to 365; unused-assets up to 3650).</param>
/// <param name="Frequency">daily, weekly or monthly.</param>
/// <param name="HourUtc">Hour of the day (0 to 23, UTC) the report goes out.</param>
/// <param name="DayOfWeek">For weekly: 1 (Monday) to 7 (Sunday).</param>
/// <param name="DayOfMonth">For monthly: 1 to 31; a day beyond the end of a short month means its last day.</param>
/// <param name="RecipientUserIds">Active users of this tenant (not addresses): 1 to 50.</param>
public sealed record ReportScheduleInput(
    string Report, int Days, string Frequency, int HourUtc, int? DayOfWeek, int? DayOfMonth, IReadOnlyList<Guid> RecipientUserIds, bool Enabled);

/// <param name="LastSentAt">When the last run finished (or a manual send was made); null if never.</param>
/// <param name="LastError">Why the last run did not (fully) reach its recipients, e.g. "Email is not configured on this server."; null when it did.</param>
/// <param name="NextRunAt">When it is next due; null while the schedule is switched off.</param>
public sealed record ReportScheduleDto(
    Guid Id, string Report, int Days, string Frequency, int HourUtc, int? DayOfWeek, int? DayOfMonth, IReadOnlyList<Guid> RecipientUserIds, bool Enabled,
    DateTimeOffset? LastSentAt, string? LastError, DateTimeOffset? NextRunAt, DateTimeOffset CreatedAt);
