# ADR-0031: Notifications, webhooks and the audit log

## Status
Accepted (Step 2.6, backend)

## Context
Until now the platform recorded everything (every command writes its event to the hash-chained `audit_log` and the outbox, D15) but
told nobody. People missed approvals waiting for them, integrations (the PADS4 plugin, Step 6.4) had no way to hear about a publish,
and the audit trail could only be read with SQL. Step 2.6 closes that: a notification service (in-app, email, Slack/Teams), signed
webhooks with retries and a delivery log, and an audit viewer with export and a SIEM feed. Everything consumes the events that already
exist; no domain code changed except where noted below. All names, roles and content in code and tests are generic.

## Part A: notifications

**Rules catalogue** (`NotificationRules`, pure functions in `Dam.Application/Notifications`; no customer content):

| Event | Who is told | Category | Text |
|---|---|---|---|
| `workflow.task.created` | people who can decide the task now | `workflow_task` | "Approval needed: <asset>" |
| `workflow.sla.breached` | the same, now including the escalation targets | `sla` | "Overdue: <asset>" |
| `workflow.task.completed` | whoever started the workflow and the asset's owner, never the person who decided | `workflow_outcome` | "Approved: ... at the 'X' step" / "Step approved" / "Rejected: ...: <comment>" |
| `asset.status.changed` to `expired` or `archived` | the asset's owner | `asset_lifecycle` | "Expired: <asset>" / "Archived: <asset>" |

**Recipients are people who could actually decide.** `NotificationPlanner` takes a task's candidates (users named directly, plus active
holders of an assignee role, and for an escalated task the escalation targets) and keeps those for whom `WorkflowTaskAccess.CanDecide`
is true, the very function the task inbox and the decision endpoint use: assignee, `workflow.tasks.decide`, `assets.approve` on that
asset (so an access rule scoped to other folders or regions excludes them), and the segregation-of-duties rule. A role member with no rule
for the folder is told nothing. `UserProfiles` assembles the `AccessProfile` of any user (roles, groups, rules, attributes) for this.

**Roles from the identity provider are now remembered.** A request is authorised from the token's roles, which are not stored; a
background consumer has no token. `users.idp_roles` (lower-cased names, migration `NotificationsWebhooksAudit`) is refreshed at JIT
provisioning from the token (`ProvisionUserCommand.Roles`; the provisioning cache key includes them so a role change re-syncs at once).
"Holds the role" for notifications means a direct assignment, a group's role or this last-seen IdP role. Existing users acquire it on
their next request. An IdP-only role holder who has never signed in since this step is not found until they do.

**Channels.**
1. *In-app*: table `notifications(user_id, type, category, title, body, link_url, asset_id, event_id, read_at, created_at)`. `link_url`
   is a console path (`/tasks`, `/library/assets/{id}`), never absolute. Own rows only: every query filters by the caller's user id taken
   from the token, never from the client; another person's id answers 404.
2. *Email*: `IEmailSender`; `SmtpEmailSender` (MailKit) when `DAM_SMTP_HOST` is set, otherwise a no-op and `emailAvailable=false` in the
   preferences response. One layout (`EmailTemplates`): tenant name in the header, title, escaped paragraphs, one button, a footer; an HTML
   and a plain-text part rendered from the same model; subject `[<tenant>] <title>` with line breaks stripped; links absolute from
   `DAM_CONSOLE_PUBLIC_URL` (no button when it is unset or not http(s)). Mailpit in the compose `mail` profile catches dev mail.
3. *Chat*: `notification_channels(kind slack|teams, name, url_cipher, url_hint, events[], active)`. The channel's incoming-webhook URL is a
   bearer secret: stored encrypted, shown only as a hint (`https://host/…a1b2`). `events[]` are patterns as for webhooks (`workflow.*`,
   `*`) over the public event catalogue (below). Slack gets `{ "text": "*Title*\nBody\n<link|Open in Orbit>" }` (mrkdwn-escaped); Teams
   gets a message with one Adaptive Card (the format Workflows webhooks accept; the retired Office 365 connector's MessageCard is not
   produced). `POST /notification-channels/{id}/test` posts a test message and reports what the service answered.

