using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Domain.Authorization;
using Dam.Infrastructure.Persistence;
using Dam.Shared.Content;
using OpenSearch.Net;
using HttpMethod = OpenSearch.Net.HttpMethod;

namespace Dam.Infrastructure.Search;

/// <summary>
/// The search query surface (spec Step 1.7b) against a tenant's OpenSearch index. Every query is built as plain JSON
/// (like <see cref="OpenSearchIndexService"/>), not the fluent high-level DSL, for the same reason: the caller's access
/// filter (<see cref="AccessQueryTranslator"/>) and facet fields are dynamic, tenant by tenant.
/// </summary>
public sealed class OpenSearchQueryService(IOpenSearchLowLevelClient client, DamDbContext db, ITenantContext tenant) : ISearchQueryService
{
    private const int MaxFacetValues = 50;

    private static readonly Dictionary<string, string> SortFields = new()
    {
        ["created_at"] = "created_at",
        ["updated_at"] = "updated_at",
        ["publish_at"] = "publish_at",
        ["expire_at"] = "expire_at",
        ["title"] = "title.keyword",
        ["status"] = "status",
        ["type"] = "type",
    };

    public async Task<SearchResultDto> SearchAsync(SearchQuery query, IReadOnlyList<ScopeClause> scope, Guid userId, CancellationToken ct)
    {
        var alias = await TenantIndexResolver.AliasAsync(db, tenant, ct);
        var limit = Math.Clamp(query.Limit ?? 50, 1, 200);

        var filter = new List<object?> { AccessQueryTranslator.ToQuery(scope, userId) };
        object?[] must;
        if (query.SimilarTo is { } similarTo)
        {
            must = [MoreLikeThis(alias, similarTo)];
            filter.Add(new Dictionary<string, object?> { ["bool"] = new Dictionary<string, object?> { ["must_not"] = AccessQueryTranslator.Term("id", similarTo) } });
        }
        else if (!string.IsNullOrWhiteSpace(query.Text))
        {
            // lenient: the "metadata.*" wildcard sweeps in every field type a schema defines, including numbers and
            // dates that can't parse a free-text query term - without it, one such field failing to parse fails the
            // whole search (search_phase_execution_exception) instead of just not matching on that field.
            must = [new Dictionary<string, object?> { ["multi_match"] = new Dictionary<string, object?> { ["query"] = query.Text, ["fields"] = new[] { "title^3", "metadata.*" }, ["lenient"] = true } }];
        }
        else must = [new Dictionary<string, object?> { ["match_all"] = new Dictionary<string, object?>() }];

        if (query.Filters is { Count: > 0 } filters)
            foreach (var (field, values) in filters) filter.Add(AccessQueryTranslator.Terms(field, values));

        var body = new Dictionary<string, object?>
        {
            ["size"] = limit,
            ["track_total_hits"] = true,
            ["query"] = new Dictionary<string, object?> { ["bool"] = new Dictionary<string, object?> { ["must"] = must, ["filter"] = filter.ToArray() } },
            ["sort"] = BuildSort(query.Sort),
        };
        if (query.Highlight) body["highlight"] = BuildHighlight();
        if (query.Facets is { Count: > 0 } facetFields) body["aggs"] = BuildAggs(facetFields);
        if (DecodeCursor(query.Cursor) is { } after) body["search_after"] = after;

        var response = await client.DoRequestAsync<StringResponse>(HttpMethod.POST, $"/{alias}/_search", ct, Body(body));
        if (!response.Success)
            return response.HttpStatusCode == 404 ? new SearchResultDto([], 0, [], null) : throw Failed("Search", response);
        return ParseSearchResponse(response.Body, limit, query.Facets);
    }

