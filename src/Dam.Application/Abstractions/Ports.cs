using Dam.Domain.Authorization;
using Dam.Domain.Common;

namespace Dam.Application.Abstractions;

public interface IClock { DateTimeOffset UtcNow { get; } }

public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    /// <summary>Identity-provider subject (the token `sub`). Stable; used as the audit actor.</summary>
    Guid? UserId { get; }
    string? Subject { get; }
    IReadOnlyCollection<string> Roles { get; }
}

public interface ITenantContext { Guid TenantId { get; } }

/// <summary>Single policy entry point (spec A7.2): RBAC + ABAC for the current user in the current tenant.</summary>
public interface IAuthorizationService
{
    /// <summary>May the current user do <paramref name="permission"/>, optionally on a specific resource?</summary>
    Task<bool> CanAsync(string permission, ResourceContext? resource = null, CancellationToken ct = default);

    /// <summary>The current user's access clauses for list filtering (search, collections). Empty means no access.</summary>
    Task<IReadOnlyList<ScopeClause>> GetScopeAsync(string permission, CancellationToken ct = default);

    Task<AccessProfile> GetProfileAsync(CancellationToken ct = default);
}

/// <summary>Builds the current user's <see cref="AccessProfile"/> (roles, groups, rules, attributes) from storage.</summary>
public interface IAccessProfileProvider
{
    Task<AccessProfile> GetAsync(CancellationToken ct);
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

/// <summary>Generic aggregate access. Handlers query with LINQ and materialise through <see cref="IQueryExecutor"/>.</summary>
public interface IRepository<T> where T : class
{
    IQueryable<T> Query();
    Task<T?> FindAsync(Guid id, CancellationToken ct);
    void Add(T entity);
    void AddRange(IEnumerable<T> entities);
    void Remove(T entity);
    void RemoveRange(IEnumerable<T> entities);
}

/// <summary>Async LINQ terminals, implemented by EF Core, so Application code never references EF.</summary>
public interface IQueryExecutor
{
    Task<List<T>> ToListAsync<T>(IQueryable<T> query, CancellationToken ct);
    Task<T?> FirstOrDefaultAsync<T>(IQueryable<T> query, CancellationToken ct);
    Task<bool> AnyAsync<T>(IQueryable<T> query, CancellationToken ct);
    Task<int> CountAsync<T>(IQueryable<T> query, CancellationToken ct);
}