**Preferences.** `notification_preferences(user, category, in_app, email)`, a row only once a person chooses. The four categories are a
fixed catalogue returned by `GET /notifications/preferences` so the UI hardcodes nothing. Defaults: in-app on everywhere; email on for
`workflow_task` and `sla` (things waiting on the person), off for `workflow_outcome` and `asset_lifecycle`. A person with neither channel
on for a category is not notified at all. Chat channels are tenant-level and ignore personal preferences.

**At-least-once, de-duplicated.** `NotificationConsumer` (own queue `dam.q.notifications`, bound to every public source event) records
what it has delivered in `notification_deliveries`, unique per (event id, channel, recipient). In-app: the ledger row and the
notification are written together in their own transaction, so a redelivery (even under a different message id, since the key is the
domain event id) skips it. Email and chat: checked before sending, written after the server accepted the message. These are committed
separately from the message's own transaction on purpose: if the mail server is down the handler throws and the host retries with its
tiered backoff (then the DLQ), but each person's in-app notification is already committed and what was already sent is already recorded,
so a retry repeats only what failed. The remaining window (a crash between a successful send and its ledger write) can send once more.

## Part B: webhooks

**Entities.** `webhooks(name, url, secret_cipher, secret_last4, events[], active, created_by, ...)` and `webhook_deliveries(webhook_id,
event_id, event_type, payload jsonb, status pending|succeeded|failed, attempt, next_attempt_at, last_status_code, last_error,
last_duration_ms, response_excerpt, created_at, delivered_at, replay_of)`. A webhook's deliveries are deleted with it (foreign key
cascade); otherwise the app role can never delete them. Unique per (webhook, event) except replays.

**Event catalogue** (`GET /webhooks/events`, `EventCatalogue`): `asset.created`, `asset.metadata.updated`, `asset.status.changed`,
`asset.published`, `asset.expired`, `asset.archived`, `asset.deleted`, `asset.version.added`, `asset.legal_hold.changed`, `rendition.ready`,
`collection.updated`, `collection.assets_changed`, `workflow.task.created`, `workflow.task.completed`, `workflow.sla.breached`,
`webhook.ping`. Internal events (ingest steps, role or access-rule changes, ...) are never delivered. The spec's `asset.published`,
`asset.expired`, `asset.archived`, `asset.deleted` are *derived*: when `asset.status.changed` has that `to`, subscribers also see the named
event (flagged `derived: true`), with an id derived deterministically from the original so a redelivery de-duplicates.
Patterns: an exact name, `asset.*` (prefix: every name starting `asset.`) or `*`; validated and required to match at least one catalogue
event when saved.

**Envelope and signature.**
```
POST <url>      Content-Type: application/json
X-Dam-Event: asset.published
X-Dam-Delivery: <delivery id>          (changes per delivery and per replay)
X-Dam-Timestamp: <unix seconds>
X-Dam-Signature: v1=<hex HMAC-SHA256(secret, timestamp + "." + raw body)>

{ "id": <event id>, "type": "asset.published", "tenantId": ..., "occurredAt": ..., "data": { ... camelCase fields of the domain event ... } }
```
`id` is the domain event id (the same across retries and replays: use it to de-duplicate). `data` is the event's own fields renamed to
camelCase with `EventId` and `OccurredAt` hoisted out. The body is stored as `jsonb`, which normalises key order and whitespace; what is
sent is exactly the stored text, and the signature is computed over exactly those bytes. **Verify** like this (the key is the secret string
as shown, `whsec_...`, as UTF-8):
```
expected = "v1=" + hex(HMAC_SHA256(secret, timestampHeader + "." + rawBody))
accept only if constant_time_equal(expected, signatureHeader)   (the header may hold several v1= values: any match)
  and |now - timestampHeader| <= 5 minutes                       (rejects captured deliveries replayed later)
```
`Dam.Shared.Webhooks.WebhookSignature` is that code (`Sign`, `Verify` returning `Valid | MissingHeaders | MalformedTimestamp |
OutsideTolerance | BadSignature`, a wrong secret is never reported as stale), shipped in the shared assembly so `Dam.Sdk.DotNet` users
and the PADS4 plugin verify with the same code the server signs with. Node: `crypto.createHmac('sha256', secret).update(ts + '.' +
rawBody).digest('hex')`; Python: `hmac.new(secret.encode(), f'{ts}.'.encode() + raw, hashlib.sha256).hexdigest()`; compare with
`timingSafeEqual` / `hmac.compare_digest`, and hash the raw bytes, not re-serialised JSON.

