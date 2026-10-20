import { Info, Sparkle } from '@phosphor-icons/react';
import { useId } from 'react';
import { useTranslation } from 'react-i18next';

/** The Smart search switch (`role="switch"`): a pill in the search bar with a tooltip that is also its accessible description. */
export function SmartToggle({ on, onChange }: { on: boolean; onChange: (on: boolean) => void }) {
  const { t } = useTranslation();
  const tipId = useId();
  return (
    <div className="dam-smarttoggle">
      <button
        type="button"
        role="switch"
        aria-checked={on}
        aria-describedby={tipId}
        className="dam-smarttoggle__btn"
        onClick={() => onChange(!on)}
      >
        <Sparkle size={16} weight={on ? 'fill' : 'bold'} aria-hidden="true" />
        <span className="dam-smarttoggle__label">{t('smartSearch.toggle.label')}</span>
      </button>
      <span id={tipId} role="tooltip" className="dam-smarttoggle__tip">
        {t('smartSearch.toggle.tooltip')}
      </span>
    </div>
  );
}

const FALLBACK_REASONS = [
  'ai_disabled',
  'provider_not_configured',
  'embeddings_disabled',
  'embeddings_unsupported',
  'provider_unavailable',
  'vectors_not_indexed',
] as const;

/** A quiet sentence for why a Smart search ran as a regular one. `empty_query` and `similar_to` are not worth saying (nothing
 * to describe, or a different kind of search) and an unknown future reason stays silent rather than alarming. */
export function FallbackNote({ reason }: { reason: string | null | undefined }) {
  const { t } = useTranslation();
  if (!reason || !(FALLBACK_REASONS as readonly string[]).includes(reason)) return null;
  return (
    <p className="dam-searchnote" role="status">
      <Info size={16} aria-hidden="true" />
      <span>{t(`smartSearch.fallback.${reason}`)}</span>
    </p>
  );
}

/** Three generic, clickable example descriptions for an empty Search page. */
export function ExampleQueries({ onPick }: { onPick: (query: string) => void }) {
  const { t } = useTranslation();
  const examples = [t('smartSearch.examples.one'), t('smartSearch.examples.two'), t('smartSearch.examples.three')];
  return (
    <div className="dam-examples" role="group" aria-label={t('smartSearch.examples.label')}>
      <span className="dam-examples__label" aria-hidden="true">
        {t('smartSearch.examples.label')}
      </span>
      {examples.map((q) => (
        <button key={q} type="button" className="dam-examples__chip" onClick={() => onPick(q)}>
          <Sparkle size={14} weight="fill" aria-hidden="true" />
          {q}
        </button>
      ))}
    </div>
  );
}
