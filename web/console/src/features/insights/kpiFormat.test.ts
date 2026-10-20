import i18n from '../../i18n';
import { changePercent, formatDay, formatHours, formatRatio, verdictOf } from './kpiFormat';

const t = i18n.getFixedT('en');

describe('changePercent', () => {
  it('is the relative change, and null when there is nothing to compare', () => {
    expect(changePercent(15, 10)).toBe(50);
    expect(changePercent(5, 10)).toBe(-50);
    expect(changePercent(0, 0)).toBe(0);
    expect(changePercent(3, 0)).toBeNull();
    expect(changePercent(null, 10)).toBeNull();
    expect(changePercent(10, null)).toBeNull();
  });
});

describe('verdictOf', () => {
  it('says whether a change is good for the metric, in either direction', () => {
    expect(verdictOf(-40, 'down')).toBe('better');
    expect(verdictOf(40, 'down')).toBe('worse');
    expect(verdictOf(40, 'up')).toBe('better');
    expect(verdictOf(-40, 'up')).toBe('worse');
  });
  it('calls a change that rounds to 0% no change, and a missing one unknown', () => {
    expect(verdictOf(0.4, 'up')).toBe('same');
    expect(verdictOf(-0.4, 'down')).toBe('same');
    expect(verdictOf(null, 'up')).toBe('unknown');
    expect(verdictOf(Number.NaN, 'up')).toBe('unknown');
  });
});

describe('formatRatio', () => {
  it('shows a fraction as a percentage with at most one decimal', () => {
    expect(formatRatio(0.943, 'en')).toBe('94.3%');
    expect(formatRatio(0.5, 'en')).toBe('50%');
    expect(formatRatio(0, 'en')).toBe('0%');
    expect(formatRatio(1, 'en')).toBe('100%');
  });
  it('never rounds a near miss up to 100%', () => {
    expect(formatRatio(0.9996, 'en')).toBe('99.9%');
  });
});

describe('formatHours', () => {
  it('uses minutes, hours or days by size', () => {
    expect(formatHours(0.75, t, 'en')).toBe('45 min');
    expect(formatHours(0.001, t, 'en')).toBe('1 min');
    expect(formatHours(5.5, t, 'en')).toBe('5.5 h');
    expect(formatHours(72, t, 'en')).toBe('3 d');
  });
});

describe('formatDay', () => {
  it('reads a date-only value as a UTC day so it never slips a day', () => {
    expect(formatDay('2026-10-01', 'en')).toBe('Oct 1, 2026');
  });
  it('passes an unreadable value through', () => {
    expect(formatDay('soon', 'en')).toBe('soon');
  });
});
