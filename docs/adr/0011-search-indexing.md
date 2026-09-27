# ADR-0011: Search indexing (Step 1.7a)

## Status
Accepted

## Context
Decision D4 picked OpenSearch 2.x with the k-NN plugin for search (spec Part A2). Until now, `GET /assets` has filtered
rows in memory (`ListAssetsHandler`, capped at 5000 scanned rows) - its own doc comment already says Step 1.7 replaces
it. Step 1.7 is large enough to split, matching every other multi-part step so far (1.5a-c, 1.6a-b):

- **1.7a (this ADR):** the index itself - per-tenant creation, mapping generated from metadata schemas, the consumer
  that keeps it in sync with asset events, and a reindex job that rebuilds it without downtime.
- **1.7b (follow-up):** the query surface - `POST /search`, `GET /search/suggest`, `GET /facets`, and saved searches
  (the `Collection` entity, spec A4.1), including translating `PolicyEngine`'s ABAC scope clauses into OpenSearch
  filter queries so search enforces exactly what `ListAssetsHandler` enforces today.

## Decisions

**One index per tenant, addressed through a write alias.** `dam-{tenant.slug}-assets` (spec's own naming) is never a
real index - it is an alias. `IndexNaming.BackingIndex` names the physical index `{alias}-{timestamp}`. A reindex
builds a brand-new backing index, bulk-loads every asset into it, then atomically repoints the alias with one
`POST /_aliases` call and drops the index it replaced. The old index keeps serving every read up to the moment the
alias moves, so a rebuild - triggered by a schema change, or just to recover from a bad state - never takes search
down (spec accept: "reindex rebuilds without downtime (alias swap)").

**The OpenSearch low-level client, not the fluent high-level DSL.** Every mapping and every document here is shaped by
a *tenant's own* metadata schema (`FieldDefinition.Searchable`/`.Facet`, its vocabulary/term_tree taxonomy), so there
is no fixed set of POCOs to map - the shape is genuinely dynamic, tenant by tenant. `IndexMappingBuilder` and
`SearchDocumentBuilder` build plain `Dictionary<string, object?>` trees instead, serialized with `System.Text.Json` and
sent through `IOpenSearchLowLevelClient.DoRequestAsync` (the client's own general escape hatch for anything outside its
named endpoints - index/alias management, bulk, get, delete). This keeps the client itself a thin transport: retries,
connection pooling and exception mapping come from the library; the JSON shape stays fully under our control. 1.7b's
query builder can layer the high-level `QueryContainer` DSL on top later if it turns out to earn its keep for the more
deeply nested `bool`/aggregation queries search needs - nothing here forecloses that.

**Mapping generated from metadata schemas, one field type at a time** (`IndexMappingBuilder`):
- `Text` -> `text` (generic analyzer), plus a `.keyword` subfield only when `Facet` is set - a long analyzed field
  nobody facets on gains nothing from an extra keyword copy.
- `MultilingualText` -> an object with one analyzed sub-field per console language (`en`, `hi`, `mr`, `ta`, `te`) plus
  a dynamic template catching any other language key under `dam_text_generic` (spec: "+ generic"). English and Hindi
  get Lucene's built-in stemmed analyzers; Marathi, Tamil and Telugu get the same tokenizer as generic today - still
  correctly searchable, just without stemming until an ICU or Indic analysis plugin is added to the OpenSearch image.
- `Number` -> `long` when `WholeNumbers`, else `double`. `Date` -> `date`, accepting both formats
  `MetadataValidationService` already accepts (`yyyy-MM-dd` or full ISO 8601). `Boolean` -> `boolean`.
- `Enum`, `Vocabulary`, `TermTree`, `Reference` -> `keyword` (exact-match codes). A vocabulary/term_tree value's
  resolved hierarchy and label live in the `terms` nested array instead (below), not as extra mapped sub-fields on
  the code itself - resolution depends on which vocabulary a *field* points to, not on the code alone.
- `Searchable: false` turns off indexing (`"index": false`) for `Text`/`MultilingualText` fields; it stays stored in
  `_source` (and in the ordinary `metadata` field for anything reading the document, not searching it) but drops out
  of full-text queries. It has no analogous meaning for the other types, which exist to be filtered/faceted exactly
  because they are not free text.

Fields are merged across every asset type's current schema into one shared `metadata.*` mapping (one index serves
every type in the tenant). A field name two asset types disagree on in type keeps whichever schema was merged first
and logs the conflict - the whole build never fails over one field, and asset types are visited in a fixed
(alphabetical) order so the outcome is at least deterministic. A real fix for a genuine conflict is a schema rename,
not something the indexer can resolve for you.

