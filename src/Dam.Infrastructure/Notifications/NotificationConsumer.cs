using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Application.Notifications;
using Dam.Application.Webhooks;
using Dam.Domain.Common;
using Dam.Domain.Identity;
using Dam.Domain.Notifications;
using Dam.Domain.Webhooks;
using Dam.Infrastructure.Messaging;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dam.Infrastructure.Notifications;

/// <summary>
/// Turns domain events into notifications (ADR-0031): in-app rows and email for the people the rules name, and a short message in
/// every chat channel subscribed to the event. The consumer is at-least-once, so each delivery is recorded in a ledger unique per
/// (event, channel, recipient) and committed on its own, apart from the message's transaction: a redelivery skips what already went out,
/// and an email failing never takes a person's in-app notification with it. A failure of any channel fails the message, which the host
/// retries with backoff and finally parks in the dead-letter queue.
/// </summary>
public sealed class NotificationConsumer(
    NotificationPlanner planner, DamDbContext db, IEmailSender email, IChatSender chat, ISecretProtector secrets, NotificationSettings settings,
    IServiceScopeFactory scopes, ITenantContext tenant, IClock clock, ILogger<NotificationConsumer> log) : EventConsumer
{
    public const string InApp = "in_app", Email = "email", Chat = "chat";

    public override string Name => "notifications";
    public override IReadOnlyList<string> RoutingKeys => EventCatalogue.SourceEvents;

    public override async Task HandleAsync(EventEnvelope message, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(message.Payload);
        var payload = doc.RootElement;
        var failures = new List<Exception>();

        if (await planner.PlanAsync(message.Name, payload, ct) is { } plan)
            await NotifyPeopleAsync(message, payload, plan, failures, ct);

        await PostToChannelsAsync(message, payload, failures, ct);

        if (failures.Count == 1) throw failures[0];
        if (failures.Count > 1) throw new AggregateException("Some notifications could not be delivered.", failures);
    }

    // ---- people ------------------------------------------------------------------------------------------------

    private async Task NotifyPeopleAsync(EventEnvelope message, JsonElement payload, NotificationPlan plan, List<Exception> failures, CancellationToken ct)
    {
        var eventId = EventIdOf(payload, message);
        var category = NotificationCategories.Find(plan.Content.Category)!;
        var stored = (await db.NotificationPreferences.AsNoTracking().Where(p => plan.Recipients.Contains(p.UserId) && p.Category == category.Key).ToListAsync(ct))
            .ToDictionary(p => p.UserId);
        var deliveries = RecipientPlanner.Plan(category, plan.Recipients, stored, email.IsEnabled);
        if (deliveries.Count == 0) return;

        var tenantName = (await db.Tenants.AsNoTracking().Select(t => t.Name).FirstOrDefaultAsync(ct)) ?? "";
        var people = await db.Users.AsNoTracking().Where(u => deliveries.Select(d => d.UserId).Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);

        foreach (var d in deliveries)
        {
            try
            {
                if (d.InApp) await RecordInAppAsync(eventId, d.UserId, plan.Content, ct);
                if (d.Email && people.TryGetValue(d.UserId, out var person) && person.Email.Length > 0)
                    await SendEmailAsync(eventId, person, tenantName, plan.Content, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Notification for user {User} failed", d.UserId);
                failures.Add(ex);
            }
        }
    }

    private async Task RecordInAppAsync(Guid eventId, Guid userId, NotificationContent c, CancellationToken ct)
    {
        await using var scope = Autonomous();
        var inner = scope.ServiceProvider.GetRequiredService<DamDbContext>();
        await using var tx = await inner.Database.BeginTransactionAsync(ct);
        if (!await ClaimAsync(inner, eventId, InApp, userId.ToString(), ct)) return;
        inner.Notifications.Add(Notification.Create(tenant.TenantId, userId, eventId, c.Type, c.Category, c.Title, c.Body, c.LinkPath, c.AssetId, clock.UtcNow));
        await inner.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task SendEmailAsync(Guid eventId, User person, string tenantName, NotificationContent c, CancellationToken ct)
    {
        if (await db.NotificationDeliveries.AsNoTracking().AnyAsync(d => d.EventId == eventId && d.Channel == Email && d.Recipient == person.Id.ToString(), ct)) return;
        var rendered = EmailTemplates.Render(new EmailModel(tenantName, c.Title, c.Body, EmailTemplates.Absolute(settings.ConsolePublicUrl, c.LinkPath)));
        await email.SendAsync(new EmailMessage(person.Email, person.DisplayName, rendered.Subject, rendered.Html, rendered.Text), ct);
        // Recorded only now that the server accepted the message. If this write is lost, a retry may send it again: at-least-once.
        await using var scope = Autonomous();
        var inner = scope.ServiceProvider.GetRequiredService<DamDbContext>();
        await ClaimAsync(inner, eventId, Email, person.Id.ToString(), ct);
    }

    // ---- chat channels -----------------------------------------------------------------------------------------

    private async Task PostToChannelsAsync(EventEnvelope message, JsonElement payload, List<Exception> failures, CancellationToken ct)
    {
        var channels = await db.NotificationChannels.AsNoTracking().Where(c => c.Active).ToListAsync(ct);
        if (channels.Count == 0) return;
        var events = EventCatalogue.Expand(message.Name, message.Payload, message.MessageId, clock.UtcNow);
        var matches = (from c in channels from e in events where c.Wants(e.Type) select (Channel: c, Event: e)).ToList();
        if (matches.Count == 0) return;

        foreach (var (channel, e) in matches)
        {
            try
            {
                if (await db.NotificationDeliveries.AsNoTracking().AnyAsync(d => d.EventId == e.Id && d.Channel == Chat && d.Recipient == channel.Id.ToString(), ct)) continue;
                var (title, body, link) = await planner.DescribeAsync(e.Type, payload, ct);
                var result = await chat.SendAsync(channel.Kind, secrets.Unprotect(channel.UrlCipher, SecretPurposes.ChatUrl),
                    new ChatMessage(title, body, EmailTemplates.Absolute(settings.ConsolePublicUrl, link)), ct);
                if (!result.Ok) throw new InvalidOperationException($"Chat channel '{channel.Name}': {result.Error ?? "failed"}");
                await using var scope = Autonomous();
                await ClaimAsync(scope.ServiceProvider.GetRequiredService<DamDbContext>(), e.Id, Chat, channel.Id.ToString(), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Posting to chat channel {Channel} failed", channel.Id);
                failures.Add(ex);
            }
        }
    }

    // ---- plumbing ----------------------------------------------------------------------------------------------

    /// <summary>A fresh scope for the same tenant, whose database work commits independently of the message's transaction.</summary>
    private AsyncServiceScope Autonomous()
    {
        var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<MutableTenantContext>().TenantId = tenant.TenantId;
        return scope;
    }

    /// <summary>Writes the ledger row. False means it already existed: this was delivered before.</summary>
    private async Task<bool> ClaimAsync(DamDbContext inner, Guid eventId, string channel, string recipient, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var inserted = await inner.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO notification_deliveries (id, tenant_id, event_id, channel, recipient, created_at)
            VALUES ({Uuid7.NewGuid()}, {tenant.TenantId}, {eventId}, {channel}, {recipient}, {now}) ON CONFLICT DO NOTHING
            """, ct);
        return inserted == 1;
    }

    private static Guid EventIdOf(JsonElement payload, EventEnvelope message) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("EventId", out var e) && e.TryGetGuid(out var g) ? g : message.MessageId;
}
