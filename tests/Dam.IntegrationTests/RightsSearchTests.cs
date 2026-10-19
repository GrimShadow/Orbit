using System.Net;
using System.Text.Json;
using Dam.Api.Auth;
using Dam.Infrastructure.Search;
using Microsoft.Extensions.DependencyInjection;
using OpenSearch.Net;
using HttpMethod = System.Net.Http.HttpMethod;

namespace Dam.IntegrationTests;

/// <summary>
/// Rights through the search index: a consumer limited to one channel stops seeing an asset when its licence excludes that channel (the Step 2.5 accept
/// criterion, "an asset restricted to web is rejected by the PADS4 plugin listing"), and a change of rights reaches the index through the event, not by hand.
/// </summary>
[Collection("pg")]
public sealed class RightsSearchTests(PostgresFixture pg, RabbitFixture mq, SearchFixture os) : PipelineTestBase(pg, mq), IClassFixture<RabbitFixture>, IClassFixture<SearchFixture>
{
    protected override string? OpenSearchUrl => os.Url;

    private static IOpenSearchLowLevelClient ClientFor(SearchFixture os) => new OpenSearchLowLevelClient(new ConnectionConfiguration(new Uri(os.Url)));

    private async Task IndexAndRefreshAsync(Guid tenant, params Guid[] assetIds)
    {
        await using (var scope = Api.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantOverride>().Value = tenant;
            var writer = scope.ServiceProvider.GetRequiredService<ISearchIndexWriter>();
            foreach (var id in assetIds) await writer.IndexAsync(id, CancellationToken.None);
        }
        await RefreshAsync(tenant);
    }

    private async Task RefreshAsync(Guid tenant) =>
        await ClientFor(os).DoRequestAsync<StringResponse>(OpenSearch.Net.HttpMethod.POST, $"/{IndexNaming.Alias("t-" + tenant.ToString("N")[..12])}/_refresh", CancellationToken.None);

