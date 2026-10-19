import { CaretDoubleUp, Clock, Rocket, UserCircle, Users } from '@phosphor-icons/react';
import { useRef, type KeyboardEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Badge, IconButton, Plus } from '../../../ui';
import { chainOf, REJECT, type WorkflowDefinition, type WorkflowStep } from '../definition';
import { useSlaText, useStepName } from './flow';
import { useUserNames } from './designerApi';

function Connector({ label, onAdd }: { label: string; onAdd: () => void }) {
  return (
    <div className="dam-wfconn">
      <IconButton label={label} size="sm" variant="secondary" className="dam-wfconn__add" onClick={onAdd}>
        <Plus size={14} weight="bold" aria-hidden="true" />
      </IconButton>
    </div>
  );
}

interface CardProps {
  step: WorkflowStep;
  number: number;
  def: WorkflowDefinition;
  selected: boolean;
  problems: number;
  onSelect: () => void;
  onKeyDown: (e: KeyboardEvent<HTMLButtonElement>) => void;
  setRef: (el: HTMLButtonElement | null) => void;
}

function StepCard({ step, number, def, selected, problems, onSelect, onKeyDown, setRef }: CardProps) {
  const { t } = useTranslation();
  const stepName = useStepName();
  const slaText = useSlaText();
  const names = useUserNames();
  const meta = `meta-${step.id}`;
  const who = (to: { roles: string[]; users: string[] }) => [...to.roles, ...to.users.map(names.nameOf)].join(', ');
  const rejectTarget = step.onReject === REJECT ? null : def.steps.find((s) => s.id === step.onReject);

  return (
    <button
      ref={setRef}
      type="button"
      className={['dam-wfcard', selected && 'dam-wfcard--selected', problems > 0 && 'dam-wfcard--invalid']
        .filter(Boolean)
        .join(' ')}
      aria-pressed={selected}
      aria-label={t('workflows.flow.stepName', { n: number, name: stepName(step) })}
      aria-describedby={meta}
      data-step-id={step.id}
      onClick={onSelect}
      onKeyDown={onKeyDown}
    >
      <span className="dam-wfcard__top">
        <span className="dam-wfcard__num" aria-hidden="true">
          {number}
        </span>
        <span className="dam-wfcard__title">{stepName(step)}</span>
        {problems > 0 && (
          <Badge tone="danger" size="sm">
            {t('workflows.flow.problems', { count: problems })}
          </Badge>
        )}
      </span>
      <span id={meta} className="dam-wfcard__body">
        <span className="dam-wfcard__who">
          {step.assignees.roles.length + step.assignees.users.length === 0 ? (
            <span className="dam-wfcard__nobody">{t('workflows.flow.nobody')}</span>
          ) : (
            <>
              {step.assignees.roles.map((r) => (
                <Badge key={`r-${r}`} size="sm">
                  <Users size={12} aria-hidden="true" /> {r}
                </Badge>
              ))}
              {step.assignees.users.map((u) => (
                <Badge key={`u-${u}`} size="sm" tone="info">
                  <UserCircle size={12} aria-hidden="true" /> {names.nameOf(u)}
                </Badge>
              ))}
            </>
          )}
        </span>
        <span className="dam-wfcard__facts">
          <Badge size="sm" tone={step.mode === 'all' ? 'brand' : 'neutral'}>
            {t(`workflows.mode.${step.mode}`)}
          </Badge>
          <span className="dam-wfcard__fact">
            <Clock size={14} aria-hidden="true" /> {slaText(step.slaHours)}
          </span>
          {step.escalation && (
            <span className="dam-wfcard__fact" title={who(step.escalation.to)}>
              <CaretDoubleUp size={14} aria-hidden="true" />
              {step.escalation.to.roles.length + step.escalation.to.users.length > 0
                ? t('workflows.flow.escalatesTo', { who: who(step.escalation.to) })
                : t('workflows.flow.escalates')}
            </span>
          )}
        </span>
        <span className="dam-wfcard__route">
          {rejectTarget ? t('workflows.flow.rejectToStep', { name: stepName(rejectTarget) }) : t('workflows.flow.rejectToDraft')}
        </span>
      </span>
    </button>
  );
}

