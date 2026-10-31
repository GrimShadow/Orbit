import { CheckCircle, Clock, WarningCircle, XCircle } from '@phosphor-icons/react';
import { useTranslation } from 'react-i18next';
import { Button } from '../../ui';
import { relativeTime } from '../../lib/relativeTime';
import { errorText, isRateLimited } from './integrationsApi';
import type { TestResultDto } from './integrationsTypes';

interface TestPanelProps {
  /** `idle` renders only the (empty) live region, so a result that arrives later is announced. */
  state: 'idle' | 'pending' | 'done' | 'error';
  result?: TestResultDto;
  error?: unknown;
  onRetry?: () => void;
}

/** The outcome of a connection test: the steps with their result and time. One live region, always mounted, so a screen
 * reader hears "testing", then the result. A 429 (one test per connection every few seconds) is a calm "wait", not a failure. */
export function TestPanel({ state, result, error, onRetry }: TestPanelProps) {
  const { t, i18n } = useTranslation();
  let body = null;
  if (state === 'pending') {
    body = (
      <p className="dam-inttest__line">
        <span className="dam-spinner" aria-hidden="true" />
        {t('integrations.test.running')}
      </p>
    );
  } else if (state === 'error' && isRateLimited(error)) {
    body = (
      <div className="dam-intnote dam-intnote--info">
        <Clock size={18} aria-hidden="true" />
        <div>
          <p className="dam-inttest__line">{t('integrations.test.wait')}</p>
          {onRetry && (
            <Button size="sm" onClick={onRetry}>
              {t('integrations.test.again')}
            </Button>
          )}
        </div>
      </div>
    );
  } else if (state === 'error') {
    body = (
      <div className="dam-intnote dam-intnote--danger">
        <WarningCircle size={18} aria-hidden="true" />
        <div>
          <p className="dam-inttest__line">{t('integrations.test.couldNotRun')}</p>
          {errorText(error) && <p className="dam-hint">{errorText(error)}</p>}
          {onRetry && (
            <Button size="sm" onClick={onRetry}>
              {t('integrations.test.again')}
            </Button>
          )}
        </div>
      </div>
    );
  } else if (state === 'done' && result) {
    body = (
      <div className="dam-inttest__result">
        <p className={`dam-intnote ${result.ok ? 'dam-intnote--ok' : 'dam-intnote--danger'}`}>
          {result.ok ? <CheckCircle size={18} aria-hidden="true" /> : <XCircle size={18} aria-hidden="true" />}
          <span>{result.ok ? t('integrations.test.passed') : t('integrations.test.failed')}</span>
        </p>
        <ol className="dam-inttest__steps" aria-label={t('integrations.test.steps')}>
          {result.steps.map((s, i) => (
            <li key={`${i}-${s.name}`} className="dam-inttest__step">
              {s.ok ? (
                <CheckCircle size={18} weight="fill" className="dam-inttest__ok" aria-hidden="true" />
              ) : (
                <XCircle size={18} weight="fill" className="dam-inttest__bad" aria-hidden="true" />
              )}
              <div className="dam-inttest__stepbody">
                <span className="dam-inttest__stepname">
                  {s.name}
                  <span className="dam-sr-only">
                    : {s.ok ? t('integrations.test.stepOk') : t('integrations.test.stepFailed')}
                  </span>
                </span>
                {s.detail && <span className="dam-inttest__detail">{s.detail}</span>}
              </div>
              <span className="dam-hint dam-inttest__ms">{t('integrations.ms', { ms: s.durationMs })}</span>
            </li>
          ))}
        </ol>
        <p className="dam-hint">
          {t('integrations.test.footer', {
            when: relativeTime(result.testedAt, i18n.language),
            ms: result.durationMs,
          })}
        </p>
      </div>
    );
  }
  return (
    <div className="dam-inttest" role="status" aria-live="polite" aria-busy={state === 'pending' || undefined}>
      {body}
    </div>
  );
}
