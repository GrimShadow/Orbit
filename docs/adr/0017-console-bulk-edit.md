# ADR-0017: Console bulk edit (Step 1.8d)

## Status
Accepted

## Context
The spec's Step 1.8 screen list bundles "bulk edit; collections (static + smart); saved searches" together as one
item, alongside everything else already split across 1.8a-c. That is too much for one PR, so it is split further:
this step (1.8d) covers bulk edit only; collections and saved searches move to a following step, renumbered 1.8e
(the keyboard-shortcuts/mobile-responsive/Playwright e2e work originally planned as 1.8e becomes 1.8f).

Before writing any UI, the backend surface was checked directly rather than assumed: **no bulk-edit endpoint exists**.
The spec's own Part A API summary table lists `POST /assets/bulk` ("bulk metadata/status/move, async job") but it was
never built - `src/Dam.Api`/`src/Dam.Application` have zero `BulkUpdate`/`BulkEdit`/`BulkMove` symbols. The only bulk
surface that exists is `POST /assets/bulk-upload` (Step 1.3/1.8b), which creates new draft assets and has nothing to
do with editing existing ones. The only asset-editing contracts available are all per-asset: `PATCH /assets/{id}`
(title/metadata/tags, full-replace), `POST /assets/{id}/move`, `POST /assets/{id}/transitions/{action}`.

## Decisions

**Bulk edit is client-side fan-out over the existing per-asset endpoints, not a new backend endpoint.** Adding a real
`POST /assets/bulk` would mean new command/validator/handler/domain-event/outbox work (per this repo's own writes
pipeline convention) for what Step 1.8 as a whole is scoped as "frontend-only." The spec's accept criterion ("bulk
edit of 50 assets < 5s round-trip") is achievable by fanning the same per-asset calls out over a small fixed-concurrency
pool - `bulkOrchestrator.ts`'s `runBulk` is the same shape `useUploadBatch.ts` already established for bulk uploads
(ADR-0015): a shared queue, a handful of workers pulling from it (5 here; uploads use 3, since these are much smaller
requests than multipart file transfers), one item's failure collected and reported without blocking or rolling back
any other - there is no server-side bulk transaction to roll back in the first place, since every id goes through its
own ordinary command. If a genuine server-side bulk endpoint is ever built (e.g. once true multi-asset atomicity or
audit-as-one-operation semantics are needed), this is the ADR that gets superseded, not amended.

**Adding/removing tags reads each asset's current tags back from the row already on screen, then sends the full
result.** `PATCH /assets/{id}`'s `tags` field fully replaces the list (same full-replace semantics ADR-0016 already
documented for `metadata`) - there is no "append tag" operation. The assets grid/table already has each selected
asset's current `tags` from the list query that's rendering it, so no extra per-asset fetch is needed before computing
the union (add) or difference (remove) to send.

**Lifecycle bulk actions only ever offer the intersection of every selected asset's own available actions**, reusing
`../asset/lifecycle`'s `availableActions` (built for the per-asset lifecycle buttons in Step 1.8c) unchanged. A
5-draft + 3-in_review selection shows no lifecycle buttons at all rather than a `submit` button that would fail for
3 of the 8; a uniform-status selection shows exactly the buttons a single asset in that status would see. As with the
per-asset case, this is a UX narrowing, not a security boundary - the server re-validates every transition
independently regardless of what the console chose to show.

**Selection is page-scoped and clears on every filter or page change.** `LibraryPage` holds `selected` as a plain
`Set<string>` of ids and resets it whenever the folder, status, type filter, or cursor changes - carrying a selection
silently across a filter change (where the previously-selected assets may no longer even be visible) was judged more
confusing than requiring re-selection. This also means `BulkEditBar` never needs to handle an id that isn't in the
currently-loaded page of `AssetDto`s, simplifying the tag-diff logic above.

**`ui-kit`'s `Table` gained the same optional selection contract `ThumbnailGrid` already had** (`selectedIds`,
`onToggleSelect`, `selectLabel`) rather than building selection only for the grid view or duplicating the pattern
console-side. Both are additive, backward-compatible props (every existing `Table` caller is unaffected), so bulk edit
works identically in the library's grid and list views.

## Consequences
- Collections (static + smart) and saved searches UI move to Step 1.8e; the original 1.8e (keyboard shortcuts,
  mobile-responsive pass, Playwright e2e) is renumbered 1.8f.
- If a tenant's real-world bulk-edit volume ever needs true server-side atomicity or a single audit entry per batch
  (rather than N independent per-asset audit entries), that is new backend work superseding this ADR's "fan-out over
  existing endpoints" decision, not an extension of it.
- A selected asset's tags can only be diffed correctly as long as the row data on screen is fresh; since selection
  clears on every filter/page/query change (including the query invalidation this feature itself triggers after a
  bulk action completes), there is no window where a stale on-screen tag list is used to compute a send.
