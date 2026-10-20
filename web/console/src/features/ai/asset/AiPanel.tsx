import { CloudArrowUp, HardDrives, MagnifyingGlass, Sparkle, WarningCircle } from '@phosphor-icons/react';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useMe } from '../../../api/me';
import { Badge, Button, Card, Skeleton, useToast, type BadgeTone } from '../../../ui';
import { TranslateAssetButton } from '../../translation/TranslateAssetButton';
import { errorMessage, type AssetDto } from '../../asset/assetApi';
import { useAiCapabilities, useAssetAi, useEnrichAsset, type AssetAi } from '../aiApi';
import { OcrBlock } from './OcrBlock';
import { RunDetails } from './RunDetails';
import { SummaryBlock } from './SummaryBlock';
import { Suggestions } from './Suggestions';
import { TranscriptView } from './TranscriptView';
import { AI_TYPES, expectedSteps, hasAiContent } from './aiFormat';
import { useDeepLinkTime } from './useDeepLinkSeek';
import './aiAsset.css';

/** The ids the header's "AI suggestions" chip scrolls to and focuses. */
export const AI_PANEL_ID = 'dam-ai';
export const AI_TITLE_ID = 'dam-ai-title';

/** How long after a file arrives the panel keeps checking whether its analysis has started, and how often. */
const NEW_FILE_WINDOW_MS = 2 * 60_000;
const NEW_FILE_POLL_MS = 4_000;

const TONE: Record<string, BadgeTone> = {
  ready: 'success',
  pending: 'info',
  running: 'info',
  failed: 'danger',
  none: 'neutral',
  disabled: 'neutral',
};

/** Calm progress while the run is waiting or working: the page refreshes itself every few seconds, so nothing here needs pressing. */
function Progress({ ai, type }: { ai: AssetAi; type: string }) {
  const { t } = useTranslation();
  const running = ai.status === 'running';
  const steps = ai.run?.steps ?? [];
  const expected = expectedSteps(type);
  const done = Math.min(steps.length, expected.length);
  const current = expected.find((name) => !steps.some((s) => s.name === name));
  const determinate = running && steps.length > 0;
  return (
    <div className="dam-ai-progress">
      <div
        className="dam-ai-progress__bar"
        role="progressbar"
        aria-label={t('aiAsset.progress.label')}
        aria-valuemin={0}
        aria-valuemax={expected.length}
        aria-valuenow={determinate ? done : undefined}
        data-indeterminate={determinate ? undefined : true}
      >
        <span style={determinate ? { width: `${Math.max(8, (done / expected.length) * 100)}%` } : undefined} />
      </div>
      <div role="status">
        <p className="dam-ai-progress__title">{t(running ? 'aiAsset.progress.running' : 'aiAsset.progress.waiting')}</p>
        {ai.run?.error && !running && (
          <p className="dam-hint dam-ai-hint">{t('aiAsset.progress.waitingWhy', { reason: ai.run.error })}</p>
        )}
        {determinate && (
          <p className="dam-hint dam-ai-hint">
            {current ? `${t('aiAsset.progress.now', { step: t(`aiAsset.steps.${current}`, { defaultValue: current }) })} · ` : ''}
            {t('aiAsset.progress.count', { done, total: expected.length })}
          </p>
        )}
      </div>
      <p className="dam-hint dam-ai-hint">{t('aiAsset.progress.hint')}</p>
    </div>
  );
}

/**
 * What the AI found for this asset, on the Details tab: suggested tags and language to accept or dismiss, the transcript of
 * spoken audio, the text read from a picture or page, and how it was analysed. Editors (`assets.update`) decide suggestions
 * and re-run the analysis; anyone who can read the asset sees the results. Hidden when AI is off and there is nothing to show.
 */
