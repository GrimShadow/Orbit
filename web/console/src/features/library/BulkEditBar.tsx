import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { BookmarkSimple, Button, FolderSimple, Select, Tag, TagInput, X, useToast } from '../../ui';
import { ActionIcon } from '../asset/actionIcon';
import { availableActions } from '../asset/lifecycle';
import { useCreateStaticCollection } from '../collections/collectionsApi';
import { NameCollectionDialog } from '../collections/NameCollectionDialog';
import { useBulkEditTags, useBulkMove, useBulkTransition } from './bulkEditApi';
import type { BulkResult } from './bulkOrchestrator';
import type { AssetDto } from './assets';
import { folderOptions, useFolders } from './folders';

const UNSET_FOLDER = '__unset';

/** The set of lifecycle actions valid for EVERY selected asset right now - the intersection, not the union, of each
 * one's own `availableActions` (`../asset/lifecycle`, spec A4.2's state machine). Offering an action that is only
 * valid for some of the selection would just mean some of the fan-out calls in `useBulkTransition` fail; narrowing
 * up front here is a UX nicety, not the actual guard - the server re-validates every one independently regardless. */
function commonActions(assets: AssetDto[]): string[] {
  if (assets.length === 0) return [];
  const [first, ...rest] = assets;
  return rest.reduce(
    (acc, a) => acc.filter((action) => availableActions(a.status).includes(action)),
    availableActions(first!.status),
  );
}

interface BulkEditBarProps {
  /** The assets currently on screen (selection never outlives this page - the caller clears it on any filter/page change). */
  assets: AssetDto[];
  selectedIds: string[];
  onClear: () => void;
}

