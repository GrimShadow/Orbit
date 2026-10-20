import { Sparkle, WarningCircle } from '@phosphor-icons/react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../../../api/client';
import { Button, Card, CheckboxGroup, Input, Modal, Select, useToast } from '../../../ui';
import { folderOptions, useFolders } from '../../library/folders';
import { useAiBackfill, type AiSettings, type AiStatus } from '../aiApi';
import {
  BACKFILL_MAX_LIMIT,
  BACKFILL_TYPES,
  backfillBody,
  estimateCount,
  estimateSeconds,
  filtered,
  initialBackfill,
  parseLimit,
  type BackfillForm,
} from './aiAdminModel';
import { useProviderName } from './StatusCard';

interface BackfillCardProps {
  /** The saved settings: the server refuses (409) while AI is off. */
  settings: AiSettings;
  status: AiStatus | undefined;
}

/** "Analyse existing assets": queue AI for what was uploaded before it was switched on. It processes real content, so a
 * confirmation always comes first. */
export function BackfillCard({ settings, status }: BackfillCardProps) {
  const { t, i18n } = useTranslation();
  const { push } = useToast();
  const providerName = useProviderName();
  const backfill = useAiBackfill();
  const folders = useFolders();
  const [form, setForm] = useState<BackfillForm>(initialBackfill);
  const [confirming, setConfirming] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);
  const nf = new Intl.NumberFormat(i18n.language);

  const patch = (p: Partial<BackfillForm>) => setForm((f) => ({ ...f, ...p }));
  const limit = parseLimit(form.limit);
  const limitInvalid = limit === null;
  const count = estimateCount(form, status);
  const off = !settings.enabled;
  const canStart = !off && !limitInvalid && form.types.length > 0 && count !== 0;

  const duration = (seconds: number) => {
    if (seconds < 60) return t('aiAdmin.backfill.timeUnderMinute');
    const minutes = Math.ceil(seconds / 60);
    if (minutes < 90) return t('aiAdmin.backfill.timeMinutes', { count: minutes });
    return t('aiAdmin.backfill.timeHours', { hours: Math.floor(minutes / 60), minutes: minutes % 60 });
  };

  let estimate: string;
  if (count === null) estimate = t('aiAdmin.backfill.estimateUnknown');
  else if (count === 0) estimate = t('aiAdmin.backfill.estimateNone');
  else
    estimate = t(filtered(form) ? 'aiAdmin.backfill.estimateUpTo' : 'aiAdmin.backfill.estimate', {
      count,
      formattedCount: nf.format(count),
      time: duration(estimateSeconds(count)),
    });

  const open = () => {
    setFailure(null);
    setConfirming(true);
  };
  const close = () => {
    if (!backfill.isPending) setConfirming(false);
  };
  const start = () => {
    setFailure(null);
    backfill.mutate(backfillBody(form), {
      onSuccess: (r) => {
        setConfirming(false);
        push({
          title: t('aiAdmin.backfill.queued', { count: r.enqueued, formattedCount: nf.format(r.enqueued) }),
          message: t('aiAdmin.backfill.queuedBody', { matched: r.matched, enqueued: r.enqueued, remaining: r.remaining }),
          tone: 'success',
        });
      },
      onError: (e) =>
        setFailure(e instanceof ApiError && (e.detail || e.title) ? (e.detail ?? e.title) : t('aiAdmin.backfill.failedHint')),
    });
  };

  const provider = providerName(status?.provider ?? settings.effectiveProvider);
  const folderChoices = [{ value: '', label: t('aiAdmin.backfill.allFolders') }, ...folderOptions(folders.data ?? [])];
  const options = [
    ...(form.folderId ? [{ value: 'sub', label: t('aiAdmin.backfill.includeSub') }] : []),
    { value: 'missing', label: t('aiAdmin.backfill.onlyMissing') },
  ];
  const optionValue = [...(form.folderId && form.includeSubfolders ? ['sub'] : []), ...(form.onlyMissing ? ['missing'] : [])];

  return (
    <Card as="section" aria-labelledby="ai-backfill-title" className="dam-ai__card" padding="lg">
      <div className="dam-ai__head">
        <h2 id="ai-backfill-title" className="dam-ai__title">
          {t('aiAdmin.backfill.title')}
        </h2>
      </div>
      <p className="dam-ai__desc">{t('aiAdmin.backfill.description')}</p>

      {off && (
        <p className="dam-aicallout dam-aicallout--neutral" role="status">
          <WarningCircle size={22} weight="fill" aria-hidden="true" />
          <span>{t('aiAdmin.backfill.off')}</span>
        </p>
      )}

      <div className="dam-aibackfill">
        <CheckboxGroup
          label={t('aiAdmin.backfill.types')}
          hint={t('aiAdmin.backfill.typesHint')}
          options={BACKFILL_TYPES.map((v) => ({ value: v, label: t(`aiAdmin.backfill.type.${v}`) }))}
          value={form.types}
          onChange={(types) => patch({ types })}
          columns={2}
        />
        <Select
          label={t('aiAdmin.backfill.folder')}
          options={folderChoices}
          value={form.folderId}
          onChange={(e) => patch({ folderId: e.target.value })}
        />
        <CheckboxGroup
          label={t('aiAdmin.backfill.options')}
          hint={t('aiAdmin.backfill.onlyMissingHint')}
          options={options}
          value={optionValue}
          onChange={(next) =>
            patch({ includeSubfolders: next.includes('sub') || !form.folderId, onlyMissing: next.includes('missing') })
          }
        />
        <Input
          label={t('aiAdmin.backfill.limit')}
          hint={t('aiAdmin.backfill.limitHint', { max: nf.format(BACKFILL_MAX_LIMIT) })}
          error={limitInvalid ? t('aiAdmin.backfill.limitInvalid', { max: nf.format(BACKFILL_MAX_LIMIT) }) : undefined}
          type="text"
          inputMode="numeric"
          value={form.limit}
          onChange={(e) => patch({ limit: e.target.value })}
        />
      </div>

      <p className="dam-ai__estimate" role="status">
        {estimate}
      </p>
      <div className="dam-ai__actions">
        <Button variant="primary" disabled={!canStart} onClick={open}>
          <Sparkle size={16} weight="bold" aria-hidden="true" />
          {t('aiAdmin.backfill.start')}
        </Button>
      </div>

      <Modal open={confirming} title={t('aiAdmin.backfill.confirmTitle')} onClose={close}>
        <div className="dam-aidialog">
          <p>
            <strong>{t('aiAdmin.backfill.confirmBody', { count: count ?? 0, formattedCount: nf.format(count ?? 0) })}</strong>
          </p>
          <p>{t('aiAdmin.backfill.confirmReal', { provider })}</p>
          {settings.sendsDataOffSite && (
            <p className="dam-aicallout dam-aicallout--warning">{t('aiAdmin.backfill.confirmOffSite')}</p>
          )}
          <p className="dam-hint">{estimate}</p>
          {failure && (
            <p className="dam-error" role="alert">
              <WarningCircle size={14} weight="bold" aria-hidden="true" />
              {failure}
            </p>
          )}
          <div className="dam-aidialog__actions">
            <Button variant="ghost" onClick={close} disabled={backfill.isPending}>
              {t('aiAdmin.backfill.cancel')}
            </Button>
            <Button variant="primary" loading={backfill.isPending} onClick={start}>
              {backfill.isPending ? t('aiAdmin.backfill.starting') : t('aiAdmin.backfill.confirm')}
            </Button>
          </div>
        </div>
      </Modal>
    </Card>
  );
}
