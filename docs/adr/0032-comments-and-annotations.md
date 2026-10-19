# ADR-0032: Comments, annotations and collaboration

## Status
Accepted (Step 2.4, backend)

## Context
Review used to happen outside the library: a comment in a chat, a screenshot with arrows in an email. Step 2.4 puts the conversation on the
asset itself: threaded comments written against the version being looked at, annotations drawn on an image or page or placed on a video's
timeline, @mentions that tell the person named, resolve and reopen, and a feed of what happened to the asset. It is built on the pieces
earlier steps left for it: the command pipeline and outbox (D15), the policy engine's per-asset read check, the notification service of
ADR-0031 (categories, preferences, email, de-duplication) and the webhook catalogue. All names in code and tests are generic.

## Model
`comments(id, tenant_id, asset_id, version_id?, parent_id?, author_id, body, anchor jsonb?, anchor_type?, resolved, resolved_by?,
resolved_at?, edited_at?, deleted_at?, created_at)` and `comment_mentions(comment_id, user_id, tenant_id)`.

- **A comment belongs to a version.** A new comment is attached to the asset's current version (or to `versionId`, which must belong to the
  asset). An asset with no file yet takes comments with a null version. Comments are never moved when a newer version arrives: they are
  about what the author was looking at.
- **Threads are one level deep.** A thread is a *first comment* (`parent_id` null) and its replies. A reply may only target a first comment
  (replying to a reply is 422 on `parentId`), carries no anchor, and takes its thread's version. Resolving belongs to the thread, so it is
  stored on the first comment; a reply shows its thread's `resolved`, `resolvedBy` and `resolvedAt` in the DTO but keeps its own text.
  `PATCH` with `resolved` on a reply is 422 on `resolved`.
- **Editing** is for the author only and sets `edited_at`; saving the same text is not an edit. **Deleting** is for the author or anyone who
  can `assets.update` the asset. It is soft, but the text is erased from the row at once (and the mention rows removed), so nothing a person
  chose to remove stays in the database. A deleted first comment that still has live replies stays in the list as a placeholder (`deleted:
  true`, body `[deleted]`, no anchor, no mentions) so the thread keeps its shape and cannot be replied to; when its last reply goes the
  placeholder disappears. Replies deleted individually are simply gone. A second delete or an edit of a deleted comment is 404.
- **Writes on a deleted or blocked asset are 409**, as for other asset operations; reading its comments still works for whoever can read it.

## Anchors
`anchor` is validated (`CommentAnchor.Validate`, a pure function) and stored as jsonb with only the fields that apply; `anchor_type` is a
separate indexed column (`region`, `point`, `timecode`; a check constraint keeps it honest). Only a first comment may carry one.

| type | fields | rules |
|---|---|---|
| `region` | `x`, `y`, `w`, `h`, optional `page` | all four required, `x`,`y` in [0,1], `w`,`h` in (0,1], `x+w <= 1` and `y+h <= 1` (1e-9 slack for floating point), `page` 1 to 10,000 |
| `point` | `x`, `y`, optional `page` | `x`,`y` in [0,1]; no size |
| `timecode` | `startMs`, optional `endMs` | `startMs` 0 to 24 h in ms, `endMs` after `startMs` and at most 24 h; null `endMs` is a single moment |

Coordinates are fractions of the rendered width and height, so they mean the same on any rendition and zoom (the Step 2.4 accept
criterion). Fields that do not belong to the type are refused (a timecode with an `x` is 422 on `anchor.x`). By asset type: video and audio
accept only `timecode`; every other type refuses `timecode` and accepts `region`/`point` (`page` is for documents and manuals). A mismatch
is 422 on `anchor.type` and the message says what to use. Errors are keyed like the request: `body`, `parentId`, `versionId`, `anchor`,
`anchor.type`, `anchor.x`, `anchor.w`, `anchor.startMs` ...

The DTO's `AnchorDto { type, x, y, w, h, page, startMs, endMs }` has the unused fields null. Numbers round-trip exactly (jsonb keeps the
decimal; tests cover 0.1+0.2 and a third).

## Text and mentions
Plain text, trimmed, 1 to 4,000 characters, no NUL, at most 20 distinct mentions. A person is named by the token
`@[Display Name](user:<guid>)` so a renamed person still resolves. On the way in the server parses the tokens and keeps a mention only for
**active people in the tenant who can read this asset**, decided by the same function the notification planner uses for "who could see
this": `UserProfiles` builds the person's access profile from what is stored (roles, groups, IdP roles, access rules, attributes) and the
policy engine checks `assets.read` against the asset. This follows access rules and attributes, not just roles: a Dealer for another region
is not offered, mentioned or told. What is stored:

