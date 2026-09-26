using System.Net;
using System.Text.Json;
using Amazon.S3.Model;
using Dam.Infrastructure;
using Dam.Infrastructure.Consumers;
using Dam.Infrastructure.Ingest;
using Dam.Infrastructure.Messaging;
using Dam.Infrastructure.Storage;
using Dam.UnitTests.Support;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dam.IntegrationTests;

/// <summary>A real ClamAV daemon. It loads its signature database on start, which takes a minute or two, so it is shared by all ingest tests.</summary>
public sealed class ClamFixture : IAsyncLifetime
{
    private readonly IContainer _clam = new ContainerBuilder("clamav/clamav-debian:1.4")
        .WithPortBinding(3310, true)
        .WithEnvironment("CLAMAV_NO_FRESHCLAMD", "true")     // the image ships with a signature database; no network needed
        .WithEnvironment("CLAMAV_NO_MILTERD", "true")
        .Build();

    public string Host => _clam.Hostname;
    public int Port => _clam.GetMappedPublicPort(3310);

    public async Task InitializeAsync()
    {
        await _clam.StartAsync();
        var deadline = DateTime.UtcNow.AddMinutes(4);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                await tcp.ConnectAsync(Host, Port);
                var s = tcp.GetStream();
                await s.WriteAsync("zPING\0"u8.ToArray());
                var buf = new byte[16];
                var n = await s.ReadAsync(buf);
                if (System.Text.Encoding.ASCII.GetString(buf, 0, n).StartsWith("PONG", StringComparison.Ordinal)) return;
            }
            catch (Exception) { /* not up yet */ }
            await Task.Delay(2000);
        }
        throw new TimeoutException("ClamAV did not become ready.");
    }

    public Task DisposeAsync() => _clam.DisposeAsync().AsTask();
}

[Collection("pg")]
public sealed class IngestTests(PostgresFixture pg, RabbitFixture mq, ClamFixture clam) : AssetTestBase(pg), IClassFixture<RabbitFixture>, IClassFixture<ClamFixture>
{
    private static readonly HttpClient S3Http = new();

