using System.Text.Json;

namespace Dam.Shared.Webhooks;

/// <param name="Succeeded24h">Deliveries that succeeded in the last 24 hours.</param>
/// <param name="Failed24h">Deliveries that gave up (all attempts used) in the last 24 hours.</param>
/// <param name="Pending">Deliveries still waiting for their first or next attempt.</param>
/// <param name="LastDeliveryAt">When the most recent delivery was created; null if none yet.</param>
/// <param name="LastStatus">Status of that delivery: pending, succeeded or failed.</param>
public sealed record WebhookStatsDto(int Succeeded24h, int Failed24h, int Pending, DateTimeOffset? LastDeliveryAt, string? LastStatus);

/// <param name="SecretLast4">Last four characters of the signing secret, to tell secrets apart. The secret itself is shown once, when created or rotated.</param>
/// <param name="Events">Event names or patterns: an exact name (<c>asset.published</c>), a prefix wildcard (<c>asset.*</c>) or <c>*</c> for everything.</param>
public sealed record WebhookDto(
    Guid Id, string Name, string Url, string SecretLast4, IReadOnlyList<string> Events, bool Active, Guid? CreatedBy,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, WebhookStatsDto Stats);

/// <param name="Secret">The signing secret. Returned only by this response; it cannot be read again, only rotated.</param>
public sealed record WebhookCreatedDto(WebhookDto Webhook, string Secret);

/// <param name="Secret">The new signing secret. Returned only by this response; the old one stops working at once.</param>
public sealed record WebhookSecretDto(WebhookDto Webhook, string Secret);

public sealed record WebhookInput(string Name, string Url, IReadOnlyList<string> Events, bool Active = true);

/// <param name="Category">asset, collection, workflow or webhook.</param>
/// <param name="Derived">True when the event is derived from another (for example <c>asset.published</c> from <c>asset.status.changed</c>).</param>
public sealed record WebhookEventTypeDto(string Name, string Category, string Description, bool Derived);

/// <param name="Status">pending, succeeded or failed.</param>
/// <param name="Attempt">Attempts made so far.</param>
/// <param name="MaxAttempts">Attempts made before the delivery is marked failed.</param>
/// <param name="NextAttemptAt">When the next attempt is due; null once succeeded or failed.</param>
/// <param name="ReplayOf">Set when this delivery was created by replaying another one.</param>
public sealed record WebhookDeliveryDto(
    Guid Id, Guid WebhookId, string WebhookName, Guid EventId, string EventType, string Status, int Attempt, int MaxAttempts,
    DateTimeOffset CreatedAt, DateTimeOffset? NextAttemptAt, DateTimeOffset? DeliveredAt, int? LastStatusCode, string? LastError,
    int? LastDurationMs, Guid? ReplayOf);

/// <param name="Payload">The exact JSON envelope sent: <c>{ id, type, tenantId, occurredAt, data }</c>.</param>
/// <param name="ResponseExcerpt">The first 2 KB of the receiver's last response body.</param>
public sealed record WebhookDeliveryDetailDto(
    Guid Id, Guid WebhookId, string WebhookName, Guid EventId, string EventType, string Status, int Attempt, int MaxAttempts,
    DateTimeOffset CreatedAt, DateTimeOffset? NextAttemptAt, DateTimeOffset? DeliveredAt, int? LastStatusCode, string? LastError,
    int? LastDurationMs, Guid? ReplayOf, JsonElement Payload, string? ResponseExcerpt);
