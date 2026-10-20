using System.Globalization;

namespace Dam.Domain.Analytics;

/// <summary>
/// The window of a KPI request: <c>days</c> UTC calendar days ending today (today included), and the equal-length window right before it
/// (ADR-0037). Both are half-open on the instant side, so a moment belongs to exactly one day.
/// </summary>
public readonly record struct KpiPeriod(DateOnly From, DateOnly To, int Days)
{
    public const int DefaultDays = 30, MaxDays = 365;

    public static KpiPeriod For(DateOnly today, int days) => new(today.AddDays(-(days - 1)), today, days);

    public DateOnly PreviousFrom => From.AddDays(-Days);
    public DateOnly PreviousTo => From.AddDays(-1);

    /// <summary>The first instant of <see cref="From"/>.</summary>
    public DateTimeOffset Start => At(From);
    /// <summary>The first instant AFTER <see cref="To"/>: the exclusive end.</summary>
    public DateTimeOffset End => At(To.AddDays(1));
    public DateTimeOffset PreviousStart => At(PreviousFrom);

    private static DateTimeOffset At(DateOnly d) => new(d.Year, d.Month, d.Day, 0, 0, 0, TimeSpan.Zero);

    public static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>The small calculations behind the KPIs. Ratios are null, never zero, when there is nothing to measure.</summary>
public static class KpiMath
{
    /// <summary>numerator / denominator, or null when the denominator is not positive. Four decimals.</summary>
    public static double? Ratio(long numerator, long denominator) =>
        denominator <= 0 ? null : Math.Round((double)numerator / denominator, 4, MidpointRounding.AwayFromZero);

    public static double? Average(long total, long count) =>
        count <= 0 ? null : Math.Round((double)total / count, 2, MidpointRounding.AwayFromZero);

    /// <summary>Hours to two decimals; null stays null.</summary>
    public static double? Hours(double? hours) => hours is null ? null : Math.Round(hours.Value, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The p-th percentile (0..1) by linear interpolation between the closest ranks: the definition of Postgres's <c>percentile_cont</c>, which the
    /// KPI queries use. Kept here so the rule is written down and tested without a database; null for no values.
    /// </summary>
    public static double? Percentile(IReadOnlyCollection<double> values, double p)
    {
        if (values.Count == 0) return null;
        var sorted = values.Order().ToArray();
        var rank = p * (sorted.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (rank - lower);
    }
}
