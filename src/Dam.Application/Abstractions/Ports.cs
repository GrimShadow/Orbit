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

/// <summary>Lets handlers raise events that are not tied to a tracked aggregate. Drained by the outbox behaviour.</summary>
public interface IEventCollector { void Raise(DomainEvent e); }

/// <summary>Work that runs on a schedule, resolved by <see cref="Key"/> when its trigger fires (host: Dam.Scheduler).</summary>
public interface IScheduledJob
{
    string Key { get; }
    Task RunAsync(IReadOnlyDictionary<string, string> data, CancellationToken ct);
}

public interface IJobScheduler
{
    /// <summary>Fire <paramref name="jobKey"/> once. Re-scheduling the same <paramref name="scheduleId"/> replaces the trigger.</summary>
    Task ScheduleOnceAsync(string scheduleId, string jobKey, DateTimeOffset at, IReadOnlyDictionary<string, string>? data = null, CancellationToken ct = default);
    Task ScheduleCronAsync(string scheduleId, string jobKey, string cron, IReadOnlyDictionary<string, string>? data = null, CancellationToken ct = default);
    Task<bool> CancelAsync(string scheduleId, CancellationToken ct = default);
}
