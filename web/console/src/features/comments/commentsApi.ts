import { ApiError, unwrap, type components } from '@dam/sdk-js';
import { keepPreviousData, useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useApiClient } from '../../api/apiClient';
import { useAuth } from '../../auth/AuthProvider';
import { applyDelete, applyEdit, applyResolved } from './optimistic';

export type CommentDto = components['schemas']['CommentDto'];
export type CommentThread = components['schemas']['CommentThreadDto'];
export type CommentThreads = components['schemas']['CommentThreadsDto'];
export type CommentCounts = components['schemas']['CommentCountsDto'];
export type ActivityItem = components['schemas']['ActivityItemDto'];
export type MentionableUser = components['schemas']['MentionableUserDto'];
export type AnchorDto = components['schemas']['AnchorDto'];

export type ThreadState = 'open' | 'resolved' | 'all';
export type ThreadKind = 'all' | 'annotation' | 'comment';
export interface ThreadFilter {
  /** Omit for every version. */
  versionId?: string;
  state?: ThreadState;
  kind?: ThreadKind;
}

/** What a stage hands the composer: a normalized region/pin (0..1 of the rendered image or page) or a timecode. */
export type NewAnchor =
  | { type: 'region'; x: number; y: number; w: number; h: number; page?: number }
  | { type: 'point'; x: number; y: number; page?: number }
  | { type: 'timecode'; startMs: number; endMs?: number | null };

/** The anchor in the shape the API takes: every field present, unused ones null. */
export function toAnchorInput(a: NewAnchor): AnchorDto {
  return {
    type: a.type,
    x: 'x' in a ? a.x : null,
    y: 'y' in a ? a.y : null,
    w: 'w' in a ? a.w : null,
    h: 'h' in a ? a.h : null,
    page: 'page' in a ? (a.page ?? null) : null,
    startMs: 'startMs' in a ? a.startMs : null,
    endMs: 'endMs' in a ? (a.endMs ?? null) : null,
  } as AnchorDto;
}

/** An ApiError that also carries the server's per-field 422 messages (`errors: { "body": ["..."] }`), which the shared
 * `unwrap` drops. */
export class CommentApiError extends ApiError {
  constructor(
    status: number,
    title: string,
    detail: string | undefined,
    correlationId: string | undefined,
    public fields: Record<string, string[]>,
  ) {
    super(status, title, detail, correlationId);
  }
}

interface Problem {
  title?: string;
  detail?: string;
  correlationId?: string;
  errors?: Record<string, string[]>;
}

function unwrapFields<T>({ data, error, response }: { data?: T; error?: unknown; response: Response }): T {
  if (response.ok) return data as T;
  const p = (error ?? {}) as Problem;
  throw new CommentApiError(
    response.status,
    p.title ?? response.statusText,
    p.detail,
    p.correlationId ?? response.headers.get('X-Correlation-ID') ?? undefined,
    p.errors && typeof p.errors === 'object' ? p.errors : {},
  );
}

/** What to show a person for a failed write: the 422 field messages (body first), else the server's detail or title. */
export function commentErrorMessage(e: unknown): string {
  if (e instanceof CommentApiError) {
    const keys = Object.keys(e.fields);
    if (keys.length > 0) {
      const ordered = [...keys.filter((k) => k === 'body'), ...keys.filter((k) => k !== 'body')];
      return ordered
        .map((k) => (e.fields[k] ?? []).join(' '))
        .filter(Boolean)
        .join(' ');
    }
  }
  return e instanceof ApiError ? e.detail || e.title : e instanceof Error ? e.message : String(e);
}

const invalidate = (qc: ReturnType<typeof useQueryClient>, assetId: string) => {
  void qc.invalidateQueries({ queryKey: ['comments', assetId] });
  void qc.invalidateQueries({ queryKey: ['activity', assetId] });
};

/** Threads for an asset (oldest first), with open/resolved/annotation counts that ignore the state and kind filters. */
export function useThreads(assetId: string, filter: ThreadFilter = {}, enabled = true) {
  const api = useApiClient();
  const { status } = useAuth();
  const { versionId, state = 'all', kind = 'all' } = filter;
  return useQuery({
    queryKey: ['comments', assetId, versionId ?? 'all', state, kind],
    queryFn: async () =>
      unwrap(
        await api.GET('/api/v1/assets/{id}/comments', { params: { path: { id: assetId }, query: { versionId, state, kind } } }),
      ),
    enabled: enabled && status === 'authenticated',
    staleTime: 15_000,
  });
}

