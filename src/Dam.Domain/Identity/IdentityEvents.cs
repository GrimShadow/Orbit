using Dam.Domain.Common;

namespace Dam.Domain.Identity;

/// <summary>Canonical event `user.provisioned` (spec A4.3).</summary>
public sealed record UserProvisioned(Guid UserId, string ExternalSubject, string Email) : DomainEvent, IEntityEvent
{
    public override string Name => "user.provisioned";
    public string EntityType => "user";
    public Guid EntityId => UserId;
}

/// <summary>Generic "something in the identity area changed" event, e.g. role.created, access_rule.deleted.</summary>
public sealed record EntityChanged(string EventName, string EntityType, Guid EntityId) : DomainEvent, IEntityEvent
{
    public override string Name => EventName;
}
