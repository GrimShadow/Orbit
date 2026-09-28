import { unwrap, type components } from '@dam/sdk-js';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useApiClient } from '../../api/apiClient';

export type AssetDto = components['schemas']['AssetDto'];
export type AssetVersionDto = components['schemas']['AssetVersionDto'];
export type RenditionDto = components['schemas']['RenditionDto'];
export type SchemaDto = components['schemas']['SchemaDto'];
export type FieldDto = components['schemas']['FieldDto'];
export type VocabularyDto = components['schemas']['VocabularyDto'];
export type TermDto = components['schemas']['TermDto'];

export function useAsset(id: string) {
  const api = useApiClient();
  return useQuery({
    queryKey: ['asset', id],
    queryFn: async () => unwrap(await api.GET('/api/v1/assets/{id}', { params: { path: { id } } })),
  });
}

export function useUpdateAsset(id: string) {
  const api = useApiClient();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (body: components['schemas']['UpdateAssetRequest']) =>
      unwrap(await api.PATCH('/api/v1/assets/{id}', { params: { path: { id } }, body })),
    onSuccess: (asset) => queryClient.setQueryData(['asset', id], asset),
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
    onSuccess: (asset) => queryClient.setQueryData(['asset', id], asset),
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

export function useAssetRenditions(id: string) {
  const api = useApiClient();
  return useQuery({
    queryKey: ['asset', id, 'renditions'],
    queryFn: async () => unwrap(await api.GET('/api/v1/assets/{id}/renditions', { params: { path: { id } } })),
  });
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

export function useSchema(assetType: string) {
  const api = useApiClient();
  return useQuery({
    queryKey: ['schema', assetType],
    queryFn: async () => unwrap(await api.GET('/api/v1/schemas/{assetType}', { params: { path: { assetType } } })),
  });
}

export function useVocabularies() {
  const api = useApiClient();
  return useQuery({ queryKey: ['vocabularies'], queryFn: async () => unwrap(await api.GET('/api/v1/vocabularies')) });
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
  return useQuery({
    queryKey: ['vocabulary', vocabularyId, 'terms'],
    queryFn: async () => {
      const nodes = unwrap(
        await api.GET('/api/v1/vocabularies/{id}/terms', { params: { path: { id: vocabularyId! }, query: { tree: true } } }),
      );
      return flattenTerms(nodes as unknown as TermNode[]);
    },
    enabled: !!vocabularyId,
  });
}
