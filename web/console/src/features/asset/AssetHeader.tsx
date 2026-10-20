import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { CaretRight } from '../../ui';
import { AiSuggestionsChip } from '../ai/asset/AiSuggestionsChip';
import { LicenceBadge } from '../rights/asset/LicenceBadge';
import { folderBreadcrumb, useFolders } from '../library/folders';
import type { AssetDto } from './assetApi';
import { useAssetVersions } from './assetApi';
import { summaryLine } from './fileFacts';
import { InlineTitle } from './InlineTitle';
import { LifecycleActions } from './LifecycleActions';
import { StatusBadge, TypeBadge } from './StatusBadge';

/** Library > folder > ... > this asset, each ancestor a real link back to that folder in the library. */
function Crumbs({ asset }: { asset: AssetDto }) {
  const { t } = useTranslation();
  const folders = useFolders().data;
  const chain = folders && asset.folderId ? folderBreadcrumb(folders, asset.folderId) : [];
  return (
    <nav aria-label={t('asset.breadcrumb')} className="dam-crumbs">
      <ol>
        <li>
          <Link to="/library">{t('nav.library')}</Link>
        </li>
        {chain.map((folder) => (
          <li key={folder.id}>
            <CaretRight aria-hidden="true" className="dam-crumbs__sep" size={12} weight="bold" />
            <Link to={`/library?folder=${encodeURIComponent(folder.id)}`}>{folder.label}</Link>
          </li>
        ))}
        <li>
          <CaretRight aria-hidden="true" className="dam-crumbs__sep" size={12} weight="bold" />
          <span aria-current="page">{asset.title}</span>
        </li>
      </ol>
    </nav>
  );
}

/** "JPEG · 1920 × 1080 · 406.3 KB" under the title - the quick answer before the full table. */
function Summary({ assetId }: { assetId: string }) {
  const version = useAssetVersions(assetId).data?.find((v) => v.isCurrent);
  return version ? <p className="dam-asset-summary">{summaryLine(version)}</p> : null;
}

export function AssetHeader({
  asset,
  onReviewRights,
  onReviewAi,
}: {
  asset: AssetDto;
  onReviewRights?: () => void;
  onReviewAi?: () => void;
}) {
  return (
    <header className="dam-asset-head">
      <Crumbs asset={asset} />
      <div className="dam-asset-head__row">
        <div className="dam-asset-head__main">
          <InlineTitle assetId={asset.id} title={asset.title} editable={asset.status !== 'deleted'} />
          <div className="dam-asset-head__meta">
            <StatusBadge status={asset.status} />
            <LicenceBadge assetId={asset.id} onReview={onReviewRights} />
            {onReviewAi && asset.status !== 'deleted' && (
              <AiSuggestionsChip assetId={asset.id} type={asset.type} onOpen={onReviewAi} />
            )}
            <TypeBadge type={asset.type} />
            <Summary assetId={asset.id} />
          </div>
        </div>
        <LifecycleActions assetId={asset.id} status={asset.status} onReviewRights={onReviewRights} />
      </div>
    </header>
  );
}
