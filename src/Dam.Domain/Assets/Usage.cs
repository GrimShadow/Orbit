using System.Globalization;
using System.Text;
using System.Text.Json;
using Dam.Domain.Authorization;
using Dam.Domain.Common;

namespace Dam.Domain.Assets;

/// <summary>What was done with an asset. Console previews and thumbnails are deliberately not usage (ADR-0029).</summary>
public static class UsageEventTypes
{
    public const string View = "view";
    public const string Download = "download";
    public const string Share = "share";
    public const string Embed = "embed";
    public const string Place = "place";
    public const string Play = "play";

    public static readonly IReadOnlyList<string> All = [View, Download, Share, Embed, Place, Play];

    public static bool IsKnown(string? type) => type is not null && All.Contains(type);
}

/// <summary>
/// One recorded use of an asset: a console download or copy-link, a proof-of-play from a player, a portal or app request
/// (spec Step 2.7 "usage events"). Append-only. <see cref="AssetUsage"/> is the per-asset summary kept in step with it.
/// </summary>
public sealed class UsageEvent : TenantEntity
{
    private UsageEvent() { }

    public Guid AssetId { get; private set; }
    public string EventType { get; private set; } = "";
    public string? Channel { get; private set; }
    public string? Rendition { get; private set; }
    /// <summary>The DAM user who made the call, or null when the system itself records the event.</summary>
    public Guid? ActorId { get; private set; }
    /// <summary>When the use happened (the reporting system's clock), not when it was reported.</summary>
    public DateTimeOffset OccurredAt { get; private set; }
    /// <summary>The reporter's own id for the event; with the tenant it makes a retry or re-send harmless.</summary>
    public string? ClientEventId { get; private set; }
    public Dictionary<string, JsonElement>? Context { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static UsageEvent Create(Guid tenantId, Guid assetId, string eventType, string? channel, string? rendition, Guid? actorId,
        DateTimeOffset occurredAt, string? clientEventId, Dictionary<string, JsonElement>? context, DateTimeOffset now) =>
        new()
        {
            TenantId = tenantId,
            AssetId = assetId,
            EventType = eventType,
            Channel = UsageRules.Blank(channel),
            Rendition = UsageRules.Blank(rendition),
            ActorId = actorId,
            OccurredAt = occurredAt.ToUniversalTime(),
            ClientEventId = UsageRules.Blank(clientEventId),
            Context = context,
            CreatedAt = now,
        };
}

/// <summary>
/// The per-asset summary of its usage events: when it was last used, first used, and how often. Kept in the same
/// transaction as the events, so "what has not been used for a year" is an indexed read rather than a scan of every event.
/// An asset with no row has never been used.
/// </summary>
public sealed class AssetUsage : ITenantScoped
{
    private AssetUsage() { }

    public Guid AssetId { get; private set; }
    public Guid TenantId { get; private set; }
    public DateTimeOffset LastUsedAt { get; private set; }
    public DateTimeOffset FirstUsedAt { get; private set; }
    public long UseCount { get; private set; }
}

/// <summary>Pure rules for usage events, kept out of the handler so they can be tested without a database.</summary>
public static class UsageRules
{
    public const int MaxBatch = 500;
    public const int MaxClientEventIdLength = 100;
    public const int MaxChannelLength = 100;
    public const int MaxRenditionLength = 50;
    public const int MaxContextBytes = 4096;
    /// <summary>Clocks drift; an event a little ahead of ours is fine, one far ahead is a bug in the reporter.</summary>
    public static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(5);
    /// <summary>External systems batch hourly and history is backfilled, so the past is open this far.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(365 * 5);

    public static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>The time to record: the reported one, or <paramref name="now"/> when none was given. Null when out of range.</summary>
    public static DateTimeOffset? ResolveOccurredAt(DateTimeOffset? reported, DateTimeOffset now, out string? error)
    {
        error = null;
        if (reported is not { } at) return now;
        if (at > now + MaxFutureSkew) { error = "Cannot be more than 5 minutes in the future."; return null; }
        if (at < now - MaxAge) { error = "Cannot be more than 5 years in the past."; return null; }
        return at;
    }

