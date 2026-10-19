# ADR-0026: Upload screen redesign - per-file type and title, per-type metadata

## Status
Accepted

## Context
The upload screen (ADR-0015) was a bare drop zone, a file list and two fields (folder, tags). ADR-0015 deferred a real
batch metadata form because `POST /assets/bulk-upload` guessed each file's type from its extension and validated one
shared metadata document against every inferred type, so there was no single schema to render a form against. It also
had no way to say "this .zip is an HTML5 bundle" (a .zip is always "other"), "this PDF is a manual", or to give an
asset a title other than its file name, and a file the server would refuse was only discovered after the upload.

## Decisions
**Three small, backward-compatible additions to the bulk-upload request** (all optional; an older client is unaffected):
- `files[].assetType` - create this file as that type. Validated like any upload (`UploadPolicy.IsKnownType` /
  `IsAllowed`): an unknown type, or one the extension does not fit (a .png as video), is rejected for that file only.
  Omitted, the type is still inferred from the extension.
- `files[].title` - the asset's title; omitted, the file name without its extension (unchanged).
- `metadataByType` - a metadata document per asset type, replacing the shared `metadata` for the types it lists. A form
  can only offer the fields of one type's schema, so a mixed batch needs one document per type; each is still validated
  per type, per file, with the failures reported per file as before.

**The page offers what those additions make possible.**
- A queue where every file has an editable title, a type chosen from the types its extension fits (shown as a select only
  when there is a real choice - a PDF is a document or a manual, a .zip is a manual, 3D package or HTML5 bundle), an
  image preview, and its size.
- Problems the server would refuse (blocked extension, no extension, empty, over the per-type size limit) are shown on the
  file before upload and the file is left out; `uploadQueue.ts` mirrors `UploadPolicy`, and the server still re-checks.
  A file already in the list is skipped, and a batch is capped at the server's 500.
- A Destination card (folder - remembered for next time in `localStorage`, best-effort - and tags) and a "Details for the
  whole batch" card with one collapsed, schema-generated section per type present in the batch, reusing the asset page's
  field renderer (`MetadataFields`, extracted from `MetadataForm`). Every field is optional at upload: a draft may stay
  incomplete and required fields are enforced when an asset is submitted for review. Untouched fields are not sent.
- A sticky action bar (count, total size, skipped count, Upload), live overall progress, and per-file results that link
  to the new asset - or, for a duplicate, to the asset that already holds the same bytes. "Review failed files" returns
  just those files to the form to fix and retry.

## Deliberately not included
- **Rights/usage terms and an owner picker** - no backend for rights (ADR-0016) and the creator is the current user.
- **Submit for review on upload** - submission enforces required metadata per asset; that belongs after the drafts exist.
- **Resumable re-attach after a page reload** - the server supports resumable uploads, the console does not yet persist
  the queue; a reload loses the in-browser queue (files already sent continue server-side).

## Consequences
- `docs/api/openapi.json` and `@dam/sdk-js` regenerated; three integration assertions cover per-file type, title and
  per-type metadata (`UploadTests`); the page has unit tests for queue rules and each flow, and the e2e suite's upload
  steps were updated to the new button.

## Addendum: destination folder up front
The folder choice already existed but only appeared after files were queued, so it was easy to miss. The Destination card (folder
and batch tags) now shows beside the drop zone from the start, with the folder's full path as a hint; the sticky action bar
says "to <folder>" and the results link to that folder. `/upload?folder=<id>` preselects a folder (it wins over the remembered
one); the library's Upload buttons pass the open folder and a folder's context menu has "Upload files here". No API change.
