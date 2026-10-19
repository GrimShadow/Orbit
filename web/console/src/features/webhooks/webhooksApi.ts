import { ApiError, unwrap, type components } from '@dam/sdk-js';
import { useInfiniteQuery, useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query';
import { useApiClient } from '../../api/apiClient';
import { useAuth } from '../../auth/AuthProvider';

export type WebhookDto = components['schemas']['WebhookDto'];
export type WebhookInput = components['schemas']['WebhookInput'];
export type WebhookEventType = components['schemas']['WebhookEventTypeDto'];
export type DeliveryDto = components['schemas']['WebhookDeliveryDto'];
export type DeliveryDetail = components['schemas']['WebhookDeliveryDetailDto'];
export type ChannelDto = components['schemas']['NotificationChannelDto'];
export type ChannelInput = components['schemas']['NotificationChannelInput'];
export type ChannelTestResult = components['schemas']['ChannelTestResultDto'];

export const isForbidden = (e: unknown) => e instanceof ApiError && e.status === 403;

/** An API error that also carries the server's per-field validation messages (`errors` in the 422 problem body), which the
 * shared `unwrap` does not keep. */
export class FieldApiError extends ApiError {
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

/** Like `unwrap`, but keeps a 422's field messages. */
function unwrapFields<T>({ data, error, response }: { data?: T; error?: unknown; response: Response }): T {
  if (response.ok) return data as T;
  const p = (error ?? {}) as Problem;
  throw new FieldApiError(
    response.status,
    p.title ?? response.statusText,
    p.detail,
    p.correlationId ?? response.headers.get('X-Correlation-ID') ?? undefined,
    p.errors && typeof p.errors === 'object' ? p.errors : {},
  );
}

export type FormField = 'name' | 'url' | 'events' | 'kind';

/** Maps the server's validation keys (`Name`, `Url`, `Events[0]`) to the form's fields; the rest is returned as `other`. */
export function fieldErrors(e: unknown): { byField: Partial<Record<FormField, string>>; other?: string } {
  if (!(e instanceof ApiError)) return { byField: {} };
  const byField: Partial<Record<FormField, string>> = {};
  const rest: string[] = [];
  if (e instanceof FieldApiError) {
    for (const [key, messages] of Object.entries(e.fields)) {
      const field = key.replace(/\[\d+\]$/, '').toLowerCase();
      const text = messages.join(' ');
      if (field === 'name' || field === 'url' || field === 'events' || field === 'kind') {
        byField[field] = byField[field] ? `${byField[field]} ${text}` : text;
      } else rest.push(text);
    }
  }
  if (Object.keys(byField).length === 0 && rest.length === 0) return { byField, other: e.detail || e.title };
  return { byField, other: rest.join(' ') || undefined };
}

/** What a person should read for an error: the server's detail when it gave one, else its title. */
export const errorText = (e: unknown) => (e instanceof ApiError ? e.detail || e.title : undefined);

const KEY = 'webhooks';
const CHANNELS = 'notification-channels';

function useEnabled(enabled: boolean) {
  const { status } = useAuth();
  return enabled && status === 'authenticated';
}

/** The public event catalogue (names, categories, which are derived). Either permission may read it. */
export function useWebhookEvents(enabled = true) {
  const api = useApiClient();
  const on = useEnabled(enabled);
  return useQuery({
    queryKey: [KEY, 'events'],
    queryFn: async () => unwrap(await api.GET('/api/v1/webhooks/events')),
    enabled: on,
    staleTime: 300_000,
    retry: false,
  });
}

/** Every webhook with its 24-hour stats; refreshed every 15 s so the badges stay roughly current. */
export function useWebhooks(enabled = true) {
  const api = useApiClient();
  const on = useEnabled(enabled);
  return useQuery({
    queryKey: [KEY, 'list'],
    queryFn: async () => unwrap(await api.GET('/api/v1/webhooks')),
    enabled: on,
    refetchInterval: 15_000,
  });
}

const refresh = (qc: QueryClient) => qc.invalidateQueries({ queryKey: [KEY] });

export function useCreateWebhook() {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (body: WebhookInput) => unwrapFields(await api.POST('/api/v1/webhooks', { body })),
    onSuccess: () => refresh(qc),
  });
}

export function useUpdateWebhook() {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (v: { id: string; body: WebhookInput }) =>
      unwrapFields(await api.PUT('/api/v1/webhooks/{id}', { params: { path: { id: v.id } }, body: v.body })),
    onSuccess: () => refresh(qc),
  });
}

/** The active switch: a PUT of the stored values with `active` flipped. The list updates at once and rolls back on failure. */
export function useToggleWebhook() {
  const api = useApiClient();
  const qc = useQueryClient();
  const listKey = [KEY, 'list'];
  return useMutation({
    mutationFn: async (v: { webhook: WebhookDto; active: boolean }) => {
      const { name, url, events } = v.webhook;
      return unwrap(
        await api.PUT('/api/v1/webhooks/{id}', {
          params: { path: { id: v.webhook.id } },
          body: { name, url, events, active: v.active },
        }),
      );
    },
    onMutate: async (v) => {
      await qc.cancelQueries({ queryKey: listKey });
      const before = qc.getQueryData<WebhookDto[]>(listKey);
      qc.setQueryData<WebhookDto[]>(listKey, (old) => old?.map((w) => (w.id === v.webhook.id ? { ...w, active: v.active } : w)));
      return { before };
    },
    onError: (_e, _v, ctx) => {
      if (ctx?.before) qc.setQueryData(listKey, ctx.before);
    },
    onSettled: () => refresh(qc),
  });
}

