# ADR-0025: File information on the asset detail page

## Status
Accepted

## Context
The asset detail page showed a preview and an editable form but nothing about the file itself - no size, dimensions,
dates or type - and the left column under the preview was empty. Reference DAMs (the Adobe screens used for ADR-0022)
all surface this.

## Decision
**No backend change.** Everything needed is already returned by `GET /assets/{id}/versions`: each version carries
`sizeBytes`, `mime`, `sha256` and a `technicalMetadata` map the ingest pipeline fills in (ADR-0005): `file`
(original name), `scan` (verdict/engine), and one of `image` (pixels, alpha, EXIF), `media` (ffprobe: duration,
container, bit rate, per-stream codec/resolution/frame rate/channels/sample rate), `pdf` or `document` (pages, size,
author, application). The asset itself supplies created/modified dates. The versions query is shared with the Versions
tab, so the panel costs no extra request.

**`buildFileFacts` (pure, unit-tested) turns that into grouped label/value facts**; a fact with no value is omitted, so
a PDF shows pages and page size, a video shows duration/resolution/frame rate/codec plus an Audio group, and an
asset with no extracted metadata shows only the general facts. The card sits under the preview; a one-line summary
("JPEG - 1920 x 1080 - 406.3 KB") sits under the title. Labels are i18n keys (en/hi); formatters (`lib/format.ts`)
are shared with the Versions tab.

## Deliberately not included
- **Owner, folder path, rights, capture date.** Owner is only an id in `AssetDto` (no display name endpoint wired
  into the console), rights has no backend (ADR-0016), and EXIF capture date/camera are shown only if the extractor
  already stored them (today only colour space and orientation are kept, so the "Camera & capture" group appears
  once the extractor records more).
- **Editing any of these.** They describe the stored file and are read-only by nature.

## Consequences
- Richer facts need only extractor changes, not UI changes: any extra string EXIF tag lands in the Camera group.
- The summary and card render nothing for an asset with no file yet.
