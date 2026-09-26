# ADR-0004: Assets and resumable direct-to-storage uploads
Status: accepted (2026-09-26), implements Step 1.3 (spec A3.3: no file bytes through the API)

## Decision
**File bytes never pass through the API.** The API creates an S3 multipart upload and hands the browser short-lived
presigned URLs (at most 15 minutes) for individual parts. The browser PUTs parts straight to object storage.
Nothing is ever hashed, copied or inspected by the API process.

### Flow
1. `POST /assets` creates a **shell** (draft, no content). Metadata is validated against the type's schema, folder defaults
   are applied first, and term codes and access attributes are derived from it. Required fields can be deferred
   (`allowIncomplete`) for "upload now, describe later"; wrong values are always refused.
2. `POST /assets/{id}/uploads` opens a multipart upload in the **quarantine** bucket at a key built from ids only
   (`staging/{tenant}/{asset}/{upload}`), never from the file name, and returns the first part URLs.
3. The client sends parts, asking `POST /uploads/{id}/parts` for fresh URLs as it goes (URLs expire).
4. **Resume:** `GET /uploads/{id}` asks storage which parts it holds. After a dropped connection the client sends only
   the missing ones. A part that was re-sent may be listed twice by some stores; the newest write wins.
5. `POST /uploads/{id}/complete` verifies every part is present and correctly sized, optionally checks client-supplied
   ETags, assembles the object and returns **202**. Hashing 20 GB takes time, so it is not done in the request.
6. The **worker** streams the object and computes SHA-256 (a client-supplied hash is only a hint, never trusted),
   then either marks the upload `ready` and publishes `ingest.requested`, or `duplicate` and discards the staged copy.
   Identical files finishing together are serialised by an advisory lock on (tenant, hash).

### Duplicates
Same content already in the tenant, ignoring file name, is a duplicate unless `allowDuplicate`. A client that already
knows the hash can pass it to be refused immediately (409 with `duplicateOfAssetId`), before or at completion. A shell
created only for a duplicate file is soft-deleted so it does not clutter the library.

### Limits and safety
- Extension allow-list per asset type, executables blocked outright, per-type maximum size (never above 20 GB), file
  names stripped of paths and control characters. This is the first gate only; Step 1.4 sniffs real content and scans it.
- One open upload per asset. Sessions expire after 24 h; the bucket has a lifecycle rule that aborts stale multipart
  uploads, so abandoned transfers do not accumulate.
- Presigned URLs are bound to the part number and object key (tampering returns 403). The URL scheme follows the
  configured storage endpoint; the SDK's HTTPS default would break plain-HTTP local and on-prem installs.
- Browser uploads need CORS on the bucket with `ETag` exposed (set up in `deploy/docker-compose.yml` for dev; production
  origins come from deployment config).

### Assets are governed like everything else
Reading an asset you may not see is a **404**, not a 403, so its existence is not revealed. Creating, updating,
moving and deleting are checked against the asset's folder and attributes (ADR-0002). Delete is soft (status `deleted`);
folders and terms that assets use cannot be deleted (terms are deprecated instead).

## Deferred
Step 1.4 promotes staged files to versions in the originals bucket after scanning; 1.6 arms expiry of stale sessions and
lifecycle transitions; 1.7 replaces the interim in-memory asset listing with search. The bulk batch endpoint accepts up
to 500 files per request (send several with the same `batchId` for 10,000).
