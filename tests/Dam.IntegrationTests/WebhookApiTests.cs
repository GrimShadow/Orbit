using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dam.Domain.Webhooks;
using Dam.Infrastructure.Notifications;
using Dam.Shared.Webhooks;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace Dam.IntegrationTests;

/// <summary>The webhook management API (ADR-0031): CRUD, secrets, validation, permissions, ping, replay, the delivery log, SSRF and isolation. No broker needed.</summary>
[Collection("pg")]
public sealed class WebhookApiTests(PostgresFixture pg) : AssetTestBase(pg)
{
    private const string Hooks = "/api/v1/webhooks";
    private const string Log = "/api/v1/webhook-deliveries";

    private async Task<(Guid Id, string Secret, JsonElement Body)> NewHook(string admin, string url, string name = "Primary", string[]? events = null, bool active = true)
    {
        var r = await Call(HttpMethod.Post, Hooks, admin, new { name, url, events = events ?? ["asset.*"], active });
        Assert.True(r.Status == HttpStatusCode.Created, r.Body.ToString());
        return (Id(r.Body, "webhook", "id"), r.Body.GetProperty("secret").GetString()!, r.Body);
    }

    private static string Header(TestReceiver.Hit hit, string name) => hit.Headers[name];

    // ---- create, read, secrets ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_webhook_is_created_with_a_secret_shown_once_stored_encrypted_and_never_returned_again()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var (id, secret, body) = await NewHook(admin, "https://example.com/hook", events: ["Asset.Published", "workflow.*"]);

        Assert.StartsWith("whsec_", secret);
        Assert.Equal(secret[^4..], body.GetProperty("webhook").GetProperty("secretLast4").GetString());
        Assert.Equal(["asset.published", "workflow.*"], Strings(body.GetProperty("webhook").GetProperty("events")));   // normalised
        Assert.True(body.GetProperty("webhook").GetProperty("active").GetBoolean());

        foreach (var response in new[] { (await Call(HttpMethod.Get, $"{Hooks}/{id}", admin)).Body, (await Call(HttpMethod.Get, Hooks, admin)).Body[0] })
        {
            Assert.DoesNotContain(secret, response.ToString());
            Assert.False(response.TryGetProperty("secret", out _));
            Assert.Equal(secret[^4..], response.GetProperty("secretLast4").GetString());
        }

