import type { CommentThread } from './commentsApi';

/** Annotation numbers shown on the stage pins/regions and on the matching thread cards. They come from the version's
 * UNFILTERED thread list (oldest first), so a number never changes when the panel's open/resolved filter does.
 * Threads without an anchor get no number. */
export function annotationNumbers(threads: readonly CommentThread[]): Map<string, number> {
  const out = new Map<string, number>();
  let n = 0;
  for (const t of threads) if (t.root.anchor && !t.root.deleted) out.set(t.root.id, ++n);
  return out;
}
