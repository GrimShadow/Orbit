using System.Net;
using System.Text.Json;
using Dam.UnitTests.Support;
using HttpMethod = System.Net.Http.HttpMethod;

namespace Dam.IntegrationTests;

/// <summary>
/// AI enrichment end to end (ADR-0035): a file goes through the real ingest pipeline, the worker's enrichment consumer asks a fake sidecar (or a fake
/// OpenAI-compatible service) and stores the results, and the search index picks them up. Real Postgres, RabbitMQ, object storage and OpenSearch.
/// </summary>
[Collection("pg")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Disposed in DisposeAsync (xUnit IAsyncLifetime).")]
public sealed class AiEnrichmentTests(PostgresFixture pg, RabbitFixture mq, SearchFixture os) : AiWorkerTestBase(pg, mq, os), IClassFixture<RabbitFixture>, IClassFixture<SearchFixture>
{
    private readonly SearchFixture _os = os;

    private static Dictionary<string, JsonElement> Steps(JsonElement ai) =>
        ai.GetProperty("run").GetProperty("steps").EnumerateArray().ToDictionary(s => s.GetProperty("name").GetString()!);

    // ---- images ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_image_is_enriched_from_its_preview_with_suggestions_text_vectors_and_the_index_follows()
    {
        var (t, admin, folder) = await TenantWithPacks();
        await EnableAi(admin);
        using var worker = Worker(); await worker.StartAsync();
        var original = Picture();
        var asset = await NewAsset(admin, folder, "image", "IMG_0042", tags: ["hero"]);
        Assert.Equal("ingested", (await Ingest(admin, asset, original, "photo.jpg")).GetProperty("status").GetString());

        var ai = await WaitForAi(admin, asset, "ready", "failed");
        Assert.Equal("ready", ai.GetProperty("status").GetString());
        Assert.Equal(["tags", "ocr", "image-embedding", "language", "text-embedding"], ai.GetProperty("run").GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("name").GetString()));
        Assert.All(ai.GetProperty("run").GetProperty("steps").EnumerateArray(), s => Assert.Equal("ok", s.GetProperty("status").GetString()));
        Assert.Equal("succeeded", ai.GetProperty("run").GetProperty("status").GetString());

        // tags the asset already has are not proposed again; low-confidence ones are not either; the rest are pending
        var suggestions = ai.GetProperty("suggestions").EnumerateArray().ToList();
        Assert.Equal(["outdoor", "travel"], suggestions.Where(s => s.GetProperty("kind").GetString() == "tag").Select(s => s.GetProperty("value").GetString()!).Order());
        Assert.All(suggestions, s => Assert.Equal("pending", s.GetProperty("status").GetString()));
        Assert.All(suggestions.Where(s => s.GetProperty("kind").GetString() == "tag"), s => Assert.Equal("fake-sidecar-tagger", s.GetProperty("model").GetString()));
        Assert.Equal("en", ai.GetProperty("language").GetString());
        Assert.Contains(suggestions, s => s.GetProperty("kind").GetString() == "language" && s.GetProperty("value").GetString() == "en");
        Assert.Equal("Acme roadster showroom opening hours", ai.GetProperty("ocrText").GetString());
        Assert.True(ai.GetProperty("embeddingsReady").GetBoolean());
        Assert.Equal(JsonValueKind.Null, ai.GetProperty("transcript").ValueKind);

        // both vectors stored, at the configured sizes, as float32
        Assert.Equal(2, Count(t, "SELECT count(*) FROM asset_embeddings"));
        Assert.Equal(512 * 4, Count(t, "SELECT octet_length(vector) FROM asset_embeddings WHERE kind = 'image'"));
        Assert.Equal(384 * 4, Count(t, "SELECT octet_length(vector) FROM asset_embeddings WHERE kind = 'text'"));
        Assert.Equal(1, Count(t, "SELECT count(*) FROM asset_extracts WHERE ocr_text IS NOT NULL"));
        Assert.Equal(1, Count(t, "SELECT count(*) FROM ai_runs WHERE status = 'succeeded'"));

        // a rendition went to the sidecar, never the original
        Assert.All(Sidecar.Uploads.Where(u => u.Path is "/v1/tag" or "/v1/embed/image"), u => Assert.Equal("webp", u.Format));
        Assert.Equal("webp", Sidecar.Uploads.Single(u => u.Path == "/v1/ocr").Format);
        Assert.Equal(1, Sidecar.CallsTo("/v1/tag"));
        Assert.Equal(1, Sidecar.CallsTo("/v1/embed/image"));

        // the search index has the text and both vectors
        var doc = await IndexedDoc(t, asset, d => d.TryGetProperty("vector_image", out _) && d.TryGetProperty("vector_text", out _));
        Assert.Equal(512, doc.GetProperty("vector_image").GetArrayLength());
        Assert.Equal(384, doc.GetProperty("vector_text").GetArrayLength());
        Assert.Equal("Acme roadster showroom opening hours", doc.GetProperty("ocr_text").GetString());
        Assert.Equal(["hero"], Strings(doc.GetProperty("tags")));                       // suggestions are not tags until accepted

        // and the whole thing is findable: by the words read from the picture, and by a description that matches nothing in text
        await RefreshAsync(t);
        var byOcr = (await Call(HttpMethod.Post, "/api/v1/search", admin, new { query = "showroom" })).Body;
        Assert.Equal(asset, byOcr.GetProperty("hits")[0].GetProperty("id").GetGuid());
        Assert.Equal(["ocr"], Strings(byOcr.GetProperty("hits")[0].GetProperty("matchedBy")));
        var byMeaning = (await Call(HttpMethod.Post, "/api/v1/search", admin, new { query = "red SUV on beach", semantic = true })).Body;
        Assert.True(byMeaning.GetProperty("semantic").GetBoolean());
        Assert.Equal([asset], byMeaning.GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("id").GetGuid()));
        Assert.Contains("image", Strings(byMeaning.GetProperty("hits")[0].GetProperty("matchedBy")));
        Assert.Equal(0, (await Call(HttpMethod.Post, "/api/v1/search", admin, new { query = "red SUV on beach" })).Body.GetProperty("total").GetInt64());

        // accepting a suggestion puts the tag on the asset and the index
        var tag = suggestions.First(s => s.GetProperty("value").GetString() == "outdoor").GetProperty("id").GetGuid();
        var accepted = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/ai/suggestions/{tag}/accept", admin);
        Assert.Equal(["hero", "outdoor"], Strings(accepted.Body.GetProperty("tags")));
        await IndexedDoc(t, asset, d => Strings(d.GetProperty("tags")).Contains("outdoor"));
        var all = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/ai/suggestions/accept-all", admin);
        Assert.Equal(["hero", "outdoor", "travel"], Strings(all.Body.GetProperty("tags")));
        Assert.Equal(2, Count(t, "SELECT count(*) FROM ai_suggestions WHERE status = 'accepted' AND kind = 'tag'"));
    }

    [Fact]
    public async Task Enrichment_is_idempotent_a_finished_version_is_not_redone_and_a_rerun_neither_duplicates_nor_resurrects_suggestions()
    {
        var (t, admin, folder) = await TenantWithPacks();
        await EnableAi(admin);
        using var worker = Worker(); await worker.StartAsync();
        var asset = await IngestedAsset(admin, folder, Picture(), "image", "photo.jpg", "image/jpeg");
        var first = await WaitForAi(admin, asset, "ready", "failed");
        var tagCalls = Sidecar.CallsTo("/v1/tag");
        Assert.Equal(1, tagCalls);

        // reject one suggestion, then run it all again on purpose
        var travel = first.GetProperty("suggestions").EnumerateArray().First(s => s.GetProperty("value").GetString() == "travel").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/ai/suggestions/{travel}/reject", admin)).Status);
        var rerun = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/ai/enrich", admin);
        Assert.Equal(HttpStatusCode.Accepted, rerun.Status);
        var second = await WaitForAi(admin, asset, "ready", "failed");
        Assert.Equal(2, second.GetProperty("run").GetProperty("attempts").GetInt32());

        Assert.Equal(2, Sidecar.CallsTo("/v1/tag"));
        Assert.Equal(3, Count(t, "SELECT count(*) FROM ai_suggestions WHERE kind = 'tag'"));          // the same three, not six
        Assert.Equal("rejected", second.GetProperty("suggestions").EnumerateArray().First(s => s.GetProperty("value").GetString() == "travel").GetProperty("status").GetString());
        Assert.Equal(2, Count(t, "SELECT count(*) FROM asset_embeddings"));                            // replaced in place
        Assert.Equal(1, Count(t, "SELECT count(*) FROM ai_runs"));

        // A finished version handed to the pipeline again without force (a duplicate request, a backfill that raced) is left alone: no call, the run untouched.
        var calls = Sidecar.Calls.Count(c => c.Path != "/healthz");
        var attempts = second.GetProperty("run").GetProperty("attempts").GetInt32();
        var versionId = (await Versions(admin, asset))[0].GetProperty("id").GetGuid();
        Sql($"INSERT INTO outbox (id, tenant_id, event_type, payload, created_at, attempts) VALUES ('{Guid.NewGuid()}', '{t}', 'asset.ai.requested', '{{\"AssetId\":\"{asset}\",\"VersionId\":\"{versionId}\",\"Force\":false}}', now(), 0)");
        for (var i = 0; i < 100 && Count(t, "SELECT count(*) FROM outbox WHERE event_type = 'asset.ai.requested' AND published_at IS NULL") > 0; i++) await Task.Delay(100);
        await Task.Delay(1500);
        Assert.Equal(calls, Sidecar.Calls.Count(c => c.Path != "/healthz"));
        var after = (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/ai", admin)).Body;
        Assert.Equal(("ready", attempts), (after.GetProperty("status").GetString(), after.GetProperty("run").GetProperty("attempts").GetInt32()));

        // Nothing left to backfill; a deliberate re-run of everything (onlyMissing false) starts the work again.
        Assert.Equal(0, (await Call(HttpMethod.Post, "/api/v1/ai/backfill", admin, new { })).Body.GetProperty("enqueued").GetInt32());
        Assert.Equal(1, (await Call(HttpMethod.Post, "/api/v1/ai/backfill", admin, new { onlyMissing = false })).Body.GetProperty("enqueued").GetInt32());
        for (var i = 0; i < 100 && Sidecar.CallsTo("/v1/tag") < 3; i++) await Task.Delay(100);
        Assert.Equal(3, Sidecar.CallsTo("/v1/tag"));
    }

    [Fact]
    public async Task A_tenant_with_ai_off_gets_a_skipped_run_and_nothing_is_sent_anywhere()
    {
        var (t, admin, folder) = await TenantWithPacks();     // never enabled
        await DropLeftoverWork();
        using var worker = Worker(); await worker.StartAsync();
        Sidecar.Calls.Clear();
        OpenAi.Calls.Clear();
        var asset = await IngestedAsset(admin, folder, Picture(), "image", "photo.jpg", "image/jpeg");

        for (var i = 0; i < 200 && Count(t, "SELECT count(*) FROM ai_runs WHERE status = 'skipped'") == 0; i++) await Task.Delay(100);
        Assert.Equal(1, Count(t, "SELECT count(*) FROM ai_runs WHERE status = 'skipped'"));
        var ai = (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/ai", admin)).Body;
        Assert.Equal("disabled", ai.GetProperty("status").GetString());
        Assert.Contains("not enabled", ai.GetProperty("run").GetProperty("error").GetString());
        Assert.Equal(0, Sidecar.Calls.Count(c => c.Path != "/healthz"));
        Assert.Empty(OpenAi.Calls);
        Assert.Equal(0, Count(t, "SELECT count(*) FROM ai_suggestions"));
        Assert.Equal(0, Count(t, "SELECT count(*) FROM asset_embeddings"));
    }

    [Fact]
    public async Task A_provider_outage_retries_then_fails_the_run_without_touching_the_ingest_and_a_rerun_recovers()
    {
        var (t, admin, folder) = await TenantWithPacks();
        await EnableAi(admin);
        Sidecar.Down = true;
        using var worker = Worker(); await worker.StartAsync();
        var asset = await IngestedAsset(admin, folder, Picture(), "image", "photo.jpg", "image/jpeg");

        var failed = await WaitForAi(admin, asset, "failed");
        Assert.Contains("Gave up after", failed.GetProperty("run").GetProperty("error").GetString());
        Assert.True(Sidecar.Calls.Count(c => c.Path != "/healthz") >= 2);                              // it was tried more than once
        Assert.Equal(0, Count(t, "SELECT count(*) FROM ai_suggestions"));
        // the ingest was never affected: the file is stored, renditions exist, the asset is indexed
        Assert.Equal(1, (await Versions(admin, asset)).GetArrayLength());
        await IndexedDoc(t, asset, d => d.GetProperty("title").GetString() == "IMG_0042");

        Sidecar.Down = false;
        Assert.Equal(HttpStatusCode.Accepted, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/ai/enrich", admin)).Status);
        var recovered = await WaitForAi(admin, asset, "ready");
        Assert.Equal("succeeded", recovered.GetProperty("run").GetProperty("status").GetString());
        Assert.NotEmpty(recovered.GetProperty("suggestions").EnumerateArray());
    }

    [Fact]
    public async Task One_step_the_provider_refuses_fails_only_that_step()
    {
        var (_, admin, folder) = await TenantWithPacks();
        await EnableAi(admin);
        using var worker = Worker(); await worker.StartAsync();
        Sidecar.TextDim = 768;                                      // the text model does not match DAM_AI_TEXT_DIM
        var asset = await IngestedAsset(admin, folder, Picture(), "image", "photo.jpg", "image/jpeg");
        var ai = await WaitForAi(admin, asset, "ready", "failed");
        var steps = Steps(ai);
        Assert.Equal("failed", steps["text-embedding"].GetProperty("status").GetString());
        Assert.Contains("DAM_AI_TEXT_DIM", steps["text-embedding"].GetProperty("detail").GetString());
        Assert.Equal("ok", steps["tags"].GetProperty("status").GetString());
        Assert.Equal("ready", ai.GetProperty("status").GetString());        // the rest of the run stands
        Assert.True(ai.GetProperty("embeddingsReady").GetBoolean());        // the image vector was stored
    }

    [Fact]
    public async Task Features_a_tenant_switched_off_are_recorded_as_skipped_and_never_called()
    {
        var (_, admin, folder) = await TenantWithPacks();
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, "/api/v1/ai/settings", admin, new { enabled = true, features = new { tags = true, ocr = false, transcribe = true, embeddings = false } })).Status);
        using var worker = Worker(); await worker.StartAsync();
        var asset = await IngestedAsset(admin, folder, Picture(), "image", "photo.jpg", "image/jpeg");
        var ai = await WaitForAi(admin, asset, "ready", "failed");
        var steps = Steps(ai);
        Assert.Equal("ok", steps["tags"].GetProperty("status").GetString());
        foreach (var off in new[] { "ocr", "image-embedding", "text-embedding" })
        {
            Assert.Equal("skipped", steps[off].GetProperty("status").GetString());
            Assert.Contains("Switched off", steps[off].GetProperty("detail").GetString());
        }
        Assert.Equal(0, Sidecar.CallsTo("/v1/ocr"));
        Assert.Equal(0, Sidecar.CallsTo("/v1/embed/image"));
        Assert.False(ai.GetProperty("embeddingsReady").GetBoolean());
    }

    // ---- switching providers is configuration, nothing else ----------------------------------------------------------

    [Fact]
    public async Task The_same_enrichment_runs_through_either_provider_chosen_per_tenant_with_no_code_change()
    {
        var (ta, adminA, folderA) = await TenantWithPacks();      // follows the deployment default: the self-hosted sidecar
        var (tb, adminB, folderB) = await TenantWithPacks();      // chooses the OpenAI-compatible service
        await EnableAi(adminA);
        await EnableAi(adminB, "openai-compatible");
        using var worker = Worker(); await worker.StartAsync();
        var file = Picture();
        var a = await IngestedAsset(adminA, folderA, file, "image", "photo.jpg", "image/jpeg");
        var b = await IngestedAsset(adminB, folderB, file, "image", "photo.jpg", "image/jpeg");
        var aiA = await WaitForAi(adminA, a, "ready", "failed");
        var aiB = await WaitForAi(adminB, b, "ready", "failed");

        Assert.Equal(("ready", "ready"), (aiA.GetProperty("status").GetString(), aiB.GetProperty("status").GetString()));
        Values(aiA, "tag").Zip(Values(aiB, "tag")).ToList().ForEach(p => Assert.Equal(p.First, p.Second));        // the same tags, whoever produced them
        Assert.Equal(["hero", "outdoor", "travel"], Values(aiB, "tag").Order());
        Assert.Equal(aiA.GetProperty("ocrText").GetString(), aiB.GetProperty("ocrText").GetString());
        Assert.Equal("en", aiB.GetProperty("language").GetString());

        // each went where it was configured to go
        Assert.Equal("fake-sidecar-tagger", Steps(aiA)["tags"].GetProperty("model").GetString());
        Assert.Equal("gpt-4o-mini", Steps(aiB)["tags"].GetProperty("model").GetString());
        Assert.Equal(1, Sidecar.CallsTo("/v1/tag"));                  // only A's
        Assert.True(OpenAi.CallsTo("chat/completions") >= 3);          // B's tags, OCR and language
        Assert.True(OpenAi.CallsTo("/embeddings") >= 1);
        Assert.All(OpenAi.Calls, c => Assert.Equal($"Bearer {OpenAi.ExpectedKey}", c.Authorization));

        // the OpenAI-compatible service has no picture vectors, so B has the text vector only: its search is by the meaning of the words
        Assert.Equal(("ok", "skipped"), (Steps(aiA)["image-embedding"].GetProperty("status").GetString(), Steps(aiB)["image-embedding"].GetProperty("status").GetString()));
        Assert.Contains("openai-compatible", Steps(aiB)["image-embedding"].GetProperty("detail").GetString());
        Assert.Equal(2, Count(ta, "SELECT count(*) FROM asset_embeddings"));
        Assert.Equal(1, Count(tb, "SELECT count(*) FROM asset_embeddings WHERE kind = 'text'"));
        Assert.Equal(0, Count(tb, "SELECT count(*) FROM asset_embeddings WHERE kind = 'image'"));

        // switching a tenant over is a settings change, and the next run uses the other provider
        await EnableAi(adminB, "self-hosted");
        Assert.Equal(HttpStatusCode.Accepted, (await Call(HttpMethod.Post, $"/api/v1/assets/{b}/ai/enrich", adminB)).Status);
        for (var i = 0; i < 200 && Sidecar.CallsTo("/v1/tag") < 2; i++) await Task.Delay(100);
        Assert.Equal(2, Sidecar.CallsTo("/v1/tag"));
        for (var i = 0; i < 200 && Count(tb, "SELECT count(*) FROM asset_embeddings WHERE kind = 'image'") == 0; i++) await Task.Delay(100);
        Assert.Equal(1, Count(tb, "SELECT count(*) FROM asset_embeddings WHERE kind = 'image'"));
    }

    private static List<string> Values(JsonElement ai, string kind) =>
        ai.GetProperty("suggestions").EnumerateArray().Where(s => s.GetProperty("kind").GetString() == kind).Select(s => s.GetProperty("value").GetString()!).ToList();

    // ---- backfill ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_backfill_enriches_assets_that_arrived_while_ai_was_off_in_chunks_and_only_those()
    {
        var (t, admin, folder) = await TenantWithPacks();
        await DropLeftoverWork();
        using var worker = Worker(); await worker.StartAsync();
        Sidecar.Calls.Clear();
        var assets = new List<Guid>();
        for (var i = 0; i < 3; i++) assets.Add(await IngestedAsset(admin, folder, Picture(10, (byte)(30 + i), 200), "image", "photo.jpg", "image/jpeg", $"IMG_{i}"));
        for (var i = 0; i < 200 && Count(t, "SELECT count(*) FROM ai_runs WHERE status = 'skipped'") < 3; i++) await Task.Delay(100);
        Assert.Equal(3, Count(t, "SELECT count(*) FROM ai_runs WHERE status = 'skipped'"));
        Assert.Equal(0, Sidecar.Calls.Count(c => c.Path != "/healthz"));

        await EnableAi(admin);
        var first = (await Call(HttpMethod.Post, "/api/v1/ai/backfill", admin, new { limit = 2 })).Body;
        Assert.Equal((3, 2, 1), (first.GetProperty("matched").GetInt32(), first.GetProperty("enqueued").GetInt32(), first.GetProperty("remaining").GetInt32()));
        var second = (await Call(HttpMethod.Post, "/api/v1/ai/backfill", admin, new { limit = 2 })).Body;
        Assert.Equal(1, second.GetProperty("enqueued").GetInt32());
        foreach (var a in assets) Assert.Equal("ready", (await WaitForAi(admin, a, "ready", "failed")).GetProperty("status").GetString());

        var status = (await Call(HttpMethod.Get, "/api/v1/ai/status", admin)).Body;
        Assert.Equal(3, status.GetProperty("counts").GetProperty("enriched").GetInt32());
        Assert.Equal(0, status.GetProperty("queueDepth").GetInt32());
        Assert.Equal(3, Sidecar.CallsTo("/v1/tag"));
        Assert.Equal(0, (await Call(HttpMethod.Post, "/api/v1/ai/backfill", admin, new { })).Body.GetProperty("enqueued").GetInt32());
    }

    // ---- recordings ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_video_gets_a_searchable_transcript_with_timestamps_poster_tags_and_vectors()
    {
        var (t, admin, folder) = await TenantWithPacks();
        await EnableAi(admin);
        using var worker = Worker(); await worker.StartAsync();
        var asset = await IngestedAsset(admin, folder, TestImages.VideoWithAudio(3), "video", "clip.mp4", "video/mp4", "Showroom walkthrough");

        var ai = await WaitForAi(admin, asset, "ready", "failed");
        Assert.Equal("ready", ai.GetProperty("status").GetString());
        var steps = Steps(ai);
        Assert.Equal(["tags", "image-embedding", "transcript", "language", "text-embedding"], ai.GetProperty("run").GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("name").GetString()));
        Assert.Equal("ok", steps["transcript"].GetProperty("status").GetString());

        var transcript = ai.GetProperty("transcript");
        Assert.Equal("en", transcript.GetProperty("language").GetString());
        Assert.Equal(9000, transcript.GetProperty("durationMs").GetInt64());
        Assert.Equal(3, transcript.GetProperty("segments").GetArrayLength());
        Assert.Equal((2500L, 6000L, "Our new roadster has a panoramic roof"),
            (transcript.GetProperty("segments")[1].GetProperty("startMs").GetInt64(), transcript.GetProperty("segments")[1].GetProperty("endMs").GetInt64(), transcript.GetProperty("segments")[1].GetProperty("text").GetString()));

        // what was sent for transcription is mono 16 kHz MP3, pulled from the video by ffmpeg, never the video itself
        var upload = Sidecar.Uploads.Single(u => u.Path == "/v1/transcribe");
        Assert.Equal("mp3", upload.Format);
        Assert.InRange(upload.Length, 2048, 25 * 1024 * 1024);
        Assert.Contains("max_seconds=3600", Sidecar.Calls.Single(c => c.Path == "/v1/transcribe").Body);
        Assert.All(Sidecar.Uploads.Where(u => u.Path is "/v1/tag" or "/v1/embed/image"), u => Assert.Equal("webp", u.Format));   // the poster

        var doc = await IndexedDoc(t, asset, d => d.TryGetProperty("transcript_segments", out _) && d.TryGetProperty("vector_text", out _));
        Assert.Equal(3, doc.GetProperty("transcript_segments").GetArrayLength());
        Assert.Equal(512, doc.GetProperty("vector_image").GetArrayLength());

        await RefreshAsync(t);
        var found = (await Call(HttpMethod.Post, "/api/v1/search", admin, new { query = "panoramic roof" })).Body;
        var hit = found.GetProperty("hits")[0];
        Assert.Equal(asset, hit.GetProperty("id").GetGuid());
        Assert.Equal(["transcript"], Strings(hit.GetProperty("matchedBy")));
        Assert.Equal(2500, hit.GetProperty("transcriptMatches")[0].GetProperty("startMs").GetInt64());     // "jump to 0:02"
    }

    [Fact]
    public async Task A_recording_gets_a_transcript_and_a_text_vector_but_no_picture_steps_and_one_with_no_sound_is_skipped_not_failed()
    {
        var (t, admin, folder) = await TenantWithPacks();
        await EnableAi(admin);
        using var worker = Worker(); await worker.StartAsync();
        var audio = await IngestedAsset(admin, folder, TestImages.Wav(2, 300 + NextVariant()), "audio", "speech.wav", "audio/wav", "Podcast");
        var ai = await WaitForAi(admin, audio, "ready", "failed");
        Assert.Equal(["transcript", "language", "text-embedding"], ai.GetProperty("run").GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("name").GetString()));
        Assert.Equal(3, ai.GetProperty("transcript").GetProperty("segments").GetArrayLength());
        Assert.Equal(1, Count(t, "SELECT count(*) FROM asset_embeddings WHERE kind = 'text'"));
        Assert.Equal(0, Count(t, "SELECT count(*) FROM asset_embeddings WHERE kind = 'image'"));

        var silent = await IngestedAsset(admin, folder, TestImages.Video(2), "video", "silent.mp4", "video/mp4", "No sound");   // a test pattern with no audio track
        var quiet = await WaitForAi(admin, silent, "ready", "failed");
        Assert.Equal("skipped", Steps(quiet)["transcript"].GetProperty("status").GetString());
        Assert.Contains("no audio", Steps(quiet)["transcript"].GetProperty("detail").GetString());
        Assert.Equal("ready", quiet.GetProperty("status").GetString());
        Assert.Equal(1, Sidecar.CallsTo("/v1/transcribe"));          // only the recording that had sound
    }

    // ---- documents ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_pdf_without_a_text_layer_is_read_with_ocr_page_by_page_and_gets_tags_and_vectors_from_page_one()
    {
        var (_, admin, folder) = await TenantWithPacks();
        await EnableAi(admin);
        using var worker = Worker(); await worker.StartAsync();
        var asset = await IngestedAsset(admin, folder, TestImages.Pdf("Flyer " + NextVariant(), 3), "document", "flyer.pdf", "application/pdf", "Showroom flyer");
        var ai = await WaitForAi(admin, asset, "ready", "failed");
        Assert.Equal("ready", ai.GetProperty("status").GetString());
        var steps = Steps(ai);
        Assert.Equal("ok", steps["text-layer"].GetProperty("status").GetString());
        Assert.Contains("no text layer", steps["text-layer"].GetProperty("detail").GetString());
        Assert.Contains("3 page(s)", steps["ocr"].GetProperty("detail").GetString());
        Assert.Equal(3, Sidecar.CallsTo("/v1/ocr"));                                    // one call per page, up to the cap
        Assert.Equal("webp", Sidecar.Uploads.First(u => u.Path == "/v1/ocr").Format);
        Assert.True(ai.GetProperty("embeddingsReady").GetBoolean());
    }
}
