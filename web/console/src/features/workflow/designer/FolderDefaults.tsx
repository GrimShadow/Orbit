import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../../../api/client';
import { Button, Card, EmptyState, Select, Skeleton, useToast } from '../../../ui';
import { useFolders, type FolderDto } from '../../library/folders';
import type { WorkflowDto } from '../workflowApi';
import { useSetFolderDefault } from './designerApi';

/** Folders in path order (parents before children) with their depth. */
function ordered(folders: readonly FolderDto[]) {
  return [...folders]
    .sort((a, b) => a.path.localeCompare(b.path))
    .map((f) => ({ folder: f, depth: f.path.split('.').length - 1 }));
}

export function FolderDefaults({ workflows }: { workflows: WorkflowDto[] }) {
  const { t } = useTranslation();
  const { push } = useToast();
  const folders = useFolders();
  const setDefault = useSetFolderDefault();
  const [pending, setPending] = useState<{ id: string; value: string } | null>(null);

  const rows = useMemo(() => ordered(folders.data ?? []), [folders.data]);
  const byId = useMemo(() => new Map((folders.data ?? []).map((f) => [f.id, f])), [folders.data]);
  const nameOf = (id: string) => workflows.find((w) => w.id === id)?.name ?? '';

  /** The nearest ancestor that names a default, which is what an asset in this folder inherits. */
  const inherited = (f: FolderDto) => {
    for (let cur = f.parentId ? byId.get(f.parentId) : undefined; cur; cur = cur.parentId ? byId.get(cur.parentId) : undefined)
      if (cur.defaultWorkflowId) return { folder: cur, workflowId: cur.defaultWorkflowId };
    return null;
  };

  const change = (f: FolderDto, value: string) => {
    setPending({ id: f.id, value });
    setDefault.mutate(
      { folderId: f.id, workflowId: value || null },
      {
        onSuccess: () => push({ title: t('workflows.folders.saved'), tone: 'success' }),
        onError: (e) =>
          push({ title: t('workflows.folders.saveFailed'), message: e instanceof ApiError ? e.title : undefined, tone: 'error' }),
        onSettled: () => setPending(null),
      },
    );
  };

  const options = [{ value: '', label: t('workflows.folders.none') }, ...workflows.map((w) => ({ value: w.id, label: w.name }))];

  return (
    <Card as="section" aria-labelledby="wf-folders-title" className="dam-wfpanel">
      <div className="dam-wfpanel__head">
        <div>
          <h3 id="wf-folders-title" className="dam-wfpanel__title">
            {t('workflows.folders.title')}
          </h3>
          <p className="dam-hint">{t('workflows.folders.description')}</p>
        </div>
      </div>
      {folders.isPending ? (
        <div role="status">
          <span className="dam-sr-only">{t('workflows.loading')}</span>
          <Skeleton height={120} />
        </div>
      ) : folders.error ? (
        <div role="alert">
          <EmptyState
            titleAs="p"
            title={t('workflows.folders.loadFailed')}
            action={<Button onClick={() => void folders.refetch()}>{t('workflows.retry')}</Button>}
          />
        </div>
      ) : rows.length === 0 ? (
        <p className="dam-hint">{t('workflows.folders.empty')}</p>
      ) : (
        <ul className="dam-wffolders" aria-label={t('workflows.folders.title')}>
          {rows.map(({ folder, depth }) => {
            const inh = !folder.defaultWorkflowId ? inherited(folder) : null;
            return (
              <li key={folder.id} className="dam-wffolders__row">
                <span className="dam-wffolders__name" style={{ paddingInlineStart: `${depth * 20}px` }}>
                  <span>{folder.name}</span>
                  {inh && (
                    <span className="dam-hint">
                      {t('workflows.folders.inherits', { workflow: nameOf(inh.workflowId), folder: inh.folder.name })}
                    </span>
                  )}
                </span>
                <div className="dam-wffolders__select">
                  <Select
                    label={t('workflows.folders.defaultFor', { name: folder.name })}
                    options={options}
                    value={pending?.id === folder.id ? pending.value : (folder.defaultWorkflowId ?? '')}
                    disabled={pending?.id === folder.id}
                    onChange={(e) => change(folder, e.target.value)}
                  />
                </div>
              </li>
            );
          })}
        </ul>
      )}
    </Card>
  );
}
