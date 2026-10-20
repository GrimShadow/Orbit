using System.Net;
using System.Text.Json;
using Dam.Infrastructure;
using Dam.Infrastructure.Messaging;
using Dam.Infrastructure.Scheduling;
using Dam.Infrastructure.Storage;
using Dam.UnitTests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dam.IntegrationTests;

[Collection("pg")]
public sealed class RetentionTests(PostgresFixture pg, RabbitFixture mq) : PipelineTestBase(pg, mq), IClassFixture<RabbitFixture>
{
    private static readonly Dictionary<string, string?> NoScan = new() { ["DAM_SCAN_MODE"] = "off" };

    private IHost SchedulerHost()
    {
        var b = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        b.Logging.SetMinimumLevel(LogLevel.Warning);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DAM_S3_ENDPOINT"] = Pg.S3Endpoint, ["DAM_S3_ACCESS_KEY"] = Pg.S3AccessKey, ["DAM_S3_SECRET_KEY"] = Pg.S3SecretKey,
        }).Build();
        b.Services.AddDamWorkerIdentity();
        b.Services.AddDamInfrastructure(Pg.AppConnection);
        b.Services.AddDamStorage(cfg);
        b.Services.AddDamRetentionScheduling(Pg.SystemConnection);
        return b.Build();
    }

    private static async Task RunJobAsync(IHost host, string jobKey)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var job = scope.ServiceProvider.GetServices<Dam.Application.Abstractions.IScheduledJob>().First(j => j.Key == jobKey);
        await job.RunAsync(new Dictionary<string, string>(), default);
    }

    private static Task RunPublishExpireAsync(IHost host) => RunJobAsync(host, PublishExpireJob.JobKey);
    private static Task RunRetentionAsync(IHost host) => RunJobAsync(host, RetentionJob.JobKey);

    private async Task<string> StatusOf(string token, Guid asset) => (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}", token)).Body.GetProperty("status").GetString()!;

    // ---- retention policy CRUD -----------------------------------------------------------------------------------

    [Fact]
    public async Task Retention_policies_can_be_managed_only_by_someone_with_retention_manage()
    {
        var (t, admin, _) = await TenantWithPacks();
        var create = await Call(HttpMethod.Post, "/api/v1/retention-policies", admin, new { name = "Standard", archiveAfterDays = 30, deleteAfterDays = 365 });
        Assert.Equal(HttpStatusCode.Created, create.Status);
        var id = create.Body.GetProperty("id").GetGuid();
        Assert.Equal(30, create.Body.GetProperty("archiveAfterDays").GetInt32());
        Assert.False(create.Body.GetProperty("legalHoldExempt").GetBoolean());

        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "/api/v1/retention-policies", As(t, "Librarian"), new { name = "X" })).Status);

        var got = await Call(HttpMethod.Get, $"/api/v1/retention-policies/{id}", admin);
        Assert.Equal(HttpStatusCode.OK, got.Status);
        Assert.Equal("Standard", got.Body.GetProperty("name").GetString());

        var updated = await Call(HttpMethod.Put, $"/api/v1/retention-policies/{id}", admin, new { name = "Standard v2", archiveAfterDays = 60, deleteAfterDays = 730, legalHoldExempt = true });
        Assert.Equal(HttpStatusCode.OK, updated.Status);
        Assert.Equal(60, updated.Body.GetProperty("archiveAfterDays").GetInt32());

        var list = await Call(HttpMethod.Get, "/api/v1/retention-policies", admin);
        Assert.Contains(list.Body.EnumerateArray(), p => p.GetProperty("id").GetGuid() == id);

        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/retention-policies/{Guid.NewGuid()}", admin)).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/api/v1/retention-policies", admin, new { name = "" })).Status);
    }

    [Fact]
    public async Task A_policy_still_assigned_to_an_asset_cannot_be_deleted()
    {
        var (_, admin, brand) = await TenantWithPacks();
        var policyId = (await Call(HttpMethod.Post, "/api/v1/retention-policies", admin, new { name = "In use" })).Body.GetProperty("id").GetGuid();
        var asset = await NewAsset(admin, brand);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/retention-policy", admin, new { retentionPolicyId = policyId })).Status);

        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Delete, $"/api/v1/retention-policies/{policyId}", admin)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/retention-policy", admin, new { retentionPolicyId = (Guid?)null })).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"/api/v1/retention-policies/{policyId}", admin)).Status);
    }

    [Fact]
    public async Task Assigning_an_unknown_retention_policy_is_refused()
    {
        var (_, admin, brand) = await TenantWithPacks();
        var asset = await NewAsset(admin, brand);
        var r = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/retention-policy", admin, new { retentionPolicyId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status);
    }

    // ---- legal hold -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Legal_hold_can_be_toggled_only_by_someone_with_retention_manage_and_is_audited()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var asset = await NewAsset(admin, brand);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/legal-hold", As(t, "Librarian"), new { hold = true })).Status);

        var r = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/legal-hold", admin, new { hold = true });
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.True(r.Body.GetProperty("legalHold").GetBoolean());
        Assert.Equal(1, Count(t, $"SELECT count(*) FROM audit_log WHERE action = 'asset.legal_hold.changed' AND entity_id = '{asset}'"));

        Assert.False((await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/legal-hold", admin, new { hold = false })).Body.GetProperty("legalHold").GetBoolean());
    }

    // ---- scheduling -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Publish_at_and_expire_at_can_be_set_and_cleared_through_the_schedule_endpoint()
    {
        var (_, admin, brand) = await TenantWithPacks();
        var asset = await NewAsset(admin, brand);
        var publishAt = DateTimeOffset.UtcNow.AddDays(1);
        var r = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/schedule", admin, new { publishAt });
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal(publishAt, r.Body.GetProperty("publishAt").GetDateTimeOffset(), TimeSpan.FromSeconds(1));
        Assert.Equal(JsonValueKind.Null, r.Body.GetProperty("expireAt").ValueKind);

        var cleared = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/schedule", admin, new { publishAt = (DateTimeOffset?)null });
        Assert.Equal(JsonValueKind.Null, cleared.Body.GetProperty("publishAt").ValueKind);
    }

    // ---- the scheduled publish/expire job -------------------------------------------------------------------------

    [Fact]
    public async Task An_approved_asset_publishes_itself_once_its_publish_at_has_passed_and_the_timer_is_then_spent()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var asset = await NewAsset(admin, brand);
        Sql($"UPDATE assets SET status = 'approved' WHERE id = '{asset}'");
        await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/schedule", admin, new { publishAt = DateTimeOffset.UtcNow.AddSeconds(-5) });

        using var host = SchedulerHost();
        await RunPublishExpireAsync(host);

        var a = (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}", admin)).Body;
        Assert.Equal("published", a.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, a.GetProperty("publishAt").ValueKind);
        Assert.Equal(1, Count(t, $"SELECT count(*) FROM audit_log WHERE action = 'asset.status.changed' AND entity_id = '{asset}'"));
    }

    [Fact]
    public async Task A_publish_at_in_the_future_does_not_fire_yet()
    {
        var (_, admin, brand) = await TenantWithPacks();
        var asset = await NewAsset(admin, brand);
        Sql($"UPDATE assets SET status = 'approved' WHERE id = '{asset}'");
        await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/schedule", admin, new { publishAt = DateTimeOffset.UtcNow.AddDays(1) });

        using var host = SchedulerHost();
        await RunPublishExpireAsync(host);
        Assert.Equal("approved", await StatusOf(admin, asset));
    }

    [Fact]
    public async Task A_publish_at_on_an_asset_that_is_not_approved_is_simply_ignored()
    {
        var (_, admin, brand) = await TenantWithPacks();
        var asset = await NewAsset(admin, brand);   // still draft
        Sql($"UPDATE assets SET publish_at = now() - interval '1 minute' WHERE id = '{asset}'");

        using var host = SchedulerHost();
        await RunPublishExpireAsync(host);
        Assert.Equal("draft", await StatusOf(admin, asset));
    }

    [Fact]
    public async Task A_published_asset_expires_itself_once_its_expire_at_has_passed()
    {
        var (_, admin, brand) = await TenantWithPacks();
        var asset = await NewAsset(admin, brand);
        Sql($"UPDATE assets SET status = 'published' WHERE id = '{asset}'");
        await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/schedule", admin, new { expireAt = DateTimeOffset.UtcNow.AddSeconds(-5) });

        using var host = SchedulerHost();
        await RunPublishExpireAsync(host);
        var a = (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}", admin)).Body;
        Assert.Equal("expired", a.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, a.GetProperty("expireAt").ValueKind);
    }

    // ---- the retention job: auto-archive and auto-purge -------------------------------------------------------------

    private static int _picture;
    private async Task<Guid> PublishedAssetWithFile(string admin, Guid folder)
    {
        var asset = await NewAsset(admin, folder);
        using var worker = StartWorker(NoScan, renditions: false); await worker.StartAsync();
        var file = TestImages.Sized(200, 150, radiusFraction: 0.15 + (Interlocked.Increment(ref _picture) % 500) * 0.0005);
        Assert.Equal("ingested", (await Ingest(admin, asset, file, "a.jpg")).GetProperty("status").GetString());
        Sql($"UPDATE assets SET status = 'expired' WHERE id = '{asset}'");
        return asset;
    }

    [Fact]
    public async Task An_expired_asset_is_archived_once_its_policys_archive_after_days_has_passed()
    {
        var (_, admin, brand) = await TenantWithPacks();
        var asset = await PublishedAssetWithFile(admin, brand);
        var policyId = (await Call(HttpMethod.Post, "/api/v1/retention-policies", admin, new { name = "Auto-archive", archiveAfterDays = 7 })).Body.GetProperty("id").GetGuid();
        await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/retention-policy", admin, new { retentionPolicyId = policyId });
        Sql($"UPDATE assets SET updated_at = now() - interval '8 days' WHERE id = '{asset}'");

        using var host = SchedulerHost();
        await RunRetentionAsync(host);
        Assert.Equal("archived", await StatusOf(admin, asset));
    }

    [Fact]
    public async Task An_expired_asset_not_yet_past_archive_after_days_is_left_alone()
    {
        var (_, admin, brand) = await TenantWithPacks();
        var asset = await PublishedAssetWithFile(admin, brand);
        var policyId = (await Call(HttpMethod.Post, "/api/v1/retention-policies", admin, new { name = "Auto-archive", archiveAfterDays = 30 })).Body.GetProperty("id").GetGuid();
        await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/retention-policy", admin, new { retentionPolicyId = policyId });
        Sql($"UPDATE assets SET updated_at = now() - interval '1 day' WHERE id = '{asset}'");

        using var host = SchedulerHost();
        await RunRetentionAsync(host);
        Assert.Equal("expired", await StatusOf(admin, asset));
    }

    [Fact]
    public async Task An_archived_asset_is_purged_and_its_file_bytes_actually_removed_once_delete_after_days_has_passed()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var asset = await PublishedAssetWithFile(admin, brand);
        var policyId = (await Call(HttpMethod.Post, "/api/v1/retention-policies", admin, new { name = "Auto-purge", deleteAfterDays = 7 })).Body.GetProperty("id").GetGuid();
        await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/retention-policy", admin, new { retentionPolicyId = policyId });
        var version = (await Versions(admin, asset))[0];
        var versionId = version.GetProperty("id").GetGuid();
        Assert.True(await InStore("dam-originals", $"{t:N}/{asset:N}/{versionId:N}"));

        // What the AI derived from the file (vector, extracted text, suggestions, run) goes with it (ADR-0035).
        Sql($"""
            INSERT INTO asset_embeddings (tenant_id, asset_id, version_id, kind, dim, model, vector, created_at) VALUES ('{t}', '{asset}', '{versionId}', 'text', 1, 'm', '\x0000803f', now());
            INSERT INTO asset_extracts (tenant_id, asset_id, version_id, ocr_text, transcript_segments, model, created_at, updated_at) VALUES ('{t}', '{asset}', '{versionId}', 'text', '[]', jsonb_build_object(), now(), now());
            INSERT INTO ai_suggestions (id, tenant_id, asset_id, version_id, kind, value, confidence, model, status, created_at) VALUES ('{Guid.NewGuid()}', '{t}', '{asset}', '{versionId}', 'tag', 'x', 0.5, 'm', 'pending', now());
            INSERT INTO ai_runs (id, tenant_id, asset_id, version_id, status, steps, error, attempts, created_at) VALUES ('{Guid.NewGuid()}', '{t}', '{asset}', '{versionId}', 'succeeded', '[]', '', 1, now());
            """);

        Sql($"UPDATE assets SET status = 'archived', updated_at = now() - interval '8 days' WHERE id = '{asset}'");
        using var host = SchedulerHost();
        await RunRetentionAsync(host);

        Assert.Equal("deleted", await StatusOf(admin, asset));
        Assert.False(await InStore("dam-originals", $"{t:N}/{asset:N}/{versionId:N}"));
        foreach (var table in new[] { "asset_embeddings", "asset_extracts", "ai_suggestions", "ai_runs" }) Assert.Equal(0, Count(t, $"SELECT count(*) FROM {table}"));
        Assert.Equal(1, Count(t, $"SELECT count(*) FROM audit_log WHERE action = 'asset.status.changed' AND entity_id = '{asset}' AND after->>'To' = 'deleted'"));
    }

    [Fact]
    public async Task A_legal_hold_blocks_purge_but_a_legal_hold_exempt_policy_purges_through_it_anyway()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var held = await PublishedAssetWithFile(admin, brand);
        var normalPolicy = (await Call(HttpMethod.Post, "/api/v1/retention-policies", admin, new { name = "Normal", deleteAfterDays = 7 })).Body.GetProperty("id").GetGuid();
        await Call(HttpMethod.Post, $"/api/v1/assets/{held}/retention-policy", admin, new { retentionPolicyId = normalPolicy });
        await Call(HttpMethod.Post, $"/api/v1/assets/{held}/legal-hold", admin, new { hold = true });
        Sql($"UPDATE assets SET status = 'archived', updated_at = now() - interval '8 days' WHERE id = '{held}'");

        using var host = SchedulerHost();
        await RunRetentionAsync(host);
        Assert.Equal("archived", await StatusOf(admin, held));   // still there: the hold won

        var exempt = await PublishedAssetWithFile(admin, brand);
        var exemptPolicy = (await Call(HttpMethod.Post, "/api/v1/retention-policies", admin, new { name = "Exempt", deleteAfterDays = 7, legalHoldExempt = true })).Body.GetProperty("id").GetGuid();
        await Call(HttpMethod.Post, $"/api/v1/assets/{exempt}/retention-policy", admin, new { retentionPolicyId = exemptPolicy });
        await Call(HttpMethod.Post, $"/api/v1/assets/{exempt}/legal-hold", admin, new { hold = true });
        Sql($"UPDATE assets SET status = 'archived', updated_at = now() - interval '8 days' WHERE id = '{exempt}'");

        await RunRetentionAsync(host);
        Assert.Equal("deleted", await StatusOf(admin, exempt));   // the policy's own exemption overrides the hold

        Assert.Equal(1, Count(t, "SELECT count(*) FROM assets WHERE status = 'archived'"));
    }
}