export function useAddComment(assetId: string) {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (v: { body: string; versionId?: string | null; parentId?: string | null; anchor?: NewAnchor | null }) =>
      unwrapFields(
        await api.POST('/api/v1/assets/{id}/comments', {
          params: { path: { id: assetId } },
          body: {
            body: v.body,
            versionId: v.versionId ?? null,
            parentId: v.parentId ?? null,
            anchor: v.anchor ? toAnchorInput(v.anchor) : null,
          } as never,
        }),
      ),
    onSuccess: () => invalidate(qc, assetId),
  });
}

type Snapshot = [readonly unknown[], CommentThreads | undefined][];

/** Applies `fn` to every cached thread list of the asset (each filter combination is its own cache entry) and hands back
 * what was there, so a failed write can put it back. */
function patchCached(
  qc: ReturnType<typeof useQueryClient>,
  assetId: string,
  fn: (data: CommentThreads, state: ThreadState, kind: ThreadKind) => CommentThreads,
): Snapshot {
  const snapshot = qc.getQueriesData<CommentThreads>({ queryKey: ['comments', assetId] });
  for (const [key, data] of snapshot) if (data) qc.setQueryData(key, fn(data, key[3] as ThreadState, key[4] as ThreadKind));
  return snapshot;
}
const restore = (qc: ReturnType<typeof useQueryClient>, snapshot?: Snapshot) =>
  snapshot?.forEach(([key, data]) => qc.setQueryData(key, data));

export interface UpdateVars {
  id: string;
  body?: string;
  resolved?: boolean;
  /** Who is resolving (shown at once as "Resolved by ..."); not sent to the server. */
  actor?: { id: string; name: string };
}

export function useUpdateComment(assetId: string) {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (v: UpdateVars) =>
      unwrapFields(
        await api.PATCH('/api/v1/comments/{id}', {
          params: { path: { id: v.id } },
          body: { body: v.body ?? null, resolved: v.resolved ?? null } as never,
        }),
      ),
    // The card changes at once; the server's answer (and the refetch below) has the final say.
    onMutate: async (v) => {
      await qc.cancelQueries({ queryKey: ['comments', assetId] });
      return {
        snapshot: patchCached(qc, assetId, (data, state) => {
          let out = data;
          if (v.resolved !== undefined) out = applyResolved(out, state, v.id, v.resolved, v.actor);
          if (v.body !== undefined) out = applyEdit(out, v.id, v.body);
          return out;
        }),
      };
    },
    onError: (_e, _v, ctx) => restore(qc, ctx?.snapshot),
    onSettled: () => invalidate(qc, assetId),
  });
}

export function useDeleteComment(assetId: string) {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => unwrapFields(await api.DELETE('/api/v1/comments/{id}', { params: { path: { id } } })),
    onMutate: async (id) => {
      await qc.cancelQueries({ queryKey: ['comments', assetId] });
      return { snapshot: patchCached(qc, assetId, (data) => applyDelete(data, id)) };
    },
    onError: (_e, _id, ctx) => restore(qc, ctx?.snapshot),
    onSettled: () => invalidate(qc, assetId),
  });
}

export function useActivity(assetId: string, enabled = true) {
  const api = useApiClient();
  const { status } = useAuth();
  return useInfiniteQuery({
    queryKey: ['activity', assetId],
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam }) =>
      unwrap(
        await api.GET('/api/v1/assets/{id}/activity', {
          params: { path: { id: assetId }, query: { limit: 30, cursor: pageParam } },
        }),
      ),
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    enabled: enabled && status === 'authenticated',
    staleTime: 15_000,
  });
}

/** People who can read this asset, for @mention autocomplete (needs at least a letter; the server returns up to 10). */
export function useMentionable(assetId: string, q: string) {
  const api = useApiClient();
  const { status } = useAuth();
  return useQuery({
    queryKey: ['mentionable', assetId, q],
    queryFn: async () =>
      unwrap(await api.GET('/api/v1/assets/{id}/mentionable', { params: { path: { id: assetId }, query: { q } } })),
    enabled: status === 'authenticated' && q.length > 0,
    staleTime: 60_000,
    placeholderData: keepPreviousData, // the list stays while the next letters load, so the picker does not flicker
  });
}

/** `@[Display Name](user:<guid>)` tokens as stored; the renderer turns them into highlighted @Name. */
export const MENTION_TOKEN = /@\[([^\]]+)\]\(user:([0-9a-fA-F-]{36})\)/g;
export const mentionToken = (name: string, userId: string) => `@[${name}](user:${userId})`;

/** 83500 -> "1:23", 3725000 -> "1:02:05" */
export function formatTimecode(ms: number): string {
  const total = Math.max(0, Math.floor(ms / 1000));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  const two = (n: number) => String(n).padStart(2, '0');
  return h > 0 ? `${h}:${two(m)}:${two(s)}` : `${m}:${two(s)}`;
}
