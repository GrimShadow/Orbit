import { ApiError } from '@dam/sdk-js';
import { CalendarCheck, PaperPlaneTilt, PencilSimple, Plus, Trash, Warning } from '@phosphor-icons/react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Badge, Button, EmptyState, Modal, Skeleton, useToast } from '@dam/ui-kit';
import { Switch } from '../webhooks/Switch';
import '../webhooks/webhooks.css';
import { useUserNames } from '../workflow/designer/designerApi';
import { useDeleteSchedule, useReportList, useSaveSchedule, useSchedules, useSendScheduleNow } from './insightsApi';
import type { ReportScheduleDto } from './insightsTypes';
import { describeFrequency, toDraft, toInput } from './schedule';
import { ScheduleForm } from './ScheduleForm';
import './insights.css';

const reasonOf = (e: unknown) => (e instanceof ApiError ? e.detail || e.title : '');

/** Scheduled reports (MIS): who is emailed which report, how often. Only for people holding `analytics.manage`; the page decides
 * that and mounts this section, so it never asks the API on behalf of someone who cannot see it. */
export function SchedulesSection() {
  const { t, i18n } = useTranslation();
  const { push } = useToast();
  const query = useSchedules(true);
  const reports = useReportList();
  const names = useUserNames((query.data ?? []).flatMap((s) => s.recipientUserIds));
  const save = useSaveSchedule();
  const remove = useDeleteSchedule();
  const sendNow = useSendScheduleNow();
  const [form, setForm] = useState<{ schedule: ReportScheduleDto | null; key: number } | null>(null);
  const [toDelete, setToDelete] = useState<ReportScheduleDto | null>(null);
  const [deleteError, setDeleteError] = useState<string | null>(null);
  const [sendingId, setSendingId] = useState<string | null>(null);

  const titleOf = (name: string) => reports.data?.find((r) => r.name === name)?.title ?? name;
  const when = (iso: string) =>
    new Intl.DateTimeFormat(i18n.language, { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(iso));
  const list = query.data ?? [];

  const toggle = (s: ReportScheduleDto, enabled: boolean) =>
    save.mutate(
      { id: s.id, body: { ...toInput(toDraft(s)), enabled } },
      { onError: (e) => push({ title: t('insights.schedules.saveFailed', { reason: reasonOf(e) }), tone: 'error' }) },
    );

  const send = (s: ReportScheduleDto) => {
    setSendingId(s.id);
    sendNow.mutate(s.id, {
      onSuccess: () => push({ title: t('insights.schedules.sent', { report: titleOf(s.report) }), tone: 'success' }),
      onError: (e) => push({ title: t('insights.schedules.sendFailed', { reason: reasonOf(e) }), tone: 'error' }),
      onSettled: () => setSendingId(null),
    });
  };

  const closeDelete = () => {
    setToDelete(null);
    setDeleteError(null);
  };

  const add = (
    <Button variant="primary" onClick={() => setForm((f) => ({ schedule: null, key: (f?.key ?? 0) + 1 }))}>
      <Plus size={16} weight="bold" aria-hidden="true" />
      {t('insights.schedules.add')}
    </Button>
  );

  let body;
  if (query.isPending) {
    body = (
      <div role="status" aria-live="polite">
        <span className="dam-sr-only">{t('insights.schedules.loading')}</span>
        <div className="dam-stack" aria-hidden="true">
          <Skeleton height={64} />
          <Skeleton height={64} />
        </div>
      </div>
    );
  } else if (query.error) {
    body = (
      <div role="alert">
        <EmptyState
          titleAs="h3"
          icon={<Warning size={28} />}
          title={t('insights.schedules.loadFailed')}
          description={t('insights.schedules.loadFailedHint')}
          action={<Button onClick={() => void query.refetch()}>{t('insights.retry')}</Button>}
        />
      </div>
    );
  } else if (list.length === 0) {
    body = (
      <EmptyState
        titleAs="h3"
        icon={<CalendarCheck size={28} />}
        title={t('insights.schedules.emptyTitle')}
        description={t('insights.schedules.emptyBody')}
        action={add}
      />
    );
  } else {
    body = (
      <ul className="dam-sched__list" aria-label={t('insights.schedules.listLabel')}>
        {list.map((s) => {
          const recipients = s.recipientUserIds.map((id) => names.nameOf(id));
          return (
            <li key={s.id} className="dam-sched">
              <div className="dam-sched__main">
                <div className="dam-sched__head">
                  <strong className="dam-sched__title">{titleOf(s.report)}</strong>
                  <Badge size="sm" tone={s.enabled ? 'success' : 'neutral'}>
                    {s.enabled ? t('insights.schedules.on') : t('insights.schedules.off')}
                  </Badge>
                </div>
                <p className="dam-sched__when">
                  {describeFrequency(s, t, i18n.language)} · {t('insights.schedules.coversDays', { count: s.days })}
                </p>
                <dl className="dam-sched__facts">
                  <div>
                    <dt>{t('insights.schedules.recipients')}</dt>
                    <dd>{recipients.join(', ')}</dd>
                  </div>
                  <div>
                    <dt>{t('insights.schedules.lastSent')}</dt>
                    <dd>{s.lastSentAt ? when(s.lastSentAt) : t('insights.schedules.neverSent')}</dd>
                  </div>
                  <div>
                    <dt>{t('insights.schedules.nextRun')}</dt>
                    <dd>{s.enabled && s.nextRunAt ? when(s.nextRunAt) : t('insights.schedules.notScheduled')}</dd>
                  </div>
                </dl>
                {s.lastError && (
                  <p className="dam-sched__error" role="note">
                    <Warning size={14} weight="bold" aria-hidden="true" />
                    <span>
                      <strong>{t('insights.schedules.lastError')}</strong> {s.lastError}
                    </span>
                  </p>
                )}
              </div>
              <div className="dam-sched__actions">
                <Switch
                  checked={s.enabled}
                  onChange={(next) => toggle(s, next)}
                  label={t('insights.schedules.enabled')}
                  ariaLabel={t('insights.schedules.toggle', { report: titleOf(s.report) })}
                />
                <Button
                  size="sm"
                  variant="secondary"
                  loading={sendingId === s.id}
                  aria-label={t('insights.schedules.sendNowFor', { report: titleOf(s.report) })}
                  onClick={() => send(s)}
                >
                  <PaperPlaneTilt size={14} weight="bold" aria-hidden="true" />
                  {t('insights.schedules.sendNow')}
                </Button>
                <button
                  type="button"
                  className="dam-btn dam-btn--ghost dam-btn--sm dam-btn--icon"
                  aria-label={t('insights.schedules.edit', { report: titleOf(s.report) })}
                  title={t('insights.schedules.edit', { report: titleOf(s.report) })}
                  onClick={() => setForm((f) => ({ schedule: s, key: (f?.key ?? 0) + 1 }))}
                >
                  <PencilSimple size={16} aria-hidden="true" />
                </button>
                <button
                  type="button"
                  className="dam-btn dam-btn--ghost dam-btn--sm dam-btn--icon"
                  aria-label={t('insights.schedules.delete', { report: titleOf(s.report) })}
                  title={t('insights.schedules.delete', { report: titleOf(s.report) })}
                  onClick={() => setToDelete(s)}
                >
                  <Trash size={16} aria-hidden="true" />
                </button>
              </div>
            </li>
          );
        })}
      </ul>
    );
  }

  return (
    <section className="dam-ins__section" aria-labelledby="dam-schedules-title">
      <div className="dam-ins__sectionhead">
        <div>
          <h2 id="dam-schedules-title" className="dam-ins__h2">
            {t('insights.schedules.title')}
          </h2>
          <p className="dam-hint">{t('insights.schedules.intro')}</p>
        </div>
        {list.length > 0 && add}
      </div>
      {body}

      {form && (
        <ScheduleForm
          key={form.key}
          schedule={form.schedule}
          onClose={() => setForm(null)}
          onSaved={(_, created) => {
            setForm(null);
            push({ title: t(created ? 'insights.schedules.created' : 'insights.schedules.saved'), tone: 'success' });
          }}
        />
      )}

      <Modal
        open={toDelete !== null}
        title={t('insights.schedules.deleteTitle')}
        closeLabel={t('insights.close')}
        onClose={closeDelete}
      >
        <div className="dam-stack dam-insform">
          <p>{t('insights.schedules.deleteBody', { report: toDelete ? titleOf(toDelete.report) : '' })}</p>
          {deleteError && (
            <p className="dam-error" role="alert">
              {deleteError}
            </p>
          )}
          <div className="dam-insform__actions">
            <Button variant="ghost" onClick={closeDelete}>
              {t('insights.cancel')}
            </Button>
            <Button
              variant="danger"
              loading={remove.isPending}
              onClick={() => {
                const gone = toDelete!;
                remove.mutate(gone.id, {
                  onSuccess: () => {
                    closeDelete();
                    push({ title: t('insights.schedules.deleted'), tone: 'success' });
                  },
                  onError: (e) => setDeleteError(t('insights.schedules.deleteFailed', { reason: reasonOf(e) })),
                });
              }}
            >
              {t('insights.schedules.deleteConfirm')}
            </Button>
          </div>
        </div>
      </Modal>
    </section>
  );
}
