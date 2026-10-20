using Dam.Domain.Common;

namespace Dam.Domain.Analytics;

public static class ScheduleFrequencies
{
    public const string Daily = "daily";
    public const string Weekly = "weekly";
    public const string Monthly = "monthly";

    public static readonly IReadOnlyList<string> All = [Daily, Weekly, Monthly];

    public static bool IsKnown(string? f) => f is not null && All.Contains(f);
}

/// <summary>When a scheduled report is next due (ADR-0037). All times UTC. A weekday is ISO (1 = Monday ... 7 = Sunday); a day of the month
/// beyond the end of a short month means that month's last day, so "the 31st" is still sent every month.</summary>
public static class ScheduleRules
{
    public const int MaxPerTenant = 50, MaxRecipients = 50;
    /// <summary>A run that has not been delivered this long after it was due is given up, and the schedule moves on to its next slot.</summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(24);

    /// <summary>The first slot strictly after <paramref name="after"/>.</summary>
    public static DateTimeOffset NextRun(string frequency, int hourUtc, int? dayOfWeek, int? dayOfMonth, DateTimeOffset after)
    {
        var from = after.ToUniversalTime();
        switch (frequency)
        {
            case ScheduleFrequencies.Weekly:
            {
                var wanted = dayOfWeek ?? 1;
                var date = DateOnly.FromDateTime(from.UtcDateTime);
                for (var i = 0; i < 8; i++)
                {
                    var d = date.AddDays(i);
                    if (IsoWeekday(d) == wanted && At(d, hourUtc) > from) return At(d, hourUtc);
                }
                throw new InvalidOperationException("Unreachable: a weekday recurs within eight days.");
            }
            case ScheduleFrequencies.Monthly:
            {
                var wanted = dayOfMonth ?? 1;
                var month = new DateOnly(from.Year, from.Month, 1);
                for (var i = 0; i < 3; i++)
                {
                    var m = month.AddMonths(i);
                    var d = new DateOnly(m.Year, m.Month, Math.Min(wanted, DateTime.DaysInMonth(m.Year, m.Month)));
                    if (At(d, hourUtc) > from) return At(d, hourUtc);
                }
                throw new InvalidOperationException("Unreachable: the next month's slot is always later.");
            }
            default:
            {
                var date = DateOnly.FromDateTime(from.UtcDateTime);
                return At(date, hourUtc) > from ? At(date, hourUtc) : At(date.AddDays(1), hourUtc);
            }
        }
    }

    public static int IsoWeekday(DateOnly d) => d.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)d.DayOfWeek;

    private static DateTimeOffset At(DateOnly d, int hour) => new(d.Year, d.Month, d.Day, hour, 0, 0, TimeSpan.Zero);
}

/// <summary>
/// An emailed report (MIS): which report, how many days it covers, how often it goes out, and to whom (tenant users, never arbitrary
/// addresses). The job sends each due schedule once per slot: <see cref="SentTo"/> remembers who already got this slot's email, so a retry after
/// a partial failure does not mail them twice. ADR-0037.
/// </summary>
public sealed class ReportSchedule : AggregateRoot
{
    private ReportSchedule() { }

    public string Report { get; private set; } = "";
    public int Days { get; private set; }
    public string Frequency { get; private set; } = ScheduleFrequencies.Weekly;
    public int HourUtc { get; private set; }
    public int? DayOfWeek { get; private set; }
    public int? DayOfMonth { get; private set; }
    public List<Guid> RecipientUserIds { get; private set; } = [];
    public bool Enabled { get; private set; } = true;
    public DateTimeOffset? LastSentAt { get; private set; }
    public string? LastError { get; private set; }
    /// <summary>The slot that is due next (or, once due, is being delivered); null while the schedule is switched off.</summary>
    public DateTimeOffset? NextRunAt { get; private set; }
    /// <summary>Recipients who already received the slot in <see cref="NextRunAt"/>.</summary>
    public List<Guid> SentTo { get; private set; } = [];
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static ReportSchedule Create(Guid tenantId, string report, int days, string frequency, int hourUtc, int? dayOfWeek, int? dayOfMonth,
        IEnumerable<Guid> recipients, bool enabled, Guid? createdBy, DateTimeOffset now)
    {
        var s = new ReportSchedule { TenantId = tenantId, CreatedBy = createdBy, CreatedAt = now };
        s.Apply(report, days, frequency, hourUtc, dayOfWeek, dayOfMonth, recipients, enabled, now);
        return s;
    }

    public void Update(string report, int days, string frequency, int hourUtc, int? dayOfWeek, int? dayOfMonth, IEnumerable<Guid> recipients, bool enabled, DateTimeOffset now) =>
        Apply(report, days, frequency, hourUtc, dayOfWeek, dayOfMonth, recipients, enabled, now);

    private void Apply(string report, int days, string frequency, int hourUtc, int? dayOfWeek, int? dayOfMonth, IEnumerable<Guid> recipients, bool enabled, DateTimeOffset now)
    {
        Report = report;
        Days = days;
        Frequency = frequency;
        HourUtc = hourUtc;
        DayOfWeek = frequency == ScheduleFrequencies.Weekly ? dayOfWeek : null;
        DayOfMonth = frequency == ScheduleFrequencies.Monthly ? dayOfMonth : null;
        RecipientUserIds = recipients.Distinct().ToList();
        Enabled = enabled;
        UpdatedAt = now;
        // Any edit starts again from now: a changed slot is never a slot already half-delivered.
        SentTo = [];
        NextRunAt = enabled ? ScheduleRules.NextRun(Frequency, HourUtc, DayOfWeek, DayOfMonth, now) : null;
        if (enabled) LastError = null;
    }

    public bool IsDue(DateTimeOffset now) => Enabled && NextRunAt is { } due && due <= now;

    /// <summary>Whether the slot has waited so long that it is better skipped than sent stale.</summary>
    public bool IsStale(DateTimeOffset now) => NextRunAt is { } due && now - due > ScheduleRules.GiveUpAfter;

    public void RecordSentTo(Guid userId)
    {
        if (!SentTo.Contains(userId)) SentTo = [.. SentTo, userId];
    }

    /// <summary>The slot is fully delivered (or deliberately finished): note what happened and move to the next slot.</summary>
    public void CompleteRun(DateTimeOffset now, string? note)
    {
        LastSentAt = now;
        LastError = note;
        SentTo = [];
        NextRunAt = Enabled ? ScheduleRules.NextRun(Frequency, HourUtc, DayOfWeek, DayOfMonth, now) : null;
    }

    /// <summary>A manual send: the same bookkeeping as a delivered slot, but the schedule keeps its place.</summary>
    public void RecordManualSend(DateTimeOffset now, string? note)
    {
        LastSentAt = now;
        LastError = note;
    }

    /// <summary>The slot could not be delivered (yet): keep it due, so the next run tries again, and say why.</summary>
    public void RecordFailure(string error) => LastError = error.Length > 500 ? error[..500] : error;

    /// <summary>The slot has waited too long: skip it, say why, and move to the next one.</summary>
    public void GiveUp(DateTimeOffset now, string reason)
    {
        LastError = (reason.Length > 460 ? reason[..460] : reason) + " Gave up on this run.";
        SentTo = [];
        NextRunAt = Enabled ? ScheduleRules.NextRun(Frequency, HourUtc, DayOfWeek, DayOfMonth, now) : null;
    }
}
