namespace Dam.Domain.Common;

public interface ITenantScoped { Guid TenantId { get; } }

public abstract record DomainEvent
{
    public Guid EventId { get; init; } = Uuid7.NewGuid();
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>Canonical name, e.g. "asset.created" (spec A4.3).</summary>
    public abstract string Name { get; }
}

public abstract class Entity
{
    public Guid Id { get; protected set; } = Uuid7.NewGuid();
}

public abstract class TenantEntity : Entity, ITenantScoped
{
    public Guid TenantId { get; protected set; }
}

public abstract class AggregateRoot : TenantEntity
{
    private readonly List<DomainEvent> _events = [];
    public IReadOnlyList<DomainEvent> DomainEvents => _events;
    protected void Raise(DomainEvent e) => _events.Add(e);
    public IReadOnlyList<DomainEvent> PullEvents() { var copy = _events.ToArray(); _events.Clear(); return copy; }
}

/// <summary>Events that concern one entity; used to fill audit_log.entity_type/entity_id.</summary>
public interface IEntityEvent
{
    string EntityType { get; }
    Guid EntityId { get; }
}
