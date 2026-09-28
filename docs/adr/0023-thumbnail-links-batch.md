# ADR-0023: Batch thumbnail delivery links for grid views

## Status
Accepted

## Context
Outside the spec's own step numbering: a user reported that after uploading an image, no thumbnail ever appeared in
the library grid. Investigating found two independent, unrelated bugs (see `CHANGELOG.md` for the other one, a
frontend-only fix to `AssetPreview`). This ADR covers the second, larger one: `ThumbnailGrid`'s `Thumb.imageUrl`
field has existed since Step 0.7 but no page has ever populated it - the library, search and collection-detail grids
have never shown a real image thumbnail for any asset, of any kind, ever. They always fell back to `ThumbnailGrid`'s
own generic file-type-icon rendering. The round-2 design refresh (ADR-0022) made this more noticeable (richer cards
draw the eye to the missing image) but did not introduce the gap.

Every existing way to get at a rendition goes through a signed, expiring, watermark-aware link
(`POST /assets/{id}/links`, ADR-0006) - deliberately so, since `Dam.Delivery` is the only thing that ever serves file
bytes (no file bytes through `dam-api`, CLAUDE.md). A grid shows dozens of assets at once; asking for a signed link
one asset at a time doesn't scale to that, and neither `AssetDto` nor `SearchHitDto` carries a thumbnail URL today
(confirmed against `docs/api/openapi.json` before starting).

## Decision
**A new endpoint, `POST /assets/links/batch`, signs one thumbnail link per asset id in a single request**, reusing
exactly the same per-asset checks `CreateDeliveryLinkHandler` already makes (read permission, not deleted/blocked, has
a current version) and the same watermarking rule (unpublished = watermarked, ADR-0006/0007) - just for many ids at
once instead of one.

**Deliberately narrow, not a generic batch-links endpoint.** Always the `"thumb"` profile - the one rendition name
shared by every renderable kind (image, video poster, PDF/Office first page - `RenditionProfiles`), no transform, no
IP binding, the default TTL. A grid only ever needs one small image per card; a generic "batch anything" endpoint
would just widen the surface for a caller that doesn't exist yet.

**An id that can't get a thumbnail maps to `null`, never an error, and never fails the rest of the batch.** A grid
routinely mixes assets some of which have no permission, no version yet, or are deleted (a page can span a filtered
view or a stale selection); one bad id must not blank out every other thumbnail on the page. This is the same
"looks absent, not an error" principle `CreateDeliveryLinkHandler` already applies to a single asset
(`Bad_link_requests_are_rejected...`), just not escalated to an HTTP error for the *whole* multi-id request. The
batch is capped at 200 ids per call (a plain safety bound, not exposed as config) - a request-level validation error,
distinct from a per-id `null`.

**Keyed by the asset id's string form (`Guid.ToString()`), not `Guid` itself, in the JSON response.** A
`Dictionary<Guid, …>`'s wire format isn't a documented guarantee; a plain string key is unambiguous.

**The frontend fetches the batch once per page of grid results, not per card.** `useThumbnailLinks` (new,
`assetApi.ts`) takes the ids of the assets currently on screen and issues one request; `toAssetThumb` gained an
optional `imageUrl` parameter it was always missing, and the three grid-showing pages (library, search, collection
detail) now pass the batch result through it. The library page only fires this request in grid view - the list/table
view shows no images and would otherwise fetch signed links it never uses.

## Consequences
- One more signed-link surface exists alongside the single-asset one; both enforce the exact same rules, so there is
  no new way to see an asset's original or a watermark-bypassing rendition - only ever the watermarked `thumb`.
- A grid page now makes one extra request (the batch call) beyond its list/search query. This is strictly better than
  the alternative of one request per card, and the response is cached client-side for 4 minutes like the existing
  per-asset delivery-link hook.
- Video/PDF/Office thumbnails work through the same call (their kinds already produce a `"thumb"`-named still -
  `VideoStills`, `PdfStills`) - this was not scoped to images only.
- Two new backend integration tests (`DeliveryTests.cs`) cover: a mixed published/unpublished batch is watermarked
  correctly per asset, and a batch with a deleted/missing/versionless id degrades to `null` per id rather than
  failing; three console test files gained thumbnail-rendering coverage (`library.test.tsx`, `search.test.tsx`,
  `collections.test.tsx`), including that the library's list view issues no batch call at all.
- `docs/api/openapi.json` and `@dam/sdk-js` regenerated (`make openapi`) for the new endpoint.
