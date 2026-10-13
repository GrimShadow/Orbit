import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { folderOptions, useFolders } from '../library/folders';
import { Badge, Button, Input, Select, UploadSimple } from '../../ui';
import { Dropzone } from './Dropzone';
import { useUploadBatch } from './useUploadBatch';
import './upload.css';

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  const units = ['KB', 'MB', 'GB', 'TB'];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toFixed(1)} ${units[unit]}`;
}

export function UploadPage() {
  const { t } = useTranslation();
  const foldersQuery = useFolders();
  const [folderId, setFolderId] = useState('');
  const [tagsText, setTagsText] = useState('');
  const [files, setFiles] = useState<File[]>([]);
  const { state, start, reset } = useUploadBatch();

  const addFiles = (added: File[]) => setFiles((prev) => [...prev, ...added]);
  const removeFile = (index: number) => setFiles((prev) => prev.filter((_, i) => i !== index));

  const onSubmit = (e: FormEvent) => {
    e.preventDefault();
    const tags = tagsText
      .split(',')
      .map((s) => s.trim())
      .filter(Boolean);
    void start(files, { folderId: folderId || undefined, tags });
  };

  return (
    <div className="dam-page dam-page--medium dam-stack">
      <h1>{t('upload.title')}</h1>

      {state.status === 'idle' ? (
        <form className="dam-stack" onSubmit={onSubmit}>
          <Dropzone label={t('upload.dropLabel')} hint={t('upload.dropHint')} onFiles={addFiles} />

          {files.length > 0 && (
            <ul className="dam-upload__list">
              {files.map((f, i) => (
                <li key={`${f.name}-${i}`} className="dam-upload__item">
                  <span className="dam-upload__name">{f.name}</span>
                  <span className="dam-hint">{formatBytes(f.size)}</span>
                  <Button type="button" variant="ghost" onClick={() => removeFile(i)}>
                    {t('upload.remove')}
                  </Button>
                </li>
              ))}
            </ul>
          )}

          <div className="dam-upload__form">
            <Select
              label={t('upload.folder')}
              value={folderId}
              onChange={(e) => setFolderId(e.target.value)}
              options={[
                { value: '', label: t('upload.noFolder') },
                ...(foldersQuery.data ? folderOptions(foldersQuery.data) : []),
              ]}
            />
            <Input
              label={t('upload.tags')}
              hint={t('upload.tagsHint')}
              value={tagsText}
              onChange={(e) => setTagsText(e.target.value)}
            />
          </div>

          <Button type="submit" variant="primary" disabled={files.length === 0}>
            <UploadSimple size={16} weight="bold" aria-hidden="true" />
            {t('upload.start')}
          </Button>
        </form>
      ) : (
        <div className="dam-stack">
          <ul className="dam-upload__list">
            {state.items.map((item, i) => (
              <li key={i} className="dam-upload__item">
                <span className="dam-upload__name">{item.fileName}</span>
                {item.status === 'uploading' ? (
                  <progress value={item.progress} max={1} aria-label={t('upload.progressLabel', { name: item.fileName })} />
                ) : (
                  <Badge>{t(`upload.status.${item.status}`)}</Badge>
                )}
                {item.error && (
                  <span role="alert" className="dam-hint">
                    {item.error}
                  </span>
                )}
              </li>
            ))}
          </ul>

          {state.status === 'done' && (
            <div className="dam-upload__done">
              <Link to="/library">{t('upload.goToLibrary')}</Link>
              <Button
                variant="secondary"
                onClick={() => {
                  reset();
                  setFiles([]);
                  setTagsText('');
                }}
              >
                {t('upload.uploadMore')}
              </Button>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
