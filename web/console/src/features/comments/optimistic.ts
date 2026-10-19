import type { CommentDto, CommentThread, CommentThreads, ThreadState } from './commentsApi';

/** Pure edits of a cached thread list, applied the moment a person acts so the card responds before the server answers.
 * The mutation hooks refetch afterwards and roll back on failure, so these only need to be right enough to look right. */

const withCounts = (data: CommentThreads, d: { open?: number; resolved?: number; annotations?: number }) => ({
  ...data,
  counts: {
    open: Math.max(0, data.counts.open + (d.open ?? 0)),
    resolved: Math.max(0, data.counts.resolved + (d.resolved ?? 0)),
    annotations: Math.max(0, data.counts.annotations + (d.annotations ?? 0)),
  },
});

/** Resolve or reopen a thread. A list filtered to the opposite state drops it, as the server's list would. */
export function applyResolved(
  data: CommentThreads,
  state: ThreadState,
  rootId: string,
  resolved: boolean,
  actor?: { id: string; name: string },
): CommentThreads {
  const thread = data.threads.find((t) => t.root.id === rootId);
  if (!thread || thread.root.resolved === resolved) return data;
  const stamp = (c: CommentDto): CommentDto => ({
    ...c,
    resolved,
    resolvedBy: resolved ? (actor?.id ?? c.resolvedBy) : null,
    resolvedByName: resolved ? (actor?.name ?? c.resolvedByName) : null,
    resolvedAt: resolved ? new Date().toISOString() : null,
  });
  const next: CommentThread = { root: stamp(thread.root), replies: thread.replies.map(stamp) };
  const hidden = (state === 'open' && resolved) || (state === 'resolved' && !resolved);
  const threads = hidden
    ? data.threads.filter((t) => t.root.id !== rootId)
    : data.threads.map((t) => (t.root.id === rootId ? next : t));
  return withCounts({ ...data, threads }, { open: resolved ? -1 : 1, resolved: resolved ? 1 : -1 });
}

/** New text on a comment or reply, marked edited. */
export function applyEdit(data: CommentThreads, id: string, body: string): CommentThreads {
  const edit = (c: CommentDto): CommentDto =>
    c.id === id ? { ...c, body, edited: true, editedAt: new Date().toISOString() } : c;
  return { ...data, threads: data.threads.map((t) => ({ root: edit(t.root), replies: t.replies.map(edit) })) };
}

/** Delete a reply (gone), a first comment with no replies (gone) or one with replies (a "[deleted]" placeholder). */
export function applyDelete(data: CommentThreads, id: string): CommentThreads {
  const owner = data.threads.find((t) => t.root.id === id || t.replies.some((r) => r.id === id));
  if (!owner) return data;
  if (owner.root.id !== id) {
    const threads = data.threads.map((t) => (t === owner ? { ...t, replies: t.replies.filter((r) => r.id !== id) } : t));
    return { ...data, threads };
  }
  const root = owner.root;
  const annotations = root.anchor ? -1 : 0;
  if (owner.replies.length === 0)
    return withCounts(
      { ...data, threads: data.threads.filter((t) => t !== owner) },
      { open: root.resolved ? 0 : -1, resolved: root.resolved ? -1 : 0, annotations },
    );
  const placeholder: CommentDto = { ...root, deleted: true, body: '[deleted]', anchor: null as never, mentions: [] };
  return withCounts(
    { ...data, threads: data.threads.map((t) => (t === owner ? { ...t, root: placeholder } : t)) },
    { annotations },
  );
}
