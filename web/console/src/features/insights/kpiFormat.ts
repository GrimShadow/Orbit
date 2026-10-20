import type { TFunction } from 'i18next';

/** Percentage change from `before` to `now`, or `null` when there is nothing to compare (either figure missing, or an
 * earlier figure of zero that makes a percentage meaningless). Equal figures are 0. */
export function changePercent(now: number | null | undefined, before: number | null | undefined): number | null {
  if (now == null || before == null) return null;
  if (before === 0) return now === 0 ? 0 : null;
  return ((now - before) / Math.abs(before)) * 100;
}

export type Verdict = 'better' | 'worse' | 'same' | 'unknown';

/** Whether a change is good news. `better` says which direction counts as good for this metric. A change that rounds to
 * 0% (the same rounding the badge shows) is "same". */
export function verdictOf(change: number | null, better: 'up' | 'down'): Verdict {
  if (change === null || !Number.isFinite(change)) return 'unknown';
  const rounded = Math.round(change);
  if (rounded === 0) return 'same';
  return (rounded > 0 ? 'up' : 'down') === better ? 'better' : 'worse';
}

/** A fraction (0.943) as "94.3%". A value that would round up to 100% without being all of it reads "99.9%", so a near miss
 * is never shown as perfect. `null` is not formatted here: callers show "No data yet". */
export function formatRatio(ratio: number, locale: string): string {
  let v = ratio;
  const nf = new Intl.NumberFormat(locale, { style: 'percent', minimumFractionDigits: 0, maximumFractionDigits: 1 });
  if (v < 1 && Math.round(v * 1000) / 1000 >= 1) v = 0.999;
  return nf.format(Math.max(0, v));
}

/** Decimal hours as minutes, hours or days: 0.75 -> "45 min", 5.5 -> "5.5 h", 72 -> "3 d". */
export function formatHours(hours: number, t: TFunction, locale: string): string {
  const nf = new Intl.NumberFormat(locale, { maximumFractionDigits: 1 });
  if (hours < 1) return t('insights.fmt.minutes', { value: nf.format(Math.max(1, Math.round(hours * 60))) });
  if (hours < 48) return t('insights.fmt.hours', { value: nf.format(hours) });
  return t('insights.fmt.days', { value: nf.format(hours / 24) });
}

export function formatCount(n: number, locale: string): string {
  return new Intl.NumberFormat(locale).format(n);
}

export function formatDecimal(n: number, locale: string): string {
  return new Intl.NumberFormat(locale, { maximumFractionDigits: 1 }).format(n);
}

/** `2026-10-01` or a full instant as a short date in the reader's language. Date-only strings are read as UTC calendar
 * days (that is what the API means by them) so the label never slips a day in a western time zone. */
export function formatDay(value: string, locale: string): string {
  const d = /^\d{4}-\d{2}-\d{2}$/.test(value) ? new Date(`${value}T00:00:00Z`) : new Date(value);
  if (Number.isNaN(d.getTime())) return value;
  return new Intl.DateTimeFormat(locale, { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC' }).format(d);
}
