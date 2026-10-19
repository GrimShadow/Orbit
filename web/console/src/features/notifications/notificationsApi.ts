import { unwrap, type components } from '@dam/sdk-js';
import {
  useInfiniteQuery,
  useMutation,
  useQuery,
  useQueryClient,
  type InfiniteData,
  type QueryClient,
} from '@tanstack/react-query';
import { useApiClient } from '../../api/apiClient';
import { useAuth } from '../../auth/AuthProvider';

export type AppNotification = components['schemas']['NotificationDto'];
export type NotificationPage = components['schemas']['PageOfNotificationDto'];
export type NotificationPreferences = components['schemas']['NotificationPreferencesDto'];
export type NotificationCategory = components['schemas']['NotificationCategoryDto'];
export type PreferenceInput = components['schemas']['NotificationPreferenceInput'];

/** Seconds between unread-count polls. React Query pauses interval refetching while the tab is in the background. */
export const COUNT_POLL_MS = 30_000;

const COUNT_KEY = ['notifications', 'unread-count'] as const;
const LISTS_KEY = ['notifications', 'list'] as const;
const PREFS_KEY = ['notifications', 'preferences'] as const;

/** How many unread notifications I have - drives the bell. Polled every 30 s. */
export function useUnreadCount() {
  const api = useApiClient();
  const { status } = useAuth();
  return useQuery({
    queryKey: COUNT_KEY,
    queryFn: async () => unwrap(await api.GET('/api/v1/notifications/unread-count')).count,
    enabled: status === 'authenticated',
    refetchInterval: COUNT_POLL_MS,
    staleTime: 15_000,
  });
}

/** The newest few, for the bell's panel. Only fetched while the panel is open. */
export function useLatestNotifications(limit: number, enabled: boolean) {
  const api = useApiClient();
  return useQuery({
    queryKey: [...LISTS_KEY, 'latest', limit],
    queryFn: async () => unwrap(await api.GET('/api/v1/notifications', { params: { query: { limit } } })),
    enabled,
    staleTime: 0,
  });
}

/** The inbox page: newest first, optionally unread only, paged with a cursor. */
export function useNotifications(unreadOnly: boolean, limit = 25) {
  const api = useApiClient();
  const { status } = useAuth();
  return useInfiniteQuery({
    queryKey: [...LISTS_KEY, 'page', { unreadOnly, limit }],
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam }) =>
      unwrap(
        await api.GET('/api/v1/notifications', {
          params: { query: { unread: unreadOnly ? true : undefined, limit, cursor: pageParam } },
        }),
      ),
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    enabled: status === 'authenticated',
    staleTime: 0,
  });
}

type ListData = NotificationPage | InfiniteData<NotificationPage>;

/** Applies `fn` to every notification in every cached list (the panel's and the page's). */
function patchLists(qc: QueryClient, fn: (n: AppNotification) => AppNotification) {
  const patchPage = (p: NotificationPage): NotificationPage => ({ ...p, items: p.items.map(fn) });
  qc.setQueriesData<ListData>({ queryKey: LISTS_KEY }, (data) => {
    if (!data) return data;
    return 'pages' in data ? { ...data, pages: data.pages.map(patchPage) } : patchPage(data);
  });
}

interface Snapshot {
  count: number | undefined;
  lists: [readonly unknown[], ListData | undefined][];
}

async function optimistic(qc: QueryClient, patch: () => void): Promise<Snapshot> {
  await Promise.all([qc.cancelQueries({ queryKey: COUNT_KEY }), qc.cancelQueries({ queryKey: LISTS_KEY })]);
  const snapshot: Snapshot = {
    count: qc.getQueryData<number>(COUNT_KEY),
    lists: qc.getQueriesData<ListData>({ queryKey: LISTS_KEY }),
  };
  patch();
  return snapshot;
}

function rollback(qc: QueryClient, snapshot: Snapshot | undefined) {
  if (!snapshot) return;
  qc.setQueryData(COUNT_KEY, snapshot.count);
  for (const [key, data] of snapshot.lists) qc.setQueryData(key, data);
}

/** Marks one notification read. The badge and the lists update straight away and are put back if the call fails. */
export function useMarkRead() {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (n: Pick<AppNotification, 'id' | 'read'>) =>
      unwrap(await api.POST('/api/v1/notifications/{id}/read', { params: { path: { id: n.id } } })),
    onMutate: (n) =>
      optimistic(qc, () => {
        if (n.read) return;
        qc.setQueryData<number>(COUNT_KEY, (c) => (c === undefined ? c : Math.max(0, c - 1)));
        patchLists(qc, (x) => (x.id === n.id ? { ...x, read: true, readAt: x.readAt ?? new Date().toISOString() } : x));
      }),
    onError: (_e, _n, snapshot) => rollback(qc, snapshot),
    onSettled: () => {
      void qc.invalidateQueries({ queryKey: COUNT_KEY });
      void qc.invalidateQueries({ queryKey: LISTS_KEY });
    },
  });
}

export function useMarkAllRead() {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async () => unwrap(await api.POST('/api/v1/notifications/read-all')),
    onMutate: () =>
      optimistic(qc, () => {
        qc.setQueryData<number>(COUNT_KEY, (c) => (c === undefined ? c : 0));
        const now = new Date().toISOString();
        patchLists(qc, (x) => (x.read ? x : { ...x, read: true, readAt: now }));
      }),
    onError: (_e, _v, snapshot) => rollback(qc, snapshot),
    onSettled: () => {
      void qc.invalidateQueries({ queryKey: COUNT_KEY });
      void qc.invalidateQueries({ queryKey: LISTS_KEY });
    },
  });
}

export function usePreferences() {
  const api = useApiClient();
  const { status } = useAuth();
  return useQuery({
    queryKey: PREFS_KEY,
    queryFn: async () => unwrap(await api.GET('/api/v1/notifications/preferences')),
    enabled: status === 'authenticated',
  });
}

export function useUpdatePreferences() {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (preferences: PreferenceInput[]) =>
      unwrap(await api.PUT('/api/v1/notifications/preferences', { body: { preferences } })),
    onSuccess: (saved) => qc.setQueryData(PREFS_KEY, saved),
  });
}

/** A console path from the server is safe to navigate to only if it is app-relative (`/tasks`, never `//host` or a URL). */
export function internalPath(linkUrl: string | null | undefined): string | null {
  return linkUrl && linkUrl.startsWith('/') && !linkUrl.startsWith('//') && !linkUrl.includes('\\') ? linkUrl : null;
}
