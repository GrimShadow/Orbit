using System.Net;
using System.Text.Json;
using Dam.Api.Auth;
using Dam.Domain.Authorization;
using Dam.Infrastructure.Search;
using Microsoft.Extensions.DependencyInjection;

namespace Dam.IntegrationTests;

/// <summary>
/// End-to-end over the real HTTP API and a real OpenSearch (spec Step 1.7b). There is no worker/consumer running here,
/// so each test indexes its own assets directly through <see cref="ISearchIndexWriter"/> (exactly what
/// <c>IndexConsumer</c> would do off an event) rather than waiting on a message bus. Access-control correctness itself
/// is proven exhaustively at the unit level (<c>AccessQueryTranslatorTests</c>, one case per <see cref="ScopeClause"/>
/// branch); the scope test here exists to prove the whole pipeline - real policy engine, real translator, real
/// OpenSearch - agrees with those unit tests, not to re-enumerate every branch again.
/// </summary>
[Collection("pg")]
public sealed class SearchApiTests(PostgresFixture pg, SearchFixture os) : AssetTestBase(pg), IClassFixture<SearchFixture>
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

    private async Task<JsonElement> Search(string token, object body)
    {
        var r = await Call(HttpMethod.Post, "/api/v1/search", token, body);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        return r.Body;
    }

    private static List<string> Titles(JsonElement result) => result.GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("title").GetString()!).ToList();

    [Fact]
    public async Task A_text_query_finds_matching_titles_and_ranks_the_better_match_first()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var a = await NewAsset(admin, brand, title: "Roadster hero shot");
        var b = await NewAsset(admin, brand, title: "A completely unrelated thing");
        await IndexAndRefreshAsync(t, a, b);

        var result = await Search(admin, new { query = "roadster" });
        Assert.Equal(1, result.GetProperty("total").GetInt64());
        Assert.Equal("Roadster hero shot", result.GetProperty("hits")[0].GetProperty("title").GetString());
        Assert.True(result.GetProperty("hits")[0].GetProperty("score").GetDouble() > 0);
    }

    [Fact]
    public async Task Filters_narrow_results_to_an_exact_field_value()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var image = await NewAsset(admin, brand, type: "image", title: "An image");
        await IndexAndRefreshAsync(t, image);

        var matching = await Search(admin, new { filters = new Dictionary<string, string[]> { ["type"] = ["image"] } });
        Assert.Equal(1, matching.GetProperty("total").GetInt64());

        var nonMatching = await Search(admin, new { filters = new Dictionary<string, string[]> { ["type"] = ["video"] } });
        Assert.Equal(0, nonMatching.GetProperty("total").GetInt64());
    }

    [Fact]
    public async Task Facets_on_a_search_request_count_values_among_the_matched_hits()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var a = await NewAsset(admin, brand, type: "image");
        var b = await NewAsset(admin, brand, type: "image");
        await IndexAndRefreshAsync(t, a, b);

        var result = await Search(admin, new { facets = new[] { "type" } });
        var facet = result.GetProperty("facets").EnumerateArray().Single(f => f.GetProperty("field").GetString() == "type");
        var bucket = facet.GetProperty("values").EnumerateArray().Single(v => v.GetProperty("value").GetString() == "image");
        Assert.Equal(2, bucket.GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task Highlight_returns_the_matched_fragment_wrapped_in_em_tags()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var a = await NewAsset(admin, brand, title: "Roadster hero shot");
        await IndexAndRefreshAsync(t, a);

        var result = await Search(admin, new { query = "roadster", highlight = true });
        var highlights = result.GetProperty("hits")[0].GetProperty("highlights");
        Assert.Contains("<em>Roadster</em>", highlights.GetProperty("title")[0].GetString());
    }

    [Fact]
    public async Task Similar_to_finds_other_assets_and_excludes_the_one_asked_about()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var a = await NewAsset(admin, brand, title: "Roadster hero shot", metadata: Meta("A red convertible roadster on a coastal highway"));
        var b = await NewAsset(admin, brand, title: "Roadster studio shot", metadata: Meta("A red convertible roadster in a studio"));
        var c = await NewAsset(admin, brand, title: "Unrelated", metadata: Meta("A grey office building"));
        await IndexAndRefreshAsync(t, a, b, c);

        var result = await Search(admin, new { similarTo = a });
        var ids = result.GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("id").GetGuid()).ToList();
        Assert.DoesNotContain(a, ids);
        Assert.Contains(b, ids);
    }

    [Fact]
    public async Task Sort_and_cursor_pagination_walk_through_every_hit_exactly_once()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++) ids.Add(await NewAsset(admin, brand, title: $"Asset {i}"));
        await IndexAndRefreshAsync(t, ids.ToArray());

        var seen = new List<Guid>();
        string? cursor = null;
        for (var page = 0; page < 10 && seen.Count < ids.Count; page++)
        {
            var result = await Search(admin, new { sort = "title", limit = 2, cursor });
            foreach (var hit in result.GetProperty("hits").EnumerateArray()) seen.Add(hit.GetProperty("id").GetGuid());
            cursor = result.TryGetProperty("nextCursor", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            if (cursor is null) break;
        }
        Assert.Equal(ids.OrderBy(x => x).ToArray(), seen.OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task Suggest_returns_title_matches_for_a_prefix()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var a = await NewAsset(admin, brand, title: "Roadster hero shot");
        await IndexAndRefreshAsync(t, a);

        var r = await Call(HttpMethod.Get, "/api/v1/search/suggest?q=Roadst", admin);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Contains(r.Body.EnumerateArray(), s => s.GetProperty("id").GetGuid() == a);
    }

    [Fact]
    public async Task The_standalone_facets_endpoint_counts_across_the_whole_visible_corpus_with_no_query()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var a = await NewAsset(admin, brand, type: "image");
        var b = await NewAsset(admin, brand, type: "image");
        await IndexAndRefreshAsync(t, a, b);

        var r = await Call(HttpMethod.Get, "/api/v1/facets?fields=type", admin);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        var bucket = r.Body.EnumerateArray().Single(f => f.GetProperty("field").GetString() == "type")
            .GetProperty("values").EnumerateArray().Single(v => v.GetProperty("value").GetString() == "image");
        Assert.Equal(2, bucket.GetProperty("count").GetInt64());
    }

    /// <summary>Proves the real pipeline (policy engine -> AccessQueryTranslator -> OpenSearch) agrees with
    /// AccessQueryTranslatorTests's unit-level claims: a Viewer sees only published assets; a Contributor additionally
    /// sees their own unpublished ones, but not someone else's; Admin sees everything.</summary>
    [Fact]
    public async Task Search_enforces_publication_status_and_ownership_exactly_like_the_in_memory_policy_engine()
    {
        var (t, admin, brand) = await TenantWithPacks();
        var ownContributor = As(t, BuiltInRoles.Contributor);
        var otherContributor = As(t, BuiltInRoles.Contributor);
        var viewer = As(t, BuiltInRoles.Viewer);

        var draft = await NewAsset(ownContributor, null, title: "Own unpublished draft");
        var published = await NewAsset(admin, brand, title: "Published asset");
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/assets/{published}/transitions/submit", admin, new { })).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/assets/{published}/transitions/approve", admin, new { })).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/v1/assets/{published}/transitions/publish", admin, new { })).Status);
        await IndexAndRefreshAsync(t, draft, published);

        Assert.Equal(["Own unpublished draft", "Published asset"], Titles(await Search(ownContributor, new { sort = "title" })));
        Assert.Equal(["Published asset"], Titles(await Search(otherContributor, new { sort = "title" })));
        Assert.Equal(["Published asset"], Titles(await Search(viewer, new { sort = "title" })));
        Assert.Equal(["Own unpublished draft", "Published asset"], Titles(await Search(admin, new { sort = "title" })));
    }
}
