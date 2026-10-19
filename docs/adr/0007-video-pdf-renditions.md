# ADR-0007: Video, PDF and signage renditions

Status: accepted (Step 1.5b)

## Context
ADR-0006 covers image renditions and the signed-link delivery origin. Spec A6 also requires video transcodes, PDF page images and
exact-fit signage profiles. This step adds those, reusing the same rendition cache, link and watermark model.

## Decision
- **A version's kind decides its profiles.** `RenditionProfiles.KindOf(mime)` maps a version to `Image`, `Video` or `Pdf` (anything
  else, including SVG, gets none). Each kind eagerly generates its own set on `asset.version.added`
  (`RenditionService.GenerateEagerAsync`), and the delivery origin renders any of them on demand if the eager pass was skipped or
  missed one, exactly as images already worked.
- **Video** (FFmpeg, spec D8): a poster frame is extracted once and resized into `thumb` (240px) and `poster` (1024px) stills, reusing
  the existing image pipeline. Four transcodes follow: `preview` (720p, capped at 8s, spec A6), `mobile` (720p, full length),
  `signage_1080` (1920×1080) and `signage_4k` (3840×2160). H.264 is used throughout rather than mixing in H.265 for the 4K profile —
  one codec is more portable and predictable across players than following the spec's "H.264/H.265" either-or literally. Output
  dimensions are computed from the source's own decoded frame, not by asking ffprobe to re-inspect the encode, so a broken or absent
  probe never corrupts a rendition's recorded size; the encode's duration is still read via ffprobe, but only as a best-effort detail
  on the rendition row.
- **PDF** (Poppler, spec D8): page 1 is rasterised once for `thumb` and `preview` (reusing the image shapes again), and every page up
  to `DAM_PDF_MAX_PAGES` (default 200) is rendered at 1600px as `pdf_pages`, one rendition row per page (`page_no` column, added to
  `renditions` alongside a `duration` column for video). A page is addressed in links and delivery URLs as `pdf_pages-{n}`.
- **Signage exact-fit is letterboxed, not cropped.** Spec A6 allows either; letterboxing never discards part of the frame, which
  matters more for compliance and brand content than filling the screen. Crop-to-fill can be added as a second profile if a customer
  needs it.
- **A video is never transcoded out clean for an unpublished asset.** Watermarking a still (thumb, poster) is done the same way as an
  image; watermarking a moving picture is not implemented here, so `POST /assets/{id}/links` refuses (409) a video-rendition request
  while the asset is unpublished, rather than silently serving something unwatermarked or silently failing at fetch time. This is a
  known gap, not a design choice to leave permanently: revisit if unpublished video preview becomes a real requirement.
- **Range requests.** The delivery origin now honours a single `Range: bytes=` request (the form every browser and video player
  sends) with `206 Partial Content` / `416`, and always advertises `Accept-Ranges: bytes`, so seeking in a video works without
  downloading the whole file first.
- **Office documents (DOCX/PPTX/XLSX) are not converted in this step.** LibreOffice is a large, separate dependency; adding it here
  would double this step's size and its own testing concerns (headless conversion is fiddlier to get reliable than FFmpeg or
  Poppler). It is Step 1.5c.

## Consequences
- Both the worker and delivery images now install `ffmpeg` and `poppler-utils`; the API and scheduler images stay lean.
- `GET /assets/{id}/renditions` reports `duration` and `pageNo` where they apply.
- For an unpublished asset the console can show only a PDF's page 1 (`preview`, watermarked) and a video's `poster`:
  `pdf_pages-{n}` 404s and video transcodes 409 until the asset is published, so page navigation and playback appear
  on publish (ADR-0028).
- Adding a video or PDF profile is the same shape of change as adding an image one: extend the catalog in `RenditionProfiles`, no
  changes to the link, cache or delivery-serving logic.
