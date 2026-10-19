import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../../../api/client';
import { Button, Card, EmptyState, IconButton, Input, Plus, Select, Skeleton, Trash, X, useToast } from '../../../ui';
import { useReplaceBindings, useWorkflowBindings, type WorkflowDto } from '../workflowApi';

/** The asset types the API accepts for a binding (`AssetTypes.All`). */
export const ASSET_TYPES = ['image', 'video', 'audio', 'document', 'manual', '3d', 'html5', 'other'] as const;

interface Row {
  key: string;
  assetType: string; // '' = any
  region: string;
  workflowId: string;
}

let nextKey = 0;
const newKey = () => `b${nextKey++}`;

const sameTarget = (a: Row, b: Row) =>
  a.assetType === b.assetType && a.region.trim().toLowerCase() === b.region.trim().toLowerCase();

/** Rows -> the replace-all payload. */
export function toPayload(rows: Row[]) {
  return rows.map((r) => ({ workflowId: r.workflowId, assetType: r.assetType || null, region: r.region.trim() || null }));
}

export function BindingsEditor({ workflows }: { workflows: WorkflowDto[] }) {
  const { t } = useTranslation();
  const { push } = useToast();
  const query = useWorkflowBindings();
  const replace = useReplaceBindings();
  const [local, setLocal] = useState<Row[] | null>(null);

  const server: Row[] = (query.data ?? []).map((b) => ({
    key: b.id,
    assetType: b.assetType ?? '',
    region: b.region ?? '',
    workflowId: b.workflowId,
  }));
  const rows = local ?? server;
  const dirty = local !== null && JSON.stringify(toPayload(local)) !== JSON.stringify(toPayload(server));

  const edit = (next: Row[]) => setLocal(next);
  const patch = (key: string, p: Partial<Row>) => edit(rows.map((r) => (r.key === key ? { ...r, ...p } : r)));

  const problem = (r: Row, i: number): string | undefined => {
    if (!r.assetType && !r.region.trim()) return t('workflows.bindings.needsTarget');
    if (rows.findIndex((o) => sameTarget(o, r)) !== i) return t('workflows.bindings.duplicate');
    return undefined;
  };
  const invalid = rows.some((r, i) => problem(r, i));

  const save = () =>
    replace.mutate(toPayload(rows), {
      onSuccess: () => {
        setLocal(null);
        push({ title: t('workflows.bindings.saved'), tone: 'success' });
      },
      onError: (e) =>
        push({ title: t('workflows.bindings.saveFailed'), message: e instanceof ApiError ? e.title : undefined, tone: 'error' }),
    });

  const typeOptions = [
    { value: '', label: t('workflows.bindings.anyType') },
    ...ASSET_TYPES.map((v) => ({ value: v, label: t(`workflows.assetType.${v}`) })),
  ];
  const workflowOptions = workflows.map((w) => ({ value: w.id, label: w.name }));

  return (
    <Card as="section" aria-labelledby="wf-bindings-title" className="dam-wfpanel">
      <div className="dam-wfpanel__head">
        <div>
          <h3 id="wf-bindings-title" className="dam-wfpanel__title">
            {t('workflows.bindings.title')}
          </h3>
          <p className="dam-hint">{t('workflows.bindings.description')}</p>
        </div>
        <Button
          size="sm"
          onClick={() => edit([...rows, { key: newKey(), assetType: 'image', region: '', workflowId: workflows[0]?.id ?? '' }])}
          disabled={workflows.length === 0}
        >
          <Plus size={14} weight="bold" aria-hidden="true" />
          {t('workflows.bindings.add')}
        </Button>
      </div>
      <p className="dam-wfpanel__note">{t('workflows.bindings.order')}</p>

      {query.isPending ? (
        <div className="dam-wfpanel__skel" role="status">
          <span className="dam-sr-only">{t('workflows.loading')}</span>
          <Skeleton height={64} />
        </div>
      ) : query.error ? (
        <div role="alert">
          <EmptyState
            titleAs="p"
            title={t('workflows.bindings.loadFailed')}
            action={<Button onClick={() => void query.refetch()}>{t('workflows.retry')}</Button>}
          />
        </div>
      ) : rows.length === 0 ? (
        <p className="dam-hint">{t('workflows.bindings.empty')}</p>
      ) : (
        <ul className="dam-wfbind" aria-label={t('workflows.bindings.title')}>
          {rows.map((r, i) => (
            <li key={r.key}>
              <div className="dam-wfbind__row" role="group" aria-label={t('workflows.bindings.row', { n: i + 1 })}>
                <Select
                  label={t('workflows.bindings.assetType')}
                  value={r.assetType}
                  options={typeOptions}
                  onChange={(e) => patch(r.key, { assetType: e.target.value })}
                />
                <Input
                  label={t('workflows.bindings.region')}
                  value={r.region}
                  placeholder={t('workflows.bindings.regionPlaceholder')}
                  maxLength={64}
                  onChange={(e) => patch(r.key, { region: e.target.value })}
                />
                <Select
                  label={t('workflows.bindings.workflow')}
                  value={r.workflowId}
                  options={workflowOptions}
                  onChange={(e) => patch(r.key, { workflowId: e.target.value })}
                  error={problem(r, i)}
                />
                <IconButton
                  label={t('workflows.bindings.remove', { n: i + 1 })}
                  className="dam-wfbind__remove"
                  onClick={() => edit(rows.filter((x) => x.key !== r.key))}
                >
                  <Trash size={16} aria-hidden="true" />
                </IconButton>
              </div>
            </li>
          ))}
        </ul>
      )}

      {dirty && (
        <div className="dam-wfpanel__foot">
          <span className="dam-wfpanel__unsaved">{t('workflows.bindings.unsaved')}</span>
          <Button variant="ghost" onClick={() => setLocal(null)} disabled={replace.isPending}>
            <X size={14} weight="bold" aria-hidden="true" />
            {t('workflows.bindings.discard')}
          </Button>
          <Button variant="primary" onClick={save} loading={replace.isPending} disabled={invalid}>
            {t('workflows.bindings.save')}
          </Button>
        </div>
      )}
    </Card>
  );
}
