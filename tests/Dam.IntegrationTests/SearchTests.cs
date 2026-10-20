using System.Text.Json;
using Dam.Infrastructure.Persistence;
using Dam.Infrastructure.Search;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using OpenSearch.Net;
using HttpMethod = OpenSearch.Net.HttpMethod;

namespace Dam.IntegrationTests;

/// <summary>A single-node OpenSearch (the same image the dev stack's "search" profile uses), shared by the "pg" collection.</summary>
public sealed class SearchFixture : IAsyncLifetime
{
    private readonly IContainer _os = new ContainerBuilder("opensearchproject/opensearch:2.17.1")
        .WithEnvironment("discovery.type", "single-node")
        .WithEnvironment("DISABLE_SECURITY_PLUGIN", "true")
        .WithEnvironment("DISABLE_INSTALL_DEMO_CONFIG", "true")
        // A throwaway node must not refuse to create indexes because the machine running the tests is nearly full (the flood-stage disk watermark).
        .WithEnvironment("cluster.routing.allocation.disk.threshold_enabled", "false")
        .WithEnvironment("OPENSEARCH_JAVA_OPTS", "-Xms256m -Xmx256m")
        .WithPortBinding(9200, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9200).ForPath("/_cluster/health")))
        .Build();

    public string Url => $"http://{_os.Hostname}:{_os.GetMappedPublicPort(9200)}";
    public Task InitializeAsync() => _os.StartAsync();
    public Task DisposeAsync() => _os.DisposeAsync().AsTask();
}

/// <summary>
/// Exercises <see cref="OpenSearchIndexService"/> directly against a real OpenSearch (spec Step 1.7). The event-driven
/// path (<c>IndexConsumer</c> reacting to asset events over RabbitMQ) is covered by
/// <see cref="SearchConsumerTests"/>; these tests call the service the consumer calls, so failures point straight at
/// mapping/indexing logic rather than the message bus.
/// </summary>
[Collection("pg")]
public sealed class SearchTests(PostgresFixture pg, SearchFixture os) : AssetTestBase(pg), IClassFixture<SearchFixture>
{
    private static IOpenSearchLowLevelClient Client(SearchFixture os) => new OpenSearchLowLevelClient(new ConnectionConfiguration(new Uri(os.Url)));

    private OpenSearchIndexService Svc(DamDbContext db, TestTenant tenant) =>
        new(Client(os), db, tenant, new SystemClock(), NullLogger<OpenSearchIndexService>.Instance);

    private static string AliasFor(Guid tenantId) => IndexNaming.Alias("t-" + tenantId.ToString("N")[..12]);

    private async Task<JsonElement?> GetDoc(string alias, Guid id)
    {
        var r = await Client(os).DoRequestAsync<StringResponse>(HttpMethod.GET, $"/{alias}/_doc/{id}", CancellationToken.None);
        return r.Success ? JsonDocument.Parse(r.Body).RootElement.Clone() : null;
    }

    [Fact]
    public async Task Indexing_an_asset_makes_it_retrievable_and_searchable_by_title()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var tenant = new TestTenant(t);
        await using var db = Pg.AppContext(tenant);
        var assetId = await NewAsset(admin, brand, title: "Roadster hero shot");

        await Svc(db, tenant).IndexAsync(assetId, CancellationToken.None);

        var alias = AliasFor(t);
        var doc = await GetDoc(alias, assetId);
        Assert.NotNull(doc);
        Assert.Equal("Roadster hero shot", doc!.Value.GetProperty("_source").GetProperty("title").GetString());

