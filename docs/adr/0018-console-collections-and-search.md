# ADR-0018: Console search, collections and saved searches (Step 1.8e)

## Status
Accepted

## Context
The spec's Step 1.8 screen list bundles "bulk edit; collections (static + smart); saved searches" as one item.
ADR-0017 already split bulk edit out as Step 1.8d; this step (renumbered 1.8e) covers collections and saved
searches, plus the search UI itself - a `POST /search` page did not exist in the console before this step (the
library screen's `GET /assets` browsing is folder-scoped and deliberately separate, ADR-0012), and there is nowhere
else a "saved search" could be built from without one.

All backend contracts here (`POST /search`, `GET /search/suggest`, `GET /facets`, and the full `/collections` CRUD
surface) were built in Step 1.7 and already match `web/sdk-js/src/schema.ts` exactly - this step is pure console UI
over an existing, unchanged API.

## Decisions

**A "saved search" is just a named smart collection - there is no separate saved-search entity or endpoint.** A
smart `Collection`'s `Query` field is already the exact `SearchRequestBody` shape `POST /search` takes (`Dam.Domain`'s
`Collection.CreateSmart`, `Dam.Application.Content.CollectionMapping.ToStored`/`FromStored` round-trip it verbatim).
"Save this search" on the new Search page therefore does nothing more than name the current query and
`POST /collections` with `type: "smart"` - there was never a reason to invent a second concept for what the backend
already models as one.

**Both collection kinds render through one code path**, because `GET /collections/{id}/assets` already resolves
either kind (a static collection's literal `assetIds`, or a smart collection's saved query re-run now) into the same
`SearchResultDto` shape `POST /search` itself returns. `CollectionDetailPage` and `SearchPage` share the same
hit-to-`Thumb` mapping and the same `ThumbnailGrid` rendering; the console never needs to know or branch on which
kind of collection it is looking at once past the list page.

**Search facets are fixed to `type` and `status`.** Real per-metadata-field facets need that asset type's current
schema's `Facet` flags to know which fields are even facetable (`FieldDto.facet`, spec ADR-0011) - across a mixed-type
result set there is no single schema to ask. `type`/`status` are the two fields meaningful across every tenant's
content model unconditionally, matching the same two filters the library screen already offers. A schema-aware,
per-type facet set is future work, not a limitation introduced by this decision - it would need the search results
page to already know (or ask for) which asset type(s) are in view.

**Creating a static collection reuses Step 1.8d's bulk-selection state directly** - "Save as collection" is one more
action in `BulkEditBar` alongside tags/move/lifecycle, posting the already-selected asset ids as-is. There is no
separate "pick assets for a new collection" flow; the library's existing multi-select is the only way to build a
static collection's initial membership. Once created, this step does not support editing a static collection's
membership afterwards (`POST /collections/{id}/assets` exists on the backend but has no console UI yet) - a
collection's contents are fixed at creation until a future step adds that.

**No comment/discussion UI, and collection sharing is the two-value visibility toggle only** (`Select` between
`private`/`tenant`), not a per-user share list - that matches exactly what `ShareCollectionRequest` supports today
(ADR-0013: curators see every collection regardless of owner, so a finer-grained ACL was never part of this backend
surface to begin with).

**Deleting a collection uses an in-page confirm `Modal`, not `window.confirm`.** This is the first console feature
with a destructive, hard-to-recover action outside the asset lifecycle's own (reversible) transitions, so it is also
the first console consumer of `ui-kit`'s `Modal`/`useToast` - both already existed in `ui-kit` unused by the console
until now, added to the `ui.ts` shim as part of this step. Console's own `test-setup.ts` was missing the
`<dialog>` `showModal`/`close` jsdom polyfill `ui-kit`'s own test setup already has (nothing in the console rendered
a native `<dialog>` before this step); it is now duplicated there rather than shared, since the two packages'
Vitest configs don't share a setup file today.

## Consequences
- Editing an existing static collection's asset membership (add/remove after creation) is deferred; the backend
  endpoint for it already exists (`POST /collections/{id}/assets`) whenever a future step adds that UI.
- Per-type metadata facets on the search page are deferred until a step gives the search UI a reason to know which
  asset type(s) it is showing (e.g. a type filter narrowing to one type, at which point that type's schema's facet
  flags become meaningful to render).
- Steps 1.8a-1.8e are now all merged; the last remaining console sub-step (keyboard shortcuts, mobile-responsive
  pass, Playwright e2e) is 1.8f.