    public async Task<IReadOnlyList<SuggestionDto>> SuggestAsync(string prefix, int limit, IReadOnlyList<ScopeClause> scope, Guid userId, CancellationToken ct)
    {
        var alias = await TenantIndexResolver.AliasAsync(db, tenant, ct);
        var body = new Dictionary<string, object?>
        {
            ["size"] = limit,
            ["_source"] = new[] { "id", "title", "type" },
            ["query"] = new Dictionary<string, object?>
            {
                ["bool"] = new Dictionary<string, object?>
                {
                    ["must"] = new Dictionary<string, object?> { ["match_phrase_prefix"] = new Dictionary<string, object?> { ["title"] = new Dictionary<string, object?> { ["query"] = prefix, ["max_expansions"] = 20 } } },
                    ["filter"] = new object?[] { AccessQueryTranslator.ToQuery(scope, userId) },
                },
            },
        };
        var response = await client.DoRequestAsync<StringResponse>(HttpMethod.POST, $"/{alias}/_search", ct, Body(body));
        if (!response.Success) return response.HttpStatusCode == 404 ? [] : throw Failed("Suggest", response);

        using var doc = JsonDocument.Parse(response.Body);
        return doc.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray()
            .Select(h => h.GetProperty("_source"))
            .Select(s => new SuggestionDto(s.GetProperty("id").GetGuid(), s.GetProperty("title").GetString() ?? "", s.GetProperty("type").GetString() ?? ""))
            .ToList();
    }

    public async Task<IReadOnlyList<FacetResultDto>> FacetsAsync(IReadOnlyList<string> fields, IReadOnlyList<ScopeClause> scope, Guid userId, CancellationToken ct)
    {
        var alias = await TenantIndexResolver.AliasAsync(db, tenant, ct);
        var body = new Dictionary<string, object?> { ["size"] = 0, ["query"] = AccessQueryTranslator.ToQuery(scope, userId), ["aggs"] = BuildAggs(fields) };
        var response = await client.DoRequestAsync<StringResponse>(HttpMethod.POST, $"/{alias}/_search", ct, Body(body));
        if (!response.Success) return response.HttpStatusCode == 404 ? [] : throw Failed("Facets", response);

        using var doc = JsonDocument.Parse(response.Body);
        return doc.RootElement.TryGetProperty("aggregations", out var aggs) ? ParseAggs(aggs, fields) : [];
    }

    private static Dictionary<string, object?> MoreLikeThis(string alias, Guid assetId) => new()
    {
        ["more_like_this"] = new Dictionary<string, object?>
        {
            ["fields"] = new[] { "title", "metadata.*" },
            ["like"] = new object?[] { new Dictionary<string, object?> { ["_index"] = alias, ["_id"] = assetId } },
            ["min_term_freq"] = 1,
            ["min_doc_freq"] = 1,
        },
    };

    private static Dictionary<string, object?> BuildAggs(IReadOnlyList<string> fields)
    {
        var aggs = new Dictionary<string, object?>();
        foreach (var field in fields.Distinct())
        {
            var termsAgg = new Dictionary<string, object?> { ["terms"] = new Dictionary<string, object?> { ["field"] = field, ["size"] = MaxFacetValues } };
            // A field under the `terms` nested array (spec: "nested terms") must be aggregated inside a nested agg.
            aggs[field] = field.StartsWith("terms.", StringComparison.Ordinal)
                ? new Dictionary<string, object?> { ["nested"] = new Dictionary<string, object?> { ["path"] = "terms" }, ["aggs"] = new Dictionary<string, object?> { [field] = termsAgg } }
                : termsAgg;
        }
        return aggs;
    }

    private static List<FacetResultDto> ParseAggs(JsonElement aggregations, IReadOnlyList<string> fields)
    {
        var results = new List<FacetResultDto>();
        foreach (var field in fields.Distinct())
        {
            if (!aggregations.TryGetProperty(field, out var agg)) continue;
            var termsAgg = field.StartsWith("terms.", StringComparison.Ordinal) && agg.TryGetProperty(field, out var nested) ? nested : agg;
            if (!termsAgg.TryGetProperty("buckets", out var buckets)) continue;
            results.Add(new FacetResultDto(field, buckets.EnumerateArray().Select(b => new FacetValueDto(BucketKey(b), b.GetProperty("doc_count").GetInt64())).ToList()));
        }
        return results;
    }

    private static string BucketKey(JsonElement bucket)
    {
        var key = bucket.GetProperty("key");
        return key.ValueKind == JsonValueKind.String ? key.GetString()! : key.GetRawText();
    }

