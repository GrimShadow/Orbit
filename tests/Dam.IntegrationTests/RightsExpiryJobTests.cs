using System.Net;
using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Domain.Licensing;
using Dam.Infrastructure.Scheduling;
using Dam.Shared.Webhooks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Dam.IntegrationTests;

/// <summary>
/// The rights job (ADR-0033) end to end on a clock the test controls: alerts at 30, 7 and 1 days exactly once each, through the console, email and webhooks;
/// published assets whose licence ended moving to expired as the system; assets in other states and extended licences left alone; drift caught up.
/// </summary>
[Collection("pg")]
public sealed class RightsExpiryJobTests(PostgresFixture pg, RabbitFixture mq) : AssetTestBase(pg), IClassFixture<RabbitFixture>
{
    private const string Inbox = "/api/v1/notifications";

    private sealed record Person(string Token, Guid Id, string Email);

    private async Task<Person> Provision(Guid tenant, string role, string name)
    {
        var token = ApiTestHost.TokenFor(tenant, Guid.NewGuid(), [role], name: name);
        var me = (await Call(HttpMethod.Get, "/api/v1/me", token)).Body;
        return new Person(token, me.GetProperty("userId").GetGuid(), me.GetProperty("email").GetString()!);
    }

    private sealed record Scene(Guid Tenant, string Admin, Guid Folder, Person Owner, FakeClock Clock);

    private async Task<Scene> NewScene()
    {
        var (t, admin, folder) = await TenantWithPacks();
        return new Scene(t, admin, folder, await Provision(t, "Librarian", "Olive Owner"), new FakeClock(DateTimeOffset.UtcNow));
    }

    private static DateOnly TodayOf(Scene s) => RightsRules.Today(s.Clock.UtcNow);

    private IHost Scheduler(Scene s) => NotifyHosts.Scheduler(Pg, s.Clock, configure: svc => svc.AddScheduledJob<RightsExpiryJob>());

    private static Task RunJob(IHost scheduler) => NotifyHosts.RunJobAsync(scheduler, RightsExpiryJob.JobKey);

    private async Task<Guid> NewRights(Scene s, string name = "Acme stock licence", DateOnly? validTo = null, DateOnly? validFrom = null)
    {
        var r = await Call(HttpMethod.Post, "/api/v1/rights", s.Admin, new { name, licenceType = "licensed", licensor = "Acme", channels = Array.Empty<string>(), regions = Array.Empty<string>(), validFrom, validTo, watermarkRequired = false });
        Assert.Equal(HttpStatusCode.Created, r.Status);
        return r.Body.GetProperty("id").GetGuid();
    }

