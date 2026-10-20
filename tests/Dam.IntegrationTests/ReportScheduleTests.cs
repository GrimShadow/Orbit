using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Domain.Analytics;
using Dam.Infrastructure.Scheduling;
using Microsoft.Extensions.DependencyInjection;

namespace Dam.IntegrationTests;

/// <summary>An email sender that keeps what it was given, can be told to refuse particular addresses, and can say it is switched off.</summary>
public sealed class SelectiveEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<EmailMessage> _sent = new();
    private readonly ConcurrentDictionary<string, bool> _refuse = new();
    public bool IsEnabled { get; set; } = true;
    public IReadOnlyList<EmailMessage> Sent => [.. _sent];
    public void Refuse(string address, bool refuse = true) => _refuse[address] = refuse;
    public IReadOnlyList<EmailMessage> To(string address) => [.. _sent.Where(m => m.ToAddress == address)];

    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        if (_refuse.TryGetValue(message.ToAddress, out var r) && r) throw new InvalidOperationException("550 mailbox unavailable");
        _sent.Enqueue(message);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Scheduled (emailed) reports (ADR-0037 decision 8): who may manage them, validation, audit, the recipients rule (tenant users only), send-now, and the job that sends due schedules once
/// per slot, retries a failed one without mailing anyone twice, says so when email is not configured, and builds each recipient's email from what they may see.
/// </summary>
[Collection("pg")]
public sealed class ReportScheduleTests(PostgresFixture pg) : AnalyticsTestBase(pg)
{
    protected override IReadOnlyDictionary<string, string?>? ApiEnvironment => new Dictionary<string, string?> { ["DAM_CONSOLE_PUBLIC_URL"] = "https://orbit.example.test" };

    private HttpClient? _mailClient;
    private readonly SelectiveEmailSender _apiMail = new();

    public override async Task DisposeAsync()
    {
        _mailClient?.Dispose();
        await base.DisposeAsync();
    }

