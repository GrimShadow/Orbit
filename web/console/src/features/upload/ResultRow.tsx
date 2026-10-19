import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { formatBytes } from '../../lib/format';
import { Badge, WarningCircle, type BadgeTone } from '../../ui';
import { FileThumb } from './QueueRow';
import type { FileStatus, UploadItemState } from './useUploadBatch';

export const FAILED = new Set<FileStatus>(['failed', 'blocked', 'rejected']);

const TONE: Record<FileStatus, BadgeTone> = {
  uploading: 'info',
  finalizing: 'info',
  ingested: 'success',
  duplicate: 'warning',
  blocked: 'danger',
  failed: 'danger',
  rejected: 'danger',
};

/** One file in the progress/results view: its status as a tone-coded badge, a progress bar while it moves, and a link to
 * the asset (or, for a duplicate, to the one that already holds the same bytes). */
export function ResultRow({ item }: { item: UploadItemState }) {
  const { t } = useTranslation();
  const active = item.status === 'uploading' || item.status === 'finalizing';
  const failed = FAILED.has(item.status);
  return (
    <li className={`dam-queue__row dam-queue__row--result${failed ? ' dam-queue__row--problem' : ''}`}>
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
      </div>
      <div className="dam-queue__status">
        <Badge tone={TONE[item.status]}>{t(`upload.status.${item.status}`)}</Badge>
        {item.status === 'ingested' && item.assetId && (
          <Link className="dam-btn dam-btn--ghost dam-btn--sm" to={`/library/assets/${item.assetId}`}>
            {t('upload.open')}
          </Link>
        )}
        {item.status === 'duplicate' && item.duplicateOfAssetId && (
          <Link className="dam-btn dam-btn--ghost dam-btn--sm" to={`/library/assets/${item.duplicateOfAssetId}`}>
            {t('upload.viewExisting')}
          </Link>
        )}
      </div>
      {item.error && (
        <p className="dam-queue__alert" role="alert">
          <WarningCircle size={16} weight="fill" aria-hidden="true" />
          <span>{item.error}</span>
        </p>
      )}
    </li>
  );
}
