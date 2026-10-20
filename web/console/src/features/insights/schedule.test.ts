import i18n from '../../i18n';
import { schedule } from './insightsTestKit';
import { describeFrequency, emptyDraft, toDraft, toInput, validateDraft, weekdayName } from './schedule';
import { parseDays } from './useInsightsPeriod';

const t = i18n.getFixedT('en');

describe('describeFrequency', () => {
  it('says daily, weekly and monthly schedules in words, in UTC', () => {
    expect(describeFrequency(schedule({ frequency: 'daily', dayOfWeek: null }), t, 'en')).toBe('Every day at 08:00 UTC');
    expect(describeFrequency(schedule({ frequency: 'weekly', dayOfWeek: 1, hourUtc: 8 }), t, 'en')).toBe(
      'Every Monday at 08:00 UTC',
    );
    expect(describeFrequency(schedule({ frequency: 'monthly', dayOfWeek: null, dayOfMonth: 15, hourUtc: 17 }), t, 'en')).toBe(
      'On day 15 of every month at 17:00 UTC',
    );
  });
  it("names the weekday in the reader's language and reads 7 as Sunday", () => {
    expect(weekdayName(1, 'en')).toBe('Monday');
    expect(weekdayName(7, 'en')).toBe('Sunday');
    expect(weekdayName(0, 'en')).toBe('Sunday');
    expect(weekdayName(6, 'en')).toBe('Saturday');
    expect(weekdayName(1, 'hi')).toBe('सोमवार');
  });
});

describe('schedule draft', () => {
  it('needs a report, 1-365 days and at least one recipient', () => {
    expect(validateDraft(emptyDraft())).toEqual({ report: true, recipients: true });
    const ok = { ...emptyDraft('kpis'), recipientUserIds: ['u1'] };
    expect(validateDraft(ok)).toEqual({});
    expect(validateDraft({ ...ok, days: '0' }).days).toBe(true);
    expect(validateDraft({ ...ok, days: '366' }).days).toBe(true);
    expect(validateDraft({ ...ok, days: '1.5' }).days).toBe(true);
    expect(validateDraft({ ...ok, days: '' }).days).toBe(true);
    expect(validateDraft({ ...ok, days: '365' })).toEqual({});
    // Unused assets can look back further, as the report says.
    expect(validateDraft({ ...ok, days: '1000' }, 3650)).toEqual({});
    expect(validateDraft({ ...ok, days: '3651' }, 3650).days).toBe(true);
  });
  it('sends only the day field that belongs to the frequency', () => {
    const base = { ...emptyDraft('kpis'), recipientUserIds: ['u1'], dayOfWeek: 3, dayOfMonth: 9 };
    expect(base.dayOfWeek).toBe(3);
    expect(toInput({ ...base, frequency: 'daily' })).toMatchObject({ dayOfWeek: null, dayOfMonth: null });
    expect(toInput({ ...base, frequency: 'weekly' })).toMatchObject({ dayOfWeek: 3, dayOfMonth: null });
    expect(toInput({ ...base, frequency: 'monthly' })).toMatchObject({ dayOfWeek: null, dayOfMonth: 9 });
  });
  it('round-trips a stored schedule', () => {
    const s = schedule();
    expect(toInput(toDraft(s))).toEqual({
      report: s.report,
      days: s.days,
      frequency: s.frequency,
      hourUtc: s.hourUtc,
      dayOfWeek: s.dayOfWeek,
      dayOfMonth: null,
      recipientUserIds: s.recipientUserIds,
      enabled: s.enabled,
    });
  });
});

describe('parseDays', () => {
  it('accepts whole days from 1 to 365 only', () => {
    expect(parseDays('90')).toBe(90);
    expect(parseDays('365')).toBe(365);
    expect(parseDays('0')).toBeNull();
    expect(parseDays('366')).toBeNull();
    expect(parseDays('1.5')).toBeNull();
    expect(parseDays('abc')).toBeNull();
    expect(parseDays(null)).toBeNull();
  });
});
