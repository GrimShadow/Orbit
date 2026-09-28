import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useParams } from 'react-router-dom';
import { Badge, Button, Input, LoadingRegion, Skeleton, Tabs } from '../../ui';
import { AssetPreview } from './AssetPreview';
import { useAsset, useUpdateAsset } from './assetApi';
import { LifecycleActions } from './LifecycleActions';
import { MetadataForm } from './MetadataForm';
import { VersionsPanel } from './VersionsPanel';
import './asset.css';

function DetailsForm({ assetId, title, tags }: { assetId: string; title: string; tags: string[] }) {
  const { t } = useTranslation();
  const update = useUpdateAsset(assetId);
  const [titleValue, setTitleValue] = useState(title);
  const [tagsValue, setTagsValue] = useState(tags.join(', '));

  return (
    <form
      className="dam-upload__form"
      onSubmit={(e) => {
        e.preventDefault();
        update.mutate({
          title: titleValue,
          tags: tagsValue
            .split(',')
            .map((s) => s.trim())
            .filter(Boolean),
          metadata: null,
        });
      }}
    >
      <Input label={t('asset.titleLabel')} value={titleValue} onChange={(e) => setTitleValue(e.target.value)} />
      <Input label={t('upload.tags')} value={tagsValue} onChange={(e) => setTagsValue(e.target.value)} />
      <Button type="submit" variant="secondary" loading={update.isPending}>
        {t('asset.saveDetails')}
      </Button>
    </form>
  );
}

export function AssetDetailPage() {
  const { t } = useTranslation();
  const { id } = useParams<{ id: string }>();
  const assetQuery = useAsset(id!);

  if (assetQuery.isPending)
    return (
      <LoadingRegion>
        <Skeleton height={320} />
      </LoadingRegion>
    );
  if (assetQuery.error || !assetQuery.data)
    return (
      <div role="alert" className="dam-page dam-stack">
        <p>{t('asset.loadFailed')}</p>
        <Link to="/library">{t('upload.goToLibrary')}</Link>
      </div>
    );

  const asset = assetQuery.data;
  return (
    <div className="dam-page dam-stack">
      <Link to="/library">{t('asset.backToLibrary')}</Link>
      <div className="dam-asset-header">
        <h1>{asset.title}</h1>
        <Badge>{t(`library.status.${asset.status}`)}</Badge>
        <Badge>{t(`library.type.${asset.type}`)}</Badge>
      </div>

      <LifecycleActions assetId={asset.id} status={asset.status} />

      <div className="dam-asset-preview">
        <AssetPreview assetId={asset.id} type={asset.type} title={asset.title} status={asset.status} />
      </div>

      <Tabs
        label={t('asset.tabs')}
        tabs={[
          {
            id: 'metadata',
            label: t('asset.metadataTab'),
            content: (
              <div className="dam-stack">
                <DetailsForm assetId={asset.id} title={asset.title} tags={asset.tags} />
                <MetadataForm assetId={asset.id} assetType={asset.type} metadata={asset.metadata as Record<string, unknown>} />
              </div>
            ),
          },
          { id: 'versions', label: t('asset.versionsTab'), content: <VersionsPanel assetId={asset.id} /> },
        ]}
      />
    </div>
  );
}
