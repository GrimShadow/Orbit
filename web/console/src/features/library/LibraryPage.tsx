import { useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate } from 'react-router-dom';
import {
  Breadcrumb,
  Button,
  CaretDown,
  EmptyState,
  FolderPlus,
  FolderSimple,
  Images,
  MagnifyingGlass,
  PageHeader,
  Rows,
  SegmentedControl,
  Select,
  SquaresFour,
  ThumbnailGrid,
  ThumbnailGridSkeleton,
  UploadSimple,
} from '../../ui';
import { BulkEditBar } from './BulkEditBar';
import { FilterChips, type FilterChip } from './FilterChips';
import { FolderPanel } from './FolderPanel';
import { LibraryList } from './LibraryList';
import { toAssetThumb } from './assetThumb';
import { useAssetsInfinite, useThumbnailMap, type AssetDto } from './assets';
import { folderBreadcrumb, useFolders } from './folders';
import { useItemActions } from './itemActions';
import { STATUSES, TYPES, useLibraryParams } from './useLibraryParams';
import { useDocumentTitle } from './useDocumentTitle';
import './library.css';

const PAGE_SIZE = 50;

/** A checkbox row that can show the "some selected" state - the library's "select all" control. */
function SelectAll({
  checked,
  indeterminate,
  label,
  onChange,
}: {
  checked: boolean;
  indeterminate: boolean;
  label: string;
  onChange: (on: boolean) => void;
}) {
  const ref = useRef<HTMLInputElement>(null);
  useEffect(() => {
    if (ref.current) ref.current.indeterminate = indeterminate;
  }, [indeterminate]);
  return (
    <label className="dam-check dam-library__selectall">
      <input
        ref={ref}
        type="checkbox"
        className="dam-check__input"
        checked={checked}
        onChange={(e) => onChange(e.target.checked)}
      />
      <span className="dam-check__label">{label}</span>
    </label>
  );
}

