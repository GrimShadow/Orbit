import { LockSimple, ShieldWarning, WarningCircle } from '@phosphor-icons/react';
import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { Badge, Card } from '../../../ui';
import { Switch } from '../../webhooks/Switch';
import '../../webhooks/webhooks.css';
import type { AiSettings } from '../aiApi';
import { choosable, effectiveKind, providerProblem, selectedProvider, type Draft } from './aiAdminModel';
import { useProviderName } from './StatusCard';

interface ProviderCardProps {
  settings: AiSettings;
  draft: Draft;
  onChange: (patch: Partial<Draft>) => void;
  /** The privacy acknowledgement is required before this draft can be saved. */
  needsAck: boolean;
  ack: boolean;
  onAck: (next: boolean) => void;
  /** The server refused the save for these fields. */
  errors: Record<string, string>;
}

/** The on/off switch for the organisation, the provider choice, and the privacy notice that goes with an off-site provider. */
export function ProviderCard({ settings, draft, onChange, needsAck, ack, onAck, errors }: ProviderCardProps) {
  const { t } = useTranslation();
  const providerName = useProviderName();
  const groupId = useId();
  const options = choosable(settings);
  const picked = effectiveKind(draft, settings);
  const current = selectedProvider(draft, settings);
  const problem = providerProblem(draft, settings);
  const providerError =
    errors.provider ??
    (problem === 'choose'
      ? t('aiAdmin.provider.chooseProblem')
      : problem === 'unconfigured'
        ? t('aiAdmin.provider.unconfiguredProblem')
        : undefined);
  const defaultName = settings.defaultProvider !== 'none' ? providerName(settings.defaultProvider) : null;
  const showPrivacy = draft.enabled && current?.sendsDataOffSite === true;
  const ackError = errors.acknowledgeOffSite;

  const choose = (kind: string) => onChange({ provider: kind === settings.defaultProvider ? null : kind });

  return (
    <Card as="section" aria-labelledby="ai-provider-title" className="dam-ai__card" padding="lg">
      <div className="dam-ai__head">
        <h2 id="ai-provider-title" className="dam-ai__title">
          {t('aiAdmin.provider.title')}
        </h2>
      </div>
      <p className="dam-ai__desc">{t('aiAdmin.provider.description')}</p>

      <Switch
        checked={draft.enabled}
        onChange={(enabled) => onChange({ enabled })}
        label={t('aiAdmin.provider.enable')}
        hint={t('aiAdmin.provider.enableHint')}
      />

      {!draft.enabled && (
        <div className="dam-aicallout dam-aicallout--neutral" role="status">
          <LockSimple size={22} weight="fill" aria-hidden="true" />
          <div>
            <strong>{t('aiAdmin.provider.offTitle')}</strong>
            <p>{t('aiAdmin.provider.offBody')}</p>
          </div>
        </div>
      )}

      <fieldset className="dam-aiproviders" aria-describedby={`${groupId}-hint`}>
        <legend className="dam-label">{t('aiAdmin.provider.choose')}</legend>
        <span id={`${groupId}-hint`} className="dam-hint">
          {t('aiAdmin.provider.chooseHint')}{' '}
          {defaultName ? t('aiAdmin.provider.defaultIs', { provider: defaultName }) : t('aiAdmin.provider.defaultNone')}
        </span>
        <ul className="dam-aiproviders__list">
          {options.map((p) => {
            const checked = picked === p.kind;
            const inUse = settings.enabled && settings.effectiveProvider === p.kind;
            const descId = `${groupId}-${p.kind}-desc`;
            return (
              <li key={p.kind}>
                {/* eslint-disable-next-line jsx-a11y/label-has-associated-control -- the radio is nested; the name is the text inside */}
                <label
                  className={`dam-aiprovider${checked ? ' dam-aiprovider--checked' : ''}${p.configured ? '' : ' dam-aiprovider--disabled'}`}
                >
                  <input
                    type="radio"
                    name={`${groupId}-provider`}
                    value={p.kind}
                    checked={checked}
                    disabled={!p.configured}
                    aria-describedby={descId}
                    onChange={() => choose(p.kind)}
                  />
                  <span className="dam-aiprovider__body">
                    <span className="dam-aiprovider__name">
                      {providerName(p.kind)}
                      {p.kind === settings.defaultProvider && (
                        <Badge size="sm" tone="neutral">
                          {t('aiAdmin.provider.default')}
                        </Badge>
                      )}
                      {inUse && (
                        <Badge size="sm" tone="brand">
                          {t('aiAdmin.provider.inUse')}
                        </Badge>
                      )}
                      {p.sendsDataOffSite ? (
                        <Badge size="sm" tone="warning">
                          <ShieldWarning size={12} weight="fill" aria-hidden="true" />
                          {t('aiAdmin.provider.offSite')}
                        </Badge>
                      ) : (
                        <Badge size="sm" tone="success">
                          <LockSimple size={12} weight="fill" aria-hidden="true" />
                          {t('aiAdmin.provider.stays')}
                        </Badge>
                      )}
                    </span>
                    <span id={descId} className="dam-aiprovider__desc">
                      {t(`aiAdmin.provider.descriptions.${p.kind}`, { defaultValue: p.description })}
                      {!p.configured && <span className="dam-aiprovider__missing"> {t('aiAdmin.provider.notConfigured')}</span>}
                    </span>
                  </span>
                </label>
              </li>
            );
          })}
        </ul>
        {providerError && (
          <span className="dam-error" role="alert">
            <WarningCircle size={14} weight="bold" aria-hidden="true" />
            {providerError}
          </span>
        )}
      </fieldset>

      {showPrivacy && current && (
        <div className="dam-aicallout dam-aicallout--warning" role="group" aria-labelledby={`${groupId}-privacy`}>
          <ShieldWarning size={22} weight="fill" aria-hidden="true" />
          <div>
            <strong id={`${groupId}-privacy`}>{t('aiAdmin.provider.privacy.title')}</strong>
            <p>{t('aiAdmin.provider.privacy.body', { provider: providerName(current.kind) })}</p>
            <p>{t('aiAdmin.provider.privacy.body2')}</p>
            {needsAck ? (
              <>
                <label className="dam-check dam-aiack">
                  <input
                    type="checkbox"
                    className="dam-check__input"
                    checked={ack}
                    aria-invalid={ackError ? true : undefined}
                    onChange={(e) => onAck(e.target.checked)}
                  />
                  <span className="dam-check__label">
                    {t('aiAdmin.provider.privacy.ack', { provider: providerName(current.kind) })}
                  </span>
                </label>
                {(ackError || !ack) && (
                  <span className={ackError ? 'dam-error' : 'dam-hint'} role={ackError ? 'alert' : undefined}>
                    {ackError ? <WarningCircle size={14} weight="bold" aria-hidden="true" /> : null}
                    {ackError ?? t('aiAdmin.provider.privacy.ackRequired')}
                  </span>
                )}
              </>
            ) : (
              <p className="dam-hint">{t('aiAdmin.provider.privacy.already', { provider: providerName(current.kind) })}</p>
            )}
          </div>
        </div>
      )}
    </Card>
  );
}
