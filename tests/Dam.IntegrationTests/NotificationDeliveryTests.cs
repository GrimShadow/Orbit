using System.Net;
using System.Text;
using System.Text.Json;
using Dam.Domain.Webhooks;
using Dam.Infrastructure.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;

namespace Dam.IntegrationTests;

/// <summary>Notifications end to end (ADR-0031): who is told, through which channel, once, respecting preferences.</summary>
[Collection("pg")]
public sealed class NotificationDeliveryTests(PostgresFixture pg, RabbitFixture mq) : AssetTestBase(pg), IClassFixture<RabbitFixture>
{
    private const string Inbox = "/api/v1/notifications";

    // ---- scene -------------------------------------------------------------------------------------------------

    private sealed record Person(string Token, Guid Id, string Email);

    private sealed class Scene
    {
        public Guid Tenant { get; init; }
        public string Admin { get; init; } = "";
        public Guid Folder { get; init; }
        public string Path { get; init; } = "";
        public Guid Workflow { get; init; }
        public required Person Owner { get; init; }       // creates the assets (Librarian)
        public required Person Submitter { get; init; }   // submits them (Editor)
        public required Person Reviewer { get; init; }    // Approver with a rule for the folder: can decide
        public required Person Outsider { get; init; }    // Approver, rule for a different folder: cannot decide here
        public required Person Roleless { get; init; }    // Approver, no rule at all
        public required Person Partial { get; init; }     // Approver, may approve but lacks workflow.tasks.decide
    }

    private static Dictionary<string, object?> Step(string id, string[] roles, Guid[]? users = null, int? sla = null, string[]? escalate = null) => new()
    {
        ["id"] = id, ["name"] = char.ToUpperInvariant(id[0]) + id[1..], ["assignees"] = new { roles, users = users ?? [] }, ["mode"] = "any", ["slaHours"] = sla,
        ["escalation"] = escalate is null ? null : new { to = new { roles = escalate, users = Array.Empty<Guid>() } }, ["onApprove"] = "complete", ["onReject"] = "reject",
    };

    private async Task<Person> Provision(Guid tenant, string role, string name)
    {
        var token = ApiTestHost.TokenFor(tenant, Guid.NewGuid(), [role], name: name);
        var me = (await Call(HttpMethod.Get, "/api/v1/me", token)).Body;
        return new Person(token, me.GetProperty("userId").GetGuid(), me.GetProperty("email").GetString()!);
    }

    private async Task Allow(string admin, Person p, string path, params string[] permissions) =>
        Assert.Equal(HttpStatusCode.Created, (await Call(HttpMethod.Post, "/api/v1/access-rules", admin, new
            { principalType = "user", principalId = p.Id, scopeType = "folder", scopeValue = path, permissions })).Status);