- a token for an eligible person is **rewritten with their current display name**, so a label can never claim to be somebody else;
- any other token (unknown id, disabled, no access, another tenant) becomes plain `@Name`. It notifies nobody, and the response is the same
  for "does not exist" and "cannot see it", so a mention cannot be used to probe who has access;
- duplicates collapse to one entry in `comment_mentions` (the text keeps each occurrence);
- the DTO returns `mentions: [{ userId, displayName }]` with current names, and `body` as stored, tokens included, so the console renders
  chips from the tokens. Plain text is the only format; there is no HTML, so there is nothing to sanitise on the server. Clients must
  escape on render like any user text.

## Permissions
No new permission and no built-in role changed.

| Action | Needs |
|---|---|
| read comments, activity | `assets.read` on the asset (not visible: 404) |
| add, edit, resolve, reopen | `comments.write` (Admin, Librarian, Editor, Approver, Contributor hold it; Viewer, Dealer, ApiClient do not) and `assets.read` on the asset; edit also needs authorship |
| delete | `assets.read`, and either authorship or `assets.update` on the asset |
| mention picker | `comments.write` and `assets.read` |

Order of checks: unknown or unreadable asset or comment is 404 before anything else, so existence is not revealed; then 403; then 409
(deleted asset). The policy-matrix tests already enumerate `comments.write` per role; the integration tests also assert which roles can
comment.

## Events
Raised by the `Comment` aggregate through the outbox and audit log in the command's transaction:

| Event | When | Payload (camelCase on the wire) | Public |
|---|---|---|---|
| `comment.added` | a first comment or a reply | `commentId, assetId, versionId, parentId, authorId, anchorType, mentions[], resolved:false` | yes |
| `comment.resolved` | a thread goes from open to resolved | `commentId, assetId, versionId, authorId, resolvedBy` | yes |
| `comment.reopened` | resolved to open | `commentId, assetId, versionId, authorId, reopenedBy` | yes |
| `comment.edited` | the text changed | `commentId, assetId, authorId, addedMentions[]` | no: audit and notifications |
| `comment.deleted` | a comment was deleted | `commentId, assetId, deletedBy` | no: audit |

Like every event they also carry `entityType: "comment"` and `entityId`. **No event carries the text** (nor does the audit log), so a webhook
receiver, a chat channel or an auditor sees that a comment was made, by whom, where and who it mentions, but not what it said. The three
public events are in `EventCatalogue` with descriptions (webhook subscriptions such as `comment.*` work) and chat channels describe them
("New comment", "Comment thread resolved", "Comment thread reopened"). Repeating a resolve or a reopen changes nothing and raises nothing.

## Notifications
Two categories join the fixed catalogue (the four existing ones are untouched, and appear first):

| Key | Label | Console | Email |
|---|---|---|---|
| `mention` | Mentions | on | on |
| `comments` | Comments and replies | on | off |

Rules, planned by `NotificationPlanner` (a comment event can now produce more than one plan):

| Event | Who is told | Category | Title |
|---|---|---|---|
| `comment.added` | everyone it mentions | `mention` | "Priya mentioned you on 'Acme brochure'" |
| `comment.added` as a reply | the author of the thread (the first comment) | `comments` | "Priya replied to your comment on '...'" |
| `comment.added` as a new thread | the asset's owner and whoever submitted it to a workflow | `comments` | "Priya commented on '...'" / "added an annotation to '...'" |
| `comment.edited` | people the edit newly mentions | `mention` | as for a mention |
| `comment.resolved` | the thread's author, if someone else resolved it | `comments` | "Priya resolved your comment on '...'" |

The body is a short excerpt (140 characters, one line, tokens shown as `@Name`). `linkUrl` is `/library/assets/{assetId}?comment={commentId}`,
which the console opens on the thread; email buttons carry the absolute form (`DAM_CONSOLE_PUBLIC_URL`) and the same HTML/text layout as
every notification. Reopening notifies nobody.

**One notification per person per comment.** The author is never told. A person who is both mentioned and concerned (the parent's author,
the owner) gets only the mention, the stronger reason (`CommentRecipients.Split`, a pure function). Everyone is checked again at delivery
time for being active and able to read the asset, with the same `AssetReaders` the mention parser uses, so a person whose access was
removed between writing and delivery is not told. The mentions are read from `comment_mentions` and the text from the comment, so nothing
sensitive rides in the event. De-duplication is the existing ledger, unique per (event id, channel, recipient): a redelivered event, even
under a new message id, notifies and mails nobody twice. Preferences are honoured per channel as for every category; the console's
notification settings screen lists the new categories because it renders the server's catalogue. `NotificationConsumer` also binds the
internal `comment.edited` event; it is not in the public catalogue, so webhooks and chat never see it.

