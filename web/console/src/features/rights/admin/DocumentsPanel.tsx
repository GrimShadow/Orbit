import { FileText, Spinner } from '@phosphor-icons/react';
import { useRef, useState, type ChangeEvent, type DragEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useMe } from '../../../api/me';
import { formatBytes } from '../../../lib/format';
import { relativeTime } from '../../../lib/relativeTime';
import {
  Button,
  DownloadSimple,
  IconButton,
  Modal,
  Skeleton,
  Trash,
  UploadSimple,
  WarningCircle,
  X,
  useToast,
} from '../../../ui';
import {
  RightsApiError,
  useDeleteRightsDocument,
  useDocumentLink,
  useRightsDocuments,
  useUploadRightsDocument,
  type Rights,
  type RightsDocument,
} from '../rightsApi';

/** The release-document rules the server enforces, mirrored so a bad file is turned away before it is sent. */
export const DOCUMENT_TYPES: Record<string, string> = {
  pdf: 'application/pdf',
  png: 'image/png',
  jpg: 'image/jpeg',
  jpeg: 'image/jpeg',
  docx: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
  txt: 'text/plain',
};
export const MAX_DOCUMENT_BYTES = 25 * 1024 * 1024;
const ACCEPT = Object.keys(DOCUMENT_TYPES)
  .map((e) => `.${e}`)
  .join(',');

const extensionOf = (name: string) => (name.includes('.') ? name.slice(name.lastIndexOf('.') + 1).toLowerCase() : '');

export type FileProblem = 'type' | 'mismatch' | 'empty' | 'size';

/** Why this file cannot be a release document, or null when it can. */
export function fileProblem(file: File): FileProblem | null {
  const expected = DOCUMENT_TYPES[extensionOf(file.name)];
  if (!expected) return 'type';
  const declared = file.type.split(';')[0]!.trim().toLowerCase();
  if (declared && declared !== expected) return 'mismatch';
  if (file.size === 0) return 'empty';
  if (file.size > MAX_DOCUMENT_BYTES) return 'size';
  return null;
}

/** The same file with the content type its extension implies, for browsers that leave `type` empty (often .docx). */
const typed = (file: File) => (file.type ? file : new File([file], file.name, { type: DOCUMENT_TYPES[extensionOf(file.name)] }));

const typeLabel = (name: string, contentType: string) => extensionOf(name).toUpperCase() || contentType;

interface Upload {
  key: number;
  name: string;
  state: 'queued' | 'uploading' | 'error';
  message?: string;
}

function DocumentRow({
  doc,
  rightsId,
  mine,
  onDelete,
}: {
  doc: RightsDocument;
  rightsId: string;
  mine: boolean;
  onDelete: () => void;
}) {
  const { t, i18n } = useTranslation();
  const { push } = useToast();
  const link = useDocumentLink(rightsId);
  const download = () =>
    link.mutate(doc.id, {
      // A fresh five-minute address each time; it forces a download, so the new tab only shows the browser's download UI.
      onSuccess: (l) => window.open(l.url, '_blank', 'noopener,noreferrer'),
      onError: (e) =>
        push({ title: t('rights.documents.downloadFailed'), message: e instanceof Error ? e.message : undefined, tone: 'error' }),
    });
  return (
    <li className="dam-rdoc">
      <FileText size={22} aria-hidden="true" className="dam-rdoc__icon" />
      <div className="dam-rdoc__text">
        <span className="dam-rdoc__name" title={doc.name}>
          {doc.name}
        </span>
        <span className="dam-rdoc__meta">
          {typeLabel(doc.name, doc.contentType)} · {formatBytes(doc.sizeBytes)} ·{' '}
          <time dateTime={doc.createdAt} title={new Date(doc.createdAt).toLocaleString(i18n.language)}>
            {mine
              ? t('rights.documents.addedByYou', { when: relativeTime(doc.createdAt, i18n.language) })
              : t('rights.documents.added', { when: relativeTime(doc.createdAt, i18n.language) })}
          </time>
        </span>
      </div>
      <div className="dam-rdoc__actions">
        <IconButton
          label={t('rights.documents.download', { name: doc.name })}
          size="sm"
          loading={link.isPending}
          onClick={download}
        >
          <DownloadSimple size={16} aria-hidden="true" />
        </IconButton>
        <IconButton label={t('rights.documents.delete', { name: doc.name })} size="sm" onClick={onDelete}>
          <Trash size={16} aria-hidden="true" />
        </IconButton>
      </div>
    </li>
  );
}

