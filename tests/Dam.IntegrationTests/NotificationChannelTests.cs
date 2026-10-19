using System.Net;
using System.Text.Json;
using Npgsql;

namespace Dam.IntegrationTests;

/// <summary>Slack and Teams channels (ADR-0031): management API, secrecy of the URL, the test button, and messages for matching events.</summary>
[Collection("pg")]
public sealed class NotificationChannelTests(PostgresFixture pg, RabbitFixture mq) : AssetTestBase(pg), IClassFixture<RabbitFixture>
{
    private const string Channels = "/api/v1/notification-channels";

    private async Task<JsonElement> NewChannel(string admin, string url, string kind = "slack", string name = "Ops", string[]? events = null, bool active = true)
    {
        var r = await Call(HttpMethod.Post, Channels, admin, new { kind, name, url, events = events ?? ["*"], active });
        Assert.True(r.Status == HttpStatusCode.Created, r.Body.ToString());
        return r.Body;
    }

    // ---- management --------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_channel_is_created_read_updated_and_deleted_and_its_url_is_a_secret()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        const string url = "https://hooks.slack.example/services/T000/B000/SECRETTOKENa1b2";
        var created = await Call(HttpMethod.Post, Channels, admin, new { kind = "slack", name = "Ops alerts", url, events = new[] { "Workflow.*", "asset.published" }, active = true });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var id = created.Body.GetProperty("id").GetGuid();
        Assert.Equal(("slack", "Ops alerts", true), (created.Body.GetProperty("kind").GetString(), created.Body.GetProperty("name").GetString(), created.Body.GetProperty("active").GetBoolean()));
        Assert.Equal(["workflow.*", "asset.published"], Strings(created.Body.GetProperty("events")));
        Assert.EndsWith("a1b2", created.Body.GetProperty("urlHint").GetString());

        foreach (var shown in new[] { created.Body.ToString(), (await Call(HttpMethod.Get, $"{Channels}/{id}", admin)).Body.ToString(), (await Call(HttpMethod.Get, Channels, admin)).Body.ToString() })
        {
            Assert.DoesNotContain("SECRETTOKEN", shown);
            Assert.DoesNotContain("\"url\"", shown);
        }
        using (var c = new NpgsqlConnection(Pg.OwnerConnection))
        {
            c.Open();
            using var cmd = new NpgsqlCommand("SELECT url_cipher FROM notification_channels WHERE id = @id", c);
            cmd.Parameters.AddWithValue("id", id);
            var cipher = (string)cmd.ExecuteScalar()!;
            Assert.StartsWith("v1.", cipher);
            Assert.DoesNotContain("SECRETTOKEN", cipher);
        }

