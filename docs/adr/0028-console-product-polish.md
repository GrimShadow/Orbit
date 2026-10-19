# ADR-0028: Console product polish - app shell, design-system completion, page redesigns

## Status
Accepted

## Context
Rounds 1-3 (ADR-0021/0022/0024) fixed the design tokens, motion, card richness and layout caps, but the console still
read as a well-styled prototype: a top header carrying raw tenant/language `<select>`s, a placeholder Home, a bare
Login and Account page, empty states that were one line of text, a metadata form built from `<fieldset>` boxes and
raw `<select multiple>` lists (the one axe violation in the whole app - no accessible name), and no product-level
navigation model. The brief for this round was "the final polished product", with Canva and Adobe (Express / AEM
Assets) as the reference points.

A full sweep was taken first - every screen × light/dark × 1440px/390px, with axe-core and console/network errors
recorded - so the work targeted real findings, not taste alone (see Findings).

## Decisions

**A sidebar-plus-top-bar application shell replaces the single header.** Wide screens get a 248px navigation sidebar
(brand, the five sections with icons, the tenant, a collapse toggle whose state is remembered) and a top bar with a
global search box, the Upload action and the account menu; phones get the top bar plus a fixed bottom tab bar. This
is the convention of every reference product and of the user's own screenshots, and it frees the pages from carrying
navigation chrome. Tenant, language and theme moved into the account menu: they are settings, not toolbar controls,
and the three raw selects were the largest single reason the old header looked like a form.

**Global search is a first-class shell element**, not just a page: Enter takes the query to `/search?q=` (the Search
page reads it) and `/` focuses it from anywhere, listed in the shortcuts help. The page's own search input remains for
refining.

**The design system was completed before pages were touched, and pages built only on it.** The ui-kit gained the
primitives every reference UI has and this one lacked (EmptyState, PageHeader, IconButton, CheckboxGroup, TagInput,
Avatar, StatCard, SegmentedControl, Card, Kbd, Logo, a tone-aware Badge), a finished token set (z-index scale, a
larger shadow, spring easing, soft brand surface, status tone pairs - all contrast-verified in both themes), and every
existing component got its missing states. Pages then consumed those; no page invents its own control. `CheckboxGroup`
and `TagInput` specifically retire every raw `<select multiple>` and every "comma-separated" text field.

**Work was parallelised by file ownership, with the two shared-file bottlenecks removed first.** Translations now
also load from per-area fragments (`web/console/src/i18n/fragments/<area>.<lang>.json`, deep-merged over the base
files at start-up) so areas add keys without editing one JSON file; the console's `ui.ts` became a blanket
`export * from '@dam/ui-kit'` and `@phosphor-icons/react` is a console dependency too, so consuming a new component or
icon needs no edit to a shared shim (ADR-0022's "ui-kit only" icon rule is relaxed to "one curated set in ui-kit, plus
direct imports where a screen needs a one-off").

**Pages.** Home became a real landing page (recent uploads, status snapshot, quick actions, collections) built from
existing endpoints; Login a branded split layout; Account a profile card; Library/Search/Collections got consistent
page headers, composed empty states, a thumbnail list view and collection cover cards; Asset detail's metadata form
got sections and real multi-select controls; the draft-video and PDF preview bugs found by the sweep were fixed.

**Library state lives in the URL.** Folder, status, type and view (`?folder=&status=&type=&view=`) are search params, so a
filtered library survives reload, can be linked, and the filter chips and back button behave; the Search page reads and
writes `?q=` the same way (typing is debounced into a `replace`, so it never fills history). Bulk edit is a floating bar
that appears with a selection rather than a fixed toolbar row.

**Menus portal into `<main>`.** `PopoverMenu`/`FloatingMenu` (the card "..." menu, context menus) render in a portal inside
the page's `<main>` landmark rather than inside the card, because cards clip (`overflow: hidden`) and a submenu such as
"Make available for" was cut off by its own tile. Focus uses `preventScroll`, and scroll-dismissal only closes a menu
when its anchor really moved (browsers emit spurious `scroll` events when a fixed element is first focused).

## Findings from the sweep that drove specific fixes
- axe: `select-name` (critical) on the vocabulary multi-select - replaced by `CheckboxGroup`.
- A draft **video** had no preview: the console asked for the `preview` transcode, which the delivery origin refuses
  for unpublished assets (ADR-0007); it now asks for the `poster` still until the asset is published.
- A **PDF** preview was the page-1 image loaded in an `<iframe>` (CSP noise, no page navigation); it is now an `<img>`
  with page controls over the `pdf_pages-{n}` renditions. Page navigation is published-only: the delivery origin
  404s `pdf_pages-{n}` for watermarked (unpublished) assets (ADR-0007), so a draft shows page 1 (`preview`) only.
- Phones showed one giant card per row (grid minimum 200px); the grid now drops to 150px minimums below 640px.

## Consequences
- No backend or API change. Everything in this round is `web/`.
- `ui.ts` no longer curates; name collisions between ui-kit components and Phosphor icons are prevented in ui-kit's
  own index (unchanged).
- The e2e suite (`tests/e2e`) was updated for the new UI (status badges, floating bulk bar, bottom tab bar, facet
  checkboxes) and passes against the real stack. A throwaway visual/axe/console-error sweep (22 screens × light/dark ×
  1440px/390px) ended with zero axe violations, console errors or failed requests; the script itself was not kept
  (it needs the seeded dev library).
- Hindi strings for the new areas are machine-first drafts and want a native review.