/** Release forms and licence files for one saved rights record: list, upload (button or drop), download, delete. */
export function DocumentsPanel({ rights, onDelete }: { rights: Rights; onDelete: (doc: RightsDocument) => void }) {
  const { t } = useTranslation();
  const { push } = useToast();
  const me = useMe();
  const query = useRightsDocuments(rights.id);
  const upload = useUploadRightsDocument(rights.id);
  const [uploads, setUploads] = useState<Upload[]>([]);
  const [over, setOver] = useState(false);
  const input = useRef<HTMLInputElement>(null);
  const queue = useRef<{ key: number; file: File }[]>([]);
  const running = useRef(false);
  const nextKey = useRef(1);

  const patch = (key: number, change: Partial<Upload> | null) =>
    setUploads((list) =>
      change ? list.map((u) => (u.key === key ? { ...u, ...change } : u)) : list.filter((u) => u.key !== key),
    );

  const problemText = (file: File, problem: FileProblem) =>
    t(`rights.documents.problem.${problem}`, { name: file.name, max: formatBytes(MAX_DOCUMENT_BYTES) });

  const errorText = (e: unknown) => {
    if (e instanceof RightsApiError) {
      const fields = Object.values(e.fields).flat().join(' ');
      return fields || e.detail || e.title;
    }
    return e instanceof Error ? e.message : t('rights.documents.uploadFailed');
  };

  // One file at a time: each is presigned, sent straight to storage, then confirmed, before the next begins.
  const drain = async () => {
    if (running.current) return;
    running.current = true;
    let done = 0;
    for (let next = queue.current.shift(); next; next = queue.current.shift()) {
      patch(next.key, { state: 'uploading' });
      try {
        await upload.mutateAsync(typed(next.file));
        patch(next.key, null);
        done++;
      } catch (e) {
        patch(next.key, { state: 'error', message: errorText(e) });
      }
    }
    running.current = false;
    if (done > 0) push({ title: t('rights.documents.uploaded', { count: done }), tone: 'success' });
  };

  const add = (files: File[]) => {
    const rejected: Upload[] = [];
    const accepted: { key: number; file: File }[] = [];
    for (const file of files) {
      const key = nextKey.current++;
      const problem = fileProblem(file);
      if (problem) rejected.push({ key, name: file.name, state: 'error', message: problemText(file, problem) });
      else accepted.push({ key, file });
    }
    setUploads((list) => [
      ...list,
      ...rejected,
      ...accepted.map((a) => ({ key: a.key, name: a.file.name, state: 'queued' as const })),
    ]);
    queue.current.push(...accepted);
    void drain();
  };

  const onPick = (e: ChangeEvent<HTMLInputElement>) => {
    add(Array.from(e.target.files ?? []));
    e.target.value = ''; // the same file can be chosen again
  };
  const onDrop = (e: DragEvent) => {
    e.preventDefault();
    setOver(false);
    add(Array.from(e.dataTransfer.files));
  };

  const docs = query.data ?? [];
  return (
    <section className="dam-rdocs" aria-labelledby={`rdocs-${rights.id}`}>
      <h3 id={`rdocs-${rights.id}`} className="dam-rdocs__title">
        {t('rights.documents.title')}
      </h3>
      <p className="dam-hint">{t('rights.documents.lead')}</p>

      {query.isPending ? (
        <div role="status" aria-live="polite" className="dam-stack dam-stack--fill">
          <span className="dam-sr-only">{t('rights.documents.loading')}</span>
          <Skeleton height={44} />
        </div>
      ) : query.isError ? (
        <div role="alert" className="dam-rwarn dam-rwarn--danger">
          <WarningCircle size={16} aria-hidden="true" />
          <span>{t('rights.documents.loadFailed')}</span>
          <Button size="sm" onClick={() => void query.refetch()}>
            {t('rights.retry')}
          </Button>
        </div>
      ) : docs.length === 0 ? (
        <p className="dam-rdocs__empty">{t('rights.documents.empty')}</p>
      ) : (
        <ul className="dam-rdoclist" aria-label={t('rights.documents.title')}>
          {docs.map((d) => (
            <DocumentRow
              key={d.id}
              doc={d}
              rightsId={rights.id}
              mine={!!me.data && d.uploadedBy === me.data.userId}
              onDelete={() => onDelete(d)}
            />
          ))}
        </ul>
      )}

      {/* Dropping is a pointer extra; the button beside it is the keyboard route. */}
      <div
        className={`dam-rdrop${over ? ' dam-rdrop--over' : ''}`}
        onDragOver={(e) => {
          e.preventDefault();
          setOver(true);
        }}
        onDragLeave={() => setOver(false)}
        onDrop={onDrop}
      >
        <UploadSimple size={20} aria-hidden="true" />
        <span>{t('rights.documents.drop')}</span>
        <Button size="sm" onClick={() => input.current?.click()}>
          {t('rights.documents.choose')}
        </Button>
        <input
          ref={input}
          type="file"
          multiple
          accept={ACCEPT}
          className="dam-sr-only"
          tabIndex={-1}
          aria-label={t('rights.documents.inputLabel')}
          onChange={onPick}
        />
      </div>
      <p className="dam-hint">{t('rights.documents.rules', { max: formatBytes(MAX_DOCUMENT_BYTES) })}</p>
      <p className="dam-hint dam-rdocs__scan">{t('rights.documents.notScanned')}</p>

      {uploads.length > 0 && (
        <ul className="dam-rupl" aria-label={t('rights.documents.uploads')}>
          {uploads.map((u) => (
            <li key={u.key} className={`dam-rupl__item dam-rupl__item--${u.state}`}>
              {u.state === 'error' ? (
                <WarningCircle size={16} aria-hidden="true" className="dam-rupl__icon" />
              ) : u.state === 'uploading' ? (
                <Spinner size={16} aria-hidden="true" className="dam-rupl__icon dam-rupl__spin" />
              ) : (
                <FileText size={16} aria-hidden="true" className="dam-rupl__icon" />
              )}
              <span className="dam-rupl__name">{u.name}</span>
              <span className="dam-rupl__state" role={u.state === 'error' ? 'alert' : 'status'}>
                {u.state === 'error' ? u.message : t(`rights.documents.state.${u.state}`)}
              </span>
              {u.state === 'error' && (
                <IconButton label={t('rights.documents.dismiss', { name: u.name })} size="sm" onClick={() => patch(u.key, null)}>
                  <X size={14} aria-hidden="true" />
                </IconButton>
              )}
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

/** Confirms, then deletes one release document. Rendered beside the editing drawer rather than inside it: React hands a
 * nested dialog's `close` event to the dialog around it, which would close the drawer too. */
export function DeleteDocumentDialog({
  rightsId,
  doc,
  onClose,
}: {
  rightsId: string;
  doc: RightsDocument | null;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const { push } = useToast();
  const remove = useDeleteRightsDocument(rightsId);
  return (
    <Modal open={doc !== null} title={t('rights.documents.deleteTitle')} closeLabel={t('rights.close')} onClose={onClose}>
      <div className="dam-stack dam-dialogform dam-rdlg">
        <p>{t('rights.documents.deleteBody', { name: doc?.name })}</p>
        <div className="dam-dialogform__actions">
          <Button variant="ghost" onClick={onClose}>
            {t('rights.cancel')}
          </Button>
          <Button
            variant="danger"
            loading={remove.isPending}
            onClick={() =>
              remove.mutate(doc!.id, {
                onSuccess: () => {
                  onClose();
                  push({ title: t('rights.documents.deleted'), tone: 'success' });
                },
                onError: (e) => {
                  onClose();
                  push({
                    title: t('rights.documents.deleteFailed'),
                    message: e instanceof Error ? e.message : undefined,
                    tone: 'error',
                  });
                },
              })
            }
          >
            {t('rights.documents.deleteConfirm')}
          </Button>
        </div>
      </div>
    </Modal>
  );
}
