import { ArrowLeft } from '@phosphor-icons/react';
import { useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { ApiError } from '../../../api/client';
import {
  Badge,
  Button,
  Card,
  EmptyState,
  Input,
  LoadingRegion,
  Modal,
  PageHeader,
  SegmentedControl,
  Skeleton,
  WarningCircle,
  useToast,
} from '../../../ui';
import { useDocumentTitle } from '../../library/useDocumentTitle';
import {
  addStepAfter,
  addStepFirst,
  blankDefinition,
  issueStepIndex,
  normalizeDefinition,
  parseDefinition,
  removeStep,
  toJson,
  twoLevelApproval,
  updateStep,
  validateDefinition,
  type DefinitionIssue,
  type WorkflowDefinition,
  type WorkflowStep,
} from '../definition';
import { asDefinition, useValidateWorkflow, useWorkflows, type WorkflowDto } from '../workflowApi';
import { Canvas } from './Canvas';
import { SettingsInspector, StepInspector } from './Inspector';
import { ValidationPanel } from './ValidationPanel';
import { DefinitionRejected, isForbidden, useSaveDesign } from './designerApi';
import { useLeaveGuard } from './useLeaveGuard';
import './designer.css';

interface Draft {
  name: string;
  description: string;
  definition: WorkflowDefinition;
}

const keyOf = (d: Draft) => JSON.stringify([d.name, d.description, d.definition]);
const uniqueIssues = (issues: DefinitionIssue[]) => {
  const seen = new Set<string>();
  return issues.filter((i) => {
    const k = `${i.path}\u0000${i.message}`;
    if (seen.has(k)) return false;
    seen.add(k);
    return true;
  });
};

type StartKey = 'blank' | 'two-level';
const template = (k: StartKey) => (k === 'two-level' ? twoLevelApproval() : blankDefinition());

function Designer({ workflow }: { workflow: WorkflowDto | null }) {
  const { t } = useTranslation();
  const { push } = useToast();
  const navigate = useNavigate();
  const validate = useValidateWorkflow();
  const saveMutation = useSaveDesign();
  const isNew = workflow === null;

  const initial = useMemo<Draft>(
    () =>
      workflow
        ? { name: workflow.name, description: workflow.description, definition: normalizeDefinition(asDefinition(workflow)) }
        : { name: '', description: '', definition: blankDefinition() },
    // The draft belongs to this mount: later refetches of the list must never overwrite an edit in progress.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [],
  );
  const [baseline, setBaseline] = useState(initial);
  const [draft, setDraft] = useState(initial);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [view, setView] = useState<'visual' | 'json'>('visual');
  const [jsonText, setJsonText] = useState('');
  const [jsonError, setJsonError] = useState<string | null>(null);
  const [serverIssues, setServerIssues] = useState<{ key: string; issues: DefinitionIssue[]; ok: boolean } | null>(null);
  const [attempted, setAttempted] = useState(false);
  const [nameError, setNameError] = useState<string | null>(null);
  const [removing, setRemoving] = useState<string | null>(null);
  const [startKey, setStartKey] = useState<StartKey>('blank');
  const [pendingStart, setPendingStart] = useState<StartKey | null>(null);
  const issuesRef = useRef<HTMLElement>(null);
  const nameRef = useRef<HTMLInputElement>(null);
  const jsonRef = useRef<HTMLTextAreaElement>(null);

  const def = draft.definition;
  const defKey = JSON.stringify(def);
  const dirty = keyOf(draft) !== keyOf(baseline);
  const guard = useLeaveGuard(dirty);
  useDocumentTitle(draft.name.trim() || t('workflows.designer.newTitle'));

  const selected = def.steps.find((s) => s.id === selectedId) ?? null;
  const selectedIndex = selected ? def.steps.indexOf(selected) : -1;

  const localIssues: DefinitionIssue[] = [
    ...(jsonError ? [{ path: '', message: t('workflows.issues.json', { message: jsonError }) }] : []),
    ...(attempted && !draft.name.trim() ? [{ path: 'name', message: t('workflows.issues.name') }] : []),
    ...validateDefinition(def),
  ];
  // The server's findings describe the definition they were computed for; any later edit makes them stale.
  const fresh = serverIssues && serverIssues.key === defKey ? serverIssues : null;
  const issues = uniqueIssues([...localIssues, ...(fresh?.issues ?? [])]);
  const issueCounts = new Map<string, number>();
  for (const i of issues) {
    const idx = issueStepIndex(i.path);
    const step = idx === null ? undefined : def.steps[idx];
    if (step) issueCounts.set(step.id, (issueCounts.get(step.id) ?? 0) + 1);
  }

  const setDefinition = (next: WorkflowDefinition) => setDraft((d) => ({ ...d, definition: next }));

  /** Selecting also brings the card into view when the inspector is a bottom sheet that would otherwise cover it. */
  const select = (id: string | null) => {
    setSelectedId(id);
    if (id && window.matchMedia?.('(max-width: 959px)').matches)
      requestAnimationFrame(() =>
        document
          .querySelector<HTMLElement>(`[data-step-id="${CSS.escape(id)}"]`)
          ?.scrollIntoView?.({ block: 'start', behavior: 'smooth' }),
      );
  };

  const patchStep = (id: string, patch: Partial<WorkflowStep>) => {
    setDefinition(updateStep(def, id, patch));
    if (patch.id && selectedId === id) setSelectedId(patch.id);
  };
  const insertAfter = (id: string) => {
    const r = addStepAfter(def, id);
    setDefinition(r.def);
    select(r.id);
  };
  const insertFirst = () => {
    const r = def.steps.length === 0 ? addStepAfter(def) : addStepFirst(def);
    setDefinition(r.def);
    select(r.id);
  };
  const confirmRemove = () => {
    if (removing) {
      setDefinition(removeStep(def, removing));
      if (selectedId === removing) setSelectedId(null);
      push({ title: t('workflows.step.deleted'), tone: 'success' });
    }
    setRemoving(null);
  };

  const switchView = (next: string) => {
    if (next === 'json') {
      setJsonText(toJson(def));
      setJsonError(null);
      setView('json');
    } else if (jsonError) {
      jsonRef.current?.focus();
    } else {
      setView('visual');
    }
  };
  const onJson = (text: string) => {
    setJsonText(text);
    const parsed = parseDefinition(text);
    if ('error' in parsed) setJsonError(parsed.error);
    else {
      setJsonError(null);
      setDefinition(parsed.def);
    }
  };

  const applyTemplate = (k: StartKey) => {
    setStartKey(k);
    setDefinition(template(k));
    setSelectedId(null);
    setPendingStart(null);
  };
  const chooseTemplate = (k: string) => {
    const next = k as StartKey;
    if (next === startKey) return;
    if (JSON.stringify(template(startKey)) !== defKey) setPendingStart(next);
    else applyTemplate(next);
  };

  const go = (issue: DefinitionIssue) => {
    if (issue.path === 'name') {
      nameRef.current?.focus();
      return;
    }
    const idx = issueStepIndex(issue.path);
    const step = idx === null ? undefined : def.steps[idx];
    if (view === 'visual') select(step?.id ?? null);
  };

  const check = () =>
    validate.mutate(def, {
      onSuccess: (r) => {
        const found = r.errors.map((e) => ({ path: e.path, message: e.message }));
        setServerIssues({ key: defKey, issues: found, ok: r.valid });
        if (r.valid && localIssues.length === 0) push({ title: t('workflows.designer.checkOk'), tone: 'success' });
      },
      onError: (e) =>
        push({ title: t('workflows.designer.checkFailed'), message: e instanceof ApiError ? e.title : undefined, tone: 'error' }),
    });

  const save = async () => {
    setAttempted(true);
    setNameError(null);
    const blocking = [...localIssues];
    if (!draft.name.trim() && !blocking.some((p) => p.path === 'name'))
      blocking.push({ path: 'name', message: t('workflows.issues.name') });
    if (blocking.length > 0) {
      push({ title: t('workflows.designer.fixFirst', { count: blocking.length }), tone: 'error' });
      if (blocking.length === 1 && blocking[0]!.path === 'name') nameRef.current?.focus();
      else issuesRef.current?.focus();
      return;
    }
    try {
      const verdict = await validate.mutateAsync(def);
      if (!verdict.valid) {
        setServerIssues({ key: defKey, issues: verdict.errors.map((e) => ({ path: e.path, message: e.message })), ok: false });
        push({ title: t('workflows.designer.fixFirst', { count: verdict.errors.length }), tone: 'error' });
        issuesRef.current?.focus();
        return;
      }
      const saved = await saveMutation.mutateAsync({
        id: workflow?.id,
        name: draft.name,
        description: draft.description,
        definition: def,
      });
      const clean = { name: saved.name, description: saved.description ?? '', definition: def };
      setBaseline({ ...clean });
      setDraft({ ...clean });
      push({ title: t('workflows.designer.saved'), tone: 'success' });
      if (isNew) navigate(`/admin/workflows/${saved.id}`, { replace: true });
    } catch (e) {
      if (e instanceof DefinitionRejected) {
        setServerIssues({ key: defKey, issues: e.issues, ok: false });
        push({ title: t('workflows.designer.fixFirst', { count: e.issues.length }), tone: 'error' });
        issuesRef.current?.focus();
      } else if (e instanceof ApiError && e.status === 409) {
        setNameError(t('workflows.designer.nameTaken'));
        nameRef.current?.focus();
      } else {
        push({ title: t('workflows.designer.saveFailed'), message: e instanceof ApiError ? e.title : undefined, tone: 'error' });
      }
    }
  };

  const discard = () => {
    setDraft(baseline);
    setSelectedId(null);
    setJsonError(null);
    setJsonText(toJson(baseline.definition));
    setNameError(null);
    setAttempted(false);
    push({ title: t('workflows.designer.discarded'), tone: 'info' });
  };

  const saving = validate.isPending || saveMutation.isPending;
  const removingStep = def.steps.find((s) => s.id === removing);
  const nameIssue = issues.find((i) => i.path === 'name')?.message;

  return (
    <div className={`dam-page dam-wfd${selected && view === 'visual' ? ' dam-wfd--sheet' : ''}`}>
      <Link className="dam-wfd__back" to="/admin/workflows">
        <ArrowLeft size={14} weight="bold" aria-hidden="true" />
        {t('workflows.designer.back')}
      </Link>
      <PageHeader
        title={draft.name.trim() || t('workflows.designer.newTitle')}
        actions={
          <div className="dam-wfd__actions">
            {dirty && (
              <Badge tone="warning" role="status">
                {t('workflows.designer.unsaved')}
              </Badge>
            )}
            <Button variant="ghost" onClick={discard} disabled={!dirty || saving}>
              {t('workflows.designer.discard')}
            </Button>
            <Button variant="primary" onClick={() => void save()} loading={saving}>
              {t('workflows.designer.save')}
            </Button>
          </div>
        }
      >
        <div className="dam-wfd__toolbar">
          <SegmentedControl
            label={t('workflows.designer.view')}
            value={view}
            onChange={switchView}
            options={[
              { value: 'visual', label: t('workflows.designer.view.visual') },
              { value: 'json', label: t('workflows.designer.view.json') },
            ]}
          />
          <Button onClick={check} loading={validate.isPending && !saveMutation.isPending}>
            {t('workflows.designer.check')}
          </Button>
        </div>
      </PageHeader>

      <div className="dam-wfd__layout">
        <div className="dam-wfd__main">
          <Card as="section" aria-labelledby="wf-details-title">
            <h2 id="wf-details-title" className="dam-sr-only">
              {t('workflows.designer.details')}
            </h2>
            <div className="dam-wfd__details">
              <Input
                ref={nameRef}
                label={t('workflows.designer.name')}
                value={draft.name}
                placeholder={t('workflows.designer.namePlaceholder')}
                maxLength={200}
                error={nameError ?? nameIssue}
                onChange={(e) => {
                  setNameError(null);
                  setDraft((d) => ({ ...d, name: e.target.value }));
                }}
              />
              <Input
                label={t('workflows.designer.description')}
                value={draft.description}
                hint={t('workflows.designer.descriptionHint')}
                maxLength={1000}
                onChange={(e) => setDraft((d) => ({ ...d, description: e.target.value }))}
              />
            </div>
            {isNew && (
              <div className="dam-wfd__start">
                <SegmentedControl
                  label={t('workflows.designer.startFrom')}
                  value={startKey}
                  onChange={chooseTemplate}
                  options={[
                    { value: 'blank', label: t('workflows.designer.startFrom.blank') },
                    { value: 'two-level', label: t('workflows.designer.startFrom.twoLevel') },
                  ]}
                />
                <span className="dam-hint">{t('workflows.designer.startFromHint')}</span>
              </div>
            )}
          </Card>

          {view === 'visual' ? (
            <Canvas
              def={def}
              selectedId={selected?.id ?? null}
              issueCounts={issueCounts}
              onSelect={select}
              onInsertFirst={insertFirst}
              onInsertAfter={insertAfter}
              onRequestRemove={(id) => def.steps.length > 1 && setRemoving(id)}
            />
          ) : (
            <Card as="section" aria-labelledby="wf-json-title">
              <h2 id="wf-json-title" className="dam-sr-only">
                {t('workflows.json.label')}
              </h2>
              <div className="dam-field">
                <label className="dam-label" htmlFor="wf-json">
                  {t('workflows.json.label')}
                </label>
                <textarea
                  id="wf-json"
                  ref={jsonRef}
                  className="dam-input dam-wfjson"
                  spellCheck={false}
                  value={jsonText}
                  aria-invalid={jsonError ? true : undefined}
                  aria-describedby={jsonError ? 'wf-json-error wf-json-hint' : 'wf-json-hint'}
                  onChange={(e) => onJson(e.target.value)}
                />
                <span id="wf-json-hint" className="dam-hint">
                  {t('workflows.json.hint')}
                </span>
                {jsonError && (
                  <span id="wf-json-error" className="dam-error" role="alert">
                    <WarningCircle size={14} weight="bold" aria-hidden="true" />
                    {jsonError}
                  </span>
                )}
              </div>
            </Card>
          )}
        </div>

        <div className="dam-wfd__side">
          <ValidationPanel ref={issuesRef} def={def} issues={issues} checkedOk={Boolean(fresh?.ok)} onGo={go} />
          {view === 'visual' && (
            <div className="dam-wfd__inspector" data-sheet={selected ? 'open' : 'closed'}>
              {selected ? (
                <StepInspector
                  key={selected.id}
                  step={selected}
                  index={selectedIndex}
                  def={def}
                  issues={issues}
                  onPatch={(patch) => patchStep(selected.id, patch)}
                  onRemove={() => setRemoving(selected.id)}
                  onDone={() => setSelectedId(null)}
                />
              ) : (
                <SettingsInspector def={def} onChange={(patch) => setDefinition({ ...def, ...patch })} />
              )}
            </div>
          )}
        </div>
      </div>

      <Modal open={removing !== null} title={t('workflows.step.deleteTitle')} onClose={() => setRemoving(null)}>
        <div className="dam-stack dam-dialogform">
          <p>
            {t('workflows.step.deleteBody', { name: removingStep ? removingStep.name.trim() || t('workflows.unnamedStep') : '' })}
          </p>
          <div className="dam-dialogform__actions">
            <Button variant="ghost" onClick={() => setRemoving(null)}>
              {t('workflows.cancel')}
            </Button>
            <Button variant="danger" onClick={confirmRemove}>
              {t('workflows.step.deleteConfirm')}
            </Button>
          </div>
        </div>
      </Modal>

      <Modal
        open={pendingStart !== null}
        title={t('workflows.designer.startFromConfirmTitle')}
        onClose={() => setPendingStart(null)}
      >
        <div className="dam-stack dam-dialogform">
          <p>{t('workflows.designer.startFromConfirmBody')}</p>
          <div className="dam-dialogform__actions">
            <Button variant="ghost" onClick={() => setPendingStart(null)}>
              {t('workflows.cancel')}
            </Button>
            <Button variant="danger" onClick={() => pendingStart && applyTemplate(pendingStart)}>
              {t('workflows.designer.startFromConfirm')}
            </Button>
          </div>
        </div>
      </Modal>

      <Modal open={guard.pending !== null} title={t('workflows.leave.title')} onClose={guard.stay}>
        <div className="dam-stack dam-dialogform">
          <p>{t('workflows.leave.body')}</p>
          <div className="dam-dialogform__actions">
            <Button variant="ghost" onClick={guard.stay}>
              {t('workflows.leave.stay')}
            </Button>
            <Button variant="danger" onClick={guard.leave}>
              {t('workflows.leave.leave')}
            </Button>
          </div>
        </div>
      </Modal>
    </div>
  );
}

export function WorkflowDesignerPage() {
  const { t } = useTranslation();
  const { id } = useParams<{ id: string }>();
  const query = useWorkflows();
  const isNew = id === 'new';
  const found = query.data?.find((w) => w.id === id) ?? null;

  if (!isNew && query.isPending)
    return (
      <div className="dam-page dam-wfd">
        <LoadingRegion label={t('workflows.designer.loading')}>
          <Skeleton width={160} height={14} />
          <Skeleton width="40%" height={32} />
          <Skeleton height={320} />
        </LoadingRegion>
      </div>
    );
  if (!isNew && (query.error || !found))
    return (
      <div role="alert" className="dam-page dam-page--narrow">
        <EmptyState
          icon={<WarningCircle size={28} />}
          title={
            isForbidden(query.error)
              ? t('workflows.forbidden.title')
              : query.error
                ? t('workflows.loadFailed')
                : t('workflows.designer.notFound.title')
          }
          description={
            isForbidden(query.error)
              ? t('workflows.forbidden.description')
              : query.error
                ? t('workflows.loadFailedHint')
                : t('workflows.designer.notFound.description')
          }
          action={
            <Link className="dam-btn dam-btn--secondary" to="/admin/workflows">
              {t('workflows.designer.back')}
            </Link>
          }
        />
      </div>
    );

  // Keyed by id, so moving between workflows (or from "new" to the saved one) always starts a fresh draft.
  return <Designer key={id} workflow={isNew ? null : found} />;
}
