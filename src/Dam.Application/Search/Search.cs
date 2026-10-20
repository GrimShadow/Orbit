using Dam.Application.Abstractions;
using Dam.Application.Messaging;
using Dam.Domain.Authorization;
using Dam.Domain.Common;
using Dam.Shared.Content;
using FluentValidation;

namespace Dam.Application.Search;

public sealed record SearchAssetsQuery(
    string? Text, IReadOnlyDictionary<string, string[]>? Filters, IReadOnlyList<string>? Facets, string? Sort,
    bool Highlight, Guid? SimilarTo, string? Cursor, int? Limit, bool Semantic = false) : IQuery<SearchResultDto>;

public sealed class SearchAssetsValidator : AbstractValidator<SearchAssetsQuery>
{
    public SearchAssetsValidator()
    {
        RuleFor(x => x.Limit).InclusiveBetween(1, 200).When(x => x.Limit is not null);
    }
}

/// <summary>
/// The query surface's entry point (spec Step 1.7b), replacing <c>ListAssetsHandler</c>'s in-memory row filtering:
/// the caller's access clauses are pushed into the OpenSearch query itself rather than checked one row at a time. No
/// clauses means no access - the same empty-but-not-an-error result <c>ListAssetsHandler</c> already returns.
/// </summary>
public sealed class SearchAssetsHandler(IAuthorizationService authz, ISearchQueryService search, ISemanticQueryEmbedder embedder) : IRequestHandler<SearchAssetsQuery, SearchResultDto>
{
    public async Task<Result<SearchResultDto>> Handle(SearchAssetsQuery r, CancellationToken ct)
    {
        var profile = await authz.GetProfileAsync(ct);
        var clauses = PolicyEngine.GetScope(profile, Permissions.AssetsRead);
        if (clauses.Count == 0) return new SearchResultDto([], 0, [], null);

        // A semantic search needs the query as vectors (ADR-0035). If the provider cannot supply them the search still runs, as an ordinary one, and says why.
        var query = new SearchQuery(r.Text, r.Filters, r.Facets, r.Sort, r.Highlight, r.SimilarTo, r.Cursor, r.Limit, r.Semantic);
        query = query with { Vectors = await SemanticVectors.ForAsync(embedder, query, ct) };
        return await search.SearchAsync(query, clauses, profile.UserId, ct);
    }
}

/// <summary>Embeds a semantic query's words for a hybrid search; shared by a direct search and by running a saved (smart collection) search.</summary>
internal static class SemanticVectors
{
    public static async Task<SemanticQueryVectors?> ForAsync(ISemanticQueryEmbedder embedder, SearchQuery query, CancellationToken ct) =>
        query.Semantic && query.SimilarTo is null && !string.IsNullOrWhiteSpace(query.Text) ? await embedder.EmbedAsync(query.Text.Trim(), ct) : null;
}

public sealed record SuggestQuery(string Prefix, int? Limit) : IQuery<IReadOnlyList<SuggestionDto>>;

public sealed class SuggestValidator : AbstractValidator<SuggestQuery>
{
    public SuggestValidator()
    {
        RuleFor(x => x.Prefix).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Limit).InclusiveBetween(1, 50).When(x => x.Limit is not null);
    }
}

public sealed class SuggestHandler(IAuthorizationService authz, ISearchQueryService search) : IRequestHandler<SuggestQuery, IReadOnlyList<SuggestionDto>>
{
    public async Task<Result<IReadOnlyList<SuggestionDto>>> Handle(SuggestQuery r, CancellationToken ct)
    {
        var profile = await authz.GetProfileAsync(ct);
        var clauses = PolicyEngine.GetScope(profile, Permissions.AssetsRead);
        if (clauses.Count == 0) return Array.Empty<SuggestionDto>();
        return Result<IReadOnlyList<SuggestionDto>>.Success(await search.SuggestAsync(r.Prefix, Math.Clamp(r.Limit ?? 10, 1, 50), clauses, profile.UserId, ct));
    }
}

/// <summary>The standalone <c>GET /facets</c>: value counts across every asset the caller may read, no query attached.
/// Callers name the exact index fields to aggregate (see <c>docs/adr/0011-search-indexing.md</c> for which mapping type
/// - and so which field path, e.g. a Text field's ".keyword" subfield - each metadata field's type gets).</summary>
public sealed record FacetsQuery(IReadOnlyList<string> Fields) : IQuery<IReadOnlyList<FacetResultDto>>;

public sealed class FacetsValidator : AbstractValidator<FacetsQuery>
{
    public FacetsValidator()
    {
        RuleFor(x => x.Fields).NotEmpty();
        RuleForEach(x => x.Fields).NotEmpty();
    }
}

public sealed class FacetsHandler(IAuthorizationService authz, ISearchQueryService search) : IRequestHandler<FacetsQuery, IReadOnlyList<FacetResultDto>>
{
    public async Task<Result<IReadOnlyList<FacetResultDto>>> Handle(FacetsQuery r, CancellationToken ct)
    {
        var profile = await authz.GetProfileAsync(ct);
        var clauses = PolicyEngine.GetScope(profile, Permissions.AssetsRead);
        if (clauses.Count == 0) return Array.Empty<FacetResultDto>();
        return Result<IReadOnlyList<FacetResultDto>>.Success(await search.FacetsAsync(r.Fields, clauses, profile.UserId, ct));
    }
}
