import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Card, Input, Select, SegmentedControl, Trash } from '../../../ui';
import {
  COMPLETE,
  emptyAssignees,
  ID_PATTERN,
  REJECT,
  type DefinitionIssue,
  type StepMode,
  type WorkflowDefinition,
  type WorkflowStep,
} from '../definition';
import { AssigneePicker } from './AssigneePicker';
import { useStepName } from './flow';

/** A switch-style checkbox with a sentence explaining what it does. */
function Toggle({
  label,
  hint,
  checked,
  onChange,
}: {
  label: string;
  hint: string;
  checked: boolean;
  onChange: (on: boolean) => void;
}) {
  return (
    <div className="dam-wftoggle">
      <label className="dam-check">
        <input type="checkbox" className="dam-check__input" checked={checked} onChange={(e) => onChange(e.target.checked)} />
        <span className="dam-check__label">{label}</span>
      </label>
      <p className="dam-hint dam-wftoggle__hint">{hint}</p>
    </div>
  );
}

/** The step id is referenced by other steps, so it is only renamed once the typed value is a valid, unused id. */
function IdField({ value, taken, onCommit }: { value: string; taken: string[]; onCommit: (id: string) => void }) {
  const { t } = useTranslation();
  const [draft, setDraft] = useState(value);
  const [seen, setSeen] = useState(value);
  if (seen !== value) {
    setSeen(value);
    setDraft(value);
  }
  const error = !ID_PATTERN.test(draft)
    ? t('workflows.inspector.idInvalid')
    : draft !== value && taken.includes(draft)
      ? t('workflows.inspector.idTaken')
      : undefined;
  const commit = () => {
    if (!error && draft !== value) onCommit(draft);
    else if (error) setDraft(value);
  };
  return (
    <Input
      label={t('workflows.inspector.id')}
      hint={t('workflows.inspector.idHint')}
      value={draft}
      error={error}
      maxLength={40}
      onChange={(e) => setDraft(e.target.value)}
      onBlur={commit}
      onKeyDown={(e) => {
        if (e.key === 'Enter') {
          e.preventDefault();
          commit();
        }
      }}
    />
  );
}

