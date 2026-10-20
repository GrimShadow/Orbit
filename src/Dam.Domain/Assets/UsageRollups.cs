using System.Globalization;
using Dam.Domain.Common;

namespace Dam.Domain.Assets;

/// <summary>
/// One row of the daily rollup (ADR-0037): how many times an asset was used in one UTC day, by event type and channel. <see cref="Channel"/> is
/// the empty string when the event had none, never null, so it can be part of the key. Rebuilt from the day's events by the rollup job;
/// never edited by hand.
/// </summary>
public sealed class UsageDaily : ITenantScoped
{
    private UsageDaily() { }

    public Guid TenantId { get; private set; }
    public DateOnly Day { get; private set; }
    public Guid AssetId { get; private set; }
    public string EventType { get; private set; } = "";
    public string Channel { get; private set; } = "";
    public long Uses { get; private set; }
}

/// <summary>A tenant-day whose events changed since it was last rolled up. <see cref="MarkSeq"/> grows with every mark, so a rollup that read
/// the day before a later event landed can tell its work no longer covers the mark and leaves it for the next run.</summary>
public sealed class UsageRollupDirty : ITenantScoped
{
    private UsageRollupDirty() { }

    public Guid TenantId { get; private set; }
    public DateOnly Day { get; private set; }
    public long MarkSeq { get; private set; }
    public DateTimeOffset MarkedAt { get; private set; }
}

/// <summary>A client event id that has been recorded: what makes re-sending a batch harmless (<c>usage_events</c> is partitioned, so a unique
/// index there cannot span partitions).</summary>
public sealed class UsageEventKey : ITenantScoped
{
    private UsageEventKey() { }

    public Guid TenantId { get; private set; }
    public string ClientEventId { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
}

/// <summary>Names and calendar rules for the monthly partitions of <c>usage_events</c> and for retention (ADR-0037).</summary>
public static class UsagePartitions
{
    public const string Table = "usage_events";
    public const string DefaultPartition = "usage_events_default";
    /// <summary>The current month and this many following ones always exist, so an event never needs the default partition.</summary>
    public const int MonthsAhead = 3;

    public static DateOnly MonthStart(DateOnly day) => new(day.Year, day.Month, 1);

    public static string NameFor(DateOnly month) => $"{Table}_{month.Year:0000}_{month.Month:00}";

    /// <summary>The month a partition name stands for (<c>usage_events_2026_10</c>), or false for any other name (the default partition...).</summary>
    public static bool TryParseMonth(string name, out DateOnly month)
    {
        month = default;
        var prefix = Table + "_";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length != prefix.Length + 7 || name[prefix.Length + 4] != '_') return false;
        if (!int.TryParse(name.AsSpan(prefix.Length, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y)
            || !int.TryParse(name.AsSpan(prefix.Length + 5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var m)
            || y is < 1 or > 9999 || m is < 1 or > 12) return false;
        month = new DateOnly(y, m, 1);
        return true;
    }

    /// <summary>The months that must exist on <paramref name="today"/>: the current one and the next <see cref="MonthsAhead"/>.</summary>
    public static IReadOnlyList<DateOnly> Needed(DateOnly today) =>
        Enumerable.Range(0, MonthsAhead + 1).Select(i => MonthStart(today).AddMonths(i)).ToList();

    /// <summary>With retention on, whole months before this day are dropped: the first day of the month <paramref name="retentionMonths"/> before
    /// the current one, so at least that many full months (and the current one) are always kept. Null when retention is off.</summary>
    public static DateOnly? RetentionHorizon(DateOnly today, int retentionMonths) =>
        retentionMonths > 0 ? MonthStart(today).AddMonths(-retentionMonths) : null;

    /// <summary>The first day of the month after <paramref name="month"/> (a partition covers <c>[month, NextMonth(month))</c>).</summary>
    public static DateOnly NextMonth(DateOnly month) => month.AddMonths(1);

    public static DateTimeOffset StartOf(DateOnly day) => new(day.Year, day.Month, day.Day, 0, 0, 0, TimeSpan.Zero);

    public static DateOnly DayOf(DateTimeOffset at) => DateOnly.FromDateTime(at.UtcDateTime);
}
