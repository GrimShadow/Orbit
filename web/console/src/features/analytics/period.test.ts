import { DEFAULT_PERIOD, decodePeriod, encodePeriod, MAX_DAYS, periodDays, type Period } from './period';

describe('period', () => {
  it('maps presets and custom windows to whole days', () => {
    expect(periodDays({ kind: '30d' })).toBe(30);
    expect(periodDays({ kind: '6m' })).toBe(183);
    expect(periodDays({ kind: '1y' })).toBe(365);
    expect(periodDays({ kind: 'custom', amount: 2, unit: 'weeks' })).toBe(14);
    expect(periodDays({ kind: 'custom', amount: 18, unit: 'months' })).toBe(540);
  });

  it('clamps custom windows to the API range', () => {
    expect(periodDays({ kind: 'custom', amount: 0, unit: 'days' })).toBe(1);
    expect(periodDays({ kind: 'custom', amount: 50, unit: 'years' })).toBe(MAX_DAYS);
  });

  it('round-trips through the URL encoding', () => {
    const cases: Period[] = [
      { kind: '30d' },
      { kind: '6m' },
      { kind: '1y' },
      { kind: 'custom', amount: 90, unit: 'days' },
      { kind: 'custom', amount: 2, unit: 'years' },
    ];
    for (const p of cases) expect(decodePeriod(encodePeriod(p))).toEqual(p);
  });

  it('falls back to the default on junk or out-of-range input', () => {
    for (const raw of [null, '', 'x', 'c:0d', 'c:9999y', 'c:12q']) expect(decodePeriod(raw)).toEqual(DEFAULT_PERIOD);
  });
});
