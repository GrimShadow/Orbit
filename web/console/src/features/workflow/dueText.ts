import { useEffect, useState } from 'react';
import { isOverdue, type WorkflowTask } from './workflowApi';

/** "5 hours", "90 minutes", "3 days" - a plain length of time in the reader's language (no "ago" / "in"). */
export function formatDuration(ms: number, locale: string): string {
  const minutes = Math.max(1, Math.round(ms / 60_000));
  const [value, unit] =
    minutes >= 2880
      ? [Math.round(minutes / 1440), 'day']
      : minutes >= 120
        ? [Math.round(minutes / 60), 'hour']
        : [minutes, 'minute'];
  return new Intl.NumberFormat(locale, { style: 'unit', unit, unitDisplay: 'long' }).format(value);
}

export type DueState = { kind: 'none' } | { kind: 'due'; ms: number } | { kind: 'overdue'; ms: number };

/** Where a pending task stands against its deadline. A task counts as overdue once the server flagged the breach OR its
 * due time has passed (the SLA sweep runs every 30 seconds, so the clock can be a moment ahead of the flag). */
export function dueState(task: Pick<WorkflowTask, 'dueAt' | 'slaBreached' | 'status'>, now: number): DueState {
  if (!task.dueAt) return isOverdue(task) ? { kind: 'overdue', ms: 0 } : { kind: 'none' };
  const left = new Date(task.dueAt).getTime() - now;
  if (isOverdue(task) || left <= 0) return { kind: 'overdue', ms: Math.max(0, -left) };
  return { kind: 'due', ms: left };
}

/** Pending inbox order: overdue first (longest overdue first), then due soonest, then tasks with no deadline (in the order
 * given, which is newest first). Stable, so ties keep the server's order. */
export function sortPending<T extends Pick<WorkflowTask, 'dueAt' | 'slaBreached' | 'status'>>(tasks: T[], now: number): T[] {
  const rank = (t: T) => {
    const s = dueState(t, now);
    return s.kind === 'overdue' ? 0 : s.kind === 'due' ? 1 : 2;
  };
  const at = (t: T) => (t.dueAt ? new Date(t.dueAt).getTime() : 0);
  return tasks
    .map((task, index) => ({ task, index }))
    .sort((a, b) => rank(a.task) - rank(b.task) || (rank(a.task) < 2 ? at(a.task) - at(b.task) : 0) || a.index - b.index)
    .map((x) => x.task);
}

/** The current time, refreshed on an interval so "due in 5 hours" does not go stale on a page left open. */
export function useNow(intervalMs = 60_000): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const id = window.setInterval(() => setNow(Date.now()), intervalMs);
    return () => window.clearInterval(id);
  }, [intervalMs]);
  return now;
}
