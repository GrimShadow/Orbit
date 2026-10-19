using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Dam.Domain.Webhooks;
using Dam.Infrastructure.Notifications;
using Dam.Infrastructure.Scheduling;
using Dam.Shared.Webhooks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Xunit.Abstractions;

namespace Dam.IntegrationTests;

/// <summary>Real events through the real outbox, broker and worker to a local receiver (ADR-0031): speed, signature, retries, isolation.</summary>
[Collection("pg")]
public sealed class WebhookDeliveryTests(PostgresFixture pg, RabbitFixture mq, ITestOutputHelper output) : AssetTestBase(pg), IClassFixture<RabbitFixture>
{
    private const string Hooks = "/api/v1/webhooks";
    private const string Log = "/api/v1/webhook-deliveries";

    private async Task<(Guid Id, string Secret)> NewHook(string admin, string url, string name = "Primary", string[]? events = null, bool active = true)
    {
        var r = await Call(HttpMethod.Post, Hooks, admin, new { name, url, events = events ?? ["asset.*"], active });
        Assert.Equal(HttpStatusCode.Created, r.Status);
        return (Id(r.Body, "webhook", "id"), r.Body.GetProperty("secret").GetString()!);
    }

    /// <summary>Checks the signature against the delivery's own timestamp, so deliveries made on a fake clock verify too.</summary>
    private static void AssertSigned(TestReceiver.Hit hit, string secret) =>
        Assert.Equal(WebhookVerification.Valid, WebhookSignature.Verify(secret, hit.Headers["X-Dam-Timestamp"], hit.Headers["X-Dam-Signature"], hit.Body,
            DateTimeOffset.FromUnixTimeSeconds(long.Parse(hit.Headers["X-Dam-Timestamp"])), WebhookSignature.DefaultTolerance));

    private static int Created(TestReceiver r) => r.Hits.Count(h => h.Headers["X-Dam-Event"] == "asset.created");

    private async Task<JsonElement> Delivery(string admin, Guid id) => (await Call(HttpMethod.Get, $"{Log}/{id}", admin)).Body;

    private async Task<List<JsonElement>> Deliveries(string admin, Guid hook, string query = "") =>
        (await Call(HttpMethod.Get, $"{Hooks}/{hook}/deliveries{query}", admin)).Body.GetProperty("items").EnumerateArray().ToList();

    // ---- end to end --------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_event_reaches_the_receiver_signed_and_within_five_seconds()
    {
        var (t, admin, folder) = await TenantWithPacks();
        await using var receiver = await TestReceiver.StartAsync();
        var (hook, secret) = await NewHook(admin, receiver.Url, events: ["asset.created"]);
        using var worker = NotifyHosts.Worker(Pg, mq);
        await worker.StartAsync();

        var sent = DateTimeOffset.UtcNow;
        var asset = await NewAsset(admin, folder, title: "Spring hero");
        var hit = await receiver.WaitForAsync(h => h.Headers["X-Dam-Event"] == "asset.created");
        var latency = hit.At - sent;
        output.WriteLine($"event -> receiver latency: {latency.TotalMilliseconds:F0} ms");
        Assert.True(latency < TimeSpan.FromSeconds(5), $"took {latency}");

        AssertSigned(hit, secret);
        var envelope = JsonDocument.Parse(hit.Body).RootElement;
        Assert.Equal("asset.created", envelope.GetProperty("type").GetString());
        Assert.Equal(t, envelope.GetProperty("tenantId").GetGuid());
        Assert.Equal(asset, envelope.GetProperty("data").GetProperty("assetId").GetGuid());
        Assert.Equal("image", envelope.GetProperty("data").GetProperty("assetType").GetString());
        Assert.Equal(["data", "id", "occurredAt", "tenantId", "type"], envelope.EnumerateObject().Select(p => p.Name).Order());
        Assert.True(Math.Abs((envelope.GetProperty("occurredAt").GetDateTimeOffset() - sent).TotalSeconds) < 30);
        Assert.Equal(1, Count(t, "SELECT count(*) FROM outbox WHERE event_type = 'asset.created'"));

        var delivery = Assert.Single(await NotifyHosts.Eventually(() => Deliveries(admin, hook), d => d.Count == 1 && d[0].GetProperty("status").GetString() == "succeeded"));
        Assert.Equal((1, 200, "asset.created"), (delivery.GetProperty("attempt").GetInt32(), delivery.GetProperty("lastStatusCode").GetInt32(), delivery.GetProperty("eventType").GetString()));
        Assert.Equal(envelope.GetProperty("id").GetGuid(), delivery.GetProperty("eventId").GetGuid());
        Assert.Equal(delivery.GetProperty("id").GetGuid().ToString(), hit.Headers["X-Dam-Delivery"]);
        Assert.True(delivery.GetProperty("lastDurationMs").GetInt32() >= 0);
    }