    private async Task<List<Guid>> Found(string token)
    {
        var r = await Call(HttpMethod.Post, "/api/v1/search", token, new { sort = "title" });
        Assert.Equal(HttpStatusCode.OK, r.Status);
        return r.Body.GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("id").GetGuid()).ToList();
    }

    private async Task<Guid> PublishedAsset(string admin, Guid folder, string[] channels, string title = "Acme hero")
    {
        var asset = await NewAsset(admin, folder, title: title, metadata: new { description = new { en = "A car" }, channels, regions = Array.Empty<string>() });
        Sql($"UPDATE assets SET status = 'published' WHERE id = '{asset}'");
        return asset;
    }

    private async Task<Guid> NewRights(string admin, string name, params string[] channels)
    {
        var r = await Call(HttpMethod.Post, "/api/v1/rights", admin, new { name, licenceType = "licensed", channels });
        Assert.Equal(HttpStatusCode.Created, r.Status);
        return r.Body.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task A_search_as_a_signage_client_loses_and_regains_the_asset_as_its_rights_change()
    {
        var (t, admin, folder) = await TenantWithPacks();
        var signage = ApiTestHost.TokenFor(t, Guid.NewGuid(), roles: ["ApiClient"], attributes: new() { ["channel"] = ["signage"] });
        var web = ApiTestHost.TokenFor(t, Guid.NewGuid(), roles: ["ApiClient"], attributes: new() { ["channel"] = ["web"] });
        var asset = await PublishedAsset(admin, folder, ["web", "signage"]);
        await IndexAndRefreshAsync(t, asset);
        Assert.Equal([asset], await Found(signage));
        Assert.Equal([asset], await Found(web));

        // the licence is for the web only
        var rights = await NewRights(admin, "Web only", "web");
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, $"/api/v1/assets/{asset}/rights", admin, new { rightsId = rights })).Status);
        await IndexAndRefreshAsync(t, asset);
        Assert.Empty(await Found(signage));          // the PADS4 plugin listing no longer offers it
        Assert.Equal([asset], await Found(web));
        Assert.Equal([asset], await Found(admin));   // global-access roles still see it

        // the facet over the whole visible corpus agrees
        var facets = (await Call(HttpMethod.Get, "/api/v1/facets?fields=status", signage)).Body.EnumerateArray().SelectMany(f => f.GetProperty("values").EnumerateArray()).ToList();
        Assert.Empty(facets);

        // widened to signage too (one request re-derives the asset)
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, $"/api/v1/rights/{rights}", admin, new { name = "Web only", licenceType = "licensed", channels = new[] { "web", "signage" } })).Status);
        await IndexAndRefreshAsync(t, asset);
        Assert.Equal([asset], await Found(signage));

        // cleared again
        await Call(HttpMethod.Put, $"/api/v1/assets/{asset}/rights", admin, new { rightsId = (Guid?)null });
        await IndexAndRefreshAsync(t, asset);
        Assert.Equal([asset], await Found(signage));

        // a licence that shares no channel with the metadata hides it from both clients
        await Call(HttpMethod.Put, $"/api/v1/assets/{asset}/rights", admin, new { rightsId = await NewRights(admin, "Print only", "print") });
        await IndexAndRefreshAsync(t, asset);
        Assert.Empty(await Found(signage));
        Assert.Empty(await Found(web));
        Assert.Equal([asset], await Found(admin));
    }

    [Fact]
    public async Task The_index_document_carries_the_effective_attributes_and_follows_the_rights_through_the_event_consumer()
    {
        var (t, admin, folder) = await TenantWithPacks();
        var asset = await PublishedAsset(admin, folder, ["web", "signage"]);
        using var worker = StartWorker(new Dictionary<string, string?> { ["DAM_OPENSEARCH_URL"] = os.Url }, renditions: false, indexing: true);
        await worker.StartAsync();
        var alias = IndexNaming.Alias("t-" + t.ToString("N")[..12]);
        var first = await Eventually(alias, asset, doc => doc.GetProperty("_source").GetProperty("attributes").GetProperty("channel").GetArrayLength() == 2);
        Assert.NotNull(first);

        var rights = await NewRights(admin, "Web only", "web");
        await Call(HttpMethod.Put, $"/api/v1/assets/{asset}/rights", admin, new { rightsId = rights });
        var narrowed = await Eventually(alias, asset, doc => ChannelsOf(doc).SequenceEqual(["web"]));
        Assert.NotNull(narrowed);

        // changing the record itself re-indexes every asset assigned to it
        await Call(HttpMethod.Put, $"/api/v1/rights/{rights}", admin, new { name = "Web only", licenceType = "licensed", channels = new[] { "signage" } });
        Assert.NotNull(await Eventually(alias, asset, doc => ChannelsOf(doc).SequenceEqual(["signage"])));

        await Call(HttpMethod.Put, $"/api/v1/rights/{rights}", admin, new { name = "Web only", licenceType = "licensed", channels = new[] { "print" } });
        Assert.NotNull(await Eventually(alias, asset, doc => ChannelsOf(doc).SequenceEqual(["~restricted"])));

        await Call(HttpMethod.Put, $"/api/v1/assets/{asset}/rights", admin, new { rightsId = (Guid?)null });
        Assert.NotNull(await Eventually(alias, asset, doc => ChannelsOf(doc).Order().SequenceEqual(["signage", "web"])));
        await worker.StopAsync();
    }

    private static string[] ChannelsOf(JsonElement doc) => doc.GetProperty("_source").GetProperty("attributes").GetProperty("channel").EnumerateArray().Select(x => x.GetString()!).ToArray();

    private async Task<JsonElement?> Eventually(string alias, Guid id, Func<JsonElement, bool> ok)
    {
        for (var i = 0; i < 150; i++)
        {
            var r = await ClientFor(os).DoRequestAsync<StringResponse>(OpenSearch.Net.HttpMethod.GET, $"/{alias}/_doc/{id}", CancellationToken.None);
            if (r.Success && JsonDocument.Parse(r.Body).RootElement.Clone() is var doc && ok(doc)) return doc;
            await Task.Delay(200);
        }
        return null;
    }
}
