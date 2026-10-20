using System.Net;
using System.Text.Json;
using Dam.Infrastructure.Messaging;
using Dam.Infrastructure.Search;
using Dam.UnitTests.Support;
using Microsoft.Extensions.Hosting;
using OpenSearch.Net;
using RabbitMQ.Client;
using HttpMethod = System.Net.Http.HttpMethod;

namespace Dam.IntegrationTests;

/// <summary>
/// What every test of the AI worker needs: a fake sidecar and a fake OpenAI-compatible service, an API and a worker pointed at them, and the means to wait for
/// the AI's results. The database AND the RabbitMQ queues are shared by all the tests of a class (and the queues by all classes), so work an earlier test
/// left behind belongs to tenants that DID enable AI and would be done by this test's worker against this test's fakes: <see cref="DropLeftoverWork"/> clears it,
/// and any test that asserts "nothing was called" must call it before starting the worker.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Disposed in DisposeAsync (xUnit IAsyncLifetime).")]
public abstract class AiWorkerTestBase(PostgresFixture pg, RabbitFixture mq, SearchFixture os) : PipelineTestBase(pg, mq)
{
    private readonly string _rabbitUrl = mq.Url;
    protected FakeSidecar Sidecar { get; private set; } = null!;
    protected FakeOpenAi OpenAi { get; private set; } = null!;
    private static int _variant;

    protected override string? OpenSearchUrl => os.Url;

    protected override IReadOnlyDictionary<string, string?>? ApiEnvironment => AiEnvironment();

    protected Dictionary<string, string?> AiEnvironment() => new()
    {
        ["DAM_AI_PROVIDER"] = "self-hosted",
        ["DAM_AI_SIDECAR_URL"] = Sidecar.Url,
        ["DAM_AI_BASE_URL"] = OpenAi.Url + "/v1",
        ["DAM_AI_API_KEY"] = OpenAi.ExpectedKey,
        ["DAM_AI_RETRY_BASE_MS"] = "1",
        ["DAM_AI_RETRIES"] = "2",
        ["DAM_AI_FAILURE_THRESHOLD"] = "1000",
        ["DAM_AI_EMBEDDING_MODEL"] = "embed-x",
    };

    public override async Task InitializeAsync()
    {
        Sidecar = await FakeSidecar.StartAsync();
        OpenAi = await FakeOpenAi.StartAsync();
        await base.InitializeAsync();
    }

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await Sidecar.DisposeAsync();
        await OpenAi.DisposeAsync();
    }

    /// <summary>The queues of the AI consumers (enrichment and translation) are purged, and every event still waiting in the outbox is marked as sent, so only
    /// work created after this call reaches this test's worker.</summary>
    protected async Task DropLeftoverWork()
    {
        Sql("UPDATE outbox SET published_at = now() WHERE published_at IS NULL");
        var conn = await new ConnectionFactory { Uri = new Uri(_rabbitUrl) }.CreateConnectionAsync();
        try
        {
            foreach (var consumer in new[] { "ai-enrich", "ai-translate" })
            {
                await using var ch = await conn.CreateChannelAsync();   // a failed purge closes its channel, so each queue gets its own
                try { await ch.QueuePurgeAsync(Topology.Queue(consumer)); }
                catch (RabbitMQ.Client.Exceptions.OperationInterruptedException) { /* the queue does not exist yet: nothing is left over */ }
            }
        }
        finally { await conn.CloseAsync(); }
    }

    protected IHost Worker()
    {
        var settings = new Dictionary<string, string?>(AiEnvironment())
        {
            ["DAM_SCAN_MODE"] = "off",
            ["DAM_OPENSEARCH_URL"] = os.Url,
            ["DAM_FFPROBE_PATH"] = "ffprobe",
        };
        return StartWorker(settings, renditions: true, indexing: true, ai: true);
    }

    protected async Task EnableAi(string admin, string? provider = null)
    {
        var r = await Call(HttpMethod.Put, "/api/v1/ai/settings", admin, new { enabled = true, provider, acknowledgeOffSite = true });
        Assert.Equal(HttpStatusCode.OK, r.Status);
    }

    protected static byte[] Picture(byte r = 220, byte g = 30, byte b = 30) => TestImages.Solid(r, g, b, Interlocked.Increment(ref _variant));

    protected static int NextVariant() => Interlocked.Increment(ref _variant);

    protected async Task<Guid> IngestedAsset(string admin, Guid folder, byte[] file, string type, string name, string mime, string title = "IMG_0042")
    {
        var asset = await NewAsset(admin, folder, type, title);
        var upload = await Ingest(admin, asset, file, name, mime);
        Assert.Equal("ingested", upload.GetProperty("status").GetString());
        return asset;
    }

    protected async Task<JsonElement> WaitForAi(string token, Guid asset, params string[] statuses)
    {
        JsonElement last = default;
        for (var i = 0; i < 400; i++)
        {
            last = (await Call(HttpMethod.Get, $"/api/v1/assets/{asset}/ai", token)).Body;
            if (last.ValueKind == JsonValueKind.Object && statuses.Contains(last.GetProperty("status").GetString())) return last;
            await Task.Delay(250);
        }
        throw new TimeoutException($"The asset never reached {string.Join("/", statuses)}; last answer: {last}");
    }

    protected async Task<JsonElement> IndexedDoc(Guid tenant, Guid asset, Func<JsonElement, bool> ready)
    {
        var alias = AliasFor(tenant);
        var client = new OpenSearchLowLevelClient(new ConnectionConfiguration(new Uri(os.Url)));
        for (var i = 0; i < 200; i++)
        {
            var r = await client.DoRequestAsync<StringResponse>(OpenSearch.Net.HttpMethod.GET, $"/{alias}/_doc/{asset}", default);
            if (r.Success)
            {
                var source = JsonDocument.Parse(r.Body).RootElement.GetProperty("_source").Clone();
                if (ready(source)) return source;
            }
            await Task.Delay(250);
        }
        throw new TimeoutException("The asset's search document never got the AI data.");
    }

    protected static string AliasFor(Guid tenant) => IndexNaming.Alias("t-" + tenant.ToString("N")[..12]);

    protected async Task RefreshAsync(Guid tenant) =>
        await new OpenSearchLowLevelClient(new ConnectionConfiguration(new Uri(os.Url))).DoRequestAsync<StringResponse>(OpenSearch.Net.HttpMethod.POST, $"/{AliasFor(tenant)}/_refresh", default);
}
