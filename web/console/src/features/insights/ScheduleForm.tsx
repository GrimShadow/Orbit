import { ApiError } from '@dam/sdk-js';
import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Input, Modal, Select } from '@dam/ui-kit';
import { Switch } from '../webhooks/Switch';
import '../webhooks/webhooks.css';
import { useReportList, useSaveSchedule } from './insightsApi';
import type { ReportScheduleDto } from './insightsTypes';
import { RecipientPicker } from './RecipientPicker';
import {
  DAYS_OF_MONTH,
  DEFAULT_MAX_DAYS,
  FREQUENCIES,
  HOURS,
  WEEKDAYS,
  emptyDraft,
  hourLabel,
  toDraft,
  toInput,
  validateDraft,
  weekdayName,
  type ScheduleDraft,
} from './schedule';
import './insights.css';

interface Props {
  /** The schedule being edited; `null` adds a new one. */
  schedule: ReportScheduleDto | null;
  onClose: () => void;
  onSaved: (saved: ReportScheduleDto, created: boolean) => void;
}

function Body({ schedule, onClose, onSaved }: Props) {
  const { t, i18n } = useTranslation();
  const reports = useReportList();
  const save = useSaveSchedule();
  const [draft, setDraft] = useState<ScheduleDraft>(() => (schedule ? toDraft(schedule) : emptyDraft()));
  const [attempted, setAttempted] = useState(false);
  const [serverError, setServerError] = useState<string | null>(null);

  const list = reports.data ?? [];
  // Until the list arrives the first report is chosen implicitly; an edited schedule keeps its own report even if the list lacks it.
  const report = draft.report || list[0]?.name || '';
  const effective = { ...draft, report };
  const maxDays = list.find((r) => r.name === report)?.maxDays ?? DEFAULT_MAX_DAYS;
  const errors = attempted ? validateDraft(effective, maxDays) : {};
  const set = (patch: Partial<ScheduleDraft>) => setDraft((d) => ({ ...d, ...patch }));
  const reportOptions = list.some((r) => r.name === report) || !report ? list : [...list, { name: report, title: report }];

  const submit = (e: FormEvent) => {
    e.preventDefault();
    setAttempted(true);
    setServerError(null);
    if (Object.keys(validateDraft(effective, maxDays)).length > 0) return;
    save.mutate(
      { id: schedule?.id, body: toInput(effective) },
      {
        onSuccess: (saved) => onSaved(saved, !schedule),
        onError: (err) =>
          setServerError(t('insights.schedules.saveFailed', { reason: err instanceof ApiError ? err.detail || err.title : '' })),
      },
    );
  };

  return (
    <form className="dam-stack dam-insform" onSubmit={submit} noValidate>
      <p className="dam-hint">{t('insights.schedules.formIntro')}</p>
      <Select
        label={t('insights.schedules.report')}
        value={report}
        error={errors.report ? t('insights.schedules.reportRequired') : undefined}
        disabled={reports.isPending}
        options={
          reports.isPending
            ? [{ value: '', label: t('insights.schedules.reportsLoading') }]
            : reportOptions.map((r) => ({ value: r.name, label: r.title }))
        }
        onChange={(e) => set({ report: e.target.value })}
      />
      <Input
        label={t('insights.schedules.days')}
        hint={t('insights.schedules.daysHint', { max: maxDays })}
        type="number"
        inputMode="numeric"
        min={1}
        max={maxDays}
        value={draft.days}
        error={errors.days ? t('insights.schedules.daysInvalid', { max: maxDays }) : undefined}
        onChange={(e) => set({ days: e.target.value })}
      />
      <div className="dam-insform__row">
        <Select
          label={t('insights.schedules.frequency')}
          value={draft.frequency}
          options={FREQUENCIES.map((f) => ({ value: f, label: t(`insights.schedules.frequency_${f}`) }))}
          onChange={(e) => set({ frequency: e.target.value as ScheduleDraft['frequency'] })}
        />
        {draft.frequency === 'weekly' && (
          <Select
            label={t('insights.schedules.weekday')}
            value={String(draft.dayOfWeek)}
            options={WEEKDAYS.map((d) => ({ value: String(d), label: weekdayName(d, i18n.language) }))}
            onChange={(e) => set({ dayOfWeek: Number(e.target.value) })}
          />
        )}
        {draft.frequency === 'monthly' && (
          <Select
            label={t('insights.schedules.dayOfMonth')}
            hint={t('insights.schedules.dayOfMonthHint')}
            value={String(draft.dayOfMonth)}
            options={DAYS_OF_MONTH.map((d) => ({ value: String(d), label: String(d) }))}
            onChange={(e) => set({ dayOfMonth: Number(e.target.value) })}
          />
        )}
        <Select
          label={t('insights.schedules.hour')}
          value={String(draft.hourUtc)}
          options={HOURS.map((h) => ({ value: String(h), label: `${hourLabel(h)} UTC` }))}
          onChange={(e) => set({ hourUtc: Number(e.target.value) })}
        />
      </div>
      <RecipientPicker
        value={draft.recipientUserIds}
        onChange={(ids) => set({ recipientUserIds: ids })}
        error={errors.recipients ? t('insights.schedules.recipientsRequired') : undefined}
      />
      <Switch
        checked={draft.enabled}
        onChange={(enabled) => set({ enabled })}
        label={t('insights.schedules.enabled')}
        hint={t('insights.schedules.enabledHint')}
      />
      {serverError && (
        <p className="dam-error" role="alert">
          {serverError}
        </p>
      )}
      <div className="dam-insform__actions">
        <Button variant="ghost" onClick={onClose}>
          {t('insights.cancel')}
        </Button>
        <Button type="submit" variant="primary" loading={save.isPending}>
          {schedule ? t('insights.schedules.save') : t('insights.schedules.create')}
        </Button>
      </div>
    </form>
  );
}

/** Create or edit dialog for one report schedule. The form remounts per open (the page keys it), so a draft never leaks
 * from one schedule into the next. */
export function ScheduleForm(props: Props) {
  const { t } = useTranslation();
  return (
    <Modal
      open
      title={props.schedule ? t('insights.schedules.editTitle') : t('insights.schedules.newTitle')}
      closeLabel={t('insights.close')}
      onClose={props.onClose}
    >
      <Body {...props} />
    </Modal>
  );
}