    /// <summary>Why this context is not acceptable, or null: it must be a JSON object of at most 4 KB.</summary>
    public static string? ContextError(JsonElement? context)
    {
        if (context is not { } c || c.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (c.ValueKind != JsonValueKind.Object) return "Must be a JSON object.";
        return Encoding.UTF8.GetByteCount(c.GetRawText()) > MaxContextBytes ? "Too large (max 4 KB)." : null;
    }
}

/// <summary>
/// The time window behind "not used since". Either the last N days or an explicit date, never both; with neither it is the
/// last 30 days. An asset is idle when <c>coalesce(last used, created) &lt; cutoff</c>: one that has never been used is
/// judged from its creation, so a brand-new asset is never flagged.
/// </summary>
public static class IdleWindow
{
    public const int DefaultDays = 30, MaxDays = 3650;

    /// <summary>Resolves <paramref name="days"/> / <paramref name="before"/> to a cutoff, or explains what is wrong.</summary>
    public static DateTimeOffset? ResolveCutoff(int? days, string? before, DateTimeOffset now, out Dictionary<string, string[]>? errors)
    {
        errors = null;
        if (days is not null && !string.IsNullOrWhiteSpace(before))
        {
            errors = new() { ["days"] = ["Give either days or before, not both."], ["before"] = ["Give either days or before, not both."] };
            return null;
        }
        if (!string.IsNullOrWhiteSpace(before))
        {
            if (TryParseBefore(before, out var at)) return at;
            errors = new() { ["before"] = ["Must be an ISO date (2026-01-31) or date and time (2026-01-31T12:00:00Z)."] };
            return null;
        }
        var d = days ?? DefaultDays;
        if (d is < 1 or > MaxDays)
        {
            errors = new() { ["days"] = [$"Must be between 1 and {MaxDays}."] };
            return null;
        }
        return now.AddDays(-d);
    }

    /// <summary>A bare date means the start of that day, UTC; a date and time without an offset is read as UTC.</summary>
    public static bool TryParseBefore(string value, out DateTimeOffset at) =>
        DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out at);

    public static DateTimeOffset IdleSince(DateTimeOffset createdAt, DateTimeOffset? lastUsedAt) => lastUsedAt ?? createdAt;

    public static bool IsIdle(DateTimeOffset createdAt, DateTimeOffset? lastUsedAt, DateTimeOffset cutoff) => IdleSince(createdAt, lastUsedAt) < cutoff;

    /// <summary>Whole days from <paramref name="idleSince"/> to <paramref name="now"/>, never negative.</summary>
    public static int IdleDays(DateTimeOffset idleSince, DateTimeOffset now) => (int)Math.Max(0, Math.Floor((now - idleSince).TotalDays));

    /// <summary>The statuses an "unused assets" list may be asked for: a single status, or "all" for every live one.</summary>
    public const string AllStatuses = "all";

    public static bool IsKnownStatusFilter(string status) =>
        status == AllStatuses || AssetStatusSet.Contains(status);

    /// <summary>"all" means every status except deleted and blocked, which are never worth cleaning up.</summary>
    public static bool Includes(string filter, string status) =>
        filter == AllStatuses ? status is not (AssetStatuses.Deleted or AssetStatuses.Blocked) : status == filter;

    private static readonly HashSet<string> AssetStatusSet =
    [
        AssetStatuses.Draft, AssetStatuses.InReview, AssetStatuses.Approved, AssetStatuses.Published, AssetStatuses.Expired,
        AssetStatuses.Archived, AssetStatuses.Deleted, AssetStatuses.Blocked,
    ];
}

/// <summary>Calendar helpers for the analytics overview. All dates are UTC (the tenant time zone is UTC for now).</summary>
public static class AnalyticsPeriods
{
    public const int DefaultDays = 30, MaxDays = 365, UploadWeeks = 12;

    public static DateTimeOffset StartOfDay(DateTimeOffset at) => new(at.UtcDateTime.Date, TimeSpan.Zero);

    /// <summary>The Monday on or before <paramref name="at"/>, 00:00 UTC.</summary>
    public static DateTimeOffset StartOfWeek(DateTimeOffset at)
    {
        var day = at.UtcDateTime.Date;
        var back = ((int)day.DayOfWeek + 6) % 7;
        return new DateTimeOffset(day.AddDays(-back), TimeSpan.Zero);
    }
}
