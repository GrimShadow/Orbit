import { useTranslation } from 'react-i18next';
import { useReviewSummary } from '../features/translation/translationApi';
import { useTaskSummary } from '../features/workflow/workflowApi';

export interface NavBadgeInfo {
  /** What is painted in the pill ("3", "99+"). */
  text: string;
  /** The link's accessible name: "Tasks, 3 pending, 1 overdue". */
  name: string;
  overdue: boolean;
}

/** The count pill on the Tasks nav item: how many tasks wait for me plus how many assets have translations waiting for my
 * review, danger-toned when any task is overdue. `null` (render nothing) while loading, on error, and at zero. Either
 * count failing to load counts as zero for that part. */
export function useTasksBadge(): NavBadgeInfo | null {
  const { t } = useTranslation();
  const summary = useTaskSummary().data;
  const reviews = useReviewSummary().data?.translation ?? 0;
  const pending = summary?.pendingForMe ?? 0;
  const overdue = summary?.overdueForMe ?? 0;
  const total = pending + reviews;
  if (total <= 0) return null;
  const parts = [t('nav.tasks')];
  if (pending > 0) parts.push(t('tasks.badge.pending', { count: pending }));
  if (overdue > 0) parts.push(t('tasks.badge.overdue', { count: overdue }));
  if (reviews > 0) parts.push(t('tasks.badge.translations', { count: reviews }));
  return { text: total > 99 ? '99+' : String(total), name: parts.join(', '), overdue: overdue > 0 };
}

export function NavCount({ badge }: { badge: NavBadgeInfo }) {
  return (
    <span className={`dam-navbadge${badge.overdue ? ' dam-navbadge--danger' : ''}`} aria-hidden="true">
      {badge.text}
    </span>
  );
}
