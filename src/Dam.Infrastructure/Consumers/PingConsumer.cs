using Dam.Infrastructure.Messaging;
using Dam.Infrastructure.Persistence;

namespace Dam.Infrastructure.Consumers;

/// <summary>Smoke-test consumer for `system.ping`: proves API → outbox → RabbitMQ → worker end to end.</summary>
public sealed class PingConsumer(DamDbContext db) : EventConsumer
{
    public override string Name => "system-ping";
    public override IReadOnlyList<string> RoutingKeys => ["system.ping"];

    public override Task HandleAsync(EventEnvelope message, CancellationToken ct)
    {
        db.AuditLog.Add(new AuditLogEntry
        {
            Ts = DateTimeOffset.UtcNow, Actor = "worker", Action = "system.ping.handled", After = message.Payload,
        });
        return Task.CompletedTask;
    }
}
