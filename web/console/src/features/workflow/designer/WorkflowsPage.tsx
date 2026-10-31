import { Copy, FlowArrow, Lightning, PencilSimple, Rocket } from '@phosphor-icons/react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { ApiError } from '../../../api/client';
import { Badge, Button, Card, EmptyState, Modal, PageHeader, Plus, Skeleton, Trash, WarningCircle, useToast } from '../../../ui';
import { useDocumentTitle } from '../../library/useDocumentTitle';
import { asDefinition, useDeleteWorkflow, useSaveWorkflow, useWorkflows, type WorkflowDto } from '../workflowApi';
import { normalizeDefinition } from '../definition';
import { BindingsEditor } from './BindingsEditor';
import { FolderDefaults } from './FolderDefaults';
import { conflictMessage, isForbidden } from './designerApi';
import { FlowSummary } from './flow';
import './designer.css';
import { useSettingsEyebrow } from '../../settings/useSettingsEyebrow';

/** "Name copy", then "Name copy 2" ... - the first name the tenant is not already using (names are unique ignoring case). */
export function copyName(name: string, taken: readonly string[], copy: string): string {
  const used = new Set(taken.map((n) => n.toLowerCase()));
  let candidate = `${name} ${copy}`;
  for (let n = 2; used.has(candidate.toLowerCase()); n++) candidate = `${name} ${copy} ${n}`;
  return candidate;
}

function Stat({ label, value }: { label: string; value: number }) {
  return (
    <div className="dam-wfstat">
      <dt>{label}</dt>
      <dd>{value}</dd>
    </div>
  );
}

function WorkflowCard({
  workflow,
  onDuplicate,
  onDelete,
  busy,
}: {
  workflow: WorkflowDto;
  onDuplicate: () => void;
  onDelete: () => void;
  busy: boolean;
}) {
  const { t } = useTranslation();
  const def = normalizeDefinition(asDefinition(workflow));
  return (
    <li>
      <Card as="article" className="dam-wfcardlist" aria-labelledby={`wf-${workflow.id}`}>
        <div className="dam-wfcardlist__head">
          <div className="dam-wfcardlist__text">
            <h2 id={`wf-${workflow.id}`} className="dam-wfcardlist__name">
              <Link to={`/settings/workflows/${workflow.id}`}>{workflow.name}</Link>
            </h2>
            <p className={workflow.description ? 'dam-wfcardlist__desc' : 'dam-wfcardlist__desc dam-hint'}>
              {workflow.description || t('workflows.noDescription')}
            </p>
          </div>
          {def.autoPublish && (
            <Badge tone="brand" size="sm">
              <Rocket size={12} aria-hidden="true" /> {t('workflows.autoPublish')}
            </Badge>
          )}
        </div>
        <FlowSummary name={workflow.name} definition={def} />
        <dl className="dam-wfstats">
          <Stat label={t('workflows.stat.running')} value={workflow.runningInstances} />
          <Stat label={t('workflows.stat.folders')} value={workflow.usedByFolders} />
          <Stat label={t('workflows.stat.bindings')} value={workflow.bindings} />
        </dl>
        <div className="dam-wfcardlist__actions">
          <Link
            className="dam-btn dam-btn--secondary dam-btn--sm"
            to={`/settings/workflows/${workflow.id}`}
            aria-label={t('workflows.editNamed', { name: workflow.name })}
          >
            <PencilSimple size={14} aria-hidden="true" />
            {t('workflows.edit')}
          </Link>
          <Button
            size="sm"
            onClick={onDuplicate}
            disabled={busy}
            aria-label={t('workflows.duplicateNamed', { name: workflow.name })}
          >
            <Copy size={14} aria-hidden="true" />
            {t('workflows.duplicate')}
          </Button>
          <Button
            size="sm"
            variant="ghost"
            className="dam-wfdanger"
            onClick={onDelete}
            aria-label={t('workflows.deleteNamed', { name: workflow.name })}
          >
            <Trash size={14} aria-hidden="true" />
            {t('workflows.delete')}
          </Button>
        </div>
      </Card>
    </li>
  );
}

