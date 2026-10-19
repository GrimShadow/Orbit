import { ApiError, unwrap, type ApiClient, type components } from '@dam/sdk-js';
import { queryOptions, useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query';
import { useApiClient } from '../../api/apiClient';

export type AssetDto = components['schemas']['AssetDto'];
export type AssetVersionDto = components['schemas']['AssetVersionDto'];
export type RenditionDto = components['schemas']['RenditionDto'];
export type SchemaDto = components['schemas']['SchemaDto'];
export type FieldDto = components['schemas']['FieldDto'];
export type VocabularyDto = components['schemas']['VocabularyDto'];
export type TermDto = components['schemas']['TermDto'];

/** Query definitions shared by the hooks below and by code that has to warm the same caches imperatively (the
 * right-click menus fetch what their items depend on with `queryClient.fetchQuery`). */
export const assetOptions = (api: ApiClient, id: string) =>
  queryOptions({
    queryKey: ['asset', id],
    queryFn: async () => unwrap(await api.GET('/api/v1/assets/{id}', { params: { path: { id } } })),
  });
export const schemaOptions = (api: ApiClient, assetType: string) =>
  queryOptions({
    queryKey: ['schema', assetType],
    queryFn: async () => unwrap(await api.GET('/api/v1/schemas/{assetType}', { params: { path: { assetType } } })),
  });
export const vocabulariesOptions = (api: ApiClient) =>
  queryOptions({ queryKey: ['vocabularies'], queryFn: async () => unwrap(await api.GET('/api/v1/vocabularies')) });
export const termsOptions = (api: ApiClient, vocabularyId: string) =>
  queryOptions({
    queryKey: ['vocabulary', vocabularyId, 'terms'],
    queryFn: async () => {
      const nodes = unwrap(
        await api.GET('/api/v1/vocabularies/{id}/terms', { params: { path: { id: vocabularyId }, query: { tree: true } } }),
      );
      return flattenTerms(nodes as unknown as TermNode[]);
    },
  });

/** A title, tag, metadata or status change makes any cached grid/list stale; the single-asset caches are set directly.
 * A status change can also start, finish or cancel an approval workflow, so the workflow views and task counts refresh too. */
function refreshLists(queryClient: QueryClient) {
  void queryClient.invalidateQueries({
    predicate: (q) => ['assets', 'search', 'collections', 'assetWorkflow', 'tasks'].includes(String(q.queryKey[0])),
  });
}

export function useAsset(id: string) {
  const api = useApiClient();
  return useQuery(assetOptions(api, id));
}

export function useUpdateAsset(id: string) {
  const api = useApiClient();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (body: components['schemas']['UpdateAssetRequest']) =>
      unwrap(await api.PATCH('/api/v1/assets/{id}', { params: { path: { id } }, body })),
    onSuccess: (asset) => {
      queryClient.setQueryData(['asset', id], asset);
      refreshLists(queryClient);
    },
  });
}

export function useTransition(id: string) {
  const api = useApiClient();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ action, reason }: { action: string; reason?: string }) =>
      unwrap(
        await api.POST('/api/v1/assets/{id}/transitions/{action}', {
          params: { path: { id, action } },
          body: { reason: reason ?? null },
        }),
      ),
    onSuccess: (asset) => {
      queryClient.setQueryData(['asset', id], asset);
      refreshLists(queryClient);
    },
  });
}

export function useAssetVersions(id: string) {
  const api = useApiClient();
  return useQuery({
    queryKey: ['asset', id, 'versions'],
    queryFn: async () => unwrap(await api.GET('/api/v1/assets/{id}/versions', { params: { path: { id } } })),
  });
}

export function useRestoreVersion(id: string) {
  const api = useApiClient();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (versionNo: number) =>
      unwrap(await api.POST('/api/v1/assets/{id}/versions/{versionNo}/restore', { params: { path: { id, versionNo } } })),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['asset', id, 'versions'] });
      void queryClient.invalidateQueries({ queryKey: ['asset', id] });
    },
  });
}

export function useAssetRenditions(id: string, enabled = true) {
  const api = useApiClient();
  return useQuery({
    queryKey: ['asset', id, 'renditions'],
    queryFn: async () => unwrap(await api.GET('/api/v1/assets/{id}/renditions', { params: { path: { id } } })),
    enabled,
  });
}

/** The server's own explanation when there is one (`detail`), since an ApiError's message is only its short title. */
export function errorMessage(e: unknown): string {
  return e instanceof ApiError ? e.detail || e.title : e instanceof Error ? e.message : String(e);
}

/** A short-lived signed link (spec ADR-0006) to a rendition, fetched lazily and cached briefly - a fresh one is asked
 * for well before the previous one could expire. */
export function useDeliveryLink(id: string, renditions: string[], enabled: boolean) {
  const api = useApiClient();
  return useQuery({
    queryKey: ['asset', id, 'links', renditions],
    queryFn: async () =>
      unwrap(
        await api.POST('/api/v1/assets/{id}/links', {
          params: { path: { id } },
          body: { renditions, ttlSeconds: 300, ip: null, transform: null },
        }),
      ),
    enabled,
    staleTime: 4 * 60 * 1000,
  });
}

/** Signed thumbnail links for many assets in one call (`POST /assets/links/batch`) - what a library/search/collection
 * grid needs to show real image thumbnails, instead of one `useDeliveryLink` per card (which doesn't scale to a grid
 * of dozens). An id the batch couldn't produce a thumbnail for (no permission, no file yet, deleted) maps to `null`;
 * `toAssetThumb` treats that the same as "not requested yet" and falls back to the file-type icon. */
export function useThumbnailLinks(assetIds: string[]) {
  const api = useApiClient();
  const key = [...assetIds].sort().join(',');
  return useQuery({
    queryKey: ['thumbnailLinks', key],
    queryFn: async () =>
      unwrap(await api.POST('/api/v1/assets/links/batch', { body: { assetIds } })) as Record<string, string | null>,
    enabled: assetIds.length > 0,
    staleTime: 4 * 60 * 1000,
  });
}

export function useSchema(assetType: string) {
  const api = useApiClient();
  return useQuery(schemaOptions(api, assetType));
}

export function useVocabularies() {
  const api = useApiClient();
  return useQuery(vocabulariesOptions(api));
}

/** Terms for one vocabulary, flattened depth-first with the same indentation convention `folderOptions` uses - good
 * enough for a picker without a dedicated hierarchical widget. `ListTerms`'s `TermNodeDto.children` is untyped
 * (`unknown[]`, an OpenAPI self-reference the generator can't express) so it is cast back to its real recursive shape. */
export interface FlatTerm {
  code: string;
  label: string;
  depth: number;
}

interface TermNode {
  term: TermDto;
  children: TermNode[];
}

function flattenTerms(nodes: TermNode[], depth = 0): FlatTerm[] {
  return nodes.flatMap((n) => [
    { code: n.term.code, label: n.term.labels.en ?? n.term.code, depth },
    ...flattenTerms(n.children, depth + 1),
  ]);
}

export function useTerms(vocabularyId: string | undefined) {
  const api = useApiClient();
  return useQuery({ ...termsOptions(api, vocabularyId ?? ''), enabled: !!vocabularyId });
}