/** The flow, top to bottom: Submitted, each step (inline "+" between them), Approved. Cards are real buttons. */
export function Canvas({
  def,
  selectedId,
  issueCounts,
  onSelect,
  onInsertFirst,
  onInsertAfter,
  onRequestRemove,
}: {
  def: WorkflowDefinition;
  selectedId: string | null;
  issueCounts: Map<string, number>;
  onSelect: (id: string | null) => void;
  onInsertFirst: () => void;
  onInsertAfter: (id: string) => void;
  onRequestRemove: (id: string) => void;
}) {
  const { t } = useTranslation();
  const stepName = useStepName();
  const refs = useRef(new Map<string, HTMLButtonElement>());
  const { chain, orphans } = chainOf(def);
  const all = [...chain, ...orphans];
  const last = chain.at(-1);

  const keyHandler = (step: WorkflowStep) => (e: KeyboardEvent<HTMLButtonElement>) => {
    const i = all.findIndex((s) => s.id === step.id);
    const move = (to: number) => {
      const target = all[to];
      if (!target) return;
      e.preventDefault();
      onSelect(target.id);
      refs.current.get(target.id)?.focus();
    };
    if (e.key === 'ArrowDown' || e.key === 'ArrowRight') move(i + 1);
    else if (e.key === 'ArrowUp' || e.key === 'ArrowLeft') move(i - 1);
    else if (e.key === 'Home') move(0);
    else if (e.key === 'End') move(all.length - 1);
    else if (e.key === 'Delete') {
      e.preventDefault();
      onRequestRemove(step.id);
    }
  };

  const card = (step: WorkflowStep, number: number) => (
    <StepCard
      step={step}
      number={number}
      def={def}
      selected={selectedId === step.id}
      problems={issueCounts.get(step.id) ?? 0}
      onSelect={() => onSelect(step.id)}
      onKeyDown={keyHandler(step)}
      setRef={(el) => {
        if (el) refs.current.set(step.id, el);
        else refs.current.delete(step.id);
      }}
    />
  );

  return (
    <section className="dam-wfflow" aria-labelledby="wf-flow-title">
      <h2 id="wf-flow-title" className="dam-sr-only">
        {t('workflows.flow.title')}
      </h2>
      <p className="dam-sr-only">{t('workflows.flow.keyboardHint')}</p>

      <div className="dam-wfnode dam-wfnode--start">
        <span className="dam-wfnode__title">{t('workflows.flow.submitted')}</span>
        <span className="dam-hint">{t('workflows.flow.submittedHint')}</span>
      </div>
      <Connector
        label={chain.length === 0 ? t('workflows.flow.insertAtEnd') : t('workflows.flow.insertAtStart')}
        onAdd={onInsertFirst}
      />

      <ol className="dam-wfsteps">
        {chain.map((step, i) => (
          <li key={step.id}>
            {card(step, i + 1)}
            <Connector
              label={step === last ? t('workflows.flow.insertAtEnd') : t('workflows.flow.insertAfter', { name: stepName(step) })}
              onAdd={() => onInsertAfter(step.id)}
            />
          </li>
        ))}
      </ol>

      <button
        type="button"
        className={`dam-wfnode dam-wfnode--end${selectedId === null ? ' dam-wfnode--selected' : ''}`}
        aria-pressed={selectedId === null}
        onClick={() => onSelect(null)}
      >
        <span className="dam-wfnode__title">{t('workflows.flow.approved')}</span>
        {def.autoPublish ? (
          <Badge tone="brand" size="sm">
            <Rocket size={12} aria-hidden="true" /> {t('workflows.flow.published')}
          </Badge>
        ) : (
          <span className="dam-hint">{t('workflows.flow.approvedHint')}</span>
        )}
      </button>

      {orphans.length > 0 && (
        <div className="dam-wforphans">
          <h3 className="dam-wforphans__title">{t('workflows.flow.orphans.title')}</h3>
          <p className="dam-hint">{t('workflows.flow.orphans.description')}</p>
          <ol className="dam-wfsteps">
            {orphans.map((step, i) => (
              <li key={step.id}>{card(step, chain.length + i + 1)}</li>
            ))}
          </ol>
        </div>
      )}
    </section>
  );
}
