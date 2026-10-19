/** The "not used for ..." window. Presets cover the common audit horizons; custom is a number of days/weeks/months/years. */
export type CustomUnit = 'days' | 'weeks' | 'months' | 'years';
export type Period = { kind: '30d' } | { kind: '6m' } | { kind: '1y' } | { kind: 'custom'; amount: number; unit: CustomUnit };

export const PRESETS = ['30d', '6m', '1y'] as const;
export const DEFAULT_PERIOD: Period = { kind: '6m' };
export const MAX_DAYS = 3650;

const UNIT_DAYS: Record<CustomUnit, number> = { days: 1, weeks: 7, months: 30, years: 365 };

/** Window length in whole days, as the API takes it. Six months is 183 days so it matches a calendar half-year better
 * than 6 x 30; "1 year" is 365. */
export function periodDays(p: Period): number {
  switch (p.kind) {
    case '30d':
      return 30;
    case '6m':
      return 183;
    case '1y':
      return 365;
    case 'custom':
      return Math.min(MAX_DAYS, Math.max(1, Math.round(p.amount * UNIT_DAYS[p.unit])));
  }
}

/** URL form: `30d`, `6m`, `1y` or `c:<amount><d|w|m|y>`, e.g. `c:90d`. */
export function encodePeriod(p: Period): string {
  return p.kind === 'custom' ? `c:${p.amount}${p.unit[0]}` : p.kind;
}

export function decodePeriod(raw: string | null | undefined): Period {
  if (raw === '30d' || raw === '6m' || raw === '1y') return { kind: raw };
  const m = /^c:(\d{1,4})([dwmy])$/.exec(raw ?? '');
  if (m) {
    const amount = Number(m[1]);
    const unit = ({ d: 'days', w: 'weeks', m: 'months', y: 'years' } as const)[m[2] as 'd' | 'w' | 'm' | 'y'];
    if (amount >= 1 && amount * UNIT_DAYS[unit] <= MAX_DAYS) return { kind: 'custom', amount, unit };
  }
  return DEFAULT_PERIOD;
}

/** Whole days between two instants, never negative. */
export function daysBetween(from: Date, to: Date): number {
  return Math.max(0, Math.floor((to.getTime() - from.getTime()) / 86_400_000));
}
