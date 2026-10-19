using System.Net;
using System.Text;
using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Shared.Webhooks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;

namespace Dam.IntegrationTests;

/// <summary>What a comment sets off, end to end (ADR-0032): mentions by in-app notification and email, replies, resolution, webhooks; once, to the right people.</summary>
[Collection("pg")]
public sealed class CommentNotificationTests(PostgresFixture pg, RabbitFixture mq) : AssetTestBase(pg), IClassFixture<RabbitFixture>
{
    private const string Inbox = "/api/v1/notifications";

    private sealed record Person(string Token, Guid Id, string Email, string Name)
    {
        public string Mention => $"@[{Name}](user:{Id})";
    }

    private sealed record Scene(Guid Tenant, string Admin, Guid Folder, Guid Asset, Person Owner, Person Priya, Person Bob, Person Vera);

    private async Task<Person> Provision(Guid tenant, string role, string name)
    {
        var token = ApiTestHost.TokenFor(tenant, Guid.NewGuid(), [role], name: name);
        var me = (await Call(HttpMethod.Get, "/api/v1/me", token)).Body;
        return new Person(token, me.GetProperty("userId").GetGuid(), me.GetProperty("email").GetString()!, name);
    }

    /// <summary>A draft asset owned by Olive (a Librarian). Priya and Bob are Editors, who can read drafts; Vera is a Viewer, who cannot.</summary>
    private async Task<Scene> NewScene()
    {
        var (t, admin, folder) = await TenantWithPacks();
        var owner = await Provision(t, "Librarian", "Olive Owner");
        var asset = await NewAsset(owner.Token, folder, title: "Acme brochure");
        return new Scene(t, admin, folder, asset, owner, await Provision(t, "Editor", "Priya Patel"), await Provision(t, "Editor", "Bob Brooks"), await Provision(t, "Viewer", "Vera Viewer"));
    }

    private async Task<Guid> Say(Person who, Guid asset, string body, Guid? parent = null)
    {
        var r = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/comments", who.Token, new { body, parentId = parent });
        Assert.True(r.Status == HttpStatusCode.Created, r.Body.ToString());
        return r.Body.GetProperty("id").GetGuid();
    }

    private async Task<List<JsonElement>> Notes(Person p)
    {
        var r = await Call(HttpMethod.Get, Inbox, p.Token);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        return r.Body.GetProperty("items").EnumerateArray().ToList();
    }

    private static Task<List<JsonElement>> Wait(Func<Task<List<JsonElement>>> notes, int count) => NotifyHosts.Eventually(notes, n => n.Count >= count);

    private static async Task Settle() => await Task.Delay(1200);

    private static List<EmailMessage> MailTo(FakeEmailSender sender, params Person[] people)
    {
        var addresses = people.Select(p => p.Email).ToHashSet();
        return sender.Sent.Where(m => addresses.Contains(m.ToAddress)).ToList();
    }

    private IHost StartWorker(FakeEmailSender email) => NotifyHosts.Worker(Pg, mq, configure: svc => svc.AddSingleton<IEmailSender>(email));

    private static string Title(JsonElement n) => n.GetProperty("title").GetString()!;
    private static string Category(JsonElement n) => n.GetProperty("category").GetString()!;

