using System.Globalization;
using System.Net;
using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Infrastructure.Scheduling;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Dam.IntegrationTests;

/// <summary>
/// Shared plumbing for the analytics tests (ADR-0037): assets of a chosen age, usage events, the overview, direct SQL as the database owner, and a scheduler host
/// (the partition, rollup and report jobs) on a clock the test controls. The database is shared by every class, so a test uses its own tenant and only asserts about it.
/// </summary>
public abstract class AnalyticsTestBase(PostgresFixture pg) : AssetTestBase(pg)
{
    protected const string EventsUrl = "/api/v1/usage/events";
    protected const string OverviewUrl = "/api/v1/analytics/overview";
    protected const string KpisUrl = "/api/v1/analytics/kpis";
    protected const string ReportsUrl = "/api/v1/analytics/reports";
    protected const string SchedulesUrl = "/api/v1/analytics/report-schedules";

    protected static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>An asset created <paramref name="createdDaysAgo"/> days ago in the given status (assets are born as drafts, so the row is edited).</summary>
    protected async Task<Guid> Aged(string admin, Guid? folder, int createdDaysAgo, string title = "Hero", string status = "published", string type = "image")
    {
        var id = await NewAsset(admin, folder, type, title, allowIncomplete: true);
        Sql($"UPDATE assets SET created_at = now() - interval '{createdDaysAgo} days', status = '{status}' WHERE id = '{id}'");
        return id;
    }

    protected Task<(HttpStatusCode Status, JsonElement Body)> Record(string token, params object[] events) => Call(HttpMethod.Post, EventsUrl, token, new { events });

    protected static object Use(Guid asset, string type = "download", int daysAgo = 0, string? channel = null, string? clientEventId = null) =>
        new { assetId = asset, eventType = type, channel, clientEventId, occurredAt = daysAgo == 0 ? (DateTimeOffset?)null : DateTimeOffset.UtcNow.AddDays(-daysAgo) };

    protected static object UseAt(Guid asset, DateTimeOffset at, string type = "download", string? channel = null, string? clientEventId = null) =>
        new { assetId = asset, eventType = type, channel, clientEventId, occurredAt = (DateTimeOffset?)at };

    protected async Task<JsonElement> GetOk(string path, string token)
    {
        var r = await Call(HttpMethod.Get, path, token);
        Assert.True(r.Status == HttpStatusCode.OK, $"GET {path} answered {(int)r.Status}: {r.Body}");
        return r.Body;
    }

    /// <summary>The numbers of the overview that depend on usage events: everything but the time it was generated.</summary>
    protected async Task<string> UsageView(string token, int days = 30)
    {
        var b = await GetOk($"{OverviewUrl}?days={days}", token);
        return b.GetProperty("usage").GetRawText() + "\n" + b.GetProperty("topAssets").GetRawText();
    }

    /// <summary>Gives an asset a first version of this size without running the upload pipeline.</summary>
    protected void AddVersion(Guid asset, long size) => Sql(
        "WITH v AS (INSERT INTO asset_versions (id, tenant_id, asset_id, version_no, bucket, storage_key, size_bytes, mime, sha256, technical_metadata, change_note, upload_id, created_at) "
        + $"SELECT gen_random_uuid(), tenant_id, id, 1, 'dam-originals', gen_random_uuid()::text, {size}, 'image/png', repeat('a', 64), '{{}}'::jsonb, '', gen_random_uuid(), now() "
        + $"FROM assets WHERE id = '{asset}' RETURNING id, asset_id) UPDATE assets SET current_version_id = v.id FROM v WHERE assets.id = v.asset_id");

    protected async Task<Guid> Folder(string admin, string name, Guid? parent = null) =>
        Id((await Call(HttpMethod.Post, "/api/v1/folders", admin, new { name, parentId = parent })).Body, "folder", "id");

    /// <summary>A user with analytics.read (a custom role) who can read assets only inside the folder with this path.</summary>
    protected async Task<string> ScopedReporter(Guid tenant, string admin, string folderPath, string roleName = "Reporter")
    {
        var token = ApiTestHost.TokenFor(tenant, Guid.NewGuid());
        var userId = (await Call(HttpMethod.Get, "/api/v1/me", token)).Body.GetProperty("userId").GetGuid();
        var role = Id((await Call(HttpMethod.Post, "/api/v1/roles", admin, new { name = roleName, permissions = new[] { "analytics.read" } })).Body, "id");
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, $"/api/v1/users/{userId}/roles", admin, new { ids = new[] { role } })).Status);
        Assert.Equal(HttpStatusCode.Created, (await Call(HttpMethod.Post, "/api/v1/access-rules", admin, new
        { principalType = "user", principalId = userId, scopeType = "folder", scopeValue = folderPath, permissions = new[] { "assets.read" } })).Status);
        return token;
    }

    protected long Scalar(string sql)
    {
        using var c = new NpgsqlConnection(Pg.OwnerConnection);
        c.Open();
        using var cmd = new NpgsqlCommand(sql, c);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    protected string Text(string sql)
    {
        using var c = new NpgsqlConnection(Pg.OwnerConnection);
        c.Open();
        using var cmd = new NpgsqlCommand(sql, c);
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
    }

    /// <summary>The scheduler's analytics jobs (partitions, rollup, scheduled reports) on the fixture database.</summary>
    protected IHost AnalyticsScheduler(IClock clock, int retentionMonths = 0, IEmailSender? email = null)
    {
        var settings = NotifyHosts.Settings(s => { if (retentionMonths > 0) s["DAM_USAGE_RETENTION_MONTHS"] = retentionMonths.ToString(CultureInfo.InvariantCulture); });
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return NotifyHosts.Scheduler(Pg, clock, settings, svc =>
        {
            svc.AddDamAnalyticsJobs(cfg);
            if (email is not null) svc.AddSingleton(email);
        });
    }

    protected static async Task RunJob(IHost host, string key, IReadOnlyDictionary<string, string>? data = null)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var job = scope.ServiceProvider.GetServices<IScheduledJob>().First(j => j.Key == key);
        await job.RunAsync(data ?? new Dictionary<string, string>(), default);
    }

    /// <summary>Rolls up every tenant's finished days that have changed (the job that runs every ten minutes).</summary>
    protected static Task Rollup(IHost host) => RunJob(host, UsageRollupJob.JobKey);

    protected static Task Recheck(IHost host) => RunJob(host, UsageRollupJob.JobKey, new Dictionary<string, string> { ["mode"] = UsageRollupJob.RecheckMode });

    protected static Task Partitions(IHost host) => RunJob(host, UsagePartitionsJob.JobKey);

    protected static string TodayUtc => "(now() AT TIME ZONE 'UTC')::date";
}
