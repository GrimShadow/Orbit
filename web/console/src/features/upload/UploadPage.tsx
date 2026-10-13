import { useEffect, useMemo, useRef, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { formatBytes } from '../../lib/format';
import { Badge, Button, CheckCircle, Input, Select, UploadSimple, XCircle } from '../../ui';
import type { Json, Values } from '../asset/MetadataFields';
import { folderOptions, useFolders } from '../library/folders';
import { Dropzone } from './Dropzone';
import { FileThumb, QueueRow } from './QueueRow';
import { TypeDetails } from './TypeDetails';
import { useUploadBatch, type UploadItemState } from './useUploadBatch';
import { addToQueue, compactMetadata, MAX_FILES, revokePreview, withType, type QueueItem } from './uploadQueue';
import './upload.css';

const FOLDER_KEY = 'dam.upload.folderId';
const FAILED = new Set(['failed', 'blocked', 'rejected']);

function rememberedFolder(): string {
  try {
    return localStorage.getItem(FOLDER_KEY) ?? '';
  } catch {
    return ''; // storage can be unavailable (private windows, blocked site data) - the page works without it
  }
}
function rememberFolder(id: string) {
  try {
    if (id) localStorage.setItem(FOLDER_KEY, id);
    else localStorage.removeItem(FOLDER_KEY);
  } catch {
    /* a remembered folder is a convenience only */
  }
}

function ResultRow({ item }: { item: UploadItemState }) {
  const { t } = useTranslation();
  const active = item.status === 'uploading' || item.status === 'finalizing';
  return (
    <li className={`dam-queue__row dam-queue__row--${FAILED.has(item.status) ? 'problem' : item.status}`}>
      <FileThumb type={item.type} previewUrl={item.previewUrl} />
      <div className="dam-queue__main">
        <span className="dam-queue__resulttitle">{item.title || item.fileName}</span>
        <div className="dam-queue__meta">
          <span className="dam-queue__filename" title={item.fileName}>
            {item.fileName}
          </span>
          <span>{formatBytes(item.size)}</span>
        </div>
        {active && (
          <progress
            className="dam-queue__progress"
            value={item.progress}
            max={1}
            aria-label={t('upload.progressLabel', { name: item.fileName })}
          />
        )}
        {item.error && (
          <p className="dam-queue__problem" role="alert">
            {item.error}
          </p>
        )}
      </div>
      <div className="dam-queue__status">
        {item.status === 'ingested' ? (
          <CheckCircle size={20} weight="fill" className="dam-queue__ok" aria-hidden="true" />
        ) : FAILED.has(item.status) ? (
          <XCircle size={20} weight="fill" className="dam-queue__bad" aria-hidden="true" />
        ) : null}
        <Badge>{t(`upload.status.${item.status}`)}</Badge>
        {item.status === 'ingested' && item.assetId && <Link to={`/library/assets/${item.assetId}`}>{t('upload.open')}</Link>}
        {item.status === 'duplicate' && item.duplicateOfAssetId && (
          <Link to={`/library/assets/${item.duplicateOfAssetId}`}>{t('upload.viewExisting')}</Link>
        )}
      </div>
    </li>
  );
}

export function UploadPage() {
  const { t } = useTranslation();
  const foldersQuery = useFolders();
  const [queue, setQueue] = useState<QueueItem[]>([]);
  const [folderChoice, setFolderChoice] = useState(rememberedFolder);
  const [tagsText, setTagsText] = useState('');
  const [metadata, setMetadata] = useState<Record<string, Values>>({});
  const [notice, setNotice] = useState<{ duplicates: number; overLimit: number } | null>(null);
  const { state, start, reset } = useUploadBatch();

  // Object URLs for previews are released when the page goes away; individual ones as their file leaves the queue.
  const queueRef = useRef(queue);
  useEffect(() => {
    queueRef.current = queue;
  }, [queue]);
  useEffect(() => () => queueRef.current.forEach(revokePreview), []);

  const folders = foldersQuery.data;
  const folderId = folders?.some((f) => f.id === folderChoice) ? folderChoice : '';
  const valid = useMemo(() => queue.filter((q) => q.problem === null), [queue]);
  const problems = queue.length - valid.length;
  const totalBytes = valid.reduce((sum, q) => sum + q.file.size, 0);
  const countByType = useMemo(() => {
    const counts = new Map<string, number>();
    for (const q of valid) counts.set(q.type, (counts.get(q.type) ?? 0) + 1);
    return [...counts.entries()];
  }, [valid]);

  const addFiles = (files: File[]) => {
    const result = addToQueue(queue, files);
    setQueue(result.queue);
    setNotice(result.duplicates + result.overLimit > 0 ? { duplicates: result.duplicates, overLimit: result.overLimit } : null);
  };
  const update = (id: string, change: (q: QueueItem) => QueueItem) =>
    setQueue((qs) => qs.map((q) => (q.id === id ? change(q) : q)));
  const remove = (item: QueueItem) => {
    revokePreview(item);
    setQueue((qs) => qs.filter((q) => q.id !== item.id));
  };
  const clearAll = () => {
    queue.forEach(revokePreview);
    setQueue([]);
    setNotice(null);
    setMetadata({});
  };

  const onSubmit = (e: FormEvent) => {
    e.preventDefault();
    if (valid.length === 0) return;
    const tags = tagsText
      .split(',')
      .map((s) => s.trim())
      .filter(Boolean);
    const metadataByType: Record<string, Record<string, unknown>> = {};
    for (const [type] of countByType) {
      const doc = compactMetadata(metadata[type] ?? {});
      if (doc) metadataByType[type] = doc;
    }
    rememberFolder(folderId);
    void start(valid, { folderId: folderId || undefined, tags, metadataByType });
  };

  const counts = {
    ready: state.items.filter((i) => i.status === 'ingested').length,
    duplicate: state.items.filter((i) => i.status === 'duplicate').length,
    failed: state.items.filter((i) => FAILED.has(i.status)).length,
    settled: state.items.filter((i) => i.status !== 'uploading' && i.status !== 'finalizing').length,
  };
  const totalSize = state.items.reduce((s, i) => s + i.size, 0) || 1;
  const overall =
    state.items.reduce((s, i) => s + (i.status === 'uploading' || i.status === 'finalizing' ? i.progress : 1) * i.size, 0) /
    totalSize;

  const reviewFailed = () => {
    const failedIds = new Set(state.items.filter((i) => FAILED.has(i.status)).map((i) => i.queueId));
    queue.filter((q) => !failedIds.has(q.id)).forEach(revokePreview);
    setQueue((qs) => qs.filter((q) => failedIds.has(q.id)));
    reset();
  };
  const uploadMore = () => {
    queue.forEach(revokePreview);
    setQueue([]);
    setTagsText('');
    setMetadata({});
    setNotice(null);
    reset();
  };

  return (
    <div className="dam-page dam-upload">
      <header className="dam-upload__header">
        <h1>{t('upload.title')}</h1>
        <p className="dam-hint">{t('upload.subtitle')}</p>
      </header>

      {state.status === 'idle' ? (
        <form onSubmit={onSubmit} className="dam-upload__formwrap">
          <Dropzone
            compact={queue.length > 0}
            icon={<UploadSimple size={28} weight="bold" aria-hidden="true" />}
            label={queue.length > 0 ? t('upload.addMore') : t('upload.dropLabel')}
            hint={queue.length > 0 ? t('upload.dropHintMore') : t('upload.dropHint')}
            detail={queue.length > 0 ? undefined : t('upload.limits', { max: MAX_FILES })}
            onFiles={addFiles}
          />
          {notice && (
            <p className="dam-upload__notice" role="status">
              {notice.duplicates > 0 && <span>{t('upload.skippedDuplicates', { count: notice.duplicates })} </span>}
              {notice.overLimit > 0 && <span>{t('upload.overLimit', { count: notice.overLimit, max: MAX_FILES })}</span>}
            </p>
          )}

          {queue.length > 0 && (
            <div className="dam-upload__layout">
              <section className="dam-upload__files" aria-labelledby="dam-upload-files">
                <div className="dam-upload__sectionhead">
                  <h2 id="dam-upload-files">{t('upload.filesHeading', { count: queue.length })}</h2>
                  <Button type="button" variant="ghost" onClick={clearAll}>
                    {t('upload.clearAll')}
                  </Button>
                </div>
                <ul className="dam-queue" aria-label={t('upload.filesHeading', { count: queue.length })}>
                  {queue.map((item) => (
                    <QueueRow
                      key={item.id}
                      item={item}
                      onTitle={(title) => update(item.id, (q) => ({ ...q, title }))}
                      onType={(type) => update(item.id, (q) => withType(q, type))}
                      onRemove={() => remove(item)}
                    />
                  ))}
                </ul>
              </section>

              <aside className="dam-upload__side">
                <section className="dam-upload__card" aria-labelledby="dam-upload-dest">
                  <h2 id="dam-upload-dest">{t('upload.destination')}</h2>
                  <Select
                    label={t('upload.folder')}
                    value={folderId}
                    onChange={(e) => setFolderChoice(e.target.value)}
                    options={[{ value: '', label: t('upload.noFolder') }, ...(folders ? folderOptions(folders) : [])]}
                  />
                  <Input
                    label={t('upload.tags')}
                    hint={t('upload.tagsHint')}
                    value={tagsText}
                    onChange={(e) => setTagsText(e.target.value)}
                  />
                </section>
                {countByType.length > 0 && (
                  <section className="dam-upload__card" aria-labelledby="dam-upload-details">
                    <h2 id="dam-upload-details">{t('upload.detailsHeading')}</h2>
                    <p className="dam-hint">{t('upload.detailsHint')}</p>
                    {countByType.map(([type, count]) => (
                      <TypeDetails
                        key={type}
                        type={type}
                        count={count}
                        values={metadata[type] ?? {}}
                        onChange={(name: string, value: Json) =>
                          setMetadata((m) => ({ ...m, [type]: { ...(m[type] ?? {}), [name]: value } }))
                        }
                      />
                    ))}
                  </section>
                )}
              </aside>
            </div>
          )}

          {queue.length > 0 && (
            <div className="dam-upload__bar">
              <p className="dam-upload__summary" aria-live="polite">
                <strong>{t('upload.summary', { count: valid.length })}</strong>
                <span className="dam-hint">{formatBytes(totalBytes)}</span>
                {problems > 0 && (
                  <span className="dam-upload__problemcount">{t('upload.problemCount', { count: problems })}</span>
                )}
              </p>
              <Button type="submit" variant="primary" disabled={valid.length === 0}>
                <UploadSimple size={16} weight="bold" aria-hidden="true" />
                {t('upload.startCount', { count: valid.length })}
              </Button>
            </div>
          )}
        </form>
      ) : (
        <section className="dam-upload__results" aria-label={t('upload.resultsHeading')}>
          <div className="dam-upload__progresscard" aria-live="polite">
            {state.status === 'done' ? (
              <h2>{counts.failed > 0 ? t('upload.doneWithProblems') : t('upload.doneAll')}</h2>
            ) : (
              <h2>{t('upload.uploading', { done: counts.settled, total: state.items.length })}</h2>
            )}
            <progress value={state.status === 'done' ? 1 : overall} max={1} aria-label={t('upload.overallProgress')} />
            <p className="dam-hint">
              {t('upload.resultCounts', { ready: counts.ready, duplicate: counts.duplicate, failed: counts.failed })}
            </p>
          </div>

          <ul className="dam-queue" aria-label={t('upload.resultsHeading')}>
            {state.items.map((item) => (
              <ResultRow key={item.queueId} item={item} />
            ))}
          </ul>

          {state.status === 'done' && (
            <div className="dam-upload__done">
              <Link className="dam-btn dam-btn--primary" to="/library">
                {t('upload.goToLibrary')}
              </Link>
              {counts.failed > 0 && (
                <Button variant="secondary" onClick={reviewFailed}>
                  {t('upload.reviewFailed', { count: counts.failed })}
                </Button>
              )}
              <Button variant="secondary" onClick={uploadMore}>
                {t('upload.uploadMore')}
              </Button>
            </div>
          )}
        </section>
      )}
    </div>
  );
}
