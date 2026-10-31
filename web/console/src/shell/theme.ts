import { useCallback, useSyncExternalStore } from 'react';

export type ThemePreference = 'light' | 'dark' | 'system';
const KEY = 'dam.theme';
const listeners = new Set<() => void>();

export function readTheme(): ThemePreference {
  try {
    const v = localStorage.getItem(KEY);
    return v === 'light' || v === 'dark' ? v : 'system';
  } catch {
    return 'system'; // storage can be unavailable (private windows, blocked site data) - follow the OS
  }
}

/** Light and dark pin `data-theme` on <html> (the design tokens key off it); system removes it so the OS preference
 * (`prefers-color-scheme`) decides. */
export function applyTheme(preference: ThemePreference) {
  const root = document.documentElement;
  if (preference === 'system') root.removeAttribute('data-theme');
  else root.setAttribute('data-theme', preference);
  syncThemeColor(preference);
}

/** The browser chrome colour (`<meta name="theme-color">`, one per OS scheme in index.html) follows the OS setting on its
 * own; a manual Light/Dark choice has to override both, and "system" restores what index.html declared. */
function syncThemeColor(preference: ThemePreference) {
  const metas = document.querySelectorAll<HTMLMetaElement>('meta[name="theme-color"]');
  metas.forEach((m) => {
    if (!m.dataset.original) m.dataset.original = m.content;
    if (preference === 'system') {
      m.content = m.dataset.original;
      return;
    }
    const dark = /dark/.test(m.media);
    const original = [...metas].find((x) => /dark/.test(x.media) === (preference === 'dark'));
    m.content = (original?.dataset.original ?? original?.content ?? m.content) || (dark ? '#06110C' : '#f5f5f4');
  });
}

export function setTheme(preference: ThemePreference) {
  try {
    if (preference === 'system') localStorage.removeItem(KEY);
    else localStorage.setItem(KEY, preference);
  } catch {
    /* the choice still applies for this visit */
  }
  applyTheme(preference);
  cache = preference;
  listeners.forEach((l) => l());
}

let cache: ThemePreference | undefined;
const subscribe = (l: () => void) => {
  listeners.add(l);
  return () => listeners.delete(l);
};

export function useTheme(): [ThemePreference, (p: ThemePreference) => void] {
  const preference = useSyncExternalStore(subscribe, () => (cache ??= readTheme()));
  return [preference, useCallback((p: ThemePreference) => setTheme(p), [])];
}
