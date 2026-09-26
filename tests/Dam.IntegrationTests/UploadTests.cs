using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.S3.Model;
using Dam.Domain.Common;
using Dam.Infrastructure;
using Dam.Infrastructure.Consumers;
using Dam.Infrastructure.Messaging;
using Dam.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dam.IntegrationTests;

[Collection("pg")]
public sealed class UploadTests(PostgresFixture pg, RabbitFixture mq) : AssetTestBase(pg), IClassFixture<RabbitFixture>
{
    private const int MiB = 1024 * 1024;
    private static readonly HttpClient S3Http = new();

    private IHost StartWorker(string? s3Endpoint = null)
    {
        var b = Host.CreateApplicationBuilder();
        b.Logging.SetMinimumLevel(LogLevel.Warning);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DAM_S3_ENDPOINT"] = s3Endpoint ?? Pg.S3Endpoint, ["DAM_S3_ACCESS_KEY"] = Pg.S3AccessKey, ["DAM_S3_SECRET_KEY"] = Pg.S3SecretKey,
        }).Build();
        b.Services.AddDamWorkerIdentity();
        b.Services.AddDamInfrastructure(Pg.AppConnection);
        b.Services.AddDamStorage(cfg);
        b.Services.AddDamMessaging(new MessagingOptions { RabbitUrl = mq.Url, MaxAttempts = 3, RetryBaseDelayMs = 100, OutboxPollMs = 100 });
        b.Services.AddEventConsumer<HashUploadConsumer>();
        b.Services.AddOutboxRelay(Pg.SystemConnection);
        return b.Build();
    }

    private static byte[] Bytes(int size, int seed) { var b = new byte[size]; new Random(seed).NextBytes(b); return b; }
    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private static async Task<string> PutPart(string url, byte[] data)
    {
        using var res = await S3Http.PutAsync(url, new ByteArrayContent(data));
        Assert.True(res.IsSuccessStatusCode, $"part upload failed: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return res.Headers.ETag!.Tag.Trim('"');
    }

    private async Task<JsonElement> Start(string token, Guid asset, string fileName, long size, object? extra = null)
    {
        var body = new Dictionary<string, object?> { ["fileName"] = fileName, ["size"] = size, ["contentType"] = "image/jpeg" };
        if (extra is not null) foreach (var p in extra.GetType().GetProperties()) body[p.Name] = p.GetValue(extra);
        var r = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/uploads", token, body);
        Assert.Equal(HttpStatusCode.Created, r.Status);
        return r.Body;
    }

    private static byte[] Slice(byte[] file, int partSize, int part) => file.Skip((part - 1) * partSize).Take(partSize).ToArray();

    /// <summary>Sends every part named in the start response, then completes; returns the completion response.</summary>
    private async Task<(HttpStatusCode Status, JsonElement Body)> UploadWhole(string token, JsonElement start, byte[] file, bool sendEtags = true)
    {
        var partSize = start.GetProperty("upload").GetProperty("partSize").GetInt32();
        var etags = new List<object>();
        foreach (var p in start.GetProperty("parts").EnumerateArray())
        {
            var n = p.GetProperty("partNumber").GetInt32();
            etags.Add(new { partNumber = n, eTag = await PutPart(p.GetProperty("url").GetString()!, Slice(file, partSize, n)) });
        }
        return await Call(HttpMethod.Post, $"/api/v1/uploads/{start.GetProperty("upload").GetProperty("id").GetGuid()}/complete", token, sendEtags ? new { parts = etags } : new { });
    }

    private async Task<JsonElement> WaitForOutcome(string token, Guid upload, int seconds = 20)
    {
        for (var i = 0; i < seconds * 10; i++)
        {
            var u = (await Call(HttpMethod.Get, $"/api/v1/uploads/{upload}", token)).Body;
            if (u.GetProperty("status").GetString() is not ("processing" or "initiated")) return u;
            await Task.Delay(100);
        }
        throw new TimeoutException("The worker did not finish hashing the upload.");
    }

    private async Task<bool> InStore(string bucket, string key)
    {
        using var s3 = Pg.S3Client();
        try { await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = key }); return true; }
        catch (Amazon.S3.AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound) { return false; }
    }

    private async Task<(Guid Tenant, string Admin, Guid Folder)> Setup() { var (t, a, f) = await TenantWithPacks(); return (t, a, f); }

    // ---- the golden path ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_file_goes_straight_to_storage_in_parts_is_hashed_by_the_worker_and_ends_up_ready_for_ingest()
    {
        var (t, admin, folder) = await Setup();
        using var worker = StartWorker(); await worker.StartAsync();
        var asset = await NewAsset(admin, folder);
        var file = Bytes(11 * MiB, 1); // 5 + 5 + 1 MiB

        var start = await Start(admin, asset, "hero.jpg", file.Length);
        var u = start.GetProperty("upload");
        Assert.Equal("initiated", u.GetProperty("status").GetString());
        Assert.Equal(5 * MiB, u.GetProperty("partSize").GetInt32());
        Assert.Equal(3, u.GetProperty("partCount").GetInt32());
        Assert.Equal(3, start.GetProperty("parts").GetArrayLength());
        var url = start.GetProperty("parts")[0].GetProperty("url").GetString()!;
        Assert.StartsWith(Pg.S3Endpoint, url);                                  // bytes go to storage, not to the API
        Assert.Contains("X-Amz-Signature", url);
        var expires = start.GetProperty("parts")[0].GetProperty("expiresAt").GetDateTimeOffset();
        Assert.InRange((expires - DateTimeOffset.UtcNow).TotalMinutes, 13, 15.5);  // short-lived: at most 15 minutes (spec A7.6)

        var done = await UploadWhole(admin, start, file);
        Assert.Equal(HttpStatusCode.Accepted, done.Status);
        Assert.Equal("processing", done.Body.GetProperty("status").GetString());

        var id = u.GetProperty("id").GetGuid();
        var outcome = await WaitForOutcome(admin, id);
        Assert.Equal("ready", outcome.GetProperty("status").GetString());
        Assert.Equal(Sha(file), outcome.GetProperty("sha256").GetString());
        Assert.Equal(file.Length, outcome.GetProperty("sizeBytes").GetInt64());

        var a = (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}", admin)).Body;
        Assert.Equal(Sha(file), a.GetProperty("checksumSha256").GetString());
        Assert.True(await InStore("dam-quarantine", $"staging/{t:N}/{asset:N}/{id:N}"));    // staged, awaiting scan and promotion (Step 1.4)
        Assert.Equal(1, Count(t, "SELECT count(*) FROM outbox WHERE event_type = 'ingest.requested'"));
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'ingest.requested'"));
    }

    [Fact]
    public async Task An_upload_can_be_resumed_after_a_drop_by_asking_what_storage_already_has()
    {
        var (_, admin, folder) = await Setup();
        using var worker = StartWorker(); await worker.StartAsync();
        var asset = await NewAsset(admin, folder);
        var file = Bytes(11 * MiB, 2);
        var start = await Start(admin, asset, "big.jpg", file.Length);
        var id = start.GetProperty("upload").GetProperty("id").GetGuid();
        var urls = start.GetProperty("parts").EnumerateArray().ToDictionary(p => p.GetProperty("partNumber").GetInt32(), p => p.GetProperty("url").GetString()!);

        // the connection "drops" after parts 1 and 3 made it
        await PutPart(urls[1], Slice(file, 5 * MiB, 1));
        await PutPart(urls[3], Slice(file, 5 * MiB, 3));

        var state = (await Call(HttpMethod.Get, $"/api/v1/uploads/{id}", admin)).Body;
        Assert.Equal("initiated", state.GetProperty("status").GetString());
        Assert.Equal([1, 3], state.GetProperty("receivedParts").EnumerateArray().Select(p => p.GetProperty("partNumber").GetInt32()).ToArray());

        // completing now names exactly what is missing
        var early = await Call(HttpMethod.Post, $"/api/v1/uploads/{id}/complete", admin, new { });
        Assert.Equal(HttpStatusCode.Conflict, early.Status);
        Assert.Equal("2", early.Body.GetProperty("missingParts").GetString());

        // ask for a fresh URL for the missing part only, send it, and finish
        var fresh = (await Call(HttpMethod.Post, $"/api/v1/uploads/{id}/parts", admin, new { partNumbers = new[] { 2 } })).Body;
        Assert.Equal(1, fresh.GetArrayLength());
        await PutPart(fresh[0].GetProperty("url").GetString()!, Slice(file, 5 * MiB, 2));
        Assert.Equal(HttpStatusCode.Accepted, (await Call(HttpMethod.Post, $"/api/v1/uploads/{id}/complete", admin, new { })).Status);

        var outcome = await WaitForOutcome(admin, id);
        Assert.Equal("ready", outcome.GetProperty("status").GetString());
        Assert.Equal(Sha(file), outcome.GetProperty("sha256").GetString()); // the reassembled file is byte-identical
        // calling complete again is harmless
        Assert.Equal(HttpStatusCode.Accepted, (await Call(HttpMethod.Post, $"/api/v1/uploads/{id}/complete", admin, new { })).Status);
    }

    [Fact]
    public async Task A_part_of_the_wrong_size_or_with_a_wrong_etag_is_caught_before_the_file_is_assembled()
    {
        var (_, admin, folder) = await Setup();
        var asset = await NewAsset(admin, folder);
        var file = Bytes(11 * MiB, 3);
        var start = await Start(admin, asset, "x.jpg", file.Length);
        var id = start.GetProperty("upload").GetProperty("id").GetGuid();
        var urls = start.GetProperty("parts").EnumerateArray().Select(p => p.GetProperty("url").GetString()!).ToArray();

        await PutPart(urls[0], Bytes(6 * MiB, 9)); // part 1 should be exactly 5 MiB
        await PutPart(urls[1], Slice(file, 5 * MiB, 2));
        await PutPart(urls[2], Slice(file, 5 * MiB, 3));
        var wrongSize = await Call(HttpMethod.Post, $"/api/v1/uploads/{id}/complete", admin, new { });
        Assert.Equal(HttpStatusCode.Conflict, wrongSize.Status);
        Assert.Equal("1", wrongSize.Body.GetProperty("wrongSizeParts").GetString());

        var good1 = await PutPart(urls[0], Slice(file, 5 * MiB, 1)); // overwrite it with the right bytes
        var badEtag = await Call(HttpMethod.Post, $"/api/v1/uploads/{id}/complete", admin, new { parts = new[] { new { partNumber = 1, eTag = "0000" } } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badEtag.Status);
        var ok = await Call(HttpMethod.Post, $"/api/v1/uploads/{id}/complete", admin, new { parts = new[] { new { partNumber = 1, eTag = good1 } } });
        Assert.Equal(HttpStatusCode.Accepted, ok.Status);
    }

    [Fact]
    public async Task When_the_worker_cannot_reach_storage_it_gives_up_and_the_upload_is_marked_failed_not_left_processing()
    {
        var (t, admin, folder) = await Setup();
        using var worker = StartWorker(s3Endpoint: "http://127.0.0.1:1"); // nothing listens here: every attempt fails
        await worker.StartAsync();
        var asset = await NewAsset(admin, folder);
        var file = Bytes(6 * MiB, 11);
        var start = await Start(admin, asset, "x.jpg", file.Length);
        await UploadWhole(admin, start, file);

        var outcome = await WaitForOutcome(admin, start.GetProperty("upload").GetProperty("id").GetGuid(), seconds: 90);
        Assert.Equal("failed", outcome.GetProperty("status").GetString());
        Assert.Contains("failed after several attempts", outcome.GetProperty("error").GetString());
        Assert.Equal(0, Count(t, "SELECT count(*) FROM outbox WHERE event_type = 'ingest.requested'"));

        // ...and the asset is free to try again with a new upload
        Assert.Equal(HttpStatusCode.Created, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/uploads", admin, new { fileName = "again.jpg", size = 1000 })).Status);
    }

    // ---- duplicates --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Identical_content_is_detected_as_a_duplicate_by_the_worker_and_the_staged_copy_is_discarded()
    {
        var (t, admin, folder) = await Setup();
        using var worker = StartWorker(); await worker.StartAsync();
        var file = Bytes(6 * MiB, 4);

        var first = await NewAsset(admin, folder, title: "Original");
        var s1 = await Start(admin, first, "a.jpg", file.Length);
        await UploadWhole(admin, s1, file);
        Assert.Equal("ready", (await WaitForOutcome(admin, s1.GetProperty("upload").GetProperty("id").GetGuid())).GetProperty("status").GetString());

        var second = await NewAsset(admin, folder, title: "Copy");
        var s2 = await Start(admin, second, "renamed.jpg", file.Length); // a different name does not fool it
        var id2 = s2.GetProperty("upload").GetProperty("id").GetGuid();
        await UploadWhole(admin, s2, file);
        var outcome = await WaitForOutcome(admin, id2);
        Assert.Equal("duplicate", outcome.GetProperty("status").GetString());
        Assert.Equal(first, outcome.GetProperty("duplicateOfAssetId").GetGuid());
        Assert.False(await InStore("dam-quarantine", $"staging/{t:N}/{second:N}/{id2:N}")); // the duplicate bytes are gone
        // the empty shell created only for that file is removed from view
        Assert.Equal("deleted", (await Call(HttpMethod.Get, $"/api/v1/assets/{second}", admin)).Body.GetProperty("status").GetString());
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'upload.duplicate'"));
        Assert.Equal(1, Count(t, "SELECT count(*) FROM outbox WHERE event_type = 'ingest.requested'")); // only the original went on
    }

    [Fact]
    public async Task Duplicates_can_be_allowed_on_purpose()
    {
        var (_, admin, folder) = await Setup();
        using var worker = StartWorker(); await worker.StartAsync();
        var file = Bytes(6 * MiB, 5);
        foreach (var title in new[] { "One", "Two" })
        {
            var asset = await NewAsset(admin, folder, title: title);
            var s = await Start(admin, asset, "same.jpg", file.Length, new { AllowDuplicate = true });
            await UploadWhole(admin, s, file);
            Assert.Equal("ready", (await WaitForOutcome(admin, s.GetProperty("upload").GetProperty("id").GetGuid())).GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task A_client_supplied_hash_lets_a_known_duplicate_be_refused_at_once_with_the_id_of_the_original()
    {
        var (_, admin, folder) = await Setup();
        using var worker = StartWorker(); await worker.StartAsync();
        var file = Bytes(6 * MiB, 6);
        var first = await NewAsset(admin, folder);
        var s1 = await Start(admin, first, "a.jpg", file.Length);
        await UploadWhole(admin, s1, file);
        await WaitForOutcome(admin, s1.GetProperty("upload").GetProperty("id").GetGuid());

        // before any bytes move
        var second = await NewAsset(admin, folder);
        var refused = await Call(HttpMethod.Post, $"/api/v1/assets/{second}/uploads", admin, new { fileName = "b.jpg", size = file.Length, sha256 = Sha(file) });
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Equal(first.ToString(), refused.Body.GetProperty("duplicateOfAssetId").GetString());

        // or at completion, when the hash was only supplied then; the half-finished upload is aborted
        var s2 = await Start(admin, second, "b.jpg", file.Length);
        var id2 = s2.GetProperty("upload").GetProperty("id").GetGuid();
        foreach (var p in s2.GetProperty("parts").EnumerateArray()) await PutPart(p.GetProperty("url").GetString()!, Slice(file, 5 * MiB, p.GetProperty("partNumber").GetInt32()));
        var late = await Call(HttpMethod.Post, $"/api/v1/uploads/{id2}/complete", admin, new { sha256 = Sha(file) });
        Assert.Equal(HttpStatusCode.Conflict, late.Status);
        Assert.Equal(first.ToString(), late.Body.GetProperty("duplicateOfAssetId").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"/api/v1/uploads/{id2}/parts", admin, new { partNumbers = new[] { 1 } })).Status);
        var state = (await Call(HttpMethod.Get, $"/api/v1/uploads/{id2}", admin)).Body;
        Assert.Equal("duplicate", state.GetProperty("status").GetString());          // the outcome was recorded, not rolled back
        Assert.Equal(first, state.GetProperty("duplicateOfAssetId").GetGuid());
        Assert.Equal("deleted", (await Call(HttpMethod.Get, $"/api/v1/assets/{second}", admin)).Body.GetProperty("status").GetString());
    }

    // ---- validation and abuse ----------------------------------------------------------------------------------

    [Fact]
    public async Task Unsafe_or_oversized_files_are_refused_before_any_upload_starts()
    {
        var (_, admin, folder) = await Setup();
        var image = await NewAsset(admin, folder);
        async Task<JsonElement> Refused(object body)
        {
            var r = await Call(HttpMethod.Post, $"/api/v1/assets/{image}/uploads", admin, body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status);
            return r.Body.GetProperty("errors");
        }
        Assert.True((await Refused(new { fileName = "setup.exe", size = 100 })).TryGetProperty("fileName", out _));
        Assert.True((await Refused(new { fileName = "photo.jpg.exe", size = 100 })).TryGetProperty("fileName", out _));
        Assert.True((await Refused(new { fileName = "manual.pdf", size = 100 })).TryGetProperty("fileName", out _));   // wrong type for an image
        Assert.True((await Refused(new { fileName = "noextension", size = 100 })).TryGetProperty("fileName", out _));
        Assert.True((await Refused(new { fileName = "a.jpg", size = 0 })).TryGetProperty("size", out _));
        Assert.True((await Refused(new { fileName = "a.jpg", size = 3L * 1024 * 1024 * 1024 })).TryGetProperty("size", out _)); // over the image limit
        Assert.True((await Refused(new { fileName = "a.jpg", size = 10, sha256 = "not-a-hash" })).TryGetProperty("sha256", out _));
        Assert.True((await Refused(new { fileName = "a.jpg", size = 10, contentType = new string('x', 201) })).TryGetProperty("contentType", out _));
    }

    [Fact]
    public async Task Path_tricks_in_a_file_name_are_stripped_and_never_reach_the_storage_key()
    {
        var (t, admin, folder) = await Setup();
        var asset = await NewAsset(admin, folder);
        var start = await Start(admin, asset, @"..\..\etc\evil.jpg", 1000);
        Assert.Equal("evil.jpg", start.GetProperty("upload").GetProperty("fileName").GetString());
        var id = start.GetProperty("upload").GetProperty("id").GetGuid();
        Assert.Equal(1, Count(t, $"SELECT count(*) FROM uploads WHERE storage_key = 'staging/{t:N}/{asset:N}/{id:N}'"));
    }

    [Fact]
    public async Task Only_one_upload_can_be_open_per_asset_and_an_aborted_upload_can_be_replaced()
    {
        var (_, admin, folder) = await Setup();
        var asset = await NewAsset(admin, folder);
        var first = await Start(admin, asset, "a.jpg", 1000);
        var id = first.GetProperty("upload").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/uploads", admin, new { fileName = "b.jpg", size = 1000 })).Status);

        var aborted = await Call(HttpMethod.Post, $"/api/v1/uploads/{id}/abort", admin);
        Assert.Equal("aborted", aborted.Body.GetProperty("status").GetString());
        Assert.Equal("aborted", (await Call(HttpMethod.Post, $"/api/v1/uploads/{id}/abort", admin)).Body.GetProperty("status").GetString()); // idempotent
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"/api/v1/uploads/{id}/parts", admin, new { partNumbers = new[] { 1 } })).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"/api/v1/uploads/{id}/complete", admin, new { })).Status);
        Assert.Equal(HttpStatusCode.Created, (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/uploads", admin, new { fileName = "b.jpg", size = 1000 })).Status);
    }

    [Fact]
    public async Task Part_requests_are_bounded_and_a_presigned_url_cannot_be_reused_for_another_part()
    {
        var (_, admin, folder) = await Setup();
        var asset = await NewAsset(admin, folder);
        var file = Bytes(11 * MiB, 8);
        var start = await Start(admin, asset, "a.jpg", file.Length);
        var id = start.GetProperty("upload").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, $"/api/v1/uploads/{id}/parts", admin, new { partNumbers = new[] { 0 } })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, $"/api/v1/uploads/{id}/parts", admin, new { partNumbers = new[] { 4 } })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, $"/api/v1/uploads/{id}/parts", admin, new { partNumbers = Enumerable.Range(1, 201) })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, $"/api/v1/uploads/{id}/parts", admin, new { partNumbers = Array.Empty<int>() })).Status);

        // the signature covers the part number: edit it and storage refuses
        var url = start.GetProperty("parts")[0].GetProperty("url").GetString()!;
        var tampered = url.Replace("partNumber=1", "partNumber=2", StringComparison.Ordinal);
        Assert.NotEqual(url, tampered);
        using var res = await S3Http.PutAsync(tampered, new ByteArrayContent(Bytes(1000, 1)));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Uploads_follow_asset_permissions_and_are_invisible_across_tenants()
    {
        var (t, admin, folder) = await Setup();
        var alice = ApiTestHost.TokenFor(t, Guid.NewGuid(), ["Contributor"]);
        var bob = ApiTestHost.TokenFor(t, Guid.NewGuid(), ["Contributor"]);
        var mine = await NewAsset(alice, folder);
        var start = await Start(alice, mine, "a.jpg", 1000);
        var id = start.GetProperty("upload").GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"/api/v1/assets/{mine}/uploads", bob, new { fileName = "b.jpg", size = 10 })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/uploads/{id}", bob)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"/api/v1/uploads/{id}/abort", bob)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"/api/v1/assets/{mine}/uploads", As(t, "Viewer"), new { fileName = "b.jpg", size = 10 })).Status);   // cannot even see a draft
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, $"/api/v1/assets/{mine}/uploads", As(t, "Approver"), new { fileName = "b.jpg", size = 10 })).Status); // can see it, may not change it
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, $"/api/v1/uploads/{id}", admin)).Status);

        var other = await NewTenantAsync("core");
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/uploads/{id}", Admin(other))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, $"/api/v1/uploads/{id}/parts", Admin(other), new { partNumbers = new[] { 1 } })).Status);
    }

    // ---- bulk --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_bulk_batch_of_200_files_creates_one_asset_each_that_inherits_the_batch_tags_and_metadata()
    {
        var (t, admin, folder) = await Setup();
        var extensions = new[] { "jpg", "png", "mp4", "pdf", "wav", "glb" };
        var files = Enumerable.Range(0, 198).Select(i => new { fileName = $"file-{i:D3}.{extensions[i % extensions.Length]}", size = 1000L + i, contentType = "application/octet-stream" }).ToList();
        files.Add(new { fileName = "virus.exe", size = 10L, contentType = "application/octet-stream" });
        files.Add(new { fileName = "lib.dll", size = 10L, contentType = "application/octet-stream" });

        var res = await Call(HttpMethod.Post, "/api/v1/assets/bulk-upload", admin, new
        {
            folderId = folder, tags = new[] { "Spring-Launch", "agency" }, metadata = new { credit = "Agency X", channels = new[] { "signage" } }, files,
        });
        Assert.Equal(HttpStatusCode.OK, res.Status);
        Assert.Equal(198, res.Body.GetProperty("created").GetInt32());
        Assert.Equal(2, res.Body.GetProperty("failed").GetInt32());
        var batch = res.Body.GetProperty("batchId").GetGuid();
        var failed = res.Body.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("assetId").ValueKind == JsonValueKind.Null).ToList();
        Assert.Equal(["lib.dll", "virus.exe"], failed.Select(f => f.GetProperty("fileName").GetString()!).Order().ToArray());

        Assert.Equal(198, Count(t, "SELECT count(*) FROM assets WHERE status = 'draft'"));
        Assert.Equal(198, Count(t, $"SELECT count(*) FROM uploads WHERE batch_id = '{batch}' AND status = 'Initiated'"));
        Assert.Equal(198, Count(t, "SELECT count(*) FROM assets WHERE tags = ARRAY['agency','spring-launch']::text[]"));                   // every asset carries the batch tags
        Assert.Equal(198, Count(t, "SELECT count(*) FROM assets WHERE metadata->>'credit' = 'Agency X'"));                                 // ...and the batch metadata
        Assert.Equal(198, Count(t, "SELECT count(*) FROM assets WHERE attributes->'channel' = '[\"signage\"]'::jsonb"));                     // ...including derived access attributes
        Assert.Equal(198, Count(t, "SELECT count(*) FROM assets WHERE metadata->'regions' = '[\"north\"]'::jsonb"));                        // ...and the folder's defaults
        Assert.Equal(33, Count(t, "SELECT count(*) FROM assets WHERE type = 'video'"));                                                      // types inferred from extensions
        Assert.Equal(66, Count(t, "SELECT count(*) FROM assets WHERE type = 'image'"));                                                      // jpg + png
        Assert.Equal(33, Count(t, "SELECT count(*) FROM assets WHERE type = '3d'"));

        var item = res.Body.GetProperty("items")[0];
        Assert.InRange(item.GetProperty("upload").GetProperty("parts").GetArrayLength(), 1, 4);
        Assert.Equal("file-000", (await Call(HttpMethod.Get, $"/api/v1/assets/{item.GetProperty("assetId").GetGuid()}", admin)).Body.GetProperty("title").GetString());

        // a second request with the same batch id adds to the batch
        var more = await Call(HttpMethod.Post, "/api/v1/assets/bulk-upload", admin, new { folderId = folder, batchId = batch, files = new[] { new { fileName = "extra.jpg", size = 500L } } });
        Assert.Equal(batch, more.Body.GetProperty("batchId").GetGuid());
        Assert.Equal(199, Count(t, $"SELECT count(*) FROM uploads WHERE batch_id = '{batch}'"));
    }

    [Fact]
    public async Task Bulk_upload_reports_bad_batch_metadata_per_file_and_enforces_its_limits()
    {
        var (tenant, admin, folder) = await Setup();
        var mixed = await Call(HttpMethod.Post, "/api/v1/assets/bulk-upload", admin, new
        {
            folderId = folder, metadata = new { vehicle = new[] { "acme_nope" } },
            files = new[] { new { fileName = "a.jpg", size = 10L }, new { fileName = "b.mp4", size = 10L } },
        });
        Assert.Equal(0, mixed.Body.GetProperty("created").GetInt32());
        Assert.Contains("not valid for image", mixed.Body.GetProperty("items")[0].GetProperty("error").GetString());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/api/v1/assets/bulk-upload", admin, new { files = Enumerable.Range(0, 501).Select(i => new { fileName = $"{i}.jpg", size = 1L }) })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/api/v1/assets/bulk-upload", admin, new { files = Array.Empty<object>() })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, "/api/v1/assets/bulk-upload", admin, new { folderId = Guid.NewGuid(), files = new[] { new { fileName = "a.jpg", size = 1L } } })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "/api/v1/assets/bulk-upload", As(tenant, "Viewer"), new { files = new[] { new { fileName = "a.jpg", size = 1L } } })).Status);
    }
}