    private async Task<Scene> NewScene(Func<Person, Person, object>? definition = null, int? sla = 2)
    {
        var (t, admin, folder) = await TenantWithPacks();
        var path = (await Call(HttpMethod.Get, $"/api/v1/folders/{folder}", admin)).Body.GetProperty("folder").GetProperty("path").GetString()!;
        var other = Id((await Call(HttpMethod.Post, "/api/v1/folders", admin, new { name = "Elsewhere" })).Body, "folder", "id");
        var otherPath = (await Call(HttpMethod.Get, $"/api/v1/folders/{other}", admin)).Body.GetProperty("folder").GetProperty("path").GetString()!;
        await Call(HttpMethod.Get, "/api/v1/me", admin);   // the admin is a known user too

        var reviewer = await Provision(t, "Approver", "Rita Reviewer");
        var outsider = await Provision(t, "Approver", "Otto Outsider");
        var roleless = await Provision(t, "Approver", "Rae Roleless");
        var partial = await Provision(t, "Approver", "Pat Partial");
        await Allow(admin, reviewer, path, "assets.approve", "workflow.tasks.decide", "assets.read", "assets.read.in_review");
        await Allow(admin, outsider, otherPath, "assets.approve", "workflow.tasks.decide", "assets.read", "assets.read.in_review");
        await Allow(admin, partial, path, "assets.approve", "assets.read", "assets.read.in_review");

        var def = definition?.Invoke(reviewer, partial) ?? new { version = 1, start = "review", autoPublish = false, distinctApprovers = true, steps = new[] { Step("review", ["Approver"], sla: sla, escalate: ["Admin"]) } };
        var wf = await Call(HttpMethod.Post, "/api/v1/workflows", admin, new { name = "Notify flow", definition = def });
        Assert.Equal(HttpStatusCode.Created, wf.Status);
        var workflow = Id(wf.Body, "id");
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Patch, $"/api/v1/folders/{folder}", admin, new { defaultWorkflowId = workflow })).Status);

        return new Scene
        {
            Tenant = t, Admin = admin, Folder = folder, Path = path, Workflow = workflow,
            Owner = await Provision(t, "Librarian", "Olive Owner"), Submitter = await Provision(t, "Editor", "Sam Submitter"),
            Reviewer = reviewer, Outsider = outsider, Roleless = roleless, Partial = partial,
        };
    }

    private async Task<Guid> Submit(Scene s, string title)
    {
        var asset = await NewAsset(s.Owner.Token, s.Folder, title: title);
        var r = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/transitions/submit", s.Submitter.Token, new { reason = (string?)null });
        Assert.True(r.Status == HttpStatusCode.OK, r.Body.ToString());
        return asset;
    }

    private async Task<List<JsonElement>> Notes(Person p, string query = "")
    {
        var r = await Call(HttpMethod.Get, Inbox + query, p.Token);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        return r.Body.GetProperty("items").EnumerateArray().ToList();
    }

    private Task<List<JsonElement>> WaitForNotes(Person p, int count, string query = "") =>
        NotifyHosts.Eventually(() => Notes(p, query), n => n.Count >= count);

    private async Task<int> Unread(Person p) => (await Call(HttpMethod.Get, $"{Inbox}/unread-count", p.Token)).Body.GetProperty("count").GetInt32();

    private async Task<Guid> TaskOf(Person reviewer, Guid asset) =>
        (await Call(HttpMethod.Get, "/api/v1/tasks", reviewer.Token)).Body.GetProperty("items").EnumerateArray().Single(t => t.GetProperty("assetId").GetGuid() == asset).GetProperty("id").GetGuid();

    private static async Task Settle() => await Task.Delay(1200);

    /// <summary>The mail sent to this scene's people. Leftover messages from other tests can reach a later test's worker, so counts are per scene.</summary>
    private static List<Dam.Application.Abstractions.EmailMessage> MailOf(FakeEmailSender sender, Scene s)
    {
        var mine = new[] { s.Owner, s.Submitter, s.Reviewer, s.Outsider, s.Roleless, s.Partial }.Select(p => p.Email).ToHashSet();
        return sender.Sent.Where(m => mine.Contains(m.ToAddress)).ToList();
    }

    // ---- who is told -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_approval_task_notifies_exactly_the_people_who_could_decide_it()
    {
        var s = await NewScene();
        var email = new FakeEmailSender();
        using var worker = NotifyHosts.Worker(Pg, mq, configure: svc => svc.AddSingleton<Dam.Application.Abstractions.IEmailSender>(email));
        await worker.StartAsync();

        var asset = await Submit(s, "Spring hero");
        var mine = await WaitForNotes(s.Reviewer, 1);
        await Settle();

        var n = Assert.Single(mine);
        Assert.Equal(("workflow.task.created", "workflow_task", "/tasks", asset), (n.GetProperty("type").GetString(), n.GetProperty("category").GetString(), n.GetProperty("linkUrl").GetString(), n.GetProperty("assetId").GetGuid()));
        Assert.Contains("Spring hero", n.GetProperty("title").GetString());
        Assert.Contains("'Review' step of 'Notify flow'", n.GetProperty("body").GetString());
        Assert.False(n.GetProperty("read").GetBoolean());
        Assert.Equal(JsonValueKind.Null, n.GetProperty("readAt").ValueKind);

        // The Approver whose rule covers another folder, the one with no rule, the one who may approve but not decide, the admin (not an
        // assignee), the owner and the submitter: nobody else is told, and nobody gets mail.
        foreach (var p in new[] { s.Outsider, s.Roleless, s.Partial, s.Owner, s.Submitter })
            Assert.Empty(await Notes(p));
        Assert.Empty(await Notes(new Person(s.Admin, Guid.Empty, "")));
        Assert.Equal(1, await Unread(s.Reviewer));
        var mail = Assert.Single(MailOf(email, s));
        Assert.Equal(s.Reviewer.Email, mail.ToAddress);
        Assert.Equal(1, Count(s.Tenant, "SELECT count(*) FROM notifications"));
    }

    [Fact]
    public async Task People_named_directly_are_told_if_they_can_decide_and_not_otherwise()
    {
        // Rita (may decide here) and Pat (may approve but not decide) are named by id; no role is involved.
        var s = await NewScene((rita, pat) => new { version = 1, start = "review", autoPublish = false, distinctApprovers = true, steps = new[] { Step("review", [], users: [rita.Id, pat.Id]) } });
        using var worker = NotifyHosts.Worker(Pg, mq);
        await worker.StartAsync();
        await Submit(s, "Named people");
        await WaitForNotes(s.Reviewer, 1);
        await Settle();
        Assert.Single(await Notes(s.Reviewer));
        Assert.Empty(await Notes(s.Partial));   // named, but may not decide here
        Assert.Empty(await Notes(s.Roleless));
    }

    [Fact]
    public async Task A_decision_notifies_the_submitter_and_the_owner_with_the_outcome_and_never_the_person_who_decided()
    {
        var s = await NewScene();
        var email = new FakeEmailSender();
        using var worker = NotifyHosts.Worker(Pg, mq, configure: svc => svc.AddSingleton<Dam.Application.Abstractions.IEmailSender>(email));
        await worker.StartAsync();

        var approved = await Submit(s, "Approved asset");
        await WaitForNotes(s.Reviewer, 1);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/tasks/{await TaskOf(s.Reviewer, approved)}/decision", s.Reviewer.Token, new { decision = "approve", comment = "Great" })).Status);
        var rejected = await Submit(s, "Rejected asset");
        await WaitForNotes(s.Reviewer, 2);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/tasks/{await TaskOf(s.Reviewer, rejected)}/decision", s.Reviewer.Token, new { decision = "reject", comment = "Wrong logo" })).Status);

        foreach (var who in new[] { s.Submitter, s.Owner })
        {
            var notes = await WaitForNotes(who, 2);
            Assert.All(notes, n => Assert.Equal("workflow_outcome", n.GetProperty("category").GetString()));
            var ok = notes.Single(n => n.GetProperty("assetId").GetGuid() == approved);
            Assert.StartsWith("Approved:", ok.GetProperty("title").GetString());
            Assert.Contains("approved by Rita Reviewer at the 'Review' step", ok.GetProperty("body").GetString());
            Assert.Equal($"/library/assets/{approved}", ok.GetProperty("linkUrl").GetString());
            var no = notes.Single(n => n.GetProperty("assetId").GetGuid() == rejected);
            Assert.StartsWith("Rejected:", no.GetProperty("title").GetString());
            Assert.Contains("rejected by Rita Reviewer at the 'Review' step: Wrong logo", no.GetProperty("body").GetString());
        }
        await Settle();
        Assert.Equal(2, (await Notes(s.Reviewer)).Count);   // only the two "approval needed" ones: the decider is not told of their own decision
        Assert.All(await Notes(s.Reviewer), n => Assert.Equal("workflow.task.created", n.GetProperty("type").GetString()));
        // Outcomes are in-app only by default; the two approval-needed mails are the only mail.
        Assert.Equal(2, MailOf(email, s).Count);
        Assert.All(MailOf(email, s), m => Assert.Equal(s.Reviewer.Email, m.ToAddress));
    }

    [Fact]
    public async Task An_overdue_task_notifies_the_assignees_and_the_escalation_targets()
    {
        var s = await NewScene(sla: 2);
        var email = new FakeEmailSender();
        using var worker = NotifyHosts.Worker(Pg, mq, configure: svc => svc.AddSingleton<Dam.Application.Abstractions.IEmailSender>(email));
        await worker.StartAsync();
        var asset = await Submit(s, "Late asset");
        await WaitForNotes(s.Reviewer, 1);

        var due = (await Call(HttpMethod.Get, "/api/v1/tasks", s.Reviewer.Token)).Body.GetProperty("items")[0].GetProperty("dueAt").GetDateTimeOffset();
        using var scheduler = NotifyHosts.Scheduler(Pg, new FakeClock(due.AddMinutes(5)));
        await NotifyHosts.RunJobAsync(scheduler, WorkflowSlaJob.JobKey);

        var overdue = (await WaitForNotes(s.Reviewer, 2)).Single(n => n.GetProperty("type").GetString() == "workflow.sla.breached");
        Assert.Equal(("sla", "/tasks", asset), (overdue.GetProperty("category").GetString(), overdue.GetProperty("linkUrl").GetString(), overdue.GetProperty("assetId").GetGuid()));
        Assert.StartsWith("Overdue:", overdue.GetProperty("title").GetString());

        var adminPerson = new Person(s.Admin, Guid.Empty, "");   // an escalation target: told because they can now decide it
        var adminNotes = await WaitForNotes(adminPerson, 1);
        Assert.Equal("workflow.sla.breached", Assert.Single(adminNotes).GetProperty("type").GetString());
        await Settle();
        foreach (var p in new[] { s.Outsider, s.Roleless, s.Partial, s.Submitter, s.Owner }) Assert.Empty(await Notes(p));
        // Approval needed + overdue for the reviewer, overdue for the admin (overdue mail is on by default). Other tests' tenants share this
        // database and the fake clock is far ahead of their deadlines too, so only this scene's addresses are counted.
        var adminEmail = (await Call(HttpMethod.Get, "/api/v1/me", s.Admin)).Body.GetProperty("email").GetString();
        var mine = email.Sent.Where(m => m.ToAddress == s.Reviewer.Email || m.ToAddress == adminEmail).ToList();
        Assert.Equal(3, mine.Count);
        Assert.Equal(2, mine.Count(m => m.ToAddress == s.Reviewer.Email));
        Assert.DoesNotContain(email.Sent, m => new[] { s.Outsider.Email, s.Roleless.Email, s.Partial.Email, s.Owner.Email, s.Submitter.Email }.Contains(m.ToAddress));
    }

    [Fact]
    public async Task An_asset_that_expires_or_is_archived_notifies_its_owner_and_nothing_else_does()
    {
        var s = await NewScene();
        using var worker = NotifyHosts.Worker(Pg, mq);
        await worker.StartAsync();
        var asset = await NewAsset(s.Owner.Token, null, title: "Seasonal banner");   // no folder: no workflow applies
        async Task Move(string action) => Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/transitions/{action}", s.Admin, new { reason = "Licence ended" })).Status);

        foreach (var action in new[] { "submit", "approve", "publish" }) await Move(action);
        await Settle();
        Assert.Empty(await Notes(s.Owner));   // draft -> published are not worth a notification

        await Move("expire");
        var first = Assert.Single(await WaitForNotes(s.Owner, 1));
        Assert.Equal(("asset.status.changed", "asset_lifecycle"), (first.GetProperty("type").GetString(), first.GetProperty("category").GetString()));
        Assert.StartsWith("Expired:", first.GetProperty("title").GetString());
        await Move("archive");
        var both = await WaitForNotes(s.Owner, 2);
        Assert.Contains(both, n => n.GetProperty("title").GetString()!.StartsWith("Archived:", StringComparison.Ordinal));
        await Settle();
        Assert.Empty(await Notes(s.Submitter));
        Assert.Empty(await Notes(s.Reviewer));
    }

    // ---- preferences -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Preferences_are_listed_with_defaults_validated_stored_per_person_and_reported_back()
    {
        var t = await NewTenantAsync();
        var a = await Provision(t, "Editor", "Ann"); var b = await Provision(t, "Editor", "Bea");

        var r = await Call(HttpMethod.Get, $"{Inbox}/preferences", a.Token);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        var cats = r.Body.GetProperty("categories").EnumerateArray().ToList();
        Assert.Equal(["workflow_task", "workflow_outcome", "sla", "asset_lifecycle"], cats.Select(c => c.GetProperty("key").GetString()));
        Assert.All(cats, c => Assert.False(string.IsNullOrWhiteSpace(c.GetProperty("label").GetString())));
        Assert.Equal([true, false, true, false], cats.Select(c => c.GetProperty("email").GetBoolean()));
        Assert.Equal(cats.Select(c => c.GetProperty("defaultEmail").GetBoolean()), cats.Select(c => c.GetProperty("email").GetBoolean()));
        Assert.False(r.Body.GetProperty("emailAvailable").GetBoolean());   // the test API host has no SMTP

        var put = await Call(HttpMethod.Put, $"{Inbox}/preferences", a.Token, new { preferences = new[] { new { category = "sla", inApp = false, email = false }, new { category = "workflow_outcome", inApp = true, email = true } } });
        Assert.Equal(HttpStatusCode.OK, put.Status);
        var after = put.Body.GetProperty("categories").EnumerateArray().ToDictionary(c => c.GetProperty("key").GetString()!);
        Assert.Equal((false, false), (after["sla"].GetProperty("inApp").GetBoolean(), after["sla"].GetProperty("email").GetBoolean()));
        Assert.Equal((true, true), (after["workflow_outcome"].GetProperty("inApp").GetBoolean(), after["workflow_outcome"].GetProperty("email").GetBoolean()));
        Assert.Equal((true, true), (after["workflow_task"].GetProperty("inApp").GetBoolean(), after["workflow_task"].GetProperty("email").GetBoolean()));   // untouched
        Assert.True(after["sla"].GetProperty("defaultEmail").GetBoolean());   // the defaults stay visible

        // Saved, and only for her.
        Assert.False((await Call(HttpMethod.Get, $"{Inbox}/preferences", a.Token)).Body.GetProperty("categories").EnumerateArray().Single(c => c.GetProperty("key").GetString() == "sla").GetProperty("inApp").GetBoolean());
        Assert.True((await Call(HttpMethod.Get, $"{Inbox}/preferences", b.Token)).Body.GetProperty("categories").EnumerateArray().Single(c => c.GetProperty("key").GetString() == "sla").GetProperty("inApp").GetBoolean());

        // Setting again updates the same row; an unknown category is refused and changes nothing.
        await Call(HttpMethod.Put, $"{Inbox}/preferences", a.Token, new { preferences = new[] { new { category = "sla", inApp = true, email = false } } });
        Assert.Equal(2, Count(t, "SELECT count(*) FROM notification_preferences"));
        var bad = await Call(HttpMethod.Put, $"{Inbox}/preferences", a.Token, new { preferences = new[] { new { category = "marketing", inApp = true, email = true } } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.Status);
        Assert.Equal(2, Count(t, "SELECT count(*) FROM notification_preferences"));
    }

    [Fact]
    public async Task Switching_a_channel_off_is_respected_for_in_app_and_for_email()
    {
        var s = await NewScene();
        var email = new FakeEmailSender();
        using var worker = NotifyHosts.Worker(Pg, mq, configure: svc => svc.AddSingleton<Dam.Application.Abstractions.IEmailSender>(email));
        await worker.StartAsync();
        await Call(HttpMethod.Put, $"{Inbox}/preferences", s.Reviewer.Token, new { preferences = new[] { new { category = "workflow_task", inApp = false, email = true } } });

        await Submit(s, "Email only");
        var mail = await NotifyHosts.Eventually(() => Task.FromResult(MailOf(email, s)), m => m.Count >= 1);
        Assert.Equal(s.Reviewer.Email, Assert.Single(mail).ToAddress);
        await Settle();
        Assert.Empty(await Notes(s.Reviewer));   // in-app off

        await Call(HttpMethod.Put, $"{Inbox}/preferences", s.Reviewer.Token, new { preferences = new[] { new { category = "workflow_task", inApp = true, email = false } } });
        await Submit(s, "In-app only");
        Assert.Single(await WaitForNotes(s.Reviewer, 1));
        await Settle();
        Assert.Single(MailOf(email, s));   // email off: no second mail

        await Call(HttpMethod.Put, $"{Inbox}/preferences", s.Reviewer.Token, new { preferences = new[] { new { category = "workflow_task", inApp = false, email = false } } });
        await Submit(s, "Nothing at all");
        await Settle();
        Assert.Single(await Notes(s.Reviewer));
        Assert.Single(MailOf(email, s));
    }

    // ---- once only ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_redelivered_event_never_notifies_anyone_twice_even_under_a_different_message_id()
    {
        var s = await NewScene();
        var email = new FakeEmailSender();
        using var worker = NotifyHosts.Worker(Pg, mq, configure: svc => svc.AddSingleton<Dam.Application.Abstractions.IEmailSender>(email));
        await worker.StartAsync();
        await Submit(s, "Once");
        await WaitForNotes(s.Reviewer, 1);

        string payload; Guid messageId;
        using (var c = Pg.RawAppConnection(s.Tenant))
        using (var cmd = new Npgsql.NpgsqlCommand("SELECT id, payload::text FROM outbox WHERE event_type = 'workflow.task.created'", c))
        using (var r = cmd.ExecuteReader()) { r.Read(); messageId = r.GetGuid(0); payload = r.GetString(1); }
        var conn = await new ConnectionFactory { Uri = new Uri(mq.Url) }.CreateConnectionAsync();
        await using var ch = await conn.CreateChannelAsync();
        // The same message again (host-level de-duplication), and the same event under a fresh message id (ledger-level de-duplication).
        foreach (var id in new[] { messageId, messageId, Guid.NewGuid(), Guid.NewGuid() })
            await ch.BasicPublishAsync("dam.events", "workflow.task.created", false, new BasicProperties
            {
                MessageId = id.ToString(), Type = "workflow.task.created",
                Headers = new Dictionary<string, object?> { ["tenant_id"] = Encoding.UTF8.GetBytes(s.Tenant.ToString()) },
            }, Encoding.UTF8.GetBytes(payload));
        await Task.Delay(2000);
        await conn.CloseAsync();

        Assert.Single(await Notes(s.Reviewer));
        Assert.Single(MailOf(email, s));
        Assert.Equal(2, Count(s.Tenant, "SELECT count(*) FROM notification_deliveries"));   // one in-app, one email
    }

    [Fact]
    public async Task A_mail_server_outage_does_not_cost_anyone_their_in_app_notification_and_the_mail_goes_out_once_when_it_recovers()
    {
        var s = await NewScene();
        var email = new FakeEmailSender { Fail = true };
        using var worker = NotifyHosts.Worker(Pg, mq, configure: svc => svc.AddSingleton<Dam.Application.Abstractions.IEmailSender>(email), maxAttempts: 6, baseDelayMs: 300);
        await worker.StartAsync();
        await Submit(s, "Outage");

        Assert.Single(await WaitForNotes(s.Reviewer, 1));   // in-app does not wait for mail
        await Task.Delay(500);
        Assert.Empty(MailOf(email, s));
        email.Fail = false;                                 // recovers before the retries run out

        var sent = await NotifyHosts.Eventually(() => Task.FromResult(MailOf(email, s)), m => m.Count >= 1, seconds: 20);
        Assert.Equal(s.Reviewer.Email, Assert.Single(sent).ToAddress);
        await Settle();
        Assert.Single(MailOf(email, s));
        Assert.Single(await Notes(s.Reviewer));
    }

    [Fact]
    public async Task A_message_that_cannot_be_delivered_ends_in_the_dead_letter_queue_after_the_retries()
    {
        var s = await NewScene();
        var email = new FakeEmailSender { Fail = true };
        using var worker = NotifyHosts.Worker(Pg, mq, configure: svc => svc.AddSingleton<Dam.Application.Abstractions.IEmailSender>(email), maxAttempts: 3, baseDelayMs: 100);
        await worker.StartAsync();
        await Submit(s, "Doomed mail");

        var conn = await new ConnectionFactory { Uri = new Uri(mq.Url) }.CreateConnectionAsync();
        await using var ch = await conn.CreateChannelAsync();
        BasicGetResult? parked = null;
        for (var i = 0; i < 100 && parked is null; i++) { parked = await ch.BasicGetAsync("dam.q.notifications.dlq", autoAck: true); if (parked is null) await Task.Delay(200); }
        Assert.NotNull(parked);
        Assert.Contains("SMTP is down", Encoding.UTF8.GetString((byte[])parked!.BasicProperties.Headers!["x-error"]!));
        Assert.Single(await Notes(s.Reviewer));   // still told in the console
        await conn.CloseAsync();
    }

    // ---- email content -----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_email_carries_the_tenant_name_an_absolute_link_and_both_a_text_and_an_html_part()
    {
        var s = await NewScene();
        var email = new FakeEmailSender();
        using var worker = NotifyHosts.Worker(Pg, mq, configure: svc => svc.AddSingleton<Dam.Application.Abstractions.IEmailSender>(email));
        await worker.StartAsync();
        await Submit(s, "<b>Bold</b> & brash");

        var mail = (await NotifyHosts.Eventually(() => Task.FromResult(MailOf(email, s)), m => m.Count >= 1)).Single();
        var tenantName = (await Call(HttpMethod.Get, "/api/v1/tenants/current", s.Admin)).Body.GetProperty("name").GetString()!;
        Assert.StartsWith($"[{tenantName}] Approval needed:", mail.Subject);
        Assert.Contains("https://orbit.example.test/tasks", mail.Text);
        Assert.Contains("href=\"https://orbit.example.test/tasks\"", mail.Html);
        Assert.Contains("&lt;b&gt;Bold&lt;/b&gt; &amp; brash", mail.Html);
        Assert.DoesNotContain("<b>Bold</b>", mail.Html);
        Assert.Contains("<b>Bold</b> & brash", mail.Text);
        Assert.Equal("Rita Reviewer", mail.ToName);
    }

    [Fact]
    public async Task Mail_goes_through_a_real_SMTP_conversation_as_multipart_alternative()
    {
        var s = await NewScene();
        await using var smtp = new TinySmtpServer();
        var settings = NotifyHosts.Settings(v => { v["DAM_SMTP_HOST"] = "127.0.0.1"; v["DAM_SMTP_PORT"] = smtp.Port.ToString(); v["DAM_SMTP_TLS"] = "none"; v["DAM_SMTP_FROM"] = "Orbit <orbit@acme.test>"; });
        using var worker = NotifyHosts.Worker(Pg, mq, settings);
        await worker.StartAsync();
        await Submit(s, "Over the wire");

        var message = await NotifyHosts.Eventually(() => Task.FromResult(smtp.Messages.Where(m => m.To.Contains(s.Reviewer.Email)).ToList()), m => m.Count >= 1);
        var (from, to, data) = Assert.Single(message);
        Assert.Contains("orbit@acme.test", from);
        Assert.Contains(s.Reviewer.Email, to);
        Assert.Contains("multipart/alternative", data);
        Assert.Contains("text/plain", data);
        Assert.Contains("text/html", data);
        Assert.Contains("Subject: [", data);
        Assert.Contains("Approval needed: Over the wire", data.Replace("=\r\n", "").Replace("=\n", ""));
        Assert.Contains("From: Orbit <orbit@acme.test>", data);
    }

    // ---- the inbox ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_inbox_pages_filters_and_marks_read_for_the_caller_only()
    {
        var s = await NewScene();
        using var worker = NotifyHosts.Worker(Pg, mq);
        await worker.StartAsync();
        await Allow(s.Admin, s.Outsider, s.Path, "assets.approve", "workflow.tasks.decide", "assets.read", "assets.read.in_review");   // a second person who gets the same tasks
        for (var i = 0; i < 5; i++) await Submit(s, "Inbox " + i);
        var mine = await WaitForNotes(s.Reviewer, 5);
        var theirs = await WaitForNotes(s.Outsider, 5);

        // Newest first, paged by cursor.
        Assert.Equal(mine.Select(n => n.GetProperty("id").GetGuid()).OrderByDescending(x => x), mine.Select(n => n.GetProperty("id").GetGuid()));
        Assert.Contains("Inbox 4", mine[0].GetProperty("title").GetString());
        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = (await Call(HttpMethod.Get, $"{Inbox}?limit=2" + (cursor is null ? "" : $"&cursor={cursor}"), s.Reviewer.Token)).Body;
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
            cursor = page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextCursor").GetString();
        } while (cursor is not null);
        Assert.Equal(mine.Select(n => n.GetProperty("id").GetGuid()), seen);

        // Marking one read is the caller's alone; someone else's id looks like one that does not exist.
        var one = mine[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"{Inbox}/{one}/read", s.Outsider.Token)).Status);
        Assert.Equal(5, await Unread(s.Reviewer));
        var read = await Call(HttpMethod.Post, $"{Inbox}/{one}/read", s.Reviewer.Token);
        Assert.Equal(HttpStatusCode.OK, read.Status);
        Assert.True(read.Body.GetProperty("read").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, read.Body.GetProperty("readAt").ValueKind);
        Assert.Equal(4, await Unread(s.Reviewer));
        Assert.Equal(5, await Unread(s.Outsider));
        Assert.Equal(4, (await Notes(s.Reviewer, "?unread=true")).Count);
        Assert.Equal(5, (await Notes(s.Reviewer)).Count);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"{Inbox}/{one}/read", s.Reviewer.Token)).Status);   // idempotent
        Assert.Equal(4, await Unread(s.Reviewer));
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"{Inbox}/{Guid.NewGuid()}/read", s.Reviewer.Token)).Status);

        // Read-all is the caller's alone too.
        var all = await Call(HttpMethod.Post, $"{Inbox}/read-all", s.Reviewer.Token);
        Assert.Equal(4, all.Body.GetProperty("marked").GetInt32());
        Assert.Equal(0, await Unread(s.Reviewer));
        Assert.Equal(5, await Unread(s.Outsider));
        Assert.Empty(await Notes(s.Reviewer, "?unread=true"));
        Assert.Equal(0, (await Call(HttpMethod.Post, $"{Inbox}/read-all", s.Reviewer.Token)).Body.GetProperty("marked").GetInt32());
        Assert.Equal(5, theirs.Count);
        Assert.Equal(5, (await Notes(s.Outsider, "?unread=true")).Count);
    }

    [Fact]
    public async Task Any_signed_in_person_has_an_inbox_and_the_inbox_cannot_be_read_unauthenticated()
    {
        var t = await NewTenantAsync();
        foreach (var role in new[] { "Viewer", "Dealer", "Contributor", "Editor" })
        {
            var token = ApiTestHost.TokenFor(t, Guid.NewGuid(), [role], attributes: new() { ["region"] = ["north"] });
            Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, Inbox, token)).Status);
            Assert.Equal(0, (await Call(HttpMethod.Get, $"{Inbox}/unread-count", token)).Body.GetProperty("count").GetInt32());
            Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, $"{Inbox}/preferences", token)).Status);
        }
        foreach (var path in new[] { Inbox, $"{Inbox}/unread-count", $"{Inbox}/preferences" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await Call(HttpMethod.Get, path, "")).Status);
    }

    // ---- isolation ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task One_tenants_events_never_notify_another_tenants_people_and_the_new_tables_are_row_secured()
    {
        var a = await NewScene();
        var b = await NewScene();
        using var worker = NotifyHosts.Worker(Pg, mq);
        await worker.StartAsync();
        await Submit(a, "Tenant A work");
        await WaitForNotes(a.Reviewer, 1);
        await Settle();

        Assert.Empty(await Notes(b.Reviewer));
        Assert.Equal(0, await Unread(b.Reviewer));
        Assert.Equal(0, Count(b.Tenant, "SELECT count(*) FROM notifications"));
        foreach (var table in new[] { "notifications", "notification_preferences", "notification_channels", "notification_deliveries", "siem_cursors" })
        {
            Assert.Equal(0, Count(b.Tenant, $"SELECT count(*) FROM {table} WHERE tenant_id = '{a.Tenant}'"));
            using var none = Pg.RawAppConnection(null);
            using var cmd = new Npgsql.NpgsqlCommand($"SELECT count(*) FROM {table}", none);
            Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));
        }
        Assert.True(Count(a.Tenant, "SELECT count(*) FROM notifications") > 0);
        using var conn = Pg.RawAppConnection(b.Tenant);
        using var insert = new Npgsql.NpgsqlCommand($"INSERT INTO notifications (id, tenant_id, user_id, type, category, title, body, event_id, created_at) VALUES (gen_random_uuid(), '{a.Tenant}', '{a.Reviewer.Id}', 't', 'sla', 'x', 'y', gen_random_uuid(), now())", conn);
        Assert.Throws<Npgsql.PostgresException>(() => insert.ExecuteNonQuery());
    }

    [Fact]
    public async Task The_roles_in_a_persons_token_are_remembered_so_background_work_can_find_them()
    {
        var t = await NewTenantAsync();
        var token = ApiTestHost.TokenFor(t, Guid.NewGuid(), ["Approver", "Brand-Lead"]);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, "/api/v1/me", token)).Status);
        Assert.Equal("{approver,brand-lead}", Convert.ToString(ScalarText(t, "SELECT idp_roles::text FROM users LIMIT 1")));
    }

    private object? ScalarText(Guid tenant, string sql)
    {
        using var c = Pg.RawAppConnection(tenant);
        using var cmd = new Npgsql.NpgsqlCommand(sql, c);
        return cmd.ExecuteScalar();
    }
}
