# ADR-0008: Office document renditions

Status: accepted (Step 1.5c)

## Context
ADR-0006 and ADR-0007 cover image, video and PDF renditions. Spec A6 and Appendix B also call for DOCX/PPTX/XLSX renditions
("Office → PDF via LibreOffice"), deferred out of Step 1.5b because LibreOffice is a large, separate dependency with its own
correctness quirks, and combining it with video/PDF would have doubled that step's size and testing surface.

## Decision
- **Content decides the format, not the file name.** DOCX, PPTX and XLSX are all ZIP containers with the same magic bytes as any
  other zip; the sniffer (`ContentSniffer`) now looks inside the ZIP directory for the one entry each format is guaranteed to have
  at a fixed path — `word/document.xml`, `ppt/presentation.xml`, `xl/workbook.xml` — the same technique `file`/libmagic use, and
  reliable because ZIP entry names are stored uncompressed regardless of the entry's own content compression. A file that doesn't
  carry one of these markers still sniffs as a plain zip, exactly as before; nothing here narrows what a generic archive upload
  accepts.
- **Office documents are rendered by becoming a PDF first**, via LibreOffice headless (`soffice --convert-to pdf`), then reusing
  the *entire* existing PDF pipeline unchanged: page-1 stills (`thumb`, `preview`) and every page as `pdf_pages-{n}`. `SourceKind`
  gained an `Office` case that behaves identically to `Pdf` everywhere except the one extra conversion step
  (`RenditionService.AsPdfAsync`); the delivery origin, link validation, caching and watermarking needed no new code.
- **Every LibreOffice call gets its own throwaway profile directory** (`-env:UserInstallation=file://...`). LibreOffice refuses a
  second instance sharing a profile and fails *silently* (exit 0, no output file) when that happens — confirmed by hand before
  writing `OfficeConverter`, since a silent failure here would have been a delivery bug that only showed up under concurrent load.
  A semaphore (`DAM_OFFICE_MAX_CONCURRENCY`, default 2) also caps how many conversions run at once: each is a full LibreOffice
  process, heavier than an ffmpeg or pdftoppm invocation.
- **Only the three components that read these formats are installed** (`libreoffice-writer`, `libreoffice-impress`,
  `libreoffice-calc`), not the full suite — no Draw, Base, Math, or GUI/help/language packages. Still the largest dependency in
  the project (~1 GB installed) by a wide margin; only the worker and delivery images carry it.

## Consequences
- A corrupt or unrecognisable Office file simply gets no renditions (logged, not fatal), the same fail-soft behaviour video and
  PDF already have.
- Adding a fourth Office format (or a legacy binary one) is a sniffer entry plus a mime constant; the rendition path needs no
  change, since it already only cares that the version is `SourceKind.Office`.
