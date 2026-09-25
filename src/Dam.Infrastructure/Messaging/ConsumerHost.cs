using System.Text;
using Dam.Application.Abstractions;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Dam.Infrastructure.Messaging;

/// <summary>Worker-side tenant context: set per message from the `tenant_id` header before any DB access.</summary>
public sealed class MutableTenantContext : ITenantContext
{
    public Guid TenantId { get; set; }
}

public sealed class SystemUser : ICurrentUser
{
    public bool IsAuthenticated => false;
    public Guid? UserId => null;
    public string? Subject => null;
    public IReadOnlyCollection<string> Roles => [];
}

/// <summary>
/// Runs one <typeparamref name="T"/> against its queue. Per delivery: open a DB transaction, insert
/// (consumer, message_id) into processed_messages, run the handler, commit, ack. A duplicate delivery hits the
/// primary key and is acked untouched. On failure the transaction rolls back and the message goes to the next
/// retry tier, or to the DLQ once <see cref="MessagingOptions.MaxAttempts"/> is reached.
/// </summary>
public sealed class ConsumerHost<T>(
    IServiceProvider services, RabbitConnection rabbit, MessagingOptions options, ILogger<ConsumerHost<T>> log)
    : IHostedService, IAsyncDisposable where T : EventConsumer
{
    private IChannel? _channel;
    private string _name = "";
    private string? _consumerTag;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Task, byte> _inFlight = new();

    public async Task StartAsync(CancellationToken ct)
    {
        string queue;
        await using (var scope = services.CreateAsyncScope())
        {
            var probe = scope.ServiceProvider.GetRequiredService<T>();
            _name = probe.Name;
            queue = Topology.Queue(_name);
            _channel = await (await rabbit.GetAsync(ct)).CreateChannelAsync(cancellationToken: ct);
            await Topology.DeclareConsumerAsync(_channel, options, probe, ct);
        }
        await _channel.BasicQosAsync(0, options.Prefetch, false, ct);
        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += (_, ea) => TrackAsync(OnMessageAsync(ea));
        _consumerTag = await _channel.BasicConsumeAsync(queue, autoAck: false, consumer, ct);
    }

    /// <summary>Graceful stop: cancel the consumer, let in-flight messages finish and ack, then close the channel.</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        if (_channel is null || _channel.IsClosed) return;
        if (_consumerTag is not null) await _channel.BasicCancelAsync(_consumerTag, cancellationToken: ct);
        try { await Task.WhenAll(_inFlight.Keys).WaitAsync(TimeSpan.FromSeconds(20), ct); }
        catch (TimeoutException) { log.LogWarning("{Consumer}: in-flight messages did not finish within 20s", _name); }
        await _channel.CloseAsync(cancellationToken: ct);
    }

    private async Task TrackAsync(Task work)
    {
        _inFlight[work] = 0;
        try { await work; }
        finally { _inFlight.TryRemove(work, out _); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null) await _channel.DisposeAsync();
    }

    private async Task OnMessageAsync(BasicDeliverEventArgs ea)
    {
        var ch = _channel!;
        var p = ea.BasicProperties;
        var body = ea.Body.ToArray();
        var attempt = int.TryParse(Topology.Header(p, "x-attempt"), out var a) ? a : 1;

        if (!Guid.TryParse(p.MessageId, out var messageId) || !Guid.TryParse(Topology.Header(p, "tenant_id"), out var tenantId)
            || tenantId == Guid.Empty)
        {
            await ParkAsync(ch, ea, attempt, "malformed message: missing message id or tenant", body);
            return;
        }

        var envelope = new EventEnvelope(messageId, p.Type ?? ea.RoutingKey, tenantId,
            Topology.Header(p, "correlation_id"), Encoding.UTF8.GetString(body), attempt);

        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<MutableTenantContext>().TenantId = tenantId;
        var uow = sp.GetRequiredService<IUnitOfWork>();
        var db = sp.GetRequiredService<DamDbContext>();
        try
        {
            await uow.BeginAsync(default);
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO processed_messages (consumer, message_id, tenant_id, processed_at)
                VALUES ({_name}, {messageId}, {tenantId}, now()) ON CONFLICT DO NOTHING
                """);
            if (inserted == 0)
            {
                await uow.RollbackAsync(default);
                log.LogInformation("{Consumer}: duplicate message {MessageId} skipped", _name, messageId);
            }
            else
            {
                await sp.GetRequiredService<T>().HandleAsync(envelope, default);
                await uow.CommitAsync(default);
            }
            await ch.BasicAckAsync(ea.DeliveryTag, false);
        }
        catch (Exception ex)
        {
            await SafeRollbackAsync(uow);
            log.LogWarning(ex, "{Consumer}: attempt {Attempt}/{Max} failed for {MessageId}", _name, attempt, options.MaxAttempts, messageId);
            if (attempt < options.MaxAttempts) await RetryAsync(ch, ea, attempt, body);
            else await ParkAsync(ch, ea, attempt, ex.Message, body);
        }
    }

    private static async Task SafeRollbackAsync(IUnitOfWork uow)
    {
        try { await uow.RollbackAsync(default); } catch { /* connection may already be gone */ }
    }

    private async Task RetryAsync(IChannel ch, BasicDeliverEventArgs ea, int attempt, byte[] body)
    {
        var props = Clone(ea.BasicProperties, attempt + 1);
        await ch.BasicPublishAsync("", Topology.RetryQueue(_name, Topology.Delay(options, attempt)), mandatory: true, props, body);
        await ch.BasicAckAsync(ea.DeliveryTag, false);
    }

    private async Task ParkAsync(IChannel ch, BasicDeliverEventArgs ea, int attempt, string error, byte[] body)
    {
        var props = Clone(ea.BasicProperties, attempt);
        props.Headers!["x-error"] = error.Length > 500 ? error[..500] : error;
        props.Headers["x-consumer"] = _name;
        props.Headers["x-failed-at"] = DateTimeOffset.UtcNow.ToString("O");
        await ch.BasicPublishAsync("", Topology.Dlq(_name), mandatory: true, props, body);
        await ch.BasicAckAsync(ea.DeliveryTag, false);
        log.LogError("{Consumer}: message {MessageId} moved to DLQ after {Attempt} attempts: {Error}", _name, ea.BasicProperties.MessageId, attempt, error);
    }

    private static BasicProperties Clone(IReadOnlyBasicProperties src, int attempt)
    {
        var headers = new Dictionary<string, object?>();
        if (src.Headers is not null) foreach (var (k, v) in src.Headers) headers[k] = v;
        headers["x-attempt"] = Encoding.UTF8.GetBytes(attempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return new BasicProperties
        {
            MessageId = src.MessageId, Type = src.Type, ContentType = src.ContentType,
            DeliveryMode = DeliveryModes.Persistent, Headers = headers,
        };
    }
}
