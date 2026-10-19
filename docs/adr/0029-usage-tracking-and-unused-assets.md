# ADR-0029: Usage tracking and unused assets

## Status
Accepted

## Context
Orbit recorded no usage. The data model has had a `UsageEvent` since the start (spec A4.1) and `ApiClient` holds
`usage.write` for PADS4 proof-of-play, but nothing stored an event, so the dashboard could not answer the first question
a library owner asks: which assets are not being used? That question needs real usage events from the console (downloads,
copy-link) and from outside systems (signage players, portals, apps), plus a few library-wide figures for a professional
dashboard. Spec Step 2.7 describes the full analytics platform (partitioned events, nightly rollups, reports); this ADR
delivers the part the dashboard needs now and leaves the rest where Step 2.7 puts it.

## Decisions

**Two tables.** `usage_events` is the append-only record (id UUIDv7, tenant, asset, event type, channel, rendition, actor,
`occurred_at`, optional `client_event_id`, `context` jsonb, `created_at`). `asset_usage` is a per-asset summary
(`last_used_at`, `first_used_at`, `use_count`) maintained in the same transaction as the events. "Not used since" is then
an indexed read of one narrow table joined to `assets`, not a scan of every event. An asset with no `asset_usage` row has
never been used. Both tables have tenant RLS exactly like the others (`ENABLE` + `FORCE`, `tenant_isolation` policy); the app
role may `SELECT, INSERT` events (never update or delete) and `SELECT, INSERT, UPDATE` the summary.

**What counts as use.** An event someone chose to record: a console download or copy-link, a proof-of-play, a portal or app
request. Event types are `view`, `download`, `share`, `embed`, `place`, `play`. Console previews and thumbnails are not
events: they happen whenever someone browses the library and would make every asset look used. Callers decide what they
send; the server does not infer usage from delivery traffic.

**Idle definition.** `idleSince = coalesce(last_used_at, created_at)`; an asset is unused for N days when
`idleSince < now - N days` (or `< before`). History starts empty, so an asset that has never been used is judged from its
creation date and a new asset is never flagged. `last_used_at` is the newest `occurred_at`, not the newest report time, so a
backfill of old events (external systems batch hourly; demo data is back-dated) moves an asset's last use to where it
really was, even if that is before the asset's own creation date (imported history). This is deliberately the literal
`coalesce`, not `max(created, last used)`.

**Writes.** `POST /usage/events` takes 1-500 events. Needs only `assets.read` on each asset, because every console user
records their own downloads; the `usage.write` permission already held by `ApiClient` stays in the catalogue but is not
required. An event for an asset that does not exist and one the caller may not read both count as `rejected`, so the
response never reveals whether an asset exists. Malformed input (unknown event type, `occurredAt` more than 5 minutes
ahead or more than 5 years back, over-long fields, `context` that is not an object or exceeds 4 KB, an empty or over-500
batch) fails the whole request with 422 naming each bad event (`Events[3].EventType`). A repeated `(tenant, clientEventId)`
is a `duplicate` (a unique partial index), so a sender can retry a batch safely. The write is one SQL statement
(`EfUsageStore`): `INSERT ... ON CONFLICT DO NOTHING` for the events, then an `INSERT ... ON CONFLICT DO UPDATE` for the
summary that increments `use_count` in the database. That makes a re-sent id harmless even when two requests race, and
concurrent batches for a hot asset cannot lose counts; both are covered by integration tests. The `actor_id` is the DAM
user of the caller (also for an `ApiClient` token, which is provisioned like any user); the column is nullable for events
the system itself records later.

**No domain event per usage event.** Every other write raises a domain event that goes to `audit_log` (hash-chained, one
advisory lock per tenant) and the outbox. Usage events can number in the thousands per minute; sending each through that
chain would serialise ingest behind the audit log and bloat the outbox with facts that are already stored in a dedicated
append-only table. The spec's `usage.recorded` event is therefore not emitted; a consumer that needs a stream should read
`usage_events`, or a batch-level event can be added later without changing the table.

**Reads.** Two queries, both behind the existing `analytics.read` permission, now also granted to Editor and Approver
(Admin and Librarian already had it).
- `GET /analytics/unused-assets` takes `days` (1-3650) or `before` (a date, meaning the start of that day UTC, or a date
  and time); giving both is 422, giving neither means 30 days. `status` defaults to `published`; `all` means every status
  except `deleted` and `blocked`; any other value is a single status. `type`, `folderId` (+ `includeSubfolders`, default
  true), `limit` (default 50, max 200) and a keyset `cursor`. Order is idle-since ascending (longest idle first), then id
  (Postgres uuid byte order). Each item carries `folderPath` as the display names of the folder and its ancestors
  ("Brand / Cars"), and `sizeBytes` of the current version.
- `GET /analytics/overview?days=30` (1-365): usage totals with the previous same-length period for a trend, a zero-filled
  per-day series, breakdowns by event type and by channel (top 6; no channel is `unspecified`), the five most-used assets,
  assets created per week for 12 weeks, and storage by asset type. "Days" are UTC calendar days ending today, so the
  series always adds up to the total; the previous period is the equal number of days before the first one.

