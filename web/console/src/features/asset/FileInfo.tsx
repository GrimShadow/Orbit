import { useTranslation } from 'react-i18next';
import { LoadingRegion, Skeleton } from '../../ui';
import type { AssetDto } from './assetApi';
import { useAssetVersions } from './assetApi';
import { buildFileFacts, summaryLine } from './fileFacts';

/** Read-only "File information" for an asset's current version - size, dimensions, dates, checksum, scan result and
 * kind-specific facts (image, video/audio, PDF/Office) - from data the ingest pipeline already stored. */
export function FileInfo({ asset }: { asset: AssetDto }) {
  const { t, i18n } = useTranslation();
  const versionsQuery = useAssetVersions(asset.id);

  if (versionsQuery.isPending)
    return (
      <LoadingRegion>
        <Skeleton height={120} />
      </LoadingRegion>
    );
  const version = versionsQuery.data?.find((v) => v.isCurrent);
  if (versionsQuery.error || !version) return null; // an asset with no file yet has nothing to describe

  const groups = buildFileFacts(asset, version, i18n.language, t as (k: string, o?: Record<string, unknown>) => string);
  return (
    <section className="dam-fileinfo" aria-labelledby="dam-fileinfo-title">
      <h2 id="dam-fileinfo-title" className="dam-fileinfo__title">
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
    </section>
  );
}

/** "JPEG · 1920 × 1080 · 406.3 KB" under the page title - the quick answer before the full table. */
export function AssetSummary({ assetId }: { assetId: string }) {
  const version = useAssetVersions(assetId).data?.find((v) => v.isCurrent);
  return version ? <p className="dam-asset-summary">{summaryLine(version)}</p> : null;
}
