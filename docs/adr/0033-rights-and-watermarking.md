# ADR-0033: Rights management and watermarking

## Status
Accepted (Step 2.5, backend)

## Context
Content is not free to use everywhere, for ever. A photograph is licensed for the web and print but not for in-store screens; a
release form covers one region; a stock licence ends on a date. Until now `Asset.RightsId` existed and did nothing, the lifecycle's
"rights valid" guard (spec Step 1.6) was a comment, and the only watermark rule was "unpublished means watermarked" (ADR-0006).
Step 2.5 makes a licence a real record that narrows who can reach an asset, blocks publishing and delivery when it is not in force,
can force a watermark, tells the asset's owner before it ends, and moves a published asset to `expired` when it has. It builds on the
authorization model of ADR-0002 (attributes on assets and users), the delivery origin of ADR-0006, the lifecycle of ADR-0009/0010
and the notification and webhook services of ADR-0031. All names in code and tests are generic ("Acme").

## Model
`rights(id, tenant_id, name, licence_type, licensor, channels text[], regions text[], valid_from date?, valid_to date?, watermark_required,
notes, created_by, created_at, updated_at)`, `rights_documents(id, tenant_id, rights_id, name, content_type, size_bytes, bucket, storage_key,
uploaded_by, created_at)` and `rights_alerts(id, tenant_id, rights_id, asset_id, threshold_days, valid_to, created_at)` (the alert ledger).
An asset points at one rights record through `assets.rights_id` (now a real foreign key, `ON DELETE RESTRICT`).

- **Licence types**: `owned`, `licensed`, `royalty_free`, `rights_managed`, `editorial`, `creative_commons`, `other` (check constraint).
- **Name** is unique per tenant ignoring case (a unique index on `lower(name)`; the API answers 422 on `name`).
- **Channels and regions are tenant data.** They are the same term CODES the metadata fields use. A value must be an active term code of the
  vocabularies behind the tenant's access-dimension fields (the current schemas' fields tagged `accessDimension` `channel` / `region`; else a
  vocabulary keyed `channel` / `region`), matched ignoring case and stored in the term's own spelling. A tenant with no such vocabulary (or an
  empty one) gets the fallback: a lower-case slug `[a-z0-9_-]{1,50}`. `global` is refused: leave the list empty to allow everything. At most
  50 values, each at most 50 characters. An empty list means "every channel" / "worldwide".
- **Validity** is calendar-date based, in UTC, and `valid_to` is the last valid day (the licence lasts through the end of that UTC day).
  Status as of today: `not_yet_valid` (today before `valid_from`), `expired` (today after `valid_to`), `expiring` (valid, `valid_to` within 30
  days, so 0 to 30 days left), otherwise `valid`. `daysRemaining` is `valid_to` minus today: 0 on the last day, negative once expired (-1 the
  day after), null without an end date. Open ends are allowed on either side; a start date after the end date is 422 on `validTo`.

## Effective access attributes (the core enforcement)
An asset's channel and region attributes come from its metadata (ADR-0002). A licence that names channels or regions NARROWS them to the
INTERSECTION, so the existing ABAC (single-asset checks, the in-memory listing, the OpenSearch filter built by `AccessQueryTranslator`) hides
the asset from attribute-restricted consumers with no special case. This is what makes "asset restricted to `web` is rejected by the PADS4
plugin listing" true: an `ApiClient` whose channel attribute is `signage` no longer finds it, in search, listings or by id (an integration test
proves it with a real policy engine and a real OpenSearch).

`RightsScope.Effective(metadata, licence)`, per dimension, case-insensitively:

| metadata side | licence side | effective |
|---|---|---|
| empty or contains `Global` (open) | empty (open) | the metadata values, unchanged |
| restricted | empty | the metadata values |
| open | restricted | the licence values |
| restricted | restricted | the intersection; **if empty, `~restricted`** |

`~restricted` (`RightsScope.RestrictedToNothing`) is the marker for an asset "restricted to nothing". An empty attribute list means
"unrestricted", so an empty intersection cannot be stored as one. Any value in a dimension (other than Global) hides the asset from a consumer
who does not hold it, and nobody can hold `~restricted` (it is not a legal term code or slug), so such an asset stays visible to roles that
are not attribute-restricted (Admin, Librarian, Editor ...) and to nobody on a channel. It appears as `["~restricted"]` in `AssetDto.attributes`;
the check endpoint reports it as `noChannelsAllowed` / `noRegionsAllowed` with an empty list instead. Hierarchy is not expanded: a region
code matches only itself, not its children.

