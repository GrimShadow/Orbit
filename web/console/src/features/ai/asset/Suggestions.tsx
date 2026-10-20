import { ApiError } from '@dam/sdk-js';
import { Check, Info, X } from '@phosphor-icons/react';
import { useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, IconButton, useToast } from '../../../ui';
import { errorMessage } from '../../asset/assetApi';
import { useAcceptAll, useDecideSuggestion, type AiSuggestion } from '../aiApi';
import { Disclosure } from './Disclosure';
import { languageName, percent } from './aiFormat';

const isPending = (s: AiSuggestion) => s.status === 'pending';

/**
 * What an editor decides: tag suggestions (chips with a confidence, add or dismiss each, add all) and a language suggestion.
 * Decided ones fold into a quiet list. A reader sees the same chips without the buttons. Nothing here changes the asset
 * unless a person presses a button (ADR-0035: tags and language are suggestions; transcripts and OCR text are derived data).
 */
export function Suggestions({
  assetId,
  suggestions,
  canDecide,
  expectTags,
}: {
  assetId: string;
  suggestions: AiSuggestion[];
  canDecide: boolean;
  /** The run looked for tags: say so when it found none worth proposing. */
  expectTags: boolean;
}) {
  const { t, i18n } = useTranslation();
  const { push } = useToast();
  const qc = useQueryClient();
  const decide = useDecideSuggestion(assetId);
  const acceptAll = useAcceptAll(assetId);
  const root = useRef<HTMLDivElement>(null);
  const [busy, setBusy] = useState<ReadonlySet<string>>(new Set());
  const [allBusy, setAllBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notes, setNotes] = useState<string[]>([]);

  const tags = suggestions.filter((s) => s.kind === 'tag');
  const languages = suggestions.filter((s) => s.kind === 'language');
  const pendingTags = tags.filter(isPending);
  const pendingLanguages = languages.filter(isPending);
  const decided = [...tags, ...languages].filter((s) => !isPending(s));
  const order = [...pendingTags, ...pendingLanguages];
  const langName = (code: string) => languageName(code, i18n.language);

  // A decided chip leaves the list; keyboard focus should land on its neighbour, not fall to the top of the page.
  const refocus = useRef<string | 'heading' | null>(null);
  useEffect(() => {
    const want = refocus.current;
    if (!want || !root.current) return;
    const target =
      want === 'heading'
        ? root.current.querySelector<HTMLElement>('[data-ai-heading]')
        : root.current.querySelector<HTMLElement>(`[data-sid="${want}"]`);
    if (target) {
      refocus.current = null;
      target.focus();
    }
  }, [suggestions]);

  const fail = useCallback(
    (e: unknown) => {
      refocus.current = null;
      setError(t('aiAsset.tags.decideFailed', { reason: errorMessage(e) }));
      // Someone else may have decided it already (409): show what is true now.
      if (e instanceof ApiError && e.status === 409) void qc.invalidateQueries({ queryKey: ['ai', 'asset', assetId] });
    },
    [qc, assetId, t],
  );

  const choose = (s: AiSuggestion, decision: 'accept' | 'reject') => {
    const i = order.findIndex((x) => x.id === s.id);
    const next = order[i + 1] ?? order[i - 1];
    refocus.current = next ? next.id : 'heading';
    setError(null);
    setNotes([]);
    setBusy((b) => new Set(b).add(s.id));
    decide.mutate(
      { suggestionId: s.id, decision },
      {
        onSuccess: (r) => {
          setNotes(r.notes);
          const language = s.kind === 'language';
          const key = `aiAsset.${language ? 'language' : 'tags'}.${decision === 'accept' ? 'added' : 'dismissed'}`;
          const vars = { tag: s.value, language: langName(s.value) };
          push(
            decision === 'accept'
              ? { title: t(key, vars), tone: 'success' }
              : { title: t(key, vars), tone: 'info', durationMs: 3000 },
          );
        },
        onError: fail,
        onSettled: () =>
          setBusy((b) => {
            const n = new Set(b);
            n.delete(s.id);
            return n;
          }),
      },
    );
  };

  const addAll = () => {
    refocus.current = 'heading';
    setError(null);
    setNotes([]);
    setAllBusy(true);
    acceptAll.mutate('tag', {
      onSuccess: (r) => {
        setNotes(r.notes);
        push({
          title: r.changed > 0 ? t('aiAsset.tags.addedAll', { count: r.changed }) : t('aiAsset.tags.addedNone'),
          tone: r.changed > 0 ? 'success' : 'info',
        });
      },
      onError: fail,
      onSettled: () => setAllBusy(false),
    });
  };

  if (suggestions.length === 0 && !expectTags) return null;

  return (
    <div ref={root} className="dam-ai-block">
      {(tags.length > 0 || expectTags) && (
        <section aria-labelledby={`dam-ai-tags-${assetId}`} className="dam-ai-sec">
          <div className="dam-ai-sec__head">
            <h3 id={`dam-ai-tags-${assetId}`} className="dam-ai-sub" tabIndex={-1} data-ai-heading>
              {t('aiAsset.tags.title')}
            </h3>
            {canDecide && pendingTags.length > 1 && (
              <Button
                size="sm"
                variant="secondary"
                loading={allBusy}
                disabled={busy.size > 0}
                aria-label={t('aiAsset.tags.addAllLabel', { count: pendingTags.length })}
                onClick={addAll}
              >
                {t('aiAsset.tags.addAll', { count: pendingTags.length })}
              </Button>
            )}
          </div>
          {pendingTags.length > 0 ? (
            <>
              <p className="dam-hint dam-ai-hint">{canDecide ? t('aiAsset.tags.hint') : t('aiAsset.tags.hintReader')}</p>
              <ul className="dam-ai-tags" aria-label={t('aiAsset.tags.list')}>
                {pendingTags.map((s) => {
                  const pct = percent(s.confidence);
                  const working = busy.has(s.id) || allBusy;
                  return (
                    <li key={s.id} className="dam-ai-tag" data-pending={working || undefined}>
                      <span className="dam-ai-tag__label">{s.value}</span>
                      <span className="dam-ai-tag__pct">
                        <span className="dam-sr-only">{t('aiAsset.tags.confidence')} </span>
                        {pct}%
                      </span>
                      {canDecide && (
                        <span className="dam-ai-tag__actions">
                          <IconButton
                            size="sm"
                            variant="ghost"
                            label={t('aiAsset.tags.add', { tag: s.value })}
                            data-sid={s.id}
                            disabled={working}
                            loading={busy.has(s.id) && decide.variables?.decision === 'accept'}
                            onClick={() => choose(s, 'accept')}
                          >
                            <Check size={14} weight="bold" aria-hidden="true" />
                          </IconButton>
                          <IconButton
                            size="sm"
                            variant="ghost"
                            label={t('aiAsset.tags.dismiss', { tag: s.value })}
                            disabled={working}
                            onClick={() => choose(s, 'reject')}
                          >
                            <X size={14} weight="bold" aria-hidden="true" />
                          </IconButton>
                        </span>
                      )}
                      <span className="dam-ai-tag__bar" aria-hidden="true" style={{ width: `${Math.max(pct, 4)}%` }} />
                    </li>
                  );
                })}
              </ul>
            </>
          ) : (
            <p className="dam-hint dam-ai-hint">{tags.length > 0 ? t('aiAsset.tags.allDone') : t('aiAsset.tags.none')}</p>
          )}
        </section>
      )}

      {pendingLanguages.length > 0 && (
        <section aria-labelledby={`dam-ai-lang-${assetId}`} className="dam-ai-sec">
          <h3 id={`dam-ai-lang-${assetId}`} className="dam-ai-sub" tabIndex={-1} data-ai-heading>
            {t('aiAsset.language.title')}
          </h3>
          {pendingLanguages.map((s) => (
            <div key={s.id} className="dam-ai-lang">
              <p className="dam-ai-lang__text">{t('aiAsset.language.looksLike', { language: langName(s.value) })}</p>
              {canDecide && (
                <span className="dam-ai-tag__actions">
                  <IconButton
                    size="sm"
                    variant="ghost"
                    label={t('aiAsset.language.accept', { language: langName(s.value) })}
                    data-sid={s.id}
                    disabled={busy.has(s.id)}
                    onClick={() => choose(s, 'accept')}
                  >
                    <Check size={14} weight="bold" aria-hidden="true" />
                  </IconButton>
                  <IconButton
                    size="sm"
                    variant="ghost"
                    label={t('aiAsset.language.dismiss', { language: langName(s.value) })}
                    disabled={busy.has(s.id)}
                    onClick={() => choose(s, 'reject')}
                  >
                    <X size={14} weight="bold" aria-hidden="true" />
                  </IconButton>
                </span>
              )}
            </div>
          ))}
        </section>
      )}

      {error && (
        <p className="dam-error dam-ai-error" role="alert">
          {error}
        </p>
      )}
      {notes.map((n) => (
        <p key={n} className="dam-ai-note" role="status">
          <Info size={16} weight="fill" aria-hidden="true" />
          {n}
        </p>
      ))}

      {decided.length > 0 && (
        <Disclosure title={t('aiAsset.tags.decided', { count: decided.length })}>
          <ul className="dam-ai-decided" aria-label={t('aiAsset.tags.decidedList')}>
            {decided.map((s) => (
              <li key={s.id} className="dam-ai-decided__item" data-status={s.status}>
                <span className="dam-ai-decided__value">
                  {s.kind === 'language' ? t('aiAsset.language.accepted', { language: langName(s.value) }) : s.value}
                </span>
                <span className="dam-ai-decided__status">
                  {s.status === 'accepted' ? t('aiAsset.tags.statusAccepted') : t('aiAsset.tags.statusRejected')}
                </span>
              </li>
            ))}
          </ul>
        </Disclosure>
      )}
    </div>
  );
}
