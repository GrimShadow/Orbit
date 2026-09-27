using System.Net;
using System.Text.Json;
using Dam.Infrastructure.Search;
using OpenSearch.Net;

namespace Dam.IntegrationTests;

/// <summary>End-to-end: an asset event travels API -> outbox -> RabbitMQ -> <c>IndexConsumer</c> -> OpenSearch (spec Step
/// 1.7). <see cref="SearchTests"/> covers the indexing logic itself in more depth without paying for the message bus.</summary>
[Collection("pg")]
public sealed class SearchConsumerTests(PostgresFixture pg, RabbitFixture mq, SearchFixture os)
    : PipelineTestBase(pg, mq), IClassFixture<RabbitFixture>, IClassFixture<SearchFixture>
{
    private static IOpenSearchLowLevelClient ClientFor(SearchFixture os) => new OpenSearchLowLevelClient(new ConnectionConfiguration(new Uri(os.Url)));

    [Fact]
    public async Task Creating_an_asset_indexes_it_and_deleting_it_removes_the_document()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var asset = await NewAsset(admin, brand, title: "Consumer indexed asset");

        using var worker = StartWorker(new Dictionary<string, string?> { ["DAM_OPENSEARCH_URL"] = os.Url }, renditions: false, indexing: true);
        await worker.StartAsync();

        var alias = IndexNaming.Alias("t-" + t.ToString("N")[..12]);
        var doc = await WaitFor(() => GetDoc(alias, asset));
        Assert.Equal("Consumer indexed asset", doc.GetProperty("_source").GetProperty("title").GetString());
        Assert.Equal("draft", doc.GetProperty("_source").GetProperty("status").GetString());

        var del = await Call(System.Net.Http.HttpMethod.Delete, $"/api/v1/assets/{asset}", admin);
        Assert.Equal(HttpStatusCode.NoContent, del.Status);
        await WaitForGone(alias, asset);

        await worker.StopAsync();
    }

    private async Task<JsonElement> GetDoc(string alias, Guid id)
    {
        var r = await ClientFor(os).DoRequestAsync<StringResponse>(OpenSearch.Net.HttpMethod.GET, $"/{alias}/_doc/{id}", CancellationToken.None);
        if (!r.Success) throw new InvalidOperationException("not indexed yet");
        return JsonDocument.Parse(r.Body).RootElement.Clone();
    }

    private async Task<JsonElement> WaitFor(Func<Task<JsonElement>> get)
    {
        for (var i = 0; i < 150; i++)
        {
            try { return await get(); }
            catch (InvalidOperationException) { await Task.Delay(200); }
        }
        throw new TimeoutException("The asset was never indexed.");
    }

    private async Task WaitForGone(string alias, Guid id)
    {
        for (var i = 0; i < 150; i++)
        {
            var r = await ClientFor(os).DoRequestAsync<StringResponse>(OpenSearch.Net.HttpMethod.GET, $"/{alias}/_doc/{id}", CancellationToken.None);
            if (!r.Success) return;
            await Task.Delay(200);
        }
        throw new TimeoutException("The asset was never removed from the index.");
    }
}
