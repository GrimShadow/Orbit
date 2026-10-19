import { useTranslation } from 'react-i18next';
import { Card, EmptyState, LoadingRegion, Skeleton, WarningCircle } from '../../ui';
import type { AssetDto } from './assetApi';
import { useAssetVersions } from './assetApi';
import { buildFileFacts } from './fileFacts';

/** Read-only "File information" for an asset's current version - size, dimensions, dates, checksum, scan result and
 * kind-specific facts (image, video/audio, PDF/Office) - from data the ingest pipeline already stored. */
export function FileInfo({ asset }: { asset: AssetDto }) {
  const { t, i18n } = useTranslation();
  const versionsQuery = useAssetVersions(asset.id);

  if (versionsQuery.isPending)
    return (
      <LoadingRegion label={t('asset.fileInfoLoading')}>
        <Card padding="md">
          <div className="dam-stack dam-stack--fill">
            <Skeleton width="40%" height={16} />
            <Skeleton height={20} />
            <Skeleton height={20} />
            <Skeleton height={20} />
          </div>
        </Card>
      </LoadingRegion>
    );
  if (versionsQuery.error)
    return (
      <Card as="section" padding="md" aria-labelledby="dam-fileinfo-title">
        <h2 id="dam-fileinfo-title" className="dam-metasection__title">
          {t('asset.fileInfo')}
        </h2>
        <EmptyState icon={<WarningCircle />} titleAs="p" title={t('asset.versionsFailed')} />
      </Card>
    );
  const version = versionsQuery.data?.find((v) => v.isCurrent);
  if (!version) return null; // an asset with no file yet has nothing to describe

  const groups = buildFileFacts(asset, version, i18n.language, t as (k: string, o?: Record<string, unknown>) => string);
  return (
    <Card as="section" padding="md" className="dam-fileinfo" aria-labelledby="dam-fileinfo-title">
      <h2 id="dam-fileinfo-title" className="dam-metasection__title">
        {t('asset.fileInfo')}
      </h2>
      <div className="dam-fileinfo__groups">
        {groups.map((g) => (
          <div key={g.id} className="dam-fileinfo__group">
            <h3 className="dam-fileinfo__group-title">{t(`asset.factGroups.${g.id}`)}</h3>
            <dl className="dam-facts">
              {g.facts.map((f) => (
                <div key={f.label} className="dam-facts__row">
                  <dt>{g.id === 'camera' ? f.label : t(`asset.facts.${f.label}`)}</dt>
                  <dd className={f.mono ? 'dam-facts__mono' : undefined} title={f.title}>
                    {f.value}
                  </dd>
                </div>
              ))}
            </dl>
          </div>
        ))}
      </div>
    </Card>
  );
}
