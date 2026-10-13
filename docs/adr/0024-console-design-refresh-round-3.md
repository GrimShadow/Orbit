# ADR-0024: Console design refresh, round 3 - full-width layout, card actions, asset detail

## Status
Accepted

## Context
After round 2 (ADR-0022) the user reported the console looked "squashed ... like there is a margin". Two causes were
confirmed in the running app, plus a third found while checking:
- `.dam-main` was capped at `max-width: 1200px` and centred, and `.dam-page` at `720px` - sensible for a form, wrong
  for a media library that should use the whole screen the way the Adobe DAM reference screens do.
- `body` never reset the browser's default 8px margin, so the entire app (header included) was inset in a white frame.
- The library listed assets oldest-first (ascending UUIDv7), so a just-uploaded asset sat on the last page.

## Decisions
**Fluid by default, narrow by exception.** `.dam-main` is now full width with fluid inline padding
(`clamp(16px, 2.5vw, 48px)`), and `.dam-page` has no cap. Text/form pages opt into a readable measure with
`.dam-page--narrow` (720px: account, home, error states) or `.dam-page--medium` (960px: upload, collections list).
Library, search, collection detail and asset detail are fluid. Thumbnail tiles grew from 160px to 200px minimum, and
the library/search sidebars from 220px to 260px (the library one is sticky on wide screens, static on mobile).

**Asset detail is two columns on wide screens.** Preview in the main column (sticky, sized to the viewport) with the
lifecycle actions and the metadata/versions tabs beside it; a single column below 1024px.

**A per-card "..." menu (`CardMenu`, ui-kit)**, the gap ADR-0022 deferred. It follows the ARIA menu-button pattern
(arrow keys, Home/End, Escape returns focus, outside click closes) and is hover-revealed on pointer devices but always
visible on touch (`@media (hover: none)`). The console's `useCardActions` fills it with "Open details" plus whichever
lifecycle transitions are valid for that asset's status - the same table the detail page and bulk bar use
(`asset/lifecycle.ts`), run through the existing `useBulkTransition` for a single id. The server still re-validates
every transition; the menu only decides what to offer.

**Newest first.** `GET /assets` now orders by id descending (cursor comparison flipped to match). UUIDv7 ids are
time-ordered, so this is newest-created-first with no schema change. It is the default for the interim listing; the
search API's own sort is untouched.

**Small fixes found on the way:** `library.status.deleted` was missing from both locale files (a raw key was shown on
the badge); plain unclassed links now take the brand colour instead of browser blue.

## Consequences
- No API contract change (only the default ordering of `GET /assets`); `docs/api/openapi.json` is unchanged.
- File size and dimensions on cards remain deferred - that data is still not on the list/search responses.
- Pages that should stay narrow must now say so explicitly; a new page is wide unless it adds the modifier.