**Secrets.** Generated on the server (`whsec_` + 32 random bytes, base64url), returned once by create and rotate and never again (the
API shows `secretLast4`). Stored with AES-256-GCM (`AesGcmSecretProtector`): key from `DAM_SECRETS_KEY` through HKDF-SHA256, a fresh nonce
per value, the purpose (`webhook-secret`, `chat-url`) authenticated as associated data. Without a key every protect/unprotect fails loudly
rather than storing in the clear; a key under 16 characters is refused. The dev key is in `appsettings.Development.json` like the delivery
signing key. Rotating invalidates the old secret at once; a replay signs with the current secret. Key rotation of `DAM_SECRETS_KEY`
itself is not built (values would need re-encrypting).

**SSRF.** `AddressGuard` refuses loopback, private (10/8, 172.16/12, 192.168/16), link-local (169.254/16, which holds the cloud metadata
address), carrier-grade NAT, multicast, reserved and documentation ranges for IPv4, and for IPv6 loopback, link/site-local, unique-local
(`fc00::/7`, the AWS IPv6 metadata address), multicast, documentation and the IPv4-embedding forms (mapped, NAT64, 6to4, compatible).
Only `http` and `https`, no credentials in the URL. Checked when a webhook or channel is saved (syntax, then every resolved address) and
on **every delivery**: `OutboundHttp`'s connect callback resolves the host, refuses if any answer is blocked, and dials the IP it just
validated, so a DNS answer that changes between check and connect (rebinding) gains nothing; redirects are never followed (a 302 is the
response); system proxies are ignored; timeout 5 s (`DAM_WEBHOOK_TIMEOUT_SECONDS`); the response body is read to a 2 KB excerpt and no
further. `DAM_WEBHOOK_ALLOW_PRIVATE=true` (development, tests) lifts the address rule only, never the scheme rule.

**Delivery.** `WebhookConsumer` (own queue `dam.q.webhooks`) turns each public event into one pending delivery per matching, active
webhook, in the message's transaction. After the commit (`EventConsumer.AfterCommitAsync`, new, best effort) it hands the ids to
`WebhookDispatcher`, a singleton with eight background workers, which make the first attempt at once, so a slow receiver never delays the
consumer or other tenants. An attempt is first **claimed** with one conditional `UPDATE ... SET attempt = attempt + 1, next_attempt_at =
<lease>`, so the creator's immediate attempt, the retry job and another worker can never send the same delivery together; a new delivery
is leased to its creator for 30 s and a crashed attempt is retried after its lease. The HTTP call holds no database connection.
`WebhookRetryJob` (`Dam.Scheduler`, every 10 s, `DAM_WEBHOOK_RETRY_POLL_CRON`) finds due pending deliveries across tenants through
`dam_system` (one `SELECT` grant and policy on `webhook_deliveries`) and attempts each through a tenant-scoped scope, eight at a time,
reading time from `IClock`. Backoff after failed attempt n: 10 s, 1 min, 5 min, 30 min, 2 h, 6 h, 12 h (each scaled by a random
0.8 to 1.2), eight attempts in all (`DAM_WEBHOOK_MAX_ATTEMPTS`), then `failed`; any non-2xx, timeout, connection error or blocked address is a failure. A deleted or switched-off
webhook fails its pending deliveries on the next attempt. **Ping** (`POST /webhooks/{id}/ping`) and **replay** (`POST
/webhook-deliveries/{id}/replay`, a new delivery with `replayOf`, same event id and payload) create the delivery in the command and attempt it within the request, returning the outcome; both are 409 for a switched-off webhook.

