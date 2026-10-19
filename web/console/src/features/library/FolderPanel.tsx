import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Images, LoadingRegion, Skeleton, Tree } from '../../ui';
import { buildFolderTree, useFolders } from './folders';
import type { useItemActions } from './itemActions';

/** The ids of every ancestor of `folderId`, so the tree opens already revealing a folder restored from the URL. */
function ancestorIds(folders: { id: string; parentId: string | null }[], folderId: string | undefined): string[] {
  const byId = new Map(folders.map((f) => [f.id, f]));
  const out: string[] = [];
  for (let cur = folderId ? byId.get(folderId) : undefined; cur?.parentId; cur = byId.get(cur.parentId)) out.push(cur.parentId);
  return out;
}

/** The folder navigator: an "All folders" first row (with the total, when known) above the folder tree. Right-click and
 * the folder menu come from `useItemActions`; creating a folder lives on the page header's "New folder" button. */
export function FolderPanel({
  selectedId,
  onSelect,
  actions,
  total,
}: {
  selectedId?: string;
  onSelect: (id: string | undefined) => void;
  actions: ReturnType<typeof useItemActions>;
  total?: string | number;
}) {
  const { t } = useTranslation();
  const { data, error, isPending, refetch } = useFolders();
  const tree = useMemo(() => (data ? buildFolderTree(data) : []), [data]);

  if (isPending)
    return (
      <LoadingRegion label={t('library.loadingFolders')}>
        <div className="dam-foldernav__skeleton">
          <Skeleton height={32} />
          <Skeleton height={28} width="80%" />
          <Skeleton height={28} width="70%" />
          <Skeleton height={28} width="75%" />
        </div>
      </LoadingRegion>
    );
  if (error)
    return (
      <div role="alert" className="dam-stack">
        <p>{t('library.foldersFailed')}</p>
        <Button onClick={() => void refetch()}>{t('library.retry')}</Button>
      </div>
    );

  return (
    <div className="dam-foldernav">
      <button type="button" className="dam-foldernav__all" aria-pressed={!selectedId} onClick={() => onSelect(undefined)}>
        <Images size={16} aria-hidden="true" />
        <span className="dam-foldernav__alllabel">{t('library.allFolders')}</span>
        {total !== undefined && <span className="dam-foldernav__count">{total}</span>}
      </button>
      {tree.length === 0 ? (
        <p className="dam-hint dam-foldernav__none">{t('library.noFolders')}</p>
      ) : (
        <Tree
          nodes={tree}
          label={t('library.folders')}
          selectedId={selectedId}
          onSelect={onSelect}
          defaultExpanded={ancestorIds(data, selectedId)}
          nodeActions={actions.folderMenu}
          menuLabel={(name) => t('item.actionsForFolder', { name })}
          onMenuOpen={actions.prefetchFolder}
        />
      )}
    </div>
  );
}
