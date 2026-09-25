using Dam.Application;
using Dam.Application.Abstractions;
using Dam.Application.Messaging;
using Dam.Application.Ping;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Dam.UnitTests;

public sealed class PingPipelineTests
{
    private sealed record PingEvent : DomainEvent { public override string Name => "test.ping"; }
    private sealed class FakeUser(bool auth, params string[] roles) : ICurrentUser
    {
        public bool IsAuthenticated => auth;
        public Guid? UserId => auth ? Guid.NewGuid() : null;
        public string? Subject => auth ? "sub" : null;
        public IReadOnlyCollection<string> Roles => roles;
    }
    private sealed class FakeAuthz(bool allow) : IAuthorizationService
    {
        public Task<bool> CanAsync(string permission, ResourceContext? resource = null, CancellationToken ct = default) => Task.FromResult(allow);
        public Task<IReadOnlyList<ScopeClause>> GetScopeAsync(string permission, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AccessProfile> GetProfileAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class FakeUow : IUnitOfWork
    {
        public List<string> Log { get; } = [];
        public Task BeginAsync(CancellationToken ct) { Log.Add("begin"); return Task.CompletedTask; }
        public Task CommitAsync(CancellationToken ct) { Log.Add("commit"); return Task.CompletedTask; }
        public Task RollbackAsync(CancellationToken ct) { Log.Add("rollback"); return Task.CompletedTask; }
    }
    private sealed class FakeEvents : IDomainEventSource, IOutbox, IEventCollector
    {
        public List<DomainEvent> Outboxed { get; } = [];
        public void Raise(DomainEvent e) { }
        public IReadOnlyList<DomainEvent> Drain() => [new PingEvent()];
        public Task EnqueueAsync(IEnumerable<DomainEvent> events, CancellationToken ct) { Outboxed.AddRange(events); return Task.CompletedTask; }
    }

    private static (IDispatcher d, FakeUow uow, FakeEvents ev) Build(bool authenticated, bool allow)
    {
        var uow = new FakeUow(); var ev = new FakeEvents();
        var sc = new ServiceCollection().AddDamApplication();
        sc.AddSingleton<ICurrentUser>(new FakeUser(authenticated));
        sc.AddSingleton<IAuthorizationService>(new FakeAuthz(allow));
        sc.AddSingleton<IUnitOfWork>(uow);
        sc.AddSingleton<IDomainEventSource>(ev);
        sc.AddSingleton<IOutbox>(ev);
        sc.AddSingleton<IEventCollector>(ev);
        return (sc.BuildServiceProvider().GetRequiredService<IDispatcher>(), uow, ev);
    }

    [Fact]
    public async Task Success_runs_handler_commits_and_writes_outbox()
    {
        var (d, uow, ev) = Build(true, true);
        var r = await d.Send(new PingCommand("hi"));
        Assert.True(r.IsSuccess);
        Assert.Equal("pong: hi", r.Value);
        Assert.Equal(["begin", "commit"], uow.Log);
        Assert.Single(ev.Outboxed);
    }

    [Fact]
    public async Task Validation_failure_returns_field_errors_and_never_opens_transaction()
    {
        var (d, uow, ev) = Build(true, true);
        var r = await d.Send(new PingCommand(""));
        Assert.True(r.IsFailure);
        Assert.Equal(ErrorType.Validation, r.Error.Type);
        Assert.Contains(nameof(PingCommand.Message), r.Error.FieldErrors!.Keys);
        Assert.Empty(uow.Log);
        Assert.Empty(ev.Outboxed);
    }

    [Fact]
    public async Task Forbidden_user_is_rejected_before_transaction()
    {
        var (d, uow, _) = Build(true, false);
        var r = await d.Send(new PingCommand("hi"));
        Assert.Equal(ErrorType.Forbidden, r.Error.Type);
        Assert.Empty(uow.Log);
    }

    [Fact]
    public async Task Anonymous_user_is_unauthorized()
    {
        var (d, _, _) = Build(false, true);
        var r = await d.Send(new PingCommand("hi"));
        Assert.Equal(ErrorType.Unauthorized, r.Error.Type);
    }
}

public sealed class Uuid7Tests
{
    [Fact]
    public void Ids_are_version7_and_time_ordered()
    {
        var a = Uuid7.NewGuid(DateTimeOffset.UnixEpoch.AddSeconds(1));
        var b = Uuid7.NewGuid(DateTimeOffset.UnixEpoch.AddSeconds(2));
        Assert.Equal('7', a.ToString()[14]);
        Assert.True(string.CompareOrdinal(a.ToString(), b.ToString()) < 0);
    }
}
