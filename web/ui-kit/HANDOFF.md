# `@dam/ui-kit` handoff - design-system polish round

Everything below is exported from `@dam/ui-kit` (the console re-exports it through `web/console/src/ui.ts`). All
styling lives in `web/ui-kit/src/styles.css` on `--dam-*` tokens and works in light and dark.

## New tokens

| Token | Light | Dark | Use |
| --- | --- | --- | --- |
| `--dam-radius-xl` | 20px | 20px | large panels |
| `--dam-shadow-lg` | navy-tinted, diffused | deeper | menus, dialogs, toasts |
| `--dam-surface-raised` | `#ffffff` | `#182038` | cards/panels above `--dam-surface` |
| `--dam-border-subtle` | `#e4e7ef` | `#24304d` | hairline dividers (table rows, menu separators, tree guides) |
| `--dam-brand-soft` / `--dam-brand-soft-text` | `#e5eee8` / `#1b4a39` (6.49:1) | `#1e3328` / `#a9dcc1` (9.26:1) | selected nav/rows/tree node, chips, brand badge |
| `--dam-tone-info-bg/-text` | `#dbeafe` / `#1e40af` (7.15:1) | `#1b2d57` / `#a9c8fd` (7.93:1) | |
| `--dam-tone-success-bg/-text` | `#ccfbf1` / `#115e59` (6.73:1) | `#0f3a38` / `#7ae9d8` (8.60:1) | |
| `--dam-tone-warning-bg/-text` | `#fef3c7` / `#92400e` (6.37:1) | `#42300f` / `#fcd34d` (8.77:1) | |
| `--dam-tone-danger-bg/-text` | `#fee2e2` / `#991b1b` (6.80:1) | `#4a1d22` / `#fda4a4` (7.40:1) | |
| `--dam-tone-neutral-bg/-text` | `#eef0f7` / `#374151` (9.05:1) | `#1f2944` / `#c9d1e3` (9.40:1) | |
| `--dam-ease-spring` | `cubic-bezier(0.32, 0.72, 0, 1)` | | presses, pills, dialogs |
| `--dam-duration-slow` | 280ms | | drawer, indicator slides |
| `--dam-z-sticky` 10 · `--dam-z-menu` 1000 · `--dam-z-overlay` 2000 · `--dam-z-modal` 2100 · `--dam-z-toast` 3000 · `--dam-z-contextmenu` 3100 | | | **never write a bare z-index** (shell.css has `2000` on `.dam-skip` and `50` on the user menu - switch them to `var(--dam-z-overlay)` / `var(--dam-z-menu)`) |
| also: `--dam-space-7` 40px, `--dam-space-8` 48px, `--dam-text-xs` 0.75rem, `--dam-text-2xl` 2rem | | | |

`--dam-focus` is now the brand green in both themes (was blue). The global ring is `2px solid var(--dam-focus)` with a
2px offset; `outline: 3px` rules in console CSS can be deleted so they inherit it.

## New components

| Import | Props | Notes |
| --- | --- | --- |
| `Badge` | `tone?: 'neutral' \| 'info' \| 'success' \| 'warning' \| 'danger' \| 'brand'`, `size?: 'sm' \| 'md'`, `children` | Neutral renders exactly `class="dam-badge"`, so the console's local `Badge` in `ui.ts` can be deleted with no visual change. Map statuses: published→`success`, in review→`info`, expiring/scheduled→`warning`, rejected/deleted→`danger`, draft→neutral. |
| `IconButton` | `label` (aria-label + title), `variant?: 'ghost' \| 'secondary' \| 'primary'`, `size?: 'sm' \| 'md'`, `loading?`, button props; `children` = the icon | 36/32px square. Use instead of `<Button aria-label>✕</Button>`. |
| `EmptyState` | `icon?`, `title`, `description?`, `action?`, `titleAs?: 'h2' \| 'h3' \| 'p'` (default h2) | Pick `titleAs` to keep heading order (axe `heading-order`). |
| `PageHeader` | `title`, `description?`, `eyebrow?`, `actions?`, `children?` (toolbar row), `titleAs?: 'h1' \| 'h2'` | Renders a `<header>` with an `h1` by default - remove the page's own `h1`. |
| `CheckboxGroup` | `label`, `options: {value,label,count?,disabled?}[]`, `value: string[]`, `onChange(next)`, `hint?`, `columns?: 1 \| 2`, `hideLabel?` | The replacement for every `<select multiple>`. A real `<fieldset>`; counts are tabular. |
| `TagInput` | `label`, `value: string[]`, `onChange`, `hint?`, `placeholder?`, `suggestions?` (native datalist), `removeLabel?(tag)`, `listLabel?`, `disabled?` | Enter/comma adds, Backspace on empty removes last, values are trimmed + lower-cased + de-duplicated. Pass translated `removeLabel`/`listLabel` (defaults are English). |
| `Avatar` | `name`, `size?: 'sm' \| 'md' \| 'lg'` | Rounded square with initials (`initialsOf(name)` is exported). Sizes are keyed off `data-size`; delete shell.css's `.dam-avatar` rule when switching. |
| `StatCard` | `label`, `value`, `hint?`, `icon?`, `tone?` (Badge tone) | |
| `SegmentedControl` | `label`, `value`, `onChange`, `options: {value,label,icon?}[]` | `role="group"` of `aria-pressed` buttons with an animated pill; use it for the grid/list toggle. |
| `Card` | `padding?: 'none' \| 'sm' \| 'md' \| 'lg'`, `interactive?`, `as?: 'div' \| 'section' \| 'article'`, div props | The raised surface. |
| `Kbd` | `children` | |
| `Logo` | `size?` (px, default 24), `wordmark?` (default true), `label?` (default "Orbit") | Inline SVG glyph + "Orbit" wordmark. `web/ui-kit/src/logo.svg` is the glyph alone for the favicon. |
| `PopoverMenu` | `anchor: HTMLElement \| null`, `label`, `items: MenuItem[]`, `onClose`, `id?` | A menu anchored below a trigger button, portalled to `<body>` (used by `CardMenu`; also right for the header user menu). |

