import { useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useParams } from 'react-router-dom';
import { Card, EmptyState, LoadingRegion, Skeleton, Tabs, WarningCircle } from '../../ui';
import { AssetHeader } from './AssetHeader';
import { AssetPreview } from './AssetPreview';
import { DetailsPanel } from './DetailsPanel';
import { MetadataForm } from './MetadataForm';
import { SaveBar } from './SaveBar';
import { VersionsPanel } from './VersionsPanel';
import { useAsset, type AssetDto } from './assetApi';
import { useAssetDraft } from './useAssetDraft';
import './asset.css';

function AssetDetail({ asset }: { asset: AssetDto }) {
  const { t } = useTranslation();
  const draft = useAssetDraft(asset);

  useEffect(() => {
    document.title = `${asset.title} - ${t('app.name')}`;
  }, [asset.title, t]);

  return (
    <div className="dam-page dam-asset">
      <AssetHeader asset={asset} />
      <div className="dam-asset-layout">
        <div className="dam-asset-layout__stage">
          <AssetPreview assetId={asset.id} type={asset.type} title={asset.title} status={asset.status} />
        </div>
        <div className="dam-asset-layout__side">
          <Tabs
            label={t('asset.tabs')}
            tabs={[
              { id: 'details', label: t('asset.detailsTab'), content: <DetailsPanel asset={asset} draft={draft} /> },
              {
                id: 'metadata',
                label: t('asset.metadataTab'),
                content: <MetadataForm assetType={asset.type} draft={draft} />,
              },
              { id: 'versions', label: t('asset.versionsTab'), content: <VersionsPanel assetId={asset.id} /> },
            ]}
          />
        </div>
      </div>
      {draft.dirty && <SaveBar saving={draft.saving} onSave={draft.save} onDiscard={draft.discard} />}
    </div>
  );
}

function AssetSkeleton() {
  const { t } = useTranslation();
  return (
    <div className="dam-page dam-asset">
      <LoadingRegion label={t('asset.loading')}>
        <div className="dam-asset-head">
          <Skeleton width={260} height={14} />
          <Skeleton width="50%" height={32} />
        </div>
        <div className="dam-asset-layout">
          <div className="dam-asset-layout__stage">
            <div className="dam-stage">
              <div className="dam-stage__loading">
                <Skeleton height="100%" />
              </div>
            </div>
          </div>
          <div className="dam-asset-layout__side">
            <Card>
              <div className="dam-stack dam-stack--fill">
                <Skeleton height={20} width="40%" />
                <Skeleton height={20} />
                <Skeleton height={20} />
              </div>
            </Card>
          </div>
        </div>
      </LoadingRegion>
    </div>
  );
}

export function AssetDetailPage() {
  const { t } = useTranslation();
  const { id } = useParams<{ id: string }>();
  const assetQuery = useAsset(id!);

  useEffect(() => {
    if (!assetQuery.data) document.title = `${t('asset.pageTitle')} - ${t('app.name')}`;
  }, [assetQuery.data, t]);

  if (assetQuery.isPending) return <AssetSkeleton />;
  if (assetQuery.error || !assetQuery.data)
    return (
      <div role="alert" className="dam-page dam-page--narrow">
        <EmptyState
          icon={<WarningCircle />}
          title={t('asset.loadFailed')}
          description={t('asset.loadFailedHint')}
          action={
            <Link className="dam-btn dam-btn--secondary" to="/library">
              {t('upload.goToLibrary')}
            </Link>
          }
        />
      </div>
    );

  // Keyed by id so moving between assets never carries one asset's unsaved edits into another.
  return <AssetDetail key={assetQuery.data.id} asset={assetQuery.data} />;
}
