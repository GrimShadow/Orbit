# ADR-0005: Ingest pipeline

Status: accepted (Step 1.4)

## Context
After a file is uploaded and hashed (ADR-0004) it sits in a quarantine bucket. Before it can become an asset version it must be
scanned for malware, identified by its real content, described technically, and stored durably. Customers differ in file sizes and
scanner policy, and the whole pipeline must run on a laptop.

## Decision
- **A chain of single-purpose consumers**, each triggered by the previous stage's outbox event:
  `upload.completed` → hash → `ingest.requested` → scan → `ingest.scanned` → sniff → `ingest.sniffed` → extract →
  `ingest.extracted` → promote → `asset.version.added` → perceptual hash. Each stage first checks the stage recorded on the upload
  (`Upload.IsAt`), so redelivery is harmless, and each stage can be retried, parked or scaled on its own.
- **Fail closed.** If the scanner cannot be reached, the stage errors, is retried, and then the upload is marked failed and visible.
  A file is never promoted unscanned because ClamAV was down. Only an explicit policy lets oversize files skip scanning
  (`DAM_SCAN_OVERSIZE=allow|block`, default allow, with the skip recorded in the version's metadata); `DAM_SCAN_MODE=off` exists for
  development and is recorded the same way.
- **Infected files** are copied to `infected/<staging key>` in the quarantine bucket (evidence), removed from staging, the upload is
  `blocked` and the asset gets status `blocked`. A blocked asset accepts no further uploads.
- **Content over names.** Our own magic-number sniffer decides the real type. Executables are refused whatever the file is called;
  the declared asset type must agree with the sniffed kind. The stored MIME is the sniffed one.
- **Best-effort technical metadata.** Image size and EXIF/IPTC (GPS is recorded only as present/absent), PDF page count and info,
  Office document properties, and audio/video via `ffprobe` (read through a presigned URL, so the worker never downloads the whole
  file). An unreadable file produces a note in `extraction.notes`, not a failure.
- **Promotion** copies the staged object to the originals bucket under a key derived from the tenant, asset and a version id that is
  itself derived from the upload id, so a retry lands on the same row and object. Files above `DAM_COPY_MULTIPART_THRESHOLD_MB`
  are copied with multipart copy so a 50 GB video does not hit the single-copy limit. The size is verified before the staged copy
  is deleted.
- **Near-duplicates** use a 64-bit perceptual hash (32×32 greyscale DCT). `GET /assets/{id}/similar?by=phash&maxDistance=8`
  compares in memory (capped at 200k hashes) and only returns assets the caller may read. A proper index is a later optimisation.

## Consequences
- ClamAV is an optional compose profile (`make up-scan`), with an arm64-capable image; ffmpeg is installed only in the worker image.
- Renditions (Step 1.5) hang off `asset.version.added`; nothing here needs to change for them.
- Adding a stage means adding a consumer and an event, not editing the others.
