namespace Dam.Shared.Content;

/// <param name="Score">Relevance score for a text query; the same, uninformative constant for every hit when
/// <c>Query</c> is empty (a pure filter/browse request) or a request sorted by something other than relevance.</param>
/// <param name="Highlights">Field name to matched fragments, only present when the request asked for highlighting.</param>
public sealed record SearchHitDto(
    Guid Id, string Type, string Status, string Title, Guid? FolderId, string? FolderPath, IReadOnlyList<string> Tags,
    IReadOnlyList<string> TaxonomyTerms, Guid? OwnerId, DateTimeOffset? PublishAt, DateTimeOffset? ExpireAt,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, double Score, IReadOnlyDictionary<string, IReadOnlyList<string>>? Highlights);

public sealed record FacetValueDto(string Value, long Count);

/// <param name="Field">The exact index field the count was aggregated on, echoing the request (e.g. "type", "status",
/// "metadata.category.keyword", "terms.path").</param>
public sealed record FacetResultDto(string Field, IReadOnlyList<FacetValueDto> Values);

/// <param name="NextCursor">Pass back as the next request's cursor; null means there are no more results.</param>
public sealed record SearchResultDto(IReadOnlyList<SearchHitDto> Hits, long Total, IReadOnlyList<FacetResultDto> Facets, string? NextCursor);

public sealed record SuggestionDto(Guid Id, string Title, string Type);