    /// <summary>An asset owned by Olive, in the given status, under the rights.</summary>
    private async Task<Guid> OwnedAsset(Scene s, Guid? rights, string status = "published", string title = "Acme hero")
    {
        var asset = await NewAsset(s.Owner.Token, s.Folder, title: title);
        // Assigned while still a draft (the API refuses to put a published asset under an ended licence, and a blocked one under any); the status is set straight after.
        if (rights is { } id) Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, $"/api/v1/assets/{asset}/rights", s.Admin, new { rightsId = id })).Status);
        Sql($"UPDATE assets SET status = '{status}' WHERE id = '{asset}'");
        return asset;
    }

    private async Task<List<JsonElement>> Notes(Person p, string? type = null)
    {
        var r = await Call(HttpMethod.Get, Inbox, p.Token);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        var items = r.Body.GetProperty("items").EnumerateArray();
        return (type is null ? items : items.Where(n => n.GetProperty("type").GetString() == type)).ToList();
    }

    private static Task<List<JsonElement>> Wait(Func<Task<List<JsonElement>>> notes, int count) => NotifyHosts.Eventually(notes, n => n.Count >= count);
    private static async Task Settle() => await Task.Delay(1200);

    private IHost Worker(FakeEmailSender email) => NotifyHosts.Worker(Pg, mq, configure: svc => svc.AddSingleton<IEmailSender>(email));

    private static List<EmailMessage> LicenceMail(FakeEmailSender email, Person p) => email.Sent.Where(m => m.ToAddress == p.Email && m.Subject.Contains("Licence for", StringComparison.Ordinal)).ToList();

    private async Task<string> StatusOf(Scene s, Guid asset) => (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}", s.Admin)).Body.GetProperty("status").GetString()!;

    // ---- alerts --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_owner_is_told_at_30_7_and_1_days_and_once_the_licence_has_ended_each_exactly_once_in_the_console_and_by_email()
    {
        var s = await NewScene();
        var email = new FakeEmailSender();
        using var worker = Worker(email);
        await worker.StartAsync();
        var rights = await NewRights(s, validTo: TodayOf(s).AddDays(30));
        var asset = await OwnedAsset(s, rights, "published", "Acme hero");
        using var scheduler = Scheduler(s);

        // 30 days left: the first alert, however often the job runs
        await RunJob(scheduler); await RunJob(scheduler);
        var first = Assert.Single(await Wait(() => Notes(s.Owner, "rights.expiring"), 1));
        Assert.Equal("Licence for 'Acme hero' expires in 30 days", first.GetProperty("title").GetString());
        Assert.Equal(("rights", $"/library/assets/{asset}", asset), (first.GetProperty("category").GetString(), first.GetProperty("linkUrl").GetString(), first.GetProperty("assetId").GetGuid()));
        Assert.Contains("'Acme stock licence'", first.GetProperty("body").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, Count(s.Tenant, $"SELECT count(*) FROM rights_alerts WHERE asset_id = '{asset}' AND threshold_days = 30"));

        // 8 days left: still within the first window; nothing new
        s.Clock.UtcNow = s.Clock.UtcNow.AddDays(22);
        await RunJob(scheduler);
        await Settle();
        Assert.Single(await Notes(s.Owner, "rights.expiring"));

        // 7 days left
        s.Clock.UtcNow = s.Clock.UtcNow.AddDays(1);
        await RunJob(scheduler); await RunJob(scheduler);
        var two = await Wait(() => Notes(s.Owner, "rights.expiring"), 2);
        await Settle();
        two = await Notes(s.Owner, "rights.expiring");
        Assert.Equal(2, two.Count);
        Assert.Contains(two, n => n.GetProperty("title").GetString() == "Licence for 'Acme hero' expires in 7 days");

        // a scheduler that restarted in between does not repeat itself
        scheduler.Dispose();
        using var restarted = Scheduler(s);
        s.Clock.UtcNow = s.Clock.UtcNow.AddDays(5);   // 2 days left: still the 7-day window
        await RunJob(restarted);
        await Settle();
        Assert.Equal(2, (await Notes(s.Owner, "rights.expiring")).Count);

        // 1 day left
        s.Clock.UtcNow = s.Clock.UtcNow.AddDays(1);
        await RunJob(restarted); await RunJob(restarted);
        var three = await Wait(() => Notes(s.Owner, "rights.expiring"), 3);
        Assert.Contains(three, n => n.GetProperty("title").GetString() == "Licence for 'Acme hero' expires in 1 day");

        // its last day: nothing more to say
        s.Clock.UtcNow = s.Clock.UtcNow.AddDays(1);
        await RunJob(restarted);
        await Settle();
        Assert.Equal(3, (await Notes(s.Owner, "rights.expiring")).Count);
        Assert.Equal("published", await StatusOf(s, asset));   // still valid today

        // the day after it ended
        s.Clock.UtcNow = s.Clock.UtcNow.AddDays(1);
        await RunJob(restarted); await RunJob(restarted);
        var ended = Assert.Single(await Wait(() => Notes(s.Owner, "rights.expired"), 1));
        Assert.Equal("Licence for 'Acme hero' has expired", ended.GetProperty("title").GetString());
        Assert.Equal("expired", await StatusOf(s, asset));
        await Settle();
        Assert.Single(await Notes(s.Owner, "rights.expired"));
        foreach (var threshold in new[] { 30, 7, 1, 0 })
            Assert.Equal(1, Count(s.Tenant, $"SELECT count(*) FROM rights_alerts WHERE asset_id = '{asset}' AND threshold_days = {threshold}"));

        // four licence emails, one per alert; the lifecycle notice that the asset expired is a separate category and not mailed by default
        Assert.Equal(4, LicenceMail(email, s.Owner).Count);
        Assert.Contains(LicenceMail(email, s.Owner), m => m.Subject.Contains("expires in 7 days", StringComparison.Ordinal));
        Assert.Contains(LicenceMail(email, s.Owner), m => m.Html.Contains($"https://orbit.example.test/library/assets/{asset}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_licence_assigned_with_days_to_spare_gets_only_the_alert_due_at_that_point()
    {
        var s = await NewScene();
        using var worker = Worker(new FakeEmailSender());
        await worker.StartAsync();
        var asset = await OwnedAsset(s, await NewRights(s, validTo: TodayOf(s).AddDays(3)));
        using var scheduler = Scheduler(s);
        await RunJob(scheduler); await RunJob(scheduler);

        var first = Assert.Single(await Wait(() => Notes(s.Owner, "rights.expiring"), 1));
        Assert.Equal("Licence for 'Acme hero' expires in 3 days", first.GetProperty("title").GetString());
        Assert.Equal(1, Count(s.Tenant, $"SELECT count(*) FROM rights_alerts WHERE asset_id = '{asset}' AND threshold_days = 7"));
        Assert.Equal(0, Count(s.Tenant, $"SELECT count(*) FROM rights_alerts WHERE asset_id = '{asset}' AND threshold_days = 30"));

        s.Clock.UtcNow = s.Clock.UtcNow.AddDays(2);   // 1 day left
        await RunJob(scheduler);
        await Wait(() => Notes(s.Owner, "rights.expiring"), 2);
        Assert.Equal(2, (await Notes(s.Owner, "rights.expiring")).Count);
    }

    [Fact]
    public async Task Extending_the_licence_starts_the_sequence_again()
    {
        var s = await NewScene();
        using var worker = Worker(new FakeEmailSender());
        await worker.StartAsync();
        var rights = await NewRights(s, validTo: TodayOf(s).AddDays(20));
        await OwnedAsset(s, rights);
        using var scheduler = Scheduler(s);
        await RunJob(scheduler);
        await Wait(() => Notes(s.Owner, "rights.expiring"), 1);

        // extended by 60 days; a month on, it is 30 days from ending again
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, $"/api/v1/rights/{rights}", s.Admin, new { name = "Acme stock licence", licenceType = "licensed", validTo = TodayOf(s).AddDays(80) })).Status);
        s.Clock.UtcNow = s.Clock.UtcNow.AddDays(50);
        await RunJob(scheduler);
        var all = await Wait(() => Notes(s.Owner, "rights.expiring"), 2);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task Only_assets_still_in_play_are_alerted_and_an_asset_without_an_owner_raises_the_event_but_tells_nobody()
    {
        var s = await NewScene();
        await using var receiver = await TestReceiver.StartAsync();
        Assert.Equal(HttpStatusCode.Created, (await Call(HttpMethod.Post, "/api/v1/webhooks", s.Admin, new { name = "Licences", url = receiver.Url, events = new[] { "rights.*" }, active = true })).Status);
        using var worker = Worker(new FakeEmailSender());
        await worker.StartAsync();
        var rights = await NewRights(s, validTo: TodayOf(s).AddDays(7));
        var live = await OwnedAsset(s, rights, "approved", "Live");
        foreach (var status in new[] { "archived", "blocked", "expired" }) await OwnedAsset(s, rights, status, "Out " + status);
        var gone = await OwnedAsset(s, rights, "published", "Gone");
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"/api/v1/assets/{gone}", s.Admin)).Status);
        var ownerless = await OwnedAsset(s, rights, "draft", "Ownerless");
        Sql($"UPDATE assets SET owner_id = NULL WHERE id = '{ownerless}'");
        await OwnedAsset(s, null, "published", "No rights");

        using var scheduler = Scheduler(s);
        await RunJob(scheduler);
        var told = Assert.Single(await Wait(() => Notes(s.Owner, "rights.expiring"), 1));
        Assert.Equal("Licence for 'Live' expires in 7 days", told.GetProperty("title").GetString());
        await Settle();
        Assert.Equal(2, Count(s.Tenant, "SELECT count(*) FROM rights_alerts"));   // Live and Ownerless only

        var hits = new List<JsonElement>();
        await receiver.WaitForAsync(h => h.Headers["X-Dam-Event"] == "rights.expiring" && h.Body.Contains(ownerless.ToString(), StringComparison.Ordinal));
        hits.AddRange(receiver.Hits.Where(h => h.Headers["X-Dam-Event"] == "rights.expiring").Select(h => JsonDocument.Parse(h.Body).RootElement.Clone()));
        Assert.Equal(new[] { live, ownerless }.Order(), hits.Select(h => h.GetProperty("data").GetProperty("assetId").GetGuid()).Order());
    }

    [Fact]
    public async Task Webhook_receivers_get_signed_licence_events_with_the_days_and_the_threshold()
    {
        var s = await NewScene();
        await using var receiver = await TestReceiver.StartAsync();
        var hook = await Call(HttpMethod.Post, "/api/v1/webhooks", s.Admin, new { name = "Licences", url = receiver.Url, events = new[] { "rights.expiring", "rights.expired", "asset.expired" }, active = true });
        var secret = hook.Body.GetProperty("secret").GetString()!;
        using var worker = Worker(new FakeEmailSender());
        await worker.StartAsync();
        var rights = await NewRights(s, "Acme stock licence", validTo: TodayOf(s).AddDays(5));
        var asset = await OwnedAsset(s, rights);
        using var scheduler = Scheduler(s);
        await RunJob(scheduler);

        var expiring = await receiver.WaitForAsync(h => h.Headers["X-Dam-Event"] == "rights.expiring");
        Assert.Equal(WebhookVerification.Valid, WebhookSignature.Verify(secret, expiring.Headers["X-Dam-Timestamp"], expiring.Headers["X-Dam-Signature"], expiring.Body,
            DateTimeOffset.FromUnixTimeSeconds(long.Parse(expiring.Headers["X-Dam-Timestamp"], System.Globalization.CultureInfo.InvariantCulture)), WebhookSignature.DefaultTolerance));
        var envelope = JsonDocument.Parse(expiring.Body).RootElement;
        Assert.Equal(("rights.expiring", s.Tenant), (envelope.GetProperty("type").GetString(), envelope.GetProperty("tenantId").GetGuid()));
        var data = envelope.GetProperty("data");
        Assert.Equal((rights, asset, "Acme stock licence", TodayOf(s).AddDays(5).ToString("yyyy-MM-dd"), 5, 7),
            (data.GetProperty("rightsId").GetGuid(), data.GetProperty("assetId").GetGuid(), data.GetProperty("rightsName").GetString(), data.GetProperty("validTo").GetString(),
             data.GetProperty("daysRemaining").GetInt32(), data.GetProperty("thresholdDays").GetInt32()));

        s.Clock.UtcNow = s.Clock.UtcNow.AddDays(6);   // it ended yesterday
        await RunJob(scheduler);
        var ended = await receiver.WaitForAsync(h => h.Headers["X-Dam-Event"] == "rights.expired");
        var endedData = JsonDocument.Parse(ended.Body).RootElement.GetProperty("data");
        Assert.Equal((rights, asset), (endedData.GetProperty("rightsId").GetGuid(), endedData.GetProperty("assetId").GetGuid()));
        var expired = await receiver.WaitForAsync(h => h.Headers["X-Dam-Event"] == "asset.expired");   // the normal lifecycle event fires for the automatic move too
        Assert.Equal(asset, JsonDocument.Parse(expired.Body).RootElement.GetProperty("data").GetProperty("assetId").GetGuid());
    }

    // ---- automatic expiry ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_published_asset_whose_licence_has_ended_moves_to_expired_in_one_run_as_the_system_and_other_statuses_are_left_alone()
    {
        var s = await NewScene();
        var rights = await NewRights(s, "Acme stock licence", validTo: TodayOf(s).AddDays(10));
        var published = await OwnedAsset(s, rights, "published", "Published");
        var approved = await OwnedAsset(s, rights, "approved", "Approved");
        var draft = await OwnedAsset(s, rights, "draft", "Draft");
        var inReview = await OwnedAsset(s, rights, "in_review", "In review");
        var archived = await OwnedAsset(s, rights, "archived", "Archived");
        var otherRights = await NewRights(s, "Still running", validTo: TodayOf(s).AddDays(40));
        var elsewhere = await OwnedAsset(s, otherRights, "published", "Other licence");
        var noRights = await OwnedAsset(s, null, "published", "No licence");
        var openEnded = await OwnedAsset(s, await NewRights(s, "Open ended"), "published", "Open ended");

        using var scheduler = Scheduler(s);
        await RunJob(scheduler);
        Assert.Equal("published", await StatusOf(s, published));   // not ended yet

        s.Clock.UtcNow = s.Clock.UtcNow.AddDays(11);   // the licence ended yesterday
        await RunJob(scheduler);

        var a = (await Call(HttpMethod.Get, $"/api/v1/assets/{published}", s.Admin)).Body;
        Assert.Equal("expired", a.GetProperty("status").GetString());
        Assert.Equal("Licence expired: Acme stock licence", a.GetProperty("statusReason").GetString());
        Assert.Equal(1, Count(s.Tenant, $"SELECT count(*) FROM audit_log WHERE action = 'asset.status.changed' AND entity_id = '{published}' AND after->>'To' = 'expired' AND actor = 'system'"));
        Assert.Equal(1, Count(s.Tenant, $"SELECT count(*) FROM outbox WHERE event_type = 'asset.status.changed' AND payload->>'AssetId' = '{published}' AND payload->>'To' = 'expired'"));
        foreach (var (asset, status) in new[] { (approved, "approved"), (draft, "draft"), (inReview, "in_review"), (archived, "archived"), (elsewhere, "published"), (noRights, "published"), (openEnded, "published") })
            Assert.Equal(status, await StatusOf(s, asset));

        // running again changes nothing and audits nothing more
        await RunJob(scheduler);
        Assert.Equal(1, Count(s.Tenant, $"SELECT count(*) FROM audit_log WHERE action = 'asset.status.changed' AND entity_id = '{published}'"));
    }

    [Fact]
    public async Task An_asset_whose_licence_was_extended_before_the_run_is_not_touched()
    {
        var s = await NewScene();
        var rights = await NewRights(s, validTo: TodayOf(s).AddDays(-1));   // it ended yesterday ...
        var asset = await OwnedAsset(s, rights, "published");
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, $"/api/v1/rights/{rights}", s.Admin, new { name = "Acme stock licence", licenceType = "licensed", validTo = TodayOf(s).AddDays(30) })).Status);   // ... and was then extended

        using var scheduler = Scheduler(s);
        await RunJob(scheduler);
        Assert.Equal("published", await StatusOf(s, asset));
    }

    [Fact]
    public async Task The_owner_hears_that_the_asset_expired_and_why()
    {
        var s = await NewScene();
        using var worker = Worker(new FakeEmailSender());
        await worker.StartAsync();
        var asset = await OwnedAsset(s, await NewRights(s, "Acme stock licence", validTo: TodayOf(s).AddDays(10)), "published", "Acme hero");
        using var scheduler = Scheduler(s);
        s.Clock.UtcNow = s.Clock.UtcNow.AddDays(11);
        await RunJob(scheduler);

        var lifecycle = Assert.Single(await Wait(() => Notes(s.Owner, "asset.status.changed"), 1));
        Assert.StartsWith("Expired:", lifecycle.GetProperty("title").GetString());
        Assert.Contains("Licence expired: Acme stock licence", lifecycle.GetProperty("body").GetString(), StringComparison.Ordinal);
        Assert.Equal(asset, lifecycle.GetProperty("assetId").GetGuid());
    }

    // ---- catching up ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Assets_whose_copy_of_the_rights_scope_fell_behind_are_re_derived_by_the_job()
    {
        var s = await NewScene();
        var rights = await Call(HttpMethod.Post, "/api/v1/rights", s.Admin, new { name = "Web only", licenceType = "licensed", channels = new[] { "web" } });
        var rightsId = rights.Body.GetProperty("id").GetGuid();
        var meta = new { description = new { en = "A car" }, channels = new[] { "web", "signage", "print" } };
        var first = await NewAsset(s.Owner.Token, s.Folder, metadata: meta);
        var second = await NewAsset(s.Owner.Token, s.Folder, title: "Second", metadata: meta);
        foreach (var asset in new[] { first, second }) await Call(HttpMethod.Put, $"/api/v1/assets/{asset}/rights", s.Admin, new { rightsId });
        async Task<string[]> Channels(Guid id) => Strings((await Call(HttpMethod.Get, $"/api/v1/assets/{id}", s.Admin)).Body.GetProperty("attributes").GetProperty("channel"));

        // as if a rights update had been cut short: the record now says print and web, but the assets still carry the old copy of its scope
        Sql($"UPDATE rights SET channels = '{{print,web}}' WHERE id = '{rightsId}'");
        Assert.Equal(["web"], await Channels(first));

        using var scheduler = Scheduler(s);
        await RunJob(scheduler);
        foreach (var id in new[] { first, second })
        {
            Assert.Equal(["print", "web"], (await Channels(id)).Order());
            Assert.Equal(1, Count(s.Tenant, $"SELECT count(*) FROM assets WHERE id = '{id}' AND rights_channels = '{{print,web}}'"));
            Assert.Equal(2, Count(s.Tenant, $"SELECT count(*) FROM audit_log WHERE action = 'asset.rights.changed' AND entity_id = '{id}'"));   // the assignment, and the catch-up
        }

        // the copies are level now, so a second run finds nothing to do
        await RunJob(scheduler);
        Assert.Equal(4, Count(s.Tenant, "SELECT count(*) FROM audit_log WHERE action = 'asset.rights.changed'"));
    }

    [Fact]
    public async Task An_update_reaching_more_assets_than_one_request_handles_reports_the_rest_and_the_job_finishes_them()
    {
        var s = await NewScene();
        var rights = await Call(HttpMethod.Post, "/api/v1/rights", s.Admin, new { name = "Web only", licenceType = "licensed", channels = new[] { "web" } });
        var rightsId = rights.Body.GetProperty("id").GetGuid();
        var meta = new { description = new { en = "A car" }, channels = new[] { "web", "signage" } };
        var assets = new List<Guid>();
        for (var i = 0; i < 501; i++) assets.Add(await NewAsset(s.Owner.Token, s.Folder, title: $"Bulk {i}", metadata: meta));
        foreach (var chunk in assets.Chunk(500))
            Assert.Equal(chunk.Length, (await Call(HttpMethod.Post, $"/api/v1/rights/{rightsId}/assign", s.Admin, new { assetIds = chunk })).Body.GetProperty("assigned").GetInt32());

        var update = await Call(HttpMethod.Put, $"/api/v1/rights/{rightsId}", s.Admin, new { name = "Web only", licenceType = "licensed", channels = new[] { "signage" } });
        Assert.Equal(HttpStatusCode.OK, update.Status);
        Assert.Equal(1, update.Body.GetProperty("pendingAssets").GetInt32());
        Assert.Equal(500, Count(s.Tenant, $"SELECT count(*) FROM assets WHERE rights_id = '{rightsId}' AND rights_channels = '{{signage}}'"));   // the first 500 by id were done in the request
        Assert.Equal(1, Count(s.Tenant, $"SELECT count(*) FROM assets WHERE rights_id = '{rightsId}' AND rights_channels = '{{web}}'"));

        using var scheduler = Scheduler(s);
        await RunJob(scheduler);
        Assert.Equal(501, Count(s.Tenant, $"SELECT count(*) FROM assets WHERE rights_id = '{rightsId}' AND rights_channels = '{{signage}}'"));
        Assert.Equal(501, Count(s.Tenant, $"SELECT count(*) FROM assets WHERE rights_id = '{rightsId}' AND attributes->'channel' = '[\"signage\"]'::jsonb"));

        // No relay runs in this test, so its ~2,500 events would sit in the outbox until the next test that starts a worker, and flood it. They are of no interest.
        Sql($"UPDATE outbox SET published_at = now() WHERE tenant_id = '{s.Tenant}' AND published_at IS NULL");
    }

    [Fact]
    public async Task Each_tenant_is_alerted_about_its_own_assets_only()
    {
        var a = await NewScene();
        var b = await NewScene();
        using var worker = Worker(new FakeEmailSender());
        await worker.StartAsync();
        await OwnedAsset(a, await NewRights(a, validTo: TodayOf(a).AddDays(7)));
        await OwnedAsset(b, await NewRights(b, "Other licence", validTo: TodayOf(b).AddDays(7)), "published", "Other hero");
        using var scheduler = Scheduler(a);
        await RunJob(scheduler);

        Assert.Single(await Wait(() => Notes(a.Owner, "rights.expiring"), 1));
        Assert.Single(await Wait(() => Notes(b.Owner, "rights.expiring"), 1));
        await Settle();
        Assert.Equal("Licence for 'Acme hero' expires in 7 days", (await Notes(a.Owner, "rights.expiring"))[0].GetProperty("title").GetString());
        Assert.Equal("Licence for 'Other hero' expires in 7 days", (await Notes(b.Owner, "rights.expiring"))[0].GetProperty("title").GetString());
        Assert.Equal(1, Count(a.Tenant, "SELECT count(*) FROM rights_alerts"));
        Assert.Equal(1, Count(b.Tenant, "SELECT count(*) FROM rights_alerts"));
    }

    [Fact]
    public async Task A_scheduled_publish_does_not_fire_for_an_asset_whose_licence_is_not_in_force()
    {
        var s = await NewScene();
        var ended = await NewRights(s, "Ended", validTo: TodayOf(s).AddDays(-1));
        var current = await NewRights(s, "Current", validTo: TodayOf(s).AddDays(30));
        var blocked = await OwnedAsset(s, null, "approved", "Blocked by licence");
        var allowed = await OwnedAsset(s, current, "approved", "Allowed");
        Sql($"UPDATE assets SET rights_id = '{ended}', publish_at = now() - interval '1 minute' WHERE id = '{blocked}'");
        Sql($"UPDATE assets SET publish_at = now() - interval '1 minute' WHERE id = '{allowed}'");

        using var host = SchedulerWithPublishJob(s);
        await NotifyHosts.RunJobAsync(host, PublishExpireJob.JobKey);
        Assert.Equal("approved", await StatusOf(s, blocked));
        Assert.Equal("published", await StatusOf(s, allowed));
    }

    private IHost SchedulerWithPublishJob(Scene s) => NotifyHosts.Scheduler(Pg, s.Clock, configure: svc => svc.AddScheduledJob<PublishExpireJob>());
}
