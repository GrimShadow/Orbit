using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Dam.Application.Abstractions;
using Dam.Domain.Common;
using Dam.Infrastructure;
using Dam.Infrastructure.Consumers;
using Dam.Infrastructure.Messaging;
using Dam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace Dam.IntegrationTests;

public sealed class RabbitFixture : IAsyncLifetime
{
    private readonly RabbitMqContainer _mq = new RabbitMqBuilder("rabbitmq:3.13-alpine").Build();
    public string Url => _mq.GetConnectionString();
    public Task InitializeAsync() => _mq.StartAsync();
    public Task DisposeAsync() => _mq.DisposeAsync().AsTask();
}

public sealed record TestEvent(string Text) : DomainEvent
{
    public override string Name => "test.message";
}

/// <summary>Counts invocations; optionally always fails, to exercise retry and DLQ.</summary>
public sealed class CountingConsumer : EventConsumer
{
    public static int Calls;
    public static volatile bool Fail;
    public override string Name => "counting";
    public override IReadOnlyList<string> RoutingKeys => ["test.message"];
    public override Task HandleAsync(EventEnvelope message, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return Fail ? throw new InvalidOperationException("boom") : Task.CompletedTask;
    }
}

[Collection("pg")]
public sealed class MessagingTests(PostgresFixture pg, RabbitFixture mq) : IClassFixture<RabbitFixture>
{
    private readonly RabbitFixture _mq = mq;