export function LibraryPage() {
  const { t, i18n } = useTranslation();
  const navigate = useNavigate();
  useDocumentTitle(t('library.title'));
  const foldersQuery = useFolders();
  const { folder: folderId, status, type, view, update } = useLibraryParams();
  const [foldersOpen, setFoldersOpen] = useState(false);

  // Selection belongs to one view of the library: any change of folder/status/type starts from nothing selected.
  const filterKey = `${folderId ?? ''}|${status}|${type}`;
  const [selection, setSelection] = useState<{ key: string; ids: string[] }>({ key: filterKey, ids: [] });
  const selectedIds = selection.key === filterKey ? selection.ids : [];
  const setSelectedIds = (ids: string[]) => setSelection({ key: filterKey, ids });
  const toggleSelect = (id: string, on: boolean) =>
    setSelectedIds(on ? [...new Set([...selectedIds, id])] : selectedIds.filter((x) => x !== id));

  const assetsQuery = useAssetsInfinite({
    folderId,
    includeSubfolders: true,
    status: status || undefined,
    type: type || undefined,
    limit: PAGE_SIZE,
  });
  const assets: AssetDto[] = useMemo(() => assetsQuery.data?.pages.flatMap((p) => p.items) ?? [], [assetsQuery.data]);
  const hasMore = assetsQuery.hasNextPage;
  // The "All folders" row's count is the unfiltered library; it is the same query (and cache entry) as the root view.
  const rootQuery = useAssetsInfinite({
    folderId: undefined,
    includeSubfolders: true,
    status: undefined,
    type: undefined,
    limit: PAGE_SIZE,
  });
  const rootCount = rootQuery.data
    ? `${rootQuery.data.pages.reduce((n, p) => n + p.items.length, 0)}${rootQuery.hasNextPage ? '+' : ''}`
    : undefined;
  const thumbnails = useThumbnailMap(assets.map((a) => a.id));

  const itemActions = useItemActions({
    onOpenFolder: (id) => update({ folder: id }),
    // Deleting the folder you are looking at would leave an empty view of nothing - go back to everything.
    onFolderDeleted: (id) => {
      if (id === folderId) update({ folder: undefined });
    },
  });
  const assetById = (id: string) => assets.find((a) => a.id === id);

  const crumbs = foldersQuery.data ? folderBreadcrumb(foldersQuery.data, folderId) : [];
  const currentFolderName = crumbs.at(-1)?.label;
  const hasFilters = status !== '' || type !== '';
  const chips: FilterChip[] = [
    ...(status
      ? [
          {
            id: 'status',
            group: t('library.status'),
            label: t(`library.status.${status}`),
            onRemove: () => update({ status: '' }),
          },
        ]
      : []),
    ...(type
      ? [{ id: 'type', group: t('library.type'), label: t(`library.type.${type}`), onRemove: () => update({ type: '' }) }]
      : []),
  ];
  const clearFilters = () => update({ status: '', type: '' });

  const loaded = assets.length;
  const description = assetsQuery.isPending
    ? undefined
    : hasMore
      ? t('library.countMore', { count: loaded })
      : t('library.count', { count: loaded });
  const showSelecting = selectedIds.length > 0;
  const busy = assetsQuery.isPlaceholderData;

  const empty =
    assetsQuery.isSuccess && loaded === 0 ? (
      hasFilters ? (
        <EmptyState
          icon={<MagnifyingGlass size={28} />}
          title={t('library.noMatches.title')}
          description={t('library.noMatches.description')}
          action={
            <Button variant="secondary" onClick={clearFilters}>
              {t('library.clearFilters')}
            </Button>
          }
          titleAs="h2"
        />
      ) : (
        <EmptyState
          icon={<Images size={28} />}
          title={folderId ? t('library.emptyFolder.title') : t('library.emptyLibrary.title')}
          description={folderId ? t('library.emptyFolder.description') : t('library.emptyLibrary.description')}
          action={
            <Link to={folderId ? `/upload?folder=${folderId}` : '/upload'} className="dam-btn dam-btn--primary">
              <UploadSimple size={16} weight="bold" aria-hidden="true" />
              {t('library.upload')}
            </Link>
          }
          titleAs="h2"
        />
      )
    ) : null;

  return (
    <div className={`dam-page dam-library${showSelecting ? ' dam-library--selecting' : ''}`}>
      <PageHeader
        title={t('library.title')}
        description={description}
        actions={
          <>
            <Button variant="secondary" onClick={() => itemActions.newFolder(folderId ?? null)}>
              <FolderPlus size={16} weight="bold" aria-hidden="true" />
              {t('item.newFolder')}
            </Button>
            <Link to={folderId ? `/upload?folder=${folderId}` : '/upload'} className="dam-btn dam-btn--primary">
              <UploadSimple size={16} weight="bold" aria-hidden="true" />
              {t('library.upload')}
            </Link>
          </>
        }
      />

      <button
        type="button"
        className="dam-library__foldertoggle"
        aria-expanded={foldersOpen}
        aria-controls="library-folders"
        onClick={() => setFoldersOpen((o) => !o)}
      >
        <FolderSimple size={16} aria-hidden="true" />
        <span className="dam-library__foldertoggletext">
          {t('library.folders')}
          <span className="dam-library__foldertogglecurrent">{currentFolderName ?? t('library.allFolders')}</span>
        </span>
        <CaretDown size={14} weight="bold" aria-hidden="true" className="dam-library__foldertogglecaret" />
      </button>

      <div className="dam-library__layout">
        <aside
          id="library-folders"
          className={`dam-library__sidebar${foldersOpen ? ' dam-library__sidebar--open' : ''}`}
          aria-label={t('library.folders')}
        >
          <h2 className="dam-library__sidehead">{t('library.folders')}</h2>
          <FolderPanel
            selectedId={folderId}
            onSelect={(id) => {
              update({ folder: id });
              setFoldersOpen(false);
            }}
            actions={itemActions}
            total={rootCount}
          />
        </aside>

        <div className="dam-library__main">
          {foldersQuery.data && crumbs.length > 0 && (
            <Breadcrumb
              label={t('library.folders')}
              rootLabel={t('library.allFolders')}
              items={crumbs}
              onNavigate={(id) => update({ folder: id })}
            />
          )}

          <div className="dam-library__toolbar">
            <div className="dam-library__filters">
              <div className="dam-library__filter">
                <Select
                  label={t('library.status')}
                  value={status}
                  onChange={(e) => update({ status: e.target.value })}
                  options={[
                    { value: '', label: t('library.anyStatus') },
                    ...STATUSES.map((s) => ({ value: s, label: t(`library.status.${s}`) })),
                  ]}
                />
              </div>
              <div className="dam-library__filter">
                <Select
                  label={t('library.type')}
                  value={type}
                  onChange={(e) => update({ type: e.target.value })}
                  options={[
                    { value: '', label: t('library.anyType') },
                    ...TYPES.map((x) => ({ value: x, label: t(`library.type.${x}`) })),
                  ]}
                />
              </div>
            </div>
            <SegmentedControl
              label={t('library.view')}
              value={view}
              onChange={(v) => update({ view: v as 'grid' | 'list' }, { replace: true })}
              options={[
                { value: 'grid', label: t('library.viewGrid'), icon: <SquaresFour size={16} weight="bold" aria-hidden="true" /> },
                { value: 'list', label: t('library.viewList'), icon: <Rows size={16} weight="bold" aria-hidden="true" /> },
              ]}
            />
          </div>

          <FilterChips chips={chips} onClear={clearFilters} />

          {assetsQuery.isPending ? (
            view === 'grid' ? (
              <ThumbnailGridSkeleton count={10} label={t('library.loading')} />
            ) : (
              <LibraryList
                assets={[]}
                thumbnails={{}}
                loading
                selectedIds={[]}
                onToggleSelect={() => {}}
                actions={itemActions}
                emptyMessage=""
              />
            )
          ) : assetsQuery.error ? (
            <div role="alert" className="dam-library__error">
              <EmptyState
                title={t('library.assetsFailed')}
                description={t('library.assetsFailedHint')}
                action={<Button onClick={() => void assetsQuery.refetch()}>{t('library.retry')}</Button>}
                titleAs="h2"
              />
            </div>
          ) : (
            <div className={`dam-library__results${busy ? ' dam-library__results--busy' : ''}`} aria-busy={busy || undefined}>
              {loaded > 0 && (
                <div className="dam-library__resultsbar">
                  <SelectAll
                    checked={selectedIds.length > 0 && assets.every((a) => selectedIds.includes(a.id))}
                    indeterminate={selectedIds.length > 0 && !assets.every((a) => selectedIds.includes(a.id))}
                    label={t('library.selectAllOnPage', { count: loaded })}
                    onChange={(on) => setSelectedIds(on ? assets.map((a) => a.id) : [])}
                  />
                </div>
              )}

              {empty}

              {loaded > 0 &&
                (view === 'grid' ? (
                  <ThumbnailGrid
                    items={assets.map((a) => toAssetThumb(t, i18n.language, a, thumbnails[a.id]))}
                    label={t('library.assetsLabel')}
                    onOpen={(id) => navigate(`/library/assets/${id}`)}
                    selectedIds={selectedIds}
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
                  <LibraryList
                    assets={assets}
                    thumbnails={thumbnails}
                    selectedIds={selectedIds}
                    onToggleSelect={toggleSelect}
                    actions={itemActions}
                    emptyMessage={t('library.noMatches.title')}
                  />
                ))}

              {loaded > 0 && (
                <div className="dam-library__more">
                  <span className="dam-hint" role="status">
                    {hasMore ? t('library.showingMore', { count: loaded }) : t('library.showingAll', { count: loaded })}
                  </span>
                  {hasMore && (
                    <Button
                      variant="secondary"
                      onClick={() => void assetsQuery.fetchNextPage()}
                      loading={assetsQuery.isFetchingNextPage}
                    >
                      {t('library.loadMore')}
                    </Button>
                  )}
                </div>
              )}
            </div>
          )}
        </div>
      </div>

      {showSelecting && <BulkEditBar assets={assets} selectedIds={selectedIds} onClear={() => setSelectedIds([])} />}
      {itemActions.dialogs}
    </div>
  );
}