**Measured latency** (event committed to the outbox to receiver having the request, real RabbitMQ and Postgres in containers, relay
polling every 50 ms in tests): median 106 ms, p90 163 ms, max 275 ms over 20 sequential events. The relay's production default poll is
500 ms (`OutboxPollMs`), which bounds the worst case at about 0.6 s plus the receiver's own time, far inside the 5 s target. See the live
live figure below.

Live on the dev stack (separate API, Worker and Scheduler processes, production relay settings, a local node receiver): 196 to 224 ms
from the start of the request that created the asset to the receiver having the signed delivery (15 sequential events, median 216 ms).

## Part C: audit log

`audit_log` is unchanged. `EfAuditReader` reads it on its own connection inside one `REPEATABLE READ READ ONLY` transaction with
`app.tenant_id` set, so RLS applies and an export's header and body come from the same snapshot even while rows are appended. A new index
`(tenant_id, ts)` serves the time filters.

- `GET /audit`: filters `from`, `to`, `actor` (the recorded identity subject **or** the Orbit user id), `action` (prefix; `%` and `_` are
  literal), `entityType`, `entityId`, `q` (free text over action, entity type, entity id and actor name or email; not the payload), newest
  first by chain position, `limit` up to 200, keyset `cursor` (the last chain position seen). `AuditEntryDto` is `{ id, chainNo,
  occurredAt, actorId, actorUserId, actorName, action, entityType, entityId, before, after, ip, hash, prevHash }`; `actorId` is what was
  recorded (the IdP subject, or `system` / `worker`), `actorUserId`/`actorName` are resolved when it is a known user. `GET /audit/actions`
  lists the distinct action names for a picker.
