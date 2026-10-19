import { useEffect, useMemo, useRef, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { formatBytes } from '../../lib/format';
import { Button, Card, CheckCircle, EmptyState, Info, PageHeader, Select, TagInput, UploadSimple, WarningCircle } from '../../ui';
import type { Json, Values } from '../asset/MetadataFields';
import { folderOptions, useFolders } from '../library/folders';
import { Dropzone } from './Dropzone';
import { QueueRow } from './QueueRow';
import { FAILED, ResultRow } from './ResultRow';
import { TypeDetails } from './TypeDetails';
import { useUploadBatch } from './useUploadBatch';
import { addToQueue, compactMetadata, MAX_FILES, revokePreview, withType, type QueueItem } from './uploadQueue';
import './upload.css';

const FOLDER_KEY = 'dam.upload.folderId';

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

export function UploadPage() {
  const { t } = useTranslation();
  const foldersQuery = useFolders();
  const [queue, setQueue] = useState<QueueItem[]>([]);
  const [folderChoice, setFolderChoice] = useState(rememberedFolder);
  const [tags, setTags] = useState<string[]>([]);
  const [metadata, setMetadata] = useState<Record<string, Values>>({});
  const [notice, setNotice] = useState<{ duplicates: number; overLimit: number } | null>(null);
  const { state, start, reset } = useUploadBatch();

  useEffect(() => {
    document.title = `${t('upload.title')} - ${t('app.name')}`;
  }, [t]);

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
    setTags([]);
    setMetadata({});
    setNotice(null);
    reset();
  };

  const kinds = ['images', 'video', 'audio', 'documents', 'models', 'bundles'].map((k) => t(`upload.kind.${k}`));

  return (
    <div className="dam-page dam-upload">
      <PageHeader title={t('upload.title')} description={t('upload.subtitle')} />

      {state.status === 'idle' ? (
        <form onSubmit={onSubmit} className="dam-upload__formwrap">
          <Dropzone
            compact={queue.length > 0}
            icon={<UploadSimple size={queue.length > 0 ? 22 : 32} weight="bold" aria-hidden="true" />}
            label={queue.length > 0 ? t('upload.addMore') : t('upload.dropLabel')}
            hint={queue.length > 0 ? t('upload.dropHintMore') : t('upload.dropHint')}
            activeLabel={t('upload.dropActive')}
            chooseLabel={t('upload.choose')}
            kinds={kinds}
            detail={t('upload.limits', { max: MAX_FILES })}
            onFiles={addFiles}
          />
          {notice && (
            <p className="dam-upload__notice" role="status">
              <Info size={16} weight="fill" aria-hidden="true" />
              <span>
                {notice.duplicates > 0 && <>{t('upload.skippedDuplicates', { count: notice.duplicates })} </>}
                {notice.overLimit > 0 && t('upload.overLimit', { count: notice.overLimit, max: MAX_FILES })}
              </span>
            </p>
          )}

          {queue.length > 0 && (
            <div className="dam-upload__layout">
              <section className="dam-upload__files" aria-labelledby="dam-upload-files">
                <div className="dam-upload__sectionhead">
                  <h2 id="dam-upload-files">{t('upload.filesHeading', { count: queue.length })}</h2>
                  <Button type="button" variant="ghost" size="sm" onClick={clearAll}>
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
                <Card as="section" aria-labelledby="dam-upload-dest" className="dam-upload__card">
                  <h2 id="dam-upload-dest">{t('upload.destination')}</h2>
                  <Select
                    label={t('upload.folder')}
                    value={folderId}
                    onChange={(e) => setFolderChoice(e.target.value)}
                    options={[{ value: '', label: t('upload.noFolder') }, ...(folders ? folderOptions(folders) : [])]}
                  />
                  <TagInput
                    label={t('upload.tags')}
                    hint={t('upload.tagsHint')}
                    value={tags}
                    onChange={setTags}
                    removeLabel={(tag) => t('asset.removeTag', { tag })}
                    listLabel={t('asset.tagList', { field: t('upload.tags') })}
                  />
                </Card>
                {countByType.length > 0 && (
                  <Card as="section" aria-labelledby="dam-upload-details" className="dam-upload__card">
                    <h2 id="dam-upload-details">{t('upload.detailsHeading')}</h2>
                    <p className="dam-hint">{t('upload.detailsHint')}</p>
                    <div className="dam-upload__accordion">
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
                    </div>
                  </Card>
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
                  <span className="dam-upload__problemcount">
                    <WarningCircle size={14} weight="fill" aria-hidden="true" />
                    {t('upload.problemCount', { count: problems })}
                  </span>
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
          {state.status === 'done' ? (
            <Card className="dam-upload__summarycard">
              <EmptyState
                titleAs="h2"
                icon={counts.failed > 0 ? <WarningCircle weight="fill" /> : <CheckCircle weight="fill" />}
                title={
                  counts.failed > 0
                    ? t('upload.doneWithProblems')
                    : counts.ready === 0
                      ? t('upload.doneNothingNew')
                      : t('upload.doneAll', { count: counts.ready })
                }
                description={t('upload.resultCounts', {
                  ready: counts.ready,
                  duplicate: counts.duplicate,
                  failed: counts.failed,
                })}
                action={
                  <>
                    <Link className="dam-btn dam-btn--primary" to="/library">
                      {t('upload.viewInLibrary')}
                    </Link>
                    {counts.failed > 0 && (
                      <Button variant="secondary" onClick={reviewFailed}>
                        {t('upload.reviewFailed', { count: counts.failed })}
                      </Button>
                    )}
                    <Button variant="secondary" onClick={uploadMore}>
                      {t('upload.uploadMore')}
                    </Button>
                  </>
                }
              />
            </Card>
          ) : (
            <Card className="dam-upload__progresscard">
              <div aria-live="polite">
                <h2>{t('upload.uploading', { done: counts.settled, total: state.items.length })}</h2>
              </div>
              <progress value={overall} max={1} aria-label={t('upload.overallProgress')} />
              <p className="dam-hint">
                {t('upload.resultCounts', { ready: counts.ready, duplicate: counts.duplicate, failed: counts.failed })}
              </p>
            </Card>
          )}

          <ul className="dam-queue" aria-label={t('upload.resultsHeading')}>
            {state.items.map((item) => (
              <ResultRow key={item.queueId} item={item} />
            ))}
          </ul>
        </section>
      )}
    </div>
  );
}
