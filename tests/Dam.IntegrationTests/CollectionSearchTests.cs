using System.Net;
using System.Text.Json;
using Dam.Api.Auth;
using Dam.Infrastructure.Search;
using Microsoft.Extensions.DependencyInjection;

namespace Dam.IntegrationTests;

/// <summary>
/// Wires <see cref="CollectionTests"/>'s CRUD/sharing into the actual search index (spec Step 1.7c): a static
/// collection's members get real `collection_ids`, its contents can be browsed through search, a smart collection's
/// contents are its saved search re-run, and - the capstone - a collection-scoped access rule (dead since Step 1.7b,
/// see ADR-0012) now genuinely narrows what a search-scoped user sees.
/// </summary>
[Collection("pg")]
public sealed class CollectionSearchTests(PostgresFixture pg, SearchFixture os) : AssetTestBase(pg), IClassFixture<SearchFixture>
{
    protected override string? OpenSearchUrl => os.Url;

    private async Task IndexAndRefreshAsync(Guid tenant, params Guid[] assetIds)
    {
        await using (var scope = Api.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantOverride>().Value = tenant;
            var writer = scope.ServiceProvider.GetRequiredService<ISearchIndexWriter>();
            foreach (var id in assetIds) await writer.IndexAsync(id, CancellationToken.None);
        }
        var alias = IndexNaming.Alias("t-" + tenant.ToString("N")[..12]);
        var client = new OpenSearch.Net.OpenSearchLowLevelClient(new OpenSearch.Net.ConnectionConfiguration(new Uri(os.Url)));
        await client.DoRequestAsync<OpenSearch.Net.StringResponse>(OpenSearch.Net.HttpMethod.POST, $"/{alias}/_refresh", CancellationToken.None);
    }

    private async Task<JsonElement> GetDoc(Guid tenant, Guid assetId)
    {
        var alias = IndexNaming.Alias("t-" + tenant.ToString("N")[..12]);
        var client = new OpenSearch.Net.OpenSearchLowLevelClient(new OpenSearch.Net.ConnectionConfiguration(new Uri(os.Url)));
        var r = await client.DoRequestAsync<OpenSearch.Net.StringResponse>(OpenSearch.Net.HttpMethod.GET, $"/{alias}/_doc/{assetId}", CancellationToken.None);
        return JsonDocument.Parse(r.Body).RootElement.Clone();
    }

    private async Task PublishAsync(string admin, Guid assetId)
    {
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/assets/{assetId}/transitions/submit", admin, new { })).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/assets/{assetId}/transitions/approve", admin, new { })).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/assets/{assetId}/transitions/publish", admin, new { })).Status);
    }

    [Fact]
    public async Task A_static_collections_members_carry_its_id_in_their_search_document()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var a = await NewAsset(admin, brand);
        var collectionId = Id((await Call(HttpMethod.Post, "/api/v1/collections", admin, new { name = "x", type = "static", assetIds = new[] { a } })).Body, "id");

        await IndexAndRefreshAsync(t, a);

        var doc = await GetDoc(t, a);
        var ids = doc.GetProperty("_source").GetProperty("collection_ids").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Contains(collectionId.ToString(), ids);
    }

    [Fact]
    public async Task Removing_an_asset_from_a_collection_clears_it_from_the_documents_collection_ids_on_reindex()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var a = await NewAsset(admin, brand);
        var collectionId = Id((await Call(HttpMethod.Post, "/api/v1/collections", admin, new { name = "x", type = "static", assetIds = new[] { a } })).Body, "id");
        await IndexAndRefreshAsync(t, a);
        Assert.Contains(collectionId.ToString(), (await GetDoc(t, a)).GetProperty("_source").GetProperty("collection_ids").EnumerateArray().Select(x => x.GetString()));

        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/collections/{collectionId}/assets", admin, new { assetIds = new[] { a }, remove = true })).Status);
        await IndexAndRefreshAsync(t, a); // simulates what IndexConsumer does on collection.assets_changed

        Assert.DoesNotContain(collectionId.ToString(), (await GetDoc(t, a)).GetProperty("_source").GetProperty("collection_ids").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task A_static_collections_contents_are_its_members_filtered_by_the_callers_own_read_scope()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var a = await NewAsset(admin, brand, title: "In the set");
        var b = await NewAsset(admin, brand, title: "Not in the set");
        var collectionId = Id((await Call(HttpMethod.Post, "/api/v1/collections", admin, new { name = "x", type = "static", assetIds = new[] { a } })).Body, "id");
        await IndexAndRefreshAsync(t, a, b);

        var contents = await Call(HttpMethod.Get, $"/api/v1/collections/{collectionId}/assets", admin);
        Assert.Equal(HttpStatusCode.OK, contents.Status);
        var ids = contents.Body.GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([a], ids);
    }

    [Fact]
    public async Task A_smart_collections_contents_are_its_saved_search_run_now()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var a = await NewAsset(admin, brand, title: "Roadster hero shot");
        var b = await NewAsset(admin, brand, title: "Something else entirely");
        await IndexAndRefreshAsync(t, a, b);

        var collectionId = Id((await Call(HttpMethod.Post, "/api/v1/collections", admin, new { name = "Roadster search", type = "smart", query = new { query = "roadster" } })).Body, "id");

        var contents = await Call(HttpMethod.Get, $"/api/v1/collections/{collectionId}/assets", admin);
        Assert.Equal(HttpStatusCode.OK, contents.Status);
        var ids = contents.Body.GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([a], ids);
    }

    /// <summary>The capstone: a collection-scoped access rule was translated by AccessQueryTranslator since Step 1.7b
    /// but could never actually match anything (every document's collection_ids was hard-coded empty). Now that
    /// membership is real, a user with no role beyond an access rule scoped to one collection sees exactly - and only -
    /// that collection's published members.</summary>
    [Fact]
    public async Task A_collection_scoped_access_rule_now_genuinely_narrows_search_results()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var inCollection = await NewAsset(admin, brand, title: "Inside");
        var outsideCollection = await NewAsset(admin, brand, title: "Outside");
        await PublishAsync(admin, inCollection);
        await PublishAsync(admin, outsideCollection);

        var collectionId = Id((await Call(HttpMethod.Post, "/api/v1/collections", admin, new { name = "Scoped set", type = "static", assetIds = new[] { inCollection } })).Body, "id");
        await IndexAndRefreshAsync(t, inCollection, outsideCollection);

        var scopedToken = ApiTestHost.TokenFor(t, Guid.NewGuid()); // no roles at all: visibility comes only from the access rule below
        // JIT-provisions the user row and hands back its internal id - an access rule's principalId is that id, not the JWT subject.
        var userId = (await Call(HttpMethod.Get, "/api/v1/me", scopedToken)).Body.GetProperty("userId").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await Call(HttpMethod.Post, "/api/v1/access-rules", admin, new
        {
            principalType = "user",
            principalId = userId,
            scopeType = "collection",
            scopeValue = collectionId,
            permissions = new[] { "assets.read" },
        })).Status);

        var result = await Call(HttpMethod.Post, "/api/v1/search", scopedToken, new { });
        Assert.Equal(HttpStatusCode.OK, result.Status);
        var ids = result.Body.GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([inCollection], ids);
    }
}