        // At rest it is ciphertext, not the secret.
        using var c = new NpgsqlConnection(Pg.OwnerConnection);
        c.Open();
        using var cmd = new NpgsqlCommand("SELECT secret_cipher FROM webhooks WHERE id = @id", c);
        cmd.Parameters.AddWithValue("id", id);
        var cipher = (string)cmd.ExecuteScalar()!;
        Assert.StartsWith("v1.", cipher);
        Assert.DoesNotContain(secret, cipher);
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'webhook.created'"));
    }

    [Fact]
    public async Task Rotating_gives_a_new_secret_once_and_the_old_one_no_longer_signs()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var receiver = await TestReceiver.StartAsync();
        var (id, oldSecret, _) = await NewHook(admin, receiver.Url);

        var rotated = await Call(HttpMethod.Post, $"{Hooks}/{id}/rotate-secret", admin);
        Assert.Equal(HttpStatusCode.OK, rotated.Status);
        var newSecret = rotated.Body.GetProperty("secret").GetString()!;
        Assert.NotEqual(oldSecret, newSecret);
        Assert.Equal(newSecret[^4..], rotated.Body.GetProperty("webhook").GetProperty("secretLast4").GetString());

        var ping = await Call(HttpMethod.Post, $"{Hooks}/{id}/ping", admin);
        Assert.Equal(HttpStatusCode.OK, ping.Status);
        var hit = Assert.Single(receiver.Hits);
        Assert.Equal(WebhookVerification.Valid, WebhookSignature.Verify(newSecret, Header(hit, "X-Dam-Timestamp"), Header(hit, "X-Dam-Signature"), hit.Body, DateTimeOffset.UtcNow, WebhookSignature.DefaultTolerance));
        Assert.Equal(WebhookVerification.BadSignature, WebhookSignature.Verify(oldSecret, Header(hit, "X-Dam-Timestamp"), Header(hit, "X-Dam-Signature"), hit.Body, DateTimeOffset.UtcNow, WebhookSignature.DefaultTolerance));
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'webhook.secret_rotated'"));
    }

    [Fact]
    public async Task A_webhook_can_be_updated_switched_off_and_deleted_with_its_delivery_history()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var receiver = await TestReceiver.StartAsync();
        var (id, _, _) = await NewHook(admin, receiver.Url);
        await Call(HttpMethod.Post, $"{Hooks}/{id}/ping", admin);
        Assert.Equal(1, Count(t, "SELECT count(*) FROM webhook_deliveries"));

        var put = await Call(HttpMethod.Put, $"{Hooks}/{id}", admin, new { name = "Renamed", url = receiver.Url + "?v=2", events = new[] { "collection.updated" }, active = false });
        Assert.Equal(HttpStatusCode.OK, put.Status);
        Assert.Equal(("Renamed", false), (put.Body.GetProperty("name").GetString(), put.Body.GetProperty("active").GetBoolean()));
        Assert.Equal(["collection.updated"], Strings(put.Body.GetProperty("events")));

        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"{Hooks}/{id}", admin)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"{Hooks}/{id}", admin)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Delete, $"{Hooks}/{id}", admin)).Status);
        Assert.Equal(0, Count(t, "SELECT count(*) FROM webhook_deliveries"));   // the cascade works for the restricted app role
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'webhook.deleted'"));
    }

    [Fact]
    public async Task The_event_catalogue_lists_what_can_be_subscribed_to()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var r = await Call(HttpMethod.Get, $"{Hooks}/events", admin);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        var names = r.Body.EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToArray();
        foreach (var n in new[] { "asset.created", "asset.published", "asset.status.changed", "collection.updated", "workflow.task.created", "workflow.task.completed", "workflow.sla.breached", "webhook.ping" })
            Assert.Contains(n, names);
        var published = r.Body.EnumerateArray().Single(e => e.GetProperty("name").GetString() == "asset.published");
        Assert.True(published.GetProperty("derived").GetBoolean());
        Assert.Equal("asset", published.GetProperty("category").GetString());
        Assert.DoesNotContain("upload.completed", names);
    }

    // ---- validation --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Bad_input_is_a_422_with_the_field_named()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        async Task<JsonElement> Errors(object body)
        {
            var r = await Call(HttpMethod.Post, Hooks, admin, body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status);
            return r.Body.GetProperty("errors");
        }
        Assert.True((await Errors(new { name = "", url = "https://example.com/h", events = new[] { "asset.*" } })).TryGetProperty("Name", out _));
        Assert.True((await Errors(new { name = "x", url = "not a url", events = new[] { "asset.*" } })).TryGetProperty("Url", out _));
        Assert.True((await Errors(new { name = "x", url = "https://example.com/h", events = Array.Empty<string>() })).TryGetProperty("Events", out _));
        Assert.True((await Errors(new { name = "x", url = "https://example.com/h", events = new[] { "asset.published", "bogus.event" } })).TryGetProperty("Events[1]", out _));
        Assert.True((await Errors(new { name = "x", url = "https://example.com/h", events = new[] { "*.published" } })).TryGetProperty("Events[0]", out _));
        Assert.True((await Errors(new { name = "x", url = "ftp://example.com/h", events = new[] { "*" } })).TryGetProperty("Url", out _));
        Assert.Equal(0, Count(t, "SELECT count(*) FROM webhooks"));
    }

    // ---- permissions -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Only_webhook_managers_can_use_the_webhook_surface()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var (id, _, _) = await NewHook(admin, "https://example.com/hook");
        var deliveryId = Id((await Call(HttpMethod.Post, $"{Hooks}/{id}/ping", admin)).Body, "id");
        foreach (var role in new[] { "Librarian", "Editor", "Approver", "Contributor", "Viewer", "Dealer", "ApiClient" })
        {
            var token = As(t, role);
            foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
            {
                (HttpMethod.Get, Hooks, null), (HttpMethod.Get, $"{Hooks}/events", null), (HttpMethod.Get, $"{Hooks}/{id}", null),
                (HttpMethod.Post, Hooks, new { name = "x", url = "https://example.com/h", events = new[] { "*" } }),
                (HttpMethod.Put, $"{Hooks}/{id}", new { name = "x", url = "https://example.com/h", events = new[] { "*" }, active = true }),
                (HttpMethod.Delete, $"{Hooks}/{id}", null), (HttpMethod.Post, $"{Hooks}/{id}/rotate-secret", null), (HttpMethod.Post, $"{Hooks}/{id}/ping", null),
                (HttpMethod.Get, $"{Hooks}/{id}/deliveries", null), (HttpMethod.Get, Log, null), (HttpMethod.Get, $"{Log}/{deliveryId}", null),
                (HttpMethod.Post, $"{Log}/{deliveryId}/replay", null),
            })
                Assert.True(HttpStatusCode.Forbidden == (await Call(method, path, token, body)).Status, $"{role} {method} {path}");
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(HttpMethod.Get, Hooks, "")).Status);
        Assert.Equal(1, Count(t, "SELECT count(*) FROM webhooks"));   // nothing above changed anything
    }

    // ---- ping and replay ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_ping_sends_a_signed_webhook_ping_event_and_returns_the_outcome()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var receiver = await TestReceiver.StartAsync();
        receiver.ResponseBody = "pong";
        var (id, secret, _) = await NewHook(admin, receiver.Url, events: ["asset.created"]);   // a ping goes out whatever the subscription

        var r = await Call(HttpMethod.Post, $"{Hooks}/{id}/ping", admin);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal(("webhook.ping", "succeeded", 1, 200), (r.Body.GetProperty("eventType").GetString(), r.Body.GetProperty("status").GetString(), r.Body.GetProperty("attempt").GetInt32(), r.Body.GetProperty("lastStatusCode").GetInt32()));
        Assert.Equal("pong", r.Body.GetProperty("responseExcerpt").GetString());
        Assert.Equal(8, r.Body.GetProperty("maxAttempts").GetInt32());

        var hit = Assert.Single(receiver.Hits);
        Assert.Equal("webhook.ping", Header(hit, "X-Dam-Event"));
        Assert.Equal(r.Body.GetProperty("id").GetGuid().ToString(), Header(hit, "X-Dam-Delivery"));
        Assert.Equal(WebhookVerification.Valid, WebhookSignature.Verify(secret, Header(hit, "X-Dam-Timestamp"), Header(hit, "X-Dam-Signature"), hit.Body, DateTimeOffset.UtcNow, WebhookSignature.DefaultTolerance));
        var envelope = JsonDocument.Parse(hit.Body).RootElement;
        Assert.Equal("webhook.ping", envelope.GetProperty("type").GetString());
        Assert.Equal(t, envelope.GetProperty("tenantId").GetGuid());
        Assert.Equal(id, envelope.GetProperty("data").GetProperty("webhookId").GetGuid());
        Assert.Equal("application/json", hit.Headers["Content-Type"].Split(';')[0]);
    }

    [Fact]
    public async Task A_failing_ping_shows_the_failure_and_stays_pending_for_a_retry()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var receiver = await TestReceiver.StartAsync();
        receiver.Respond = _ => 503;
        receiver.ResponseBody = "maintenance";
        var (id, _, _) = await NewHook(admin, receiver.Url);

        var r = await Call(HttpMethod.Post, $"{Hooks}/{id}/ping", admin);
        Assert.Equal(HttpStatusCode.OK, r.Status);   // the ping itself worked; the receiver is what failed
        Assert.Equal(("pending", 1, 503), (r.Body.GetProperty("status").GetString(), r.Body.GetProperty("attempt").GetInt32(), r.Body.GetProperty("lastStatusCode").GetInt32()));
        Assert.Contains("503", r.Body.GetProperty("lastError").GetString());
        Assert.Equal("maintenance", r.Body.GetProperty("responseExcerpt").GetString());
        var next = r.Body.GetProperty("nextAttemptAt").GetDateTimeOffset();
        Assert.InRange((next - DateTimeOffset.UtcNow).TotalSeconds, 7, 13);   // 10 s plus or minus 20% jitter
    }

    [Fact]
    public async Task Replaying_creates_a_new_delivery_of_the_same_payload_and_attempts_it_now()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var receiver = await TestReceiver.StartAsync();
        var (id, secret, _) = await NewHook(admin, receiver.Url);
        var first = (await Call(HttpMethod.Post, $"{Hooks}/{id}/ping", admin)).Body;

        var replay = await Call(HttpMethod.Post, $"{Log}/{first.GetProperty("id").GetGuid()}/replay", admin);
        Assert.Equal(HttpStatusCode.OK, replay.Status);
        Assert.NotEqual(first.GetProperty("id").GetGuid(), replay.Body.GetProperty("id").GetGuid());
        Assert.Equal(first.GetProperty("id").GetGuid(), replay.Body.GetProperty("replayOf").GetGuid());
        Assert.Equal(first.GetProperty("eventId").GetGuid(), replay.Body.GetProperty("eventId").GetGuid());
        Assert.Equal(("succeeded", 1), (replay.Body.GetProperty("status").GetString(), replay.Body.GetProperty("attempt").GetInt32()));
        Assert.Equal(first.GetProperty("payload").ToString(), replay.Body.GetProperty("payload").ToString());

        Assert.Equal(2, receiver.Hits.Count);
        Assert.Equal(receiver.Hits[0].Body, receiver.Hits[1].Body);                                   // same envelope, same event id
        Assert.NotEqual(Header(receiver.Hits[0], "X-Dam-Delivery"), Header(receiver.Hits[1], "X-Dam-Delivery"));
        var h = receiver.Hits[1];
        Assert.Equal(WebhookVerification.Valid, WebhookSignature.Verify(secret, Header(h, "X-Dam-Timestamp"), Header(h, "X-Dam-Signature"), h.Body, DateTimeOffset.UtcNow, WebhookSignature.DefaultTolerance));
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"{Log}/{Guid.NewGuid()}/replay", admin)).Status);
    }

    [Fact]
    public async Task A_switched_off_webhook_is_not_called_by_ping_or_replay()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var receiver = await TestReceiver.StartAsync();
        var (id, _, _) = await NewHook(admin, receiver.Url);
        var delivery = Id((await Call(HttpMethod.Post, $"{Hooks}/{id}/ping", admin)).Body, "id");
        await Call(HttpMethod.Put, $"{Hooks}/{id}", admin, new { name = "Primary", url = receiver.Url, events = new[] { "asset.*" }, active = false });

        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"{Hooks}/{id}/ping", admin)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"{Log}/{delivery}/replay", admin)).Status);
        Assert.Single(receiver.Hits);
    }

    // ---- the delivery log --------------------------------------------------------------------------------------

    private void InsertDelivery(Guid tenant, Guid hook, string type, string status, DateTimeOffset created, Guid? id = null)
    {
        using var c = new NpgsqlConnection(Pg.OwnerConnection);
        c.Open();
        using var cmd = new NpgsqlCommand("""
            INSERT INTO webhook_deliveries (id, tenant_id, webhook_id, event_id, event_type, payload, status, attempt, created_at, next_attempt_at)
            VALUES (@id, @t, @h, gen_random_uuid(), @type, '{}'::jsonb, @status, 1, @created, CASE WHEN @status = 'pending' THEN @created + interval '1 hour' END)
            """, c);
        cmd.Parameters.AddWithValue("id", id ?? Dam.Domain.Common.Uuid7.NewGuid());
        cmd.Parameters.AddWithValue("t", tenant);
        cmd.Parameters.AddWithValue("h", hook);
        cmd.Parameters.AddWithValue("type", type);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("created", created.ToUniversalTime());
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task The_delivery_log_filters_by_webhook_status_event_type_and_date_and_pages_by_cursor()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var (h1, _, _) = await NewHook(admin, "https://example.com/one", "One");
        var (h2, _, _) = await NewHook(admin, "https://example.com/two", "Two");
        var day = new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 7; i++) InsertDelivery(t, h1, i % 2 == 0 ? "asset.created" : "workflow.task.created", i < 4 ? "succeeded" : i < 6 ? "failed" : "pending", day.AddMinutes(i));
        InsertDelivery(t, h2, "asset.published", "succeeded", day.AddDays(2));

        async Task<List<JsonElement>> Items(string query)
        {
            var r = await Call(HttpMethod.Get, query, admin);
            Assert.Equal(HttpStatusCode.OK, r.Status);
            return r.Body.GetProperty("items").EnumerateArray().ToList();
        }

        Assert.Equal(8, (await Items($"{Log}?limit=50")).Count);
        Assert.Equal(7, (await Items($"{Hooks}/{h1}/deliveries")).Count);
        Assert.Single(await Items($"{Hooks}/{h2}/deliveries"));
        Assert.Single(await Items($"{Log}?webhookId={h2}"));
        Assert.Equal(2, (await Items($"{Log}?status=failed")).Count);
        Assert.Single(await Items($"{Log}?status=pending"));
        Assert.Equal(5, (await Items($"{Log}?status=succeeded")).Count);
        Assert.Equal(4, (await Items($"{Log}?eventType=asset.created")).Count);
        Assert.Equal(3, (await Items($"{Log}?eventType=workflow.*")).Count);
        Assert.Equal(5, (await Items($"{Log}?eventType=asset.*")).Count);
        Assert.Equal(7, (await Items($"{Log}?from={Uri.EscapeDataString(day.AddHours(-1).ToString("O"))}&to={Uri.EscapeDataString(day.AddHours(1).ToString("O"))}")).Count);
        Assert.Single(await Items($"{Log}?from={Uri.EscapeDataString(day.AddDays(1).ToString("O"))}"));
        Assert.Single(await Items($"{Log}?status=succeeded&eventType=asset.*&webhookId={h2}"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Get, $"{Log}?status=bogus", admin)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"{Hooks}/{Guid.NewGuid()}/deliveries", admin)).Status);

        // Paging: newest first, no repeats, no gaps.
        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = (await Call(HttpMethod.Get, $"{Log}?limit=3" + (cursor is null ? "" : $"&cursor={cursor}"), admin)).Body;
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
            cursor = page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextCursor").GetString();
        } while (cursor is not null);
        Assert.Equal(8, seen.Count);
        Assert.Equal(8, seen.Distinct().Count());
        Assert.Equal(seen.OrderByDescending(x => x), seen);
    }

    [Fact]
    public async Task A_delivery_shows_its_exact_payload_and_the_list_carries_per_webhook_statistics()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var receiver = await TestReceiver.StartAsync();
        var (id, _, _) = await NewHook(admin, receiver.Url);
        await Call(HttpMethod.Post, $"{Hooks}/{id}/ping", admin);
        receiver.Respond = _ => 500;
        await Call(HttpMethod.Post, $"{Hooks}/{id}/ping", admin);

        var list = (await Call(HttpMethod.Get, $"{Hooks}/{id}/deliveries", admin)).Body.GetProperty("items");
        var detail = (await Call(HttpMethod.Get, $"{Log}/{list[0].GetProperty("id").GetGuid()}", admin)).Body;
        Assert.Equal("webhook.ping", detail.GetProperty("payload").GetProperty("type").GetString());
        Assert.Equal("Primary", detail.GetProperty("webhookName").GetString());

        var stats = (await Call(HttpMethod.Get, $"{Hooks}/{id}", admin)).Body.GetProperty("stats");
        Assert.Equal((1, 0, 1), (stats.GetProperty("succeeded24h").GetInt32(), stats.GetProperty("failed24h").GetInt32(), stats.GetProperty("pending").GetInt32()));
        Assert.Equal("pending", stats.GetProperty("lastStatus").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"{Log}/{Guid.NewGuid()}", admin)).Status);
    }

    // ---- SSRF --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("http://127.0.0.1:9/hook")]
    [InlineData("http://localhost/hook")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://10.0.0.5/hook")]
    [InlineData("http://192.168.1.10:8080/hook")]
    [InlineData("http://172.16.0.1/hook")]
    [InlineData("http://[::1]/hook")]
    [InlineData("http://[fd00:ec2::254]/")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://2130706433/")]      // 127.0.0.1 as a single decimal number
    public async Task Private_and_internal_targets_are_refused_when_the_server_does_not_allow_them(string url)
    {
        await using var strict = new ApiTestHost(Pg).Factory(allowPrivateWebhooks: false);
        var t = Guid.NewGuid();
        await ApiTestHost.EnsureTenantAsync(strict, t);
        using var client = strict.CreateClient();
        var token = ApiTestHost.TokenFor(t, Guid.NewGuid(), ["Admin"]);

        using var req = new HttpRequestMessage(HttpMethod.Post, Hooks) { Headers = { Authorization = new("Bearer", token) }, Content = JsonContent.Create(new { name = "x", url, events = new[] { "*" } }) };
        using var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.Contains("url", body.GetProperty("errors").EnumerateObject().Select(p => p.Name.ToLowerInvariant()));
        Assert.Equal(0, Count(t, "SELECT count(*) FROM webhooks"));
    }

    [Fact]
    public async Task Non_http_schemes_are_refused_even_when_private_targets_are_allowed()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        foreach (var url in new[] { "file:///etc/passwd", "ftp://example.com/x", "gopher://example.com", "javascript:alert(1)", "http://user:pw@example.com/x" })
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, Hooks, admin, new { name = "x", url, events = new[] { "*" } })).Status);
    }

    [Fact]
    public async Task Every_delivery_is_checked_again_so_a_target_that_became_private_is_never_called()
    {
        // Created while private targets were allowed (or pointed at a public name that later resolved inside the network)...
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var receiver = await TestReceiver.StartAsync();
        var (id, _, _) = await NewHook(admin, receiver.Url);

        // ...and now the server is strict. The delivery path refuses the connection itself.
        await using var strict = new ApiTestHost(Pg).Factory(allowPrivateWebhooks: false);
        using var client = strict.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Hooks}/{id}/ping") { Headers = { Authorization = new("Bearer", admin) } };
        using var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var delivery = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("pending", delivery.GetProperty("status").GetString());
        Assert.Contains("Blocked", delivery.GetProperty("lastError").GetString());
        Assert.Empty(receiver.Hits);
    }

    [Fact]
    public async Task A_name_that_resolves_to_a_loopback_address_is_refused_at_connect_time_and_a_redirect_is_never_followed()
    {
        using var strict = new OutboundHttp(new Dam.Application.Abstractions.WebhookSettings { AllowPrivate = false });
        await Assert.ThrowsAsync<BlockedDestinationException>(() => strict.ResolveAsync("localhost", default));
        await Assert.ThrowsAsync<BlockedDestinationException>(() => strict.ResolveAsync("127.0.0.1", default));
        var blocked = await Assert.ThrowsAsync<HttpRequestException>(() => strict.Client.GetAsync("http://localhost:9/"));
        Assert.Contains("not allowed", Walk(blocked));

        // With private targets allowed (development), a 302 is reported as the response, not chased.
        await using var receiver = await TestReceiver.StartAsync();
        receiver.Respond = _ => 302;
        using var open = new OutboundHttp(new Dam.Application.Abstractions.WebhookSettings { AllowPrivate = true });
        using var res = await open.Client.GetAsync(receiver.Url);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Single(receiver.Hits);

        static string Walk(Exception e) => string.Join(" | ", Chain(e).Select(x => x.Message));
        static IEnumerable<Exception> Chain(Exception? e) { for (; e is not null; e = e.InnerException) yield return e; }
    }

    // ---- isolation ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Webhooks_and_deliveries_are_isolated_per_tenant_by_the_api_and_by_row_level_security()
    {
        var a = await NewTenantAsync(); var adminA = Admin(a);
        var b = await NewTenantAsync(); var adminB = Admin(b);
        await using var receiver = await TestReceiver.StartAsync();
        var (hookA, _, _) = await NewHook(adminA, receiver.Url, "Tenant A hook");
        var deliveryA = Id((await Call(HttpMethod.Post, $"{Hooks}/{hookA}/ping", adminA)).Body, "id");

        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"{Hooks}/{hookA}", adminB)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Put, $"{Hooks}/{hookA}", adminB, new { name = "x", url = "https://example.com/h", events = new[] { "*" }, active = true })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Delete, $"{Hooks}/{hookA}", adminB)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"{Hooks}/{hookA}/ping", adminB)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"{Hooks}/{hookA}/rotate-secret", adminB)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"{Log}/{deliveryA}", adminB)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"{Log}/{deliveryA}/replay", adminB)).Status);
        Assert.Empty((await Call(HttpMethod.Get, Hooks, adminB)).Body.EnumerateArray());
        Assert.Empty((await Call(HttpMethod.Get, Log, adminB)).Body.GetProperty("items").EnumerateArray());

        foreach (var table in new[] { "webhooks", "webhook_deliveries" })
        {
            Assert.True(Count(a, $"SELECT count(*) FROM {table}") > 0, table);
            Assert.Equal(0, Count(b, $"SELECT count(*) FROM {table} WHERE tenant_id = '{a}'"));
            using var none = Pg.RawAppConnection(null);
            using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {table}", none);
            Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));
        }
        using var conn = Pg.RawAppConnection(b);
        using var insert = new NpgsqlCommand($"INSERT INTO webhooks (id, tenant_id, name, url, secret_cipher, secret_last4, events, active, created_at, updated_at) VALUES (gen_random_uuid(), '{a}', 'x', 'https://x.test', 'c', 'abcd', '{{*}}', true, now(), now())", conn);
        Assert.Throws<PostgresException>(() => insert.ExecuteNonQuery());
    }

    [Fact]
    public async Task The_app_role_can_never_delete_deliveries_directly_and_the_system_role_can_only_read_them()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var receiver = await TestReceiver.StartAsync();
        var (id, _, _) = await NewHook(admin, receiver.Url);
        await Call(HttpMethod.Post, $"{Hooks}/{id}/ping", admin);

        using (var c = Pg.RawAppConnection(t))
        using (var cmd = new NpgsqlCommand("DELETE FROM webhook_deliveries", c))
            Assert.Equal("42501", Assert.Throws<PostgresException>(() => cmd.ExecuteNonQuery()).SqlState);

        await using var sys = new NpgsqlConnection(Pg.SystemConnection);
        await sys.OpenAsync();
        await using (var read = new NpgsqlCommand("SELECT count(*) FROM webhook_deliveries", sys))
            Assert.True(Convert.ToInt64(await read.ExecuteScalarAsync()) >= 1);
        foreach (var sql in new[] { "SELECT count(*) FROM webhooks", "UPDATE webhook_deliveries SET status = 'failed'", "DELETE FROM webhook_deliveries", "SELECT count(*) FROM notifications" })
        {
            await using var cmd = new NpgsqlCommand(sql, sys);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync())).SqlState);
        }
    }
}