**Scope.** The unused list honours the caller's `assets.read` scope exactly as `ListAssetsHandler` does: the idle filter,
status, type and folder filters run in SQL, and the access clauses (folder/collection rules, attribute restrictions,
status visibility) are applied in memory to the rows in list order, capped at 5,000 rows scanned. `total` is the exact
count of readable matches when the scan finished under the cap; otherwise `totalIsExact` is false and `total` is what was
found so far. When one access clause covers every candidate (an unscoped role grant with no attribute limits, which for a
published-only role also requires the list to be about published assets), the database pages and counts and the total is
always exact, however large the library. A caller with no matching access gets an empty list, not an error, and a count
never includes assets they cannot read. The overview is different on purpose: usage totals, uploads and storage are
tenant-wide aggregates for whoever holds `analytics.read` (a role grant, not a scoped one); only `topAssets` is limited to
assets the caller can read, because it names assets.

**Storage bytes** are the `size_bytes` of each non-deleted asset's current version (`assets.current_version_id` to
`asset_versions`). Earlier versions and generated renditions are not counted, so the figure is "what the library holds",
not the object-store bill; an asset still waiting for its first file counts toward its type's asset count with zero bytes.
`Upload.SizeBytes` is a staging figure and is not used.

**Dangling rows.** Both tables reference `assets(id)` with `ON DELETE CASCADE`. The application never hard-deletes an
asset (purge is the status change to `deleted`, and the app role has no `DELETE` on `assets`), so in practice the rows stay
with the asset and a deleted asset's history is kept; the cascade is a safety net for an operator who removes an asset by
hand. No cleanup code in the purge path was needed.

## Deliberately not included
- **Partitioning and nightly rollups** (spec Step 2.7). One indexed table is enough for the dashboard now; partitioning
  `usage_events` by month and rolling up per-day/per-asset counts is the next step when volume demands it (the spec's
  1M events/day target). The overview's `GROUP BY` over the period runs on the `(tenant_id, occurred_at)` index.
- **ClickHouse or any second store**, per the spec's "ClickHouse-free" approach.
- **Retention of events.** Events are kept indefinitely for now. When rollups exist, raw events older than the retention
  window can be dropped partition by partition; the summary table is unaffected.
- **Per-asset analytics, report API and scheduled reports** (`GET /analytics/assets/{id}`, `/analytics/reports/{name}`).

## Consequences
- Migration `UsageTracking` adds the two tables, their indexes (`(tenant_id, asset_id, occurred_at desc)`,
  `(tenant_id, occurred_at)`, unique partial `(tenant_id, client_event_id)` where not null; `asset_usage (tenant_id,
  last_used_at)`), RLS and grants. Built-in roles are re-synced from code at API start, so existing tenants pick up
  `analytics.read` for Editor and Approver on the next restart.
- `docs/api/openapi.json` and `@dam/sdk-js` regenerated. The OpenAPI generator marks every DTO field required (nullable
  where null is allowed), so generated request types need optional fields passed explicitly as `null`.
- Tests: pure rules and validators (`tests/Dam.UnitTests/UsageTests.cs`) and the API against a real Postgres
  (`tests/Dam.IntegrationTests/UsageTests.cs`): recording, dedupe including concurrent races, rejected assets, windows by
  days and `before`, never-used assets, filters, paging (including equal idle times), scope enforcement and the scan cap,
  `analytics.read` per role, the overview shape, and tenant isolation including RLS on the new tables.

## Console (dashboard and unused-assets tracker)
- **What the console records.** "Open original" and "Copy link" on the asset page post a `download` / `share` event
  (channel `console`), and only when the link is to the real file; watermarked draft previews, posters, thumbnails and
  browsing never count. The beacon is fire-and-forget so a failure cannot get in the way of the action.
- **Dashboard.** Home is now an analytics dashboard: headline cards (total with a 12-week upload trend, published, uses in
  the last 30 days with a trend against the previous 30, storage), a daily usage chart with a by-channel split, "Library at a
  glance" (status, type and storage by type as distribution bars), a "Not used" tracker, most-used assets, then review queue,
  recent and collections. Charts are dependency-free CSS/SVG in `@dam/ui-kit` (`BarChart`, `DistributionBar`, `Sparkline`,
  `TrendBadge`, chart tokens `--dam-chart-1..6`) and expose one accessible summary each. A role without `analytics.read`
  gets the same cards with review and draft counts in place of usage and storage, and no analytics panels.
- **Unused tracker.** The window is chosen as 30 days, 6 months (183 days), 1 year or Custom (amount and days / weeks /
  months / years, up to 10 years), kept in the URL (`?unused=6m`, `c:90d`) and remembered per browser. It lists published
  assets by default, or all statuses; the full page (`/insights/unused`) adds a type filter and paging. Rows show last used
  (or "Never used"), idle days and size. Selecting rows offers **Archive**: a published asset is expired and then archived
  (the lifecycle only allows archiving an expired asset), each by its own transition, so a failure on one is reported
  without stopping the rest, and any asset can be withdrawn back to draft later. **Export CSV** fetches every match (up to
  5,000), neutralises formula-leading cells and writes a UTF-8 BOM for spreadsheets.
- Hindi strings for the `analytics` area are not translated yet; they fall back to English.