**Where it lives.** `assets.attributes` stays the effective value (what the policy and the index read), so nothing that reads it changed.
Three new columns make it re-derivable: `metadata_attributes` (what metadata alone derives; backfilled from `attributes` by the migration),
and `rights_channels` / `rights_regions` (a copy of the assigned record's scope). `Asset.SetMetadata` recomputes `attributes` from the first
and the copies, so editing metadata keeps the narrowing without loading the rights; `Asset.AssignRights` sets the id and the copies and
recomputes. Both raise `asset.rights.changed` (internal), which `IndexConsumer` now binds, so the document is re-indexed through the outbox.

**Re-deriving when a rights record changes.** `PUT /rights/{id}` re-derives the assigned assets IN THE SAME TRANSACTION (and each raises its own
`asset.rights.changed`) when, and only when, the channels or regions changed, for up to **500** assets (non-deleted, by id). If more are
assigned the response says how many were left in `pendingAssets`. The remainder, and any other drift (a copy that does not match its record),
is caught by the rights job (below), whose first step finds assets whose `rights_channels` / `rights_regions` differ from their record and
re-derives them. Chosen over an event consumer because the common case then needs no eventual consistency (the response and the next search
agree), the unbounded case is handled by a job that already exists for another reason, and a consumer would add a queue and an idempotence
story for the rare record with thousands of assets.

## Enforcement points
1. **Publishing** (`publish` transition): refused with **409** when the asset's licence has ended or not begun: "The licence 'X' ended on
   2026-10-01. Renew the licence or assign other rights before publishing." Other transitions do not care. The same guard applies to a
   workflow's auto-publish (the asset stays `approved` and the instance's `outcomeReason` says why) and to the scheduled publish job (the asset
   stays `approved`). Assigning a licence that is not in force to an already **published** asset is refused (409; bulk: skipped), since it would
   be unavailable at once. A draft may be put under any licence.
2. **Links** (`POST /assets/{id}/links`): optional `channel` and `region` (the consumer's context; letters, digits, `-`, `_`; case is ignored). A
   licence that has ended or not begun, a channel or region the licence does not cover, or one the asset's effective attributes exclude,
   is **403** `rights_restricted` with `reason` (the first sentence), `reasons` (all) and `title`. Without them only the licence's validity is
   checked. Both values are signed into the token (see below).
3. **The delivery origin at fetch time** reads the asset's current rights again; it never trusts what was true when the link was signed. Licence
   ended: **410** `rights_expired` (like an expired asset). Not yet begun: **403** `rights_not_valid`. A channel/region claimed by the link that
   the rights no longer allow: **403** `rights_restricted`. The wording is generic: the endpoint is public and the licence's name is not for link
   holders.
4. **Thumbnail batches** (`POST /assets/links/batch`): an asset whose licence is not in force gets a null link; `watermark_required` marks the
   token.

## watermark_required
It forces the delivery watermark exactly as "unpublished" does: the token's watermark flag is set at issue AND the origin sets it again from the
asset's current rights at fetch, so a link issued before the requirement was added serves a watermarked copy afterwards (and `Cache-Control` is
`private`). Consequences are the unpublished rules, unchanged: images and still renditions (thumb, preview, web, posters, transforms) carry
the watermark; the `original` rendition is refused (409 when linking, 403 on an old link); video transcodes are refused (409 when linking, 404 on
an old link; stills are watermarked); PDF pages (`pdf_pages-N`) are 404 when watermarked (the first-page stills are watermarked). **No override
permission exists** (nobody may fetch a clean copy of a watermark-required asset); that is deliberate and simple. The `watermarked` flag in the
link response is true for both reasons.

## Signed claims
`DeliveryClaims` gained optional `Channel` and `Region`; the token payload gained optional `C` and `R`, written only when set. A token signed
before this step has neither, still verifies, and a token without them is byte-for-byte what it was (unit tests cover the old format built by hand,
the new round trip, and tampering). They let the origin re-check rights against the consumer context and a delivery log attribute the fetch.

