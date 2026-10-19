using Dam.Domain.Common;

namespace Dam.Domain.Webhooks;

/// <summary>
/// An outbound subscription (spec A4.1 <c>Webhook</c>, ADR-0031): matching domain events are POSTed to <see cref="Url"/> as signed JSON.
/// The signing secret exists in the clear only in the response that created or rotated it; here it is stored encrypted.
/// </summary>
public sealed class Webhook : AggregateRoot
{
    private Webhook() { }

    public string Name { get; private set; } = "";
    public string Url { get; private set; } = "";
    /// <summary>The secret encrypted with the server's <c>DAM_SECRETS_KEY</c> (see ISecretProtector).</summary>
    public string SecretCipher { get; private set; } = "";
    public string SecretLast4 { get; private set; } = "";
    public List<string> Events { get; private set; } = [];
    public bool Active { get; private set; } = true;
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Webhook Create(Guid tenantId, string name, string url, IEnumerable<string> events, bool active, string secretCipher, string secretLast4,
        Guid? createdBy, DateTimeOffset now) =>
        new()
        {
            TenantId = tenantId, Name = name.Trim(), Url = url.Trim(), Events = EventPattern.Normalize(events), Active = active,
            SecretCipher = secretCipher, SecretLast4 = secretLast4, CreatedBy = createdBy, CreatedAt = now, UpdatedAt = now,
        };

    public void Update(string name, string url, IEnumerable<string> events, bool active, DateTimeOffset now)
    {
        Name = name.Trim();
        Url = url.Trim();
        Events = EventPattern.Normalize(events);
        Active = active;
        UpdatedAt = now;
    }

    public void RotateSecret(string secretCipher, string secretLast4, DateTimeOffset now)
    {
        SecretCipher = secretCipher;
        SecretLast4 = secretLast4;
        UpdatedAt = now;
    }

    /// <summary>Does this webhook want an event with this name? Inactive webhooks want nothing.</summary>
    public bool Wants(string eventName) => Active && EventPattern.MatchesAny(Events, eventName);
}

/// <summary>
/// One event to be delivered to one webhook, with its attempt history. The body (<see cref="Payload"/>) is the exact JSON envelope that
/// is sent; the signature is computed over it at send time. Deliveries are history: the app role cannot delete them.
/// </summary>
public sealed class WebhookDelivery : TenantEntity
{
    private WebhookDelivery() { }

    public Guid WebhookId { get; private set; }
    public Guid EventId { get; private set; }
    public string EventType { get; private set; } = "";
    public string Payload { get; private set; } = "{}";
    public string Status { get; private set; } = DeliveryStatuses.Pending;
    public int Attempt { get; private set; }
    public DateTimeOffset? NextAttemptAt { get; private set; }
    public int? LastStatusCode { get; private set; }
    public string? LastError { get; private set; }
    public int? LastDurationMs { get; private set; }
    public string? ResponseExcerpt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? DeliveredAt { get; private set; }
    public Guid? ReplayOf { get; private set; }

    /// <summary>
    /// A new pending delivery. Its first attempt is made straight away by whoever created it; <paramref name="leaseUntil"/> keeps the
    /// retry job from also picking it up in the meantime (and picks it up after that if the creator died).
    /// </summary>
    public static WebhookDelivery Create(Guid tenantId, Guid webhookId, Guid eventId, string eventType, string payload, DateTimeOffset now,
        DateTimeOffset leaseUntil, Guid? replayOf = null) =>
        new()
        {
            TenantId = tenantId, WebhookId = webhookId, EventId = eventId, EventType = eventType, Payload = payload, CreatedAt = now,
            NextAttemptAt = leaseUntil, ReplayOf = replayOf,
        };

    public bool IsPending => Status == DeliveryStatuses.Pending;

    public void Succeeded(int statusCode, int durationMs, string? excerpt, DateTimeOffset now)
    {
        Status = DeliveryStatuses.Succeeded;
        NextAttemptAt = null;
        DeliveredAt = now;
        LastStatusCode = statusCode;
        LastDurationMs = durationMs;
        LastError = null;
        ResponseExcerpt = excerpt;
    }

    /// <summary>Records a failed attempt; <paramref name="retryAfter"/> null means there are no attempts left and the delivery is failed.</summary>
    public void AttemptFailed(int? statusCode, string error, int durationMs, string? excerpt, TimeSpan? retryAfter, DateTimeOffset now)
    {
        LastStatusCode = statusCode;
        LastError = error.Length > 500 ? error[..500] : error;
        LastDurationMs = durationMs;
        ResponseExcerpt = excerpt;
        if (retryAfter is { } wait)
        {
            Status = DeliveryStatuses.Pending;
            NextAttemptAt = now + wait;
        }
        else
        {
            Status = DeliveryStatuses.Failed;
            NextAttemptAt = null;
        }
    }
}
