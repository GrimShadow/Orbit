# ADR-0022: Console design system refresh - breadcrumbs, richer cards, iconography (round 2)

## Status
Accepted

## Context
Round one (ADR-0021) was a token-and-motion foundation pass: color, typography, radius, shadows, transitions. The
user asked for a round two of per-page polish, pointing at Adobe's own DAM products (Bridge-style review, AEM
Assets, and AEM's search results screen) as a reference for the level of information density and navigational
structure a mature DAM UI carries. This round closes the two biggest structural gaps between Orbit's library/search/
collections screens and those references: **no path breadcrumb** (only a folder tree sidebar) and **thumbnail cards
that showed almost nothing** (title, one subtitle line, one badge - no type glyph, no recency, no visual weight to
selection). A third, smaller gap - every icon-less button - was addressed by extending round one's motion work with
a real icon set, since Adobe's own toolbars are icon-first.

## Decisions

**`@phosphor-icons/react` is the icon set, added only to `ui-kit`.** `redesign-existing-projects`'s own audit
explicitly flags Lucide/Feather as "the default AI icon choice" to avoid; Phosphor has full multi-weight coverage
(needed for both filled "selected" states and outline default states) and, at ~3000 icons, easily covers every
concept used here. It is a dependency of `ui-kit` only, re-exported through both `ui-kit/src/index.ts` (a curated,
explicit list - not `export *`, since several Phosphor icon names collide with this file's own component exports,
e.g. `Table`) and the console's `ui.ts` shim, so the console never imports it directly. Production bundle impact was
measured, not assumed: +14.5KB gzip for every icon actually used across the app (tree-shaking confirmed working -
Vite's own module count jumped from 172 to 4718 modules processed, but only the icons actually imported survive into
the bundle).

**Card richness (`Thumb.icon`, `Thumb.meta`) is additive to `ui-kit`'s existing `ThumbnailGrid`, not a new
component.** A card now shows: a `FileTypeIcon` (a new small `ui-kit` component mapping the asset-type vocabulary -
image/video/audio/document/manual/3d/html5/other, a fixed technical taxonomy, not tenant content - to an icon) next
to the type label, and a relative-time line ("Updated 6 days ago") below it. **File size and pixel dimensions, which
Adobe's own cards also show, are deliberately left out of this round**: neither `AssetDto` nor `SearchHitDto` carries
that data today (it lives only on `AssetVersionDto`, one call per asset away) - showing it on a grid of 50 cards
would mean either N+1 requests per page load or a backend change to denormalize it onto the list/search response.
Both are out of scope for a frontend-only design round; whichever is chosen later is a backend decision, not a UI
one, and is called out in Consequences rather than worked around here.

**A single shared `toAssetThumb` mapping replaces three near-identical copies.** `AssetDto` (library) and
`SearchHitDto` (search, collections) already share the exact fields a card needs (`id`, `type`, `status`, `title`,
`updatedAt`); the library, search, and collection-detail screens all now build their `Thumb`s through one function
(`features/library/assetThumb.tsx`) instead of three separately-maintained ones that would inevitably drift.

**Selection is shown as a filled checkmark badge with a tinted card ring, not a plain top-left checkbox.** The
underlying `<input type="checkbox">` stays for full keyboard/screen-reader semantics (visually reduced to a plain
circle via `appearance: none`); a `Check` icon is layered on top and only made visible via the checkbox's own
`:checked` state (a sibling selector, not JS-driven visibility), so the accessible control and its visual
presentation stay properly linked without a second source of truth for "is this selected." The selected card also
gets a `--dam-brand`-colored ring (`box-shadow: 0 0 0 2px`) - both cues Adobe's own review-timeline screen uses side
by side (a blue tick badge plus a tinted card background).

**Breadcrumbs are ancestor-chain, built from `parentId`, not the ltree `path` string.** `folderBreadcrumb` walks each
folder's `parentId` to the root rather than splitting the dotted `path` field, because `path` is a name-based ltree
key (renaming a folder changes its `path` segment, not its `id`) - walking ids stays correct across a rename, parsing
a name string would not. The trail sits above the existing filter toolbar in `LibraryPage`, reusing (not replacing)
the folder tree sidebar - clicking a breadcrumb segment or a tree node do the same thing (`setFolderId`), so the two
navigation surfaces can never disagree about the current folder.

**Icons attach to existing buttons; no new toolbar layout.** Rather than restructuring `BulkEditBar`/
`LifecycleActions`/the view toggle into an Adobe-style icon-above-label vertical toolbar (a bigger, riskier layout
change touching several components' markup), each existing button gained a leading icon via `Button`'s own
`display: inline-flex; gap` (already part of round one) - `Tag` for tag edits, `FolderSimple` for move,
`BookmarkSimple` for save-as-collection/save-search, `SquaresFour`/`Rows` for the grid/list toggle, `UploadSimple` for
starting an upload, and one `ActionIcon` per lifecycle action (`features/asset/actionIcon.tsx`) shared between the
asset detail page's own lifecycle buttons and the bulk-edit bar, so the same action reads identically in both places.

## Consequences
- File size and dimensions on grid cards remain a known, explicitly deferred gap - the natural next step is a
  backend decision (denormalize onto `AssetDto`/`SearchHitDto`, likely from the current version's already-extracted
  technical metadata) before any further frontend work there.
- Per-card quick-action menus (Adobe's "..." kebab: Details/Download/Move/Rename/Assign) were considered and
  deliberately left out of this round - every one of those actions already exists, but only on the asset detail page;
  duplicating them per-card is real new interaction surface (not a polish-level change) and is a reasonable candidate
  for a future round, not this one.
- `FileTypeIcon` and `ActionIcon` are now the single source of truth for their respective icon mappings; any new
  asset type or lifecycle action needs an entry there, not a new inline icon choice at each call site.
