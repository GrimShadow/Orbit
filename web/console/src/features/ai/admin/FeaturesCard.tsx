import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { Card, Input } from '../../../ui';
import { Switch } from '../../webhooks/Switch';
import '../../webhooks/webhooks.css';
import type { AiStatus } from '../aiApi';
import { FEATURE_KEYS, featureSupported, languageInvalid, type Draft, type FeatureKey } from './aiAdminModel';

/** Common OCR language hints, offered as suggestions; any code the server accepts can be typed. */
const LANGUAGE_SUGGESTIONS = ['en', 'hi', 'mr', 'ta', 'te', 'bn', 'gu', 'kn', 'ml', 'pa', 'ur', 'fr', 'de', 'es', 'pt-br'];

interface FeaturesCardProps {
  draft: Draft;
  onChange: (patch: Partial<Draft>) => void;
  /** The provider in use and what it can do, when the status is known: a switch can then say a step will be skipped. */
  status: AiStatus | undefined;
  /** The server refused the OCR language. */
  languageError?: string;
}

/** One switch per kind of analysis, plus the language hint for reading text. */
export function FeaturesCard({ draft, onChange, status, languageError }: FeaturesCardProps) {
  const { t } = useTranslation();
  const listId = useId();
  const invalid = languageInvalid(draft.ocrLanguage);

  const setFeature = (key: FeatureKey, on: boolean) => onChange({ features: { ...draft.features, [key]: on } });

  return (
    <Card as="section" aria-labelledby="ai-features-title" className="dam-ai__card" padding="lg">
      <div className="dam-ai__head">
        <h2 id="ai-features-title" className="dam-ai__title">
          {t('aiAdmin.features.title')}
        </h2>
      </div>
      <p className="dam-ai__desc">{t('aiAdmin.features.description')}</p>

      <ul className="dam-aifeatures">
        {FEATURE_KEYS.map((key) => {
          const unsupported = status !== undefined && status.enabled && !featureSupported(key, status.capabilities);
          return (
            <li key={key}>
              <Switch
                checked={draft.features[key]}
                onChange={(on) => setFeature(key, on)}
                label={t(`aiAdmin.features.${key}`)}
                hint={
                  unsupported && draft.features[key]
                    ? `${t(`aiAdmin.features.${key}Hint`)} ${t('aiAdmin.features.unsupported')}`
                    : t(`aiAdmin.features.${key}Hint`)
                }
              />
            </li>
          );
        })}
      </ul>

      <div className="dam-ai__language">
        <Input
          label={t('aiAdmin.features.ocrLanguage')}
          hint={t('aiAdmin.features.ocrLanguageHint')}
          error={languageError ?? (invalid ? t('aiAdmin.features.ocrLanguageInvalid') : undefined)}
          value={draft.ocrLanguage}
          placeholder={t('aiAdmin.features.ocrLanguagePlaceholder')}
          list={listId}
          maxLength={10}
          autoComplete="off"
          spellCheck={false}
          onChange={(e) => onChange({ ocrLanguage: e.target.value })}
        />
        <datalist id={listId}>
          {LANGUAGE_SUGGESTIONS.map((code) => (
            <option key={code} value={code} />
          ))}
        </datalist>
      </div>
    </Card>
  );
}
