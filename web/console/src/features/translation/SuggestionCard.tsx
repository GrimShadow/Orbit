import { ApiError } from '@dam/sdk-js';
import { Sparkle, WarningCircle } from '@phosphor-icons/react';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useId, useRef, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { Button, useToast } from '../../ui';
import { errorMessage } from '../asset/assetApi';
import { TRANSLATION_KIND, type AiSuggestion, type AssetAi } from '../ai/aiApi';
import { languageName } from '../ai/asset/aiFormat';
import { useGlossaryHasTerms } from '../glossary/glossaryApi';
import { useDecideTranslation } from './translationApi';
import './translation.css';

export const isTranslation = (s: AiSuggestion) => s.kind === TRANSLATION_KIND;

interface SuggestionCardProps {
  assetId: string;
  suggestion: AiSuggestion;
  /** The field's label ("Headline"), for names and messages. */
  fieldLabel: string;
  /** `inline` sits in the target pane of the editor (the field is on screen); `listed` is one of several in the AI panel. */
  variant: 'inline' | 'listed';
  /** An editor may decide; a reader only sees it. */
  canDecide: boolean;
  /** Called after the suggestion was accepted or rejected (the card is about to go away: move focus somewhere sensible). */
  onDecided?: (decision: 'accept' | 'reject') => void;
}

/**
 * One pending translation, to accept, edit before accepting, or reject. Accepting writes the text into the asset exactly
 * as typing it would (ADR-0036). When the target language already has text the server answers 409 and the card asks
 * before replacing it. Nothing changes on the asset until a person presses a button.
 */