        // Updating without a url keeps the stored one; with a url replaces it.
        var keep = await Call(HttpMethod.Put, $"{Channels}/{id}", admin, new { kind = "teams", name = "Renamed", url = (string?)null, events = new[] { "*" }, active = false });
        Assert.Equal(HttpStatusCode.OK, keep.Status);
        Assert.Equal(("teams", "Renamed", false), (keep.Body.GetProperty("kind").GetString(), keep.Body.GetProperty("name").GetString(), keep.Body.GetProperty("active").GetBoolean()));
        Assert.EndsWith("a1b2", keep.Body.GetProperty("urlHint").GetString());
        var replaced = await Call(HttpMethod.Put, $"{Channels}/{id}", admin, new { kind = "teams", name = "Renamed", url = "https://teams.example/workflows/xyz9", events = new[] { "*" }, active = true });
        Assert.EndsWith("xyz9", replaced.Body.GetProperty("urlHint").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"{Channels}/{id}", admin)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"{Channels}/{id}", admin)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Delete, $"{Channels}/{id}", admin)).Status);
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'notification_channel.created'"));
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'notification_channel.deleted'"));
    }

    [Fact]
    public async Task Bad_channel_input_is_refused_per_field()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        async Task<JsonElement> Errors(object body) { var r = await Call(HttpMethod.Post, Channels, admin, body); Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status); return r.Body.GetProperty("errors"); }
        Assert.True((await Errors(new { kind = "discord", name = "x", url = "https://example.com/h", events = new[] { "*" } })).TryGetProperty("Kind", out _));
        Assert.True((await Errors(new { kind = "slack", name = "", url = "https://example.com/h", events = new[] { "*" } })).TryGetProperty("Name", out _));
        Assert.True((await Errors(new { kind = "slack", name = "x", url = "ftp://example.com/h", events = new[] { "*" } })).TryGetProperty("Url", out _));
        Assert.True((await Errors(new { kind = "slack", name = "x", url = (string?)null, events = new[] { "*" } })).TryGetProperty("Url", out _));
        Assert.True((await Errors(new { kind = "slack", name = "x", url = "https://example.com/h", events = new[] { "nonsense" } })).TryGetProperty("Events[0]", out _));
        Assert.True((await Errors(new { kind = "slack", name = "x", url = "https://example.com/h", events = Array.Empty<string>() })).TryGetProperty("Events", out _));
        Assert.Equal(0, Count(t, "SELECT count(*) FROM notification_channels"));
    }

    [Fact]
    public async Task Only_holders_of_notifications_manage_can_manage_channels()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var receiver = await TestReceiver.StartAsync();
        var id = (await NewChannel(admin, receiver.Url)).GetProperty("id").GetGuid();
        foreach (var role in new[] { "Librarian", "Editor", "Approver", "Contributor", "Viewer", "Dealer", "ApiClient" })
        {
            var token = As(t, role);
            foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
            {
                (HttpMethod.Get, Channels, null), (HttpMethod.Get, $"{Channels}/{id}", null),
                (HttpMethod.Post, Channels, new { kind = "slack", name = "x", url = "https://example.com/h", events = new[] { "*" } }),
                (HttpMethod.Put, $"{Channels}/{id}", new { kind = "slack", name = "x", url = (string?)null, events = new[] { "*" }, active = true }),
                (HttpMethod.Delete, $"{Channels}/{id}", null), (HttpMethod.Post, $"{Channels}/{id}/test", null),
            })
                Assert.True(HttpStatusCode.Forbidden == (await Call(method, path, token, body)).Status, $"{role} {method} {path}");
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(HttpMethod.Get, Channels, "")).Status);
        Assert.Empty(receiver.Hits);
        // The event catalogue serves pickers for both kinds of manager (both are Admin-only in the built-in roles).
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, "/api/v1/webhooks/events", admin)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, "/api/v1/webhooks/events", As(t, "Editor"))).Status);
    }

    [Fact]
    public async Task The_test_button_posts_a_message_and_reports_what_the_service_answered()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await using var slack = await TestReceiver.StartAsync();
        await using var teams = await TestReceiver.StartAsync();
        var slackId = (await NewChannel(admin, slack.Url, "slack")).GetProperty("id").GetGuid();
        var teamsId = (await NewChannel(admin, teams.Url, "teams")).GetProperty("id").GetGuid();

        var ok = await Call(HttpMethod.Post, $"{Channels}/{slackId}/test", admin);
        Assert.Equal(HttpStatusCode.OK, ok.Status);
        Assert.True(ok.Body.GetProperty("ok").GetBoolean());
        Assert.Equal(200, ok.Body.GetProperty("statusCode").GetInt32());
        Assert.Contains("Orbit test message", JsonDocument.Parse(slack.Hits[0].Body).RootElement.GetProperty("text").GetString());

        Assert.True((await Call(HttpMethod.Post, $"{Channels}/{teamsId}/test", admin)).Body.GetProperty("ok").GetBoolean());
        var card = JsonDocument.Parse(teams.Hits[0].Body).RootElement;
        Assert.Equal("message", card.GetProperty("type").GetString());
        Assert.Equal("application/vnd.microsoft.card.adaptive", card.GetProperty("attachments")[0].GetProperty("contentType").GetString());

        slack.Respond = _ => 404;
        var failed = await Call(HttpMethod.Post, $"{Channels}/{slackId}/test", admin);
        Assert.Equal(HttpStatusCode.OK, failed.Status);   // the test ran; the service said no
        Assert.False(failed.Body.GetProperty("ok").GetBoolean());
        Assert.Equal(404, failed.Body.GetProperty("statusCode").GetInt32());
        Assert.Contains("404", failed.Body.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"{Channels}/{Guid.NewGuid()}/test", admin)).Status);
    }

    [Fact]
    public async Task Channels_are_isolated_per_tenant()
    {
        var a = await NewTenantAsync(); var adminA = Admin(a);
        var b = await NewTenantAsync(); var adminB = Admin(b);
        await using var receiver = await TestReceiver.StartAsync();
        var id = (await NewChannel(adminA, receiver.Url)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"{Channels}/{id}", adminB)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"{Channels}/{id}/test", adminB)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Delete, $"{Channels}/{id}", adminB)).Status);
        Assert.Empty((await Call(HttpMethod.Get, Channels, adminB)).Body.EnumerateArray());
        Assert.Empty(receiver.Hits);
    }

    // ---- events into channels ----------------------------------------------------------------------------------

    private static string TextOf(TestReceiver.Hit hit) => JsonDocument.Parse(hit.Body).RootElement.GetProperty("text").GetString()!;

    [Fact]
    public async Task A_matching_event_posts_a_message_once_and_non_matching_or_inactive_channels_hear_nothing()
    {
        var (t, admin, folder) = await TenantWithPacks();
        await using var assetsChannel = await TestReceiver.StartAsync();
        await using var workflowChannel = await TestReceiver.StartAsync();
        await using var inactive = await TestReceiver.StartAsync();
        await using var everything = await TestReceiver.StartAsync();
        await NewChannel(admin, assetsChannel.Url, "slack", "Assets", ["asset.created"]);
        await NewChannel(admin, workflowChannel.Url, "slack", "Workflow", ["workflow.*"]);
        await NewChannel(admin, inactive.Url, "slack", "Off", ["*"], active: false);
        await NewChannel(admin, everything.Url, "teams", "All", ["asset.created", "asset.published"]);
        using var worker = NotifyHosts.Worker(Pg, mq);
        await worker.StartAsync();

        var asset = await NewAsset(admin, folder, title: "Spring hero");
        var hit = await assetsChannel.WaitForAsync();
        Assert.Equal("*Asset created: Spring hero*\n<https://orbit.example.test/library/assets/" + asset + "|Open in Orbit>", TextOf(hit));
        var card = JsonDocument.Parse((await everything.WaitForAsync()).Body).RootElement.GetProperty("attachments")[0].GetProperty("content");
        Assert.Equal("Asset created: Spring hero", card.GetProperty("body")[0].GetProperty("text").GetString());
        Assert.Equal($"https://orbit.example.test/library/assets/{asset}", card.GetProperty("actions")[0].GetProperty("url").GetString());
        await Task.Delay(1000);
        Assert.Single(assetsChannel.Hits);   // asset.metadata.updated is not asset.created
        Assert.Empty(workflowChannel.Hits);
        Assert.Empty(inactive.Hits);

        // A derived event: published reaches the channel that asked for it by that name.
        foreach (var action in new[] { "submit", "approve", "publish" })
            Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/transitions/{action}", admin, new { reason = (string?)null })).Status);
        var published = await everything.WaitForAsync(h => h.Body.Contains("Asset published", StringComparison.Ordinal));
        Assert.Contains("Spring hero", published.Body);
        Assert.Contains("approved -", published.Body);   // the detail "approved -> published" (JSON escapes the ">")
        await Task.Delay(500);
        Assert.Equal(2, everything.Hits.Count);
        Assert.Equal(3, Count(t, "SELECT count(*) FROM notification_deliveries WHERE channel = 'chat'"));   // created for two channels, published for one
    }

    [Fact]
    public async Task A_workflow_wildcard_channel_hears_about_approval_tasks()
    {
        var (_, admin, folder) = await TenantWithPacks();
        var step = new { id = "review", name = "Review", assignees = new { roles = new[] { "Admin" }, users = Array.Empty<Guid>() }, mode = "any", slaHours = (int?)null, escalation = (object?)null, onApprove = "complete", onReject = "reject" };
        var wf = await Call(HttpMethod.Post, "/api/v1/workflows", admin, new { name = "Admin review", definition = new { version = 1, start = "review", autoPublish = false, distinctApprovers = false, steps = new[] { step } } });
        Assert.Equal(HttpStatusCode.Created, wf.Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Patch, $"/api/v1/folders/{folder}", admin, new { defaultWorkflowId = Id(wf.Body, "id") })).Status);
        await using var receiver = await TestReceiver.StartAsync();
        await NewChannel(admin, receiver.Url, "slack", "Approvals", ["workflow.*"]);
        using var worker = NotifyHosts.Worker(Pg, mq);
        await worker.StartAsync();

        var asset = await NewAsset(admin, folder, title: "Needs sign-off");
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/transitions/submit", admin, new { reason = (string?)null })).Status);
        var created = await receiver.WaitForAsync();
        Assert.StartsWith("*Approval needed: Needs sign-off*\nStep: Review", TextOf(created));

        var task = (await Call(HttpMethod.Get, "/api/v1/tasks", admin)).Body.GetProperty("items")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/tasks/{task}/decision", admin, new { decision = "reject", comment = "Not yet" })).Status);
        var decided = await receiver.WaitForAsync(h => TextOf(h).Contains("Approval decided", StringComparison.Ordinal));
        Assert.Contains("Rejected: Not yet", TextOf(decided));
    }

    [Fact]
    public async Task A_chat_service_that_keeps_failing_ends_in_the_dead_letter_queue_without_posting_twice_elsewhere()
    {
        var (_, admin, folder) = await TenantWithPacks();
        await using var broken = await TestReceiver.StartAsync();
        await using var healthy = await TestReceiver.StartAsync();
        broken.Respond = _ => 500;
        await NewChannel(admin, broken.Url, "slack", "Broken", ["asset.created"]);
        await NewChannel(admin, healthy.Url, "slack", "Healthy", ["asset.created"]);
        using var worker = NotifyHosts.Worker(Pg, mq, maxAttempts: 3, baseDelayMs: 100);
        await worker.StartAsync();
        await NewAsset(admin, folder, title: "Chat failure");

        await healthy.WaitForAsync();
        await NotifyHosts.Eventually(() => Task.FromResult(broken.Hits.Count), c => c >= 3);
        await Task.Delay(1000);
        Assert.Equal(3, broken.Hits.Count);    // tried on every attempt, then parked
        Assert.Single(healthy.Hits);           // the healthy channel was delivered once and recorded: the retries skipped it
    }
}