    // ---- mentions ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_mention_notifies_the_person_in_the_console_and_by_email_with_a_link_to_the_comment()
    {
        var s = await NewScene();
        var email = new FakeEmailSender();
        using var worker = StartWorker(email);
        await worker.StartAsync();

        var comment = await Say(s.Priya, s.Asset, $"Hi {s.Bob.Mention}, can you check the logo?");

        var bobs = await Wait(() => Notes(s.Bob), 1);
        await Settle();
        var n = Assert.Single(await Notes(s.Bob));
        Assert.Equal(("comment.added", "mention", "Priya Patel mentioned you on 'Acme brochure'"), (n.GetProperty("type").GetString(), Category(n), Title(n)));
        Assert.Equal("Hi @Bob Brooks, can you check the logo?", n.GetProperty("body").GetString());   // the token is shown as a name
        Assert.Equal($"/library/assets/{s.Asset}?comment={comment}", n.GetProperty("linkUrl").GetString());
        Assert.Equal(s.Asset, n.GetProperty("assetId").GetGuid());
        Assert.False(n.GetProperty("read").GetBoolean());
        Assert.Equal(1, (await Call(HttpMethod.Get, $"{Inbox}/unread-count", s.Bob.Token)).Body.GetProperty("count").GetInt32());
        Assert.NotEmpty(bobs);

        // ...and mail, with an HTML and a text part, an absolute link, and the person's name escaped.
        var mail = Assert.Single(MailTo(email, s.Bob));
        var tenantName = (await Call(HttpMethod.Get, "/api/v1/tenants/current", s.Admin)).Body.GetProperty("name").GetString();
        Assert.Equal($"[{tenantName}] Priya Patel mentioned you on 'Acme brochure'", mail.Subject);
        var link = $"https://orbit.example.test/library/assets/{s.Asset}?comment={comment}";
        Assert.Contains(link, mail.Text);
        Assert.Contains($"href=\"{link}\"", mail.Html);
        Assert.Contains("Hi @Bob Brooks, can you check the logo?", mail.Text);
        Assert.Equal("Bob Brooks", mail.ToName);
        Assert.DoesNotContain("user:", mail.Text);   // no token syntax reaches a person

        // Nobody else mentioned or concerned is told: not the author, not the viewer who cannot see a draft.
        Assert.Empty(await Notes(s.Priya));
        Assert.Empty(await Notes(s.Vera));
        Assert.Empty(MailTo(email, s.Priya, s.Vera));
    }

    [Fact]
    public async Task Someone_who_cannot_see_the_asset_is_never_mentioned_or_told_even_if_the_token_names_them()
    {
        var s = await NewScene();
        var email = new FakeEmailSender();
        using var worker = StartWorker(email);
        await worker.StartAsync();

        var comment = await Say(s.Priya, s.Asset, $"{s.Vera.Mention} {s.Bob.Mention}");
        await Wait(() => Notes(s.Bob), 1);
        await Settle();
        Assert.Empty(await Notes(s.Vera));
        Assert.Empty(MailTo(email, s.Vera));
        Assert.Equal(0, Count(s.Tenant, $"SELECT count(*) FROM comment_mentions WHERE comment_id = '{comment}' AND user_id = '{s.Vera.Id}'"));
        Assert.Equal(0, Count(s.Tenant, $"SELECT count(*) FROM notifications WHERE user_id = '{s.Vera.Id}'"));
    }

