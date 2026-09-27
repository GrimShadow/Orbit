# ADR-0015: Console upload - drag-drop and bulk uploader (Step 1.8b)

## Status
Accepted

## Context
Step 1.8b is the second console sub-step (1.8a built the typed API client and the library screen). The backend's
resumable, direct-to-S3 multipart upload surface has existed since Step 1.3/1.4 (`POST /assets/{id}/uploads`,
`POST /assets/bulk-upload`, `POST /uploads/{id}/parts`, `POST /uploads/{id}/complete`, `GET /uploads/{id}`) but had no
console UI at all. The spec's own screen list asks for "drag-drop + bulk uploader with batch metadata form".

## Decisions

**One `POST /assets/bulk-upload` call for the whole batch, not one `POST /assets` + one `POST /assets/{id}/uploads` per
file.** The bulk endpoint's own doc comment already says what's wanted here almost verbatim: "one draft asset and one
upload per file, all sharing the batch metadata and tags - failures are per file." It creates every asset shell and
opens every upload session in a single request, and per-file type inference (`UploadPolicy.InferType`, by extension)
and per-type metadata validation happen server-side - the console never needs to know or ask for a file's asset type up
front.

**The batch metadata form covers folder and tags only, not a schema-driven metadata form.** `BulkUploadCommand`'s
shared `metadata` is validated once per *inferred* asset type across the whole batch (a mixed batch of images and
videos validates the same metadata against both schemas independently) - there is no single schema to render a form
against before upload, since the type isn't known until each file's name is inspected server-side. Folder and tags are
the only fields that are genuinely type-agnostic. A full schema-driven metadata form belongs on the asset detail page
(Step 1.8c), once each asset's actual type is known - editing metadata post-upload, not pre-guessing it, is the more
useful default that also happens to be the only one this endpoint's shape actually supports.

**File bytes never pass through `dam-api`; the console never sees a hand-rolled multipart implementation either.**
`uploadOrchestrator.ts` slices each `File` into `partSize`-byte chunks (native `Blob.slice`, no library) and `PUT`s
each straight to its presigned URL, reading the returned `ETag` back - the same shape the spec's own "no file bytes
through dam-api" rule (ADR-0004) already establishes for every other client. The typed API client is used for
everything else (requesting more presigned URLs past the first few, completing, polling); only the S3 `PUT` itself is
a bare `fetch`, since there is no OpenAPI operation for "send bytes to an arbitrary presigned URL."

**Upload finishes when the ingest pipeline says so, not when bytes finish transferring.** `CompleteUpload` returns 202
and the transfer isn't done in any user-facing sense until `GetUpload` reports a terminal status
(`ingested`/`duplicate`/`blocked`/`failed` - Step 1.4's scan → sniff → extract → promote pipeline). The orchestrator
polls every 500ms until one of those lands, and the UI shows exactly that status per file - "uploading" while bytes
move, then whatever the pipeline decided, never a false "done" the moment the browser stops sending data.

**A drag-drop zone that is also, unconditionally, an ordinary file input - not a drag-only widget.** `Dropzone.tsx`
overlays a real (visually hidden but focusable) `<input type="file">` across the whole drop target, so choosing files
works identically with a mouse click, Enter/Space on a focused label, or a screen reader - drag-and-drop is additive,
never the only way to pick files. This stays console-local rather than becoming a new `ui-kit` component: it is used
in exactly one place today, and every ui-kit component so far exists because at least two screens needed it.

**A small, fixed-concurrency worker pool (3 files at once), not one `Promise.all` over every file.** A batch can be up
to 500 files (`BulkUploadValidator.MaxFiles`); starting every file's multipart transfer at once would open hundreds of
concurrent connections for no benefit. A shared queue with a handful of workers pulling from it keeps the browser's own
connection pool from being the bottleneck while still uploading several files in parallel - plain array `shift()`
under a fixed number of async workers, no library.

## Consequences
- `GET /folders?tree=true`'s stale-in-`schema.ts` symptom from ADR-0014 is now also fixed at the source: `make openapi`
  runs `pnpm --filter @dam/sdk-js generate` right after regenerating `docs/api/openapi.json`, so the two can no longer
  drift apart the way `schema.ts` had already silently done by the time this step started (the committed schema was
  missing every upload endpoint entirely - regenerating it, not writing new code, both fixed it and is now automatic
  going forward).
- `web/sdk-js/src/schema.ts` is now in `.prettierignore`. Reformatting it (`prettier --write`, and so `pnpm
  format:check`'s own `--write` remediation) was observed to silently *delete* whole endpoint blocks - every upload
  path vanished from the file with no error, twice, reproducibly, immediately after a clean regeneration. The root
  cause was not tracked down (some construct in this specific file's generated output that Prettier's TypeScript
  printer mishandles at this size is the leading suspect); the fix is simply to never run Prettier over a file nothing
  should be hand-editing anyway. Anyone regenerating this file should verify it (e.g. `grep -c upload schema.ts`)
  before trusting a following `pnpm format:check`/`--write` pass, in case the same issue resurfaces elsewhere.
- Asset detail (Step 1.8c) is where a freshly uploaded file's metadata actually gets edited with the full schema-driven
  form; this step only gets bytes and a title (the filename) into the library.
- Real progress is only ever known for the "uploading" phase (bytes sent / total); the pipeline itself gives no
  incremental progress once `complete` is called, so the UI shows a status label, not a progress bar, from that point on.
