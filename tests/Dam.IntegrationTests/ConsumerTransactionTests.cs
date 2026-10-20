using System.Diagnostics;
using System.Text;
using Dam.Application.Abstractions;
using Dam.Domain.Common;
using Dam.Infrastructure;
using Dam.Infrastructure.Messaging;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Dam.IntegrationTests;

public sealed record LockProbeTrigger(string Text, string Route) : DomainEvent
{
    public override string Name => Route;
}

public sealed record LockProbeWrite(string Text) : DomainEvent
{
    public override string Name => "test.lockprobe.write";
}

/// <summary>What a probe consumer did, and the gates a test uses to hold it mid-work.</summary>
public sealed class LockProbe
{
    private int _calls;

    public TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Calls => Volatile.Read(ref _calls);
    public bool FailFirst { get; private set; }

    public void Reset(bool failFirst = false)
    {
        Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _calls, 0);
        FailFirst = failFirst;
    }

    /// <summary>Saves an audited row (which takes the tenant's audit-chain lock), reports that it is mid-work, waits to be released, and optionally fails the first time.</summary>
    public async Task WorkAsync(DamDbContext db, IOutbox outbox, CancellationToken ct)
    {
        var call = Interlocked.Increment(ref _calls);
        await outbox.EnqueueAsync([new LockProbeWrite("from the handler")], ct);
        await db.SaveChangesAsync(ct);
        Entered.TrySetResult();
        await Release.Task.WaitAsync(ct);
        if (FailFirst && call == 1) throw new InvalidOperationException("first attempt fails");
    }
}

/// <summary>The default: the host's transaction (and the audit-chain lock the handler's first audited row takes) lasts as long as the handler.</summary>
public sealed class HostTransactionProbeConsumer(DamDbContext db, IOutbox outbox) : EventConsumer
{
    public static readonly LockProbe Probe = new();
    public override string Name => "probe-host-tx";
    public override IReadOnlyList<string> RoutingKeys => ["test.lockprobe.hosttx"];
    public override Task HandleAsync(EventEnvelope message, CancellationToken ct) => Probe.WorkAsync(db, outbox, ct);
}

/// <summary>A long, idempotent job that asks the host for no transaction (ADR-0038).</summary>
public sealed class ShortTransactionProbeConsumer(DamDbContext db, IOutbox outbox) : EventConsumer
{
    public static readonly LockProbe Probe = new();
    public override string Name => "probe-short-tx";
    public override IReadOnlyList<string> RoutingKeys => ["test.lockprobe.shorttx"];
    public override bool RunsInHostTransaction => false;
    public override Task HandleAsync(EventEnvelope message, CancellationToken ct) => Probe.WorkAsync(db, outbox, ct);
}

/// <summary>ADR-0038: how the consumer host brackets a handler, and what that does to the tenant's audit chain.</summary>
[Collection("pg")]
public sealed class ConsumerTransactionTests(PostgresFixture pg, RabbitFixture mq) : IClassFixture<RabbitFixture>
{
    private IHost WorkerHost()
    {
        var b = Host.CreateApplicationBuilder();
        b.Logging.SetMinimumLevel(LogLevel.Critical);
        var options = new MessagingOptions { RabbitUrl = mq.Url, MaxAttempts = 5, RetryBaseDelayMs = 50, OutboxPollMs = 100 };
        b.Services.AddDamWorkerIdentity();
        b.Services.AddDamInfrastructure(pg.AppConnection);
        b.Services.AddDamMessaging(options);
        b.Services.AddEventConsumer<HostTransactionProbeConsumer>();
        b.Services.AddEventConsumer<ShortTransactionProbeConsumer>();
        b.Services.AddOutboxRelay(pg.SystemConnection);
        return b.Build();
    }

    private async Task EmitAsync(Guid tenant, params DomainEvent[] events)
    {
        await using var db = pg.AppContext(new TestTenant(tenant));
        await new EfOutbox(db, new SystemUser(), new SystemClock()).EnqueueAsync(events, default);
        await db.SaveChangesAsync();
    }

    private static async Task<T> Eventually<T>(Func<Task<T>> probe, Func<T, bool> ok, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        T last;
        do
        {
            last = await probe();
            if (ok(last)) return last;
            await Task.Delay(50);
        } while (DateTime.UtcNow < deadline);
        return last;
    }

    private async Task<int> Processed(Guid tenant, string consumer)
    {
        await using var db = pg.AppContext(new TestTenant(tenant));
        return await db.ProcessedMessages.CountAsync(m => m.Consumer == consumer);
    }

    private async Task<long> AuditCount(Guid tenant, string action)
    {
        await using var db = pg.AppContext(new TestTenant(tenant));
        return await db.AuditLog.LongCountAsync(a => a.Action == action);
    }

