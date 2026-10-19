import { useCallback } from 'react';
import { useSearchParams } from 'react-router-dom';
import { decodePeriod, encodePeriod, type Period } from './period';

const KEY = 'dam.unusedPeriod';

function remembered(): string | null {
  try {
    return window.localStorage.getItem(KEY);
  } catch {
    return null;
  }
}

/** The chosen "not used for" window: in the URL (`?unused=6m`) so a view can be linked and survives reload, and
 * remembered per browser so the dashboard and the full page open on whatever was picked last. */
export function usePeriod(): [Period, (p: Period) => void] {
  const [params, setParams] = useSearchParams();
  const period = decodePeriod(params.get('unused') ?? remembered());
  const set = useCallback(
    (next: Period) => {
      const encoded = encodePeriod(next);
      try {
        window.localStorage.setItem(KEY, encoded);
      } catch {
        /* private mode: the URL still carries it */
      }
      setParams(
        (prev) => {
          const p = new URLSearchParams(prev);
          p.set('unused', encoded);
          return p;
        },
        { replace: true },
      );
    },
    [setParams],
  );
  return [period, set];
}