- `GET /audit/verify?from&to` recomputes the chain **in the database** with the same `audit_row_hash` the append trigger uses (one
  definition of what a row's hash covers) over the rows whose time is in the range, widened to every row between the first and last of
  them (the chain is by position, so a clock that stepped back must not look like a hole), and checks, in order: no gap in `chain_no`,
  `prev_hash` equals the previous row's hash (for the first row, the stored hash of the row before the range), the stored hash equals the
  recomputed one. Result `{ verified, checked, firstId, lastId, firstChainNo, lastChainNo, firstHash, lastHash, brokenAtId,
  brokenAtChainNo, reason: gap | prev_hash_mismatch | hash_mismatch, from, to }`; it stops at the first bad row. A changed field, a deleted
  row and a recomputed hash on the changed row (caught on the next row) are each detected. Limits: the tail cannot be proven: deleting
  the newest rows leaves a shorter, valid chain, which is what the first/last hash in an export, the SIEM copy and database backups are for.
- `GET /audit/export?format=csv|cef&<filters>` streams, row by row, never buffering the range. It begins with `#` comment lines: product,
  tenant, time generated, range, filters, `chain-verification: VERIFIED|FAILED` (the chain over the *time range*, whatever the other
  filters), rows checked and chain positions, first and last chain hash, `chain-broken-at` when failed, `rows-in-export`; then the rows
  (CSV: a header row and RFC 4180 cells; cells that begin `= + - @` are prefixed with `'` so a spreadsheet does not run them; CEF: one
  event per line); then `# end of export: rows=N first-hash=... last-hash=... chain-verification=...`, so a truncated download lacks its end.
  A broken chain still exports (the point is to tell the reader). `Content-Disposition: attachment`.
- **CEF mapping** (`CefFormatter`): `CEF:0|Orbit|DAM|<version>|<action>|<readable action>|<severity>|<extension>`. Header fields escape `\`
  and `|`; extension values escape `\` and `=`; line breaks become `\n` / `\r` so one event is one line. Extension: `rt` (epoch ms),
  `externalId` (audit id), `act` (action), `suser` (actor name), `suid` (actor id), `src` (ip), `cs1/cs1Label=entityType`, `cs2=entityId`,
  `cs3=tenantId`, `cs4=hash`, `cs5=prevHash`, `cn1=chainNo`, `msg` (the payload, cut at 1,000 characters). Severity: 7 rejected/blocked; 6
  `workflow.sla.breached`, `legal_hold`, and any `access_rule.*`, `role.*`, `tenant.*`, `api_client.*`, `webhook.*`,
  `notification_channel.*`; 5 `*.deleted` / purge; otherwise 3.
- **SIEM forwarder** (`SiemForwardJob`, `Dam.Scheduler`, every 30 s, registered only when `DAM_SIEM_ENABLED=true`; `DAM_SIEM_POLL_CRON`):
  for each tenant (listed through `dam_system`'s existing tenants grant) it reads rows after the tenant's cursor (`siem_cursors`, tenant
  RLS) through the tenant-scoped reader, sends them as RFC 5424 messages `<PRI>1 <ts> <host> orbit-dam - audit - CEF:0|...` (facility
  local0 = 16; syslog severity from the CEF severity: 6/5/4/2) over TCP or UDP (`DAM_SIEM_HOST`, `DAM_SIEM_PORT` default 514,
  `DAM_SIEM_PROTOCOL` tcp|udp, `DAM_SIEM_FRAMING` newline (default) | octet (RFC 6587 octet counting), `DAM_SIEM_HOSTNAME`), and only then
  advances the cursor. Up to four batches of 500 per tenant per run; a backlog drains over runs. First run forwards the existing history
  (`DAM_SIEM_BACKFILL=false` starts from now). A restart neither re-sends nor skips; a failed send is logged and retried next tick with
  the cursor where it was; a crash between sending and saving the cursor repeats one batch (at-least-once, the chain number and hash
  make repeats detectable). UDP has no delivery guarantee. No new grant on `audit_log` was needed: `dam_system` still cannot read it.

## API surface (tags Notifications, Webhooks, Audit; all under `/api/v1`)

| Method and path | Body or query | Returns | Needs |
|---|---|---|---|
| GET `/notifications` | `unread`, `limit`, `cursor` | `Page<NotificationDto>` | sign-in (own rows) |
| GET `/notifications/unread-count` | | `UnreadCountDto { count }` | sign-in |
| POST `/notifications/{id}/read` | | `NotificationDto` (404 if not yours) | sign-in |
| POST `/notifications/read-all` | | `MarkAllReadResultDto { marked }` | sign-in |
| GET `/notifications/preferences` | | `NotificationPreferencesDto { categories[], emailAvailable }` | sign-in |
| PUT `/notifications/preferences` | `{ preferences: [{ category, inApp, email }] }` | `NotificationPreferencesDto` | sign-in |
| GET, POST `/notification-channels` | `NotificationChannelInput { kind, name, url, events[], active }` | `NotificationChannelDto[]`, 201 `NotificationChannelDto` | `notifications.manage` |
| GET, PUT, DELETE `/notification-channels/{id}` | input (a null `url` keeps the stored one) | `NotificationChannelDto`, 204 | `notifications.manage` |
| POST `/notification-channels/{id}/test` | | `ChannelTestResultDto { ok, statusCode, error, durationMs }` | `notifications.manage` |
| GET `/webhooks/events` | | `WebhookEventTypeDto[]` | `webhooks.manage` or `notifications.manage` |
| GET, POST `/webhooks` | `WebhookInput { name, url, events[], active }` | `WebhookDto[]`, 201 `WebhookCreatedDto { webhook, secret }` | `webhooks.manage` |
| GET, PUT, DELETE `/webhooks/{id}` | input | `WebhookDto`, 204 | `webhooks.manage` |
| POST `/webhooks/{id}/rotate-secret` | | `WebhookSecretDto { webhook, secret }` | `webhooks.manage` |
| POST `/webhooks/{id}/ping` | | `WebhookDeliveryDetailDto` | `webhooks.manage` |
| GET `/webhooks/{id}/deliveries` | `status`, `eventType`, `from`, `to`, `limit`, `cursor` | `Page<WebhookDeliveryDto>` | `webhooks.manage` |
| GET `/webhook-deliveries` | the same plus `webhookId` | `Page<WebhookDeliveryDto>` | `webhooks.manage` |
| GET `/webhook-deliveries/{id}` | | `WebhookDeliveryDetailDto` (payload, response excerpt) | `webhooks.manage` |
| POST `/webhook-deliveries/{id}/replay` | | `WebhookDeliveryDetailDto` | `webhooks.manage` |
| GET `/audit` | filters above | `Page<AuditEntryDto>` | `audit.read` |
| GET `/audit/actions`, `/audit/verify`, `/audit/export` | | `string[]`, `AuditVerificationDto`, a file | `audit.read` |

**Permissions.** `webhooks.manage` and `audit.read` already existed (Admin only). Chat channels get a new `notifications.manage`
(Admin only, added to `Permissions.All`; the policy-matrix test enumerates `All`, so it is covered): a channel is an integration of the
tenant, not a personal setting. The inbox needs only authentication. No built-in role changed.

## Configuration (env)
`DAM_SECRETS_KEY`; `DAM_SMTP_HOST`, `_PORT` (587), `_USER`, `_PASSWORD`, `_FROM`, `_TLS` (`auto` | `starttls` | `tls` | `none`);
`DAM_CONSOLE_PUBLIC_URL`; `DAM_WEBHOOK_ALLOW_PRIVATE`, `DAM_WEBHOOK_TIMEOUT_SECONDS`, `DAM_WEBHOOK_MAX_ATTEMPTS`, `DAM_WEBHOOK_RETRY_POLL_CRON`;
`DAM_SIEM_ENABLED`, `_HOST`, `_PORT`, `_PROTOCOL`, `_FRAMING`, `_BACKFILL`, `_HOSTNAME`, `_POLL_CRON`. The API, Worker and Scheduler read
the secrets key and webhook settings (the API for create-time checks and ping/replay, the Worker for consumers, the Scheduler for
retries); the Worker also reads SMTP; the Scheduler reads SIEM.

## Deliberately not included
- Per-event, per-asset or digest preferences, quiet hours, a notification for comments or mentions (Step 2.4) or expiry alerts (Step 2.5):
  the catalogue is a table in code and grows with those steps.
- Localised notification text and email (English only for now).
- Per-webhook custom headers, payload transforms, IP allow-lists for receivers, mTLS to receivers, secret rotation with an overlap window
  (the verifier already accepts several signatures, the sender sends one), and re-encryption when `DAM_SECRETS_KEY` changes.
- Deleting old deliveries or notifications (no retention job yet; both are small rows).
- Teams MessageCard (retired connector) and Slack Block Kit: a single text or Adaptive Card message is sent.
- Syslog over TLS (RFC 5425) for the SIEM feed.

## Consequences
- `docs/api/openapi.json` and `@dam/sdk-js` regenerated: tags Notifications, Webhooks, Audit (operation ids `ListNotifications`,
  `GetUnreadNotificationCount`, `MarkNotificationRead`, `MarkAllNotificationsRead`, `GetNotificationPreferences`,
  `UpdateNotificationPreferences`, `ListNotificationChannels`, `CreateNotificationChannel`, `GetNotificationChannel`,
  `UpdateNotificationChannel`, `DeleteNotificationChannel`, `TestNotificationChannel`, `ListWebhookEventTypes`, `ListWebhooks`, `CreateWebhook`,
  `GetWebhook`, `UpdateWebhook`, `DeleteWebhook`, `RotateWebhookSecret`, `PingWebhook`, `ListWebhookDeliveries`, `ListAllWebhookDeliveries`,
  `GetWebhookDelivery`, `ReplayWebhookDelivery`, `ListAudit`, `ListAuditActions`, `VerifyAudit`, `ExportAudit`).
- Migration `NotificationsWebhooksAudit`: tables `webhooks`, `webhook_deliveries`, `notifications`, `notification_preferences`,
  `notification_channels`, `notification_deliveries`, `siem_cursors` (tenant RLS forced on each), `users.idp_roles`, index
  `ix_audit_log_tenant_id_ts`; grants in `SecuritySql.NotificationGrants`/`WebhookSystemGrants`.
- `EventConsumer` gains `AfterCommitAsync`; `Dam.Worker` hosts two more consumers and the dispatcher; `Dam.Scheduler` two more jobs.
- Compose: a `mail` profile (Mailpit, SMTP on host port 11025, UI on 18025) and `make up-mail`.
- Tests: pure rules (`WebhookTests`, `NotificationTests`, `AuditFormatTests` in `tests/Dam.UnitTests`: signatures including tamper, wrong
  secret and the clock window, backoff and jitter, event patterns and the derived events, SSRF classification, secrets at rest, rules and
  recipient planning, preference defaults, email rendering and escaping, chat payloads, CEF/syslog/CSV escaping) and, against real
  Postgres and RabbitMQ, `WebhookApiTests`, `WebhookDeliveryTests`, `NotificationDeliveryTests`, `NotificationChannelTests`, `AuditTests`
  and `SiemTests` (end-to-end delivery within 5 s with a valid signature, retries and backoff on a fake clock to `failed`, replay, ping,
  disabled, tenant isolation and RLS, notifications for the right people only, preferences, dedupe on redelivery, mail via a fake sender
  and a real SMTP conversation, chat posts, audit filters, paging, CSV and CEF export, tamper detection, SIEM delivery and resume).

## Console
- **Notifications**: a bell with an unread count (polled every 30 s) opens an accessible panel in the top bar (a full-width sheet on phones);
  `/notifications` has Inbox (All/Unread) and Preferences tabs. Preferences come from the server's category catalogue, with In-app and Email
  switches; email switches are disabled with an explanation when SMTP is not configured. `linkUrl` is only followed when it is an app-relative path.
- **Webhooks and chat channels** (`/admin/webhooks`, needs `webhooks.manage`; the Chat channels tab needs `notifications.manage`): webhook
  cards (active switch, 24 h stats, event chips), an event picker with wildcard patterns, a one-time secret reveal with signature-verification
  snippets, delivery log (status, "retry in ..." countdown, filters kept in the URL, drawer with payload/response, replay), Slack/Teams channels
  with test sends. Known limits: the API exposes only the latest attempt (no per-attempt timeline), a channel's last test result lives in the
  browser only, and the ui-kit toast region sits below modal dialogs (outcomes are also shown inline inside drawers).
- **Audit log** (`/admin/audit`, needs `audit.read`): URL-backed filters, keyset-paged results, a detail drawer with a before/after diff, an
  on-demand "Verify integrity" run (never automatic - it is O(n)), and authenticated streamed CSV / CEF export whose files begin with the
  verification summary. The entity-type filter is free text and the actor filter takes an id (no list endpoints for either).
- Hindi strings for `notifications`, `webhooks` and `audit` are not translated yet and fall back to English.
