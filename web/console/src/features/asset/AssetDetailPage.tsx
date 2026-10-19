import { useCallback, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useParams, useSearchParams } from 'react-router-dom';
import { CommentsProvider, useCommentsUi } from '../comments/CommentsContext';
import { useThreads } from '../comments/commentsApi';
import { ActivityTab } from '../comments/panel/ActivityTab';
import { CommentsTab } from '../comments/panel/CommentsTab';
import { RIGHTS_TITLE_ID } from '../rights/asset/RightsPanel';
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

const TAB_IDS = ['details', 'metadata', 'comments', 'activity', 'versions'] as const;
type TabId = (typeof TAB_IDS)[number];

function AssetDetailBody({ asset }: { asset: AssetDto }) {
  const { t } = useTranslation();
  const draft = useAssetDraft(asset);
  const ui = useCommentsUi();
  const [params, setParams] = useSearchParams();
  // The tab lives in the URL (`?tab=`), so a link can open the Comments tab; `?comment=` implies it.
  const requested = params.get('tab');
  const tab: TabId = TAB_IDS.includes(requested as TabId) ? (requested as TabId) : params.has('comment') ? 'comments' : 'details';
  const setTab = useCallback(
    (id: string) =>
      setParams(
        (prev) => {
          const next = new URLSearchParams(prev);
          if (id === 'details') next.delete('tab');
          else next.set('tab', id);
          return next;
        },
        { replace: true },
      ),
    [setParams],
  );
  // The preview asks for the composer after a region is drawn or a time captured: show the Comments tab and focus it.
  const [focusRequest, setFocusRequest] = useState(0);
  const { registerFocusComposer } = ui;
  useEffect(() => {
    registerFocusComposer(() => {
      setTab('comments');
      setFocusRequest((n) => n + 1);
    });
    return () => registerFocusComposer(null);
  }, [registerFocusComposer, setTab]);
  const handled = useCallback(() => setFocusRequest(0), []);
  // "Review rights" (the licence chip, a blocked Publish) opens the Details tab and brings the rights panel into view.
  const [reviewRequest, setReviewRequest] = useState(0);
  const reviewed = useRef(0);
  useEffect(() => {
    if (reviewRequest === reviewed.current || tab !== 'details') return;
    const heading = document.getElementById(RIGHTS_TITLE_ID);
    if (!heading) return;
    reviewed.current = reviewRequest;
    heading.scrollIntoView?.({ block: 'start' });
    heading.focus();
  }, [reviewRequest, tab]);
  const showRights = useCallback(() => {
    setTab('details');
    setReviewRequest((n) => n + 1);
  }, [setTab]);
  const open = useThreads(asset.id, { versionId: asset.currentVersionId ?? undefined }).data?.counts.open ?? 0;

  useEffect(() => {
    document.title = `${asset.title} - ${t('app.name')}`;
  }, [asset.title, t]);

  return (
    <div className="dam-page dam-asset">
      <AssetHeader asset={asset} onReviewRights={showRights} />
      <div className="dam-asset-layout">
        <div className="dam-asset-layout__stage">
          <AssetPreview assetId={asset.id} type={asset.type} title={asset.title} status={asset.status} />
        </div>
        <div className="dam-asset-layout__side">
          <Tabs
            label={t('asset.tabs')}
            value={tab}
            onChange={setTab}
            tabs={[
              { id: 'details', label: t('asset.detailsTab'), content: <DetailsPanel asset={asset} draft={draft} /> },
              {
                id: 'metadata',
                label: t('asset.metadataTab'),
                content: <MetadataForm assetType={asset.type} draft={draft} />,
              },
              {
                id: 'comments',
                label: open > 0 ? t('comments.tabWithCount', { count: open }) : t('comments.tab'),
                content: <CommentsTab assetId={asset.id} focusRequest={focusRequest} onFocusHandled={handled} />,
              },
              { id: 'activity', label: t('comments.activityTab'), content: <ActivityTab assetId={asset.id} /> },
              { id: 'versions', label: t('asset.versionsTab'), content: <VersionsPanel assetId={asset.id} /> },
            ]}
          />
        </div>
      </div>
      {draft.dirty && <SaveBar saving={draft.saving} onSave={draft.save} onDiscard={draft.discard} />}
    </div>
  );
}

function AssetDetail({ asset }: { asset: AssetDto }) {
  return (
    <CommentsProvider assetId={asset.id} assetType={asset.type} versionId={asset.currentVersionId}>
      <AssetDetailBody asset={asset} />
    </CommentsProvider>
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
