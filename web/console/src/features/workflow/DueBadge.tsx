import { Clock, WarningCircle } from '@phosphor-icons/react';
import { useTranslation } from 'react-i18next';
import { Badge } from '../../ui';
import { dueState, formatDuration } from './dueText';
import type { WorkflowTask } from './workflowApi';

/** "Due in 5 hours" (neutral, info inside the last day), "Overdue by 2 hours" (danger) or "No deadline". The words always
 * carry the meaning; the tone only reinforces it. */
export function DueBadge({ task, now }: { task: Pick<WorkflowTask, 'dueAt' | 'slaBreached' | 'status'>; now: number }) {
  const { t, i18n } = useTranslation();
  const due = dueState(task, now);
  if (due.kind === 'overdue')
    return (
      <Badge tone="danger" size="sm">
        <WarningCircle size={12} weight="fill" aria-hidden="true" />
        {t('tasks.due.overdue', { duration: formatDuration(due.ms, i18n.language) })}
      </Badge>
    );
  if (due.kind === 'due')
    return (
      <Badge tone={due.ms <= 86_400_000 ? 'info' : 'neutral'} size="sm">
        <Clock size={12} weight="bold" aria-hidden="true" />
        {t('tasks.due.in', { duration: formatDuration(due.ms, i18n.language) })}
      </Badge>
    );
  return (
    <Badge size="sm">
      <Clock size={12} weight="bold" aria-hidden="true" />
      {t('tasks.due.none')}
    </Badge>
  );
}