## The rights job
`RightsExpiryJob` (`rights.expiry`, Dam.Scheduler) runs once about 20 seconds after the scheduler starts and then hourly (`DAM_RIGHTS_POLL_CRON`,
default `0 5 * * * ?`). Like the other cross-tenant jobs it finds candidates through the read-only `dam_system` connection (it gained `SELECT` on
`rights`, with the usual `system_read_rights` policy; assets were already readable) and applies each change through a tenant-scoped scope with
the normal outbox and audit pipeline; one failing asset is logged and never stops the batch. The date is the UTC date of `IClock`, so tests
drive it with a fake clock. Every step is idempotent. In order:
1. **Catch up** assets whose copy of the rights scope differs from the record (see above).
2. **Alert.** For each asset in `draft`, `in_review`, `approved` or `published` whose licence ends within 30 days, or ended within the last 30 days,
   raise `rights.expiring` / `rights.expired`. The threshold due is the tightest one reached: 30 days left or fewer is the 30-day alert, 7 or
   fewer the 7-day alert, 1 or fewer (including 0, the last day) the 1-day alert; an asset assigned with 3 days left gets the 7-day alert once.
   A **ledger row** per (asset, rights, threshold, `valid_to`) is inserted with `ON CONFLICT DO NOTHING` and is the claim: only the run that
   inserts it raises the event, so repeated runs, restarts and two schedulers cannot repeat an alert, and extending the licence (a new
   `valid_to`) starts the sequence over. `rights.expired` uses threshold 0.
3. **Expire.** A **published** asset whose licence's `valid_to` is before today moves to `expired` with the reason **"Licence expired: <name>"**,
   as the system actor (audit actor `system`), through `Asset.ChangeStatus`, so `asset.status.changed` and the derived `asset.expired` fire
   and the owner gets the usual "Expired" lifecycle notice. The licence is re-read in the tenant scope before the change, so an extension in the
   meantime wins. Assets in any other status are left alone.

## Notifications and webhooks
- Category **`rights`**, "Licence alerts": in-app ON, email ON (the existing four, `mention` and `comments` are untouched; it appears last).
- Two public events join the catalogue (category `rights`): `rights.expiring` (`rightsId, assetId, rightsName, validTo, daysRemaining,
  thresholdDays`) and `rights.expired` (`rightsId, assetId, rightsName, validTo`). Webhooks (`rights.*` works), chat channels and the
  notification consumer pick them up from the catalogue. `validTo` is an ISO date. The housekeeping events `rights.created`, `rights.updated`,
  `rights.deleted`, `rights.document.added`, `rights.document.removed` and `asset.rights.changed` are internal (audit trail and, for the last,
  the index).
- The **owner** of the asset is told (active, not deleted asset; an asset with no owner raises the event but tells nobody): "Licence for 'Acme
  hero' expires in 7 days" / "... expires today" / "... has expired", link `/library/assets/{id}`, the email carrying the absolute form. Not the
  rights managers: they have the list endpoint with its status filter. An asset that is auto-expired also produces the existing "Expired:" notice
  (category `asset_lifecycle`, email off by default), so the owner may see two notes that day.

## Documents (release forms, licence files)
`POST /rights/{id}/documents` validates `{ fileName, contentType, size }` and returns `{ documentId, uploadUrl, method: "PUT", headers, expiresAt,
maxBytes }`, a presigned single PUT (15 minutes, content type signed) to `rights/{tenant}/{rights}/{document}` in the **originals** bucket (built from
ids only; the file never passes through the API). Nothing is recorded yet. `POST .../{documentId}/complete` (same name, type, size) then HEADs
the object, requires its size to equal the declared size and not exceed the limit, reads its first bytes and checks they look like the declared type
(`%PDF-`, the PNG or JPEG signature, a ZIP container for docx, no NUL bytes for text), and only then records the row. A file that fails is deleted from
storage and the answer is 422 (`size` or `contentType`); no object at the address is 409; completing twice is 409. Allowed: pdf, png, jpeg, docx and
txt, by declared content type AND file extension (they must agree), at most **25 MB**. `GET .../{documentId}/link` returns a 5-minute presigned GET
whose response is forced to `Content-Disposition: attachment` (name sanitised, RFC 6266 `filename*` for non-ASCII) and
`Content-Type: application/octet-stream`, so a browser downloads and never renders it. All of it needs `rights.manage`.

**Documents are NOT virus-scanned.** The ingest pipeline (ADR-0005) scans asset files; this path does not go through it. The mitigations are the type
allow-list, the content-start check, forced attachment downloads, a neutral type and short-lived addresses. A scan could be added by reusing
`IVirusScanner` in `complete`. Known limit: a PUT that is never completed leaves an object under the prefix (the presigned address expires, but the
bytes stay); there is no list operation on the store port to sweep them, so a janitor is future work. Deleting a document, or its rights record, deletes
the objects.

## API (tag Rights; all under `/api/v1`; 422 errors are keyed by field)

