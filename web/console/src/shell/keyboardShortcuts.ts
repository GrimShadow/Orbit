export interface NavShortcut {
  key: string;
  path: string;
  labelKey: string;
}

/** Single-key, no-chord shortcuts (spec Step 1.8: "keyboard shortcuts") - one letter per primary nav destination,
 * deliberately not Gmail-style two-key chords, so a single `keydown` is enough to decide whether to act. */
export const NAV_SHORTCUTS: NavShortcut[] = [
  { key: 'l', path: '/library', labelKey: 'shell.shortcuts.library' },
  { key: 's', path: '/search', labelKey: 'shell.shortcuts.search' },
  { key: 'c', path: '/collections', labelKey: 'shell.shortcuts.collections' },
  { key: 'u', path: '/upload', labelKey: 'shell.shortcuts.upload' },
];

export const HELP_KEY = '?';

interface KeyLike {
  key: string;
  ctrlKey: boolean;
  metaKey: boolean;
  altKey: boolean;
}

/** Never treat a keystroke as a shortcut while any modifier is held (so OS/browser shortcuts and multi-key combos
 * pass through untouched) or while focus is on a form control or contenteditable region (so shortcuts never hijack
 * ordinary typing - e.g. typing "library" into the search box must never jump the page to /library). */
export function isShortcutTarget(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return true;
  if (target.isContentEditable) return false;
  return target.tagName !== 'INPUT' && target.tagName !== 'TEXTAREA' && target.tagName !== 'SELECT';
}

export function matchNavShortcut(e: KeyLike): string | undefined {
  if (e.ctrlKey || e.metaKey || e.altKey) return undefined;
  return NAV_SHORTCUTS.find((s) => s.key === e.key.toLowerCase())?.path;
}

export function isHelpShortcut(e: KeyLike): boolean {
  return !e.ctrlKey && !e.metaKey && !e.altKey && e.key === HELP_KEY;
}
