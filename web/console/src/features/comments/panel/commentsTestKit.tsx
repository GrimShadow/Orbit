// Fixtures, a stateful fake comments backend and a render harness for the Comments / Activity panel tests.
import { render } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useEffect, type ReactNode } from 'react';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { ToastProvider } from '../../../ui';
import { AuthContext } from '../../../auth/AuthProvider';
import { json, signedIn, type Call } from '../../workflow/workflowTestKit';
import { CommentsProvider, useCommentsUi } from '../CommentsContext';
import type { ActivityItem, CommentDto, CommentThread, CommentThreads, MentionableUser, NewAnchor } from '../commentsApi';

export const ME = 'u1';
export const PRIYA = '0197b000-0000-7000-8000-00000000a001';
export const SAM = '0197b000-0000-7000-8000-00000000a002';

export const ago = (minutes: number) => new Date(Date.now() - minutes * 60_000).toISOString();

export function comment(over: Partial<CommentDto> & { id: string }): CommentDto {
  return {
    assetId: 'a1',
    versionId: 'v2',
    versionNo: 2,
    parentId: null,
    authorId: PRIYA,
    authorName: 'Priya Natarajan',
    body: 'Please brighten the sky.',
    mentions: [],
    anchor: null as never,
    resolved: false,
    resolvedBy: null,
    resolvedByName: null,
    resolvedAt: null,
    edited: false,
    editedAt: null,
    deleted: false,
    createdAt: ago(90),
    ...over,
  };
}

export const region = (over: Record<string, unknown> = {}) =>
  ({ type: 'region', x: 0.1, y: 0.2, w: 0.3, h: 0.25, page: null, startMs: null, endMs: null, ...over }) as never;
export const timecode = (startMs: number, endMs: number | null = null) =>
  ({ type: 'timecode', x: null, y: null, w: null, h: null, page: null, startMs, endMs }) as never;

export const thread = (root: CommentDto, replies: CommentDto[] = []): CommentThread => ({ root, replies });

export const PEOPLE: MentionableUser[] = [
  { id: PRIYA, displayName: 'Priya Natarajan', email: 'priya@acme.example' },
  { id: SAM, displayName: 'Sam Reviewer', email: 'sam@acme.example' },
];

export function activity(over: Partial<ActivityItem> & { id: string }): ActivityItem {
  return {
    kind: 'comment',
    occurredAt: ago(30),
    actorId: PRIYA,
    actorName: 'Priya Natarajan',
    summary: 'added a comment',
    commentId: null,
    versionNo: null,
    detail: null,
    ...over,
  };
}

const counts = (threads: CommentThread[]) => ({
  open: threads.filter((t) => !t.root.resolved).length,
  resolved: threads.filter((t) => t.root.resolved).length,
  annotations: threads.filter((t) => t.root.anchor).length,
});

export interface Backend {
  threads: CommentThread[];
  /** Answer for POST /comments, PATCH, DELETE - override per test. */
  posts: Call[];
  patches: { id: string; body: unknown }[];
  deletes: string[];
  failPost?: Response;
}