    private static object?[] BuildSort(string? sort)
    {
        if (string.IsNullOrEmpty(sort) || sort == "_score")
            return [new Dictionary<string, object?> { ["_score"] = new Dictionary<string, object?> { ["order"] = "desc" } }, IdTiebreak];
        var descending = sort.StartsWith('-');
        var name = descending ? sort[1..] : sort;
        if (!SortFields.TryGetValue(name, out var field))
            return [new Dictionary<string, object?> { ["_score"] = new Dictionary<string, object?> { ["order"] = "desc" } }, IdTiebreak];
        return [new Dictionary<string, object?> { [field] = new Dictionary<string, object?> { ["order"] = descending ? "desc" : "asc" } }, IdTiebreak];
    }

    private static readonly Dictionary<string, object?> IdTiebreak = new() { ["id"] = new Dictionary<string, object?> { ["order"] = "asc" } };

    private static Dictionary<string, object?> BuildHighlight() => new()
    {
        ["fields"] = new Dictionary<string, object?> { ["title"] = new Dictionary<string, object?>(), ["metadata.*"] = new Dictionary<string, object?>() },
        ["pre_tags"] = new[] { "<em>" },
        ["post_tags"] = new[] { "</em>" },
    };

    private static SearchResultDto ParseSearchResponse(string json, int limit, IReadOnlyList<string>? facetFields)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var hitsEl = root.GetProperty("hits");
        var total = hitsEl.GetProperty("total").GetProperty("value").GetInt64();

        var hits = new List<SearchHitDto>();
        JsonElement? lastSort = null;
        foreach (var hit in hitsEl.GetProperty("hits").EnumerateArray())
        {
            var source = hit.GetProperty("_source");
            var score = hit.TryGetProperty("_score", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0d;
            IReadOnlyDictionary<string, IReadOnlyList<string>>? highlights = hit.TryGetProperty("highlight", out var hl)
                ? hl.EnumerateObject().ToDictionary(p => p.Name, p => (IReadOnlyList<string>)p.Value.EnumerateArray().Select(x => x.GetString()!).ToList())
                : null;
            hits.Add(new SearchHitDto(
                source.GetProperty("id").GetGuid(),
                source.GetProperty("type").GetString()!,
                source.GetProperty("status").GetString()!,
                source.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                StringOrNull(source, "folder_id") is { } fid ? Guid.Parse(fid) : null,
                StringOrNull(source, "folder_path"),
                StringArray(source, "tags"),
                StringArray(source, "taxonomy_terms"),
                StringOrNull(source, "owner_id") is { } oid ? Guid.Parse(oid) : null,
                DateOrNull(source, "publish_at"),
                DateOrNull(source, "expire_at"),
                source.GetProperty("created_at").GetDateTimeOffset(),
                source.GetProperty("updated_at").GetDateTimeOffset(),
                score,
                highlights));
            if (hit.TryGetProperty("sort", out var sortEl)) lastSort = sortEl.Clone();
        }

        var facets = facetFields is { Count: > 0 } && root.TryGetProperty("aggregations", out var aggs) ? ParseAggs(aggs, facetFields) : [];
        // Only offer a next page when this one was full - otherwise a client would keep paging into nothing.
        var nextCursor = hits.Count == limit && lastSort is { } ls ? EncodeCursor(ls) : null;
        return new SearchResultDto(hits, total, facets, nextCursor);
    }

    private static string? StringOrNull(JsonElement source, string name) =>
        source.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? DateOrNull(JsonElement source, string name) =>
        source.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetDateTimeOffset() : null;

    private static List<string> StringArray(JsonElement source, string name) =>
        source.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];

    private static string EncodeCursor(JsonElement sortValues) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(sortValues.GetRawText()));

    /// <summary>A garbled or tampered cursor starts the search over rather than failing the request.</summary>
    private static JsonElement? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor)) return null;
        try { return JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor))).RootElement.Clone(); }
        catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
    }

    private static InvalidOperationException Failed(string what, StringResponse response) => new($"{what} failed: {response.Body}");

    private static PostData Body(object value) => PostData.String(JsonSerializer.Serialize(value));
}
