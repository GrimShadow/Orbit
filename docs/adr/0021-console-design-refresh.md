# ADR-0021: Console design system refresh - typography, color, motion (round 1)

## Status
Accepted

## Context
Outside the spec's own step numbering: the console (built incrementally across Steps 0.7 and 1.8a-g) was
functionally complete but visually generic - every classic "AI-generated admin panel" fingerprint was present at
once: an indigo/purple accent (the single most common AI-design tell), `system-ui` with only two font weights in
use, a uniform 8px/5px border radius on every element regardless of size, and zero transition durations anywhere -
buttons and inputs changed state instantly with no hover/press feedback. None of this was a deliberate design
decision; it was simply never addressed while the underlying features were being built.

This step used three design-focused skills (`ui-designer`, `ux-architect`, and a vendored `redesign-existing-projects`
skill - see below) to audit the current `web/ui-kit/src/styles.css` design system against a concrete checklist and
fix the highest-leverage items first, in place, without touching component markup or breaking any existing test.

**On the skills used:** the user asked for `design-taste-frontend` specifically, but that skill's own scope note says
it targets "landing pages, portfolios, and redesigns - not dashboards, not data tables, not multi-step product UI",
which is exactly what the Orbit console is. It was still installed (`npx skills add Leonxlnx/taste-skill`, MIT
licensed, reviewed before installing) since the same install brought in `redesign-existing-projects` - a sibling
skill in that repo built specifically for upgrading an existing app's design in place, which is what this step
actually needed.

## Decisions

**Forest green replaces indigo/purple as the brand accent - a decision made by the user, not inferred.** Given a
choice between a teal, an amber/copper, or a forest green direction, the user picked forest green. Light mode uses
Tailwind's green-700/green-800 pair (`#15803d`/`#166534`, matching the exact 600/700-step relationship the old
indigo-700/800 pair had); dark mode inverts to a light green-400/green-300 pair (`#4ade80`/`#86efac`) on a dark
surface, mirroring the existing dark-mode pattern where filled buttons use a light accent with dark text. The
existing `--dam-success` token was shifted from a near-identical green (`#047857`/`#6ee7b7`) to teal
(`#0d9488`/`#2dd4bf`) specifically so the new brand color and the semantic "success" color never sit next to each
other looking like the same color with different jobs.

**Geist (Vercel, SIL OFL) replaces `system-ui` for Latin text; `Noto Sans Devanagari` stays as the Hindi fallback,
unchanged.** Self-hosted (two vendored variable `.woff2` files, ~70KB each, `web/ui-kit/src/fonts/`) rather than
loaded from a font CDN - the spec's own Phase 4 already commits to an air-gapped/offline-bundle deployment story, so
a runtime dependency on Google Fonts or any external font host would work against that from day one. Geist was
picked over other "not-Inter" alternatives (Outfit, Plus Jakarta Sans, Satoshi) because it was purpose-built by
Vercel for exactly this kind of dense product dashboard, ships a matching monospace (`Geist Mono`, vendored
alongside, available as `--dam-font-mono` for future tabular/numeric UI), and needed no licensing research (OFL,
freely redistributable - `web/ui-kit/src/fonts/README.md` documents the exact source and how to update it, since the
`geist` npm package's own JS wrapper is Next.js-only and was removed after extracting the two font files it doesn't
otherwise contribute anything here).

**Border radius became a three-step scale (6px / 10px / 16px) instead of one uniform value repeated everywhere.**
Tight functional elements (buttons, inputs, tree rows, tags) kept the smallest tier; container-level elements (the
table wrapper) sit at the middle tier; and elements meant to read as soft, premium surfaces - dialogs and library
thumbnail cards - moved up to the largest tier. This is the single check on the redesign audit's list that most
directly separates "generated" from "designed": identical radii everywhere is a strong AI-default tell precisely
because it requires no compositional judgment to produce.

**Every interactive element now has an explicit transition and a real hover/press state - none did before.**
Buttons gained a 160ms transition plus a `scale(0.98)` on `:active` (a physical "press" cue); inputs and selects
gained a border-color hover state (previously: none at all - a field looked identical whether it was interactive or
disabled until you actually clicked it); tabs and tree rows gained transitioned color/background changes instead of
instant swaps; and library thumbnail cards - previously completely static - now lift (`translateY(-2px)`) and gain
elevation on hover, using a new two-tier shadow scale (`--dam-shadow-sm` resting, `--dam-shadow` on hover/elevated)
built on the same dark-navy-tinted shadow color the design already had (that part turned out to already be reasonably
correct - see Consequences).

**Existing `box-shadow` tinting was reassessed, not changed.** The redesign audit specifically flags "generic
box-shadow... pure black at low opacity" as a tell. The console's shadows were already `rgb(16 24 40 / …)` - a dark
navy tint, not pure black - which already matches the audit's own recommended fix (tint shadows to the surface's
hue family). No change was needed there; a second, lighter elevation tier (`--dam-shadow-sm`) was added instead so
resting and hovered/elevated states are visually distinct.

## Consequences
- This is explicitly **round one** - a design-token-and-motion foundation pass across `ui-kit`'s shared component
  styles, not a page-by-page visual redesign. It touches every screen that consumes these tokens (all of them) but
  does not change any component's markup, add new components, or redesign any single screen's layout/content
  structure. Further passes (per-page polish, empty-state design, iconography, etc.) are deliberately out of scope
  here and are a separate, later decision.
- `web/ui-kit/src/fonts/` now holds two vendored binary font files (~140KB combined) plus their OFL license text,
  checked into the repository rather than fetched at build or runtime.
- Every WCAG contrast pair the new colors touch was verified (axe accessibility test in `ui-kit`'s own suite, plus a
  manual contrast-ratio check for the dark-mode pairs axe's JSDOM run doesn't exercise) - see the PR for the actual
  ratios; all clear AA (4.5:1 text / 3:1 non-text) with meaningful margin.
- `.agents/skills/` (13 skills from the `Leonxlnx/taste-skill` repo, including `design-taste-frontend` and
  `redesign-existing-projects`) is now a permanent part of this repo, available to any future session working on the
  console's design.
