import type { TFunction } from 'i18next';
import type { ReportScheduleDto, ReportScheduleInput, ScheduleFrequency } from './insightsTypes';

export const FREQUENCIES: ScheduleFrequency[] = ['daily', 'weekly', 'monthly'];
export const DAYS_OF_MONTH = Array.from({ length: 31 }, (_, i) => i + 1);
export const HOURS = Array.from({ length: 24 }, (_, i) => i);
/** Weekdays as the API numbers them: 1 (Monday) to 7 (Sunday). */
export const WEEKDAYS = [1, 2, 3, 4, 5, 6, 7];

/** The weekday's name in the reader's language (1 = Monday ... 7 = Sunday; 0 is read as Sunday too). */
export function weekdayName(dayOfWeek: number, locale: string): string {
  // 2024-01-01 was a Monday.
  const d = new Date(Date.UTC(2024, 0, dayOfWeek === 0 ? 7 : dayOfWeek));
  return new Intl.DateTimeFormat(locale, { weekday: 'long', timeZone: 'UTC' }).format(d);
}

export const hourLabel = (hour: number) => `${String(hour).padStart(2, '0')}:00`;

/** "Every day at 08:00 UTC", "Every Monday at 08:00 UTC", "On day 15 of every month at 08:00 UTC". */
export function describeFrequency(
  s: Pick<ReportScheduleDto, 'frequency' | 'hourUtc' | 'dayOfWeek' | 'dayOfMonth'>,
  t: TFunction,
  locale: string,
): string {
  const time = hourLabel(s.hourUtc);
  if (s.frequency === 'weekly' && s.dayOfWeek !== null)
    return t('insights.schedules.freqWeekly', { day: weekdayName(s.dayOfWeek, locale), time });
  if (s.frequency === 'monthly' && s.dayOfMonth !== null) return t('insights.schedules.freqMonthly', { day: s.dayOfMonth, time });
  return t('insights.schedules.freqDaily', { time });
}

export interface ScheduleDraft {
  report: string;
  days: string;
  frequency: ScheduleFrequency;
  hourUtc: number;
  dayOfWeek: number;
  dayOfMonth: number;
  recipientUserIds: string[];
  enabled: boolean;
}

export function emptyDraft(report = ''): ScheduleDraft {
  return {
    report,
    days: '30',
    frequency: 'weekly',
    hourUtc: 8,
    dayOfWeek: 1,
    dayOfMonth: 1,
    recipientUserIds: [],
    enabled: true,
  };
}

export function toDraft(s: ReportScheduleDto): ScheduleDraft {
  return {
    report: s.report,
    days: String(s.days),
    frequency: s.frequency,
    hourUtc: s.hourUtc,
    dayOfWeek: s.dayOfWeek ?? 1,
    dayOfMonth: s.dayOfMonth ?? 1,
    recipientUserIds: s.recipientUserIds,
    enabled: s.enabled,
  };
}

export type DraftErrors = Partial<Record<'report' | 'days' | 'recipients', true>>;

/** The most days a report can cover, 365 unless the report says otherwise (unused assets: 3650). */
export const DEFAULT_MAX_DAYS = 365;

export function validateDraft(d: ScheduleDraft, maxDays = DEFAULT_MAX_DAYS): DraftErrors {
  const errors: DraftErrors = {};
  if (!d.report) errors.report = true;
  const days = Number(d.days);
  if (!/^\d{1,4}$/.test(d.days.trim()) || days < 1 || days > maxDays) errors.days = true;
  if (d.recipientUserIds.length === 0) errors.recipients = true;
  return errors;
}

/** Only the field that belongs to the chosen frequency is sent; the other is `null`. */
export function toInput(d: ScheduleDraft): ReportScheduleInput {
  return {
    report: d.report,
    days: Number(d.days),
    frequency: d.frequency,
    hourUtc: d.hourUtc,
    dayOfWeek: d.frequency === 'weekly' ? d.dayOfWeek : null,
    dayOfMonth: d.frequency === 'monthly' ? d.dayOfMonth : null,
    recipientUserIds: d.recipientUserIds,
    enabled: d.enabled,
  };
}
