import type { BadgeTone } from '@dam/ui-kit';

/** Lifecycle status -> badge tone, the same mapping everywhere a status is shown as a label. */
export function statusTone(status: string): BadgeTone {
  switch (status) {
    case 'published':
      return 'success';
    case 'in_review':
      return 'info';
    case 'approved':
      return 'brand';
    case 'expired':
      return 'warning';
    case 'deleted':
      return 'danger';
    default:
      return 'neutral';
  }
}
