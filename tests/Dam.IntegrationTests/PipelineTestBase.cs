using System.Net;
using System.Text.Json;
using Amazon.S3.Model;
using Dam.Infrastructure;
using Dam.Infrastructure.Consumers;
using Dam.Infrastructure.Delivery;
using Dam.Infrastructure.Ingest;
using Dam.Infrastructure.Messaging;
using Dam.Infrastructure.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dam.IntegrationTests;

/// <summary>Helpers for tests that run the worker: start it, push a file through the whole ingest pipeline, wait for the result.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Disposed in DisposeAsync (xUnit IAsyncLifetime).")]
public abstract class PipelineTestBase(PostgresFixture pg, RabbitFixture mq) : AssetTestBase(pg)
{
    private static readonly HttpClient S3Http = new();
    private HttpClient? _delivery;
    private WebApplication? _app;

    /// <summary>An in-process delivery origin backed by the same fixture database and storage, started lazily on first use.</summary>
    protected async Task<HttpClient> Delivery()
    {
        if (_delivery is not null) return _delivery;
        _app = DeliveryHost.Build([], b =>
        {
            b.WebHost.UseUrls("http://127.0.0.1:0");
            b.Logging.SetMinimumLevel(LogLevel.Warning);
            b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DAM_DB_CONNECTION"] = Pg.AppConnection, ["DAM_S3_ENDPOINT"] = Pg.S3Endpoint, ["DAM_S3_ACCESS_KEY"] = Pg.S3AccessKey,
                ["DAM_S3_SECRET_KEY"] = Pg.S3SecretKey, ["DAM_DELIVERY_SIGNING_KEY"] = ApiTestHost.SigningKey, ["DAM_VIDEO_PRESET"] = "ultrafast",
            });
        });
        await _app.StartAsync();
        return _delivery = new HttpClient { BaseAddress = new Uri(_app.Urls.First()), Timeout = TimeSpan.FromSeconds(60) };
    }

    public override async Task DisposeAsync()
    {
        _delivery?.Dispose();
        if (_app is not null) await _app.DisposeAsync();
        await base.DisposeAsync();
    }

    protected static string PathOf(string url) => new Uri(url).PathAndQuery;

    /// <param name="settings">Extra or overriding configuration (for example the ClamAV address).</param>
    /// <param name="renditions">Register the rendition consumer. Leave it out to exercise on-demand rendering.</param>
    protected IHost StartWorker(Dictionary<string, string?>? settings = null, bool renditions = true)
    {
        var b = Host.CreateApplicationBuilder();
        b.Logging.SetMinimumLevel(LogLevel.Warning);
        var values = new Dictionary<string, string?>
        {
            ["DAM_S3_ENDPOINT"] = Pg.S3Endpoint,
            ["DAM_S3_ACCESS_KEY"] = Pg.S3AccessKey,
            ["DAM_S3_SECRET_KEY"] = Pg.S3SecretKey,
            ["DAM_FFPROBE_PATH"] = "ffprobe-not-installed-in-tests",
            ["DAM_COPY_MULTIPART_THRESHOLD_MB"] = "5",
            ["DAM_COPY_PART_MB"] = "5",     // exercise the multipart-copy path with small files
            ["DAM_VIDEO_PRESET"] = "ultrafast",
        };
        foreach (var (k, v) in settings ?? []) values[k] = v;
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        b.Services.AddDamWorkerIdentity();
        b.Services.AddDamInfrastructure(Pg.AppConnection);
        b.Services.AddDamStorage(cfg);
        b.Services.AddDamIngest(cfg);
        b.Services.AddDamRendering(cfg);
        b.Services.AddDamMessaging(new MessagingOptions { RabbitUrl = mq.Url, MaxAttempts = 2, RetryBaseDelayMs = 100, OutboxPollMs = 100 });
        b.Services.AddEventConsumer<HashUploadConsumer>();
        b.Services.AddEventConsumer<ScanUploadConsumer>();
        b.Services.AddEventConsumer<SniffUploadConsumer>();
        b.Services.AddEventConsumer<ExtractUploadConsumer>();
        b.Services.AddEventConsumer<PromoteUploadConsumer>();
        b.Services.AddEventConsumer<PerceptualHashConsumer>();
        if (renditions) b.Services.AddEventConsumer<RenditionConsumer>();
        b.Services.AddOutboxRelay(Pg.SystemConnection);
        return b.Build();
    }

    /// <summary>Uploads a file to an asset, straight to storage, and waits for the pipeline to finish; returns the final upload.</summary>
    protected async Task<JsonElement> Ingest(string token, Guid asset, byte[] file, string fileName, string contentType = "image/jpeg")
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
        var result = await WaitForEnd(token, id);
        if (result.GetProperty("status").GetString() is "failed" or "blocked")
            Console.WriteLine($"Ingest ended as {result.GetProperty("status").GetString()}: {result.GetProperty("error").GetString()}");
        return result;
    }

    protected async Task<JsonElement> WaitForEnd(string token, Guid upload, int seconds = 60)
    {
        for (var i = 0; i < seconds * 5; i++)
        {
            var u = (await Call(HttpMethod.Get, $"/api/v1/uploads/{upload}", token)).Body;
            if (u.GetProperty("status").GetString() is "ingested" or "blocked" or "failed" or "duplicate") return u;
            await Task.Delay(200);
        }
        throw new TimeoutException("The ingest pipeline did not finish.");
    }

    protected async Task<bool> InStore(string bucket, string key)
    {
        using var s3 = Pg.S3Client();
        try { await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = key }); return true; }
        catch (Amazon.S3.AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound) { return false; }
    }

    protected async Task<JsonElement> Versions(string token, Guid asset) => (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/versions", token)).Body;
}