export function WorkflowsPage() {
  const { t } = useTranslation();
  const { push } = useToast();
  useDocumentTitle(t('workflows.title'));
  const eyebrow = useSettingsEyebrow();
  const query = useWorkflows();
  const save = useSaveWorkflow();
  const remove = useDeleteWorkflow();
  const [pendingDelete, setPendingDelete] = useState<WorkflowDto | null>(null);
  const [refusal, setRefusal] = useState<string | null>(null);

  const workflows = query.data ?? [];

  const duplicate = (w: WorkflowDto) =>
    save.mutate(
      {
        name: copyName(
          w.name,
          workflows.map((x) => x.name),
          t('workflows.duplicateSuffix'),
        ),
        description: w.description,
        definition: asDefinition(w),
      },
      {
        onSuccess: (created) => {
          push({ title: t('workflows.duplicated', { name: created.name }), tone: 'success' });
        },
        onError: (e) =>
          push({ title: t('workflows.duplicateFailed'), message: e instanceof ApiError ? e.title : undefined, tone: 'error' }),
      },
    );

  const closeDelete = () => {
    setPendingDelete(null);
    setRefusal(null);
  };

  const newButton = (
    <Link className="dam-btn dam-btn--primary" to="/settings/workflows/new">
      <Plus size={16} weight="bold" aria-hidden="true" />
      {t('workflows.new')}
    </Link>
  );

  const forbidden = isForbidden(query.error);

  return (
    <div className="dam-page dam-wfpage">
      <PageHeader
        title={t('workflows.title')}
        eyebrow={eyebrow}
        description={
          query.isSuccess && workflows.length > 0 ? t('workflows.count', { count: workflows.length }) : t('workflows.description')
        }
        actions={forbidden ? undefined : newButton}
      />

      {query.isPending ? (
        <div role="status" aria-live="polite">
          <span className="dam-sr-only">{t('workflows.loading')}</span>
          <ul className="dam-wflist" aria-hidden="true">
            {[0, 1].map((i) => (
              <li key={i}>
                <Card>
                  <div className="dam-stack dam-stack--fill">
                    <Skeleton height={20} width="40%" />
                    <Skeleton height={14} width="70%" />
                    <Skeleton height={40} />
                  </div>
                </Card>
              </li>
            ))}
          </ul>
        </div>
      ) : forbidden ? (
        <EmptyState
          icon={<WarningCircle size={28} />}
          title={t('workflows.forbidden.title')}
          description={t('workflows.forbidden.description')}
        />
      ) : query.error ? (
        <div role="alert">
          <EmptyState
            icon={<WarningCircle size={28} />}
            title={t('workflows.loadFailed')}
            description={t('workflows.loadFailedHint')}
            action={<Button onClick={() => void query.refetch()}>{t('workflows.retry')}</Button>}
          />
        </div>
      ) : (
        <>
          {workflows.length === 0 ? (
            <EmptyState
              icon={<FlowArrow size={28} />}
              title={t('workflows.empty.title')}
              description={t('workflows.empty.description')}
              action={newButton}
            />
          ) : (
            <ul className="dam-wflist" aria-label={t('workflows.title')}>
              {workflows.map((w) => (
                <WorkflowCard
                  key={w.id}
                  workflow={w}
                  busy={save.isPending}
                  onDuplicate={() => duplicate(w)}
                  onDelete={() => setPendingDelete(w)}
                />
              ))}
            </ul>
          )}
          {workflows.length === 0 && (
            <p className="dam-wfpage__tip">
              <Lightning size={16} aria-hidden="true" /> {t('workflows.empty.template')}
            </p>
          )}

          <section className="dam-wfapplies" aria-labelledby="wf-applies-title">
            <h2 id="wf-applies-title" className="dam-wfapplies__title">
              {t('workflows.applies.title')}
            </h2>
            <p className="dam-hint">
              {workflows.length === 0 ? t('workflows.applies.needWorkflow') : t('workflows.applies.description')}
            </p>
            {workflows.length > 0 && (
              <div className="dam-wfapplies__grid">
                <BindingsEditor workflows={workflows} />
                <FolderDefaults workflows={workflows} />
              </div>
            )}
          </section>
        </>
      )}

      <Modal open={pendingDelete !== null} title={t('workflows.deleteTitle')} onClose={closeDelete}>
        <div className="dam-stack dam-dialogform">
          <p>{t('workflows.deleteBody', { name: pendingDelete?.name })}</p>
          {refusal && (
            <p role="alert" className="dam-wfrefusal">
              <WarningCircle size={16} aria-hidden="true" />
              <span>{t('workflows.deleteRefused', { name: pendingDelete?.name, reason: refusal })}</span>
            </p>
          )}
          <div className="dam-dialogform__actions">
            <Button variant="ghost" onClick={closeDelete}>
              {t('workflows.cancel')}
            </Button>
            <Button
              variant="danger"
              loading={remove.isPending}
              onClick={() => {
                setRefusal(null);
                remove.mutate(pendingDelete!.id, {
                  onSuccess: () => {
                    closeDelete();
                    push({ title: t('workflows.deleted'), tone: 'success' });
                  },
                  onError: (e) => {
                    const reason = conflictMessage(e);
                    if (reason) setRefusal(reason);
                    else {
                      closeDelete();
                      push({
                        title: t('workflows.deleteFailed'),
                        message: e instanceof ApiError ? e.title : undefined,
                        tone: 'error',
                      });
                    }
                  },
                });
              }}
            >
              {t('workflows.delete')}
            </Button>
          </div>
        </div>
      </Modal>
    </div>
  );
}