export function useDeleteWebhook() {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => unwrap(await api.DELETE('/api/v1/webhooks/{id}', { params: { path: { id } } })),
    onSuccess: () => refresh(qc),
  });
}

export function useRotateSecret() {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => unwrap(await api.POST('/api/v1/webhooks/{id}/rotate-secret', { params: { path: { id } } })),
    onSuccess: () => refresh(qc),
  });
}

export function usePingWebhook() {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => unwrap(await api.POST('/api/v1/webhooks/{id}/ping', { params: { path: { id } } })),
    onSuccess: () => refresh(qc),
  });
}

export interface DeliveryFilter {
  webhookId?: string;
  status?: string;
  /** An exact event name or a `category.*` prefix. */
  eventType?: string;
  /** ISO instants. */
  from?: string;
  to?: string;
}

export const POLL_MS = 10_000;
const hasPending = (items: DeliveryDto[] | undefined) => !!items?.some((d) => d.status === 'pending');

/** Deliveries, newest first; pages accumulate. Polls every 10 s while any loaded delivery is still pending. */
export function useDeliveries(filter: DeliveryFilter, enabled = true, limit = 25) {
  const api = useApiClient();
  const on = useEnabled(enabled);
  return useInfiniteQuery({
    queryKey: [KEY, 'deliveries', filter, limit],
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam }) =>
      unwrap(await api.GET('/api/v1/webhook-deliveries', { params: { query: { ...filter, limit, cursor: pageParam } } })),
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    enabled: on,
    refetchInterval: (q) => (hasPending(q.state.data?.pages.flatMap((p) => p.items)) ? POLL_MS : false),
  });
}

/** One delivery with its payload and response excerpt; polls while it is pending. */
export function useDelivery(id: string | null) {
  const api = useApiClient();
  const on = useEnabled(id !== null);
  return useQuery({
    queryKey: [KEY, 'delivery', id],
    queryFn: async () => unwrap(await api.GET('/api/v1/webhook-deliveries/{id}', { params: { path: { id: id! } } })),
    enabled: on,
    retry: false,
    refetchInterval: (q) => (q.state.data?.status === 'pending' ? POLL_MS : false),
  });
}

export function useReplayDelivery() {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) =>
      unwrap(await api.POST('/api/v1/webhook-deliveries/{id}/replay', { params: { path: { id } } })),
    onSuccess: () => refresh(qc),
  });
}

/* ---- chat channels ---- */

export function useChannels(enabled = true) {
  const api = useApiClient();
  const on = useEnabled(enabled);
  return useQuery({
    queryKey: [CHANNELS, 'list'],
    queryFn: async () => unwrap(await api.GET('/api/v1/notification-channels')),
    enabled: on,
  });
}

const refreshChannels = (qc: QueryClient) => qc.invalidateQueries({ queryKey: [CHANNELS] });

export function useSaveChannel() {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (v: { id?: string; body: ChannelInput }) =>
      v.id
        ? unwrapFields(await api.PUT('/api/v1/notification-channels/{id}', { params: { path: { id: v.id } }, body: v.body }))
        : unwrapFields(await api.POST('/api/v1/notification-channels', { body: v.body })),
    onSuccess: () => refreshChannels(qc),
  });
}

/** The active switch on a channel: a PUT with `url: null`, which keeps the stored URL. */
export function useToggleChannel() {
  const api = useApiClient();
  const qc = useQueryClient();
  const listKey = [CHANNELS, 'list'];
  return useMutation({
    mutationFn: async (v: { channel: ChannelDto; active: boolean }) => {
      const { kind, name, events } = v.channel;
      return unwrap(
        await api.PUT('/api/v1/notification-channels/{id}', {
          params: { path: { id: v.channel.id } },
          body: { kind, name, url: null, events, active: v.active },
        }),
      );
    },
    onMutate: async (v) => {
      await qc.cancelQueries({ queryKey: listKey });
      const before = qc.getQueryData<ChannelDto[]>(listKey);
      qc.setQueryData<ChannelDto[]>(listKey, (old) => old?.map((c) => (c.id === v.channel.id ? { ...c, active: v.active } : c)));
      return { before };
    },
    onError: (_e, _v, ctx) => {
      if (ctx?.before) qc.setQueryData(listKey, ctx.before);
    },
    onSettled: () => refreshChannels(qc),
  });
}

export function useDeleteChannel() {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) =>
      unwrap(await api.DELETE('/api/v1/notification-channels/{id}', { params: { path: { id } } })),
    onSuccess: () => refreshChannels(qc),
  });
}

export function useTestChannel() {
  const api = useApiClient();
  return useMutation({
    mutationFn: async (id: string) =>
      unwrap(await api.POST('/api/v1/notification-channels/{id}/test', { params: { path: { id } } })),
  });
}
