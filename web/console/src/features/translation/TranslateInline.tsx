import { Sparkle } from '@phosphor-icons/react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { Button } from '../../ui';
import { errorMessage } from '../asset/assetApi';
import { useAssetAi } from '../ai/aiApi';
import { SuggestionCard, isTranslation } from './SuggestionCard';
import type { TranslationScopeValue } from './TranslationScope';
import { languageName } from './languages';
import { useTranslateAsset, useTranslationJobs } from './translationApi';
import { useTranslateSupport } from './useTranslateSupport';

interface Props {
  scope: TranslationScopeValue;
  field: string;
  fieldLabel: string;
  source: string;
  target: string;
  /** The source text as it is in the editor now (possibly not saved yet). */
  sourceText: string;
  onDecided: (decision: 'accept' | 'reject') => void;
}

/** What sits under the target language box: the "Translate with AI" button (or an honest reason it is not on offer), its
 * progress, and the pending AI suggestion for this field and language. For editors only; readers see a plain text box. */
export function TranslateInline({ scope, field, fieldLabel, source, target, sourceText, onDecided }: Props) {
  const { t, i18n } = useTranslation();
  const support = useTranslateSupport();
  const ai = useAssetAi(scope.assetId, support.canEdit);
  const translate = useTranslateAsset(scope.assetId);
  const [started, setStarted] = useState<{ jobId: string; field: string; language: string } | null>(null);
  const [startError, setStartError] = useState<string | null>(null);
  const jobs = useTranslationJobs(scope.assetId, started !== null);

  if (!support.canEdit) return null;

  const sourceName = languageName(source, i18n.language);
  const targetName = languageName(target, i18n.language);
  const savedMl = scope.saved[field];
  const savedSource =
    savedMl && typeof savedMl === 'object' && !Array.isArray(savedMl)
      ? String((savedMl as Record<string, unknown>)[source] ?? '')
      : '';

  const suggestion = (ai.data?.suggestions ?? [])
    .filter((s) => isTranslation(s) && s.status === 'pending' && s.field === field && s.language === target)
    .sort((a, b) => b.createdAt.localeCompare(a.createdAt))[0];

  const job =
    started && started.field === field && started.language === target
      ? jobs.data?.jobs.find((j) => j.id === started.jobId)
      : undefined;
  const item = job?.items.find((i) => i.field === field && i.language === target);
  const running =
    translate.isPending ||
    (!!started &&
      started.field === field &&
      started.language === target &&
      (!job || job.status === 'pending' || job.status === 'running'));

  // Translating works from the saved text, so unsaved edits to the source would be left out.
  let blocked: string | null = null;
  if (sourceText.trim() === '') blocked = t('translation.inline.needSource', { language: sourceName });
  else if (sourceText !== savedSource) blocked = t('translation.inline.saveFirst', { language: sourceName });

  const start = () => {
    setStartError(null);
    translate.mutate(
      { fields: [field], targetLanguages: [target], sourceLanguage: source, overwrite: true },
      {
        onSuccess: (j) => setStarted({ jobId: j.id, field, language: target }),
        onError: (e) => setStartError(t('translation.inline.startFailed', { reason: errorMessage(e) })),
      },
    );
  };

  let status: string | null = null;
  if (startError) status = startError;
  else if (running) status = t('translation.inline.working', { language: targetName });
  else if (job && item) {
    if (item.status === 'ok') status = t('translation.inline.done', { language: targetName });
    else if (item.status === 'skipped')
      status = t('translation.inline.skipped', { reason: item.reason ?? t('translation.job.item.skipped') });
    else status = item.reason ? t('translation.inline.failed', { reason: item.reason }) : t('translation.inline.failedPlain');
  } else if (job && (job.status === 'failed' || job.status === 'partial')) status = t('translation.inline.failedPlain');

  return (
    <div className="dam-ml__ai">
      {support.state === 'ready' && (
        <div className="dam-ml__aibar">
          <Button
            size="sm"
            variant="secondary"
            loading={running}
            disabled={blocked !== null}
            aria-label={t('translation.inline.translateLabel', { field: fieldLabel, language: targetName })}
            onClick={start}
          >
            <Sparkle size={14} weight="fill" aria-hidden="true" />
            {t('translation.inline.translate')}
          </Button>
          {blocked && <span className="dam-hint">{blocked}</span>}
        </div>
      )}
      {(support.state === 'off' || support.state === 'unsupported') && (
        <p className="dam-hint dam-ml__nohint">
          {t(support.state === 'off' ? 'translation.inline.off' : 'translation.inline.unsupported')}{' '}
          {support.canManageAi && <Link to="/settings/ai">{t('translation.inline.settings')}</Link>}
        </p>
      )}
      <div role="status" aria-live="polite" className="dam-ml__live">
        {status}
      </div>
      {suggestion && (
        <SuggestionCard
          assetId={scope.assetId}
          suggestion={suggestion}
          fieldLabel={fieldLabel}
          variant="inline"
          canDecide
          onDecided={onDecided}
        />
      )}
    </div>
  );
}
