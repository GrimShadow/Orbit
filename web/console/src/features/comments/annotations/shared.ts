import { useCallback, useState, type KeyboardEvent } from 'react';
import { useMe } from '../../../api/me';

/** Annotating needs the permission to comment; everyone who can read still SEES existing annotations. */
export function useCanComment(): boolean {
  const me = useMe();
  return Boolean(me.data?.permissions.includes('comments.write'));
}

const VISIBLE_KEY = 'dam.annotations.visible';

/** Show/hide every annotation on the stage; remembered per browser (storage may be unavailable, so every access is guarded). */
export function useAnnotationsVisible(): [boolean, (on: boolean) => void] {
  const [visible, setVisibleState] = useState(() => {
    try {
      return localStorage.getItem(VISIBLE_KEY) !== 'false';
    } catch {
      return true;
    }
  });
  const setVisible = useCallback((on: boolean) => {
    setVisibleState(on);
    try {
      localStorage.setItem(VISIBLE_KEY, String(on));
    } catch {
      /* not remembered */
    }
  }, []);
  return [visible, setVisible];
}

/** True when a key press belongs to something being typed into (or has a modifier), so a stage shortcut must not fire. */
export function ignoreShortcut(e: KeyboardEvent<HTMLElement>): boolean {
  if (e.defaultPrevented || e.altKey || e.ctrlKey || e.metaKey || e.repeat) return true;
  const t = e.target;
  if (!(t instanceof HTMLElement)) return false;
  return (
    Boolean(t.closest('[contenteditable=""], [contenteditable="true"]')) || ['INPUT', 'TEXTAREA', 'SELECT'].includes(t.tagName)
  );
}
