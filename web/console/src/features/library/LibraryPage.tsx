import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate } from 'react-router-dom';
import {
  Breadcrumb,
  Button,
  LoadingRegion,
  Rows,
  Select,
  Skeleton,
  FolderPlus,
  SquaresFour,
  Table,
  ThumbnailGrid,
  Tree,
  type Column,
  type TreeNode,
} from '../../ui';
import { useThumbnailLinks } from '../asset/assetApi';
import { useItemActions } from './itemActions';
import { toAssetThumb } from './assetThumb';
import { useAssets, type AssetDto } from './assets';
import { BulkEditBar } from './BulkEditBar';
import { buildFolderTree, folderBreadcrumb, useFolders } from './folders';
import './library.css';

const STATUSES = ['draft', 'in_review', 'approved', 'published', 'expired', 'archived', 'deleted'];
const TYPES = ['image', 'video', 'audio', 'document', 'manual', '3d', 'html5', 'other'];

function FolderSidebar({
  selectedId,
  onSelect,
  actions,
}: {
  selectedId?: string;
  onSelect: (id: string | undefined) => void;
  actions: ReturnType<typeof useItemActions>;
}) {
  const { t } = useTranslation();
  const { data, error, isPending, refetch } = useFolders();

  if (isPending)
    return (
      <LoadingRegion>
        <Skeleton height={20} width={160} />
        <Skeleton height={20} width={140} />
        <Skeleton height={20} width={150} />
      </LoadingRegion>
    );
  if (error)
    return (
      <div role="alert" className="dam-stack">
        <p>{t('library.foldersFailed')}</p>
        <Button onClick={() => void refetch()}>{t('library.retry')}</Button>
      </div>
    );

  const tree: TreeNode[] = buildFolderTree(data);
  return (
    <div className="dam-stack">
      <button type="button" className="dam-btn dam-btn--ghost" aria-pressed={!selectedId} onClick={() => onSelect(undefined)}>
        {t('library.allFolders')}
      </button>
      <Tree
        nodes={tree}
        label={t('library.folders')}
        selectedId={selectedId}
        onSelect={onSelect}
        nodeActions={actions.folderMenu}
        menuLabel={(name) => t('item.actionsForFolder', { name })}
        onMenuOpen={actions.prefetchFolder}
      />
      <button
        type="button"
        className="dam-btn dam-btn--secondary dam-btn--sm"
        onClick={() => actions.newFolder(selectedId ?? null)}
      >
        <FolderPlus size={16} weight="bold" aria-hidden="true" />
        {t('item.newFolder')}
      </button>
    </div>
  );
}

