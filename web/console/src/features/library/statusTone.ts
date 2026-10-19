import type { BadgeTone } from '../../ui';

/** Lifecycle status -> badge colour. Colour is never the only signal (the badge always carries the status text). */
const STATUS_TONE: Record<string, BadgeTone> = {
  draft: 'neutral',
  in_review: 'info',
  approved: 'success',
  published: 'success',
  expired: 'warning',
  archived: 'neutral',
  deleted: 'danger',
};

export const statusTone = (status: string): BadgeTone => STATUS_TONE[status] ?? 'neutral';
