import { useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { useSearchParams } from 'react-router-dom';
import { useSchema } from '../asset/assetApi';
import type { AiSuggestion } from '../ai/aiApi';
import { SuggestionCard } from './SuggestionCard';

/** The id of the "Translation suggestions" heading, which the reviewer inbox links to (`?focus=translations`). */
export const TRANSLATIONS_HEADING_ID = 'dam-ai-translations';

/**
 * The AI panel's translation section: every pending translation of this asset, each as a card to accept, edit or
 * reject, under its own heading. Arriving with `?focus=translations` (from the reviewer inbox) puts keyboard focus on the
 * heading and scrolls it into view.
 */
export function TranslationSuggestions({
  assetId,
  assetType,
  pending,
  canDecide,
}: {
  assetId: string;
  assetType: string;
  pending: AiSuggestion[];
  canDecide: boolean;
}) {
  const { t } = useTranslation();
  const schema = useSchema(assetType);
  const [params] = useSearchParams();
  const heading = useRef<HTMLHeadingElement>(null);
  const focused = useRef<string | null>(null);
  const wanted = params.get('focus') === 'translations';
  const labels = new Map((schema.data?.fields ?? []).map((f) => [f.name, f.label]));

  useEffect(() => {
    // The heading exists only once there is something pending, which can be after the first render.
    if (!wanted || focused.current === assetId || !heading.current) return;
    focused.current = assetId;
    heading.current.scrollIntoView?.({ block: 'start' });
    heading.current.focus();
  }, [wanted, assetId, pending.length]);

  if (pending.length === 0) return null;
  return (
    <section aria-labelledby={TRANSLATIONS_HEADING_ID} className="dam-ai-sec">
      <h3 id={TRANSLATIONS_HEADING_ID} ref={heading} className="dam-ai-sub" tabIndex={-1} data-ai-heading>
        {t('translation.panel.title', { count: pending.length })}
      </h3>
      <p className="dam-hint dam-ai-hint">{t(canDecide ? 'translation.panel.hint' : 'translation.panel.hintReader')}</p>
      <ul className="dam-tsuglist">
        {pending.map((s) => (
          <li key={s.id}>
            <SuggestionCard
              assetId={assetId}
              suggestion={s}
              fieldLabel={(s.field && labels.get(s.field)) || s.field || ''}
              variant="listed"
              canDecide={canDecide}
              onDecided={() => heading.current?.focus()}
            />
          </li>
        ))}
      </ul>
    </section>
  );
}