## Activity feed
`GET /assets/{id}/activity?limit&cursor` returns `Page<ActivityItemDto>`, newest first, for anyone who can read the asset. It is **not** the
audit log (which needs `audit.read`). Items are `{ id, kind, occurredAt, actorId, actorName, summary, commentId, versionNo, detail }`:

- comments and replies come from the `comments` table (`kind` `comment`, `reply`; summary "added a comment", "added an annotation",
  "replied to a comment"; `detail` the excerpt). Deleted comments are not in the feed;
- everything else comes from this asset's audit entries, filtered to a **whitelist of actions** (`ActivitySummaries.Actions`) and mapped to
  a friendly kind and phrase: `asset.created` (`created`), `asset.version.added` (`version_added`, "uploaded version 3", with the uploader
  named from the version itself since the ingest worker is the recorded actor), `asset.status.changed` (`status_changed`, "changed the
  status to in review", detail "from draft"), `asset.metadata.updated` (`metadata_updated`), `asset.legal_hold.changed` (`legal_hold`),
  `workflow.task.completed` (`workflow_decision`, with the reviewer's comment as detail, which the workflow panel already shows to readers),
  `workflow.sla.breached` (`workflow_overdue`), `comment.resolved` / `comment.reopened` (`resolved`, `reopened`, dropped if the thread
  was since deleted). The metadata entry written in the same instant as `asset.created` is left out. Ingest steps, renditions, uploads and
  everything else never appear;
- `summary` is a phrase that follows the actor's name. Only the whitelisted fields are ever read from an audit payload and the DTO has no
  place for a payload, an IP address or a hash, so there is nothing to leak. The actor is a resolved user (`actorId`, `actorName`) or
  "System".
- Keyset paging over (time, id): each source is read for `limit + 1` rows older than the cursor, merged, and cut. The cursor is opaque
  (`<ticks>.<id>`); a malformed one is 422.

`GET /assets/{id}/mentionable?q=` lists up to 10 active people who can read the asset and match `q` by name or email (case-insensitive
substring, prefix matches first), never the caller. It needs `comments.write` and asset read, and checks candidates against the asset in
pages, so it can neither list the tenant for someone without access nor offer someone who could not see what they are mentioned in.

## API (tag Comments; all under `/api/v1`)

| Method and path | Body or query | Returns | Needs |
|---|---|---|---|
| GET `/assets/{id}/comments` | `versionId`, `state` open/resolved/all, `kind` annotation/comment/all | `CommentThreadsDto { threads[], counts }` | `assets.read` |
| POST `/assets/{id}/comments` | `AddCommentInput { body, versionId?, parentId?, anchor? }` | 201 `CommentDto` | `comments.write` + read |
| PATCH `/comments/{id}` | `UpdateCommentInput { body?, resolved? }` (null leaves a field) | `CommentDto` | `comments.write` + read; body: author |
| DELETE `/comments/{id}` | | 204 | read + (author or `assets.update`) |
| GET `/assets/{id}/activity` | `limit` (up to 200), `cursor` | `Page<ActivityItemDto>` | `assets.read` |
| GET `/assets/{id}/mentionable` | `q` | `MentionableUserDto[] { id, displayName, email }` | `comments.write` + read |

`CommentThreadsDto.threads` are in reading order: **oldest thread first**, each `CommentThreadDto { root, replies[] }` with replies oldest
first. `counts { open, resolved, annotations }` are threads (a deleted placeholder with replies still counts), for the version in `versionId`
or all versions, **regardless of the `state` and `kind` filters**, so tabs can show totals while a filter is applied. `CommentDto { id,
assetId, versionId, versionNo, parentId, authorId, authorName, body, mentions[], anchor, resolved, resolvedBy, resolvedByName, resolvedAt,
edited, editedAt, deleted, createdAt }`. `PATCH` with neither field is 422 on `body`. Operation ids: `ListComments`, `AddComment`,
`UpdateComment`, `DeleteComment`, `ListAssetActivity`, `ListMentionableUsers`.

## Persistence
Migration `CommentsAndAnnotations`: both tables with forced tenant RLS and the usual policy; grants in `SecuritySql.CommentGrants`
(`comments`: select, insert, update, never delete; `comment_mentions`: select, insert, delete, because an edit replaces mentions). No
`dam_system` grant: nothing cross-tenant reads comments. Foreign keys: asset cascades (a hard-deleted asset takes its comments with it;
the app only ever soft-deletes, retention purge removes file bytes and sets status), version is `SET NULL`, a reply's parent cascades,
mentions cascade from the comment, so no orphans are possible. Indexes: (tenant, asset, created_at) for the list, (tenant, asset, resolved),
(tenant, asset, anchor_type) where set, parent, and (tenant, user) on mentions. `IAssetActivityReader` (`EfAssetActivityReader`) reads the
asset's audit entries through the ordinary tenant-scoped context, so the feed is under RLS like everything else; no raw SQL.

## Deliberately not included
- Reactions, attachments in comments, rich text, comment drafts, and per-thread watchers or "follow this asset" (only the people named,
  the thread's author, the owner and the submitter are told).
- Notifications to other participants of a thread beyond its author, digests, and quiet hours (ADR-0031 lists the same limit).
- Moving a thread to another version or re-anchoring it after the file changes. A comment on version 2 stays on version 2; comparing
  versions is the reader's job.
- Region or point annotations on a video frame, or a timecode on an audio waveform region (a timecode range is the supported form).
- Reporting abuse, moderation queues, comment retention separate from the asset. Comments follow the asset's own lifecycle.
- A bulk comment-count endpoint for the library grid (`GET /assets/comment-counts`): not needed yet; counts are per asset.
- Notifying people newly mentioned by an edit that happens *after* they lost access: they are filtered like everyone else.

## Consequences
- `docs/api/openapi.json` and `@dam/sdk-js` regenerated. The generator types a nullable object reference without `| null` (as for
  `AssetWorkflowDto.instance`): `anchor` is null at runtime for a comment without one.
- The spec listed `GET/POST /assets/{id}/comments` and `PATCH /comments/{id}`; `DELETE /comments/{id}`, the activity feed and the mention
  picker are additions. `comment.added` is as in the spec's event list; `comment.resolved` and `comment.reopened` are new public events.
- `NotificationPlanner.PlanAsync` now returns a list (a comment can produce a mention plan and a comments plan). `NotificationConsumer`
  listens to one internal event more. `Dam.Infrastructure` registers `IAssetActivityReader`; the worker registers `AssetReaders`.
- Tests: pure rules in `tests/Dam.UnitTests/CommentTests.cs` (every anchor bound and type rule, token parsing and normalising, excerpts,
  feed mapping and cursors, notification text, recipient rules, the aggregate and its events, catalogue entries) and, against real
  Postgres and RabbitMQ, `tests/Dam.IntegrationTests/CommentTests.cs` (create, versions, threads, filters, counts, replies, anchors incl.
  exact round trips, edit, resolve, delete and placeholders, scope by role and by attribute, deleted assets, mentions, the picker, the
  feed and its paging, tenant isolation and RLS, grants, cascade) and `CommentNotificationTests.cs` (in-app and email for a mention,
  preferences per channel, replies, owner and submitter, mention wins, author never told, resolution, edit mentions, redelivery,
  a signed webhook delivery of `comment.added` and `comment.resolved` without the text).

## Console (for the frontend step)
- Open a thread from `?comment={id}`; render `body` by splitting on the token pattern and showing `@Name` chips from `mentions`.
- Draw regions as fractions (`x`, `y`, `w`, `h` of the displayed image) and send them as is; a PDF viewer adds `page`; a video player sends
  `startMs` (and `endMs` for a range) in milliseconds.
- The `@` picker calls `GET /assets/{id}/mentionable?q=` and inserts `@[Display Name](user:id)`; the server relabels and filters.
- A comment's `versionNo` tells which version a pin belongs to; filter with `versionId` to show only the version on screen.

## Console
- **Comments tab** (asset page, with a `?tab=` and `?comment=` deep link used by notifications): threads oldest first with replies, resolve/reopen, edit
  (author) and delete (author or anyone who can update the asset), filters for state, kind and version kept in the URL, and a composer with `@mention`
  autocomplete. The composer keeps plain `@Name` text plus a list of mention ranges and converts to the stored `@[Name](user:<id>)` tokens on send, so
  only highlighted names notify anyone. The tab label carries the open-thread count for the current version.
- **Activity tab**: the curated merged stream from `GET /assets/{id}/activity` with load more.
- **Annotations on the stage** (`features/comments/annotations`): an Annotate mode draws a rectangle (or a pin) with pointer events (mouse, touch,
  pen), normalized against the image's live bounding rect so zoom, scroll and resize keep it aligned; documents record the page; existing
  annotations are numbered buttons synced both ways with the thread cards (hover, select, deep link). Video gets "Comment at <time>" (shortcut `m`,
  not `c`, which the shell binds to Collections), an optional end time, a timeline of markers (ranges as bars, click to seek) and a caption chip
  during playback. Unpublished videos show a disabled capture button (no playable video). Audio has no player stage today, so no audio annotations yet.
- Shared state between the stage and the panel lives in `CommentsContext` (selection, hover, draft anchor, seek, show-page, focus-composer).
- Not built: keyboard placement of a region (drawing is pointer-only; selecting and commenting are keyboard accessible), per-tab unsent drafts,
  Hindi strings (fall back to English).
