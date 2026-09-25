using Dam.Domain.Common;

namespace Dam.Application.Abstractions;

public interface IClock { DateTimeOffset UtcNow { get; } }

public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    Guid? UserId { get; }
    IReadOnlyCollection<string> Roles { get; }
}

public interface ITenantContext { Guid TenantId { get; } }

/// <summary>Single policy entry point (RBAC + ABAC arrive in Step 1.1).</summary>
public interface IAuthorizationService
{
    bool Can(ICurrentUser user, string permission, object? resource = null);
}

public interface IUnitOfWork
{
    Task BeginAsync(CancellationToken ct);
    Task CommitAsync(CancellationToken ct);
    Task RollbackAsync(CancellationToken ct);
}

/// <summary>Collects events raised by aggregates touched in the current scope.</summary>
public interface IDomainEventSource { IReadOnlyList<DomainEvent> Drain(); }

public interface IOutbox { Task EnqueueAsync(IEnumerable<DomainEvent> events, CancellationToken ct); }