export function LibraryPage() {
  const { t, i18n } = useTranslation();
  const navigate = useNavigate();
  const foldersQuery = useFolders();
  const [folderId, setFolderId] = useState<string | undefined>();
  const [status, setStatus] = useState('');
  const [type, setType] = useState('');
  const [view, setView] = useState<'grid' | 'list'>('grid');
  const [cursor, setCursor] = useState<string | undefined>();
  const [selected, setSelected] = useState<Set<string>>(new Set());

  const setFilter = (apply: () => void) => {
    setCursor(undefined);
    setSelected(new Set());
    apply();
  };

  const toggleSelect = (id: string, isSelected: boolean) =>
    setSelected((s) => {
      const next = new Set(s);
      if (isSelected) next.add(id);
      else next.delete(id);
      return next;
    });

  const assetsQuery = useAssets({
    folderId,
    includeSubfolders: true,
    status: status || undefined,
    type: type || undefined,
    cursor,
    limit: 50,
  });
  // Only the grid view shows images; skip the batch link request entirely while viewing the list/table.
  const itemActions = useItemActions({
    onOpenFolder: (id) => setFilter(() => setFolderId(id)),
    // Deleting the folder you are looking at would leave an empty view of nothing - go back to everything.
    onFolderDeleted: (id) => {
      if (id === folderId) setFilter(() => setFolderId(undefined));
    },
  });
  const assetById = (id: string) => assetsQuery.data?.items.find((a) => a.id === id);
  const thumbnailLinks = useThumbnailLinks(view === 'grid' ? (assetsQuery.data?.items.map((a) => a.id) ?? []) : []);

  const columns: Column<AssetDto>[] = [
    { key: 'title', header: t('library.columnTitle'), render: (a) => <Link to={`/library/assets/${a.id}`}>{a.title}</Link> },
    { key: 'type', header: t('library.columnType'), render: (a) => t(`library.type.${a.type}`) },
    { key: 'status', header: t('library.columnStatus'), render: (a) => t(`library.status.${a.status}`) },
    { key: 'updatedAt', header: t('library.columnUpdated'), render: (a) => new Date(a.updatedAt).toLocaleString() },
  ];

  return (
    <div className="dam-page dam-library">
      <h1>{t('library.title')}</h1>
      <div className="dam-library__layout">
        <aside className="dam-library__sidebar" aria-label={t('library.folders')}>
          <FolderSidebar selectedId={folderId} onSelect={(id) => setFilter(() => setFolderId(id))} actions={itemActions} />
        </aside>
        <div className="dam-library__main">
          {foldersQuery.data && (
            <Breadcrumb
              label={t('library.folders')}
              rootLabel={t('library.allFolders')}
              items={folderBreadcrumb(foldersQuery.data, folderId)}
              onNavigate={(id) => setFilter(() => setFolderId(id))}
            />
          )}
          <div className="dam-library__toolbar">
            <Select
              label={t('library.status')}
              value={status}
              onChange={(e) => setFilter(() => setStatus(e.target.value))}
              options={[
                { value: '', label: t('library.any') },
                ...STATUSES.map((s) => ({ value: s, label: t(`library.status.${s}`) })),
              ]}
            />
            <Select
              label={t('library.type')}
              value={type}
              onChange={(e) => setFilter(() => setType(e.target.value))}
              options={[
                { value: '', label: t('library.any') },
                ...TYPES.map((x) => ({ value: x, label: t(`library.type.${x}`) })),
              ]}
            />
            <div className="dam-library__viewToggle" role="group" aria-label={t('library.view')}>
              <button
                type="button"
                aria-pressed={view === 'grid'}
                className="dam-btn dam-btn--ghost"
                onClick={() => setView('grid')}
              >
                <SquaresFour size={16} weight="bold" aria-hidden="true" />
                {t('library.viewGrid')}
              </button>
              <button
                type="button"
                aria-pressed={view === 'list'}
                className="dam-btn dam-btn--ghost"
                onClick={() => setView('list')}
              >
                <Rows size={16} weight="bold" aria-hidden="true" />
                {t('library.viewList')}
              </button>
            </div>
          </div>

          {assetsQuery.isPending ? (
            <LoadingRegion>
              <Skeleton height={160} />
            </LoadingRegion>
          ) : assetsQuery.error ? (
            <div role="alert" className="dam-stack">
              <p>{t('library.assetsFailed')}</p>
              <Button onClick={() => void assetsQuery.refetch()}>{t('library.retry')}</Button>
            </div>
          ) : (
            <>
              {assetsQuery.data.items.length > 0 && (
                <label className="dam-checkbox-label">
                  <input
                    type="checkbox"
                    checked={assetsQuery.data.items.every((a) => selected.has(a.id))}
                    onChange={(e) => setSelected(e.target.checked ? new Set(assetsQuery.data.items.map((a) => a.id)) : new Set())}
                  />
                  {t('library.selectAllOnPage', { count: assetsQuery.data.items.length })}
                </label>
              )}

              {selected.size > 0 && (
                <BulkEditBar assets={assetsQuery.data.items} selectedIds={[...selected]} onClear={() => setSelected(new Set())} />
              )}

              {view === 'grid' ? (
                <ThumbnailGrid
                  items={assetsQuery.data.items.map((a) => toAssetThumb(t, i18n.language, a, thumbnailLinks.data?.[a.id]))}
                  label={t('library.assetsLabel')}
                  onOpen={(id) => navigate(`/library/assets/${id}`)}
                  selectedIds={[...selected]}
                  onToggleSelect={toggleSelect}
                  selectLabel={(title) => t('library.selectAsset', { title })}
                  cardActions={(id) => {
                    const asset = assetById(id);
                    return asset ? itemActions.assetMenu(asset) : [];
                  }}
                  onMenuOpen={(id) => {
                    const asset = assetById(id);
                    if (asset) itemActions.prefetchAsset(asset);
                  }}
                  menuLabel={(title) => t('library.card.actionsFor', { title })}
                />
              ) : (
                <Table
                  columns={columns}
                  rows={assetsQuery.data.items}
                  getRowId={(a) => a.id}
                  caption={t('library.assetsLabel')}
                  emptyMessage={t('library.empty')}
                  selectedIds={[...selected]}
                  onToggleSelect={toggleSelect}
                  selectLabel={(a) => t('library.selectAsset', { title: a.title })}
                />
              )}
              {assetsQuery.data.items.length === 0 && view === 'grid' && <p className="dam-empty">{t('library.empty')}</p>}
              {assetsQuery.data.nextCursor && (
                <Button
                  variant="secondary"
                  onClick={() => {
                    setSelected(new Set());
                    setCursor(assetsQuery.data.nextCursor!);
                  }}
                  loading={assetsQuery.isFetching}
                >
                  {t('library.next')}
                </Button>
              )}
            </>
          )}
        </div>
      </div>
      {itemActions.dialogs}
    </div>
  );
}