export function BulkEditBar({ assets, selectedIds, onClear }: BulkEditBarProps) {
  const { t } = useTranslation();
  const { push } = useToast();
  const foldersQuery = useFolders();
  const editTags = useBulkEditTags();
  const move = useBulkMove();
  const transition = useBulkTransition();
  const createCollection = useCreateStaticCollection();
  const [tags, setTags] = useState<string[]>([]);
  const [folderId, setFolderId] = useState(UNSET_FOLDER);
  const [panel, setPanel] = useState<'tags' | 'move' | null>(null);
  const [saveOpen, setSaveOpen] = useState(false);

  const selected = assets.filter((a) => selectedIds.includes(a.id));
  const actions = commonActions(selected);
  const busy = editTags.isPending || move.isPending || transition.isPending || createCollection.isPending;

  const report = (result: BulkResult) => {
    if (result.failed.length === 0) {
      push({ title: t('library.bulk.allSucceeded', { count: result.succeeded.length }), tone: 'success' });
    } else {
      push({
        title: t('library.bulk.someFailed', { succeeded: result.succeeded.length, failed: result.failed.length }),
        message: result.failed[0]?.error,
        tone: 'error',
      });
    }
    setTags([]);
    setFolderId(UNSET_FOLDER);
    setPanel(null);
    onClear();
  };

  const togglePanel = (next: 'tags' | 'move') => setPanel((p) => (p === next ? null : next));

  return (
    <>
      <div className="dam-bulkdock">
        <div className="dam-bulkbar" role="region" aria-label={t('library.bulk.selectedCount', { count: selectedIds.length })}>
          {panel === 'tags' && (
            <div className="dam-bulkbar__panel">
              <div className="dam-bulkbar__panelfield">
                <TagInput
                  label={t('library.bulk.tagsLabel')}
                  value={tags}
                  onChange={setTags}
                  placeholder={t('library.bulk.tagsPlaceholder')}
                  removeLabel={(tag) => t('library.bulk.removeTagChip', { tag })}
                  listLabel={t('library.bulk.tagsChosen')}
                />
              </div>
              <Button
                variant="primary"
                size="sm"
                disabled={tags.length === 0 || busy}
                loading={editTags.isPending}
                onClick={() => editTags.mutate({ assets: selected, tags, remove: false }, { onSuccess: report })}
              >
                {t('library.bulk.addTags')}
              </Button>
              <Button
                variant="secondary"
                size="sm"
                disabled={tags.length === 0 || busy}
                loading={editTags.isPending}
                onClick={() => editTags.mutate({ assets: selected, tags, remove: true }, { onSuccess: report })}
              >
                {t('library.bulk.removeTags')}
              </Button>
            </div>
          )}

          {panel === 'move' && foldersQuery.data && (
            <div className="dam-bulkbar__panel">
              <div className="dam-bulkbar__panelfield">
                <Select
                  label={t('library.bulk.moveLabel')}
                  value={folderId}
                  onChange={(e) => setFolderId(e.target.value)}
                  options={[
                    { value: UNSET_FOLDER, label: t('library.bulk.chooseFolder') },
                    { value: '', label: t('upload.noFolder') },
                    ...folderOptions(foldersQuery.data),
                  ]}
                />
              </div>
              <Button
                variant="primary"
                size="sm"
                disabled={folderId === UNSET_FOLDER || busy}
                loading={move.isPending}
                onClick={() => move.mutate({ ids: selectedIds, folderId: folderId || null }, { onSuccess: report })}
              >
                {t('library.bulk.move')}
              </Button>
            </div>
          )}

          <div className="dam-bulkbar__row">
            <span className="dam-bulkbar__count">{t('library.bulk.selectedCount', { count: selectedIds.length })}</span>
            <Button variant="ghost" size="sm" onClick={onClear}>
              <X size={14} weight="bold" aria-hidden="true" />
              {t('library.bulk.clear')}
            </Button>
            <span className="dam-bulkbar__sep" aria-hidden="true" />

            <div className="dam-bulkbar__actions">
              <Button
                variant="secondary"
                size="sm"
                aria-expanded={panel === 'tags'}
                disabled={busy}
                onClick={() => togglePanel('tags')}
              >
                <Tag size={16} weight="bold" aria-hidden="true" />
                {t('library.bulk.tags')}
              </Button>
              {foldersQuery.data && (
                <Button
                  variant="secondary"
                  size="sm"
                  aria-expanded={panel === 'move'}
                  disabled={busy}
                  onClick={() => togglePanel('move')}
                >
                  <FolderSimple size={16} weight="bold" aria-hidden="true" />
                  {t('library.bulk.moveTo')}
                </Button>
              )}
            </div>

            {actions.length > 0 && (
              <div className="dam-bulkbar__actions" role="group" aria-label={t('asset.lifecycleActions')}>
                {actions.map((action) => (
                  <Button
                    key={action}
                    variant={action === 'reject' ? 'danger' : 'secondary'}
                    size="sm"
                    loading={transition.isPending}
                    onClick={() => transition.mutate({ ids: selectedIds, action }, { onSuccess: report })}
                  >
                    <ActionIcon action={action} size={16} weight="bold" aria-hidden="true" />
                    {t(`asset.action.${action}`)}
                  </Button>
                ))}
              </div>
            )}

            <Button variant="secondary" size="sm" disabled={busy} onClick={() => setSaveOpen(true)}>
              <BookmarkSimple size={16} weight="bold" aria-hidden="true" />
              {t('library.bulk.saveAsCollection')}
            </Button>
          </div>
        </div>
      </div>
      <NameCollectionDialog
        open={saveOpen}
        title={t('library.bulk.saveAsCollectionTitle')}
        busy={createCollection.isPending}
        onClose={() => setSaveOpen(false)}
        onSave={(name) =>
          createCollection.mutate(
            { name, assetIds: selectedIds },
            {
              onSuccess: () => {
                setSaveOpen(false);
                push({ title: t('library.bulk.saveAsCollectionSucceeded', { name }), tone: 'success' });
                onClear();
              },
            },
          )
        }
      />
    </>
  );
}