    private IHost StartWorker(int? clamPort = null)
    {
        var b = Host.CreateApplicationBuilder();
        b.Logging.SetMinimumLevel(LogLevel.Warning);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DAM_S3_ENDPOINT"] = Pg.S3Endpoint,
            ["DAM_S3_ACCESS_KEY"] = Pg.S3AccessKey,
            ["DAM_S3_SECRET_KEY"] = Pg.S3SecretKey,
            ["DAM_CLAMAV_HOST"] = clam.Host,
            ["DAM_CLAMAV_PORT"] = (clamPort ?? clam.Port).ToString(),
            ["DAM_FFPROBE_PATH"] = "ffprobe-not-installed-in-tests",
            ["DAM_COPY_MULTIPART_THRESHOLD_MB"] = "5",
            ["DAM_COPY_PART_MB"] = "5",     // exercise the multipart-copy path with small files
        }).Build();
        b.Services.AddDamWorkerIdentity();
        b.Services.AddDamInfrastructure(Pg.AppConnection);
        b.Services.AddDamStorage(cfg);
        b.Services.AddDamIngest(cfg);
        b.Services.AddDamMessaging(new MessagingOptions { RabbitUrl = mq.Url, MaxAttempts = 2, RetryBaseDelayMs = 100, OutboxPollMs = 100 });
        b.Services.AddEventConsumer<HashUploadConsumer>();
        b.Services.AddEventConsumer<ScanUploadConsumer>();
        b.Services.AddEventConsumer<SniffUploadConsumer>();
        b.Services.AddEventConsumer<ExtractUploadConsumer>();
        b.Services.AddEventConsumer<PromoteUploadConsumer>();
        b.Services.AddEventConsumer<PerceptualHashConsumer>();
        b.Services.AddOutboxRelay(Pg.SystemConnection);
        return b.Build();
    }

    /// <summary>Uploads a file to an asset, straight to storage, and waits for the pipeline to finish; returns the final upload.</summary>
    private async Task<JsonElement> Ingest(string token, Guid asset, byte[] file, string fileName, string contentType = "image/jpeg")
    {
        var start = (await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/uploads", token, new { fileName, size = file.Length, contentType })).Body;
        var partSize = start.GetProperty("upload").GetProperty("partSize").GetInt32();
        var etags = new List<object>();
        foreach (var p in start.GetProperty("parts").EnumerateArray())
        {
            var n = p.GetProperty("partNumber").GetInt32();
            using var res = await S3Http.PutAsync(p.GetProperty("url").GetString()!, new ByteArrayContent(file.Skip((n - 1) * partSize).Take(partSize).ToArray()));
            Assert.True(res.IsSuccessStatusCode);
            etags.Add(new { partNumber = n, eTag = res.Headers.ETag!.Tag.Trim('"') });
        }
        var id = start.GetProperty("upload").GetProperty("id").GetGuid();
        var done = await Call(HttpMethod.Post, $"/api/v1/uploads/{id}/complete", token, new { parts = etags });
        Assert.Equal(HttpStatusCode.Accepted, done.Status);
        return await WaitForEnd(token, id);
    }

    private async Task<JsonElement> WaitForEnd(string token, Guid upload, int seconds = 60)
    {
        for (var i = 0; i < seconds * 5; i++)
        {
            var u = (await Call(HttpMethod.Get, $"/api/v1/uploads/{upload}", token)).Body;
            if (u.GetProperty("status").GetString() is "ingested" or "blocked" or "failed" or "duplicate") return u;
            await Task.Delay(200);
        }
        throw new TimeoutException("The ingest pipeline did not finish.");
    }

    private async Task<bool> InStore(string bucket, string key)
    {
        using var s3 = Pg.S3Client();
        try { await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = key }); return true; }
        catch (Amazon.S3.AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound) { return false; }
    }

    private async Task<JsonElement> Versions(string token, Guid asset) => (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/versions", token)).Body;

    private async Task<string> WaitForPhash(Guid tenant, Guid asset)
    {
        for (var i = 0; i < 150; i++)
        {
            using var c = Pg.RawAppConnection(tenant);
            using var cmd = new Npgsql.NpgsqlCommand("SELECT phash FROM assets WHERE id = @id", c);
            cmd.Parameters.AddWithValue("id", asset);
            if (cmd.ExecuteScalar() is string s and not "") return s;
            await Task.Delay(200);
        }
        throw new TimeoutException("No perceptual hash was stored.");
    }

    // ---- acceptance: JPEG technical metadata -----------------------------------------------------------------

    [Fact]
    public async Task A_clean_jpeg_is_scanned_promoted_to_the_originals_bucket_and_its_technical_metadata_is_stored()
    {
        var (t, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(); await worker.StartAsync();
        var asset = await NewAsset(admin, folder);
        var file = TestImages.WithExif(TestImages.Disc(320), "AcmeCam", "Model 7");

        var u = await Ingest(admin, asset, file, "hero.jpg");
        Assert.Equal("ingested", u.GetProperty("status").GetString());

        var versions = await Versions(admin, asset);
        Assert.Equal(1, versions.GetArrayLength());
        var v = versions[0];
        Assert.Equal(1, v.GetProperty("versionNo").GetInt32());
        Assert.True(v.GetProperty("isCurrent").GetBoolean());
        Assert.Equal(file.Length, v.GetProperty("sizeBytes").GetInt64());
        Assert.Equal("image/jpeg", v.GetProperty("mime").GetString());
        var tech = v.GetProperty("technicalMetadata");
        Assert.Equal(320, tech.GetProperty("image").GetProperty("width").GetInt32());
        Assert.Equal("AcmeCam", tech.GetProperty("image").GetProperty("exif").GetProperty("Make").GetString());
        Assert.Equal("clean", tech.GetProperty("scan").GetProperty("verdict").GetString());
        Assert.DoesNotContain("storage", v.GetRawText(), StringComparison.OrdinalIgnoreCase);   // where the bytes live is not exposed

        var versionId = v.GetProperty("id").GetGuid();
        Assert.True(await InStore("dam-originals", $"{t:N}/{asset:N}/{versionId:N}"));
        Assert.False(await InStore("dam-quarantine", $"staging/{t:N}/{asset:N}/{u.GetProperty("id").GetGuid():N}"));  // staging copy is gone
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'asset.version.added'"));
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'ingest.scanned'"));
        Assert.False(string.IsNullOrEmpty(await WaitForPhash(t, asset)));
    }

    // ---- acceptance: EICAR blocked ---------------------------------------------------------------------------

    [Fact]
    public async Task The_EICAR_test_file_is_blocked_quarantined_and_never_becomes_a_version()
    {
        var (t, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(); await worker.StartAsync();
        var asset = await NewAsset(admin, folder, type: "document");

        var u = await Ingest(admin, asset, TestImages.Eicar(), "invoice.pdf", "application/pdf");
        Assert.Equal("blocked", u.GetProperty("status").GetString());
        Assert.Contains("EICAR", u.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0, (await Versions(admin, asset)).GetArrayLength());
        var a = (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}", admin)).Body;
        Assert.Equal("blocked", a.GetProperty("status").GetString());
        var id = u.GetProperty("id").GetGuid();
        Assert.True(await InStore("dam-quarantine", $"infected/staging/{t:N}/{asset:N}/{id:N}"));    // kept for investigation
        Assert.False(await InStore("dam-quarantine", $"staging/{t:N}/{asset:N}/{id:N}"));
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'ingest.blocked'"));
        Assert.Equal(0, Count(t, "SELECT count(*) FROM outbox WHERE event_type = 'asset.version.added'"));

        // a blocked asset takes no further uploads
        var again = await Call(HttpMethod.Post, $"/api/v1/assets/{asset}/uploads", admin, new { fileName = "x.pdf", size = 10, contentType = "application/pdf" });
        Assert.Equal(HttpStatusCode.Conflict, again.Status);
    }

    // ---- content over names ------------------------------------------------------------------------------------

    [Fact]
    public async Task An_executable_renamed_to_jpg_is_refused_by_what_it_is_not_what_it_is_called()
    {
        var (t, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(); await worker.StartAsync();
        var asset = await NewAsset(admin, folder);
        var exe = new byte[2048]; exe[0] = (byte)'M'; exe[1] = (byte)'Z';

        var u = await Ingest(admin, asset, exe, "holiday.jpg");
        Assert.Equal("failed", u.GetProperty("status").GetString());
        Assert.Contains("executable", u.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, (await Versions(admin, asset)).GetArrayLength());
        Assert.Equal(1, Count(t, "SELECT count(*) FROM audit_log WHERE action = 'ingest.rejected'"));
    }

    [Fact]
    public async Task A_png_uploaded_as_an_image_asset_is_accepted_even_when_named_with_the_wrong_extension()
    {
        var (_, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(); await worker.StartAsync();
        var asset = await NewAsset(admin, folder);
        var u = await Ingest(admin, asset, TestImages.Disc(128, format: ".png"), "logo.jpg");
        Assert.Equal("ingested", u.GetProperty("status").GetString());
        Assert.Equal("image/png", (await Versions(admin, asset))[0].GetProperty("mime").GetString());   // the real type wins
    }

    // ---- new versions -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_second_upload_becomes_version_two_and_the_first_stays_available()
    {
        var (t, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(); await worker.StartAsync();
        var asset = await NewAsset(admin, folder);
        Assert.Equal("ingested", (await Ingest(admin, asset, TestImages.Disc(200), "a.jpg")).GetProperty("status").GetString());
        Assert.Equal("ingested", (await Ingest(admin, asset, TestImages.Checkerboard(200), "b.jpg")).GetProperty("status").GetString());

        var versions = await Versions(admin, asset);
        Assert.Equal(2, versions.GetArrayLength());
        var byNo = versions.EnumerateArray().ToDictionary(v => v.GetProperty("versionNo").GetInt32());
        Assert.False(byNo[1].GetProperty("isCurrent").GetBoolean());
        Assert.True(byNo[2].GetProperty("isCurrent").GetBoolean());
        Assert.True(await InStore("dam-originals", $"{t:N}/{asset:N}/{byNo[1].GetProperty("id").GetGuid():N}"));
        Assert.True(await InStore("dam-originals", $"{t:N}/{asset:N}/{byNo[2].GetProperty("id").GetGuid():N}"));

        var one = await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/versions/1", admin);
        Assert.Equal(HttpStatusCode.OK, one.Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/versions/9", admin)).Status);
    }

    [Fact]
    public async Task A_file_bigger_than_the_copy_threshold_is_promoted_intact_through_the_multipart_copy_path()
    {
        var (t, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(); await worker.StartAsync();
        var asset = await NewAsset(admin, folder);
        var file = TestImages.Noise(1800, 1800, seed: 3);                     // a PNG of several MiB that cannot compress away
        Assert.True(file.Length > 5 * 1024 * 1024 + 1024, $"fixture too small: {file.Length}");

        var u = await Ingest(admin, asset, file, "big.png", "image/png");
        Assert.Equal("ingested", u.GetProperty("status").GetString());
        var v = (await Versions(admin, asset))[0];
        Assert.Equal(file.Length, v.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(u.GetProperty("sha256").GetString(), v.GetProperty("sha256").GetString());

        using var s3 = Pg.S3Client();
        using var got = await s3.GetObjectAsync("dam-originals", $"{t:N}/{asset:N}/{v.GetProperty("id").GetGuid():N}");
        using var ms = new MemoryStream(); await got.ResponseStream.CopyToAsync(ms);
        Assert.Equal(file, ms.ToArray());                                     // byte-for-byte identical
    }

    // ---- acceptance: near duplicates -------------------------------------------------------------------------

    [Fact]
    public async Task Similar_finds_a_resized_copy_and_not_a_different_picture_and_only_shows_what_the_caller_may_see()
    {
        var (t, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(); await worker.StartAsync();
        var original = await NewAsset(admin, folder, title: "Original");
        var copy = await NewAsset(admin, folder, title: "Smaller copy");
        var other = await NewAsset(admin, folder, title: "Something else");
        var disc = TestImages.Disc(300);
        await Ingest(admin, original, disc, "o.jpg");
        await Ingest(admin, copy, TestImages.Resize(disc, 120), "c.jpg");
        await Ingest(admin, other, TestImages.Checkerboard(300), "x.jpg");
        foreach (var a in new[] { original, copy, other }) await WaitForPhash(t, a);

        var r = await Call(HttpMethod.Get, $"/api/v1/assets/{original}/similar?by=phash&maxDistance=12", admin);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        var hits = r.Body.EnumerateArray().ToList();
        Assert.True(hits.Count > 0, $"no hits; phashes: {string.Join(",", await Task.WhenAll(new[] { original, copy, other }.Select(a => WaitForPhash(t, a))))} body: {r.Body}");
        Assert.Contains(hits, h => h.GetProperty("assetId").GetGuid() == copy && h.GetProperty("distance").GetInt32() <= 12);
        Assert.DoesNotContain(hits, h => h.GetProperty("assetId").GetGuid() == other);
        Assert.DoesNotContain(hits, h => h.GetProperty("assetId").GetGuid() == original);

        // a caller with no rights sees no assets, so nothing similar is revealed to them
        var nobody = await Call(HttpMethod.Get, $"/api/v1/assets/{original}/similar?by=phash", As(t, "Nobody"));
        Assert.Equal(HttpStatusCode.NotFound, nobody.Status);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Get, $"/api/v1/assets/{original}/similar?by=colour", admin)).Status);
    }

    // ---- failing safe --------------------------------------------------------------------------------------------

    [Fact]
    public async Task When_the_virus_scanner_is_unreachable_the_file_is_never_promoted_and_the_upload_is_visibly_failed()
    {
        var (t, admin, folder) = await TenantWithPacks();
        using var worker = StartWorker(clamPort: 1); await worker.StartAsync();       // nothing listens there
        var asset = await NewAsset(admin, folder);

        var u = await Ingest(admin, asset, TestImages.Disc(100), "hero.jpg");
        Assert.Equal("failed", u.GetProperty("status").GetString());
        Assert.Equal(0, (await Versions(admin, asset)).GetArrayLength());
        Assert.False(await InStore("dam-originals", $"{t:N}/{asset:N}/{u.GetProperty("id").GetGuid():N}"));
    }
}
