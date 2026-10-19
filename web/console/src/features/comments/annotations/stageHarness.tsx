/* Test helpers for the stage annotation tests: a fake API, a real CommentsProvider and a probe that exposes its state. */
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render } from '@testing-library/react';
import { useEffect } from 'react';
import type { Mock } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { AssetPreview } from '../../asset/AssetPreview';
import { AuthContext, type AuthState } from '../../../auth/AuthProvider';
import { ToastProvider } from '../../../ui';
import { CommentsProvider, useCommentsUi, type CommentsUi } from '../CommentsContext';
import type { CommentThread } from '../commentsApi';

export const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

export const probe: { ui: CommentsUi | null; focus: Mock<() => void> } = { ui: null, focus: vi.fn<() => void>() };

function Probe() {
  const ui = useCommentsUi();
  const { registerFocusComposer } = ui;
  // After every render, so tests read the context the stage last saw.
  useEffect(() => {
    probe.ui = ui;
  });
  useEffect(() => {
    registerFocusComposer(probe.focus);
    return () => registerFocusComposer(null);
  }, [registerFocusComposer]);
  return null;
}

/** One thread. `anchor` fields not given are null, as the API sends them. */
export function thread(
  id: string,
  anchor: Record<string, unknown> | null,
  over: { body?: string; resolved?: boolean; versionId?: string; deleted?: boolean } = {},
): CommentThread {
  return {
    root: {
      id,
      assetId: 'a1',
      versionId: over.versionId ?? 'v2',
      versionNo: 2,
      parentId: null,
      authorId: 'u1',
      authorName: 'Priya',
      body: over.body ?? `Comment ${id}`,
      mentions: [],
      anchor: anchor ? { x: null, y: null, w: null, h: null, page: null, startMs: null, endMs: null, ...anchor } : null,
      resolved: over.resolved ?? false,
      resolvedBy: null,
      resolvedByName: null,
      resolvedAt: null,
      edited: false,
      editedAt: null,
      deleted: over.deleted ?? false,
      createdAt: '2026-01-01T00:00:00Z',
    },
    replies: [],
  } as unknown as CommentThread;
}
export const region = (
  id: string,
  x: number,
  y: number,
  w: number,
  h: number,
  over: Parameters<typeof thread>[2] & { page?: number } = {},
) => thread(id, { type: 'region', x, y, w, h, page: over.page ?? null }, over);
export const pin = (id: string, x: number, y: number, over: Parameters<typeof thread>[2] & { page?: number } = {}) =>
  thread(id, { type: 'point', x, y, page: over.page ?? null }, over);
export const moment = (id: string, startMs: number, endMs: number | null = null, over: Parameters<typeof thread>[2] = {}) =>
  thread(id, { type: 'timecode', startMs, endMs }, over);

export interface StageOptions {
  type?: string;
  status?: string;
  threads?: CommentThread[];
  permissions?: string[];
  pages?: number;
  versionId?: string | null;
  /** Make GET /me fail, as when permissions cannot be loaded. */
  failMe?: boolean;
}

const signedIn: AuthState = {
  status: 'authenticated',
  name: 'Dev',
  email: 'dev@dam.local',
  accessToken: 'tok',
  signIn: vi.fn(async () => {}),
  signOut: vi.fn(async () => {}),
};

export const requests: string[] = [];

/** Renders the asset preview for asset `a1` under a real comments provider with a fake API behind it. */
export function renderStage(opts: StageOptions = {}) {
  const {
    type = 'image',
    status = 'published',
    threads = [],
    permissions = ['comments.write'],
    pages = 3,
    failMe = false,
  } = opts;
  requests.length = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      if (!(input instanceof Request)) throw new Error('unexpected raw fetch');
      const url = new URL(input.url);
      requests.push(`${input.method} ${url.pathname}${url.search}`);
      if (url.pathname === '/api/v1/me')
        return failMe
          ? json({ title: 'no' }, 500)
          : json({
              userId: 'u9',
              permissions,
              roles: [],
              groups: [],
              attributes: {},
              tenantId: 't',
              accessRuleCount: 0,
              inactiveRoles: [],
            });
      if (url.pathname === '/api/v1/assets/a1/comments')
        return json({ threads, counts: { open: threads.length, resolved: 0, annotations: threads.length } });
      if (url.pathname === '/api/v1/assets/a1/renditions')
        return json(
          Array.from({ length: pages }, (_, i) => ({ profile: 'pdf_pages', pageNo: i + 1, mime: 'image/webp', status: 'ready' })),
        );
      if (url.pathname === '/api/v1/assets/a1/links') {
        const body = (await input.json()) as { renditions: string[] };
        const links: Record<string, string> = {};
        for (const r of body.renditions) links[r] = `https://cdn.test/a1-${r}.jpg`;
        return json({ expiresAt: '2030-01-01T00:00:00Z', watermarked: status !== 'published', links });
      }
      return json({ title: 'not found' }, 404);
    }),
  );
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const ui = render(
    <AuthContext.Provider value={signedIn}>
      <QueryClientProvider client={client}>
        <ToastProvider>
          <MemoryRouter>
            <CommentsProvider assetId="a1" assetType={type} versionId={opts.versionId === undefined ? 'v2' : opts.versionId}>
              <Probe />
              <AssetPreview assetId="a1" type={type} title="Hero" status={status} />
            </CommentsProvider>
          </MemoryRouter>
        </ToastProvider>
      </QueryClientProvider>
    </AuthContext.Provider>,
  );
  return { ...ui, client };
}

export function resetHarness() {
  cleanup();
  vi.unstubAllGlobals();
  probe.ui = null;
  probe.focus.mockClear();
  try {
    localStorage.clear();
  } catch {
    /* ignore */
  }
}

/** jsdom lays nothing out: give images a box (viewport rect and offset box) so overlay and pointer maths have numbers. */
export function mockImageBox(box: {
  left: number;
  top: number;
  width: number;
  height: number;
  offsetLeft?: number;
  offsetTop?: number;
}) {
  const restore: Array<() => void> = [];
  const define = (proto: object, prop: string, get: (this: HTMLElement) => unknown) => {
    const prev = Object.getOwnPropertyDescriptor(proto, prop);
    Object.defineProperty(proto, prop, { configurable: true, get });
    restore.push(() => (prev ? Object.defineProperty(proto, prop, prev) : delete (proto as Record<string, unknown>)[prop]));
  };
  const isMedia = (el: HTMLElement) => el instanceof HTMLImageElement || el instanceof HTMLVideoElement;
  define(HTMLElement.prototype, 'offsetWidth', function () {
    return isMedia(this) ? box.width : 0;
  });
  define(HTMLElement.prototype, 'offsetHeight', function () {
    return isMedia(this) ? box.height : 0;
  });
  define(HTMLElement.prototype, 'offsetLeft', function () {
    return isMedia(this) ? (box.offsetLeft ?? 0) : 0;
  });
  define(HTMLElement.prototype, 'offsetTop', function () {
    return isMedia(this) ? (box.offsetTop ?? 0) : 0;
  });
  const rect = Element.prototype.getBoundingClientRect;
  Element.prototype.getBoundingClientRect = function (this: Element) {
    if (this instanceof HTMLImageElement || this instanceof HTMLVideoElement)
      return {
        ...box,
        x: box.left,
        y: box.top,
        right: box.left + box.width,
        bottom: box.top + box.height,
        toJSON: () => ({}),
      } as DOMRect;
    return rect.call(this);
  };
  restore.push(() => (Element.prototype.getBoundingClientRect = rect));
  return () => restore.reverse().forEach((r) => r());
}
