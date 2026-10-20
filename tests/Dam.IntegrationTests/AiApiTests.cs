using System.Net;
using System.Text.Json;
using Dam.Domain.Ai;
using Dam.Infrastructure;
using Dam.Infrastructure.Ai;
using Dam.Infrastructure.Messaging;
using Dam.Infrastructure.Scheduling;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Dam.Domain.Authorization;
using Npgsql;

namespace Dam.IntegrationTests;

/// <summary>The AI endpoints over the real HTTP API (settings, status, test, backfill, suggestions), with a fake sidecar. No worker: suggestions are written directly.</summary>
[Collection("pg")]
public class AiApiTests(PostgresFixture pg, SearchFixture os) : AssetTestBase(pg), IClassFixture<SearchFixture>
{
    protected FakeSidecar Sidecar { get; private set; } = null!;
    protected override string? OpenSearchUrl => os.Url;

    protected override IReadOnlyDictionary<string, string?>? ApiEnvironment => new Dictionary<string, string?>
    {
        ["DAM_AI_PROVIDER"] = "self-hosted", ["DAM_AI_SIDECAR_URL"] = Sidecar.Url,
    };

    public override async Task InitializeAsync()
    {
        Sidecar = await FakeSidecar.StartAsync();
        await base.InitializeAsync();
    }

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await Sidecar.DisposeAsync();
    }

    private async Task<JsonElement> Put(string token, object body)
    {
        var r = await Call(HttpMethod.Put, "/api/v1/ai/settings", token, body);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        return r.Body;
    }

    /// <summary>An asset in a tenant without schemas (or with them): no metadata, which a draft may do.</summary>
    protected Task<Guid> Plain(string token, string type = "image", string[]? tags = null) => NewAsset(token, null, type, metadata: new { }, tags: tags, allowIncomplete: true);

    /// <summary>Gives an asset a "file" (a current version id, with no bytes behind it), so suggestions for it can be decided.</summary>
    protected Guid GiveVersion(Guid asset)
    {
        var version = Guid.NewGuid();
        Sql($"UPDATE assets SET current_version_id = '{version}' WHERE id = '{asset}'");
        return version;
    }

    protected async Task<Guid> Suggest(Guid tenant, Guid asset, Guid version, string kind, string value, float confidence = 0.8f)
    {
        await using var db = Pg.AppContext(new TestTenant(tenant));
        var s = AiSuggestion.Create(tenant, asset, version, kind, value, confidence, "test-model", DateTimeOffset.UtcNow);
        db.AiSuggestions.Add(s);
        await db.SaveChangesAsync();
        return s.Id;
    }

    // ---- settings ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_tenant_starts_with_ai_off_whatever_the_deployment_default_is()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var s = (await Call(HttpMethod.Get, "/api/v1/ai/settings", admin)).Body;
        Assert.False(s.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, s.GetProperty("provider").ValueKind);
        Assert.Equal("self-hosted", s.GetProperty("defaultProvider").GetString());
        Assert.Equal("self-hosted", s.GetProperty("effectiveProvider").GetString());
        Assert.False(s.GetProperty("sendsDataOffSite").GetBoolean());
        Assert.True(s.GetProperty("features").GetProperty("embeddings").GetBoolean());
        var providers = s.GetProperty("providers").EnumerateArray().ToDictionary(p => p.GetProperty("kind").GetString()!, p => p.GetProperty("configured").GetBoolean());
        Assert.Equal((true, false, true), (providers["self-hosted"], providers["openai-compatible"], providers["none"]));
    }

    [Fact]
    public async Task Settings_are_saved_audited_and_isolated_between_tenants()
    {
        var a = await NewTenantAsync(); var b = await NewTenantAsync();
        var saved = await Put(Admin(a), new { enabled = true, features = new { tags = true, ocr = false, transcribe = true, embeddings = true }, ocrLanguage = "HI" });
        Assert.True(saved.GetProperty("enabled").GetBoolean());
        Assert.False(saved.GetProperty("features").GetProperty("ocr").GetBoolean());
        Assert.Equal("hi", saved.GetProperty("ocrLanguage").GetString());
        Assert.NotEqual(JsonValueKind.Null, saved.GetProperty("updatedBy").ValueKind);

        Assert.Equal(1, Count(a, "SELECT count(*) FROM audit_log WHERE action = 'ai.settings.changed'"));
        Assert.Equal(1, Count(a, "SELECT count(*) FROM ai_settings"));
        Assert.False((await Call(HttpMethod.Get, "/api/v1/ai/settings", Admin(b))).Body.GetProperty("enabled").GetBoolean());
        Assert.Equal(0, Count(b, "SELECT count(*) FROM ai_settings"));      // row-level security: tenant B sees nothing of A's

        await Put(Admin(a), new { enabled = false });                       // a replacement, not a patch
        var again = (await Call(HttpMethod.Get, "/api/v1/ai/settings", Admin(a))).Body;
        Assert.False(again.GetProperty("enabled").GetBoolean());
        Assert.True(again.GetProperty("features").GetProperty("ocr").GetBoolean());
        Assert.Equal(1, Count(a, "SELECT count(*) FROM ai_settings"));
    }

    [Fact]
    public async Task Bad_settings_are_refused_field_by_field()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        async Task<JsonElement> Errors(object body)
        {
            var r = await Call(HttpMethod.Put, "/api/v1/ai/settings", admin, body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status);
            return r.Body.GetProperty("errors");
        }
        Assert.True((await Errors(new { enabled = true, provider = "gemini" })).TryGetProperty("provider", out _));
        Assert.True((await Errors(new { enabled = false, ocrLanguage = "not a language" })).TryGetProperty("ocrLanguage", out _));
        Assert.Contains("not configured", (await Errors(new { enabled = true, provider = "openai-compatible" })).GetProperty("provider")[0].GetString());
        Assert.Contains("Choose a provider", (await Errors(new { enabled = true, provider = "none" })).GetProperty("provider")[0].GetString());
        Assert.Equal(0, Count(t, "SELECT count(*) FROM ai_settings"));
    }

    [Fact]
    public async Task Only_administrators_manage_ai_and_reading_results_needs_only_read_access()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        foreach (var role in new[] { "Librarian", "Editor", "Approver", "Contributor", "Viewer", "Dealer", "ApiClient" })
        {
            var token = As(t, role);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, "/api/v1/ai/settings", token)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Put, "/api/v1/ai/settings", token, new { enabled = false })).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, "/api/v1/ai/status", token)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "/api/v1/ai/backfill", token, new { })).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "/api/v1/ai/test", token)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "/api/v1/ai/reindex", token)).Status);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(HttpMethod.Get, "/api/v1/ai/settings", "not-a-token")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, "/api/v1/ai/settings", admin)).Status);
        Assert.Contains("ai.manage", (await Call(HttpMethod.Get, "/api/v1/permissions", admin)).Body.EnumerateArray().Select(p => p.GetString()));
    }

    // ---- capabilities (any signed-in user) ----------------------------------------------------------------------------

    [Fact]
    public async Task Any_signed_in_user_can_ask_what_ai_can_do_and_the_answer_follows_the_settings_and_leaks_nothing()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var viewer = As(t, "Viewer");
        async Task<JsonElement> Caps(string token) { var r = await Call(HttpMethod.Get, "/api/v1/ai/capabilities", token); Assert.Equal(HttpStatusCode.OK, r.Status); return r.Body; }

        var off = await Caps(viewer);
        Assert.Equal(("self-hosted", false, false, false, false, false, false),
            (off.GetProperty("provider").GetString(), off.GetProperty("enabled").GetBoolean(), off.GetProperty("semanticSearch").GetBoolean(), off.GetProperty("enrichment").GetBoolean(),
             off.GetProperty("textVectors").GetBoolean(), off.GetProperty("imageVectors").GetBoolean(), off.GetProperty("sendsDataOffSite").GetBoolean()));

        await Put(admin, new { enabled = true });
        var on = await Caps(viewer);
        Assert.All(new[] { "enabled", "semanticSearch", "enrichment", "textVectors", "imageVectors" }, k => Assert.True(on.GetProperty(k).GetBoolean(), k));
        Assert.False(on.GetProperty("sendsDataOffSite").GetBoolean());
        Assert.DoesNotContain(Sidecar.Url, on.GetRawText());                    // no addresses, keys or models
        Assert.Equal(["enabled", "enrichment", "imageVectors", "provider", "semanticSearch", "sendsDataOffSite", "summarise", "textVectors", "translate"], on.EnumerateObject().Select(p => p.Name).Order());
        // The self-hosted provider summarises on its own (extractive) but cannot translate yet; summaries can be switched off.
        Assert.Equal((false, true), (on.GetProperty("translate").GetBoolean(), on.GetProperty("summarise").GetBoolean()));

        await Put(admin, new { enabled = true, features = new { tags = true, ocr = false, transcribe = false, embeddings = false } });
        var noVectors = await Caps(viewer);
        Assert.Equal((true, true, false, false, false), (noVectors.GetProperty("enabled").GetBoolean(), noVectors.GetProperty("enrichment").GetBoolean(), noVectors.GetProperty("semanticSearch").GetBoolean(),
            noVectors.GetProperty("textVectors").GetBoolean(), noVectors.GetProperty("imageVectors").GetBoolean()));

        await Put(admin, new { enabled = true, features = new { tags = false, ocr = false, transcribe = false, embeddings = false } });
        Assert.True((await Caps(viewer)).GetProperty("enrichment").GetBoolean());       // summaries are still on
        await Put(admin, new { enabled = true, features = new { tags = false, ocr = false, transcribe = false, embeddings = false, summaries = false } });
        var allOff = await Caps(viewer);
        Assert.Equal((false, false), (allOff.GetProperty("enrichment").GetBoolean(), allOff.GetProperty("summarise").GetBoolean()));

        await Put(admin, new { enabled = false });
        Assert.False((await Caps(viewer)).GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(HttpMethod.Get, "/api/v1/ai/capabilities", "not-a-token")).Status);

        // another tenant's settings do not leak into this one's answer
        var other = await NewTenantAsync();
        await Put(Admin(other), new { enabled = true });
        Assert.False((await Caps(viewer)).GetProperty("enabled").GetBoolean());
        Assert.True((await Caps(As(other, "Viewer"))).GetProperty("enabled").GetBoolean());
    }

    // ---- status, test, backfill, reindex -------------------------------------------------------------------------

    [Fact]
    public async Task Status_probes_the_provider_and_counts_what_has_been_enriched()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var a = await Plain(admin); var b = await Plain(admin);
        var va = GiveVersion(a); GiveVersion(b);
        Sql($"INSERT INTO ai_runs (id, tenant_id, asset_id, version_id, status, steps, error, attempts, created_at) VALUES ('{Guid.NewGuid()}', '{t}', '{a}', '{va}', 'succeeded', '[]', '', 1, now())");

        var s = (await Call(HttpMethod.Get, "/api/v1/ai/status", admin)).Body;
        Assert.False(s.GetProperty("enabled").GetBoolean());
        Assert.Equal("self-hosted", s.GetProperty("provider").GetString());
        Assert.Equal("ok", s.GetProperty("health").GetProperty("status").GetString());
        Assert.Contains("clip-fake", s.GetProperty("health").GetProperty("models").GetProperty("clip").GetString());
        Assert.True(s.GetProperty("capabilities").GetProperty("imageEmbeddings").GetBoolean());
        Assert.False(s.GetProperty("capabilities").GetProperty("translate").GetBoolean());
        var counts = s.GetProperty("counts");
        Assert.Equal((2, 1, 0, 0, 1), (counts.GetProperty("eligible").GetInt32(), counts.GetProperty("enriched").GetInt32(), counts.GetProperty("pending").GetInt32(),
            counts.GetProperty("failed").GetInt32(), counts.GetProperty("notStarted").GetInt32()));
        Assert.Equal(0, s.GetProperty("queueDepth").GetInt32());
        var index = s.GetProperty("vectorIndex");
        Assert.Equal((384, 512, false), (index.GetProperty("textDim").GetInt32(), index.GetProperty("imageDim").GetInt32(), index.GetProperty("needsReindex").GetBoolean()));
    }

    [Fact]
    public async Task The_test_endpoint_embeds_a_picture_and_a_phrase_and_times_each_step_without_enabling_ai()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);   // never enabled: the test sends nothing of the tenant's
        var r = (await Call(HttpMethod.Post, "/api/v1/ai/test", admin)).Body;
        Assert.True(r.GetProperty("ok").GetBoolean());
        Assert.Equal("self-hosted", r.GetProperty("provider").GetString());
        var steps = r.GetProperty("steps").EnumerateArray().ToDictionary(s => s.GetProperty("name").GetString()!);
        Assert.Equal(["embed-image", "embed-text", "embed-text-for-images", "health"], steps.Keys.Order());
        Assert.All(steps.Values, s => Assert.True(s.GetProperty("ok").GetBoolean()));
        Assert.Contains("512 dimensions", steps["embed-image"].GetProperty("detail").GetString());
        Assert.Contains("384 dimensions", steps["embed-text"].GetProperty("detail").GetString());
        Assert.True(r.GetProperty("totalMs").GetInt32() >= 0);
        var pictureUpload = Sidecar.Uploads.Single(u => u.Path == "/v1/embed/image");
        Assert.Equal("png", pictureUpload.Format);
        Assert.True(pictureUpload.Length < 1000);        // a tiny built-in picture, nothing from the library

        Sidecar.Down = true;
        var down = (await Call(HttpMethod.Post, "/api/v1/ai/test", admin)).Body;
        Assert.False(down.GetProperty("ok").GetBoolean());
        Assert.Contains(down.GetProperty("steps").EnumerateArray(), s => !s.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Sizes_that_do_not_match_the_configuration_fail_the_test_with_a_clear_message()
    {
        var t = await NewTenantAsync();
        Sidecar.TextDim = 768;
        var r = (await Call(HttpMethod.Post, "/api/v1/ai/test", Admin(t))).Body;
        Assert.False(r.GetProperty("ok").GetBoolean());
        var failed = r.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "embed-text");
        Assert.Contains("DAM_AI_TEXT_DIM", failed.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Backfill_and_rerun_refuse_to_start_while_ai_is_off_and_reindex_is_queued_as_an_event()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var asset = await Plain(admin); GiveVersion(asset);
        var backfill = await Call(HttpMethod.Post, "/api/v1/ai/backfill", admin, new { });
        Assert.Equal(HttpStatusCode.Conflict, backfill.Status);
        Assert.Contains("not enabled", backfill.Body.GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/ai/enrich", admin)).Status);
        Assert.Equal(0, Count(t, "SELECT count(*) FROM outbox WHERE event_type = 'asset.ai.requested'"));

        var reindex = await Call(HttpMethod.Post, "/api/v1/ai/reindex", admin);
        Assert.Equal(HttpStatusCode.Accepted, reindex.Status);
        Assert.Equal(1, Count(t, "SELECT count(*) FROM outbox WHERE event_type = 'search.reindex.requested'"));
    }

    [Fact]
    public async Task Backfill_queues_assets_in_chunks_marks_them_pending_and_skips_what_is_already_queued()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await Put(admin, new { enabled = true });
        var images = new List<Guid>();
        for (var i = 0; i < 3; i++) { var a = await Plain(admin, "image"); GiveVersion(a); images.Add(a); }
        var video = await Plain(admin, "video"); GiveVersion(video);
        var other = await Plain(admin, "other"); GiveVersion(other);       // not a type that is analysed by default
        await Plain(admin, "image");                                        // no file yet: nothing to analyse

        var first = (await Call(HttpMethod.Post, "/api/v1/ai/backfill", admin, new { limit = 2 })).Body;
        Assert.Equal((4, 2, 2), (first.GetProperty("matched").GetInt32(), first.GetProperty("enqueued").GetInt32(), first.GetProperty("remaining").GetInt32()));
        Assert.Equal(2, Count(t, "SELECT count(*) FROM ai_runs WHERE status = 'pending'"));
        Assert.Equal(2, Count(t, "SELECT count(*) FROM outbox WHERE event_type = 'asset.ai.requested'"));

        var second = (await Call(HttpMethod.Post, "/api/v1/ai/backfill", admin, new { limit = 10 })).Body;
        Assert.Equal((2, 2, 0), (second.GetProperty("matched").GetInt32(), second.GetProperty("enqueued").GetInt32(), second.GetProperty("remaining").GetInt32()));
        var third = (await Call(HttpMethod.Post, "/api/v1/ai/backfill", admin, new { })).Body;
        Assert.Equal(0, third.GetProperty("enqueued").GetInt32());                  // all queued already

        var again = (await Call(HttpMethod.Post, "/api/v1/ai/backfill", admin, new { onlyMissing = false, types = new[] { "video" } })).Body;
        Assert.Equal(1, again.GetProperty("enqueued").GetInt32());                  // a filter, and a deliberate re-run
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/api/v1/ai/backfill", admin, new { types = new[] { "hologram" } })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/api/v1/ai/backfill", admin, new { limit = 0 })).Status);

        var status = (await Call(HttpMethod.Get, "/api/v1/ai/status", admin)).Body;
        Assert.Equal(4, status.GetProperty("counts").GetProperty("pending").GetInt32());
        Assert.Equal(4, status.GetProperty("queueDepth").GetInt32());
    }

    [Fact]
    public async Task The_scheduled_backfill_job_queues_work_for_tenants_that_have_ai_on_and_nobody_else()
    {
        var on = await NewTenantAsync(); var off = await NewTenantAsync();
        await Put(Admin(on), new { enabled = true });
        var queued = new List<Guid>();
        for (var i = 0; i < 3; i++) { var a = await Plain(Admin(on)); GiveVersion(a); queued.Add(a); }
        var idle = await Plain(Admin(off)); GiveVersion(idle);

        var b = Host.CreateApplicationBuilder();
        b.Logging.SetMinimumLevel(LogLevel.Warning);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DAM_AI_PROVIDER"] = "self-hosted", ["DAM_AI_SIDECAR_URL"] = Sidecar.Url, ["DAM_AI_BACKFILL_CHUNK"] = "2" }).Build();
        b.Services.AddDamWorkerIdentity();
        b.Services.AddDamInfrastructure(Pg.AppConnection);
        b.Services.AddSingleton(new NpgsqlDataSourceBuilder(Pg.SystemConnection).Build());   // the read-only cross-tenant connection the scheduler hosts register
        b.Services.AddDamAiScheduling(cfg);
        using var host = b.Build();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var job = scope.ServiceProvider.GetServices<Dam.Application.Abstractions.IScheduledJob>().Single(j => j.Key == "ai.backfill");
            await job.RunAsync(new Dictionary<string, string>(), default);     // no tenant named: every tenant with AI on, in chunks of two
        }
        Assert.Equal(3, Count(on, "SELECT count(*) FROM ai_runs WHERE status = 'pending'"));
        Assert.Equal(3, Count(on, "SELECT count(*) FROM outbox WHERE event_type = 'asset.ai.requested'"));
        Assert.Equal(0, Count(off, "SELECT count(*) FROM ai_runs"));
        Assert.Equal(0, Count(off, "SELECT count(*) FROM outbox WHERE event_type = 'asset.ai.requested'"));

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var job = scope.ServiceProvider.GetServices<Dam.Application.Abstractions.IScheduledJob>().Single(j => j.Key == "ai.backfill");
            await job.RunAsync(new Dictionary<string, string> { ["tenantId"] = off.ToString() }, default);   // asked for by hand, but AI is off there
            await job.RunAsync(new Dictionary<string, string> { ["tenantId"] = on.ToString() }, default);    // nothing left to queue
        }
        Assert.Equal(0, Count(off, "SELECT count(*) FROM ai_runs"));
        Assert.Equal(3, Count(on, "SELECT count(*) FROM outbox WHERE event_type = 'asset.ai.requested'"));
    }

    [Fact]
    public async Task Rerun_queues_one_asset_marks_it_pending_and_refuses_a_second_run_while_one_is_going()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await Put(admin, new { enabled = true });
        var asset = await Plain(admin); GiveVersion(asset);
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"/api/v1/assets/{await Plain(admin)}/ai/enrich", admin)).Status);   // no file

        var r = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/ai/enrich", admin);
        Assert.Equal(HttpStatusCode.Accepted, r.Status);
        Assert.Equal("pending", r.Body.GetProperty("status").GetString());
        Assert.Equal(1, Count(t, "SELECT count(*) FROM outbox WHERE event_type = 'asset.ai.requested'"));
        Assert.Equal("pending", (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/ai", admin)).Body.GetProperty("status").GetString());

        Sql($"UPDATE ai_runs SET status = 'running' WHERE asset_id = '{asset}'");
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/ai/enrich", admin)).Status);
    }

    // ---- an asset's results and deciding on suggestions ------------------------------------------------------------

    [Fact]
    public async Task An_asset_with_no_results_reports_none_or_disabled()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var asset = await Plain(admin); GiveVersion(asset);
        var off = (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/ai", admin)).Body;
        Assert.Equal("disabled", off.GetProperty("status").GetString());
        Assert.False(off.GetProperty("embeddingsReady").GetBoolean());
        Assert.Equal(0, off.GetProperty("suggestions").GetArrayLength());

        await Put(admin, new { enabled = true });
        Assert.Equal("none", (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/ai", admin)).Body.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/assets/{Guid.NewGuid()}/ai", admin)).Status);
    }

    [Fact]
    public async Task Accepting_a_tag_adds_it_to_the_asset_through_the_normal_path_with_audit_and_refuses_a_second_decision()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await Put(admin, new { enabled = true });
        var asset = await Plain(admin, tags: ["existing"]); var v = GiveVersion(asset);
        var outdoor = await Suggest(t, asset, v, "tag", "outdoor", 0.9f);
        var travel = await Suggest(t, asset, v, "tag", "travel", 0.5f);

        var list = (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/ai", admin)).Body.GetProperty("suggestions");
        Assert.Equal(["outdoor", "travel"], list.EnumerateArray().Select(s => s.GetProperty("value").GetString()!));   // best first
        Assert.All(list.EnumerateArray(), s => Assert.Equal("pending", s.GetProperty("status").GetString()));

        var updatesBefore = Count(t, $"SELECT count(*) FROM audit_log WHERE action = 'asset.metadata.updated' AND entity_id = '{asset}'");
        var outboxBefore = Count(t, $"SELECT count(*) FROM outbox WHERE event_type = 'asset.metadata.updated' AND payload->>'AssetId' = '{asset}'");
        var accepted = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/ai/suggestions/{outdoor}/accept", admin);
        Assert.Equal(HttpStatusCode.OK, accepted.Status);
        Assert.Equal(["existing", "outdoor"], Strings(accepted.Body.GetProperty("tags")));
        Assert.Equal(1, accepted.Body.GetProperty("changed").GetInt32());
        Assert.Equal(["existing", "outdoor"], Strings((await Call(HttpMethod.Get, $"/api/v1/assets/{asset}", admin)).Body.GetProperty("tags")));
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'asset.ai.suggestion.decided' AND after::text LIKE '%accepted%'"));
        // The tag landed through the asset's own change event: audited, delivered to the outbox (so the index and webhooks see it).
        Assert.Equal(updatesBefore + 1, Count(t, $"SELECT count(*) FROM audit_log WHERE action = 'asset.metadata.updated' AND entity_id = '{asset}'"));
        Assert.Equal(outboxBefore + 1, Count(t, $"SELECT count(*) FROM outbox WHERE event_type = 'asset.metadata.updated' AND payload->>'AssetId' = '{asset}'"));

        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/ai/suggestions/{outdoor}/accept", admin)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/ai/suggestions/{outdoor}/reject", admin)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/ai/suggestions/{Guid.NewGuid()}/accept", admin)).Status);

        var rejected = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/ai/suggestions/{travel}/reject", admin);
        Assert.Equal(HttpStatusCode.OK, rejected.Status);
        Assert.Equal(["existing", "outdoor"], Strings(rejected.Body.GetProperty("tags")));   // a rejection changes nothing on the asset
        var final = (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/ai", admin)).Body.GetProperty("suggestions").EnumerateArray()
            .ToDictionary(s => s.GetProperty("value").GetString()!, s => s.GetProperty("status").GetString());
        Assert.Equal(("accepted", "rejected"), (final["outdoor"], final["travel"]));
        Assert.NotEqual(JsonValueKind.Null, (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/ai", admin)).Body.GetProperty("suggestions")[0].GetProperty("decidedBy").ValueKind);
    }

    [Fact]
    public async Task Accept_all_takes_every_pending_tag_and_stops_at_the_tag_limit()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await Put(admin, new { enabled = true });
        var asset = await Plain(admin, tags: Enumerable.Range(0, 48).Select(i => $"t{i}").ToArray()); var v = GiveVersion(asset);
        foreach (var tag in new[] { "alpha", "beta", "gamma" }) await Suggest(t, asset, v, "tag", tag);
        await Suggest(t, asset, v, "language", "en");

        var r = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/ai/suggestions/accept-all?kind=tag", admin);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal(2, r.Body.GetProperty("changed").GetInt32());                  // only two fit under 50 tags
        Assert.Equal(50, r.Body.GetProperty("tags").GetArrayLength());
        Assert.Contains("left pending", r.Body.GetProperty("notes")[0].GetString());
        var statuses = r.Body.GetProperty("suggestions").EnumerateArray().ToDictionary(s => s.GetProperty("value").GetString()!, s => s.GetProperty("status").GetString());
        Assert.Equal(("accepted", "accepted", "pending"), (statuses["alpha"], statuses["beta"], statuses["gamma"]));
        var language = (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/ai", admin)).Body.GetProperty("suggestions").EnumerateArray().Single(x => x.GetProperty("kind").GetString() == "language");
        Assert.Equal("pending", language.GetProperty("status").GetString());          // the language was not asked for
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/ai/suggestions/accept-all?kind=colour", admin)).Status);
    }

    [Fact]
    public async Task Accepting_a_language_sets_the_language_field_when_the_schema_has_one_and_says_so_when_it_cannot()
    {
        var t = await NewTenantAsync("core"); var admin = Admin(t);
        await Put(admin, new { enabled = true });
        var asset = await Plain(admin); var v = GiveVersion(asset);
        var en = await Suggest(t, asset, v, "language", "en");

        var r = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/ai/suggestions/{en}/accept", admin);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal(0, r.Body.GetProperty("notes").GetArrayLength());
        Assert.Equal(["en"], Strings((await Call(HttpMethod.Get, $"/api/v1/assets/{asset}", admin)).Body.GetProperty("metadata").GetProperty("language")));

        var klingon = await Suggest(t, asset, v, "language", "tlh");                 // not a term of the tenant's language vocabulary
        var refused = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/ai/suggestions/{klingon}/accept", admin);
        Assert.Equal(HttpStatusCode.OK, refused.Status);
        Assert.Contains("not applied", refused.Body.GetProperty("notes")[0].GetString());
        Assert.Equal(["en"], Strings((await Call(HttpMethod.Get, $"/api/v1/assets/{asset}", admin)).Body.GetProperty("metadata").GetProperty("language")));

        // A tenant whose image schema has no language field: the acceptance is recorded, nothing else changes.
        var bare = await NewTenantAsync(); var bareAdmin = Admin(bare);
        await Put(bareAdmin, new { enabled = true });
        var plain = await Plain(bareAdmin); var pv = GiveVersion(plain);
        var s = await Suggest(bare, plain, pv, "language", "en");
        var note = (await Call(HttpMethod.Post, $"/api/v1/assets/{plain}/ai/suggestions/{s}/accept", bareAdmin)).Body.GetProperty("notes")[0].GetString();
        Assert.Contains("no 'language' field", note);
    }

    [Fact]
    public async Task Deciding_needs_update_rights_and_suggestions_of_an_older_version_cannot_be_decided()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        await Put(admin, new { enabled = true });
        var asset = await Plain(admin); var v = GiveVersion(asset);
        var s = await Suggest(t, asset, v, "tag", "outdoor");
        Sql($"UPDATE assets SET status = 'published' WHERE id = '{asset}'");

        var viewer = As(t, "Viewer");
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/ai", viewer)).Status);   // a viewer may read results
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/ai/suggestions/{s}/accept", viewer)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/ai/suggestions/{s}/reject", viewer)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/ai/suggestions/accept-all", viewer)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/ai/enrich", viewer)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/ai/suggestions/{s}/accept", As(t, "Editor"))).Status);

        var old = await Suggest(t, asset, Guid.NewGuid(), "tag", "stale");
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/ai/suggestions/{old}/accept", admin)).Status);

        // another tenant cannot see or decide any of it
        var other = await NewTenantAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/ai", Admin(other))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/ai/suggestions/{s}/reject", Admin(other))).Status);
    }

    [Fact]
    public async Task The_new_tables_are_isolated_by_row_level_security_and_follow_the_asset_when_it_is_removed()
    {
        var a = await NewTenantAsync(); var admin = Admin(a);
        var asset = await Plain(admin); var v = GiveVersion(asset);
        await Suggest(a, asset, v, "tag", "outdoor");
        await using (var db = Pg.AppContext(new TestTenant(a)))
        {
            db.AssetEmbeddings.Add(AssetEmbedding.Create(a, asset, v, EmbeddingKinds.Text, "m", [1f, 0f], DateTimeOffset.UtcNow));
            var extract = AssetExtract.Create(a, asset, v, DateTimeOffset.UtcNow);
            extract.SetOcr("text", "en", "m", DateTimeOffset.UtcNow);
            db.AssetExtracts.Add(extract);
            db.AiRuns.Add(AiRun.Create(a, asset, v, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        foreach (var table in new[] { "ai_suggestions", "asset_embeddings", "asset_extracts", "ai_runs" })
        {
            Assert.Equal(1, Count(a, $"SELECT count(*) FROM {table}"));
            var b = await NewTenantAsync();
            Assert.Equal(0, Count(b, $"SELECT count(*) FROM {table}"));
            using var noTenant = Pg.RawAppConnection(null);
            using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {table}", noTenant);
            Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
        }

        // Another tenant's session cannot write a row for this one (the policy's check).
        var intruder = await NewTenantAsync();
        using (var c = Pg.RawAppConnection(intruder))
        {
            using var cmd = new NpgsqlCommand($"INSERT INTO ai_runs (id, tenant_id, asset_id, version_id, status, steps, error, attempts, created_at) VALUES ('{Guid.NewGuid()}', '{a}', '{asset}', '{Guid.NewGuid()}', 'pending', '[]', '', 0, now())", c);
            Assert.ThrowsAny<PostgresException>(() => cmd.ExecuteNonQuery());
        }

        // A malformed vector is refused by the table itself.
        using (var c = Pg.RawAppConnection(a))
        {
            using var cmd = new NpgsqlCommand($"INSERT INTO asset_embeddings (tenant_id, asset_id, version_id, kind, dim, model, vector, created_at) VALUES ('{a}', '{asset}', '{Guid.NewGuid()}', 'text', 4, 'm', '\\x0001', now())", c);
            Assert.ThrowsAny<PostgresException>(() => cmd.ExecuteNonQuery());
        }

        Sql($"DELETE FROM assets WHERE id = '{asset}'");   // only an owner can: the app role never hard-deletes assets
        foreach (var table in new[] { "ai_suggestions", "asset_embeddings", "asset_extracts", "ai_runs" }) Assert.Equal(0, Count(a, $"SELECT count(*) FROM {table}"));
    }
}

/// <summary>A deployment whose only configured provider is a cloud service: turning it on sends pictures and audio off-site, so it must be acknowledged.</summary>
[Collection("pg")]
public sealed class AiOffSiteSettingsTests(PostgresFixture pg, SearchFixture os) : AssetTestBase(pg), IClassFixture<SearchFixture>
{
    protected override string? OpenSearchUrl => os.Url;

    protected override IReadOnlyDictionary<string, string?>? ApiEnvironment => new Dictionary<string, string?>
    {
        ["DAM_AI_PROVIDER"] = "openai-compatible", ["DAM_AI_BASE_URL"] = "https://ai.example.test/v1", ["DAM_AI_API_KEY"] = "sk-never-shown-0123456789",
    };

    [Fact]
    public async Task Enabling_a_provider_that_sends_data_off_site_needs_an_explicit_acknowledgement_and_the_key_is_never_returned()
    {
        var t = await NewTenantAsync(); var admin = Admin(t);
        var settings = (await Call(HttpMethod.Get, "/api/v1/ai/settings", admin)).Body;
        Assert.True(settings.GetProperty("sendsDataOffSite").GetBoolean());
        Assert.False(settings.GetProperty("enabled").GetBoolean());
        Assert.False((await Call(HttpMethod.Get, "/api/v1/ai/capabilities", As(t, "Viewer"))).Body.GetProperty("sendsDataOffSite").GetBoolean());   // nothing is sent while it is off
        Assert.DoesNotContain("sk-never-shown", settings.GetRawText());

        var refused = await Call(HttpMethod.Put, "/api/v1/ai/settings", admin, new { enabled = true });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.Status);
        Assert.Contains("sends the tenant's pictures and audio", refused.Body.GetProperty("errors").GetProperty("acknowledgeOffSite")[0].GetString());
        Assert.Equal(0, Count(t, "SELECT count(*) FROM ai_settings"));

        var accepted = await Call(HttpMethod.Put, "/api/v1/ai/settings", admin, new { enabled = true, acknowledgeOffSite = true });
        Assert.Equal(HttpStatusCode.OK, accepted.Status);
        Assert.DoesNotContain("sk-never-shown", accepted.Body.GetRawText());
        var caps = (await Call(HttpMethod.Get, "/api/v1/ai/capabilities", As(t, "Viewer"))).Body;      // the OpenAI-compatible provider has no image vectors
        Assert.Equal(("openai-compatible", true, true, false, true), (caps.GetProperty("provider").GetString(), caps.GetProperty("enabled").GetBoolean(), caps.GetProperty("textVectors").GetBoolean(),
            caps.GetProperty("imageVectors").GetBoolean(), caps.GetProperty("sendsDataOffSite").GetBoolean()));
        Assert.True(caps.GetProperty("semanticSearch").GetBoolean());
        Assert.DoesNotContain("example.test", caps.GetRawText());
        Assert.DoesNotContain("sk-never-shown", caps.GetRawText());
        // Once acknowledged, a later change (a feature switched off) does not ask again; switching it off never needs it.
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, "/api/v1/ai/settings", admin, new { enabled = true, features = new { tags = false, ocr = true, transcribe = true, embeddings = true } })).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, "/api/v1/ai/settings", admin, new { enabled = false })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Put, "/api/v1/ai/settings", admin, new { enabled = true })).Status);   // off and on again: confirm again

        // The self-hosted provider is not configured in this deployment, so choosing it is refused.
        var other = await Call(HttpMethod.Put, "/api/v1/ai/settings", admin, new { enabled = true, provider = "self-hosted" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, other.Status);
        Assert.Contains("DAM_AI_SIDECAR_URL", other.Body.GetProperty("errors").GetProperty("provider")[0].GetString());
    }
}
