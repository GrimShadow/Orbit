import { ApiError, unwrap } from '@dam/sdk-js';
import { useInfiniteQuery, useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query';
import { useApiClient } from '../../api/apiClient';
import { useAuth } from '../../auth/AuthProvider';
import type { ActivityPage, ConnectionDto, ConnectionInput, ConnectorDto, TestResultDto } from './integrationsTypes';

// The generated schema types statuses and strategies as plain strings and `config` as open objects; each response is cast to the
// narrower types in integrationsTypes.ts.
const KEY = 'integrations';

export const isForbidden = (e: unknown) => e instanceof ApiError && e.status === 403;
export const isRateLimited = (e: unknown) => e instanceof ApiError && e.status === 429;

/** What a person should read for an error: the server's detail when it gave one, else its title. */
export const errorText = (e: unknown) => (e instanceof ApiError ? e.detail || e.title : undefined);

/** The 422 problem's per-field messages (`errors` in the body), keyed `config.<field>` / `secrets.<field>` / `name`. */
export function serverFieldErrors(e: unknown): Record<string, string[]> {
  if (!(e instanceof ApiError) || e.status !== 422) return {};
  const raw = e.problem?.errors;
  if (!raw || typeof raw !== 'object') return {};
  const out: Record<string, string[]> = {};
  for (const [k, v] of Object.entries(raw as Record<string, unknown>)) {
    const list = Array.isArray(v) ? v.map(String) : typeof v === 'string' ? [v] : [];
    if (list.length > 0) out[k] = list;
  }
  return out;
}

function useEnabled(enabled: boolean) {
  const { status } = useAuth();
  return enabled && status === 'authenticated';
}

/** The generated request type wants every key present: `connector` is only used when creating, so it is null otherwise. */
const wire = (b: ConnectionInput) => ({ ...b, connector: b.connector ?? null });

const refresh = (qc: QueryClient) => qc.invalidateQueries({ queryKey: [KEY] });

/** The connector catalog: what can be connected, and the fields each one asks for. */
export function useConnectors(enabled = true) {
  const api = useApiClient();
  const on = useEnabled(enabled);
  return useQuery({
    queryKey: [KEY, 'connectors'],
    queryFn: async () => unwrap(await api.GET('/api/v1/integrations/connectors')) as ConnectorDto[],
    enabled: on,
    staleTime: 300_000,
  });
}

export function useConnections(enabled = true) {
  const api = useApiClient();
  const on = useEnabled(enabled);
  return useQuery({
    queryKey: [KEY, 'connections'],
    queryFn: async () => unwrap(await api.GET('/api/v1/integrations/connections')) as ConnectionDto[],
    enabled: on,
  });
}

/** Creates (no `id`) or updates a connection. A secret left out of `body.secrets` is kept by the server. */
export function useSaveConnection() {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (v: { id?: string; body: ConnectionInput }) =>
      (v.id
        ? unwrap(await api.PUT('/api/v1/integrations/connections/{id}', { params: { path: { id: v.id } }, body: wire(v.body) }))
        : unwrap(await api.POST('/api/v1/integrations/connections', { body: wire(v.body) }))) as ConnectionDto,
    onSuccess: () => refresh(qc),
  });
}

/** The enabled switch: a PUT of the stored values with `enabled` flipped (no secrets sent, so all are kept). The list updates
 * at once and rolls back on failure. */
export function useToggleConnection() {
  const api = useApiClient();
  const qc = useQueryClient();
  const listKey = [KEY, 'connections'];
  return useMutation({
    mutationFn: async (v: { connection: ConnectionDto; enabled: boolean }) => {
      const c = v.connection;
      const config: ConnectionInput['config'] = {};
      for (const [k, val] of Object.entries(c.config)) if (val !== null && val !== undefined) config[k] = val;
      const body: ConnectionInput = { name: c.name, enabled: v.enabled, config, secrets: {}, clearSecrets: [] };
      return unwrap(
        await api.PUT('/api/v1/integrations/connections/{id}', { params: { path: { id: c.id } }, body: wire(body) }),
      ) as ConnectionDto;
    },
    onMutate: async (v) => {
      await qc.cancelQueries({ queryKey: listKey });
      const before = qc.getQueryData<ConnectionDto[]>(listKey);
      qc.setQueryData<ConnectionDto[]>(listKey, (old) =>
        old?.map((c) => (c.id === v.connection.id ? { ...c, enabled: v.enabled } : c)),
      );
      return { before };
    },
    onError: (_e, _v, ctx) => {
      if (ctx?.before) qc.setQueryData(listKey, ctx.before);
    },
    onSettled: () => refresh(qc),
  });
}

export function useDeleteConnection() {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) =>
      unwrap(await api.DELETE('/api/v1/integrations/connections/{id}', { params: { path: { id } } })),
    onSuccess: () => refresh(qc),
  });
}

/** Runs the connector's test; the connection's status, detail and last-tested time change, so the list is refreshed. The
 * server allows one test per connection every 5 seconds and answers 429 otherwise. */
export function useTestConnection() {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) =>
      unwrap(await api.POST('/api/v1/integrations/connections/{id}/test', { params: { path: { id } } })) as TestResultDto,
    onSettled: () => refresh(qc),
  });
}

export const ACTIVITY_PAGE = 25;

/** A connection's recent calls, newest first; pages accumulate with the cursor. */
export function useActivity(connectionId: string | null) {
  const api = useApiClient();
  const on = useEnabled(connectionId !== null);
  return useInfiniteQuery({
    queryKey: [KEY, 'activity', connectionId],
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam }) =>
      unwrap(
        await api.GET('/api/v1/integrations/connections/{id}/activity', {
          params: { path: { id: connectionId! }, query: { limit: ACTIVITY_PAGE, cursor: pageParam } },
        }),
      ) as ActivityPage,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    enabled: on,
    retry: false,
  });
}
