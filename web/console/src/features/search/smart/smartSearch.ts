import { useCallback } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useAiCapabilities } from '../../ai/aiApi';

const STORAGE_KEY = 'dam.search.smart';

/** The remembered choice of this browser, or `null` when it has never been made (or storage is unavailable). */
export function readSmartPreference(): boolean | null {
  try {
    const v = localStorage.getItem(STORAGE_KEY);
    return v === '1' ? true : v === '0' ? false : null;
  } catch {
    return null;
  }
}

export function writeSmartPreference(on: boolean) {
  try {
    localStorage.setItem(STORAGE_KEY, on ? '1' : '0');
  } catch {
    /* storage unavailable: the URL still carries the choice for this visit */
  }
}

export interface SmartSearchState {
  /** The capabilities answer has arrived (or failed) - a search should not run before this, or it would run twice. */
  ready: boolean;
  /** This organisation can do Smart search right now (AI on, vectors exist, index ready). The toggle is only offered when true. */
  available: boolean;
  /** Whether searches should ask for it: available AND chosen. */
  on: boolean;
  setOn: (on: boolean) => void;
}

/** Smart search on/off for the Search page. Precedence: `?smart=1|0` in the URL (a shared link or the toggle just used), then the
 * choice remembered in this browser, then ON - describing what you want is the point of the feature, and a regular search
 * still runs inside it (the lexical leg), so on is never worse for a short exact query. Never on unless capabilities allow it. */
export function useSmartSearch(): SmartSearchState {
  const caps = useAiCapabilities();
  const [params, setParams] = useSearchParams();
  const available = caps.data?.semanticSearch === true;
  const fromUrl = params.get('smart');
  const chosen = fromUrl === '1' ? true : fromUrl === '0' ? false : (readSmartPreference() ?? true);
  const setOn = useCallback(
    (on: boolean) => {
      writeSmartPreference(on);
      setParams(
        (prev) => {
          const p = new URLSearchParams(prev);
          p.set('smart', on ? '1' : '0');
          return p;
        },
        { replace: true },
      );
    },
    [setParams],
  );
  return { ready: !caps.isPending, available, on: available && chosen, setOn };
}

/** The "why it matched" kinds, in display order; anything else the API may add later is ignored. */
export const MATCH_KINDS = ['text', 'image', 'ocr', 'transcript'] as const;
export type MatchKind = (typeof MATCH_KINDS)[number];

/** Seconds for a `?t=` jump link. */
export const toSeconds = (ms: number) => Math.max(0, Math.floor(ms / 1000));
