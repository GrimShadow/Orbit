import { Sparkle, WarningCircle } from '@phosphor-icons/react';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, useToast } from '../../../ui';
import { relativeTime } from '../../../lib/relativeTime';
import { errorMessage } from '../../asset/assetApi';
import { useAssetAi, useSummariseAsset, type AssetAi } from '../aiApi';
import { languageName } from './aiFormat';

/** How often to look for the summary after asking for one, and for how long before saying it is taking a while. */
const POLL_MS = 3_000;
const GIVE_UP_MS = 2 * 60_000;

/**
 * The AI-written summary of a document, always labelled as AI-generated (it is derived data, shown as found and never
 * written into the asset's metadata; ADR-0036), with its language, the model and when it was made. Editors can ask for
 * a new one: the request is accepted at once and the summary appears here by itself when it is ready.
 */
export function SummaryBlock({ assetId, ai, canSummarise }: { assetId: string; ai: AssetAi; canSummarise: boolean }) {
  const { t, i18n } = useTranslation();
  const { push } = useToast();
  const summarise = useSummariseAsset(assetId);
  const { refetch } = useAssetAi(assetId);
  const summary = ai.summary ?? null;
  // The request is over when a summary newer than the one on screen at the time arrives.
  const [waiting, setWaiting] = useState<{ previous: string | null; until: number } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [slow, setSlow] = useState(false);
  const summarising = waiting !== null && !slow && (summary?.createdAt ?? null) === waiting.previous;

  useEffect(() => {
    if (!summarising || !waiting) return;
    const timer = setInterval(() => {
      if (Date.now() > waiting.until) setSlow(true);
      else void refetch();
    }, POLL_MS);
    return () => clearInterval(timer);
  }, [summarising, waiting, refetch]);

  if (!summary && !canSummarise) return null;

  const ask = () => {
    setError(null);
    setSlow(false);
    summarise.mutate(undefined, {
      onSuccess: () => {
        setWaiting({ previous: summary?.createdAt ?? null, until: Date.now() + GIVE_UP_MS });
        push({ title: t('aiAsset.summary.started'), tone: 'info', durationMs: 3000 });
      },
      onError: (e) => setError(t('aiAsset.summary.failed', { reason: errorMessage(e) })),
    });
  };

  const when = summary ? new Date(summary.createdAt).toLocaleString(i18n.language) : '';
  return (
    <section aria-labelledby={`dam-ai-summary-${assetId}`} className="dam-ai-sec dam-summary">
      <div className="dam-ai-sec__head">
        <h3 id={`dam-ai-summary-${assetId}`} className="dam-ai-sub dam-summary__title">
          <Sparkle size={14} weight="fill" aria-hidden="true" />
          {t('aiAsset.summary.title')}
        </h3>
        {canSummarise && (
          <Button size="sm" variant="secondary" loading={summarise.isPending || summarising} onClick={ask}>
            {t(summary ? 'aiAsset.summary.again' : 'aiAsset.summary.ask')}
          </Button>
        )}
      </div>
      {summary ? (
        <>
          <p className="dam-summary__text" lang={summary.language ?? undefined}>
            {summary.text}
          </p>
          <p className="dam-hint dam-ai-hint">
            {[
              summary.language
                ? t('aiAsset.summary.language', { language: languageName(summary.language, i18n.language) })
                : null,
              t('aiAsset.summary.model', { model: summary.model }),
            ]
              .filter(Boolean)
              .join(' · ')}
            {' · '}
            <time dateTime={summary.createdAt} title={when}>
              {relativeTime(summary.createdAt, i18n.language)}
            </time>
          </p>
        </>
      ) : (
        <p className="dam-hint dam-ai-hint">{t('aiAsset.summary.none')}</p>
      )}
      <div role="status" aria-live="polite" className="dam-summary__live">
        {summarising ? t('aiAsset.summary.working') : slow ? t('aiAsset.summary.slow') : ''}
      </div>
      {error && (
        <p className="dam-error dam-ai-error" role="alert">
          <WarningCircle size={14} weight="bold" aria-hidden="true" />
          {error}
        </p>
      )}
    </section>
  );
}
