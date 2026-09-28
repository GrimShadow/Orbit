const UNITS: [Intl.RelativeTimeFormatUnit, number][] = [
  ['year', 60 * 60 * 24 * 365],
  ['month', 60 * 60 * 24 * 30],
  ['week', 60 * 60 * 24 * 7],
  ['day', 60 * 60 * 24],
  ['hour', 60 * 60],
  ['minute', 60],
];

/** "6 days ago" / "in 3 hours" - localized to whichever language the console is currently showing (en/hi), matching
 * `Intl.RelativeTimeFormat`'s own locale support rather than a hand-rolled English-only formatter. Falls back to
 * "just now" under a minute, since a relative unit that small reads as noise, not information. */
export function relativeTime(iso: string, locale: string): string {
  const diffSec = (new Date(iso).getTime() - Date.now()) / 1000;
  const rtf = new Intl.RelativeTimeFormat(locale, { numeric: 'auto' });
  for (const [unit, secondsInUnit] of UNITS) {
    if (Math.abs(diffSec) >= secondsInUnit) return rtf.format(Math.round(diffSec / secondsInUnit), unit);
  }
  return rtf.format(0, 'minute');
}
