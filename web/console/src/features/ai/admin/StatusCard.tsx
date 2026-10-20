import { ArrowsClockwise, CheckCircle, MinusCircle, WarningCircle } from '@phosphor-icons/react';
import { useTranslation } from 'react-i18next';
import { Badge, Button, Card, DistributionBar, EmptyState, Skeleton } from '../../../ui';
import type { AiStatus } from '../aiApi';
import { CAPABILITIES, healthTone } from './aiAdminModel';

interface StatusCardProps {
  status: AiStatus | undefined;
  loading: boolean;
  failed: boolean;
  fetching: boolean;
  onRefresh: () => void;
  /** A rebuild was asked for and has not finished. */
  rebuilding: boolean;
  rebuildBusy: boolean;
  onRebuild: () => void;
}

/** The provider's name in the reader's language (unknown kinds are shown as the server names them). */
export function useProviderName() {
  const { t } = useTranslation();
  return (kind: string) => t(`aiAdmin.providerNames.${kind}`, { defaultValue: kind });
}

/** Provider in use, its health, what it can do, how far the library has got and whether Smart search's index is current. */
export function StatusCard({
  status,
  loading,
  failed,
  fetching,
  onRefresh,
  rebuilding,
  rebuildBusy,
  onRebuild,
}: StatusCardProps) {
  const { t, i18n } = useTranslation();
  const nf = new Intl.NumberFormat(i18n.language);

  let body;
  if (loading) {
    body = (
      <div role="status" aria-live="polite">
        <span className="dam-sr-only">{t('aiAdmin.status.loading')}</span>
        <div className="dam-ai__skeleton" aria-hidden="true">
          <Skeleton height={44} />
          <Skeleton height={32} />
          <Skeleton height={64} />
        </div>
      </div>
    );
  } else if (failed || !status) {
    body = (
      <div role="alert">
        <EmptyState
          icon={<WarningCircle size={28} />}
          title={t('aiAdmin.status.failed')}
          description={t('aiAdmin.status.failedHint')}
          titleAs="h3"
          action={<Button onClick={onRefresh}>{t('aiAdmin.retry')}</Button>}
        />
      </div>
    );
  } else {
    body = <StatusBody status={status} nf={nf} rebuilding={rebuilding} rebuildBusy={rebuildBusy} onRebuild={onRebuild} />;
  }

  return (
    <Card as="section" aria-labelledby="ai-status-title" className="dam-ai__card" padding="lg">
      <div className="dam-ai__head">
        <h2 id="ai-status-title" className="dam-ai__title">
          {t('aiAdmin.status.title')}
        </h2>
        <Button size="sm" variant="secondary" loading={fetching && !loading} onClick={onRefresh}>
          {!(fetching && !loading) && <ArrowsClockwise size={14} weight="bold" aria-hidden="true" />}
          {t('aiAdmin.status.refresh')}
        </Button>
      </div>
      {body}
    </Card>
  );
}