    /// <summary>The API with a mail server (the default test API has none).</summary>
    private async Task<(HttpStatusCode Status, JsonElement Body)> CallWithMail(HttpMethod method, string path, string token, object? body = null)
    {
        _mailClient ??= Api.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IEmailSender>(_apiMail))).CreateClient();
        using var req = new HttpRequestMessage(method, path) { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) } };
        if (body is not null) req.Content = JsonContent.Create(body);
        using var res = await _mailClient.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, text.Length > 0 ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    private sealed record Person(string Token, Guid Id, string Email);

    private async Task<Person> Provision(Guid tenant, string role, string name = "Person")
    {
        var token = ApiTestHost.TokenFor(tenant, Guid.NewGuid(), [role], name: name);
        var me = (await Call(HttpMethod.Get, "/api/v1/me", token)).Body;
        return new Person(token, me.GetProperty("userId").GetGuid(), me.GetProperty("email").GetString()!);
    }

    private static object Body(string report = "kpis", int days = 30, string frequency = "daily", int hour = 6, int? dow = null, int? dom = null, Guid[]? to = null, bool enabled = true) =>
        new { report, days, frequency, hourUtc = hour, dayOfWeek = dow, dayOfMonth = dom, recipientUserIds = to ?? [], enabled };

    private async Task<JsonElement> Create(string token, object body)
    {
        var r = await Call(HttpMethod.Post, SchedulesUrl, token, body);
        Assert.True(r.Status == HttpStatusCode.Created, $"{(int)r.Status}: {r.Body}");
        return r.Body;
    }

    private void MakeDue(Guid schedule, string ago = "1 minute") => Sql($"UPDATE report_schedules SET next_run_at = now() - interval '{ago}' WHERE id = '{schedule}'");

    /// <summary>Other tests' schedules are due too, and the job looks across every tenant: switch off everyone else's so a run only concerns this test.</summary>
    private void QuietOthers(Guid tenant) => Sql($"UPDATE report_schedules SET enabled = false WHERE tenant_id <> '{tenant}'");

    private async Task<JsonElement> Get(Guid tenant, string admin, Guid id) =>
        (await GetOk(SchedulesUrl, admin)).EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == id);

    // ---- management --------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_schedule_is_created_listed_updated_and_deleted_with_an_audit_row_for_each_change()
    {
        var (t, admin, _) = await TenantWithPacks();
        var editor = await Provision(t, "Editor");
        var created = await Create(admin, Body("top-assets", 14, "weekly", 9, dow: 3, to: [editor.Id]));
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal(("top-assets", 14, "weekly", 9, 3), (created.GetProperty("report").GetString(), created.GetProperty("days").GetInt32(), created.GetProperty("frequency").GetString(), created.GetProperty("hourUtc").GetInt32(), created.GetProperty("dayOfWeek").GetInt32()));
        Assert.Equal(JsonValueKind.Null, created.GetProperty("dayOfMonth").ValueKind);
        Assert.Equal([editor.Id], created.GetProperty("recipientUserIds").EnumerateArray().Select(x => x.GetGuid()));
        Assert.True(created.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("lastSentAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, created.GetProperty("lastError").ValueKind);
        var next = created.GetProperty("nextRunAt").GetDateTimeOffset();
        Assert.Equal(((int)DayOfWeek.Wednesday, 9), ((int)next.DayOfWeek, next.Hour));
        Assert.True(next > DateTimeOffset.UtcNow && next <= DateTimeOffset.UtcNow.AddDays(7));

        Assert.Equal(id, (await GetOk(SchedulesUrl, admin)).EnumerateArray().Single().GetProperty("id").GetGuid());

        var put = await Call(HttpMethod.Put, $"{SchedulesUrl}/{id}", admin, Body("kpis", 60, "monthly", 5, dom: 31, to: [editor.Id], enabled: false));
        Assert.Equal(HttpStatusCode.OK, put.Status);
        Assert.Equal(("kpis", 60, "monthly", 31), (put.Body.GetProperty("report").GetString(), put.Body.GetProperty("days").GetInt32(), put.Body.GetProperty("frequency").GetString(), put.Body.GetProperty("dayOfMonth").GetInt32()));
        Assert.Equal(JsonValueKind.Null, put.Body.GetProperty("dayOfWeek").ValueKind);        // a monthly schedule has no weekday
        Assert.Equal(JsonValueKind.Null, put.Body.GetProperty("nextRunAt").ValueKind);        // switched off: never due
        Assert.False(put.Body.GetProperty("enabled").GetBoolean());

        var on = await Call(HttpMethod.Put, $"{SchedulesUrl}/{id}", admin, Body("kpis", 60, "monthly", 5, dom: 31, to: [editor.Id]));
        Assert.NotEqual(JsonValueKind.Null, on.Body.GetProperty("nextRunAt").ValueKind);

        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"{SchedulesUrl}/{id}", admin)).Status);
        Assert.Empty((await GetOk(SchedulesUrl, admin)).EnumerateArray());

        Assert.Equal(1, Count(t, $"SELECT count(*) FROM audit_log WHERE action = 'report_schedule.created' AND entity_type = 'report_schedule' AND entity_id = '{id}'"));
        Assert.Equal(2, Count(t, $"SELECT count(*) FROM audit_log WHERE action = 'report_schedule.updated' AND entity_id = '{id}'"));
        Assert.Equal(1, Count(t, $"SELECT count(*) FROM audit_log WHERE action = 'report_schedule.deleted' AND entity_id = '{id}'"));
        Assert.Equal(4, Count(t, $"SELECT count(*) FROM outbox WHERE event_type LIKE 'report_schedule.%'"));
    }

    [Fact]
    public async Task Unknown_schedules_are_not_found()
    {
        var (_, admin, _) = await TenantWithPacks();
        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Put, $"{SchedulesUrl}/{id}", admin, Body(to: [Guid.NewGuid()]))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Delete, $"{SchedulesUrl}/{id}", admin)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"{SchedulesUrl}/{id}/send-now", admin)).Status);
    }

    [Theory]
    [InlineData("Admin", true)]
    [InlineData("Librarian", true)]
    [InlineData("Editor", false)]
    [InlineData("Approver", false)]
    [InlineData("Contributor", false)]
    [InlineData("Viewer", false)]
    public async Task Only_admin_and_librarian_manage_scheduled_reports(string role, bool allowed)
    {
        var (t, admin, _) = await TenantWithPacks();
        var me = await Provision(t, role);
        var existing = (await Create(admin, Body(to: [me.Id]))).GetProperty("id").GetGuid();
        var token = me.Token;
        var forbidden = allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden;

        Assert.Equal(forbidden, (await Call(HttpMethod.Get, SchedulesUrl, token)).Status);
        Assert.Equal(allowed ? HttpStatusCode.Created : HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, SchedulesUrl, token, Body(to: [me.Id]))).Status);
        Assert.Equal(forbidden, (await Call(HttpMethod.Put, $"{SchedulesUrl}/{existing}", token, Body(to: [me.Id]))).Status);
        Assert.Equal(allowed ? HttpStatusCode.Conflict : HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, $"{SchedulesUrl}/{existing}/send-now", token)).Status);   // allowed, but this API has no mail server
        Assert.Equal(allowed ? HttpStatusCode.NoContent : HttpStatusCode.Forbidden, (await Call(HttpMethod.Delete, $"{SchedulesUrl}/{existing}", token)).Status);
    }

    [Fact]
    public async Task Schedules_need_a_token()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(HttpMethod.Get, SchedulesUrl, "not-a-token")).Status);
    }

    [Fact]
    public async Task A_librarian_may_run_reports_and_manage_them_but_an_editor_may_only_run_them()
    {
        var (t, _, _) = await TenantWithPacks();
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, $"{ReportsUrl}/kpis", As(t, "Editor"))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, SchedulesUrl, As(t, "Editor"))).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, SchedulesUrl, As(t, "Librarian"))).Status);
    }

    // ---- validation --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Recipients_must_be_active_users_of_the_tenant_so_a_schedule_cannot_mail_outsiders()
    {
        var (t, admin, _) = await TenantWithPacks();
        var (tb, _, _) = await TenantWithPacks();
        var mine = await Provision(t, "Editor");
        var theirs = await Provision(tb, "Editor");
        var left = await Provision(t, "Editor");
        Sql($"UPDATE users SET status = 'Disabled' WHERE id = '{left.Id}'");

        foreach (var bad in new[] { Guid.NewGuid(), theirs.Id, left.Id })
        {
            var r = await Call(HttpMethod.Post, SchedulesUrl, admin, Body(to: [mine.Id, bad]));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status);
            Assert.Contains(bad.ToString(), r.Body.GetProperty("errors").GetProperty("recipientUserIds")[0].GetString(), StringComparison.Ordinal);
        }
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, SchedulesUrl, admin, Body(to: []))).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, SchedulesUrl, admin, new { report = "kpis", days = 30, frequency = "daily", hourUtc = 6, enabled = true }))
            .Status);                                                                          // no recipients at all
        Assert.Empty((await GetOk(SchedulesUrl, admin)).EnumerateArray());

        Assert.Equal(HttpStatusCode.Created, (await Call(HttpMethod.Post, SchedulesUrl, admin, Body(to: [mine.Id, mine.Id]))).Status);        // repeats are merged
        Assert.Single((await GetOk(SchedulesUrl, admin)).EnumerateArray().Single().GetProperty("recipientUserIds").EnumerateArray());
        var update = await Call(HttpMethod.Put, $"{SchedulesUrl}/{(await GetOk(SchedulesUrl, admin)).EnumerateArray().Single().GetProperty("id").GetGuid()}", admin, Body(to: [theirs.Id]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, update.Status);                         // updates are checked too
    }

    [Fact]
    public async Task Settings_are_validated_field_by_field()
    {
        var (t, admin, _) = await TenantWithPacks();
        var me = await Provision(t, "Editor");
        async Task<HashSet<string>> Fields(object body)
        {
            var r = await Call(HttpMethod.Post, SchedulesUrl, admin, body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status);
            return r.Body.GetProperty("errors").EnumerateObject().Select(p => p.Name).ToHashSet();
        }
        Assert.Contains("report", await Fields(Body("revenue", to: [me.Id])));
        Assert.Contains("days", await Fields(Body("kpis", 0, to: [me.Id])));
        Assert.Contains("days", await Fields(Body("kpis", 366, to: [me.Id])));
        Assert.Contains("days", await Fields(Body("unused-assets", 3651, to: [me.Id])));
        Assert.Contains("frequency", await Fields(Body(frequency: "hourly", to: [me.Id])));
        Assert.Contains("hourUtc", await Fields(Body(hour: 24, to: [me.Id])));
        Assert.Contains("dayOfWeek", await Fields(Body(frequency: "weekly", to: [me.Id])));
        Assert.Contains("dayOfMonth", await Fields(Body(frequency: "monthly", dom: 0, to: [me.Id])));
        Assert.Equal(HttpStatusCode.Created, (await Call(HttpMethod.Post, SchedulesUrl, admin, Body("unused-assets", 3650, to: [me.Id]))).Status);
    }

    [Fact]
    public async Task A_tenant_has_at_most_fifty_schedules()
    {
        var (t, admin, _) = await TenantWithPacks();
        var me = await Provision(t, "Editor");
        Sql("INSERT INTO report_schedules (id, tenant_id, report, days, frequency, hour_utc, recipient_user_ids, enabled, sent_to, created_at, updated_at) "
            + $"SELECT gen_random_uuid(), '{t}', 'kpis', 7, 'daily', 6, ARRAY['{me.Id}']::uuid[], false, '{{}}', now(), now() FROM generate_series(1, 50)");
        var r = await Call(HttpMethod.Post, SchedulesUrl, admin, Body(to: [me.Id]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status);
    }

    [Fact]
    public async Task One_tenants_schedules_are_invisible_and_untouchable_to_another()
    {
        var (a, adminA, _) = await TenantWithPacks();
        var (_, adminB, _) = await TenantWithPacks();
        var id = (await Create(adminA, Body(to: [(await Provision(a, "Editor")).Id]))).GetProperty("id").GetGuid();
        Assert.Empty((await GetOk(SchedulesUrl, adminB)).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Delete, $"{SchedulesUrl}/{id}", adminB)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Put, $"{SchedulesUrl}/{id}", adminB, Body(to: [Guid.NewGuid()]))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"{SchedulesUrl}/{id}/send-now", adminB)).Status);
        Assert.Single((await GetOk(SchedulesUrl, adminA)).EnumerateArray());
    }

    // ---- send now ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Send_now_emails_each_recipient_a_table_of_the_report_and_a_link_without_moving_the_schedule()
    {
        var (t, admin, _) = await TenantWithPacks();
        var editor = await Provision(t, "Editor", "Edith");
        var librarian = await Provision(t, "Librarian", "Lib");
        var schedule = await Create(admin, Body("kpis", 30, "weekly", 7, dow: 2, to: [editor.Id, librarian.Id]));
        var id = schedule.GetProperty("id").GetGuid();
        var due = schedule.GetProperty("nextRunAt").GetDateTimeOffset();

        var r = await CallWithMail(HttpMethod.Post, $"{SchedulesUrl}/{id}/send-now", admin);
        Assert.Equal(HttpStatusCode.Accepted, r.Status);
        Assert.Equal((2, 0, 0), (r.Body.GetProperty("sent").GetInt32(), r.Body.GetProperty("skipped").GetInt32(), r.Body.GetProperty("failed").GetInt32()));

        Assert.Equal(new[] { editor.Email, librarian.Email }.Order(), _apiMail.Sent.Select(m => m.ToAddress).Order());
        var mail = _apiMail.To(editor.Email).Single();
        Assert.Contains("Key performance indicators", mail.Subject, StringComparison.Ordinal);
        Assert.Contains("Tenant ", mail.Subject, StringComparison.Ordinal);                         // the tenant's name leads the subject
        Assert.Contains("<table", mail.Html, StringComparison.Ordinal);
        Assert.Contains("Publishing effort", mail.Html, StringComparison.Ordinal);
        Assert.Contains("href=\"https://orbit.example.test/insights/reports/kpis?days=30\"", mail.Html, StringComparison.Ordinal);
        Assert.Contains("Open in Orbit: https://orbit.example.test/insights/reports/kpis?days=30", mail.Text, StringComparison.Ordinal);
        Assert.Equal("Edith", mail.ToName);

        var after = await Get(t, admin, id);
        Assert.NotEqual(JsonValueKind.Null, after.GetProperty("lastSentAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("lastError").ValueKind);
        Assert.Equal(due, after.GetProperty("nextRunAt").GetDateTimeOffset());                       // the schedule keeps its place
    }

    [Fact]
    public async Task Send_now_builds_each_recipients_email_from_what_that_recipient_may_see()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var other = await Folder(admin, "Other");
        var mine = await Aged(admin, brand, 100, "Brand hero"); var secret = await Aged(admin, other, 100, "Board minutes");
        await Record(admin, Use(mine, "view", 1), Use(secret, "view", 1), Use(secret, "view", 2));
        var scoped = await ScopedReporter(t, admin, "brand");
        var scopedMe = (await Call(HttpMethod.Get, "/api/v1/me", scoped)).Body;
        var adminMe = (await Call(HttpMethod.Get, "/api/v1/me", admin)).Body;
        var id = (await Create(admin, Body("top-assets", 30, "daily", 6, to: [adminMe.GetProperty("userId").GetGuid(), scopedMe.GetProperty("userId").GetGuid()]))).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Accepted, (await CallWithMail(HttpMethod.Post, $"{SchedulesUrl}/{id}/send-now", admin)).Status);
        var adminMail = _apiMail.To(adminMe.GetProperty("email").GetString()!).Single();
        var scopedMail = _apiMail.To(scopedMe.GetProperty("email").GetString()!).Single();
        Assert.Contains("Board minutes", adminMail.Html, StringComparison.Ordinal);
        Assert.Contains("Brand hero", adminMail.Html, StringComparison.Ordinal);
        Assert.Contains("Brand hero", scopedMail.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("Board minutes", scopedMail.Html, StringComparison.Ordinal);          // not theirs to see
        Assert.DoesNotContain("Board minutes", scopedMail.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_now_leaves_out_recipients_who_cannot_read_analytics_and_says_so()
    {
        var (t, admin, _) = await TenantWithPacks();
        var editor = await Provision(t, "Editor");
        var viewer = await Provision(t, "Viewer");
        var id = (await Create(admin, Body(to: [editor.Id, viewer.Id]))).GetProperty("id").GetGuid();

        var r = await CallWithMail(HttpMethod.Post, $"{SchedulesUrl}/{id}/send-now", admin);
        Assert.Equal(HttpStatusCode.Accepted, r.Status);
        Assert.Equal((1, 1, 0), (r.Body.GetProperty("sent").GetInt32(), r.Body.GetProperty("skipped").GetInt32(), r.Body.GetProperty("failed").GetInt32()));
        Assert.Empty(_apiMail.To(viewer.Email));
        Assert.Contains("Not sent to 1 recipient", (await Get(t, admin, id)).GetProperty("lastError").GetString(), StringComparison.Ordinal);

        var onlyViewer = (await Create(admin, Body(to: [viewer.Id]))).GetProperty("id").GetGuid();
        var none = await CallWithMail(HttpMethod.Post, $"{SchedulesUrl}/{onlyViewer}/send-now", admin);
        Assert.Equal(HttpStatusCode.Conflict, none.Status);
    }

    [Fact]
    public async Task Send_now_without_a_mail_server_is_a_conflict_that_names_the_reason_and_sends_nothing()
    {
        var (t, admin, _) = await TenantWithPacks();
        var editor = await Provision(t, "Editor");
        var id = (await Create(admin, Body(to: [editor.Id]))).GetProperty("id").GetGuid();
        var r = await Call(HttpMethod.Post, $"{SchedulesUrl}/{id}/send-now", admin);      // the default test API has no SMTP host
        Assert.Equal(HttpStatusCode.Conflict, r.Status);
        Assert.Contains("Email is not configured", r.Body.GetProperty("title").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_now_reports_a_recipient_the_mail_server_refused_and_the_others_still_get_it()
    {
        var (t, admin, _) = await TenantWithPacks();
        var good = await Provision(t, "Editor"); var bad = await Provision(t, "Editor");
        _apiMail.Refuse(bad.Email);
        var id = (await Create(admin, Body(to: [good.Id, bad.Id]))).GetProperty("id").GetGuid();
        var r = await CallWithMail(HttpMethod.Post, $"{SchedulesUrl}/{id}/send-now", admin);
        Assert.Equal(HttpStatusCode.Accepted, r.Status);
        Assert.Equal((1, 0, 1), (r.Body.GetProperty("sent").GetInt32(), r.Body.GetProperty("skipped").GetInt32(), r.Body.GetProperty("failed").GetInt32()));
        Assert.Single(_apiMail.To(good.Email));
        Assert.Contains("550", (await Get(t, admin, id)).GetProperty("lastError").GetString(), StringComparison.Ordinal);
    }

    // ---- the job -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_due_schedule_is_sent_once_and_moves_to_its_next_slot()
    {
        var (t, admin, _) = await TenantWithPacks();
        QuietOthers(t);
        var a = await Provision(t, "Editor"); var b = await Provision(t, "Approver");
        var id = (await Create(admin, Body("kpis", 30, "daily", 6, to: [a.Id, b.Id]))).GetProperty("id").GetGuid();
        var mail = new SelectiveEmailSender();
        using var scheduler = AnalyticsScheduler(new FakeClock(DateTimeOffset.UtcNow), email: mail);

        await RunJob(scheduler, ReportSchedulesJob.JobKey);                       // not due yet
        Assert.Empty(mail.Sent);

        MakeDue(id);
        await RunJob(scheduler, ReportSchedulesJob.JobKey);
        await RunJob(scheduler, ReportSchedulesJob.JobKey);                       // a second run finds nothing due
        Assert.Equal(2, mail.Sent.Count(m => m.ToAddress == a.Email || m.ToAddress == b.Email));
        Assert.Single(mail.To(a.Email));
        Assert.Contains("Open in Orbit: https://orbit.example.test/insights/reports/kpis?days=30", mail.To(a.Email)[0].Text, StringComparison.Ordinal);

        var after = await Get(t, admin, id);
        Assert.NotEqual(JsonValueKind.Null, after.GetProperty("lastSentAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("lastError").ValueKind);
        var next = after.GetProperty("nextRunAt").GetDateTimeOffset();
        Assert.True(next > DateTimeOffset.UtcNow && next <= DateTimeOffset.UtcNow.AddDays(1));
        Assert.Equal(6, next.Hour);
        Assert.Equal(0, Count(t, $"SELECT cardinality(sent_to) FROM report_schedules WHERE id = '{id}'"));        // the ledger is cleared for the next slot
    }

    [Fact]
    public async Task A_disabled_schedule_and_one_not_yet_due_send_nothing()
    {
        var (t, admin, _) = await TenantWithPacks();
        QuietOthers(t);
        var a = await Provision(t, "Editor");
        var off = (await Create(admin, Body(to: [a.Id], enabled: false))).GetProperty("id").GetGuid();
        var later = (await Create(admin, Body(to: [a.Id]))).GetProperty("id").GetGuid();
        Sql($"UPDATE report_schedules SET next_run_at = now() - interval '1 hour' WHERE id = '{off}'");     // as if it had been due before it was switched off
        var mail = new SelectiveEmailSender();
        using var scheduler = AnalyticsScheduler(new FakeClock(DateTimeOffset.UtcNow), email: mail);
        await RunJob(scheduler, ReportSchedulesJob.JobKey);
        Assert.Empty(mail.Sent);
        _ = later;
    }

    [Fact]
    public async Task A_failed_send_is_retried_at_the_next_run_and_nobody_is_mailed_twice()
    {
        var (t, admin, _) = await TenantWithPacks();
        QuietOthers(t);
        var a = await Provision(t, "Editor"); var b = await Provision(t, "Editor"); var c = await Provision(t, "Approver");
        var id = (await Create(admin, Body("kpis", 30, "daily", 6, to: [a.Id, b.Id, c.Id]))).GetProperty("id").GetGuid();
        MakeDue(id);
        var mail = new SelectiveEmailSender();
        mail.Refuse(b.Email);
        using var scheduler = AnalyticsScheduler(new FakeClock(DateTimeOffset.UtcNow), email: mail);

        await RunJob(scheduler, ReportSchedulesJob.JobKey);
        Assert.Equal(2, mail.Sent.Count);
        var failing = await Get(t, admin, id);
        Assert.Contains("550", failing.GetProperty("lastError").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, failing.GetProperty("lastSentAt").ValueKind);
        Assert.True(failing.GetProperty("nextRunAt").GetDateTimeOffset() < DateTimeOffset.UtcNow);        // still due: the next run tries again
        Assert.Equal(2, Count(t, $"SELECT cardinality(sent_to) FROM report_schedules WHERE id = '{id}'"));

        await RunJob(scheduler, ReportSchedulesJob.JobKey);                                                 // still refused: those who have it are not mailed again
        Assert.Equal(2, mail.Sent.Count);

        mail.Refuse(b.Email, false);
        await RunJob(scheduler, ReportSchedulesJob.JobKey);
        Assert.Equal(3, mail.Sent.Count);
        Assert.Single(mail.To(a.Email)); Assert.Single(mail.To(b.Email)); Assert.Single(mail.To(c.Email));
        var done = await Get(t, admin, id);
        Assert.Equal(JsonValueKind.Null, done.GetProperty("lastError").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, done.GetProperty("lastSentAt").ValueKind);
        Assert.True(done.GetProperty("nextRunAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task A_scheduler_that_restarts_between_recipients_does_not_mail_them_again()
    {
        var (t, admin, _) = await TenantWithPacks();
        QuietOthers(t);
        var a = await Provision(t, "Editor"); var b = await Provision(t, "Editor");
        var id = (await Create(admin, Body(to: [a.Id, b.Id]))).GetProperty("id").GetGuid();
        MakeDue(id);
        var mail = new SelectiveEmailSender();
        mail.Refuse(b.Email);
        using (var first = AnalyticsScheduler(new FakeClock(DateTimeOffset.UtcNow), email: mail)) await RunJob(first, ReportSchedulesJob.JobKey);
        mail.Refuse(b.Email, false);
        using (var second = AnalyticsScheduler(new FakeClock(DateTimeOffset.UtcNow), email: mail)) await RunJob(second, ReportSchedulesJob.JobKey);
        Assert.Single(mail.To(a.Email));
        Assert.Single(mail.To(b.Email));
    }

    [Fact]
    public async Task Without_a_mail_server_nothing_is_sent_the_schedule_says_why_and_it_sends_once_email_exists()
    {
        var (t, admin, _) = await TenantWithPacks();
        QuietOthers(t);
        var a = await Provision(t, "Editor");
        var id = (await Create(admin, Body(to: [a.Id]))).GetProperty("id").GetGuid();
        MakeDue(id);

        using (var noMail = AnalyticsScheduler(new FakeClock(DateTimeOffset.UtcNow)))                       // no SMTP host: the no-op sender
            await RunJob(noMail, ReportSchedulesJob.JobKey);
        var waiting = await Get(t, admin, id);
        Assert.Contains("Email is not configured", waiting.GetProperty("lastError").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, waiting.GetProperty("lastSentAt").ValueKind);
        Assert.True(waiting.GetProperty("nextRunAt").GetDateTimeOffset() < DateTimeOffset.UtcNow);          // kept: it is sent as soon as mail works

        var mail = new SelectiveEmailSender();
        using (var withMail = AnalyticsScheduler(new FakeClock(DateTimeOffset.UtcNow), email: mail))
            await RunJob(withMail, ReportSchedulesJob.JobKey);
        Assert.Single(mail.To(a.Email));
        Assert.Equal(JsonValueKind.Null, (await Get(t, admin, id)).GetProperty("lastError").ValueKind);
    }

    [Fact]
    public async Task A_slot_that_could_not_be_delivered_for_a_day_is_given_up_and_the_schedule_moves_on()
    {
        var (t, admin, _) = await TenantWithPacks();
        QuietOthers(t);
        var a = await Provision(t, "Editor");
        var id = (await Create(admin, Body(to: [a.Id]))).GetProperty("id").GetGuid();
        MakeDue(id, "25 hours");
        using var scheduler = AnalyticsScheduler(new FakeClock(DateTimeOffset.UtcNow));                       // still no mail server
        await RunJob(scheduler, ReportSchedulesJob.JobKey);
        var s = await Get(t, admin, id);
        Assert.Contains("Gave up", s.GetProperty("lastError").GetString(), StringComparison.Ordinal);
        Assert.True(s.GetProperty("nextRunAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Recipients_who_are_gone_or_lost_access_are_skipped_and_the_run_still_completes()
    {
        var (t, admin, _) = await TenantWithPacks();
        QuietOthers(t);
        var ok = await Provision(t, "Editor"); var viewer = await Provision(t, "Viewer"); var gone = await Provision(t, "Editor");
        var id = (await Create(admin, Body(to: [ok.Id, viewer.Id, gone.Id]))).GetProperty("id").GetGuid();
        Sql($"UPDATE users SET status = 'Disabled' WHERE id = '{gone.Id}'");
        MakeDue(id);
        var mail = new SelectiveEmailSender();
        using var scheduler = AnalyticsScheduler(new FakeClock(DateTimeOffset.UtcNow), email: mail);
        await RunJob(scheduler, ReportSchedulesJob.JobKey);
        Assert.Single(mail.To(ok.Email));
        Assert.Empty(mail.To(viewer.Email));
        Assert.Empty(mail.To(gone.Email));
        var s = await Get(t, admin, id);
        Assert.Contains("Not sent to 2 recipient", s.GetProperty("lastError").GetString(), StringComparison.Ordinal);
        Assert.NotEqual(JsonValueKind.Null, s.GetProperty("lastSentAt").ValueKind);
        Assert.True(s.GetProperty("nextRunAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);            // not retried for ever: nothing will change that
    }

    [Fact]
    public async Task The_job_builds_each_recipients_email_from_what_they_may_see()
    {
        var (t, admin, brand) = await TenantWithPacks();
        QuietOthers(t);
        var other = await Folder(admin, "Other");
        var mine = await Aged(admin, brand, 100, "Brand hero"); var secret = await Aged(admin, other, 100, "Board minutes");
        Sql($"UPDATE assets SET created_at = now() - interval '200 days' WHERE id = '{secret}'");
        await Record(admin, Use(mine, "view", 1), Use(secret, "view", 1));
        var scoped = await ScopedReporter(t, admin, "brand");
        var scopedMe = (await Call(HttpMethod.Get, "/api/v1/me", scoped)).Body;
        var adminMe = (await Call(HttpMethod.Get, "/api/v1/me", admin)).Body;
        var recipients = new[] { adminMe.GetProperty("userId").GetGuid(), scopedMe.GetProperty("userId").GetGuid() };
        var mail = new SelectiveEmailSender();
        using var scheduler = AnalyticsScheduler(new FakeClock(DateTimeOffset.UtcNow), email: mail);

        foreach (var report in new[] { "top-assets", "unused-assets" })
        {
            var id = (await Create(admin, Body(report, report == "unused-assets" ? 1 : 30, "daily", 6, to: recipients))).GetProperty("id").GetGuid();
            Sql($"UPDATE assets SET created_at = now() - interval '100 days' WHERE id = '{secret}' AND false");
            MakeDue(id);
            await RunJob(scheduler, ReportSchedulesJob.JobKey);
        }
        var adminMails = mail.To(adminMe.GetProperty("email").GetString()!);
        var scopedMails = mail.To(scopedMe.GetProperty("email").GetString()!);
        Assert.Equal(2, adminMails.Count);
        Assert.Equal(2, scopedMails.Count);
        Assert.Contains(adminMails, m => m.Html.Contains("Board minutes", StringComparison.Ordinal));
        Assert.All(scopedMails, m => Assert.DoesNotContain("Board minutes", m.Html, StringComparison.Ordinal));
        Assert.All(scopedMails, m => Assert.DoesNotContain(secret.ToString(), m.Text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_report_that_names_no_asset_is_built_once_for_everyone()
    {
        var (t, admin, brand) = await TenantWithPacks();
        QuietOthers(t);
        var hero = await Aged(admin, brand, 100, "Hero"); AddVersion(hero, 4096);
        var people = new[] { await Provision(t, "Editor"), await Provision(t, "Approver"), await Provision(t, "Librarian") };
        var id = (await Create(admin, Body("storage", 30, "daily", 6, to: people.Select(p => p.Id).ToArray()))).GetProperty("id").GetGuid();
        MakeDue(id);
        var mail = new SelectiveEmailSender();
        using var scheduler = AnalyticsScheduler(new FakeClock(DateTimeOffset.UtcNow), email: mail);
        await RunJob(scheduler, ReportSchedulesJob.JobKey);
        Assert.Equal(3, mail.Sent.Count(m => people.Any(p => p.Email == m.ToAddress)));
        Assert.All(people, p => Assert.Contains("4 KB", mail.To(p.Email).Single().Html, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_email_shows_at_most_fifty_rows_and_says_how_many_there_are()
    {
        var (t, admin, brand) = await TenantWithPacks();
        QuietOthers(t);
        var seed = await Aged(admin, brand, 100, "Seed");
        Sql("INSERT INTO assets (id, tenant_id, folder_id, type, status, title, metadata, metadata_schema_version, taxonomy_terms, tags, attributes, metadata_attributes, rights_channels, rights_regions, legal_hold, status_reason, created_at, updated_at) "
            + "SELECT gen_random_uuid(), tenant_id, folder_id, type, status, 'Bulk asset ' || lpad(n::text, 3, '0'), '{}'::jsonb, 0, '{}', '{}', '{}'::jsonb, '{}'::jsonb, '{}', '{}', false, '', created_at - n * interval '1 second', created_at "
            + $"FROM assets, generate_series(1, 120) AS n WHERE id = '{seed}'");
        var me = await Provision(t, "Editor");
        var id = (await Create(admin, Body("unused-assets", 30, "daily", 6, to: [me.Id]))).GetProperty("id").GetGuid();
        MakeDue(id);
        var mail = new SelectiveEmailSender();
        using var scheduler = AnalyticsScheduler(new FakeClock(DateTimeOffset.UtcNow), email: mail);
        await RunJob(scheduler, ReportSchedulesJob.JobKey);
        var html = mail.To(me.Email).Single().Html;
        Assert.Equal(50, html.Split("Bulk asset ").Length - 1 + html.Split(">Seed<").Length - 1);
        Assert.Contains("Showing the first 50 of 121 rows.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_schedule_table_is_private_to_its_tenant()
    {
        var (a, adminA, _) = await TenantWithPacks();
        var (b, _, _) = await TenantWithPacks();
        await Create(adminA, Body(to: [(await Provision(a, "Editor")).Id]));
        Assert.Equal(1, Count(a, "SELECT count(*) FROM report_schedules"));
        Assert.Equal(0, Count(b, "SELECT count(*) FROM report_schedules"));
        Assert.Equal(0, Count(b, $"SELECT count(*) FROM report_schedules WHERE tenant_id = '{a}'"));
    }

    // ---- next-run calculation, end to end ----------------------------------------------------------------------

    [Theory]
    [InlineData("daily", null, null)]
    [InlineData("weekly", 5, null)]
    [InlineData("monthly", null, 31)]
    [InlineData("monthly", null, 1)]
    public async Task The_next_run_returned_is_the_one_the_schedule_rules_compute(string frequency, int? dow, int? dom)
    {
        var (t, admin, _) = await TenantWithPacks();
        var me = await Provision(t, "Editor");
        var before = DateTimeOffset.UtcNow;
        var s = await Create(admin, Body("kpis", 30, frequency, 13, dow, dom, [me.Id]));
        var next = s.GetProperty("nextRunAt").GetDateTimeOffset();
        Assert.InRange(next, ScheduleRules.NextRun(frequency, 13, dow, dom, before), ScheduleRules.NextRun(frequency, 13, dow, dom, DateTimeOffset.UtcNow));
        Assert.Equal(13, next.Hour);
        Assert.Equal(0, next.Minute);
    }
}
