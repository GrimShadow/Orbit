import { ArrowRight, Clock } from '@phosphor-icons/react';
import { Fragment } from 'react';
import { useTranslation } from 'react-i18next';
import { chainOf, type WorkflowDefinition, type WorkflowStep } from '../definition';

/** What a step is called on screen: its name, or a placeholder while it is still blank. */
export function useStepName() {
  const { t } = useTranslation();
  return (s: Pick<WorkflowStep, 'name' | 'id'>) => s.name.trim() || t('workflows.unnamedStep');
}

export function useSlaText() {
  const { t } = useTranslation();
  return (hours: number | null) => (hours === null ? t('workflows.noSla') : t('workflows.sla', { hours }));
}

/** A compact one-line picture of a workflow: each step with its mode and deadline, joined by arrows. */
export function FlowSummary({ name, definition }: { name: string; definition: WorkflowDefinition }) {
  const { t } = useTranslation();
  const stepName = useStepName();
  const slaText = useSlaText();
  const { chain, orphans } = chainOf(definition);
  const steps = [...chain, ...orphans];
  if (steps.length === 0) return <p className="dam-hint">{t('workflows.noSteps')}</p>;
  return (
    <ol className="dam-wfsummary" aria-label={t('workflows.flow', { name })}>
      {steps.map((s, i) => (
        <Fragment key={s.id}>
          {i > 0 && (
            <li className="dam-wfsummary__arrow" aria-hidden="true">
              <ArrowRight size={14} weight="bold" />
            </li>
          )}
          <li className="dam-wfsummary__step">
            <span className="dam-wfsummary__name">{stepName(s)}</span>
            <span className="dam-wfsummary__meta">
              {t(`workflows.mode.${s.mode}`)}
              <span aria-hidden="true"> · </span>
              <Clock size={12} aria-hidden="true" /> {slaText(s.slaHours)}
            </span>
          </li>
        </Fragment>
      ))}
    </ol>
  );
}