export function StepInspector({
  step,
  index,
  def,
  issues,
  onPatch,
  onRemove,
  onDone,
}: {
  step: WorkflowStep;
  index: number;
  def: WorkflowDefinition;
  issues: DefinitionIssue[];
  onPatch: (patch: Partial<WorkflowStep>) => void;
  onRemove: () => void;
  onDone: () => void;
}) {
  const { t } = useTranslation();
  const stepName = useStepName();
  const prefix = `steps[${index}]`;
  const errorFor = (field: string): string | undefined => {
    const hits = issues.filter(
      (i) =>
        i.path === `${prefix}.${field}` || i.path.startsWith(`${prefix}.${field}.`) || i.path.startsWith(`${prefix}.${field}[`),
    );
    return hits.length > 0 ? [...new Set(hits.map((h) => h.message))].join(' ') : undefined;
  };
  const others = def.steps.filter((s) => s.id !== step.id);
  const stepOptions = (extra: { value: string; label: string }) => {
    const options = [...others.map((s) => ({ value: s.id, label: stepName(s) })), extra];
    return options;
  };
  const approveOptions = stepOptions({ value: COMPLETE, label: t('workflows.inspector.finish') });
  const rejectOptions = [
    { value: REJECT, label: t('workflows.inspector.returnToDraft') },
    ...others.map((s) => ({ value: s.id, label: stepName(s) })),
  ];
  // A target the list does not offer (a step that no longer exists) stays selectable so it is visible, not silently swapped.
  const withCurrent = (opts: { value: string; label: string }[], current: string) =>
    opts.some((o) => o.value === current) ? opts : [...opts, { value: current, label: current }];

  return (
    <Card as="section" aria-labelledby="wf-inspector-title" className="dam-wfinspector">
      <div className="dam-wfinspector__head">
        <h2 id="wf-inspector-title" className="dam-wfinspector__title">
          {t('workflows.inspector.step', { n: index + 1, name: stepName(step) })}
        </h2>
        <Button size="sm" variant="ghost" className="dam-wfinspector__done" onClick={onDone}>
          {t('workflows.inspector.done')}
        </Button>
      </div>

      <div className="dam-wfinspector__form">
        <Input
          label={t('workflows.inspector.name')}
          value={step.name}
          placeholder={t('workflows.inspector.namePlaceholder')}
          error={errorFor('name')}
          onChange={(e) => onPatch({ name: e.target.value })}
        />

        <AssigneePicker
          label={t('workflows.inspector.assignees')}
          hint={t('workflows.inspector.assigneesHint')}
          value={step.assignees}
          onChange={(assignees) => onPatch({ assignees })}
          error={errorFor('assignees')}
        />

        <div className="dam-field">
          <SegmentedControl
            label={t('workflows.inspector.mode')}
            value={step.mode}
            onChange={(mode) => onPatch({ mode: mode as StepMode })}
            options={[
              { value: 'any', label: t('workflows.inspector.mode.any') },
              { value: 'all', label: t('workflows.inspector.mode.all') },
            ]}
          />
          <span className="dam-hint">
            {step.mode === 'all' ? t('workflows.inspector.mode.allHint') : t('workflows.inspector.mode.anyHint')}
          </span>
        </div>

        <Input
          label={t('workflows.inspector.sla')}
          hint={t('workflows.inspector.slaHint')}
          type="number"
          inputMode="numeric"
          min={1}
          max={8760}
          step={1}
          value={step.slaHours ?? ''}
          error={errorFor('slaHours')}
          onChange={(e) => onPatch({ slaHours: e.target.value === '' ? null : Number(e.target.value) })}
        />

        <Toggle
          label={t('workflows.inspector.escalate')}
          hint={t('workflows.inspector.escalateHint')}
          checked={step.escalation !== null}
          onChange={(on) => onPatch({ escalation: on ? { to: emptyAssignees() } : null })}
        />
        {step.escalation && (
          <>
            {step.slaHours === null && <p className="dam-hint dam-wfwarn">{t('workflows.inspector.escalateNeedsSla')}</p>}
            <AssigneePicker
              label={t('workflows.inspector.escalateTo')}
              value={step.escalation.to}
              onChange={(to) => onPatch({ escalation: { to } })}
              error={errorFor('escalation')}
            />
          </>
        )}

        <Select
          label={t('workflows.inspector.onApprove')}
          hint={t('workflows.inspector.onApproveHint')}
          options={withCurrent(approveOptions, step.onApprove)}
          value={step.onApprove}
          error={errorFor('onApprove')}
          onChange={(e) => onPatch({ onApprove: e.target.value })}
        />
        <Select
          label={t('workflows.inspector.onReject')}
          hint={t('workflows.inspector.onRejectHint')}
          options={withCurrent(rejectOptions, step.onReject)}
          value={step.onReject}
          error={errorFor('onReject')}
          onChange={(e) => onPatch({ onReject: e.target.value })}
        />

        <details className="dam-wfadvanced">
          <summary>{t('workflows.inspector.advanced')}</summary>
          <IdField value={step.id} taken={def.steps.map((s) => s.id)} onCommit={(id) => onPatch({ id })} />
        </details>

        <div className="dam-wfinspector__danger">
          <Button variant="danger" onClick={onRemove} disabled={def.steps.length <= 1}>
            <Trash size={14} aria-hidden="true" />
            {t('workflows.inspector.deleteStep')}
          </Button>
          {def.steps.length <= 1 && <span className="dam-hint">{t('workflows.inspector.cannotDeleteLast')}</span>}
        </div>
      </div>
    </Card>
  );
}

export function SettingsInspector({
  def,
  onChange,
  children,
}: {
  def: WorkflowDefinition;
  onChange: (patch: Partial<WorkflowDefinition>) => void;
  children?: ReactNode;
}) {
  const { t } = useTranslation();
  return (
    <Card as="section" aria-labelledby="wf-settings-title" className="dam-wfinspector">
      <div className="dam-wfinspector__head">
        <h2 id="wf-settings-title" className="dam-wfinspector__title">
          {t('workflows.inspector.settings')}
        </h2>
      </div>
      <p className="dam-hint">{t('workflows.inspector.selectHint')}</p>
      <div className="dam-wfinspector__form">
        <Toggle
          label={t('workflows.inspector.autoPublish')}
          hint={t('workflows.inspector.autoPublishHint')}
          checked={def.autoPublish}
          onChange={(autoPublish) => onChange({ autoPublish })}
        />
        <Toggle
          label={t('workflows.inspector.distinct')}
          hint={t('workflows.inspector.distinctHint')}
          checked={def.distinctApprovers}
          onChange={(distinctApprovers) => onChange({ distinctApprovers })}
        />
        {children}
      </div>
    </Card>
  );
}
