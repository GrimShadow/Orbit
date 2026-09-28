# ADR-0019: Console keyboard shortcuts and mobile-responsive pass (Step 1.8f)

## Status
Accepted

## Context
The spec's Step 1.8 screen list ends with "keyboard shortcuts", and its accept criterion includes "mobile-responsive
at 390 px width" alongside the Playwright e2e flow. This step covers both; the Playwright e2e suite itself is split
out further as Step 1.8g (see Consequences) since standing up the full `docker compose` stack (Postgres, Keycloak,
SeaweedFS, RabbitMQ, OpenSearch, the API/Worker/Scheduler hosts, and the console dev server) to drive a real browser
through it is a materially different, heavier kind of work than the rest of Step 1.8's pure frontend-code sub-steps.

## Decisions

**Keyboard shortcuts are single letters, no chords, and never fire while a form control has focus.**
`shell/keyboardShortcuts.ts` maps one key per primary nav destination (`l`/`s`/`c`/`u` for library/search/
collections/upload) plus `?` for a help dialog - not Gmail-style two-key sequences, which would need to track and
time out a pending first keystroke for no real benefit at this scale. `isShortcutTarget` refuses to treat a keydown
as a shortcut whenever the event's target is an `<input>`/`<textarea>`/`<select>` or a contenteditable region (or any
modifier key is held) - this is the one property most worth getting right: a shortcut system that hijacks ordinary
typing (e.g. typing "library" into the search box jumping the page to `/library`) would be worse than having none.

**The help dialog is reachable two ways: pressing `?`, or a visible "?" button in the header.** A shortcut a person
has to already know exists to discover is a trap for anyone who didn't read a changelog; the visible button costs one
more header control and makes the whole feature self-documenting. Both paths open the same `Modal` listing every
shortcut, built from the same `NAV_SHORTCUTS` array the handler itself reads - the help text and the actual behaviour
cannot drift apart into two different lists.

**The mobile-responsive pass was a CSS audit against the already-existing patterns from earlier steps, not a
from-scratch redesign.** Every screen built since 1.7's UI kit (Step 0.7) already uses `flex-wrap: wrap` on its
toolbars/bars (library, upload, bulk edit) and a `max-width: 640px` stacking breakpoint on any two-column layout
(library's sidebar+grid, search's facets+results); `ui-kit`'s own shared primitives were already viewport-safe
without any change (`Table` wraps in a horizontally-scrolling container rather than force the page to scroll,
`ThumbnailGrid` is `repeat(auto-fill, minmax(160px, 1fr))` so it reflows to fewer columns on its own, `Dialog` caps
its width at `min(560px, calc(100vw - 32px))`). The one real gap this step found and fixed: `.dam-collections__row`
(Step 1.8e, new this cycle) had no `flex-wrap`, so a long collection name next to its type/visibility badges and
delete button could overflow horizontally at 390px - it now wraps like every other toolbar-shaped row in the console.

**No live browser check at 390px was done in this step** - the CSS-level audit above already accounts for every
custom layout rule in the console, and running the full stack to drive a real viewport-sized browser is exactly the
Step 1.8g work this ADR defers (see Consequences). Splitting it out rather than standing up the stack just to eyeball
a few pages avoided doing that infrastructure work twice.

## Consequences
- Step 1.8g (Playwright e2e against the real `docker compose` stack) now carries the spec's actual verification of
  "mobile-responsive at 390 px width" as a real, automated check (a viewport-sized page load with no horizontal
  scroll), not just this step's static CSS reasoning - if it finds a gap this audit missed, that is new work for
  1.8g, not a contradiction of this ADR.
- Keyboard shortcuts are global (any authenticated page) and nav-only; they do not yet cover in-page actions (e.g.
  "select all" in the library, or an asset's own lifecycle actions). Extending the same `isShortcutTarget`-gated
  pattern to page-specific shortcuts is straightforward future work if a need for it shows up.
