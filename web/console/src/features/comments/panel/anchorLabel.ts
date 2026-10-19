import type { TFunction } from 'i18next';
import { formatTimecode } from '../commentsApi';

/** The parts of an anchor the label needs - satisfied by both the API's `AnchorDto` and the stage's `NewAnchor`. */
export interface AnchorLike {
  type: string;
  page?: number | null;
  startMs?: number | null;
  endMs?: number | null;
}

/** "Region on page 2", "Pin", "At 1:23", "1:23 - 1:40" - what an anchor is called on a card and on the composer's chip. */
export function anchorLabel(a: AnchorLike, t: TFunction): string {
  if (a.type === 'timecode') {
    const start = formatTimecode(a.startMs ?? 0);
    return a.endMs != null
      ? t('comments.anchor.range', { start, end: formatTimecode(a.endMs) })
      : t('comments.anchor.at', { time: start });
  }
  const key = a.type === 'point' ? 'pin' : 'region';
  return a.page != null ? t(`comments.anchor.${key}Page`, { page: a.page }) : t(`comments.anchor.${key}`);
}
