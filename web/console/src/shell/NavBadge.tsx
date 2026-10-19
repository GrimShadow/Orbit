import { useTranslation } from 'react-i18next';
import { useTaskSummary } from '../features/workflow/workflowApi';

export interface NavBadgeInfo {
  /** What is painted in the pill ("3", "99+"). */
  text: string;
  /** The link's accessible name: "Tasks, 3 pending, 1 overdue". */
  name: string;
  overdue: boolean;
}

/** The count pill on the Tasks nav item: how many tasks wait for me, danger-toned when any is overdue. `null` (render
 * nothing) while loading, on error, and at zero. */
export function useTasksBadge(): NavBadgeInfo | null {
  const { t } = useTranslation();
  const summary = useTaskSummary().data;
  if (!summary || summary.pendingForMe <= 0) return null;
  const { pendingForMe: pending, overdueForMe: overdue } = summary;
  const parts = [t('nav.tasks'), t('tasks.badge.pending', { count: pending })];
  if (overdue > 0) parts.push(t('tasks.badge.overdue', { count: overdue }));
  return { text: pending > 99 ? '99+' : String(pending), name: parts.join(', '), overdue: overdue > 0 };
}

export function NavCount({ badge }: { badge: NavBadgeInfo }) {
  return (
    <span className={`dam-navbadge${badge.overdue ? ' dam-navbadge--danger' : ''}`} aria-hidden="true">
      {badge.text}
    </span>
  );
}