        // A GET by id reads straight from the primary and needs no refresh; _search does not see a write until the next
        // one (near-real-time, ~1s by default) - forced here so the test is deterministic instead of racing the clock.
        await Client(os).DoRequestAsync<StringResponse>(HttpMethod.POST, $"/{alias}/_refresh", CancellationToken.None);
        var search = await Client(os).DoRequestAsync<StringResponse>(HttpMethod.POST, $"/{alias}/_search", CancellationToken.None,
            PostData.String("""{"query":{"match":{"title":"roadster"}}}"""));
        Assert.True(search.Success);
        using var results = JsonDocument.Parse(search.Body);
        Assert.True(results.RootElement.GetProperty("hits").GetProperty("total").GetProperty("value").GetInt32() >= 1);
    }

    [Fact]
    public async Task Deleting_an_asset_removes_its_document()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var tenant = new TestTenant(t);
        await using var db = Pg.AppContext(tenant);
        var assetId = await NewAsset(admin, brand);
        var svc = Svc(db, tenant);

        await svc.IndexAsync(assetId, CancellationToken.None);
        Assert.NotNull(await GetDoc(AliasFor(t), assetId));

        await svc.DeleteAsync(assetId, CancellationToken.None);
        Assert.Null(await GetDoc(AliasFor(t), assetId));
    }

    [Fact]
    public async Task A_soft_deleted_asset_is_removed_from_the_index_rather_than_reindexed()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var tenant = new TestTenant(t);
        await using var db = Pg.AppContext(tenant);
        var assetId = await NewAsset(admin, brand);
        var svc = Svc(db, tenant);
        await svc.IndexAsync(assetId, CancellationToken.None);

        Sql($"UPDATE assets SET status = 'deleted' WHERE id = '{assetId}'");
        await svc.IndexAsync(assetId, CancellationToken.None); // the event that fires after a delete must not resurrect the document

        Assert.Null(await GetDoc(AliasFor(t), assetId));
    }

    [Fact]
    public async Task Reindex_builds_a_fresh_index_and_swaps_the_alias_onto_it()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var tenant = new TestTenant(t);
        await using var db = Pg.AppContext(tenant);
        var svc = Svc(db, tenant);
        var alive = new List<Guid>();
        for (var i = 0; i < 5; i++) alive.Add(await NewAsset(admin, brand, title: $"Asset {i}"));
        var deleted = await NewAsset(admin, brand, title: "Gone");
        Sql($"UPDATE assets SET status = 'deleted' WHERE id = '{deleted}'");

        await svc.EnsureIndexAsync(CancellationToken.None); // establishes the (still empty) index the reindex will replace
        var alias = AliasFor(t);
        var oldIndex = await CurrentBackingIndex(alias);

        var count = await svc.ReindexAsync(CancellationToken.None);
        Assert.Equal(5, count); // the soft-deleted asset is excluded

        var newIndex = await CurrentBackingIndex(alias);
        Assert.NotEqual(oldIndex, newIndex);
        var oldStillThere = await Client(os).DoRequestAsync<StringResponse>(HttpMethod.HEAD, $"/{oldIndex}", CancellationToken.None);
        Assert.False(oldStillThere.Success); // the replaced index is dropped once the alias no longer points to it

        foreach (var id in alive) Assert.NotNull(await GetDoc(alias, id));
        Assert.Null(await GetDoc(alias, deleted));
    }

    [Fact]
    public async Task The_mapping_resolves_a_taxonomy_fields_codes_into_the_nested_terms_array()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var tenant = new TestTenant(t);
        await using var db = Pg.AppContext(tenant);
        var assetId = await NewAsset(admin, brand, metadata: new { description = new { en = "x" }, vehicle = new[] { "acme_roadster_gt_2026" } });

        await Svc(db, tenant).IndexAsync(assetId, CancellationToken.None);

        var terms = (await GetDoc(AliasFor(t), assetId))!.Value.GetProperty("_source").GetProperty("terms").EnumerateArray().ToList();
        Assert.Contains(terms, e => e.GetProperty("field").GetString() == "vehicle" && e.GetProperty("code").GetString() == "acme_roadster_gt_2026");
    }

    private async Task<string> CurrentBackingIndex(string alias)
    {
        var r = await Client(os).DoRequestAsync<StringResponse>(HttpMethod.GET, $"/_alias/{alias}", CancellationToken.None);
        using var doc = JsonDocument.Parse(r.Body);
        return doc.RootElement.EnumerateObject().Select(p => p.Name).Single();
    }
}
