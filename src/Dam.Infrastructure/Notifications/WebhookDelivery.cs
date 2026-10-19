using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Dam.Application.Abstractions;
using Dam.Application.Webhooks;
using Dam.Domain.Webhooks;
using Dam.Infrastructure.Messaging;
using Dam.Infrastructure.Persistence;
using Dam.Shared.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dam.Infrastructure.Notifications;

/// <summary>
/// Makes one delivery attempt (ADR-0031). The attempt is first <i>claimed</i> with a single conditional UPDATE that also pushes the
/// delivery's next-attempt time out, so the creator's immediate attempt, the retry job and another worker can never send the same
/// delivery twice at once; a crash mid-attempt is retried after the lease. The HTTP call holds no database connection.
/// </summary>
public sealed class WebhookDeliverer(
    DamDbContext db, ISecretProtector secrets, OutboundHttp http, WebhookSettings settings, IClock clock, ITenantContext tenant, ILogger<WebhookDeliverer> log)
    : IWebhookDeliverer
{
    public async Task AttemptAsync(Guid deliveryId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var lease = now + settings.Timeout + TimeSpan.FromSeconds(60);
        var claimed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE webhook_deliveries SET attempt = attempt + 1, next_attempt_at = {lease}
            WHERE id = {deliveryId} AND tenant_id = {tenant.TenantId} AND status = 'pending' AND (next_attempt_at <= {now} OR attempt = 0)
            """, ct);
        if (claimed == 0) return;

        var delivery = await db.WebhookDeliveries.FirstAsync(d => d.Id == deliveryId, ct);
        await db.Entry(delivery).ReloadAsync(ct);   // the scope may already track an older copy
        var hook = await db.Webhooks.AsNoTracking().FirstOrDefaultAsync(w => w.Id == delivery.WebhookId, ct);

        if (hook is not { Active: true })
        {
            delivery.AttemptFailed(null, "The webhook is switched off or was deleted.", 0, null, retryAfter: null, now);
            await db.SaveChangesAsync(ct);
            return;
        }

        var body = delivery.Payload;
        var result = await SendAsync(hook, delivery, body, now, ct);
        var done = clock.UtcNow;
        if (result.Status is >= 200 and < 300)
            delivery.Succeeded(result.Status.Value, result.DurationMs, result.Excerpt, done);
        else
        {
            var wait = settings.Retry.DelayAfter(delivery.Attempt, Random.Shared.NextDouble);
            delivery.AttemptFailed(result.Status, result.Error ?? $"The receiver answered {result.Status}.", result.DurationMs, result.Excerpt, wait, done);
        }
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex)
        {
            // Most likely the webhook (and with it this delivery) was deleted while the request was in flight.
            log.LogWarning(ex, "Could not record the outcome of delivery {Delivery}", deliveryId);
        }
    }

    private sealed record Outcome(int? Status, string? Error, int DurationMs, string? Excerpt);

    private async Task<Outcome> SendAsync(Webhook hook, WebhookDelivery delivery, string body, DateTimeOffset now, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(settings.Timeout);
        try
        {
            var secret = secrets.Unprotect(hook.SecretCipher, SecretPurposes.Webhook);
            var ts = now.ToUnixTimeSeconds();
            using var request = new HttpRequestMessage(HttpMethod.Post, hook.Url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.UserAgent.ParseAdd("Orbit-Webhooks/1.0");
            request.Headers.TryAddWithoutValidation(WebhookSignature.EventHeader, delivery.EventType);
            request.Headers.TryAddWithoutValidation(WebhookSignature.DeliveryHeader, delivery.Id.ToString());
            request.Headers.TryAddWithoutValidation(WebhookSignature.TimestampHeader, ts.ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Headers.TryAddWithoutValidation(WebhookSignature.SignatureHeader, WebhookSignature.Sign(secret, ts, body));

            using var response = await http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var excerpt = await OutboundHttp.ReadExcerptAsync(response.Content, settings.ResponseExcerptBytes, timeout.Token);
            return new Outcome((int)response.StatusCode, response.IsSuccessStatusCode ? null : $"The receiver answered {(int)response.StatusCode}.", (int)sw.ElapsedMilliseconds, excerpt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new Outcome(null, OutboundHttp.Explain(ex, timeout.IsCancellationRequested), (int)sw.ElapsedMilliseconds, null);
        }
    }
}

/// <summary>
/// Runs "attempt this delivery now" requests in the background on a few workers, so a slow receiver never delays the consumer that
/// created the delivery (or other tenants' deliveries). If the queue is ever full, the retry job picks the delivery up after its lease.
/// </summary>
public sealed class WebhookDispatcher(IServiceScopeFactory scopes, ILogger<WebhookDispatcher> log) : BackgroundService
{
    private const int Workers = 8;
    private readonly Channel<(Guid Tenant, Guid Delivery)> _queue = Channel.CreateBounded<(Guid, Guid)>(new BoundedChannelOptions(10_000)
        { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = false });

    public bool Enqueue(Guid tenantId, Guid deliveryId) => _queue.Writer.TryWrite((tenantId, deliveryId));

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, Workers).Select(_ => RunAsync(stoppingToken)));

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (tenantId, deliveryId) in _queue.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    scope.ServiceProvider.GetRequiredService<MutableTenantContext>().TenantId = tenantId;
                    await scope.ServiceProvider.GetRequiredService<IWebhookDeliverer>().AttemptAsync(deliveryId, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Webhook delivery {Delivery} could not be attempted; the retry job will pick it up", deliveryId);
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}

/// <summary>
/// Fans each public domain event out to the tenant's matching, active webhooks as pending deliveries, then (after the transaction has
/// committed) hands them to the <see cref="WebhookDispatcher"/> for an immediate attempt. Failures from there on are the retry job's.
/// </summary>
public sealed class WebhookConsumer(DamDbContext db, IClock clock, WebhookSettings settings, ITenantContext tenant, WebhookDispatcher dispatcher) : EventConsumer
{
    private readonly List<Guid> _created = [];

    public override string Name => "webhooks";
    public override IReadOnlyList<string> RoutingKeys => EventCatalogue.SourceEvents;

    public override async Task HandleAsync(EventEnvelope message, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var events = EventCatalogue.Expand(message.Name, message.Payload, message.MessageId, now);
        if (events.Count == 0) return;
        var hooks = await db.Webhooks.AsNoTracking().Where(w => w.Active).ToListAsync(ct);
        foreach (var hook in hooks)
            foreach (var e in events.Where(e => hook.Wants(e.Type)))
            {
                if (await db.WebhookDeliveries.AnyAsync(d => d.WebhookId == hook.Id && d.EventId == e.Id && d.ReplayOf == null, ct)) continue;
                var delivery = WebhookDelivery.Create(tenant.TenantId, hook.Id, e.Id, e.Type, WebhookEnvelope.Build(e, tenant.TenantId), now, now + settings.Lease);
                db.WebhookDeliveries.Add(delivery);
                _created.Add(delivery.Id);
            }
    }

    public override Task AfterCommitAsync(EventEnvelope message, CancellationToken ct)
    {
        foreach (var id in _created) dispatcher.Enqueue(tenant.TenantId, id);
        return Task.CompletedTask;
    }
}
