# ADR-0013: Saved searches - the `Collection` entity (Step 1.7c)

## Status
Accepted

## Context
Step 1.7 closes with spec A4.1's `Collection`: "id, tenant_id, name, type (static/smart), query(jsonb), asset_ids[],
share settings | Smart = saved search". ADR-0011 (1.7a) and ADR-0012 (1.7b) both left `collection_ids` hard-coded empty
in the search document, with an explicit note that `ScopeType.Collection` access rules - already fully wired in
`PolicyEngine`/`AccessQueryTranslator` - could never actually match anything until this step gave them real data. That
is the central obligation this ADR discharges.

## Decisions

**Only a static collection's membership is real and stored; a smart collection's is never computed until asked.**
`Collection.AssetIds` (a Postgres `uuid[]`, `List<Guid>` backing field) is the literal spec `asset_ids[]` column. A
smart collection's `Query` (spec `query jsonb`, modelled as `Dictionary<string, JsonElement>` so it can reuse the same
`AsJson<T>()` EF helper every other jsonb column already uses) is the *only* thing stored for it - "membership" is
whatever that query currently matches, re-run fresh every time (`GetCollectionContentsQuery`, spec: "Smart = saved
search"). Consequently, **only static collections feed `ScopeType.Collection` access rules** - a smart collection's
contents can change the instant someone else's unrelated asset changes, which is not a sound basis for a permission
grant. This is enforced simply by construction: `TenantCollectionIndex` (the reverse lookup a search document's
`collection_ids` is built from) only ever loads `CollectionType.Static` rows.

**Visibility is two-level ("share settings"): Private (owner only) or Tenant (anyone who can read assets), plus
curators see everything regardless.** `CollectionMapping.Visible(c, userId, canManage) => canManage || c.OwnerId ==
userId || c.Visibility == Tenant`. Concretely: only Librarian/Editor/Admin hold `collections.manage` and can create or
mutate a collection at all, and since all three already see every collection under this rule, "Private" meaningfully
hides a collection only from everyone else in the tenant (Viewer, Contributor, Dealer, Approver, ApiClient) - not from
fellow curators. This mirrors how curation teams actually work in practice (moderators see each other's drafts) and
keeps the permission model flat: no new "collections.manage.own" permission, no per-resource ownership check on writes
- `collections.manage` alone gates every mutation, exactly like `folders.manage`/`taxonomy.manage` already do
tenant-wide. A richer per-role/per-user share list is a natural additive follow-up (one more jsonb column) if it turns
out to be needed; nothing here forecloses it.

**A collection's contents are resolved through search, not a bespoke read path** (`GetCollectionContentsQuery`): a
static collection becomes a `terms` filter on `id` (already a plain keyword field) over the caller's own access scope;
a smart collection is its stored `Query`, deserialized straight back into a `SearchQuery` and run the same way
- both go through `ISearchQueryService.SearchAsync`, so "browse a collection" is never more permissive, differently
paginated, or differently sorted than "search" itself. The stored/round-tripped `Query` uses `JsonSerializer`'s
default (PascalCase) options on both sides deliberately - it is pure internal state, never parsed by a client, so there
is no reason to entangle it with the camelCase convention the HTTP API uses for the same fields.

**`Collection.ChangeAssets` reports only what actually moved.** Adding an id already present, or removing one already
absent, is silently ignored - the caller is describing an end state ("these are now members"), not asserting that
every id is individually novel - and, more importantly, this keeps `CollectionAssetsChanged`'s added/removed lists
exactly the set of assets that need re-indexing, never a spurious no-op reindex.

**Search-index wiring reuses 1.7a's index-per-tenant-artifact pattern exactly.** `TenantCollectionIndex` (asset ->
static collection ids, loaded once and reused across many documents) sits alongside `TenantSchemaIndex` and
`TenantTaxonomyIndex`; `SearchDocumentBuilder.Build` and `OpenSearchIndexService`'s single-asset and bulk-reindex paths
both take it the same way they already took the other two. `IndexConsumer` gains one more routing key,
`collection.assets_changed`, whose payload already carries the affected asset ids directly (added and removed) - no
extra lookup needed to know which documents to refresh, and deleting a collection raises the same event with every
member as "removed" (via `IEventCollector.Raise`, since the aggregate itself is being removed, not mutated, so there
is nothing left to call `.Raise()` on afterwards).

**`Asset.ToResource()` - the in-memory, per-row policy check `ListAssetsHandler`, `GetAssetHandler` and friends use -
still does not populate real `CollectionIds`.** Wiring that in would mean a `Collections` lookup on every single-asset
permission check across the whole API surface, for a scope type whose main audience is genuinely search/browse, not
"can I open this one asset's direct link." `ListAssetsHandler` is already the interim path Step 1.7 as a whole is
retiring (ADR-0011); collection-scoped access rules take effect in search today, and only there. A collection-scoped
rule that grants nothing outside search is a real, but scoped and documented, limitation - not a silent gap.

## Consequences
- No new permission constant: `collections.manage` (already defined, already granted to Librarian/Editor) covers every
  mutation; reads are gated by `CollectionMapping.Visible` instead of a permission check.
- `POST /collections`, `GET /collections`, `GET /collections/{id}`, `GET /collections/{id}/assets`, `PUT
  /collections/{id}` (rename), `PUT /collections/{id}/query` (smart only), `POST /collections/{id}/assets` (static
  only, add or remove), `POST /collections/{id}/share`, `DELETE /collections/{id}`.
- A collection-scoped access rule (`scopeType: "collection"`) - accepted by validation since Step 1.1 but dead until
  now - genuinely narrows search results as of this step; proven by `CollectionSearchTests`'s capstone test, not just
  asserted.
- Console UI for browsing/managing collections is Step 1.8 (library UI), not this step.