/** A tiny comment backend over `threads`: GET filters like the server, writes are recorded (and POST adds the comment). */
export function serveComments(
  initial: CommentThread[],
  more: (c: Call) => Response | Promise<Response> | undefined = () => undefined,
) {
  const be: Backend = { threads: initial, posts: [], patches: [], deletes: [] };
  const handler = (c: Call): Response | Promise<Response> | undefined => {
    const custom = more(c);
    if (custom) return custom;
    if (c.pathname === '/api/v1/assets/a1/comments' && c.method === 'GET') {
      const versionId = c.search.get('versionId');
      const state = c.search.get('state') ?? 'all';
      const kind = c.search.get('kind') ?? 'all';
      const inVersion = be.threads.filter((t) => !versionId || t.root.versionId === versionId);
      const shown = inVersion.filter(
        (t) =>
          (state === 'all' || (state === 'open') === !t.root.resolved) &&
          (kind === 'all' || (kind === 'annotation') === !!t.root.anchor),
      );
      const body: CommentThreads = { threads: shown, counts: counts(inVersion) };
      return json(body);
    }
    if (c.pathname === '/api/v1/assets/a1/comments' && c.method === 'POST')
      return (async () => {
        be.posts.push(c);
        if (be.failPost) return be.failPost;
        const input = (await c.json()) as { body: string; parentId: string | null; versionId: string | null; anchor: unknown };
        const made = comment({
          id: `new-${be.posts.length}`,
          authorId: ME,
          authorName: 'Dev Admin',
          body: input.body,
          parentId: input.parentId,
          anchor: (input.anchor ?? null) as never,
          createdAt: new Date().toISOString(),
        });
        if (input.parentId)
          be.threads = be.threads.map((t) => (t.root.id === input.parentId ? { ...t, replies: [...t.replies, made] } : t));
        else be.threads = [...be.threads, thread(made)];
        return json(made, 201);
      })();
    const patch = /^\/api\/v1\/comments\/([^/]+)$/.exec(c.pathname);
    if (patch && c.method === 'PATCH')
      return (async () => {
        const id = patch[1]!;
        const input = (await c.json()) as { body: string | null; resolved: boolean | null };
        be.patches.push({ id, body: input });
        let out: CommentDto | undefined;
        be.threads = be.threads.map((t) => {
          const edit = (x: CommentDto) => {
            if (x.id !== id) return x;
            out = {
              ...x,
              body: input.body ?? x.body,
              edited: input.body != null ? true : x.edited,
              resolved: input.resolved ?? x.resolved,
              resolvedBy: input.resolved ? ME : input.resolved === false ? null : x.resolvedBy,
              resolvedByName: input.resolved ? 'Dev Admin' : input.resolved === false ? null : x.resolvedByName,
              resolvedAt: input.resolved ? new Date().toISOString() : input.resolved === false ? null : x.resolvedAt,
            };
            return out;
          };
          const root = edit(t.root);
          return { root, replies: t.replies.map(edit) };
        });
        return json(out ?? comment({ id }));
      })();
    if (patch && c.method === 'DELETE') {
      const id = patch[1]!;
      be.deletes.push(id);
      be.threads = be.threads
        .map((t) => ({ ...t, replies: t.replies.filter((r) => r.id !== id) }))
        .filter((t) => t.root.id !== id || t.replies.length > 0)
        .map((t) =>
          t.root.id === id ? { ...t, root: { ...t.root, deleted: true, body: '[deleted]', anchor: null as never } } : t,
        );
      return new Response(null, { status: 204 });
    }
    if (c.pathname === '/api/v1/assets/a1/mentionable') {
      const q = (c.search.get('q') ?? '').toLowerCase();
      return json(PEOPLE.filter((p) => p.displayName.toLowerCase().includes(q) || p.email.includes(q)));
    }
    return undefined;
  };
  return { be, handler };
}

/** Shows (and lets a test drive) the shared comments state, standing in for the preview stage. */
export function Stage({ spies }: { spies: { seek: (ms: number) => void; page: (p: number) => void } }) {
  const ui = useCommentsUi();
  const { registerSeek, registerShowPage } = ui;
  useEffect(() => {
    registerSeek(spies.seek);
    registerShowPage(spies.page);
    return () => {
      registerSeek(null);
      registerShowPage(null);
    };
  }, [registerSeek, registerShowPage, spies]);
  const draw: NewAnchor = { type: 'region', x: 0.25, y: 0.5, w: 0.125, h: 0.25, page: 2 };
  return (
    <div data-testid="stage">
      <output data-testid="ui-selected">{ui.selectedThreadId ?? ''}</output>
      <output data-testid="ui-hovered">{ui.hoveredThreadId ?? ''}</output>
      <output data-testid="ui-draft">{ui.draftAnchor ? JSON.stringify(ui.draftAnchor) : ''}</output>
      <button onClick={() => ui.selectThread('r1')}>stage: select r1</button>
      <button onClick={() => ui.hoverThread('r1')}>stage: hover r1</button>
      <button
        onClick={() => {
          ui.setDraftAnchor(draw);
          ui.focusComposer();
        }}
      >
        stage: draw region
      </button>
      <button onClick={() => ui.setDraftAnchor({ type: 'timecode', startMs: 83500 })}>stage: capture time</button>
    </div>
  );
}

function Where() {
  const loc = useLocation();
  return <output data-testid="where">{`${loc.pathname}${loc.search}`}</output>;
}

export function newQueryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } });
}

/** Renders `children` (a tab) with auth, query client, toasts, a router at `route` and the comments context. */
export function renderPanel(
  children: ReactNode,
  opts: { route?: string; versionId?: string | null; spies?: { seek: (ms: number) => void; page: (p: number) => void } } = {},
) {
  const spies = opts.spies ?? { seek: vi.fn(), page: vi.fn() };
  const client = newQueryClient();
  const utils = render(
    <AuthContext.Provider value={signedIn}>
      <QueryClientProvider client={client}>
        <ToastProvider>
          <MemoryRouter initialEntries={[opts.route ?? '/library/assets/a1']}>
            <CommentsProvider assetId="a1" assetType="image" versionId={opts.versionId === undefined ? 'v2' : opts.versionId}>
              <Stage spies={spies} />
              {children}
              <Where />
            </CommentsProvider>
          </MemoryRouter>
        </ToastProvider>
      </QueryClientProvider>
    </AuthContext.Provider>,
  );
  return { ...utils, spies, client };
}
