using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Dam.Infrastructure.Persistence;

public sealed class EfUnitOfWork(DamDbContext db) : IUnitOfWork
{
    private IDbContextTransaction? _tx;

    public async Task BeginAsync(CancellationToken ct) => _tx = await db.Database.BeginTransactionAsync(ct);

    public async Task CommitAsync(CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        if (_tx is not null) await _tx.CommitAsync(ct);
        _tx = null;
    }

    public async Task RollbackAsync(CancellationToken ct)
    {
        if (_tx is not null) await _tx.RollbackAsync(ct);
        _tx = null;
        db.ChangeTracker.Clear();
    }
}

/// <summary>Collects events raised on tracked aggregates plus loose events raised via <see cref="IEventCollector"/>.</summary>
public sealed class EfDomainEventSource(DamDbContext db) : IDomainEventSource, IEventCollector
{
    private readonly List<DomainEvent> _loose = [];

    public void Raise(DomainEvent e) => _loose.Add(e);

    public IReadOnlyList<DomainEvent> Drain()
    {
        var events = db.ChangeTracker.Entries<AggregateRoot>().SelectMany(e => e.Entity.PullEvents()).ToList();
        events.AddRange(_loose);
        _loose.Clear();
        return events;
    }
}

/// <summary>
/// Persists each event to audit_log first, then to the outbox, in the caller's transaction (decision D15, spec A3.3).
/// </summary>
public sealed class EfOutbox(DamDbContext db, ICurrentUser user, IClock clock) : IOutbox
{
    public Task EnqueueAsync(IEnumerable<DomainEvent> events, CancellationToken ct)
    {
        var now = clock.UtcNow;
        foreach (var e in events)
        {
            var payload = JsonSerializer.Serialize(e, e.GetType());
            var entity = e as IEntityEvent;
            db.AuditLog.Add(new AuditLogEntry
            {
                Ts = now,
                Actor = user.UserId?.ToString() ?? "system",
                Action = e.Name,
                EntityType = entity?.EntityType,
                EntityId = entity?.EntityId,
                After = payload,
            });
            db.Outbox.Add(new OutboxMessage
            {
                EventType = e.Name,
                Payload = payload,
                CorrelationId = System.Diagnostics.Activity.Current?.TraceId.ToString(),
                CreatedAt = now,
            });
        }
        return Task.CompletedTask;
    }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
