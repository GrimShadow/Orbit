import { CheckCircle, MinusCircle, Warning, XCircle } from '@phosphor-icons/react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { Badge, Button, type BadgeTone } from '../../ui';
import { languageName } from './languages';
import { reviewHref } from './reviewLink';
import { useTranslationJobs } from './translationApi';
import { jobActive, type TranslationItem } from './translationTypes';

const TONE: Record<string, BadgeTone> = { ok: 'success', skipped: 'warning', failed: 'danger' };
const ICON = { ok: CheckCircle, skipped: MinusCircle, failed: XCircle } as const;

function Item({ item, label }: { item: TranslationItem; label: string }) {
  const { t, i18n } = useTranslation();
  const Icon = ICON[item.status] ?? Warning;
  return (
    <li className="dam-tjob__item" data-status={item.status}>
      <Icon size={16} weight="fill" aria-hidden="true" />
      <span className="dam-tjob__what">
        {t('translation.job.itemWhat', { field: label, language: languageName(item.language, i18n.language) })}
      </span>
      <Badge tone={TONE[item.status] ?? 'neutral'} size="sm">
        {t(`translation.job.item.${item.status}`, { defaultValue: item.status })}
      </Badge>
      {item.reason && <span className="dam-hint dam-tjob__reason">{item.reason}</span>}
    </li>
  );
}

/** Where a translate request has got to: polls the asset's jobs while the job runs and lists, per field and language,
 * what was translated, skipped (with the reason) or failed (with the reason). */
export function JobProgress({
  assetId,
  assetType,
  jobId,
  fieldLabels,
  onReview,
}: {
  assetId: string;
  assetType: string;
  jobId: string;
  fieldLabels: Record<string, string>;
  /** Called when the person follows the link to the suggestions (the dialog closes). */
  onReview: () => void;
}) {
  const { t } = useTranslation();
  const jobs = useTranslationJobs(assetId);
  const job = jobs.data?.jobs.find((j) => j.id === jobId);

  if (jobs.error && !job)
    return (
      <div role="alert" className="dam-tjob__wait">
        <span className="dam-error">{t('translation.job.loadFailed')}</span>
        <Button size="sm" variant="secondary" onClick={() => void jobs.refetch()}>
          {t('translation.job.retry')}
        </Button>
      </div>
    );
  // The job is created before this view shows, but the list may not have caught up for a moment.
  if (!job || jobActive(job))
    return (
      <div role="status" aria-live="polite" className="dam-tjob__wait">
        <span className="dam-spinner" aria-hidden="true" />
        <span>{job?.status === 'running' ? t('translation.job.running') : t('translation.job.pending')}</span>
        <span className="dam-hint">{t('translation.job.keepWorking')}</span>
      </div>
    );

  const ok = job.items.filter((i) => i.status === 'ok').length;
  return (
    <div className="dam-tjob">
      <p role="status" aria-live="polite" className="dam-tjob__summary" data-status={job.status}>
        {t(`translation.job.done.${job.status}`, { defaultValue: t('translation.job.done.succeeded'), count: ok })}
      </p>
      {job.error && (
        <p className="dam-error" role="alert">
          {job.error}
        </p>
      )}
      <ul className="dam-tjob__items" aria-label={t('translation.job.results')}>
        {job.items.map((item) => (
          <Item key={`${item.field}:${item.language}`} item={item} label={fieldLabels[item.field] ?? item.field} />
        ))}
      </ul>
      {ok > 0 && (
        <p>
          <Link to={reviewHref(assetId, assetType)} className="dam-btn dam-btn--primary" onClick={onReview}>
            {t('translation.job.review')}
          </Link>
        </p>
      )}
    </div>
  );
}
