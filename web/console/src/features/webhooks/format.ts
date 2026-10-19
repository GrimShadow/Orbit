import { useEffect, useState } from 'react';
import type { DeliveryDto } from './webhooksApi';

/** "5 min", "2 hr", "30 sec" in the reader's language. */
export function shortDuration(ms: number, locale: string): string {
  const sec = Math.max(1, Math.round(ms / 1000));
  const [value, unit] =
    sec >= 7200 ? [Math.round(sec / 3600), 'hour'] : sec >= 120 ? [Math.round(sec / 60), 'minute'] : [sec, 'second'];
  return new Intl.NumberFormat(locale, { style: 'unit', unit, unitDisplay: 'short' }).format(value);
}

/** The current time, refreshed every `everyMs`, for text such as "Retry in 5 min" that must keep counting down. */
export function useNow(everyMs = 15_000): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), everyMs);
    return () => clearInterval(id);
  }, [everyMs]);
  return now;
}

/** How a delivery stands: `retrying` is a pending delivery that has already been tried once and failed. */
export type DeliveryState = 'succeeded' | 'failed' | 'pending' | 'retrying';

export function deliveryState(d: Pick<DeliveryDto, 'status' | 'lastStatusCode' | 'lastError'>): DeliveryState {
  if (d.status === 'succeeded') return 'succeeded';
  if (d.status === 'failed') return 'failed';
  return d.lastStatusCode !== null || d.lastError !== null ? 'retrying' : 'pending';
}

/** A short, safe label for a JSON value shown in a `<pre>`. */
export function prettyJson(value: unknown): string {
  try {
    return JSON.stringify(value, null, 2) ?? '';
  } catch {
    return String(value);
  }
}