    [Fact]
    public async Task A_handler_in_the_host_transaction_holds_the_audit_chain_lock_until_it_finishes()
    {
        // The reproduction of the bug: the handler saved one audited row and is now busy; another audited write for the same tenant has to queue behind it.
        var probe = HostTransactionProbeConsumer.Probe; probe.Reset();
        var tenant = Uuid7.NewGuid();
        await using var stopping = new StoppingHost(WorkerHost());
        await stopping.Host.StartAsync();
        await EmitAsync(tenant, new LockProbeTrigger("go", "test.lockprobe.hosttx"));
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var other = EmitAsync(tenant, new LockProbeWrite("from elsewhere"));
        Assert.NotSame(other, await Task.WhenAny(other, Task.Delay(1500)));   // still waiting for the lock

        probe.Release.SetResult();
        await other.WaitAsync(TimeSpan.FromSeconds(10));                         // and goes through once the handler's transaction commits
        Assert.Equal(1, await Eventually(() => Processed(tenant, "probe-host-tx"), c => c == 1));
    }

    [Fact]
    public async Task A_consumer_that_opts_out_of_the_host_transaction_does_not_block_audited_writes_while_it_works()
    {
        var probe = ShortTransactionProbeConsumer.Probe; probe.Reset();
        var tenant = Uuid7.NewGuid();
        await using var stopping = new StoppingHost(WorkerHost());
        await stopping.Host.StartAsync();
        await EmitAsync(tenant, new LockProbeTrigger("go", "test.lockprobe.shorttx"));
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var clock = Stopwatch.StartNew();
        await EmitAsync(tenant, new LockProbeWrite("from elsewhere")).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"An audited write waited {clock.Elapsed.TotalSeconds:0.0} s for a handler that was only waiting.");
        Assert.Equal(0, await Processed(tenant, "probe-short-tx"));   // the message is not recorded until the handler has finished

        probe.Release.SetResult();
        Assert.Equal(1, await Eventually(() => Processed(tenant, "probe-short-tx"), c => c == 1));
        Assert.Equal(2, await AuditCount(tenant, "test.lockprobe.write"));   // the handler's own row, committed as it went, and the other write
    }

    [Fact]
    public async Task A_redelivered_message_is_not_handled_again_once_a_consumer_without_the_host_transaction_has_finished_it()
    {
        var probe = ShortTransactionProbeConsumer.Probe; probe.Reset();
        probe.Release.SetResult();
        var tenant = Uuid7.NewGuid();
        await using var stopping = new StoppingHost(WorkerHost());
        await stopping.Host.StartAsync();
        await EmitAsync(tenant, new LockProbeTrigger("go", "test.lockprobe.shorttx"));
        Assert.Equal(1, await Eventually(() => Processed(tenant, "probe-short-tx"), c => c == 1));

        Guid messageId;
        await using (var db = pg.AppContext(new TestTenant(tenant)))
            messageId = (await db.Outbox.SingleAsync(o => o.EventType == "test.lockprobe.shorttx")).Id;
        var conn = await new ConnectionFactory { Uri = new Uri(mq.Url) }.CreateConnectionAsync();
        await using var ch = await conn.CreateChannelAsync();
        for (var i = 0; i < 3; i++)
            await ch.BasicPublishAsync("dam.events", "test.lockprobe.shorttx", false, new BasicProperties
            {
                MessageId = messageId.ToString(),
                Type = "test.lockprobe.shorttx",
                Headers = new Dictionary<string, object?> { ["tenant_id"] = Encoding.UTF8.GetBytes(tenant.ToString()) },
            }, Encoding.UTF8.GetBytes("""{}"""));
        // A queue that has nothing left to deliver is the state to wait for.
        var drained = await Eventually(async () => (await ch.QueueDeclarePassiveAsync(Topology.Queue("probe-short-tx"))).MessageCount, n => n == 0);
        Assert.Equal(0u, drained);
        await Task.Delay(300);   // the last delivery may still be in the host's hands
        Assert.Equal(1, probe.Calls);
        Assert.Equal(1, await Processed(tenant, "probe-short-tx"));
        await conn.CloseAsync();
    }

    [Fact]
    public async Task A_failed_attempt_of_a_consumer_without_the_host_transaction_is_retried_and_recorded_once()
    {
        var probe = ShortTransactionProbeConsumer.Probe; probe.Reset(failFirst: true);
        probe.Release.SetResult();
        var tenant = Uuid7.NewGuid();
        await using var stopping = new StoppingHost(WorkerHost());
        await stopping.Host.StartAsync();
        await EmitAsync(tenant, new LockProbeTrigger("go", "test.lockprobe.shorttx"));

        Assert.Equal(1, await Eventually(() => Processed(tenant, "probe-short-tx"), c => c == 1));
        Assert.Equal(2, probe.Calls);                                            // the first attempt failed after saving a row; the retry ran the handler again
        Assert.Equal(1, await Processed(tenant, "probe-short-tx"));
    }
}