| Method and path | Body or query | Returns | Needs |
|---|---|---|---|
| GET `/rights` | `status` valid/expiring/expired/not_yet_valid, `q` (name contains) | `RightsDto[]` by name | `rights.manage` |
| POST `/rights` | `RightsInput { name, licenceType, licensor?, channels?, regions?, validFrom?, validTo?, watermarkRequired?, notes? }` | 201 `RightsDto` | `rights.manage` |
| GET `/rights/{id}` | | `RightsDto` | `rights.manage` |
| PUT `/rights/{id}` | `RightsInput` (replaces; omitted lists become empty) | `RightsDto` (`pendingAssets`) | `rights.manage` |
| DELETE `/rights/{id}` | | 204; **409** with `assetCount` while any live asset uses it | `rights.manage` |
| POST `/rights/{id}/assign` | `AssignRightsInput { assetIds (1-500), replace? }` | `AssignRightsResultDto { assigned, skipped, failed, items[{ assetId, outcome, reason }] }` | `rights.manage` on each asset |
| GET `/rights/{id}/documents` | | `RightsDocumentDto[]` | `rights.manage` |
| POST `/rights/{id}/documents` | `RightsDocumentUploadInput { fileName, contentType, size }` | 201 `RightsDocumentUploadDto` | `rights.manage` |
| POST `/rights/{id}/documents/{docId}/complete` | `RightsDocumentCompleteInput { fileName, contentType, size }` | 201 `RightsDocumentDto` | `rights.manage` |
| GET `/rights/{id}/documents/{docId}/link` | | `RightsDocumentLinkDto { url, expiresAt }` | `rights.manage` |
| DELETE `/rights/{id}/documents/{docId}` | | 204 | `rights.manage` |
| GET `/assets/{id}/rights` | | `AssetRightsDto { assetId, rights: RightsSummaryDto? }` | `assets.read` |
| PUT `/assets/{id}/rights` | `SetAssetRightsInput { rightsId? }` (null clears) | `AssetRightsDto` | `rights.manage` on the asset |
| GET `/assets/{id}/rights/check` | `channel?`, `region?` | `RightsCheckDto { allowed, reasons[], status, watermarkRequired, validTo, effectiveChannels[], effectiveRegions[], noChannelsAllowed, noRegionsAllowed }` | `assets.read` |
| POST `/assets/{id}/links` | `CreateLinkRequest` + `channel?`, `region?` | `DeliveryLinkDto` (403 `rights_restricted`) | `assets.read` (as before) |

