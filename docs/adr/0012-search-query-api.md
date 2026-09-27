# ADR-0012: Search query API (Step 1.7b)

## Status
Accepted

## Context
Step 1.7a (ADR-0011) built the index: mapping, `IndexConsumer`, reindex-with-alias-swap. It deliberately left the query
surface for a follow-up. This ADR covers that follow-up: `POST /search`, `GET /search/suggest`, `GET /facets`. Saved
searches (the `Collection` entity, spec A4.1) are further split off into Step 1.7c - a genuinely separate concern
(a new aggregate, CRUD, sharing) that doesn't belong in the same PR as the query engine itself.

`ListAssetsHandler`'s own doc comment has said since Step 1.3 that this step would replace its in-memory row filtering.
That is the central design constraint here: search must enforce access **exactly** as `PolicyEngine`/`ScopeClause` do
today, translated into an OpenSearch query instead of a per-row check.

## Decisions

**`AccessQueryTranslator` mirrors `ScopeClause.Matches` clause for clause, condition for condition.** Every clause is an
OR-branch (`bool.should`, `minimum_should_match: 1`); within a clause, the scope filter, owner-only check, each
attribute dimension and the status check are AND-ed (`bool.filter`) in the same order `Matches` checks them. Each
piece has a direct, deliberate translation:
- `ScopeType.Folder` -> `term(folder_path) OR prefix(folder_path + ".")`, exactly `Paths.IsSelfOrDescendant`.
- `ScopeType.AssetType` -> a `case_insensitive` term, matching `string.Equals(..., OrdinalIgnoreCase)`.
- An attribute dimension -> `no value in that field OR "Global" OR intersects the allowed set`, exactly
  `PolicyEngine.AttributesAllow` (no value = the field's array is empty, which Lucene indexes as "does not exist").
- Statuses -> `terms(status, allowedStatuses) OR (owner_id == caller, when the clause grants assets.read.unpublished.own)` -
  `allowedStatuses` always includes "published" already (`PolicyEngine.Clause` guarantees it), so this one filter
  covers both "published is always visible" and "in_review, if granted" in one `terms` query.

This is unit-tested exhaustively (`AccessQueryTranslatorTests`, one case per branch) since it is the one piece a
tenant's data isolation in search actually depends on - a mistranslation here is a cross-tenant or over-visible-draft
leak, not a wrong search result. `SearchApiTests` then proves the *real* pipeline (policy engine, translator, a real
OpenSearch) agrees with those unit-level claims for a representative case (Viewer / Contributor-own / Admin), rather
than re-enumerating every branch a second time against a container.

**Queries are still hand-built JSON via the low-level client**, for the same reason 1.7a's mapping/document builders
are: the access filter and the facet field list are dynamic per request, and OpenSearch's own `lenient: true` and
nested-aggregation mechanics are easiest to get right writing the JSON directly rather than fighting a fluent DSL
that would need the same dynamic shape anyway.

**`multi_match` against `metadata.*` needs `lenient: true`.** The wildcard sweeps in every metadata field regardless of
type; a text query like "roadster" fails outright (`search_phase_execution_exception`, not just a non-match) the moment
the schema has so much as one `date` or `number` field, because OpenSearch tries to parse the query string as that
field's type. `lenient: true` makes a field it can't query for that value simply not match, instead of failing the
whole request - this surfaced immediately as a real integration-test failure, not a hypothetical.

**`similar_to` is `more_like_this` against title and metadata text, not the k-NN vector fields.** The vector fields
exist in the mapping (ADR-0011) but nothing populates them until Phase 2 Step 2.1 - `similar_to` today is a legitimate,
useful term-overlap similarity, and swapping it for a k-NN query later is a query-body change, not a mapping or API
change.

**Cursor pagination via `search_after`, not `from`/`size`.** The cursor is the last hit's own sort values, base64-JSON
encoded opaquely (same "opaque string" contract as `ListAssetsQuery`'s cursor) - `search_after` scales to the spec's
100k-asset accept criterion without OpenSearch's `from`+`size` deep-pagination cost/limit. Every sort always ends with
an `id` tiebreak so pagination is stable even when many hits share a sort value (relevance ties, same `created_at`,
etc). A next page is only offered when the current one was full, so a client can't page forever into nothing.

**Facet fields are named directly by the caller, as literal index field paths** (`"type"`, `"metadata.category.keyword"`,
`"terms.path"`), both in `POST /search`'s own `facets` and the standalone `GET /facets`. No auto-discovery of "which
metadata fields are marked Facet" happens server-side: the console already gets each field's `Facet` flag from
`GET /content/schemas/{assetType}`, and building the field-path list from that (a Text field's facet lives at
`metadata.<name>.keyword`; a Vocabulary/Enum/Number/Date/Boolean field's facet is just `metadata.<name>`) is a small,
already-documented (ADR-0011) piece of client-side logic. Duplicating that FieldType-to-path mapping on the Application
side - which cannot see `Dam.Infrastructure`'s `IndexMappingBuilder` at all, by design (Clean layering: `Dam.Application`
has no reference to `Dam.Infrastructure`) - would mean maintaining it in two places for no real benefit.

**`GET /facets` counts the whole visible corpus (no query attached); `POST /search`'s own `facets` count alongside its
hits.** These are the two facet use cases the spec's A5 table and Step 1.7 prose separately call for, and they share
one aggregation builder (`OpenSearchQueryService.BuildAggs`/`ParseAggs`) - a nested field (`terms.*`) is wrapped in a
`nested` aggregation automatically either way.

**`ISearchQueryService` is a new `Dam.Application.Abstractions` port**, implemented by `OpenSearchQueryService` in
Infrastructure and registered alongside 1.7a's `ISearchIndexAdmin`/`ISearchIndexWriter` in `AddDamSearch` - the same
shape as every other Infrastructure-backed capability in this codebase. `Dam.Api` did not call `AddDamSearch` before
this step (only Worker and Scheduler did); it does now, which means every integration test that boots the API host
needs *a* `DAM_OPENSEARCH_URL` - `ApiTestHost.Factory` defaults to a harmless placeholder that is never actually
connected to unless a test's request path reaches `ISearchQueryService`, and only `SearchApiTests` overrides it with a
real `SearchFixture` container.

**Two small, additive extensions to 1.7a's own mapping/document shape**: a `folder_id` keyword field (1.7a only stored
`folder_path`, but a search hit needs an id the client can call `GET /folders/{id}` with) and hoisting the
tenant-slug-to-alias lookup into a shared `TenantIndexResolver` (both `OpenSearchIndexService` and the new
`OpenSearchQueryService` needed it). Both are additive: a live index's mapping already accepts new fields without a
reindex (ADR-0011), and existing documents (before this change) simply have no `folder_id` until the asset is next
reindexed or re-touched - never a correctness problem, since `folder_path` alone was always sufficient for the
`ScopeType.Folder` filter itself.

## Consequences
- `POST /search`, `GET /search/suggest` and `GET /facets` all reuse `Permissions.AssetsRead` (via
  `PolicyEngine.GetScope`, exactly like `ListAssetsHandler`) - no new permission constant. Collection-scoped access
  rules (`ScopeType.Collection`) still translate correctly today (`AccessQueryTranslator` handles the case), but can
  never actually match anything until Step 1.7c gives `collection_ids` real values (`SearchDocumentBuilder` still
  hard-codes it empty, same as `Asset.ToResource` does for the in-memory path).
- `ListAssetsHandler`/`GET /assets` is intentionally left as-is for now (still the interim in-memory listing) - retiring
  it in favour of `POST /search` for simple listing use cases is a console/API-consumer migration, not a backend change,
  and out of scope here.
- Semantic search (`semantic=true/false` in the spec's A5 table) waits for Step 2.1, when the k-NN vector fields
  actually have vectors in them.
