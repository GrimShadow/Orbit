import { useTranslation } from 'react-i18next';
import { Badge, Button, LoadingRegion, Skeleton } from '../../ui';
import { useAssetVersions, useRestoreVersion } from './assetApi';

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  const units = ['KB', 'MB', 'GB'];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toFixed(1)} ${units[unit]}`;
}

export function VersionsPanel({ assetId }: { assetId: string }) {
  const { t } = useTranslation();
  const versionsQuery = useAssetVersions(assetId);
  const restore = useRestoreVersion(assetId);

  if (versionsQuery.isPending)
    return (
      <LoadingRegion>
        <Skeleton height={80} />
      </LoadingRegion>
    );
  if (versionsQuery.error) return <p className="dam-hint">{t('asset.versionsFailed')}</p>;

  return (
    <ul className="dam-stack" aria-label={t('asset.versions')}>
      {versionsQuery.data.map((v) => (
        <li key={v.id} className="dam-upload__item">
          <span className="dam-upload__name">
            {t('asset.versionLabel', { n: v.versionNo })}
            {v.isCurrent && <Badge>{t('asset.current')}</Badge>}
          </span>
          <span className="dam-hint">{formatBytes(v.sizeBytes)}</span>
          <span className="dam-hint">{v.mime}</span>
          <span className="dam-hint">{new Date(v.createdAt).toLocaleString()}</span>
          {!v.isCurrent && (
            <Button variant="secondary" loading={restore.isPending} onClick={() => restore.mutate(v.versionNo)}>
              {t('asset.restore')}
            </Button>
          )}
        </li>
      ))}
    </ul>
  );
}
