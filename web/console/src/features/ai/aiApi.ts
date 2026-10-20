import { ApiError, unwrap, type components } from '@dam/sdk-js';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useApiClient } from '../../api/apiClient';
import { useAuth } from '../../auth/AuthProvider';

export type AssetAi = components['schemas']['AssetAiDto'];
export type AiSuggestion = components['schemas']['AiSuggestionDto'];
export type AiRun = components['schemas']['AiRunDto'];
export type AiTranscript = NonNullable<components['schemas']['AiTranscriptDto']>;
export type AiSettings = components['schemas']['AiSettingsDto'];
export type AiStatus = components['schemas']['AiStatusDto'];
export type AiCapabilities = components['schemas']['AiCapabilitiesDto'];

export type AiSuggestionKind = 'tag' | 'language';
/** The AI state of an asset's current version: none = never analysed, disabled = AI is off for the tenant. */
export type AiAssetStatus = 'none' | 'pending' | 'running' | 'ready' | 'failed' | 'disabled';

const invalidateAsset = (qc: ReturnType<typeof useQueryClient>, assetId: string) => {
  void qc.invalidateQueries({ queryKey: ['ai', 'asset', assetId] });
  void qc.invalidateQueries({ queryKey: ['asset', assetId] });
  void qc.invalidateQueries({ queryKey: ['assets'] });
  void qc.invalidateQueries({ queryKey: ['search'] });
};

/** What the AI found for an asset's current version. Polls while a run is pending or running so the panel fills in by itself. */
export function useAssetAi(assetId: string, enabled = true) {
  const api = useApiClient();
  const { status } = useAuth();
  return useQuery({
    queryKey: ['ai', 'asset', assetId],
    queryFn: async () => unwrap(await api.GET('/api/v1/assets/{id}/ai', { params: { path: { id: assetId } } })),
    enabled: enabled && status === 'authenticated',
    staleTime: 10_000,
    refetchInterval: (q) => (q.state.data?.status === 'pending' || q.state.data?.status === 'running' ? 3_000 : false),
  });
}

export function useEnrichAsset(assetId: string) {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async () => unwrap(await api.POST('/api/v1/assets/{id}/ai/enrich', { params: { path: { id: assetId } } })),
    onSuccess: () => invalidateAsset(qc, assetId),
  });
}

export function useDecideSuggestion(assetId: string) {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (v: { suggestionId: string; decision: 'accept' | 'reject' }) =>
      v.decision === 'accept'
        ? unwrap(
            await api.POST('/api/v1/assets/{id}/ai/suggestions/{suggestionId}/accept', {
              params: { path: { id: assetId, suggestionId: v.suggestionId } },
            }),
          )
        : unwrap(
            await api.POST('/api/v1/assets/{id}/ai/suggestions/{suggestionId}/reject', {
              params: { path: { id: assetId, suggestionId: v.suggestionId } },
            }),
          ),
    onSuccess: () => invalidateAsset(qc, assetId),
  });
}

export function useAcceptAll(assetId: string) {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (kind: AiSuggestionKind) =>
      unwrap(
        await api.POST('/api/v1/assets/{id}/ai/suggestions/accept-all', { params: { path: { id: assetId }, query: { kind } } }),
      ),
    onSuccess: () => invalidateAsset(qc, assetId),
  });
}

/** Admin: the tenant's AI settings (provider, features, privacy). Needs `ai.manage`. */
export function useAiSettings(enabled = true) {
  const api = useApiClient();
  const { status } = useAuth();
  return useQuery({
    queryKey: ['ai', 'settings'],
    queryFn: async () => unwrap(await api.GET('/api/v1/ai/settings')),
    enabled: enabled && status === 'authenticated',
  });
}

/** An API error that keeps a 422's per-field messages (`errors`: `provider`, `ocrLanguage`, `acknowledgeOffSite`). */
export class AiApiError extends ApiError {
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

export const isForbidden = (e: unknown) => e instanceof ApiError && e.status === 403;

export function useSaveAiSettings() {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (body: components['schemas']['AiSettingsRequest']) => {
      const { data, error, response } = await api.PUT('/api/v1/ai/settings', { body });
      if (response.ok) return data as AiSettings;
      const p = (error ?? {}) as { title?: string; detail?: string; correlationId?: string; errors?: Record<string, string[]> };
      throw new AiApiError(
        response.status,
        p.title ?? response.statusText,
        p.detail,
        p.correlationId ?? response.headers.get('X-Correlation-ID') ?? undefined,
        p.errors && typeof p.errors === 'object' ? p.errors : {},
      );
    },
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['ai'] });
    },
  });
}

/** Admin: provider health, queue depth, how many assets are enriched, and whether the vector index needs a reindex. */
export function useAiStatus(enabled = true) {
  const api = useApiClient();
  const { status } = useAuth();
  return useQuery({
    queryKey: ['ai', 'status'],
    queryFn: async () => unwrap(await api.GET('/api/v1/ai/status')),
    enabled: enabled && status === 'authenticated',
    refetchInterval: 15_000,
  });
}

export function useAiTest() {
  const api = useApiClient();
  return useMutation({ mutationFn: async () => unwrap(await api.POST('/api/v1/ai/test')) });
}

export function useAiBackfill() {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (body: Partial<components['schemas']['AiBackfillInput']> = {}) =>
      unwrap(
        await api.POST('/api/v1/ai/backfill', {
          body: { types: null, folderId: null, includeSubfolders: null, onlyMissing: null, limit: null, ...body },
        }),
      ),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['ai', 'status'] }),
  });
}

export function useAiReindex() {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async () => unwrap(await api.POST('/api/v1/ai/reindex')),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['ai', 'status'] }),
  });
}

/** 83500 -> "1:23" for transcript timestamps and jump links. */
export function formatTimestamp(ms: number): string {
  const total = Math.max(0, Math.floor(ms / 1000));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  const two = (n: number) => String(n).padStart(2, '0');
  return h > 0 ? `${h}:${two(m)}:${two(s)}` : `${m}:${two(s)}`;
}

/** What AI can do for this organisation, for ANY signed-in user (no admin permission): drives whether Smart search and the AI
 * panel are offered. Cached for a minute; contains no provider URLs, keys or model names. */
export function useAiCapabilities() {
  const api = useApiClient();
  const { status } = useAuth();
  return useQuery({
    queryKey: ['ai', 'capabilities'],
    queryFn: async () => unwrap(await api.GET('/api/v1/ai/capabilities')),
    enabled: status === 'authenticated',
    staleTime: 60_000,
  });
}
