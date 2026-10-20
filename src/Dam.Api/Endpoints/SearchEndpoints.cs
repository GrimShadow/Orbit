using Dam.Application.Messaging;
using Dam.Application.Search;
using Dam.Shared.Content;
using static Dam.Api.Endpoints.IdentityEndpoints;

namespace Dam.Api.Endpoints;

/// <param name="Query">Free-text query; omit for a pure filter/browse request.</param>
/// <param name="Filters">Exact-match filters, index field to allowed values - e.g. {"type": ["image"]}.</param>
/// <param name="Facets">Index fields to return value counts for alongside the hits.</param>
/// <param name="Sort">"field" or "-field" (descending); omit for relevance.</param>
/// <param name="SimilarTo">Find assets like this one instead of matching <see cref="Query"/>.</param>
/// <param name="Semantic">Hybrid search: the words, plus what the query looks like (CLIP against the pictures) and what it means (text embeddings), fused
/// with Reciprocal Rank Fusion. The sort is ignored (the order is the fused rank) and the cursor is an offset into the fused list. When the AI provider is off
/// or fails, the search runs as an ordinary one and the answer's semanticFallback says why.</param>
public sealed record SearchRequestBody(
    string? Query, IReadOnlyDictionary<string, string[]>? Filters, IReadOnlyList<string>? Facets, string? Sort,
    bool Highlight = false, Guid? SimilarTo = null, string? Cursor = null, int? Limit = null, bool Semantic = false);

/// <summary>The search query surface (spec Step 1.7b): `POST /search`, `GET /search/suggest`, `GET /facets`.</summary>
public static class SearchEndpoints
{
    public static void MapSearchEndpoints(this RouteGroupBuilder v1)
    {
        var search = v1.MapGroup("/search").WithTags("Search");

        search.MapPost("", (SearchRequestBody r, IDispatcher d, CancellationToken ct) =>
                Run(d, new SearchAssetsQuery(r.Query, r.Filters, r.Facets, r.Sort, r.Highlight, r.SimilarTo, r.Cursor, r.Limit, r.Semantic),
                    v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("SearchAssets").WithSummary("Query, filters, facets, sort, highlight, similar_to and (with semantic) hybrid text-plus-meaning search over every asset the caller may read. Hits report matchedBy and, for recordings, transcriptMatches").Produces<SearchResultDto>();

        search.MapGet("/suggest", (string q, int? limit, IDispatcher d, CancellationToken ct) =>
                Run(d, new SuggestQuery(q, limit), v => Task.FromResult(Results.Ok(v)), ct))
            .WithName("SuggestAssets").WithSummary("Title matches for typeahead").Produces<IReadOnlyList<SuggestionDto>>();

        v1.MapGet("/facets", (string fields, IDispatcher d, CancellationToken ct) =>
                Run(d, new FacetsQuery(fields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
                    v => Task.FromResult(Results.Ok(v)), ct))
            .WithTags("Search").WithName("GetFacets")
            .WithSummary("Value counts for the given index fields (comma-separated) across every asset the caller may read")
            .Produces<IReadOnlyList<FacetResultDto>>();
    }
}