    [Fact]
    public async Task Latency_from_event_to_receiver_over_twenty_events_stays_well_under_five_seconds()
    {
        var (_, admin, folder) = await TenantWithPacks();
        await using var receiver = await TestReceiver.StartAsync();
        await NewHook(admin, receiver.Url, events: ["asset.created"]);
        using var worker = NotifyHosts.Worker(Pg, mq);
        await worker.StartAsync();
        await Task.Delay(500);   // the consumer queues are bound before the first event

        var latencies = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            var sent = DateTimeOffset.UtcNow;
            await NewAsset(admin, folder, title: "Latency " + i);
            var hit = await receiver.WaitForAsync(h => h.Body.Contains("Latency", StringComparison.Ordinal) || true, skip: i);
            latencies.Add((hit.At - sent).TotalMilliseconds);
        }
        latencies.Sort();
        output.WriteLine($"latency over 20 sequential events (ms): min {latencies[0]:F0}, median {latencies[10]:F0}, p90 {latencies[17]:F0}, max {latencies[^1]:F0}");
        Assert.True(latencies[^1] < 5000, $"slowest was {latencies[^1]:F0} ms");
    }

    [Fact]
    public async Task A_status_change_to_published_is_delivered_as_the_spec_event_asset_published()
    {
        var (_, admin, folder) = await TenantWithPacks();
        await using var receiver = await TestReceiver.StartAsync();
        var (hook, secret) = await NewHook(admin, receiver.Url, events: ["asset.published", "asset.archived"]);
        using var worker = NotifyHosts.Worker(Pg, mq);
        await worker.StartAsync();

        var asset = await NewAsset(admin, folder);
        foreach (var action in new[] { "submit", "approve", "publish" })
            Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/transitions/{action}", admin, new { reason = (string?)null })).Status);

        var hit = await receiver.WaitForAsync();
        Assert.Equal("asset.published", hit.Headers["X-Dam-Event"]);
        AssertSigned(hit, secret);
        var data = JsonDocument.Parse(hit.Body).RootElement.GetProperty("data");
        Assert.Equal((asset, "approved", "published"), (data.GetProperty("assetId").GetGuid(), data.GetProperty("from").GetString(), data.GetProperty("to").GetString()));
        await Task.Delay(1500);
        Assert.Single(receiver.Hits);   // submit and approve are not asset.published
        Assert.Single(await Deliveries(admin, hook));
    }

    [Fact]
    public async Task A_webhook_that_is_switched_off_or_not_subscribed_is_not_called_and_one_switched_on_later_hears_only_later_events()
    {
        var (_, admin, folder) = await TenantWithPacks();
        await using var off = await TestReceiver.StartAsync();
        await using var other = await TestReceiver.StartAsync();
        await using var on = await TestReceiver.StartAsync();
        var (offHook, _) = await NewHook(admin, off.Url, "Off", ["asset.created"], active: false);
        await NewHook(admin, other.Url, "Other", ["collection.updated"]);
        await NewHook(admin, on.Url, "On", ["asset.created"]);
        using var worker = NotifyHosts.Worker(Pg, mq);
        await worker.StartAsync();

        await NewAsset(admin, folder, title: "First");
        await on.WaitForAsync();
        await Task.Delay(1000);
        Assert.Empty(off.Hits);
        Assert.Empty(other.Hits);
        Assert.Empty(await Deliveries(admin, offHook));

        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, $"{Hooks}/{offHook}", admin, new { name = "Off", url = off.Url, events = new[] { "asset.created" }, active = true })).Status);
        await NewAsset(admin, folder, title: "Second");
        await off.WaitForAsync();
        Assert.Equal(1, Created(off));   // not the first asset's event
    }

    [Fact]
    public async Task One_event_reaches_every_matching_webhook_and_a_duplicate_message_is_delivered_once()
    {
        var (t, admin, folder) = await TenantWithPacks();
        await using var one = await TestReceiver.StartAsync();
        await using var two = await TestReceiver.StartAsync();
        await NewHook(admin, one.Url, "One", ["asset.created"]);
        await NewHook(admin, two.Url, "Two", ["asset.created", "asset.metadata.updated"]);
        using var worker = NotifyHosts.Worker(Pg, mq);
        await worker.StartAsync();

        await NewAsset(admin, folder);
        await one.WaitForAsync();
        await two.WaitForAsync();

        // The broker delivers the very same message again, twice.
        var outbox = await Task.Run(() => { using var c = Pg.RawAppConnection(t); using var cmd = new Npgsql.NpgsqlCommand("SELECT id, payload::text FROM outbox WHERE event_type = 'asset.created'", c); using var r = cmd.ExecuteReader(); r.Read(); return (r.GetGuid(0), r.GetString(1)); });
        var conn = await new ConnectionFactory { Uri = new Uri(mq.Url) }.CreateConnectionAsync();
        await using var ch = await conn.CreateChannelAsync();
        for (var i = 0; i < 2; i++)
            await ch.BasicPublishAsync("dam.events", "asset.created", false, new BasicProperties
            {
                MessageId = outbox.Item1.ToString(), Type = "asset.created",
                Headers = new Dictionary<string, object?> { ["tenant_id"] = Encoding.UTF8.GetBytes(t.ToString()) },
            }, Encoding.UTF8.GetBytes(outbox.Item2));
        await Task.Delay(1500);
        await conn.CloseAsync();

        await Task.Delay(500);
        Assert.Equal(1, Created(one));
        Assert.Equal(1, Created(two));
        Assert.Equal(3, Count(t, "SELECT count(*) FROM webhook_deliveries"));   // created for One; created and metadata.updated for Two - the duplicates added nothing
    }

    // ---- failures, retries, backoff ----------------------------------------------------------------------------

    [Fact]
    public async Task A_failing_endpoint_is_retried_with_exponential_backoff_by_the_job_and_marked_failed_after_eight_attempts()
    {
        var (_, admin, folder) = await TenantWithPacks();
        await using var receiver = await TestReceiver.StartAsync();
        receiver.Respond = _ => 500;
        receiver.ResponseBody = "upstream broke";
        var (hook, secret) = await NewHook(admin, receiver.Url, events: ["asset.created"]);
        using var worker = NotifyHosts.Worker(Pg, mq);
        await worker.StartAsync();
        await NewAsset(admin, folder);

        // First attempt: made immediately by the worker, failed, scheduled for about ten seconds later.
        var first = Assert.Single(await NotifyHosts.Eventually(() => Deliveries(admin, hook), d => d.Count == 1 && d[0].GetProperty("attempt").GetInt32() == 1 && d[0].GetProperty("lastStatusCode").ValueKind != JsonValueKind.Null));
        var id = first.GetProperty("id").GetGuid();
        Assert.Equal(("pending", 500), (first.GetProperty("status").GetString(), first.GetProperty("lastStatusCode").GetInt32()));
        Assert.Contains("500", first.GetProperty("lastError").GetString());
        var next = first.GetProperty("nextAttemptAt").GetDateTimeOffset();

        var expected = new[] { 10.0, 60, 300, 1800, 7200, 21600, 43200 };
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        using var scheduler = NotifyHosts.Scheduler(Pg, clock);

        // Nothing is retried before it is due.
        clock.UtcNow = next.AddSeconds(-1);
        await NotifyHosts.RunJobAsync(scheduler, WebhookRetryJob.JobKey);
        Assert.Single(receiver.Hits);

        for (var attempt = 1; attempt <= 7; attempt++)
        {
            // Attempt n failed; the wait before attempt n+1 is the schedule's n-th step, give or take 20% jitter.
            var due = (await Delivery(admin, id)).GetProperty("nextAttemptAt").GetDateTimeOffset();
            if (attempt > 1)
            {
                var waited = (due - clock.UtcNow).TotalSeconds;
                Assert.InRange(waited, expected[attempt - 1] * 0.8 - 0.01, expected[attempt - 1] * 1.2 + 0.01);
            }
            clock.UtcNow = due.AddSeconds(1);
            await NotifyHosts.RunJobAsync(scheduler, WebhookRetryJob.JobKey);
            var d = await Delivery(admin, id);
            Assert.Equal(attempt + 1, d.GetProperty("attempt").GetInt32());
            Assert.Equal(attempt + 1, receiver.Hits.Count);
            if (attempt < 7) Assert.Equal("pending", d.GetProperty("status").GetString());
        }

        var final = await Delivery(admin, id);
        Assert.Equal(("failed", 8, 500), (final.GetProperty("status").GetString(), final.GetProperty("attempt").GetInt32(), final.GetProperty("lastStatusCode").GetInt32()));
        Assert.Equal(JsonValueKind.Null, final.GetProperty("nextAttemptAt").ValueKind);
        Assert.Equal("upstream broke", final.GetProperty("responseExcerpt").GetString());
        Assert.Equal(8, final.GetProperty("maxAttempts").GetInt32());

        // Nothing more happens, however much later it gets.
        clock.UtcNow = clock.UtcNow.AddDays(7);
        await NotifyHosts.RunJobAsync(scheduler, WebhookRetryJob.JobKey);
        Assert.Equal(8, receiver.Hits.Count);

        // Every attempt was signed with the same secret and carried the same delivery id; the log filters find the failure.
        Assert.All(receiver.Hits, h => { AssertSigned(h, secret); Assert.Equal(id.ToString(), h.Headers["X-Dam-Delivery"]); });
        Assert.Single(await Deliveries(admin, hook, "?status=failed"));
        Assert.Empty(await Deliveries(admin, hook, "?status=pending"));
        Assert.Equal(1, (await Call(HttpMethod.Get, $"{Hooks}/{hook}", admin)).Body.GetProperty("stats").GetProperty("failed24h").GetInt32());
    }

    [Fact]
    public async Task A_receiver_that_recovers_gets_the_delivery_on_a_later_attempt_and_it_is_then_succeeded_once()
    {
        var (_, admin, folder) = await TenantWithPacks();
        await using var receiver = await TestReceiver.StartAsync();
        receiver.Respond = n => n < 3 ? 503 : 204;
        var (hook, _) = await NewHook(admin, receiver.Url, events: ["asset.created"]);
        using var worker = NotifyHosts.Worker(Pg, mq);
        await worker.StartAsync();
        await NewAsset(admin, folder);
        await NotifyHosts.Eventually(() => Deliveries(admin, hook), d => d.Count == 1 && d[0].GetProperty("attempt").GetInt32() == 1 && d[0].GetProperty("lastStatusCode").ValueKind != JsonValueKind.Null);

        var clock = new FakeClock(DateTimeOffset.UtcNow);
        using var scheduler = NotifyHosts.Scheduler(Pg, clock);
        for (var i = 0; i < 4; i++)
        {
            var d = (await Deliveries(admin, hook)).Single();
            if (d.GetProperty("status").GetString() == "succeeded") break;
            clock.UtcNow = d.GetProperty("nextAttemptAt").GetDateTimeOffset().AddSeconds(1);
            await NotifyHosts.RunJobAsync(scheduler, WebhookRetryJob.JobKey);
        }
        var done = (await Deliveries(admin, hook)).Single();
        Assert.Equal(("succeeded", 3, 204), (done.GetProperty("status").GetString(), done.GetProperty("attempt").GetInt32(), done.GetProperty("lastStatusCode").GetInt32()));
        Assert.Equal(JsonValueKind.Null, done.GetProperty("nextAttemptAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, done.GetProperty("lastError").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, done.GetProperty("deliveredAt").ValueKind);
        Assert.Equal(3, receiver.Hits.Count);

        clock.UtcNow = clock.UtcNow.AddHours(1);
        await NotifyHosts.RunJobAsync(scheduler, WebhookRetryJob.JobKey);
        Assert.Equal(3, receiver.Hits.Count);   // delivered deliveries are never sent again
    }

    [Fact]
    public async Task A_slow_receiver_is_given_up_on_after_the_timeout_and_the_failure_says_so()
    {
        var (_, admin, folder) = await TenantWithPacks();
        await using var receiver = await TestReceiver.StartAsync();
        receiver.Delay = TimeSpan.FromSeconds(4);
        var (hook, _) = await NewHook(admin, receiver.Url, events: ["asset.created"]);
        using var worker = NotifyHosts.Worker(Pg, mq, NotifyHosts.Settings(s => s["DAM_WEBHOOK_TIMEOUT_SECONDS"] = "1"));
        await worker.StartAsync();
        await NewAsset(admin, folder);

        var d = Assert.Single(await NotifyHosts.Eventually(() => Deliveries(admin, hook), x => x.Count == 1 && x[0].GetProperty("attempt").GetInt32() == 1 && x[0].GetProperty("lastError").ValueKind != JsonValueKind.Null));
        Assert.Equal("pending", d.GetProperty("status").GetString());
        Assert.Equal("The request timed out.", d.GetProperty("lastError").GetString());
        Assert.Equal(JsonValueKind.Null, d.GetProperty("lastStatusCode").ValueKind);
        Assert.InRange(d.GetProperty("lastDurationMs").GetInt32(), 900, 3000);
    }

    // ---- isolation ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task One_tenants_events_never_reach_another_tenants_webhook()
    {
        var a = await TenantWithPacks();
        var b = await TenantWithPacks();
        await using var receiverA = await TestReceiver.StartAsync();
        await using var receiverB = await TestReceiver.StartAsync();
        var (hookA, secretA) = await NewHook(a.Admin, receiverA.Url, "A", ["asset.created"]);
        var (hookB, _) = await NewHook(b.Admin, receiverB.Url, "B", ["asset.created"]);
        using var worker = NotifyHosts.Worker(Pg, mq);
        await worker.StartAsync();

        var assetA = await NewAsset(a.Admin, a.Brand, title: "Only A");
        var hit = await receiverA.WaitForAsync(h => h.Headers["X-Dam-Event"] == "asset.created");
        await Task.Delay(1500);

        Assert.Empty(receiverB.Hits);
        AssertSigned(hit, secretA);
        Assert.Equal(a.Tenant, JsonDocument.Parse(hit.Body).RootElement.GetProperty("tenantId").GetGuid());
        Assert.Equal(assetA, JsonDocument.Parse(hit.Body).RootElement.GetProperty("data").GetProperty("assetId").GetGuid());
        Assert.Empty(await Deliveries(b.Admin, hookB));
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"{Hooks}/{hookA}/deliveries", b.Admin)).Status);
        Assert.Equal(0, Count(b.Tenant, "SELECT count(*) FROM webhook_deliveries"));

        // And the other way round, through the same worker.
        await NewAsset(b.Admin, b.Brand, title: "Only B");
        await receiverB.WaitForAsync(h => h.Headers["X-Dam-Event"] == "asset.created");
        Assert.Equal(b.Tenant, JsonDocument.Parse(receiverB.Hits[0].Body).RootElement.GetProperty("tenantId").GetGuid());
        await Task.Delay(500);
        Assert.Equal(1, Created(receiverA));
    }

    // ---- the retry job's own contract --------------------------------------------------------------------------

    [Fact]
    public async Task The_retry_job_covers_every_tenant_and_leaves_a_fresh_delivery_to_the_attempt_that_created_it()
    {
        var a = await NewTenantAsync(); var adminA = Admin(a);
        var b = await NewTenantAsync(); var adminB = Admin(b);
        await using var receiverA = await TestReceiver.StartAsync();
        await using var receiverB = await TestReceiver.StartAsync();
        receiverA.Respond = _ => 500;
        receiverB.Respond = _ => 500;
        var (hookA, _) = await NewHook(adminA, receiverA.Url);
        var (hookB, _) = await NewHook(adminB, receiverB.Url);
        var pingA = (await Call(HttpMethod.Post, $"{Hooks}/{hookA}/ping", adminA)).Body;
        var pingB = (await Call(HttpMethod.Post, $"{Hooks}/{hookB}/ping", adminB)).Body;
        var latest = new[] { pingA, pingB }.Max(p => p.GetProperty("nextAttemptAt").GetDateTimeOffset());

        // A delivery created but not yet attempted (its creator holds the lease) is not picked up by the job...
        var lease = DateTimeOffset.UtcNow.AddSeconds(25);
        Sql($"INSERT INTO webhook_deliveries (id, tenant_id, webhook_id, event_id, event_type, payload, status, attempt, created_at, next_attempt_at) VALUES (gen_random_uuid(), '{a}', '{hookA}', gen_random_uuid(), 'asset.created', '{{}}', 'pending', 0, now(), '{lease:O}')");
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        using var scheduler = NotifyHosts.Scheduler(Pg, clock);
        await NotifyHosts.RunJobAsync(scheduler, WebhookRetryJob.JobKey);
        Assert.Single(receiverA.Hits);

        // ...but one whose creator died is, once its lease has run out, and both tenants' failures are retried in one run.
        clock.UtcNow = latest.AddMinutes(1);
        await NotifyHosts.RunJobAsync(scheduler, WebhookRetryJob.JobKey);
        Assert.Equal(3, receiverA.Hits.Count);   // the ping's retry and the abandoned delivery
        Assert.Equal(2, receiverB.Hits.Count);
        Assert.Single(await Deliveries(adminB, hookB));
    }
}