export function SuggestionCard({ assetId, suggestion: s, fieldLabel, variant, canDecide, onDecided }: SuggestionCardProps) {
  const { t, i18n } = useTranslation();
  const { push } = useToast();
  const qc = useQueryClient();
  const decide = useDecideTranslation(assetId);
  const glossary = useGlossaryHasTerms().data;
  const titleId = useId();
  const editId = useId();
  const confirmRef = useRef<HTMLDivElement>(null);
  const card = useRef<HTMLDivElement>(null);
  const [params] = useSearchParams();
  const focusWanted = variant === 'inline' && params.get('focus') === 'translations';

  const language = s.language ?? '';
  const target = languageName(language, i18n.language);
  const source = s.sourceLanguage ? languageName(s.sourceLanguage, i18n.language) : null;
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(s.value);
  /** Set once the server said the target already has text: the text that will be sent if the person confirms. */
  const [replacing, setReplacing] = useState<{ text?: string } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [working, setWorking] = useState<'accept' | 'reject' | null>(null);

  useEffect(() => {
    if (replacing) confirmRef.current?.focus();
  }, [replacing]);

  // Arriving from the reviewer inbox on the Metadata tab: the first suggestion on the page takes focus, once.
  useEffect(() => {
    const el = card.current;
    if (!focusWanted || !el || el.dataset.focused) return;
    if (document.querySelector('.dam-tsug[data-variant="inline"]') !== el) return;
    el.dataset.focused = 'true';
    el.scrollIntoView?.({ block: 'center' });
    el.focus();
  }, [focusWanted]);

  const accept = async (text: string | undefined, replace: boolean) => {
    setError(null);
    setWorking('accept');
    try {
      await decide.mutateAsync({
        suggestionId: s.id,
        decision: 'accept',
        body: { ...(text !== undefined && { text }), ...(replace && { replace: true }) },
      });
      push({ title: t('translation.suggestion.accepted', { field: fieldLabel, language: target }), tone: 'success' });
      onDecided?.('accept');
    } catch (e) {
      if (e instanceof ApiError && e.status === 409 && !replace) {
        // Either the target language already has text (ask before replacing it) or somebody decided this suggestion first.
        await qc.refetchQueries({ queryKey: ['ai', 'asset', assetId], exact: true });
        const fresh = qc.getQueryData<AssetAi>(['ai', 'asset', assetId]);
        if (fresh?.suggestions.some((x) => x.id === s.id && x.status === 'pending')) {
          setReplacing({ text });
        } else {
          setError(t('translation.suggestion.alreadyDecided'));
        }
      } else {
        setError(t('translation.suggestion.decideFailed', { reason: errorMessage(e) }));
      }
    } finally {
      setWorking(null);
    }
  };

  const reject = async () => {
    setError(null);
    setWorking('reject');
    try {
      await decide.mutateAsync({ suggestionId: s.id, decision: 'reject' });
      push({ title: t('translation.suggestion.rejected'), tone: 'info', durationMs: 3000 });
      onDecided?.('reject');
    } catch (e) {
      setError(t('translation.suggestion.decideFailed', { reason: errorMessage(e) }));
    } finally {
      setWorking(null);
    }
  };

  const names = { field: fieldLabel, language: target };
  const busy = working !== null;
  const count = (editing ? draft : s.value).length;

  return (
    <div
      ref={card}
      tabIndex={-1}
      className="dam-tsug"
      role="group"
      aria-labelledby={titleId}
      data-variant={variant}
      data-stale={s.sourceStale || undefined}
    >
      <p id={titleId} className="dam-tsug__title">
        <Sparkle size={14} weight="fill" aria-hidden="true" />
        <span>{variant === 'inline' ? t('translation.suggestion.title') : t('translation.suggestion.titleFor', names)}</span>
        {variant === 'listed' && <span className="dam-tsug__badge">{t('translation.suggestion.title')}</span>}
      </p>

      {s.sourceStale && (
        <p className="dam-tsug__warn" role="note">
          <WarningCircle size={16} weight="fill" aria-hidden="true" />
          <span>{t('translation.suggestion.stale', { language: source ?? t('translation.suggestion.sourceFallback') })}</span>
        </p>
      )}

      {editing ? (
        <div className="dam-tsug__edit">
          <label className="dam-sr-only" htmlFor={editId}>
            {t('translation.suggestion.editedLabel', names)}
          </label>
          <textarea
            id={editId}
            className="dam-input dam-metafield__textarea"
            lang={language}
            rows={4}
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
          />
        </div>
      ) : (
        <blockquote className="dam-tsug__text" lang={language}>
          {s.value}
        </blockquote>
      )}

      <p className="dam-hint dam-tsug__meta">
        {[source ? t('translation.suggestion.from', { language: source }) : null, t('translation.chars', { count }), s.model]
          .filter(Boolean)
          .join(' · ')}
      </p>
      {glossary && <p className="dam-hint dam-tsug__meta">{t('translation.suggestion.glossary')}</p>}

      {replacing && (
        <div ref={confirmRef} className="dam-tsug__confirm" role="alert" tabIndex={-1}>
          <p className="dam-tsug__confirmtitle">{t('translation.suggestion.replaceTitle', { language: target })}</p>
          <p>{t('translation.suggestion.replaceBody', names)}</p>
          <div className="dam-tsug__actions">
            <Button size="sm" variant="danger" loading={working === 'accept'} onClick={() => void accept(replacing.text, true)}>
              {t('translation.suggestion.replace')}
            </Button>
            <Button size="sm" variant="ghost" disabled={busy} onClick={() => setReplacing(null)}>
              {t('translation.suggestion.keep')}
            </Button>
          </div>
        </div>
      )}

      {error && (
        <p className="dam-error dam-tsug__error" role="alert">
          <WarningCircle size={14} weight="bold" aria-hidden="true" />
          {error}
        </p>
      )}

      {canDecide && !replacing && (
        <div className="dam-tsug__actions">
          {editing ? (
            <>
              <Button
                size="sm"
                variant="primary"
                loading={working === 'accept'}
                disabled={busy || draft.trim() === ''}
                aria-label={t('translation.suggestion.acceptEditedLabel', names)}
                onClick={() => void accept(draft, false)}
              >
                {t('translation.suggestion.acceptEdited')}
              </Button>
              <Button
                size="sm"
                variant="ghost"
                disabled={busy}
                onClick={() => {
                  setEditing(false);
                  setDraft(s.value);
                }}
              >
                {t('translation.suggestion.cancelEdit')}
              </Button>
            </>
          ) : (
            <>
              <Button
                size="sm"
                variant="primary"
                loading={working === 'accept'}
                disabled={busy}
                aria-label={t('translation.suggestion.acceptLabel', names)}
                onClick={() => void accept(undefined, false)}
              >
                {t('translation.suggestion.accept')}
              </Button>
              <Button
                size="sm"
                variant="secondary"
                disabled={busy}
                aria-label={t('translation.suggestion.editLabel', names)}
                onClick={() => setEditing(true)}
              >
                {t('translation.suggestion.edit')}
              </Button>
              <Button
                size="sm"
                variant="ghost"
                loading={working === 'reject'}
                disabled={busy}
                aria-label={t('translation.suggestion.rejectLabel', names)}
                onClick={() => void reject()}
              >
                {t('translation.suggestion.reject')}
              </Button>
            </>
          )}
        </div>
      )}
    </div>
  );
}
