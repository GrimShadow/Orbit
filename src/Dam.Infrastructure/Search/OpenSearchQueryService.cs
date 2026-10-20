using System.Text;
using System.Text.Json;
using Dam.Application.Abstractions;
using Dam.Domain.Ai;
using Dam.Domain.Authorization;
using Dam.Infrastructure.Ai;
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
public sealed class OpenSearchQueryService(IOpenSearchLowLevelClient client, DamDbContext db, ITenantContext tenant, AiOptions? ai = null) : ISearchQueryService
{
    private const int MaxFacetValues = 50;
    private readonly AiOptions _ai = ai ?? new AiOptions();

    /// <summary>What a hit needs; the stored vectors, OCR text and transcripts are large and never come back.</summary>
    private static readonly string[] SourceFields =
        ["id", "type", "status", "title", "folder_id", "folder_path", "tags", "taxonomy_terms", "owner_id", "publish_at", "expire_at", "created_at", "updated_at"];

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

        if (query.Semantic)
        {
            string? fallback = null;
            if (query.SimilarTo is not null) fallback = "similar_to";
            else if (string.IsNullOrWhiteSpace(query.Text)) fallback = "empty_query";
            else if (query.Vectors is null || (query.Vectors.Clip is null && query.Vectors.Text is null)) fallback = query.Vectors?.Fallback ?? "provider_unavailable";
            else
            {
                var hybrid = await SemanticAsync(query, alias, scope, userId, limit, ct);
                if (hybrid is not null) return hybrid;
                fallback = "vectors_not_indexed";   // the index has no usable vector fields (made before AI, or for other sizes): a reindex fixes it
            }
            return (await LexicalAsync(query, alias, scope, userId, limit, ct)) with { SemanticFallback = fallback };
        }
        return await LexicalAsync(query, alias, scope, userId, limit, ct);
    }

    /// <summary>The access filter plus the caller's exact-match filters: the same filter goes into every leg of a hybrid search.</summary>
    private static List<object?> FilterClauses(SearchQuery query, IReadOnlyList<ScopeClause> scope, Guid userId)
    {
        var filter = new List<object?> { AccessQueryTranslator.ToQuery(scope, userId) };
        if (query.Filters is { Count: > 0 } filters)
            foreach (var (field, values) in filters) filter.Add(AccessQueryTranslator.Terms(field, values));
        return filter;
    }

    private async Task<SearchResultDto> LexicalAsync(SearchQuery query, string alias, IReadOnlyList<ScopeClause> scope, Guid userId, int limit, CancellationToken ct)
    {
        var filter = new List<object?> { AccessQueryTranslator.ToQuery(scope, userId) };
        object?[] must;
        if (query.SimilarTo is { } similarTo)
        {
            must = [MoreLikeThis(alias, similarTo)];
            filter.Add(new Dictionary<string, object?> { ["bool"] = new Dictionary<string, object?> { ["must_not"] = AccessQueryTranslator.Term("id", similarTo) } });
        }
        else if (!string.IsNullOrWhiteSpace(query.Text)) must = [TextQuery(query.Text)];
        else must = [new Dictionary<string, object?> { ["match_all"] = new Dictionary<string, object?>() }];

        if (query.Filters is { Count: > 0 } filters)
            foreach (var (field, values) in filters) filter.Add(AccessQueryTranslator.Terms(field, values));

        var body = new Dictionary<string, object?>
        {
            ["size"] = limit,
            ["track_total_hits"] = true,
            ["_source"] = SourceFields,
            ["query"] = new Dictionary<string, object?> { ["bool"] = new Dictionary<string, object?> { ["must"] = must, ["filter"] = filter.ToArray() } },
            ["sort"] = BuildSort(query.Sort),
        };
        if (query.Highlight) body["highlight"] = BuildHighlight();
        if (query.Facets is { Count: > 0 } facetFields) body["aggs"] = BuildAggs(facetFields);
        if (DecodeCursor(query.Cursor) is { } after) body["search_after"] = after;

        var response = await client.DoRequestAsync<StringResponse>(HttpMethod.POST, $"/{alias}/_search", ct, Body(body));
        if (!response.Success)
            return response.HttpStatusCode == 404 ? new SearchResultDto([], 0, [], null) : throw Failed("Search", response);
        return ParseSearchResponse(response.Body, limit, query.Facets, withMatches: query.SimilarTo is null && !string.IsNullOrWhiteSpace(query.Text));
    }

    /// <summary>
    /// Words in the title or metadata, words read from a picture or page (ocr_text), words in the AI summary of a document (summary_text) and words said in a recording (the nested transcript segments, whose best
    /// lines come back as inner hits so a match can say when it was said). Each clause is named so a hit can report which of them matched.
    /// </summary>
    private static Dictionary<string, object?> TextQuery(string text) => new()
    {
        ["bool"] = new Dictionary<string, object?>
        {
            ["minimum_should_match"] = 1,
            ["should"] = new object?[]
            {
                // lenient: the "metadata.*" wildcard sweeps in every field type a schema defines, including numbers and
                // dates that can't parse a free-text query term - without it, one such field failing to parse fails the
                // whole search (search_phase_execution_exception) instead of just not matching on that field.
                new Dictionary<string, object?> { ["multi_match"] = new Dictionary<string, object?> { ["query"] = text, ["fields"] = new[] { "title^3", "metadata.*" }, ["lenient"] = true, ["_name"] = "text" } },
                new Dictionary<string, object?> { ["match"] = new Dictionary<string, object?> { [IndexMappingBuilder.OcrTextField] = new Dictionary<string, object?> { ["query"] = text, ["_name"] = "ocr" } } },
                new Dictionary<string, object?> { ["match"] = new Dictionary<string, object?> { [IndexMappingBuilder.SummaryTextField] = new Dictionary<string, object?> { ["query"] = text, ["_name"] = "summary" } } },
                // The whole transcript as text: catches a phrase that runs across two lines, and is what gets highlighted.
                new Dictionary<string, object?> { ["match"] = new Dictionary<string, object?> { [IndexMappingBuilder.TranscriptTextField] = new Dictionary<string, object?> { ["query"] = text, ["_name"] = "transcript" } } },
                new Dictionary<string, object?>
                {
                    ["nested"] = new Dictionary<string, object?>
                    {
                        ["path"] = IndexMappingBuilder.TranscriptSegmentsField,
                        ["score_mode"] = "max",
                        ["ignore_unmapped"] = true,
                        ["_name"] = "transcript",
                        ["query"] = new Dictionary<string, object?> { ["match"] = new Dictionary<string, object?> { [$"{IndexMappingBuilder.TranscriptSegmentsField}.text"] = text } },
                        ["inner_hits"] = new Dictionary<string, object?>
                        {
                            ["name"] = "segments", ["size"] = 3,
                            ["_source"] = new[] { $"{IndexMappingBuilder.TranscriptSegmentsField}.start_ms", $"{IndexMappingBuilder.TranscriptSegmentsField}.text" },
                        },
                    },
                },
            },
        },
    };

    // ---------------------------------------------------------------------------------------------- hybrid search

    private static readonly string[] MatchOrder = ["text", "image", "ocr", "summary", "transcript"];

    private const string LexicalLeg = "lexical", ImageLeg = "image", MeaningLeg = "meaning";

    private sealed record LegHit(Guid Id, double Score, JsonElement Source, IReadOnlyList<string> Matched, IReadOnlyList<TranscriptMatchDto> Transcript,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? Highlights);

    /// <summary>
    /// The hybrid search (ADR-0035): the words (lexical), what the query looks like (its CLIP embedding against the image vectors) and what it means (its
    /// text embedding against the text vectors), each limited by the SAME access and user filters, fused with Reciprocal Rank Fusion. Returns null when no
    /// vector leg could run at all (the index has no usable vector fields), so the caller falls back to plain text search.
    /// </summary>
    private async Task<SearchResultDto?> SemanticAsync(SearchQuery query, string alias, IReadOnlyList<ScopeClause> scope, Guid userId, int limit, CancellationToken ct)
    {
        var depth = _ai.SemanticDepth;
        var filter = FilterClauses(query, scope, userId).ToArray();
        var v = query.Vectors!;

        Dictionary<string, object?> Base(object q) => new() { ["size"] = depth, ["_source"] = SourceFields, ["track_total_hits"] = false, ["query"] = q };
        var lexicalBody = Base(new Dictionary<string, object?> { ["bool"] = new Dictionary<string, object?> { ["must"] = new[] { TextQuery(query.Text!) }, ["filter"] = filter } });
        if (query.Highlight) lexicalBody["highlight"] = BuildHighlight();
        Dictionary<string, object?> Knn(string field, float[] vector) => Base(new Dictionary<string, object?>
        {
            ["knn"] = new Dictionary<string, object?>
            {
                [field] = new Dictionary<string, object?>
                {
                    ["vector"] = vector, ["k"] = depth,
                    ["filter"] = new Dictionary<string, object?> { ["bool"] = new Dictionary<string, object?> { ["filter"] = filter } },
                },
            },
        });

        var lexicalTask = SearchLegAsync(alias, lexicalBody, ct);
        var imageTask = v.Clip is null ? Task.FromResult<List<LegHit>?>([]) : SearchLegAsync(alias, Knn(IndexMappingBuilder.VectorImageField, v.Clip), ct);
        var meaningTask = v.Text is null ? Task.FromResult<List<LegHit>?>([]) : SearchLegAsync(alias, Knn(IndexMappingBuilder.VectorTextField, v.Text), ct);
        await Task.WhenAll(lexicalTask, imageTask, meaningTask);
        var (lexical, image, meaning) = (await lexicalTask ?? [], await imageTask, await meaningTask);
        var requested = (v.Clip is not null ? 1 : 0) + (v.Text is not null ? 1 : 0);
        var failed = (v.Clip is not null && image is null ? 1 : 0) + (v.Text is not null && meaning is null ? 1 : 0);
        if (failed == requested) return null;   // none of the vector fields could answer

        var imageHits = Trim(image ?? [], v.ImageMinCosine ?? _ai.ImageFloor);
        var meaningHits = Trim(meaning ?? [], v.TextMinCosine ?? _ai.TextFloor(AiProviderKinds.SelfHosted));
        var legs = new List<RankedList> { new(LexicalLeg, lexical.Select(h => h.Id).ToList()), new(ImageLeg, imageHits.Select(h => h.Id).ToList()), new(MeaningLeg, meaningHits.Select(h => h.Id).ToList()) };
        var fused = RankFusion.Fuse(legs, _ai.RrfK);

        var byId = new Dictionary<Guid, LegHit>();
        foreach (var h in lexical.Concat(imageHits).Concat(meaningHits)) byId.TryAdd(h.Id, h);
        var lexicalById = lexical.ToDictionary(h => h.Id);

        var offset = DecodeOffset(query.Cursor);
        var page = fused.Skip(offset).Take(limit).ToList();
        var hits = new List<SearchHitDto>();
        foreach (var item in page)
        {
            var leg = byId[item.Id];
            var matchedBy = new List<string>();
            if (lexicalById.TryGetValue(item.Id, out var lex)) matchedBy.AddRange(lex.Matched);
            if (item.Legs.Contains(ImageLeg)) matchedBy.Add("image");
            if (item.Legs.Contains(MeaningLeg)) matchedBy.Add("text");
            var ordered = MatchOrder.Where(matchedBy.Contains).ToList();
            hits.Add(ToHit(leg.Source, item.Score, lex?.Highlights, ordered, lex?.Transcript));
        }

        IReadOnlyList<FacetResultDto> facets = [];
        if (query.Facets is { Count: > 0 } facetFields && fused.Count > 0)
        {
            var facetBody = new Dictionary<string, object?>
            {
                ["size"] = 0,
                ["query"] = new Dictionary<string, object?>
                {
                    ["bool"] = new Dictionary<string, object?>
                    {
                        ["filter"] = new object?[] { new Dictionary<string, object?> { ["ids"] = new Dictionary<string, object?> { ["values"] = fused.Select(f => f.Id.ToString()).ToArray() } }, AccessQueryTranslator.ToQuery(scope, userId) },
                    },
                },
                ["aggs"] = BuildAggs(facetFields),
            };
            var facetResponse = await client.DoRequestAsync<StringResponse>(HttpMethod.POST, $"/{alias}/_search", ct, Body(facetBody));
            if (facetResponse.Success)
            {
                using var fd = JsonDocument.Parse(facetResponse.Body);
                if (fd.RootElement.TryGetProperty("aggregations", out var aggs)) facets = ParseAggs(aggs, facetFields);
            }
        }

        var next = offset + limit < fused.Count ? EncodeOffset(offset + limit) : null;
        var ran = new List<string> { "text" };
        if (v.Clip is not null && image is not null) ran.Add("image");
        if (v.Text is not null && meaning is not null) ran.Add("meaning");
        return new SearchResultDto(hits, fused.Count, facets, next, Semantic: true, SemanticLegs: ran);
    }

    /// <summary>One search request of the hybrid. Null when the index cannot answer it (no such vector field, or a size that does not match).</summary>
    private async Task<List<LegHit>?> SearchLegAsync(string alias, Dictionary<string, object?> body, CancellationToken ct)
    {
        var response = await client.DoRequestAsync<StringResponse>(HttpMethod.POST, $"/{alias}/_search", ct, Body(body));
        if (!response.Success) return response.HttpStatusCode == 404 ? [] : response.HttpStatusCode == 400 ? null : throw Failed("Search", response);
        using var doc = JsonDocument.Parse(response.Body);
        var hits = new List<LegHit>();
        foreach (var hit in doc.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray())
        {
            var source = hit.GetProperty("_source").Clone();
            var score = hit.TryGetProperty("_score", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0d;
            var matched = hit.TryGetProperty("matched_queries", out var mq) && mq.ValueKind == JsonValueKind.Array
                ? mq.EnumerateArray().Select(x => x.GetString()!).Distinct().ToList() : [];
            hits.Add(new LegHit(source.GetProperty("id").GetGuid(), score, source, matched, TranscriptMatches(hit), Highlights(hit)));
        }
        return hits;
    }

    /// <summary>A vector leg keeps only close-enough hits: above the floor for its model and within a margin of its own best, so a query nothing resembles finds nothing.</summary>
    private List<LegHit> Trim(List<LegHit> hits, double minCosine)
    {
        if (hits.Count == 0) return hits;
        var best = VectorMath.CosineFromKnnScore(hits.Max(h => h.Score));
        var floor = Math.Max(minCosine, best - _ai.KnnMargin);
        return hits.Where(h => VectorMath.CosineFromKnnScore(h.Score) >= floor).ToList();
    }

    private static string EncodeOffset(int offset) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{{\"o\":{offset}}}"));

    private static int DecodeOffset(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(cursor)));
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("o", out var o) && o.TryGetInt32(out var n) && n >= 0 ? n : 0;
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return 0; }
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
        ["fields"] = new Dictionary<string, object?>
        {
            ["title"] = new Dictionary<string, object?>(), ["metadata.*"] = new Dictionary<string, object?>(),
            [IndexMappingBuilder.OcrTextField] = new Dictionary<string, object?> { ["fragment_size"] = 160, ["number_of_fragments"] = 2 },
            [IndexMappingBuilder.SummaryTextField] = new Dictionary<string, object?> { ["fragment_size"] = 160, ["number_of_fragments"] = 2 },
            [IndexMappingBuilder.TranscriptTextField] = new Dictionary<string, object?> { ["fragment_size"] = 160, ["number_of_fragments"] = 2 },
        },
        ["pre_tags"] = new[] { "<em>" },
        ["post_tags"] = new[] { "</em>" },
    };

    private static SearchResultDto ParseSearchResponse(string json, int limit, IReadOnlyList<string>? facetFields, bool withMatches = false)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var hitsEl = root.GetProperty("hits");
        var total = hitsEl.GetProperty("total").GetProperty("value").GetInt64();

        var hits = new List<SearchHitDto>();
        JsonElement? lastSort = null;
        foreach (var hit in hitsEl.GetProperty("hits").EnumerateArray())
        {
            var score = hit.TryGetProperty("_score", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0d;
            IReadOnlyList<string>? matchedBy = null;
            if (withMatches)
            {
                var names = hit.TryGetProperty("matched_queries", out var mq) && mq.ValueKind == JsonValueKind.Array ? mq.EnumerateArray().Select(x => x.GetString()!).ToHashSet() : [];
                matchedBy = MatchOrder.Where(names.Contains).ToList();
            }
            hits.Add(ToHit(hit.GetProperty("_source"), score, Highlights(hit), matchedBy, withMatches ? TranscriptMatches(hit) : null));
            if (hit.TryGetProperty("sort", out var sortEl)) lastSort = sortEl.Clone();
        }

        var facets = facetFields is { Count: > 0 } && root.TryGetProperty("aggregations", out var aggs) ? ParseAggs(aggs, facetFields) : [];
        // Only offer a next page when this one was full - otherwise a client would keep paging into nothing.
        var nextCursor = hits.Count == limit && lastSort is { } ls ? EncodeCursor(ls) : null;
        return new SearchResultDto(hits, total, facets, nextCursor);
    }

    private static SearchHitDto ToHit(JsonElement source, double score, IReadOnlyDictionary<string, IReadOnlyList<string>>? highlights, IReadOnlyList<string>? matchedBy, IReadOnlyList<TranscriptMatchDto>? transcript) =>
        new(
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
            highlights,
            matchedBy is { Count: > 0 } ? matchedBy : null,
            transcript is { Count: > 0 } ? transcript : null);

    private static Dictionary<string, IReadOnlyList<string>>? Highlights(JsonElement hit) =>
        hit.TryGetProperty("highlight", out var hl)
            ? hl.EnumerateObject().ToDictionary(p => p.Name, p => (IReadOnlyList<string>)p.Value.EnumerateArray().Select(x => x.GetString()!).ToList())
            : null;

    /// <summary>The transcript lines that matched (at most three), with the time each starts at.</summary>
    private static List<TranscriptMatchDto> TranscriptMatches(JsonElement hit)
    {
        var result = new List<TranscriptMatchDto>();
        if (!hit.TryGetProperty("inner_hits", out var inner) || !inner.TryGetProperty("segments", out var seg)) return result;
        foreach (var h in seg.GetProperty("hits").GetProperty("hits").EnumerateArray())
        {
            if (!h.TryGetProperty("_source", out var src)) continue;
            var start = src.TryGetProperty("start_ms", out var sm) && sm.TryGetInt64(out var ms) ? ms : 0;
            var text = src.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String ? tx.GetString() ?? "" : "";
            result.Add(new TranscriptMatchDto(start, text));
        }
        return result.OrderBy(m => m.StartMs).ToList();
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