    [Fact]
    public async Task A_mention_honours_the_persons_preferences_for_each_channel()
    {
        var s = await NewScene();
        var email = new FakeEmailSender();
        using var worker = StartWorker(email);
        await worker.StartAsync();
        async Task Prefer(bool inApp, bool mail) => Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, $"{Inbox}/preferences", s.Bob.Token, new { preferences = new[] { new { category = "mention", inApp, email = mail } } })).Status);

        await Prefer(inApp: true, mail: false);            // console only
        await Say(s.Priya, s.Asset, $"{s.Bob.Mention} one");
        await Wait(() => Notes(s.Bob), 1);
        await Settle();
        Assert.Single(await Notes(s.Bob));
        Assert.Empty(MailTo(email, s.Bob));

        await Prefer(inApp: false, mail: true);            // mail only
        await Say(s.Priya, s.Asset, $"{s.Bob.Mention} two");
        await NotifyHosts.Eventually(() => Task.FromResult(MailTo(email, s.Bob)), m => m.Count >= 1);
        await Settle();
        Assert.Single(await Notes(s.Bob));
        Assert.Single(MailTo(email, s.Bob));

        await Prefer(inApp: false, mail: false);           // nothing at all
        await Say(s.Priya, s.Asset, $"{s.Bob.Mention} three");
        await Settle();
        Assert.Single(await Notes(s.Bob));
        Assert.Single(MailTo(email, s.Bob));
    }

    // ---- comments and replies ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_top_level_comment_tells_the_asset_owner_in_the_console_only_by_default()
    {
        var s = await NewScene();
        var email = new FakeEmailSender();
        using var worker = StartWorker(email);
        await worker.StartAsync();

        var comment = await Say(s.Priya, s.Asset, "Looks great");
        await Wait(() => Notes(s.Owner), 1);
        await Settle();
        var n = Assert.Single(await Notes(s.Owner));
        Assert.Equal(("comments", "Priya Patel commented on 'Acme brochure'", "Looks great"), (Category(n), Title(n), n.GetProperty("body").GetString()));
        Assert.Equal($"/library/assets/{s.Asset}?comment={comment}", n.GetProperty("linkUrl").GetString());
        Assert.Empty(MailTo(email, s.Owner));            // comments: console on, mail off by default
        Assert.Empty(await Notes(s.Bob));                // not the owner, not mentioned
        Assert.Empty(await Notes(s.Priya));              // the author

        // An annotation is described as one.
        var annotated = await Call(HttpMethod.Post, $"/api/v1/assets/{s.Asset}/comments", s.Priya.Token, new { body = "Here", anchor = new { type = "region", x = 0.1, y = 0.1, w = 0.2, h = 0.2 } });
        Assert.Equal(HttpStatusCode.Created, annotated.Status);
        var both = await Wait(() => Notes(s.Owner), 2);
        Assert.Contains(both, x => Title(x) == "Priya Patel added an annotation to 'Acme brochure'");
    }

    [Fact]
    public async Task The_person_who_submitted_the_asset_is_told_of_a_new_comment_too()
    {
        var s = await NewScene();
        // A workflow on the folder records who submitted the asset (a submit with no workflow starts nothing to remember).
        var step = new Dictionary<string, object?>
        {
            ["id"] = "review", ["name"] = "Review", ["assignees"] = new { roles = new[] { "Approver" }, users = Array.Empty<Guid>() }, ["mode"] = "any", ["slaHours"] = null,
            ["escalation"] = null, ["onApprove"] = "complete", ["onReject"] = "reject",
        };
        var wf = await Call(HttpMethod.Post, "/api/v1/workflows", s.Admin, new { name = "Quick", definition = new { version = 1, start = "review", autoPublish = false, distinctApprovers = true, steps = new[] { step } } });
        Assert.Equal(HttpStatusCode.Created, wf.Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Patch, $"/api/v1/folders/{s.Folder}", s.Admin, new { defaultWorkflowId = wf.Body.GetProperty("id").GetGuid() })).Status);
        using var worker = StartWorker(new FakeEmailSender());
        await worker.StartAsync();
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/assets/{s.Asset}/transitions/submit", s.Bob.Token, new { reason = (string?)null })).Status);   // Bob submits Olive's asset

        await Say(s.Priya, s.Asset, "A thought on the submission");
        await Wait(() => Notes(s.Bob), 1);
        await Wait(() => Notes(s.Owner), 1);
        await Settle();
        Assert.Contains(await Notes(s.Bob), n => Category(n) == "comments" && Title(n).StartsWith("Priya Patel commented", StringComparison.Ordinal));
        Assert.Contains(await Notes(s.Owner), n => Category(n) == "comments");
    }

    [Fact]
    public async Task A_reply_tells_the_author_of_the_thread_and_not_the_owner()
    {
        var s = await NewScene();
        var email = new FakeEmailSender();
        using var worker = StartWorker(email);
        await worker.StartAsync();

        var root = await Say(s.Bob, s.Asset, "Which crop do we want?");
        await Wait(() => Notes(s.Owner), 1);
        var reply = await Say(s.Priya, s.Asset, "The square one", parent: root);

        var bobs = await Wait(() => Notes(s.Bob), 1);
        await Settle();
        var n = Assert.Single(bobs);
        Assert.Equal(("comments", "Priya Patel replied to your comment on 'Acme brochure'", "The square one"), (Category(n), Title(n), n.GetProperty("body").GetString()));
        Assert.Equal($"/library/assets/{s.Asset}?comment={reply}", n.GetProperty("linkUrl").GetString());
        Assert.Single(await Notes(s.Owner));                       // only about Bob's first comment; a reply is the thread's business
        Assert.Empty(MailTo(email, s.Bob));
        Assert.Empty(await Notes(s.Priya));
    }

    [Fact]
    public async Task One_notification_per_person_per_comment_and_a_mention_wins_over_a_reply_or_ownership()
    {
        var s = await NewScene();
        using var worker = StartWorker(new FakeEmailSender());
        await worker.StartAsync();

        // Priya replies to Bob's thread and mentions Bob in it: Bob hears once, as a mention.
        var root = await Say(s.Bob, s.Asset, "Question");
        await Wait(() => Notes(s.Owner), 1);
        await Say(s.Priya, s.Asset, $"{s.Bob.Mention} answered, {s.Bob.Mention} again", parent: root);
        await Wait(() => Notes(s.Bob), 1);

        // Priya comments and mentions the owner: the owner hears once, as a mention, not also as owner.
        await Say(s.Priya, s.Asset, $"{s.Owner.Mention} please look");
        await Wait(() => Notes(s.Owner), 2);
        await Settle();

        var bobs = await Notes(s.Bob);
        Assert.Equal("mention", Category(Assert.Single(bobs)));
        var owners = await Notes(s.Owner);
        Assert.Equal(2, owners.Count);                             // Bob's first comment (as owner), then Priya's (as mentioned)
        Assert.Equal(["comments", "mention"], owners.Select(Category).Order());
        Assert.Equal(1, Count(s.Tenant, $"SELECT count(*) FROM notifications WHERE user_id = '{s.Bob.Id}'"));
    }

    [Fact]
    public async Task The_author_is_never_told_about_their_own_comment_even_when_owner_or_mentioning_themselves()
    {
        var s = await NewScene();
        using var worker = StartWorker(new FakeEmailSender());
        await worker.StartAsync();

        var root = await Say(s.Owner, s.Asset, $"A note to self {s.Owner.Mention}");      // the owner comments on their own asset and mentions themselves
        await Say(s.Owner, s.Asset, "And a reply to myself", parent: root);
        var mine = await Say(s.Priya, s.Asset, "Priya's own thread");
        await Say(s.Priya, s.Asset, "Priya replies to herself", parent: mine);
        await Wait(() => Notes(s.Owner), 1);                                               // only Priya's thread tells the owner
        await Settle();

        Assert.Single(await Notes(s.Owner));
        Assert.Empty(await Notes(s.Priya));
    }

    [Fact]
    public async Task Resolving_tells_the_thread_author_unless_they_resolved_it_themselves_and_reopening_tells_nobody()
    {
        var s = await NewScene();
        using var worker = StartWorker(new FakeEmailSender());
        await worker.StartAsync();
        var root = await Say(s.Priya, s.Asset, "Is the date right?");
        await Wait(() => Notes(s.Owner), 1);

        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Patch, $"/api/v1/comments/{root}", s.Priya.Token, new { resolved = true })).Status);   // her own: nobody is told
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Patch, $"/api/v1/comments/{root}", s.Priya.Token, new { resolved = false })).Status);
        await Settle();
        Assert.Empty(await Notes(s.Priya));

        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Patch, $"/api/v1/comments/{root}", s.Bob.Token, new { resolved = true })).Status);
        var priyas = await Wait(() => Notes(s.Priya), 1);
        var n = Assert.Single(priyas);
        Assert.Equal(("comment.resolved", "comments", "Bob Brooks resolved your comment on 'Acme brochure'", "Is the date right?"),
            (n.GetProperty("type").GetString(), Category(n), Title(n), n.GetProperty("body").GetString()));
        Assert.Equal($"/library/assets/{s.Asset}?comment={root}", n.GetProperty("linkUrl").GetString());

        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Patch, $"/api/v1/comments/{root}", s.Bob.Token, new { resolved = false })).Status);
        await Settle();
        Assert.Single(await Notes(s.Priya));   // reopening is silent
        Assert.Single(await Notes(s.Owner));
    }

    [Fact]
    public async Task An_edit_that_adds_a_mention_tells_only_the_newly_mentioned()
    {
        var s = await NewScene();
        var email = new FakeEmailSender();
        using var worker = StartWorker(email);
        await worker.StartAsync();
        var comment = await Say(s.Priya, s.Asset, $"Draft for {s.Bob.Mention}");
        await Wait(() => Notes(s.Bob), 1);

        var edit = await Call(HttpMethod.Patch, $"/api/v1/comments/{comment}", s.Priya.Token, new { body = $"Draft for {s.Bob.Mention} and {s.Owner.Mention}" });
        Assert.Equal(HttpStatusCode.OK, edit.Status);
        var owners = await Wait(() => Notes(s.Owner), 2);
        await Settle();

        var mention = Assert.Single(owners, n => Category(n) == "mention");
        Assert.Equal(("comment.edited", "Priya Patel mentioned you on 'Acme brochure'"), (mention.GetProperty("type").GetString(), Title(mention)));
        Assert.Single(await Notes(s.Bob));                          // already mentioned: not told again
        Assert.Single(MailTo(email, s.Bob));
        Assert.Single(MailTo(email, s.Owner));                      // mentions are mailed by default
    }

    // ---- once only ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_redelivered_comment_event_never_notifies_anyone_twice()
    {
        var s = await NewScene();
        var email = new FakeEmailSender();
        using var worker = StartWorker(email);
        await worker.StartAsync();
        await Say(s.Priya, s.Asset, $"{s.Bob.Mention} once only");
        await Wait(() => Notes(s.Bob), 1);
        await Wait(() => Notes(s.Owner), 1);
        await NotifyHosts.Eventually(() => Task.FromResult(MailTo(email, s.Bob)), m => m.Count >= 1);

        string payload; Guid messageId;
        using (var c = Pg.RawAppConnection(s.Tenant))
        using (var cmd = new Npgsql.NpgsqlCommand("SELECT id, payload::text FROM outbox WHERE event_type = 'comment.added'", c))
        using (var r = cmd.ExecuteReader()) { r.Read(); messageId = r.GetGuid(0); payload = r.GetString(1); }
        var conn = await new ConnectionFactory { Uri = new Uri(mq.Url) }.CreateConnectionAsync();
        await using var ch = await conn.CreateChannelAsync();
        foreach (var id in new[] { messageId, messageId, Guid.NewGuid(), Guid.NewGuid() })     // the same message again, and the same event under new message ids
            await ch.BasicPublishAsync("dam.events", "comment.added", false, new BasicProperties
            {
                MessageId = id.ToString(), Type = "comment.added",
                Headers = new Dictionary<string, object?> { ["tenant_id"] = Encoding.UTF8.GetBytes(s.Tenant.ToString()) },
            }, Encoding.UTF8.GetBytes(payload));
        await Task.Delay(2000);
        await conn.CloseAsync();

        Assert.Single(await Notes(s.Bob));
        Assert.Single(await Notes(s.Owner));
        Assert.Single(MailTo(email, s.Bob));
        Assert.Equal(3, Count(s.Tenant, "SELECT count(*) FROM notification_deliveries"));   // Bob in-app and mail, the owner in-app
    }

    // ---- preferences catalogue ---------------------------------------------------------------------------------

    [Fact]
    public async Task The_preferences_list_the_mention_and_comment_categories_with_their_defaults()
    {
        var t = await NewTenantAsync();
        var token = As(t, "Editor");
        var cats = (await Call(HttpMethod.Get, $"{Inbox}/preferences", token)).Body.GetProperty("categories").EnumerateArray().ToDictionary(c => c.GetProperty("key").GetString()!);
        Assert.Equal(("Mentions", true, true), (cats["mention"].GetProperty("label").GetString(), cats["mention"].GetProperty("inApp").GetBoolean(), cats["mention"].GetProperty("email").GetBoolean()));
        Assert.Equal(("Comments and replies", true, false), (cats["comments"].GetProperty("label").GetString(), cats["comments"].GetProperty("inApp").GetBoolean(), cats["comments"].GetProperty("email").GetBoolean()));
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, $"{Inbox}/preferences", token, new { preferences = new[] { new { category = "comments", inApp = true, email = true } } })).Status);
    }

    // ---- webhooks ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Comment_events_are_in_the_catalogue_and_reach_a_subscribed_receiver_signed_and_without_the_text()
    {
        var s = await NewScene();
        var events = (await Call(HttpMethod.Get, "/api/v1/webhooks/events", s.Admin)).Body.EnumerateArray().ToDictionary(e => e.GetProperty("name").GetString()!);
        foreach (var name in new[] { "comment.added", "comment.resolved", "comment.reopened" })
        {
            Assert.True(events.ContainsKey(name), name);
            Assert.False(string.IsNullOrWhiteSpace(events[name].GetProperty("description").GetString()));
        }
        Assert.False(events.ContainsKey("comment.edited"));
        Assert.False(events.ContainsKey("comment.deleted"));

        await using var receiver = await TestReceiver.StartAsync();
        var hook = await Call(HttpMethod.Post, "/api/v1/webhooks", s.Admin, new { name = "Comments", url = receiver.Url, events = new[] { "comment.*" }, active = true });
        Assert.Equal(HttpStatusCode.Created, hook.Status);
        var secret = hook.Body.GetProperty("secret").GetString()!;
        using var worker = NotifyHosts.Worker(Pg, mq);
        await worker.StartAsync();

        var created = await Call(HttpMethod.Post, $"/api/v1/assets/{s.Asset}/comments", s.Priya.Token,
            new { body = $"Top secret wording for {s.Bob.Mention}", anchor = new { type = "region", x = 0.1, y = 0.2, w = 0.3, h = 0.4 } });
        var comment = created.Body.GetProperty("id").GetGuid();
        var hit = await receiver.WaitForAsync(h => h.Headers["X-Dam-Event"] == "comment.added");

        Assert.Equal(WebhookVerification.Valid, WebhookSignature.Verify(secret, hit.Headers["X-Dam-Timestamp"], hit.Headers["X-Dam-Signature"], hit.Body,
            DateTimeOffset.FromUnixTimeSeconds(long.Parse(hit.Headers["X-Dam-Timestamp"])), WebhookSignature.DefaultTolerance));
        var envelope = JsonDocument.Parse(hit.Body).RootElement;
        Assert.Equal(("comment.added", s.Tenant), (envelope.GetProperty("type").GetString(), envelope.GetProperty("tenantId").GetGuid()));
        var data = envelope.GetProperty("data");
        Assert.Equal((comment, s.Asset, s.Priya.Id, "region", false), (data.GetProperty("commentId").GetGuid(), data.GetProperty("assetId").GetGuid(), data.GetProperty("authorId").GetGuid(), data.GetProperty("anchorType").GetString(), data.GetProperty("resolved").GetBoolean()));
        Assert.Equal([s.Bob.Id], data.GetProperty("mentions").EnumerateArray().Select(m => m.GetGuid()));
        Assert.Equal(JsonValueKind.Null, data.GetProperty("parentId").ValueKind);
        Assert.True(data.TryGetProperty("versionId", out _));
        Assert.DoesNotContain("Top secret", hit.Body);                       // the text never leaves in an event

        // A reply is a comment.added with its parent; resolving and reopening are their own events.
        var reply = await Call(HttpMethod.Post, $"/api/v1/assets/{s.Asset}/comments", s.Bob.Token, new { body = "Reply", parentId = comment });
        var replyHit = await receiver.WaitForAsync(h => h.Headers["X-Dam-Event"] == "comment.added" && h.Body.Contains(reply.Body.GetProperty("id").GetString()!, StringComparison.Ordinal));
        Assert.Equal(comment, JsonDocument.Parse(replyHit.Body).RootElement.GetProperty("data").GetProperty("parentId").GetGuid());
        await Call(HttpMethod.Patch, $"/api/v1/comments/{comment}", s.Bob.Token, new { resolved = true });
        await Call(HttpMethod.Patch, $"/api/v1/comments/{comment}", s.Bob.Token, new { resolved = false });
        var resolved = await receiver.WaitForAsync(h => h.Headers["X-Dam-Event"] == "comment.resolved");
        Assert.Equal(s.Bob.Id, JsonDocument.Parse(resolved.Body).RootElement.GetProperty("data").GetProperty("resolvedBy").GetGuid());
        await receiver.WaitForAsync(h => h.Headers["X-Dam-Event"] == "comment.reopened");
        Assert.Equal(0, receiver.Hits.Count(h => h.Headers["X-Dam-Event"] is "comment.edited" or "comment.deleted"));
    }
}
