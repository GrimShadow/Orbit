# ADR-0027: Right-click menus, folder management, "make available for", and a theme selector

## Status
Accepted

## Context
Four gaps in the console, reported together:
- there was no way to create, rename or delete a folder (the backend has always supported it);
- no light/dark control (the design tokens already supported both);
- no right-click menu on assets or folders, and so nowhere to rename, delete, or say which systems an asset is for;
- folders were listed by their path key ("brand_assets"), not their name ("Brand assets").

## Decisions
**Folders show `name`, never `label`.** On a folder, `label` is the immutable slug that forms its dotted `path`; `name`
is the display name and the only thing rename changes. The console had been displaying `label` everywhere (tree,
breadcrumb, pickers) and its test fixtures had the two swapped, so nothing caught it.

**One menu primitive for every menu.** `MenuList` (ui-kit) is the ARIA `menu` pattern - arrows, Home/End, Right opens a
submenu, Left/Escape step back, checkable items (`menuitemcheckbox`), separators, danger styling, disabled/empty
submenus - and is rendered by both the "..." button (`CardMenu`) and the new right-click `ContextMenu` (portal, opens at
the pointer, clamped to the viewport, closes on outside click/scroll/resize/Escape and returns focus). `ThumbnailGrid`
and `Tree` open it on right-click (and the keyboard context-menu key, which the browser reports as the same event);
the "..." button stays as the touch/discoverable route, since touch browsers do not reliably fire `contextmenu`.

**Folder management uses the endpoints that already existed.** A "New folder" button and a per-folder menu (open, new
subfolder, rename, make available for, delete) call `POST/PATCH/DELETE /folders`. Rename changes only the display name.
The server's refusal to delete a folder that still holds assets or sub-folders is shown as written (its `detail`, not
the bare "Conflict" title), and deleting the folder you are looking at returns the library to "All folders".

**"Make available for" is the existing channel mechanism, not a new entity.** The core pack already gives every asset
type a `channels` field, bound to a `channel` vocabulary and tagged `accessDimension: channel` - which is also what the
access policy matches on. The menu finds that field from the schema (the field tagged with the `channel` dimension)
and lists the vocabulary's terms, so which systems exist, and what the field is called, stay **tenant data**: Canva,
PADS4 and Web API were added as `channel` terms in the dev tenant, not in code. Choosing one adds or removes that term
in the asset's `channels` (saved as a whole metadata document, so the asset is re-read first to avoid overwriting a
concurrent edit); on a folder it adds the term to every asset inside it (sub-folders included) after a confirmation.
It records where an asset may be used and narrows who can reach it through the channel access dimension; it does not
by itself push anything to Canva or PADS4 - those integrations would read this field.

**A tenant without that field gets an honest menu.** If a tenant's schema has no channel-tagged field, or the vocabulary
has no terms, the submenu says so instead of waiting on a lookup. (The dev tenant's schemas predated the tag - pack
apply is additive and never changes existing content - so its eight schemas were moved to a new version tagging
`channels`, through the ordinary schema API.)

**Theme: Light / Dark / Match system**, a header control that sets `data-theme` on `<html>` (system removes it so
`prefers-color-scheme` decides), remembered in `localStorage` (best-effort) and applied before the first render so a
saved choice does not flash the other theme. `color-scheme` follows so native controls match.

**Deleted assets are hidden by default** (a trash), listed via Status -> Deleted; the delete dialog says where to find them.

## Not included
- Menus on rows of the list/table view, or acting on a multi-selection from the menu (the bulk bar covers selections).
- Removing a system from a whole folder (the per-asset menu toggles; the folder action only adds).
- An admin screen for the systems list - add or rename them as terms of the `channel` vocabulary.
- A long-press gesture on touch; the "..." button is the touch route.

## Consequences
- No backend or API contract change. Tests: ui-kit menu behaviour (right-click, submenus, check marks, keyboard, Tree),
  console flows for asset rename/delete/toggle, folder create/rename/delete/bulk-add, the theme control, and the
  folder display name; exercised end to end in a real browser against the running stack.
