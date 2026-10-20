import { CheckCircle, MinusCircle, XCircle } from '@phosphor-icons/react';
import { useTranslation } from 'react-i18next';
import { relativeTime } from '../../../lib/relativeTime';
import type { AiRun } from '../aiApi';
import { Disclosure } from './Disclosure';
import { formatStepDuration } from './aiFormat';

const ICON = {
  ok: <CheckCircle size={18} weight="fill" aria-hidden="true" />,
  skipped: <MinusCircle size={18} weight="regular" aria-hidden="true" />,
  failed: <XCircle size={18} weight="fill" aria-hidden="true" />,
} as const;

/** "How this was analysed": what ran, with which model, how long it took, what was skipped and why. Quiet, folded by default. */
export function RunDetails({ run }: { run: NonNullable<AiRun> }) {
  const { t, i18n } = useTranslation();
  const finished = run.finishedAt ?? run.startedAt;
  const took = run.startedAt && run.finishedAt ? Date.parse(run.finishedAt) - Date.parse(run.startedAt) : null;
  const summary = [
    finished
      ? t(run.finishedAt ? 'aiAsset.run.finished' : 'aiAsset.run.started', { when: relativeTime(finished, i18n.language) })
      : null,
    took !== null && took >= 0 ? t('aiAsset.run.duration', { time: formatStepDuration(took) }) : null,
    run.attempts > 1 ? t('aiAsset.run.attempts', { count: run.attempts }) : null,
  ].filter(Boolean);
  return (
    <Disclosure title={t('aiAsset.run.title')}>
      {summary.length > 0 && <p className="dam-hint dam-ai-hint">{summary.join(' · ')}</p>}
      {run.steps.length > 0 && (
        <ol className="dam-ai-steps" aria-label={t('aiAsset.run.steps')}>
          {run.steps.map((s, i) => {
            const status = s.status in ICON ? (s.status as keyof typeof ICON) : 'ok';
            return (
              <li key={`${s.name}-${i}`} className={`dam-ai-step dam-ai-step--${status}`}>
                <span className="dam-ai-step__icon">{ICON[status]}</span>
                <div className="dam-ai-step__body">
                  <p className="dam-ai-step__head">
                    <span className="dam-ai-step__name">{t(`aiAsset.stepName.${s.name}`, { defaultValue: s.name })}</span>
                    <span className="dam-ai-step__status">{t(`aiAsset.stepStatus.${status}`)}</span>
                  </p>
                  <p className="dam-ai-step__meta">
                    {[s.model, s.durationMs > 0 ? formatStepDuration(s.durationMs) : null].filter(Boolean).join(' · ')}
                  </p>
                  {s.detail && <p className="dam-ai-step__detail">{s.detail}</p>}
                </div>
              </li>
            );
          })}
        </ol>
      )}
      {run.error && <p className="dam-ai-step__detail">{t('aiAsset.run.error', { error: run.error })}</p>}
    </Disclosure>
  );
}