Order of checks on asset routes: an unknown or unreadable asset is 404 first, then 403, then 409 (deleted or blocked asset), as elsewhere.
`RightsSummaryDto` (the asset's view) has `licensor` and `notes` only for callers with `rights.manage` (null otherwise); its `documentsCount` is
visible to every reader but the documents are not. `AssetDto` gained `rightsId`; it did NOT gain a status (that needs a join per listing row; use
`GET /assets/{id}/rights`). Rights records are not paged (a plain list, like retention policies); `RightsDto` carries the live `assetCount` and
`documentCount`. Operation ids: `ListRights`, `CreateRights`, `GetRights`, `UpdateRights`, `DeleteRights`, `AssignRights`, `ListRightsDocuments`,
`StartRightsDocumentUpload`, `CompleteRightsDocumentUpload`, `GetRightsDocumentLink`, `DeleteRightsDocument`, `GetAssetRights`, `SetAssetRights`,
`CheckAssetRights`.

Bulk assignment results: `assigned`; `skipped` (already this record; has other rights and `replace` is false, naming them; deleted or blocked; the
licence is not in force for a published asset); `failed` (not found, which is also what an unreadable or foreign asset looks like; not permitted).
Assigning needs read access to the asset first, then `rights.manage` on it.

## Permissions
No permission was added and no built-in role changed. `rights.manage` is held by Admin and Librarian only (Editor, Approver, Contributor, Viewer,
Dealer and ApiClient do not); the policy-matrix test already enumerates it per role and an integration test asserts every endpoint's answer per role.
Because the check on an asset passes the asset as a resource, an access rule that grants `rights.manage` in a folder or region scopes it there.
Reading an asset's rights summary and the check need only `assets.read`. Overrides ("let this role fetch a clean copy anyway") are not part of this
step.

## Persistence
Migration `RightsAndWatermarking`: the three tables with forced tenant RLS and the usual policy; `assets` gains `metadata_attributes` (jsonb,
backfilled), `rights_channels` and `rights_regions` (text[], default empty) and the foreign key; indexes on (tenant, rights_id) where set and
`valid_to`; a unique index on (asset, rights, threshold, valid_to) for the ledger and one on `storage_key`. Grants in `SecuritySql.RightsGrants`:
`rights` select/insert/update/delete, `rights_documents` select/insert/delete, `rights_alerts` select/insert only (an append-only ledger; its rows go
with their record or asset through the foreign-key cascades, which need no grant). `RightsSystemGrants` gives `dam_system` read-only access to
`rights`. `IObjectStore` gained `PresignPut` and `PresignDownload`.

## Deliberately not included
- Overrides and exceptions for the watermark, the original, or an expired licence.
- A virus scan of release documents and a janitor for never-completed uploads (see Documents).
- Hierarchical channel or region matching (a licence for `emea` covering `emea.de`); codes match exactly.
- Multiple rights per asset, per-version rights, usage counts against a licence (rights-managed quotas), or royalties.
- Paging or sorting options on `GET /rights`.
- Alerting rights managers, or sending a digest instead of one note per asset.
- A rights summary or status on every library listing row.
- The console screens (rights list and editor, the asset's rights panel and assign dialog, licence badges): a separate frontend step.

## Consequences
- `docs/api/openapi.json` and `@dam/sdk-js` regenerated. The generator types `AssetRightsDto.rights` without `| null` (as for other nullable
  object references); it is null at runtime for an asset with no rights.
- `AssetDto.attributes` can now contain `~restricted`; clients that display attributes should hide it or show "none".
- `CreateLinkRequest` and `DeliveryClaims` gained optional members; old callers and old tokens are unaffected.
- The notification categories are now seven (a test that listed six was updated). `DAM_RIGHTS_POLL_CRON` is a new scheduler setting.
- ADR-0030 says a workflow's auto-publish skips rights because they were not implemented yet. That no longer holds: auto-publish is subject to the
  licence guard too.
- Tests: pure rules in `tests/Dam.UnitTests/RightsTests.cs` (validity boundaries and open ends, the UTC date, alert thresholds, every metadata
  and licence combination of the intersection, the aggregate and its events, the check and its reasons, definition and document rules, the
  download header, signed claims including a hand-built old token, catalogue and notification text) and, against real Postgres, SeaweedFS,
  RabbitMQ and OpenSearch, `RightsTests.cs` (records, vocabularies, uniqueness, roles, assignment and derived attributes, update re-derivation,
  check, bulk assignment, publish guard, documents end to end, tenant isolation, RLS, grants, a channel-restricted `ApiClient`),
  `RightsDeliveryTests.cs` (through the real delivery host), `RightsExpiryJobTests.cs` (alerts once per threshold across restarts, console, email,
  signed webhooks, automatic expiry and what it leaves alone, catch-up, tenant separation, scheduled publish) and `RightsSearchTests.cs` (search
  as a signage client, and the index following rights through the consumer).

## Console (for the frontend step)
- The asset page can show `GET /assets/{id}/rights` (name, licence type, validity and a badge from `status`/`daysRemaining`, channels and regions,
  watermark note); managers also get licensor, notes, the documents list and an assign/clear control (`PUT /assets/{id}/rights`).
- A rights screen lists `GET /rights` (filter by `status`, search by `q`), edits with `PUT`, shows `assetCount` and warns on the 409 from delete.
  Channel and region pickers should offer the tenant's channel and region vocabulary terms (the API accepts exactly their codes).
- Upload a document in two steps: `POST .../documents`, `PUT` the file to `uploadUrl` with the returned `headers`, then `.../complete` with the same
  name, type and size. Downloading opens `.../link` in a new tab.
- Use `GET /assets/{id}/rights/check?channel=&region=` to explain why an asset is not available somewhere; show `reasons` as is.
- A notification with category `rights` links to `/library/assets/{id}`.

## Console
- **Rights admin** (`/admin/rights`, needs `rights.manage`): cards with status chips ("N days left"), validity, channel/region chips, watermark badge and counts; a
  summary strip; search and status filters in the URL; a form drawer with channel and region pickers fed by the tenant's vocabularies (free entry when none),
  live status from the dates, a warning before a change that narrows where assigned assets can be used, and a documents section (presign -> PUT straight to
  storage -> complete, forced-download links, a "not virus-scanned" note). Deleting a record still assigned to assets shows the server's count.
- **Asset page**: a Rights panel (status, validity, effective channels/regions - never the raw `~restricted` marker, watermark badge; managers also see licensor and
  notes), assign/change/remove, and an on-demand "Check availability" for a channel and region. Publish is disabled with an explanation and a "Review rights" jump
  when the licence has ended or not begun, a small licence chip sits beside the status badge, and a rights 403 on a preview shows a calm stage message.
- **Library**: a bulk "Assign rights" action (managers only, optional replace, batches of 500, per-asset results).
- A published, watermark-required asset previews through a watermarked preview only (the original is withheld), so the preview no longer asks for `original`.
- Hindi strings for `rights` and `rightsAsset` fall back to English.
