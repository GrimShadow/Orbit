import { useId, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Badge, Button, Modal, WarningCircle } from '../../ui';
import { languageName, useEnabledLanguages } from '../translation/languages';
import { GLOSSARY_LIMIT, useImportGlossary, type ImportOutcome } from './glossaryApi';
import { IMPORT_MAX_LINES, parseGlossaryLines } from './glossaryModel';
import './glossary.css';

const PREVIEW_ROWS = 8;

function Body({ onClose }: { onClose: () => void }) {
  const { t, i18n } = useTranslation();
  const languages = useEnabledLanguages();
  const runImport = useImportGlossary();
  const areaId = useId();
  const [text, setText] = useState('');
  const [progress, setProgress] = useState<{ done: number; total: number } | null>(null);
  const [outcome, setOutcome] = useState<ImportOutcome | null>(null);

  const parsed = useMemo(() => parseGlossaryLines(text, languages), [text, languages]);
  const locked = parsed.entries.filter((e) => e.input.mode === 'locked').length;
  const mapped = parsed.entries.length - locked;
  const working = progress !== null && outcome === null;

  const start = async () => {
    const entries = parsed.entries.map((e) => e.input);
    setProgress({ done: 0, total: entries.length });
    const result = await runImport(entries, (done, total) => setProgress({ done, total }));
    setOutcome(result);
  };

  if (outcome) {
    return (
      <div className="dam-stack dam-dialogform dam-gimport">
        <p role="status" aria-live="polite" className="dam-gimport__result">
          {t('glossary.import.created', { count: outcome.created })}
        </p>
        {outcome.limitReached && (
          <p className="dam-error" role="alert">
            <WarningCircle size={14} weight="bold" aria-hidden="true" />
            {t('glossary.import.limit', { max: GLOSSARY_LIMIT.toLocaleString(i18n.language), count: outcome.skipped })}
          </p>
        )}
        {outcome.failed.length > 0 && (
          <div>
            <p className="dam-label">{t('glossary.import.failedTitle', { count: outcome.failed.length })}</p>
            <ul className="dam-gimport__errors">
              {outcome.failed.map((f, i) => (
                <li key={i}>
                  <strong>{f.term}</strong> <span className="dam-hint">{f.reason}</span>
                </li>
              ))}
            </ul>
          </div>
        )}
        <div className="dam-dialogform__actions">
          <Button variant="primary" onClick={onClose}>
            {t('glossary.import.done')}
          </Button>
        </div>
      </div>
    );
  }

  return (
    <div className="dam-stack dam-dialogform dam-gimport">
      <p className="dam-hint">{t('glossary.import.intro')}</p>
      <pre className="dam-gimport__example" aria-label={t('glossary.import.exampleLabel')}>
        {t('glossary.import.example')}
      </pre>
      <div className="dam-field">
        <label className="dam-label" htmlFor={areaId}>
          {t('glossary.import.label')}
        </label>
        <textarea
          id={areaId}
          className="dam-input dam-gimport__area"
          rows={7}
          spellCheck={false}
          value={text}
          disabled={working}
          aria-describedby={`${areaId}-hint`}
          onChange={(e) => setText(e.target.value)}
        />
        <span id={`${areaId}-hint`} className="dam-hint">
          {t('glossary.import.languagesHint', {
            languages: languages.map((l) => `${l} (${languageName(l, i18n.language)})`).join(', '),
            max: IMPORT_MAX_LINES,
          })}
        </span>
      </div>

      <div aria-live="polite" className="dam-gimport__preview">
        {text.trim() === '' ? null : (
          <>
            <p className="dam-gimport__summary">
              {parsed.entries.length > 0
                ? t('glossary.import.summary', { count: parsed.entries.length, locked, mapped })
                : t('glossary.import.nothing')}
            </p>
            {parsed.truncated && <p className="dam-hint">{t('glossary.import.truncated', { max: IMPORT_MAX_LINES })}</p>}
            {parsed.entries.length > 0 && (
              <ul className="dam-gimport__list" aria-label={t('glossary.import.previewLabel')}>
                {parsed.entries.slice(0, PREVIEW_ROWS).map((e) => (
                  <li key={e.line}>
                    <Badge size="sm" tone={e.input.mode === 'locked' ? 'neutral' : 'brand'}>
                      {t(`glossary.mode.${e.input.mode}`)}
                    </Badge>
                    <span className="dam-gimport__term">{e.input.term}</span>
                    {e.input.mode === 'mapped' && (
                      <span className="dam-hint">
                        {Object.entries(e.input.translations)
                          .map(([l, v]) => `${l}: ${v}`)
                          .join(' · ')}
                      </span>
                    )}
                  </li>
                ))}
                {parsed.entries.length > PREVIEW_ROWS && (
                  <li className="dam-hint">{t('glossary.import.more', { count: parsed.entries.length - PREVIEW_ROWS })}</li>
                )}
              </ul>
            )}
            {parsed.errors.length > 0 && (
              <div role="alert">
                <p className="dam-label dam-gimport__errtitle">
                  <WarningCircle size={14} weight="bold" aria-hidden="true" />
                  {t('glossary.import.errors', { count: parsed.errors.length })}
                </p>
                <ul className="dam-gimport__errors">
                  {parsed.errors.map((e) => (
                    <li key={e.line}>
                      <span className="dam-gimport__line">{t('glossary.import.line', { line: e.line })}</span>{' '}
                      {t(`glossary.import.error.${e.code}`, { detail: e.detail })}
                    </li>
                  ))}
                </ul>
              </div>
            )}
          </>
        )}
      </div>

      {working && (
        <p role="status" aria-live="polite">
          {t('glossary.import.progress', { done: progress.done, total: progress.total })}
        </p>
      )}
      <div className="dam-dialogform__actions">
        <Button variant="ghost" onClick={onClose} disabled={working}>
          {t('glossary.cancel')}
        </Button>
        <Button variant="primary" disabled={parsed.entries.length === 0} loading={working} onClick={() => void start()}>
          {t('glossary.import.submit', { count: parsed.entries.length })}
        </Button>
      </div>
    </div>
  );
}

/** Paste a list of terms (one per line) and add them all: a term alone is locked, `term | hi=... | ta=...` is mapped.
 * The preview shows what will be created and every line that cannot be, before anything is sent. */
export function GlossaryImport({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  return (
    <Modal open={open} title={t('glossary.import.title')} closeLabel={t('glossary.close')} onClose={onClose}>
      <Body onClose={onClose} />
    </Modal>
  );
}
