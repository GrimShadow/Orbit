using Dam.Domain.Common;
using Dam.Domain.Webhooks;

namespace Dam.Domain.Notifications;

/// <param name="Key">Stable id used by the preferences API.</param>
public sealed record NotificationCategory(string Key, string Label, string Description, bool DefaultInApp, bool DefaultEmail);

/// <summary>The fixed set of things a person can choose to hear about. Email is on by default only for things that wait for them.</summary>
public static class NotificationCategories
{
    public const string WorkflowTask = "workflow_task";
    public const string WorkflowOutcome = "workflow_outcome";
    public const string Sla = "sla";
    public const string AssetLifecycle = "asset_lifecycle";

    public static readonly IReadOnlyList<NotificationCategory> All =
    [
        new(WorkflowTask, "Approvals waiting for me", "A new approval task you can decide is opened.", true, true),
        new(WorkflowOutcome, "Outcome of my submissions", "An asset you submitted or own is approved or rejected.", true, false),
        new(Sla, "Overdue approvals", "An approval you can decide has passed its deadline.", true, true),
        new(AssetLifecycle, "Asset expiry and archiving", "An asset you own expires or is archived.", true, false),
    ];

    public static NotificationCategory? Find(string key) => All.FirstOrDefault(c => c.Key == key);
    public static bool IsKnown(string key) => Find(key) is not null;
}

public static class ChannelKinds
{
    public const string Slack = "slack";
    public const string Teams = "teams";
    public static readonly IReadOnlyList<string> All = [Slack, Teams];
}

/// <summary>An in-app notification for one person. Only its owner can read it or mark it read.</summary>
public sealed class Notification : TenantEntity
{
    private Notification() { }

    public Guid UserId { get; private set; }
    public string Type { get; private set; } = "";
    public string Category { get; private set; } = "";
    public string Title { get; private set; } = "";
    public string Body { get; private set; } = "";
    public string? LinkUrl { get; private set; }
    public Guid? AssetId { get; private set; }
    /// <summary>The domain event it came from (one notification per event and person).</summary>
    public Guid EventId { get; private set; }
    public DateTimeOffset? ReadAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static Notification Create(Guid tenantId, Guid userId, Guid eventId, string type, string category, string title, string body, string? linkUrl,
        Guid? assetId, DateTimeOffset now) =>
        new()
        {
            TenantId = tenantId, UserId = userId, EventId = eventId, Type = type, Category = category, Title = Clip(title, 200), Body = Clip(body, 2000),
            LinkUrl = linkUrl, AssetId = assetId, CreatedAt = now,
        };

    public void MarkRead(DateTimeOffset now) => ReadAt ??= now;

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}

/// <summary>A person's choice for one category. A missing row means "use the category's default".</summary>
public sealed class NotificationPreference : TenantEntity
{
    private NotificationPreference() { }

    public Guid UserId { get; private set; }
    public string Category { get; private set; } = "";
    public bool InApp { get; private set; }
    public bool Email { get; private set; }

    public static NotificationPreference Create(Guid tenantId, Guid userId, string category, bool inApp, bool email) =>
        new() { TenantId = tenantId, UserId = userId, Category = category, InApp = inApp, Email = email };

    public void Set(bool inApp, bool email)
    {
        InApp = inApp;
        Email = email;
    }

    /// <summary>The effective (in-app, email) setting for a category given a person's stored choice, if any.</summary>
    public static (bool InApp, bool Email) Effective(NotificationCategory category, NotificationPreference? stored) =>
        stored is null ? (category.DefaultInApp, category.DefaultEmail) : (stored.InApp, stored.Email);
}

/// <summary>A tenant-level chat destination (Slack or Teams incoming webhook) that receives a short message for matching events.</summary>
public sealed class NotificationChannel : AggregateRoot
{
    private NotificationChannel() { }

    public string Kind { get; private set; } = "";
    public string Name { get; private set; } = "";
    /// <summary>The incoming-webhook URL encrypted at rest: it is a bearer secret (anyone holding it can post to the channel).</summary>
    public string UrlCipher { get; private set; } = "";
    public string UrlHint { get; private set; } = "";
    public List<string> Events { get; private set; } = [];
    public bool Active { get; private set; } = true;
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static NotificationChannel Create(Guid tenantId, string kind, string name, string urlCipher, string urlHint, IEnumerable<string> events, bool active,
        Guid? createdBy, DateTimeOffset now) =>
        new()
        {
            TenantId = tenantId, Kind = kind, Name = name.Trim(), UrlCipher = urlCipher, UrlHint = urlHint, Events = EventPattern.Normalize(events),
            Active = active, CreatedBy = createdBy, CreatedAt = now, UpdatedAt = now,
        };

    public void Update(string kind, string name, IEnumerable<string> events, bool active, DateTimeOffset now)
    {
        Kind = kind;
        Name = name.Trim();
        Events = EventPattern.Normalize(events);
        Active = active;
        UpdatedAt = now;
    }

    public void ReplaceUrl(string urlCipher, string urlHint, DateTimeOffset now)
    {
        UrlCipher = urlCipher;
        UrlHint = urlHint;
        UpdatedAt = now;
    }

    public bool Wants(string eventName) => Active && EventPattern.MatchesAny(Events, eventName);

    /// <summary>A masked form safe to show: scheme and host, then the last four characters.</summary>
    public static string Hint(string url)
    {
        var u = new Uri(url);
        var tail = url.Length >= 4 ? url[^4..] : url;
        return $"{u.Scheme}://{u.Host}/…{tail}";
    }
}

/// <summary>
/// Remembers what has already been delivered so an event redelivered by the broker never notifies the same person (or channel) twice.
/// Unique per (event, channel, recipient): channel is in_app, email or chat; recipient is a user id or a chat channel id.
/// </summary>
public sealed class NotificationDelivery : TenantEntity
{
    private NotificationDelivery() { }

    public Guid EventId { get; private set; }
    public string Channel { get; private set; } = "";
    public string Recipient { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }

    public static NotificationDelivery Create(Guid tenantId, Guid eventId, string channel, string recipient, DateTimeOffset now) =>
        new() { TenantId = tenantId, EventId = eventId, Channel = channel, Recipient = recipient, CreatedAt = now };
}

/// <summary>How far the SIEM forwarder has got in a tenant's audit chain. Advanced only after a batch was sent.</summary>
public sealed class SiemCursor : TenantEntity
{
    private SiemCursor() { }

    public long LastChainNo { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static SiemCursor Create(Guid tenantId, long lastChainNo, DateTimeOffset now) => new() { TenantId = tenantId, LastChainNo = lastChainNo, UpdatedAt = now };

    public void Advance(long lastChainNo, DateTimeOffset now)
    {
        LastChainNo = lastChainNo;
        UpdatedAt = now;
    }
}
