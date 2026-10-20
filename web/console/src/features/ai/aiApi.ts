import { ApiError, unwrap, type components } from '@dam/sdk-js';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useApiClient } from '../../api/apiClient';
import { useAuth } from '../../auth/AuthProvider';

/** The AI summary of a document: derived data labelled as AI-generated, never written into editorial metadata (ADR-0036). */
export interface AiSummary {
  text: string;
  language: string | null;
  model: string;
  createdAt: string;
}
// Step 2.2 added fields to three existing DTOs (ADR-0036). The generated schema carries them as loosely typed fields; these
// aliases narrow them to what the server actually sends, and keep every one optional so an older response still type-checks.
export type AiSuggestion = components['schemas']['AiSuggestionDto'] & {
  /** Translation suggestions only: the metadata field, the target language, and the language it was translated from. */
  field?: string | null;
  language?: string | null;
  sourceLanguage?: string | null;
  /** True when the source text has changed since the translation was made. */
  sourceStale?: boolean;
};
export type AssetAi = Omit<components['schemas']['AssetAiDto'], 'suggestions' | 'summary'> & {
  suggestions: AiSuggestion[];
  summary?: AiSummary | null;
};
export type AiRun = components['schemas']['AiRunDto'];
export type AiTranscript = NonNullable<components['schemas']['AiTranscriptDto']>;
/** Which analyses are on. `summaries` (Step 2.2) is optional on the wire: a server that predates it omits it, and it then counts as on. */
export type AiFeatures = components['schemas']['AiFeaturesDto'] & { summaries?: boolean };
export type AiSettings = Omit<components['schemas']['AiSettingsDto'], 'features'> & { features: AiFeatures };
export type AiStatus = components['schemas']['AiStatusDto'];
export type AiCapabilities = components['schemas']['AiCapabilitiesDto'] & { translate?: boolean; summarise?: boolean };

export type AiSuggestionKind = 'tag' | 'language';
export const TRANSLATION_KIND = 'translation';
/** The AI state of an asset's current version: none = never analysed, disabled = AI is off for the tenant. */
export type AiAssetStatus = 'none' | 'pending' | 'running' | 'ready' | 'failed' | 'disabled';

export const invalidateAsset = (qc: ReturnType<typeof useQueryClient>, assetId: string) => {
  void qc.invalidateQueries({ queryKey: ['ai', 'asset', assetId] });
  void qc.invalidateQueries({ queryKey: ['asset', assetId] });
  void qc.invalidateQueries({ queryKey: ['assets'] });
  void qc.invalidateQueries({ queryKey: ['search'] });
  // A decided translation leaves the reviewer inbox and the nav count.
  void qc.invalidateQueries({ queryKey: ['ai', 'reviews'] });
};

/** What the AI found for an asset's current version. Polls while a run is pending or running so the panel fills in by itself. */
export function useAssetAi(assetId: string, enabled = true) {
  const api = useApiClient();
  const { status } = useAuth();
  return useQuery({
    queryKey: ['ai', 'asset', assetId],
    queryFn: async () => unwrap(await api.GET('/api/v1/assets/{id}/ai', { params: { path: { id: assetId } } })) as AssetAi,
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

/** Asks for a summary of the text read from the asset (202; it arrives with the AI view, which the caller keeps checking). */
export function useSummariseAsset(assetId: string) {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async () => unwrap(await api.POST('/api/v1/assets/{id}/ai/summarise', { params: { path: { id: assetId } } })),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['ai', 'asset', assetId] }),
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

export type AiSettingsRequest = Omit<components['schemas']['AiSettingsRequest'], 'features'> & { features: AiFeatures | null };

export const isForbidden = (e: unknown) => e instanceof ApiError && e.status === 403;

export function useSaveAiSettings() {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (body: AiSettingsRequest) => {
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
    queryFn: async () => unwrap(await api.GET('/api/v1/ai/capabilities')) as AiCapabilities,
    enabled: status === 'authenticated',
    staleTime: 60_000,
  });
}