New curated icon exports: `CaretDown`, `CaretRight`, `DotsThree`, `DownloadSimple`, `FolderOpen`, `Images`, `Info`,
`MagnifyingGlass`, `Plus`, `WarningCircle`, `X`.

## Changes to existing components (props unchanged)

- **Button**: 36px md / 32px sm; spring press; primary has an inner highlight; ghost hover tint; danger hover.
- **Input/Select**: 36px; brand focus ring (2px, offset); error message now shows a warning icon. `Select` keeps a
  native `<select>` but wraps it in `.dam-select-wrap` with a Phosphor `CaretDown`. A raw `className="dam-select"`
  on a plain `<select>` still gets a (grey) CSS caret; `select[multiple]` gets none.
- **Table**: no vertical rules, hairline dividers, uppercase 11px headers, sticky header, hover tint, selected row gets
  `.dam-table__row--selected` (brand-soft). Sort indicators are Phosphor carets. Put `className="dam-table__thumb"` on
  a 40px `<img>` (or span) in a `render` cell.
- **Dialog/Modal/Drawer**: blurred backdrop, 16px radius, 24px padding, `IconButton` close, scale/fade open; the
  drawer slides from the end edge (RTL-aware via `[dir=rtl]`).
- **Toast**: tone icon, slide-in from the end, `IconButton` dismiss. Behaviour unchanged.
- **Tabs**: transform-animated underline (`.dam-tablist__indicator`), hover tint.
- **Tree**: rotating `CaretRight`, `FolderSimple`/`FolderOpen` per node, 2px indent guides, brand-soft selected pill.
  The label is now wrapped in `.dam-treeitem__label` (truncates).
- **FacetList**: same checkbox rows as `CheckboxGroup` (`.dam-check*`); `.dam-facet__opt`/`__count` classes stay on
  the markup.
- **Skeleton**: transform-based shimmer; off under `prefers-reduced-motion`.
- **ThumbnailGrid**: image scales 1.02 on card hover; selection circle and "..." trigger have a blurred backdrop;
  below 640px the grid is `minmax(150px, 1fr)` with a 12px gap and meta lines truncate.
- **CardMenu** (bug fix): the "..." menu is now portalled to `<body>` via `PopoverMenu` (`position: fixed`,
  `z-index: var(--dam-z-menu)`), anchored to the trigger's end edge and flipped above when there is no room, so the
  card's `overflow: hidden`/hover transform can no longer clip it. `.dam-cardmenu__list` no longer exists.
- **Menu/ContextMenu**: `--dam-shadow-lg`, 8px item radius, fade + rise on open, danger items tint red on hover.
- **Global**: `prefers-reduced-motion` disables all transitions/animations except the loading spinner; thin themed
  scrollbars; one `:focus-visible` ring everywhere (including portalled layers).

## Things consumers should do

1. Delete the local `Badge` in `web/console/src/ui.ts` and switch status badges to tones.
2. Replace `<select multiple className="dam-select">` (e.g. `MetadataFields`) with `CheckboxGroup`.
3. Replace raw `z-index` numbers and `outline: 3px solid var(--dam-focus)` rules in console CSS with the tokens /
   the inherited ring; remove `.dam-avatar` from `shell.css` once `Avatar` is used.
4. Use `PageHeader` for page titles and `EmptyState` (with `titleAs`) instead of `.dam-empty` text where a next step
   exists.
5. Favicon: point `index.html` at a copy of `web/ui-kit/src/logo.svg`.
