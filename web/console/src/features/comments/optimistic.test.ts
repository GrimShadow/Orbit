import { applyDelete, applyEdit, applyResolved } from './optimistic';
import { comment, thread } from './panel/commentsTestKit';
import type { CommentThreads } from './commentsApi';

const data = (): CommentThreads => ({
  threads: [
    thread(comment({ id: 'r1', anchor: { type: 'point' } as never }), [comment({ id: 'p1', parentId: 'r1' })]),
    thread(comment({ id: 'r2' })),
    thread(comment({ id: 'r3', resolved: true })),
  ],
  counts: { open: 2, resolved: 1, annotations: 1 },
});

describe('applyResolved', () => {
  it('resolves a thread (and its replies) and moves the counts', () => {
    const out = applyResolved(data(), 'all', 'r1', true, { id: 'u1', name: 'Dev Admin' });
    expect(out.threads[0]!.root).toMatchObject({ resolved: true, resolvedByName: 'Dev Admin' });
    expect(out.threads[0]!.replies[0]!.resolved).toBe(true);
    expect(out.counts).toEqual({ open: 1, resolved: 2, annotations: 1 });
  });
  it('drops the thread from a list filtered to the other state', () => {
    expect(applyResolved(data(), 'open', 'r2', true).threads.map((t) => t.root.id)).toEqual(['r1', 'r3']);
    expect(applyResolved(data(), 'resolved', 'r3', false).threads.map((t) => t.root.id)).toEqual(['r1', 'r2']);
  });
  it('does nothing when the thread is already in that state or unknown', () => {
    const d = data();
    expect(applyResolved(d, 'all', 'r3', true)).toBe(d);
    expect(applyResolved(d, 'all', 'nope', true)).toBe(d);
  });
});

describe('applyEdit', () => {
  it('replaces the text and marks it edited', () => {
    const out = applyEdit(data(), 'p1', 'New words');
    expect(out.threads[0]!.replies[0]).toMatchObject({ body: 'New words', edited: true });
    expect(out.threads[1]!.root.edited).toBe(false);
  });
});

describe('applyDelete', () => {
  it('removes a reply', () => {
    expect(applyDelete(data(), 'p1').threads[0]!.replies).toEqual([]);
  });
  it('removes a lone first comment and fixes the counts', () => {
    const out = applyDelete(data(), 'r2');
    expect(out.threads.map((t) => t.root.id)).toEqual(['r1', 'r3']);
    expect(out.counts).toEqual({ open: 1, resolved: 1, annotations: 1 });
  });
  it('leaves a placeholder when the first comment has replies', () => {
    const out = applyDelete(data(), 'r1');
    expect(out.threads[0]!.root).toMatchObject({ deleted: true, body: '[deleted]' });
    expect(out.threads[0]!.replies).toHaveLength(1);
    expect(out.counts).toEqual({ open: 2, resolved: 1, annotations: 0 });
  });
});
