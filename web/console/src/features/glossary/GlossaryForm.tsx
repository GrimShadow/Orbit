import { ApiError } from '@dam/sdk-js';
import { useId, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Input, Modal, Plus, Trash, WarningCircle } from '../../ui';
import { languageName, useEnabledLanguages } from '../translation/languages';
import type { GlossaryMode, GlossaryTerm } from '../translation/translationTypes';
import { isLimitError, useSaveGlossaryTerm } from './glossaryApi';
import { emptyDraft, toDraft, toInput, validateDraft, type DraftErrors, type TermDraft } from './glossaryModel';
import './glossary.css';

interface Props {
  open: boolean;
  /** The term being edited; `null` adds a new one. */
  term: GlossaryTerm | null;
  onClose: () => void;
  onSaved: (term: GlossaryTerm, created: boolean) => void;
}

function Body({ term, onClose, onSaved }: Omit<Props, 'open'>) {
  const { t, i18n } = useTranslation();
  const languages = useEnabledLanguages();
  const save = useSaveGlossaryTerm();
  const base = useId();
  const [draft, setDraft] = useState<TermDraft>(() => (term ? toDraft(term) : emptyDraft()));
  const [attempted, setAttempted] = useState(false);
  const [serverError, setServerError] = useState<string | null>(null);

  const errors: DraftErrors = attempted ? validateDraft(draft) : {};
  const used = new Set(draft.rows.map((r) => r.language));
  const free = languages.filter((l) => !used.has(l));

  const setMode = (mode: GlossaryMode) =>
    setDraft((d) => ({
      ...d,
      mode,
      // Choosing "mapped" starts with one rendering to fill in.
      rows: mode === 'mapped' && d.rows.length === 0 ? [{ language: free[0] ?? '', text: '' }] : d.rows,
    }));
  const setRow = (i: number, patch: Partial<{ language: string; text: string }>) =>
    setDraft((d) => ({ ...d, rows: d.rows.map((r, j) => (j === i ? { ...r, ...patch } : r)) }));

  const submit = (e: FormEvent) => {
    e.preventDefault();
    setAttempted(true);
    setServerError(null);
    if (Object.keys(validateDraft(draft)).length > 0) return;
    save.mutate(
      { id: term?.id, body: toInput(draft) },
      {
        onSuccess: (saved) => onSaved(saved, !term),
        onError: (err) => {
          if (isLimitError(err)) setServerError(t('glossary.limitReached'));
          else if (err instanceof ApiError && err.status === 409)
            setServerError(t('glossary.duplicate', { term: draft.term.trim() }));
          else setServerError(t('glossary.form.saveFailed', { reason: err instanceof ApiError ? err.detail || err.title : '' }));
        },
      },
    );
  };

  return (
    <form className="dam-stack dam-dialogform dam-gform" onSubmit={submit} noValidate>
      <Input
        label={t('glossary.form.term')}
        hint={t('glossary.form.termHint')}
        value={draft.term}
        autoComplete="off"
        aria-required="true"
        error={errors.term ? t('glossary.form.termRequired') : undefined}
        onChange={(e) => setDraft((d) => ({ ...d, term: e.target.value }))}
      />

      <fieldset className="dam-gmode" aria-describedby={`${base}-modehint`}>
        <legend className="dam-label">{t('glossary.form.mode')}</legend>
        <ul className="dam-gmode__list">
          {(['locked', 'mapped'] as const).map((m) => (
            <li key={m}>
              {/* eslint-disable-next-line jsx-a11y/label-has-associated-control -- the radio is nested; the name is the text inside */}
              <label className={`dam-gmode__opt${draft.mode === m ? ' dam-gmode__opt--checked' : ''}`}>
                <input type="radio" name={`${base}-mode`} value={m} checked={draft.mode === m} onChange={() => setMode(m)} />
                <span>
                  <span className="dam-gmode__name">{t(`glossary.mode.${m}`)}</span>
                  <span className="dam-gmode__desc">{t(`glossary.mode.${m}Desc`)}</span>
                </span>
              </label>
            </li>
          ))}
        </ul>
        <span id={`${base}-modehint`} className="dam-sr-only">
          {t('glossary.explain')}
        </span>
      </fieldset>

      {draft.mode === 'mapped' && (
        <fieldset className="dam-grows" aria-describedby={errors.rows ? `${base}-rows-error` : undefined}>
          <legend className="dam-label">{t('glossary.form.renderings')}</legend>
          <ul className="dam-grows__list">
            {draft.rows.map((row, i) => {
              const options = languages.filter((l) => l === row.language || !used.has(l));
              return (
                <li key={i} className="dam-grows__row">
                  <span className="dam-select-wrap">
                    <select
                      className="dam-select"
                      aria-label={t('glossary.form.rowLanguage', { n: i + 1 })}
                      value={row.language}
                      onChange={(e) => setRow(i, { language: e.target.value })}
                    >
                      {row.language === '' && <option value="">{t('glossary.form.chooseLanguage')}</option>}
                      {options.map((l) => (
                        <option key={l} value={l}>
                          {`${languageName(l, i18n.language)} (${l})`}
                        </option>
                      ))}
                    </select>
                  </span>
                  <input
                    className="dam-input"
                    aria-label={t('glossary.form.rowText', { n: i + 1 })}
                    lang={row.language || undefined}
                    value={row.text}
                    autoComplete="off"
                    onChange={(e) => setRow(i, { text: e.target.value })}
                  />
                  <button
                    type="button"
                    className="dam-btn dam-btn--ghost dam-btn--sm dam-btn--icon"
                    aria-label={t('glossary.form.removeRow', { n: i + 1 })}
                    title={t('glossary.form.removeRow', { n: i + 1 })}
                    onClick={() => setDraft((d) => ({ ...d, rows: d.rows.filter((_, j) => j !== i) }))}
                  >
                    <Trash size={16} aria-hidden="true" />
                  </button>
                </li>
              );
            })}
          </ul>
          {errors.rows && (
            <span id={`${base}-rows-error`} className="dam-error" role="alert">
              <WarningCircle size={14} weight="bold" aria-hidden="true" />
              {t(`glossary.form.${errors.rows}`)}
            </span>
          )}
          {free.length > 0 && (
            <div>
              <Button
                size="sm"
                variant="secondary"
                onClick={() => setDraft((d) => ({ ...d, rows: [...d.rows, { language: free[0]!, text: '' }] }))}
              >
                <Plus size={14} weight="bold" aria-hidden="true" />
                {t('glossary.form.addLanguage')}
              </Button>
            </div>
          )}
        </fieldset>
      )}

      <Input
        label={t('glossary.form.note')}
        hint={t('glossary.form.noteHint')}
        value={draft.note}
        autoComplete="off"
        onChange={(e) => setDraft((d) => ({ ...d, note: e.target.value }))}
      />

      {serverError && (
        <p className="dam-error" role="alert">
          <WarningCircle size={14} weight="bold" aria-hidden="true" />
          {serverError}
        </p>
      )}
      <div className="dam-dialogform__actions">
        <Button variant="ghost" onClick={onClose}>
          {t('glossary.cancel')}
        </Button>
        <Button type="submit" variant="primary" loading={save.isPending}>
          {t(term ? 'glossary.form.save' : 'glossary.form.add')}
        </Button>
      </div>
    </form>
  );
}

/** Add or edit one glossary term in a dialog. The form restarts from the term it is opened for each time. */
export function GlossaryForm({ open, term, onClose, onSaved }: Props) {
  const { t } = useTranslation();
  return (
    <Modal
      open={open}
      title={t(term ? 'glossary.form.editTitle' : 'glossary.form.addTitle')}
      closeLabel={t('glossary.close')}
      onClose={onClose}
    >
      <Body term={term} onClose={onClose} onSaved={onSaved} />
    </Modal>
  );
}