    private IHost WorkerHost(Action<IServiceCollection>? extra = null, int maxAttempts = 5, int baseDelayMs = 50)
    {
        var b = Host.CreateApplicationBuilder();
        b.Logging.SetMinimumLevel(LogLevel.Warning);
        var options = new MessagingOptions
            { RabbitUrl = _mq.Url, MaxAttempts = maxAttempts, RetryBaseDelayMs = baseDelayMs, OutboxPollMs = 100 };
        b.Services.AddDamWorkerIdentity();
        b.Services.AddDamInfrastructure(pg.AppConnection);
        b.Services.AddDamMessaging(options);
        b.Services.AddEventConsumer<PingConsumer>();
        b.Services.AddEventConsumer<CountingConsumer>();
        extra?.Invoke(b.Services);
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
            await Task.Delay(100);
        } while (DateTime.UtcNow < deadline);
        return last;
    }

    private async Task<long> AuditCount(Guid tenant, string action)
    {
        await using var db = pg.AppContext(new TestTenant(tenant));
        return await db.AuditLog.LongCountAsync(a => a.Action == action);
    }

    [Fact]
    public async Task Event_is_relayed_from_outbox_and_consumed_exactly_once_even_if_redelivered()
    {
        var tenant = Uuid7.NewGuid();
        await using var stopping = new StoppingHost(WorkerHost());
        var host = stopping.Host;
        await host.StartAsync();

        await EmitAsync(tenant, new SystemPingEventForTest("hello"));
        Assert.Equal(1, await Eventually(() => AuditCount(tenant, "system.ping.handled"), c => c >= 1));

        // Simulate the broker (or a relay crash) delivering the same message again.
        Guid messageId;
        await using (var db = pg.AppContext(new TestTenant(tenant)))
            messageId = (await db.Outbox.SingleAsync(o => o.EventType == "system.ping")).Id;
        var conn = await new ConnectionFactory { Uri = new Uri(_mq.Url) }.CreateConnectionAsync();
        await using var ch = await conn.CreateChannelAsync();
        for (var i = 0; i < 3; i++)
            await ch.BasicPublishAsync("dam.events", "system.ping", false, new BasicProperties
            {
                MessageId = messageId.ToString(), Type = "system.ping",
                Headers = new Dictionary<string, object?> { ["tenant_id"] = Encoding.UTF8.GetBytes(tenant.ToString()) },
            }, Encoding.UTF8.GetBytes("""{"Message":"hello"}"""));
        await Task.Delay(1500);

        Assert.Equal(1, await AuditCount(tenant, "system.ping.handled"));
        await conn.CloseAsync();
    }

    [Fact]
    public async Task Ping_through_the_real_API_endpoint_is_consumed_by_the_worker_exactly_once()
    {
        var tenant = Uuid7.NewGuid();
        await using var stopping = new StoppingHost(WorkerHost());
        await stopping.Host.StartAsync();
        await using var api = new ApiTestHost(pg).Factory();
        using var client = api.CreateClient();

        HttpRequestMessage Ping(string token) => new(HttpMethod.Post, "/api/v1/system/ping")
        {
            Content = JsonContent.Create(new { message = "e2e" }),
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
        };

        // Non-admins are refused before anything is written.
        var denied = await client.SendAsync(Ping(ApiTestHost.Token(tenant, roles: "Editor")));
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, denied.StatusCode);

        var res = await client.SendAsync(Ping(ApiTestHost.Token(tenant, roles: "Admin")));
        Assert.Equal(System.Net.HttpStatusCode.OK, res.StatusCode);

        Assert.Equal(1, await Eventually(() => AuditCount(tenant, "system.ping.handled"), c => c >= 1));
        await Task.Delay(1000);
        Assert.Equal(1, await AuditCount(tenant, "system.ping.handled"));
        Assert.Equal(1, await AuditCount(tenant, "system.ping")); // written by the API in the command's transaction
    }

    [Fact]
    public async Task Outbox_rows_are_marked_published_and_only_the_relay_role_can_see_all_tenants()
    {
        var t1 = Uuid7.NewGuid(); var t2 = Uuid7.NewGuid();
        await using var stopping = new StoppingHost(WorkerHost());
        var host = stopping.Host;
        await host.StartAsync();
        await EmitAsync(t1, new SystemPingEventForTest("a"));
        await EmitAsync(t2, new SystemPingEventForTest("b"));

        await using var sys = new NpgsqlConnection(pg.SystemConnection);
        await sys.OpenAsync();
        var unpublished = await Eventually(async () =>
        {
            await using var c = new NpgsqlCommand($"SELECT count(*) FROM outbox WHERE tenant_id IN ('{t1}','{t2}') AND published_at IS NULL", sys);
            return (long)(await c.ExecuteScalarAsync())!;
        }, n => n == 0);
        Assert.Equal(0, unpublished);

        // The relay role can read across tenants but cannot touch anything else.
        await using var other = new NpgsqlCommand("SELECT count(*) FROM audit_log", sys);
        await Assert.ThrowsAsync<PostgresException>(() => other.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Poison_message_is_retried_then_lands_in_the_DLQ_after_five_attempts()
    {
        CountingConsumer.Calls = 0;
        CountingConsumer.Fail = true;
        var tenant = Uuid7.NewGuid();
        await using var stopping = new StoppingHost(WorkerHost(baseDelayMs: 50));
        var host = stopping.Host;
        await host.StartAsync();

        await EmitAsync(tenant, new TestEvent("poison"));

        var conn = await new ConnectionFactory { Uri = new Uri(_mq.Url) }.CreateConnectionAsync();
        await using var ch = await conn.CreateChannelAsync();
        var parked = await Eventually(async () => await ch.BasicGetAsync(Topology.Dlq("counting"), autoAck: true), r => r is not null);
        Assert.NotNull(parked);
        Assert.Equal("5", Topology.Header(parked!.BasicProperties, "x-attempt"));
        Assert.Contains("boom", Topology.Header(parked.BasicProperties, "x-error"));
        Assert.Equal(5, CountingConsumer.Calls); // exactly maxAttempts handler runs, no more

        await Task.Delay(1000);
        Assert.Equal(5, CountingConsumer.Calls);
        Assert.Null(await ch.BasicGetAsync(Topology.Queue("counting"), autoAck: true));
        await conn.CloseAsync();
    }

    [Fact]
    public async Task Transient_failure_is_retried_and_succeeds_without_reaching_the_DLQ()
    {
        CountingConsumer.Calls = 0;
        CountingConsumer.Fail = true;
        var tenant = Uuid7.NewGuid();
        await using var stopping = new StoppingHost(WorkerHost(baseDelayMs: 100));
        var host = stopping.Host;
        await host.StartAsync();
        await EmitAsync(tenant, new TestEvent("flaky"));

        await Eventually(() => Task.FromResult(CountingConsumer.Calls), c => c >= 1);
        CountingConsumer.Fail = false; // recovers before attempts run out

        var conn = await new ConnectionFactory { Uri = new Uri(_mq.Url) }.CreateConnectionAsync();
        await using var ch = await conn.CreateChannelAsync();
        await using var db = pg.AppContext(new TestTenant(tenant));
        var done = await Eventually(async () => await db.ProcessedMessages.CountAsync(m => m.Consumer == "counting"), c => c == 1);
        Assert.Equal(1, done);
        Assert.Null(await ch.BasicGetAsync(Topology.Dlq("counting"), autoAck: true));
        await conn.CloseAsync();
    }
}

/// <summary>Same wire shape as the API's SystemPingEvent, without depending on the Application assembly here.</summary>
public sealed record SystemPingEventForTest(string Message) : DomainEvent
{
    public override string Name => "system.ping";
}

/// <summary>Stops the host gracefully on dispose so a failing assertion is reported instead of a shutdown hang.</summary>
public sealed class StoppingHost(IHost host) : IAsyncDisposable
{
    public IHost Host { get; } = host;

    public async ValueTask DisposeAsync()
    {
        try { await Host.StopAsync(TimeSpan.FromSeconds(10)); } finally { Host.Dispose(); }
    }
}
