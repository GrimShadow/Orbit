using System.Text;
using RabbitMQ.Client;

namespace Dam.Infrastructure.Messaging;

public sealed class MessagingOptions
{
    public string RabbitUrl { get; set; } = "amqp://guest:guest@localhost:5672";
    public string Exchange { get; set; } = "dam.events";
    /// <summary>Total delivery attempts before a message is parked in the DLQ.</summary>
    public int MaxAttempts { get; set; } = 5;
    /// <summary>Delay before attempt 2; doubles for each later attempt.</summary>
    public int RetryBaseDelayMs { get; set; } = 2000;
    public ushort Prefetch { get; set; } = 8;
    public int OutboxPollMs { get; set; } = 500;
    public int OutboxBatchSize { get; set; } = 100;
}

/// <summary>What a consumer receives. <see cref="Payload"/> is the event's JSON.</summary>
public sealed record EventEnvelope(Guid MessageId, string Name, Guid TenantId, string? CorrelationId, string Payload, int Attempt);

/// <summary>
/// Base class for event handlers. Each consumer gets its own queue bound to <see cref="RoutingKeys"/>.
/// The host runs <see cref="HandleAsync"/> inside a DB transaction that also records the message id,
/// so a redelivered message is skipped and its effects commit exactly once.
/// </summary>
public abstract class EventConsumer
{
    public abstract string Name { get; }
    public abstract IReadOnlyList<string> RoutingKeys { get; }
    public abstract Task HandleAsync(EventEnvelope message, CancellationToken ct);
}

public static class Topology
{
    public const string Retry = "retry";

    public static string Queue(string consumer) => $"dam.q.{consumer}";
    public static string Dlq(string consumer) => $"dam.q.{consumer}.dlq";
    public static string RetryQueue(string consumer, int delayMs) => $"dam.q.{consumer}.retry.{delayMs}";

    /// <summary>Delay before the delivery that follows failed attempt <paramref name="failedAttempt"/> (1-based).</summary>
    public static int Delay(MessagingOptions o, int failedAttempt) => o.RetryBaseDelayMs * (1 << (failedAttempt - 1));

    public static async Task DeclareExchangeAsync(IChannel ch, MessagingOptions o, CancellationToken ct) =>
        await ch.ExchangeDeclareAsync(o.Exchange, ExchangeType.Topic, durable: true, cancellationToken: ct);

    /// <summary>
    /// main queue + one retry queue per backoff tier (a queue-level TTL avoids head-of-line blocking) + DLQ.
    /// Expired retry messages dead-letter back to the main queue via the default exchange.
    /// </summary>
    public static async Task DeclareConsumerAsync(IChannel ch, MessagingOptions o, EventConsumer c, CancellationToken ct)
    {
        await DeclareExchangeAsync(ch, o, ct);
        var main = Queue(c.Name);
        await ch.QueueDeclareAsync(main, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await ch.QueueDeclareAsync(Dlq(c.Name), durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        foreach (var rk in c.RoutingKeys)
            await ch.QueueBindAsync(main, o.Exchange, rk, cancellationToken: ct);
        for (var attempt = 1; attempt < o.MaxAttempts; attempt++)
        {
            var delay = Delay(o, attempt);
            await ch.QueueDeclareAsync(RetryQueue(c.Name, delay), durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?>
                {
                    ["x-message-ttl"] = delay,
                    ["x-dead-letter-exchange"] = "",
                    ["x-dead-letter-routing-key"] = main,
                }, cancellationToken: ct);
        }
    }

    public static string? Header(IReadOnlyBasicProperties p, string key) =>
        p.Headers is not null && p.Headers.TryGetValue(key, out var v) && v is byte[] b ? Encoding.UTF8.GetString(b) : null;
}

/// <summary>One long-lived AMQP connection per process; channels are created per component.</summary>
public sealed class RabbitConnection(MessagingOptions options) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _conn;

    public async Task<IConnection> GetAsync(CancellationToken ct)
    {
        if (_conn is { IsOpen: true }) return _conn;
        await _gate.WaitAsync(ct);
        try
        {
            _conn ??= await new ConnectionFactory { Uri = new Uri(options.RabbitUrl), AutomaticRecoveryEnabled = true }
                .CreateConnectionAsync(ct);
            return _conn;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_conn is { IsOpen: true })
                await _conn.CloseAsync(200, "shutdown", TimeSpan.FromSeconds(5), abort: false);
        }
        catch (Exception) { /* best effort on shutdown */ }
        _gate.Dispose();
    }
}
