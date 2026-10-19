import { CheckCircle, WarningCircle } from '@phosphor-icons/react';
import { forwardRef } from 'react';
import { useTranslation } from 'react-i18next';
import { Card } from '../../../ui';
import { issueStepIndex, type DefinitionIssue, type WorkflowDefinition } from '../definition';
import { useStepName } from './flow';

/** Everything wrong, as buttons: choosing one selects the step it is about. The server's findings (after "Check" or a
 * refused save) appear alongside the instant local ones. */
export const ValidationPanel = forwardRef<
  HTMLElement,
  {
    def: WorkflowDefinition;
    issues: DefinitionIssue[];
    checkedOk: boolean;
    onGo: (issue: DefinitionIssue) => void;
  }
>(function ValidationPanel({ def, issues, checkedOk, onGo }, ref) {
  const { t } = useTranslation();
  const stepName = useStepName();
  const where = (i: DefinitionIssue) => {
    const idx = issueStepIndex(i.path);
    const step = idx === null ? undefined : def.steps[idx];
    return step ? stepName(step) : t('workflows.issues.workflow');
  };
  return (
    <Card as="section" aria-labelledby="wf-issues-title" className="dam-wfissues" ref={ref} tabIndex={-1}>
      <h2 id="wf-issues-title" className="dam-wfissues__title">
        {issues.length > 0 ? (
          <WarningCircle size={18} weight="fill" aria-hidden="true" className="dam-wfissues__icon dam-wfissues__icon--bad" />
        ) : (
          <CheckCircle size={18} weight="fill" aria-hidden="true" className="dam-wfissues__icon dam-wfissues__icon--ok" />
        )}
        {t('workflows.issues.title')}
        {issues.length > 0 && <span className="dam-wfissues__count"> ({issues.length})</span>}
      </h2>
      {issues.length === 0 ? (
        <p className="dam-hint" role="status">
          {checkedOk ? t('workflows.issues.checked') : t('workflows.issues.none')}
        </p>
      ) : (
        <ul className="dam-wfissues__list">
          {issues.map((i, n) => (
            <li key={`${i.path}-${n}-${i.message}`}>
              <button type="button" className="dam-wfissues__item" onClick={() => onGo(i)}>
                <span className="dam-wfissues__where">{where(i)}</span>
                <span className="dam-wfissues__msg">{i.message}</span>
              </button>
            </li>
          ))}
        </ul>
      )}
      <p className="dam-hint dam-wfissues__hint">{t('workflows.issues.hint')}</p>
    </Card>
  );
});
