namespace Dam.Shared.Content;

/// <param name="Score">Relevance score for a text query; the same, uninformative constant for every hit when
/// <c>Query</c> is empty (a pure filter/browse request) or a request sorted by something other than relevance.</param>
/// <param name="Highlights">Field name to matched fragments, only present when the request asked for highlighting.</param>
/// <param name="MatchedBy">Why the asset matched: text (title or metadata words, or the meaning of the text), image (the query looks like the picture),
/// ocr (words read from the picture or pages), summary (words in the AI-written summary of a document) and transcript (words spoken). Null for a browse request with no text query.</param>
/// <param name="TranscriptMatches">Up to three transcript lines containing the query, with their start time, for "jump to this moment".</param>
public sealed record SearchHitDto(
    Guid Id, string Type, string Status, string Title, Guid? FolderId, string? FolderPath, IReadOnlyList<string> Tags,
    IReadOnlyList<string> TaxonomyTerms, Guid? OwnerId, DateTimeOffset? PublishAt, DateTimeOffset? ExpireAt,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, double Score, IReadOnlyDictionary<string, IReadOnlyList<string>>? Highlights,
    IReadOnlyList<string>? MatchedBy = null, IReadOnlyList<TranscriptMatchDto>? TranscriptMatches = null);

public sealed record TranscriptMatchDto(long StartMs, string Text);

public sealed record FacetValueDto(string Value, long Count);

/// <param name="Field">The exact index field the count was aggregated on, echoing the request (e.g. "type", "status",
/// "metadata.category.keyword", "terms.path").</param>
public sealed record FacetResultDto(string Field, IReadOnlyList<FacetValueDto> Values);

/// <param name="NextCursor">Pass back as the next request's cursor; null means there are no more results. For a semantic search it is an opaque
/// offset into the fused result list, not a live cursor.</param>
/// <param name="Semantic">True when a hybrid (text plus meaning) search actually ran.</param>
/// <param name="SemanticLegs">Which parts of the hybrid ran: text (the words), embedding of the words (text) and image (what the pictures look like).</param>
/// <param name="SemanticFallback">Why a requested semantic search ran as an ordinary text search instead (ai_disabled, embeddings_disabled,
/// provider_unavailable, image_embeddings_unsupported, empty_query, similar_to, vectors_not_indexed); null otherwise.</param>
public sealed record SearchResultDto(
    IReadOnlyList<SearchHitDto> Hits, long Total, IReadOnlyList<FacetResultDto> Facets, string? NextCursor,
    bool Semantic = false, IReadOnlyList<string>? SemanticLegs = null, string? SemanticFallback = null);

public sealed record SuggestionDto(Guid Id, string Title, string Type);