export function AiPanel({ asset }: { asset: AssetDto }) {
  const { t } = useTranslation();
  const { push } = useToast();
  const caps = useAiCapabilities();
  const me = useMe().data;
  const eligible = AI_TYPES.has(asset.type);
  const query = useAssetAi(asset.id, eligible);
  const enrich = useEnrichAsset(asset.id);
  const deepLinkMs = useDeepLinkTime();
  const [error, setError] = useState<string | null>(null);

  const canEdit = !!me?.permissions.includes('assets.update') && asset.status !== 'deleted';
  const enrichment = !!caps.data?.enrichment;
  const ai = query.data;

  // A file that has only just arrived has no run yet but will get one by itself (enrichment starts when ingest finishes), and
  // the lookup only polls once a run exists: look again every few seconds for a short while so the panel does not stay on
  // "not analysed" while the analysis starts.
  const awaitingRun = enrichment && ai?.status === 'none' && ai.run === null && eligible;
  const arrived = Date.parse(asset.updatedAt);
  const { refetch } = query;
  useEffect(() => {
    if (!awaitingRun) return;
    const timer = setInterval(() => {
      if (Date.now() - arrived > NEW_FILE_WINDOW_MS) clearInterval(timer);
      else void refetch();
    }, NEW_FILE_POLL_MS);
    return () => clearInterval(timer);
  }, [awaitingRun, arrived, refetch]);

  if (!eligible) return null;
  if (query.isPending) {
    return enrichment ? (
      <Card as="section" padding="md" aria-labelledby={AI_TITLE_ID} id={AI_PANEL_ID} className="dam-ai">
        <h2 id={AI_TITLE_ID} className="dam-metasection__title">
          {t('aiAsset.title')}
        </h2>
        <div role="status" aria-label={t('aiAsset.loading')} className="dam-ai-loading">
          <Skeleton height={18} width="70%" />
          <Skeleton height={18} />
        </div>
      </Card>
    ) : null;
  }

  const analyse = () => {
    setError(null);
    enrich.mutate(undefined, {
      onSuccess: () => push({ title: t('aiAsset.analyseStarted'), tone: 'info', durationMs: 3000 }),
      onError: (e) => setError(`${t('aiAsset.analyseFailed')}: ${errorMessage(e)}`),
    });
  };

  if (query.error || !ai) {
    // Only an organisation that uses AI hears about a failed lookup; for everyone else the panel simply is not there.
    if (!enrichment) return null;
    return (
      <Card as="section" padding="md" aria-labelledby={AI_TITLE_ID} id={AI_PANEL_ID} className="dam-ai">
        <h2 id={AI_TITLE_ID} className="dam-metasection__title">
          {t('aiAsset.title')}
        </h2>
        <p className="dam-error" role="alert">
          <WarningCircle size={16} weight="bold" aria-hidden="true" />
          {t('aiAsset.loadFailed')}
        </p>
        <div>
          <Button size="sm" variant="secondary" onClick={() => void query.refetch()}>
            {t('aiAsset.retry')}
          </Button>
        </div>
      </Card>
    );
  }

  const status = ai.status;
  const offerAnalyse = canEdit && enrichment && status !== 'disabled';
  if (!hasAiContent(ai) && !(enrichment && status === 'none')) return null;

  const run = ai.run;
  const segments = ai.transcript?.segments ?? [];
  const document = asset.type === 'document' || asset.type === 'manual';
  // A summary is made from the text read from the asset, so it can only be asked for once there is some.
  const canSummarise =
    canEdit && !!caps.data?.enabled && !!caps.data.summarise && status !== 'disabled' && (!!ai.ocrText || segments.length > 0);
  const tagsStep = run?.steps.find((s) => s.name === 'tags');
  const expectTags = status === 'ready' && tagsStep?.status === 'ok';
  // "Analysed on..." once there is an analysis; before that (or while it runs) "Analysis runs on...".
  const analysed = status === 'ready' || status === 'failed' || status === 'disabled';

  return (
    <Card as="section" padding="md" aria-labelledby={AI_TITLE_ID} id={AI_PANEL_ID} className="dam-ai">
      <div className="dam-ai__head">
        <h2 id={AI_TITLE_ID} tabIndex={-1} className="dam-metasection__title dam-ai__title">
          <Sparkle size={16} weight="fill" aria-hidden="true" />
          {t('aiAsset.title')}
        </h2>
        <div className="dam-ai__headside">
          <Badge tone={TONE[status] ?? 'neutral'} size="sm">
            {t(`aiAsset.status.${status}`, { defaultValue: status })}
          </Badge>
          {offerAnalyse && status === 'ready' && (
            <Button size="sm" variant="ghost" loading={enrich.isPending} onClick={analyse}>
              {t('aiAsset.analyseAgain')}
            </Button>
          )}
        </div>
      </div>
      <TranslateAssetButton asset={asset} />

      {status === 'disabled' && <p className="dam-hint dam-ai-hint">{t('aiAsset.disabled.note')}</p>}

      {(status === 'pending' || status === 'running') && <Progress ai={ai} type={asset.type} />}

      {status === 'failed' && (
        <div className="dam-ai-failed" role="alert">
          <p className="dam-ai-failed__title">
            <WarningCircle size={18} weight="fill" aria-hidden="true" />
            {t('aiAsset.failed.title')}
          </p>
          <p className="dam-ai-failed__reason">{run?.error ?? t('aiAsset.failed.fallback')}</p>
          {offerAnalyse && (
            <div>
              <Button size="sm" variant="secondary" loading={enrich.isPending} onClick={analyse}>
                {t('aiAsset.retry')}
              </Button>
            </div>
          )}
        </div>
      )}

      {status === 'none' && (
        <div className="dam-ai-none">
          <p className="dam-ai-none__title">{t('aiAsset.none.title')}</p>
          <p className="dam-hint dam-ai-hint">
            {run?.status === 'skipped'
              ? run.error
                ? t('aiAsset.none.skipped', { reason: run.error })
                : t('aiAsset.none.skippedPlain')
              : t(canEdit ? 'aiAsset.none.hint' : 'aiAsset.none.hintReader')}
          </p>
          {offerAnalyse && (
            <div>
              <Button size="sm" variant="secondary" loading={enrich.isPending} onClick={analyse}>
                {t('aiAsset.analyse')}
              </Button>
            </div>
          )}
        </div>
      )}

      {error && (
        <p className="dam-error dam-ai-error" role="alert">
          {error}
        </p>
      )}

      <SummaryBlock assetId={asset.id} ai={ai} canSummarise={canSummarise} />

      <Suggestions
        assetId={asset.id}
        assetType={asset.type}
        suggestions={ai.suggestions}
        canDecide={canEdit}
        expectTags={expectTags}
      />

      {ai.transcript && segments.length > 0 && <TranscriptView transcript={ai.transcript} deepLinkMs={deepLinkMs} />}

      {ai.ocrText && <OcrBlock text={ai.ocrText} truncated={ai.ocrTextTruncated} language={ai.ocrLanguage} document={document} />}

      {run && status !== 'none' && <RunDetails run={run} />}

      {(ai.embeddingsReady || caps.data?.enabled) && (
        <div className="dam-ai-foot">
          {ai.embeddingsReady && (
            <p className="dam-ai-foot__line">
              <MagnifyingGlass size={14} weight="bold" aria-hidden="true" />
              {t('aiAsset.search.ready')}
            </p>
          )}
          {caps.data?.enabled && (
            <p className="dam-ai-foot__line">
              {caps.data.sendsDataOffSite ? (
                <CloudArrowUp size={14} weight="bold" aria-hidden="true" />
              ) : (
                <HardDrives size={14} weight="bold" aria-hidden="true" />
              )}
              {caps.data.sendsDataOffSite
                ? t(`aiAsset.privacy.offSite${analysed ? '' : 'Will'}`, {
                    provider: t(`aiAsset.privacy.provider.${caps.data.provider}`, { defaultValue: caps.data.provider }),
                  })
                : t(`aiAsset.privacy.onSite${analysed ? '' : 'Will'}`)}
            </p>
          )}
        </div>
      )}
    </Card>
  );
}