function StatusBody({
  status,
  nf,
  rebuilding,
  rebuildBusy,
  onRebuild,
}: {
  status: AiStatus;
  nf: Intl.NumberFormat;
  rebuilding: boolean;
  rebuildBusy: boolean;
  onRebuild: () => void;
}) {
  const { t } = useTranslation();
  const providerName = useProviderName();
  const { health, capabilities, counts, vectorIndex } = status;
  const models = Object.entries(health.models ?? {});
  const total = counts.eligible;
  const healthLabel = t(`aiAdmin.status.healthLabels.${health.status}`, { defaultValue: health.status });

  const segments = [
    { label: t('aiAdmin.status.counts.enriched'), value: counts.enriched, series: 0, display: nf.format(counts.enriched) },
    { label: t('aiAdmin.status.counts.running'), value: counts.running, series: 3, display: nf.format(counts.running) },
    { label: t('aiAdmin.status.counts.pending'), value: counts.pending, series: 1, display: nf.format(counts.pending) },
    { label: t('aiAdmin.status.counts.failed'), value: counts.failed, series: 2, display: nf.format(counts.failed) },
    { label: t('aiAdmin.status.counts.skipped'), value: counts.skipped, series: 4, display: nf.format(counts.skipped) },
    { label: t('aiAdmin.status.counts.notStarted'), value: counts.notStarted, series: 5, display: nf.format(counts.notStarted) },
  ];

  return (
    <div className="dam-ai__statusbody">
      {!status.enabled && (
        <div className="dam-aicallout dam-aicallout--neutral" role="status">
          <MinusCircle size={22} weight="fill" aria-hidden="true" />
          <div>
            <strong>{t('aiAdmin.status.offTitle')}</strong>
            <p>{t('aiAdmin.status.offBody')}</p>
          </div>
        </div>
      )}

      {vectorIndex.needsReindex && (
        <div className="dam-aicallout dam-aicallout--warning" role="alert">
          <WarningCircle size={22} weight="fill" aria-hidden="true" />
          <div>
            <strong>{t('aiAdmin.status.reindexTitle')}</strong>
            <p>{t('aiAdmin.status.reindexBody')}</p>
          </div>
          <Button variant="primary" loading={rebuildBusy || rebuilding} onClick={onRebuild}>
            {rebuildBusy || rebuilding ? t('aiAdmin.status.reindexing') : t('aiAdmin.status.reindexButton')}
          </Button>
        </div>
      )}

      <dl className="dam-ai__facts">
        <div>
          <dt>{t('aiAdmin.status.provider')}</dt>
          <dd>{providerName(status.provider)}</dd>
        </div>
        <div>
          <dt>{t('aiAdmin.status.health')}</dt>
          <dd className="dam-ai__healthrow">
            <Badge tone={healthTone(health.status)}>{healthLabel}</Badge>
            {health.latencyMs != null && (
              <span className="dam-hint" title={t('aiAdmin.status.latencyLabel', { ms: health.latencyMs })}>
                {t('aiAdmin.status.latency', { ms: nf.format(health.latencyMs) })}
              </span>
            )}
          </dd>
        </div>
        {models.length > 0 && (
          <div className="dam-ai__models">
            <dt>{t('aiAdmin.status.models')}</dt>
            <dd>
              <ul className="dam-ai__modellist">
                {models.map(([key, name]) => (
                  <li key={key}>
                    <span className="dam-hint">{t(`aiAdmin.status.modelNames.${key}`, { defaultValue: key })}</span>
                    <code>{name}</code>
                  </li>
                ))}
              </ul>
            </dd>
          </div>
        )}
      </dl>
      {health.detail && health.status !== 'ok' && <p className="dam-hint dam-ai__detail">{health.detail}</p>}

      <section aria-labelledby="ai-cap-title" className="dam-ai__block">
        <h3 id="ai-cap-title" className="dam-ai__sub">
          {t('aiAdmin.status.capabilities')}
        </h3>
        <ul className="dam-ai__chips">
          {CAPABILITIES.map(({ key, later }) => {
            const on = capabilities[key];
            const state = later ? t('aiAdmin.status.capLater') : on ? t('aiAdmin.status.capYes') : t('aiAdmin.status.capNo');
            return (
              <li key={key}>
                <Badge
                  tone={later ? 'neutral' : on ? 'success' : 'neutral'}
                  className={on && !later ? undefined : 'dam-ai__chip--off'}
                >
                  {on && !later ? (
                    <CheckCircle size={14} weight="fill" aria-hidden="true" />
                  ) : (
                    <MinusCircle size={14} weight="regular" aria-hidden="true" />
                  )}
                  {t(`aiAdmin.status.cap.${key}`)}
                  {later ? <span className="dam-ai__chipnote"> ({state})</span> : <span className="dam-sr-only">: {state}</span>}
                </Badge>
              </li>
            );
          })}
        </ul>
      </section>

      <section aria-labelledby="ai-progress-title" className="dam-ai__block">
        <h3 id="ai-progress-title" className="dam-ai__sub">
          {t('aiAdmin.status.progress')}
        </h3>
        {total === 0 ? (
          <p className="dam-hint">{t('aiAdmin.status.progressNone')}</p>
        ) : (
          <>
            <p className="dam-ai__summary">
              {t('aiAdmin.status.progressSummary', { enriched: nf.format(counts.enriched), eligible: nf.format(total) })}
            </p>
            <DistributionBar label={t('aiAdmin.status.progress')} segments={segments} compact />
          </>
        )}
        <p className="dam-hint dam-ai__queue" role="status">
          {status.queueDepth > 0
            ? t('aiAdmin.status.queue', { count: status.queueDepth, formattedCount: nf.format(status.queueDepth) })
            : t('aiAdmin.status.queueEmpty')}
        </p>
      </section>

      <section aria-labelledby="ai-index-title" className="dam-ai__block">
        <h3 id="ai-index-title" className="dam-ai__sub">
          {t('aiAdmin.status.index')}
          {!vectorIndex.needsReindex && (
            <Badge tone="success" size="sm" className="dam-ai__indexbadge">
              <CheckCircle size={12} weight="fill" aria-hidden="true" />
              {t('aiAdmin.status.indexOk')}
            </Badge>
          )}
        </h3>
        <p className="dam-hint">
          {t('aiAdmin.status.indexDims', { text: nf.format(vectorIndex.textDim), image: nf.format(vectorIndex.imageDim) })}
        </p>
        {vectorIndex.needsReindex && (
          <p className="dam-hint">
            {t('aiAdmin.status.indexLive', {
              text: vectorIndex.liveTextDim != null ? nf.format(vectorIndex.liveTextDim) : t('aiAdmin.status.indexUnknown'),
              image: vectorIndex.liveImageDim != null ? nf.format(vectorIndex.liveImageDim) : t('aiAdmin.status.indexUnknown'),
            })}
          </p>
        )}
      </section>
    </div>
  );
}