**Nested terms, not a bag of codes.** `Asset.TaxonomyTerms` is already a flat list of codes (kept as-is, `taxonomy_terms`,
for simple filters). For richer facet drill-down, every vocabulary/term_tree field's selected codes are additionally
resolved (`TenantTaxonomyIndex`, by `(vocabulary key, code)` - a code alone is not unique tenant-wide, spec A4.1) into
a `terms` nested array: `{field, vocabulary, code, path, ancestor_codes, label}`. A facet or filter on `terms.path` or
`terms.ancestor_codes` then drills down a hierarchical vocabulary exactly the way the taxonomy itself is structured,
across every field uniformly, rather than one bespoke sub-mapping per vocabulary.

**k-NN vector fields are declared now, populated later.** `vector_text` (1024-d) and `vector_image` (512-d) are part
of the mapping from the first index onward, with `index.knn: true` and an HNSW/cosine method, even though nothing
writes to them until Phase 2 Step 2.1. Declaring the shape now means semantic search lands later without a mapping
change - and without the reindex a mapping change would otherwise force.

**Events carry only ids; the consumer always re-reads.** Every asset event (`asset.created`, `.metadata.updated`,
`.status.changed`, `.version.added`, `.legal_hold.changed`) carries nothing but ids (and, for a status change, the two
status strings) - confirmed by reading every event record in `Assets.cs`. `IndexConsumer` binds to all five routing
keys on one queue (`EventConsumer.RoutingKeys` already supports many keys on one consumer) and always re-reads the
asset, its folder and its taxonomy before writing - a fresh document, never a delta. The one exception: a status change
whose new status is `deleted` removes the document without a read, since a deleted asset needs nothing else in its
document. There is no separate `asset.deleted` event in code (the spec's A4.3 list names one; the implementation
raises the generic `AssetStatusChanged` for every transition including delete) - `IndexConsumer` switches on
`AssetStatusChanged.To` rather than on a routing key that does not exist.

**The reindex job is a manual trigger, not a cron.** `SearchReindexJob` (`search.reindex`) rebuilds one tenant (an
optional `tenantId` datum) or every tenant (none given) through `IJobScheduler.ScheduleOnceAsync`. A schema change or
a mapping bug is what should cause a rebuild, not a clock, so unlike the Step 1.6 retention jobs this one is not
scheduled on a recurring cron in `Dam.Scheduler/Program.cs` - 1.7b's admin endpoint (or a one-off `dotnet run` call)
fires it when needed. It reuses the retention jobs' existing read-only system connection to list every tenant
(`dam_system` gains one more grant + RLS policy for it, `SecuritySql.SearchSystemGrants`, on `tenants` - mirroring the
existing `system_read_assets`/`system_read_retention_policies` policies from Step 1.6b).

## Consequences
- A new `DAM_OPENSEARCH_URL` env var (Worker, Scheduler); the compose `opensearch` service (`make up-search` /
  `make up-all`) was already scaffolded in Step 0.2 and needed no changes.
- A tenant's index is provisioned lazily: `EnsureIndexAsync` runs before every `IndexAsync`/`DeleteAsync`, so a
  brand-new tenant needs no separate provisioning step, and a schema's new field merges into the live mapping
  automatically. A field whose *type* changed cannot merge (OpenSearch rejects it) - that case is logged and left for
  an explicit reindex rather than silently failing the write.
- `POST /search` and friends, and the `Collection` entity saved searches need, do not exist yet - that is 1.7b.
- Console UI for search/facets is out of scope for the whole of Step 1.7 (spec Step 1.8 covers the console library UI).
