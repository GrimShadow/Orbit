import { useCallback } from 'react';
import { useSearchParams } from 'react-router-dom';

export const INSIGHTS_PRESETS = [30, 90, 365] as const;
export const INSIGHTS_DEFAULT_DAYS = 30;
export const INSIGHTS_MAX_DAYS = 365;
const KEY = 'dam.insightsDays';

/** A whole number of days from 1 to 365, or `null` for anything else (the API takes 1-365). */
export function parseDays(raw: string | null | undefined): number | null {
  if (!raw || !/^\d{1,3}$/.test(raw.trim())) return null;
  const n = Number(raw);
  return n >= 1 && n <= INSIGHTS_MAX_DAYS ? n : null;
}

function remembered(): string | null {
  try {
    return window.localStorage.getItem(KEY);
  } catch {
    return null;
  }
}

/** The reporting window in days: in the URL (`?days=90`) so a view can be linked and survives reload, and remembered per
 * browser so Key results and Reports open on whatever was picked last. */
export function useInsightsPeriod(): [number, (days: number) => void] {
  const [params, setParams] = useSearchParams();
  const days = parseDays(params.get('days')) ?? parseDays(remembered()) ?? INSIGHTS_DEFAULT_DAYS;
  const set = useCallback(
    (next: number) => {
      try {
        window.localStorage.setItem(KEY, String(next));
      } catch {
        /* private mode: the URL still carries it */
      }
      setParams(
        (prev) => {
          const p = new URLSearchParams(prev);
          p.set('days', String(next));
          return p;
        },
        { replace: true },
      );
    },
    [setParams],
  );
  return [days, set];
}
