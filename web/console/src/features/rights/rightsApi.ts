import { ApiError, unwrap, type components } from '@dam/sdk-js';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useApiClient } from '../../api/apiClient';
import { useAuth } from '../../auth/AuthProvider';

export type Rights = components['schemas']['RightsDto'];
export type RightsInputBody = components['schemas']['RightsInput'];
export type RightsSummary = NonNullable<components['schemas']['RightsSummaryDto']>;
export type RightsCheck = components['schemas']['RightsCheckDto'];
export type RightsDocument = components['schemas']['RightsDocumentDto'];
export type AssignResult = components['schemas']['AssignRightsResultDto'];

export type RightsStatus = 'valid' | 'expiring' | 'expired' | 'not_yet_valid';
export const LICENCE_TYPES = [
  'owned',
  'licensed',
  'royalty_free',
  'rights_managed',
  'editorial',
  'creative_commons',
  'other',
] as const;

/** The server marks an asset restricted to nothing with this attribute value; never show it to people. */
export const RESTRICTED_MARKER = '~restricted';

export const isForbidden = (e: unknown) => e instanceof ApiError && e.status === 403;

/** An API error that also keeps what the plain `unwrap` drops: a 422's per-field messages (`errors`) and the numeric
 * `assetCount` the server adds to the 409 for deleting rights that assets still use. */
export class RightsApiError extends ApiError {
  constructor(
    status: number,
    title: string,
    detail: string | undefined,
    correlationId: string | undefined,
    public fields: Record<string, string[]>,
    public assetCount: number | undefined,
  ) {
    super(status, title, detail, correlationId);
  }
}

interface Problem {
  title?: string;
  detail?: string;
  correlationId?: string;
  errors?: Record<string, string[]>;
  assetCount?: number;
}

/** Like `unwrap`, but throws a {@link RightsApiError}. */
function unwrapRights<T>({ data, error, response }: { data?: T; error?: unknown; response: Response }): T {
  if (response.ok) return data as T;
  const p = (error ?? {}) as Problem;
  throw new RightsApiError(
    response.status,
    p.title ?? response.statusText,
    p.detail,
    p.correlationId ?? response.headers.get('X-Correlation-ID') ?? undefined,
    p.errors && typeof p.errors === 'object' ? p.errors : {},
    typeof p.assetCount === 'number' ? p.assetCount : undefined,
  );
}

const invalidateAll = (qc: ReturnType<typeof useQueryClient>) => {
  void qc.invalidateQueries({ queryKey: ['rights'] });
  void qc.invalidateQueries({ queryKey: ['assetRights'] });
  void qc.invalidateQueries({ queryKey: ['asset'] });
  void qc.invalidateQueries({ queryKey: ['assets'] });
};

export function useRightsList(filter: { status?: string; q?: string } = {}, enabled = true) {
  const api = useApiClient();
  const { status } = useAuth();
  return useQuery({
    queryKey: ['rights', 'list', filter],
    queryFn: async () => unwrap(await api.GET('/api/v1/rights', { params: { query: { status: filter.status, q: filter.q } } })),
    enabled: enabled && status === 'authenticated',
  });
}

export function useSaveRights() {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (v: { id?: string; body: RightsInputBody }) =>
      v.id
        ? unwrapRights(await api.PUT('/api/v1/rights/{id}', { params: { path: { id: v.id } }, body: v.body }))
        : unwrapRights(await api.POST('/api/v1/rights', { body: v.body })),
    onSuccess: () => invalidateAll(qc),
  });
}

export function useDeleteRights() {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => unwrapRights(await api.DELETE('/api/v1/rights/{id}', { params: { path: { id } } })),
    onSuccess: () => invalidateAll(qc),
  });
}

/** Assign (or, with `replace`, move) rights to up to 500 assets; the result lists what happened per asset. */
export function useAssignRights() {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (v: { rightsId: string; assetIds: string[]; replace?: boolean }) =>
      unwrap(
        await api.POST('/api/v1/rights/{id}/assign', {
          params: { path: { id: v.rightsId } },
          body: { assetIds: v.assetIds, replace: v.replace ?? false },
        }),
      ),
    onSuccess: () => invalidateAll(qc),
  });
}

/** An asset's rights summary, or null when none is assigned. Readable by anyone who can read the asset. */
export function useAssetRights(assetId: string, enabled = true) {
  const api = useApiClient();
  const { status } = useAuth();
  return useQuery({
    queryKey: ['assetRights', assetId],
    queryFn: async () =>
      (unwrap(await api.GET('/api/v1/assets/{id}/rights', { params: { path: { id: assetId } } })).rights ??
        null) as RightsSummary | null,
    enabled: enabled && status === 'authenticated',
    staleTime: 15_000,
  });
}

export function useSetAssetRights(assetId: string) {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (rightsId: string | null) =>
      unwrap(await api.PUT('/api/v1/assets/{id}/rights', { params: { path: { id: assetId } }, body: { rightsId } as never })),
    onSuccess: () => invalidateAll(qc),
  });
}

/** "Can this asset be used on <channel> in <region>?" - both optional. */
export function useRightsCheck(assetId: string, channel: string, region: string, enabled: boolean) {
  const api = useApiClient();
  return useQuery({
    queryKey: ['assetRights', assetId, 'check', channel, region],
    queryFn: async () =>
      unwrap(
        await api.GET('/api/v1/assets/{id}/rights/check', {
          params: { path: { id: assetId }, query: { channel: channel || undefined, region: region || undefined } },
        }),
      ),
    enabled,
    staleTime: 10_000,
  });
}

export function useRightsDocuments(rightsId: string, enabled = true) {
  const api = useApiClient();
  return useQuery({
    queryKey: ['rights', rightsId, 'documents'],
    queryFn: async () => unwrap(await api.GET('/api/v1/rights/{id}/documents', { params: { path: { id: rightsId } } })),
    enabled,
  });
}

/** Presign, PUT the file straight to storage, then confirm - the file bytes never pass through the API. */
export function useUploadRightsDocument(rightsId: string) {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (file: File) => {
      const contentType = file.type || 'application/octet-stream';
      const up = unwrapRights(
        await api.POST('/api/v1/rights/{id}/documents', {
          params: { path: { id: rightsId } },
          body: { fileName: file.name, contentType, size: file.size },
        }),
      );
      const res = await fetch(up.uploadUrl, {
        method: 'PUT',
        headers: { ...(up.headers as Record<string, string> | undefined), 'Content-Type': contentType },
        body: file,
      });
      if (!res.ok) throw new Error(`Upload failed (${res.status})`);
      return unwrapRights(
        await api.POST('/api/v1/rights/{id}/documents/{documentId}/complete', {
          params: { path: { id: rightsId, documentId: up.documentId } },
          body: { fileName: file.name, contentType, size: file.size },
        }),
      );
    },
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['rights'] });
    },
  });
}

export function useDeleteRightsDocument(rightsId: string) {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (docId: string) =>
      unwrap(
        await api.DELETE('/api/v1/rights/{id}/documents/{documentId}', { params: { path: { id: rightsId, documentId: docId } } }),
      ),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['rights'] }),
  });
}

/** A short-lived forced-download link for a release document. */
export function useDocumentLink(rightsId: string) {
  const api = useApiClient();
  return useMutation({
    mutationFn: async (docId: string) =>
      unwrap(
        await api.GET('/api/v1/rights/{id}/documents/{documentId}/link', {
          params: { path: { id: rightsId, documentId: docId } },
        }),
      ),
  });
}
