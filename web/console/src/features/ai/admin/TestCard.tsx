import { CheckCircle, Plugs, WarningCircle, XCircle } from '@phosphor-icons/react';
import { useTranslation } from 'react-i18next';
import { Button, Card } from '../../../ui';
import { useAiTest } from '../aiApi';
import { useProviderName } from './StatusCard';

/** "Test connection": asks the server to try the provider with a tiny built-in picture and phrase, and shows each step. */
export function TestCard() {
  const { t, i18n } = useTranslation();
  const providerName = useProviderName();
  const test = useAiTest();
  const nf = new Intl.NumberFormat(i18n.language);
  const result = test.data;

  const stepName = (name: string) => t(`aiAdmin.test.step.${name}`, { defaultValue: name });

  return (
    <Card as="section" aria-labelledby="ai-test-title" className="dam-ai__card" padding="lg">
      <div className="dam-ai__head">
        <h2 id="ai-test-title" className="dam-ai__title">
          {t('aiAdmin.test.title')}
        </h2>
        <Button variant="secondary" loading={test.isPending} onClick={() => test.mutate()}>
          {!test.isPending && <Plugs size={16} weight="bold" aria-hidden="true" />}
          {test.isPending ? t('aiAdmin.test.running') : t('aiAdmin.test.run')}
        </Button>
      </div>
      <p className="dam-ai__desc">{t('aiAdmin.test.description')}</p>

      <div aria-live="polite">
        {test.isError && (
          <div className="dam-aicallout dam-aicallout--danger" role="alert">
            <WarningCircle size={22} weight="fill" aria-hidden="true" />
            <div>
              <strong>{t('aiAdmin.test.error')}</strong>
              <p>{t('aiAdmin.test.errorBody')}</p>
            </div>
          </div>
        )}
        {result && !test.isPending && !test.isError && (
          <div className="dam-aitest">
            <div className={`dam-aicallout dam-aicallout--${result.ok ? 'success' : 'danger'}`}>
              {result.ok ? (
                <CheckCircle size={22} weight="fill" aria-hidden="true" />
              ) : (
                <XCircle size={22} weight="fill" aria-hidden="true" />
              )}
              <div>
                <strong>{result.ok ? t('aiAdmin.test.ok') : t('aiAdmin.test.fail')}</strong>
                <p>
                  {result.ok
                    ? t('aiAdmin.test.okBody', { provider: providerName(result.provider), ms: nf.format(result.totalMs) })
                    : t('aiAdmin.test.failBody', { provider: providerName(result.provider) })}
                </p>
              </div>
            </div>
            <ul className="dam-aisteps" aria-label={t('aiAdmin.test.steps')}>
              {result.steps.map((s) => (
                <li key={s.name} className={`dam-aistep${s.ok ? '' : ' dam-aistep--fail'}`}>
                  {s.ok ? (
                    <CheckCircle size={18} weight="fill" aria-hidden="true" className="dam-aistep__icon" />
                  ) : (
                    <XCircle size={18} weight="fill" aria-hidden="true" className="dam-aistep__icon" />
                  )}
                  <div className="dam-aistep__text">
                    <span className="dam-aistep__name">
                      {stepName(s.name)}
                      <span className="dam-sr-only">: {s.ok ? t('aiAdmin.test.stepOk') : t('aiAdmin.test.stepFail')}</span>
                    </span>
                    {!s.ok && (
                      <>
                        <span className="dam-aistep__hint">{t(`aiAdmin.test.hint.${s.name}`, { defaultValue: '' })}</span>
                        {s.detail && <span className="dam-aistep__detail">{s.detail}</span>}
                      </>
                    )}
                  </div>
                  <span className="dam-aistep__ms">{t('aiAdmin.test.ms', { ms: nf.format(s.durationMs) })}</span>
                </li>
              ))}
            </ul>
          </div>
        )}
      </div>
    </Card>
  );
}
