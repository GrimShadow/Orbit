import { ArrowDown, ArrowUp } from '@phosphor-icons/react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, IconButton, Modal, Plus, Trash, WarningCircle } from '../../ui';
import {
  MAX_LEVEL,
  chapterRowsToDtos,
  chaptersToRows,
  moveRow,
  newChapterRow,
  validateChapterRows,
  type ChapterRow,
} from './knowledgeDraft';
import { errorReason, useSaveChapters } from './knowledgeApi';
import { CHAPTER_LIMIT, CHAPTER_TITLE_MAX, type ChapterDto } from './knowledgeTypes';

interface Props {
  open: boolean;
  documentId: string;
  chapters: ChapterDto[];
  onClose: () => void;
  onSaved: () => void;
}

function Body({ documentId, chapters, onClose, onSaved }: Omit<Props, 'open'>) {
  const { t } = useTranslation();
  const save = useSaveChapters();
  const [rows, setRows] = useState<ChapterRow[]>(() => chaptersToRows(chapters));
  const [attempted, setAttempted] = useState(false);
  const [serverError, setServerError] = useState<string | null>(null);

  const checked = validateChapterRows(rows);
  const visible = attempted ? checked : { rows: new Map<number, never>(), list: checked.list };
  const patch = (key: number, change: Partial<ChapterRow>) =>
    setRows((rs) => rs.map((r) => (r.key === key ? { ...r, ...change } : r)));
  const msg = (code: string | undefined) =>
    code ? t(`knowledge.chapters.errors.${code}`, { max: CHAPTER_TITLE_MAX, limit: CHAPTER_LIMIT }) : undefined;

  const submit = () => {
    setAttempted(true);
    setServerError(null);
    if (checked.rows.size > 0 || checked.list) return;
    save.mutate(
      { id: documentId, chapters: chapterRowsToDtos(rows) },
      { onSuccess: onSaved, onError: (e) => setServerError(t('knowledge.chapters.saveFailed', { reason: errorReason(e) })) },
    );
  };

  return (
    <div className="dam-stack dam-dialogform dam-knchapters">
      <p className="dam-hint">{t('knowledge.chapters.editHint')}</p>
      {rows.length === 0 ? (
        <p className="dam-hint">{t('knowledge.chapters.editEmpty')}</p>
      ) : (
        <ol className="dam-knrows">
          {rows.map((row, i) => {
            const e = visible.rows.get(row.key) ?? {};
            const n = i + 1;
            return (
              <li key={row.key} className="dam-knrow">
                <div className="dam-knrow__fields">
                  <div className="dam-field dam-knrow__title">
                    <label className="dam-label" htmlFor={`kn-title-${row.key}`}>
                      {t('knowledge.chapters.rowTitle', { n })}
                    </label>
                    <input
                      id={`kn-title-${row.key}`}
                      className="dam-input"
                      value={row.title}
                      autoComplete="off"
                      aria-invalid={e.title ? true : undefined}
                      onChange={(ev) => patch(row.key, { title: ev.target.value })}
                    />
                    {e.title && <span className="dam-error">{msg(e.title)}</span>}
                  </div>
                  <div className="dam-field">
                    <label className="dam-label" htmlFor={`kn-level-${row.key}`}>
                      {t('knowledge.chapters.rowLevel', { n })}
                    </label>
                    <span className="dam-select-wrap">
                      <select
                        id={`kn-level-${row.key}`}
                        className="dam-select"
                        value={row.level}
                        onChange={(ev) => patch(row.key, { level: ev.target.value })}
                      >
                        {Array.from({ length: MAX_LEVEL }, (_, l) => (
                          <option key={l + 1} value={String(l + 1)}>
                            {l + 1}
                          </option>
                        ))}
                      </select>
                    </span>
                  </div>
                  <div className="dam-field">
                    <label className="dam-label" htmlFor={`kn-from-${row.key}`}>
                      {t('knowledge.chapters.rowFrom', { n })}
                    </label>
                    <input
                      id={`kn-from-${row.key}`}
                      className="dam-input"
                      value={row.pageFrom}
                      inputMode="numeric"
                      autoComplete="off"
                      aria-invalid={e.pageFrom ? true : undefined}
                      onChange={(ev) => patch(row.key, { pageFrom: ev.target.value })}
                    />
                    {e.pageFrom && <span className="dam-error">{msg(e.pageFrom)}</span>}
                  </div>
                  <div className="dam-field">
                    <label className="dam-label" htmlFor={`kn-to-${row.key}`}>
                      {t('knowledge.chapters.rowTo', { n })}
                    </label>
                    <input
                      id={`kn-to-${row.key}`}
                      className="dam-input"
                      value={row.pageTo}
                      inputMode="numeric"
                      autoComplete="off"
                      aria-invalid={e.pageTo ? true : undefined}
                      onChange={(ev) => patch(row.key, { pageTo: ev.target.value })}
                    />
                    {e.pageTo && <span className="dam-error">{msg(e.pageTo)}</span>}
                  </div>
                </div>
                <div className="dam-knrow__tools">
                  <IconButton
                    size="sm"
                    label={t('knowledge.chapters.moveUp', { n })}
                    disabled={i === 0}
                    onClick={() => setRows((rs) => moveRow(rs, i, -1))}
                  >
                    <ArrowUp size={16} aria-hidden="true" />
                  </IconButton>
                  <IconButton
                    size="sm"
                    label={t('knowledge.chapters.moveDown', { n })}
                    disabled={i === rows.length - 1}
                    onClick={() => setRows((rs) => moveRow(rs, i, 1))}
                  >
                    <ArrowDown size={16} aria-hidden="true" />
                  </IconButton>
                  <IconButton
                    size="sm"
                    label={t('knowledge.chapters.remove', { n })}
                    onClick={() => setRows((rs) => rs.filter((r) => r.key !== row.key))}
                  >
                    <Trash size={16} aria-hidden="true" />
                  </IconButton>
                </div>
              </li>
            );
          })}
        </ol>
      )}
      <div>
        <Button
          size="sm"
          variant="secondary"
          disabled={rows.length >= CHAPTER_LIMIT}
          onClick={() => setRows((rs) => [...rs, newChapterRow({ level: rs.at(-1)?.level ?? '1' })])}
        >
          <Plus size={14} weight="bold" aria-hidden="true" />
          {t('knowledge.chapters.add')}
        </Button>
      </div>
      <p className="dam-hint" aria-live="polite">
        {t('knowledge.chapters.rowCount', { count: rows.length, limit: CHAPTER_LIMIT })}
      </p>
      {visible.list && (
        <p className="dam-error" role="alert">
          <WarningCircle size={14} weight="bold" aria-hidden="true" />
          {msg(visible.list)}
        </p>
      )}
      {attempted && checked.rows.size > 0 && (
        <p className="dam-error" role="alert">
          <WarningCircle size={14} weight="bold" aria-hidden="true" />
          {t('knowledge.chapters.fixRows', { count: checked.rows.size })}
        </p>
      )}
      {serverError && (
        <p className="dam-error" role="alert">
          <WarningCircle size={14} weight="bold" aria-hidden="true" />
          {serverError}
        </p>
      )}
      <div className="dam-dialogform__actions">
        <Button variant="ghost" onClick={onClose}>
          {t('knowledge.cancel')}
        </Button>
        <Button variant="primary" loading={save.isPending} onClick={submit}>
          {t('knowledge.chapters.save')}
        </Button>
      </div>
    </div>
  );
}

/** Enter or correct a document's chapters by hand: add, remove and reorder rows with a title, a level and the pages. Saving
 * marks the chapters as entered by hand, so a later re-index leaves them alone unless asked to replace them. */
export function ChapterEditor({ open, documentId, chapters, onClose, onSaved }: Props) {
  const { t } = useTranslation();
  return (
    <Modal open={open} title={t('knowledge.chapters.editTitle')} closeLabel={t('knowledge.close')} onClose={onClose}>
      <Body documentId={documentId} chapters={chapters} onClose={onClose} onSaved={onSaved} />
    </Modal>
  );
}
