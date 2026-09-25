using Dam.Domain.Common;

namespace Dam.Infrastructure.Persistence;

public sealed class OutboxMessage : ITenantScoped
{
    public Guid Id { get; set; } = Uuid7.NewGuid();
    public Guid TenantId { get; set; }
    public string EventType { get; set; } = "";
    public string Payload { get; set; } = "{}";
    public string? CorrelationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int Attempts { get; set; }
}

/// <summary>Append-only. chain_no/prev_hash/hash are assigned by the database trigger.</summary>
public sealed class AuditLogEntry : ITenantScoped
{
    public Guid Id { get; set; } = Uuid7.NewGuid();
    public Guid TenantId { get; set; }
    public DateTimeOffset Ts { get; set; }
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string? EntityType { get; set; }
    public Guid? EntityId { get; set; }
    public string? Before { get; set; }
    public string? After { get; set; }
    public string? Ip { get; set; }
    public long ChainNo { get; set; }
    public string PrevHash { get; set; } = "";
    public string Hash { get; set; } = "";
}
