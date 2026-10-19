import { unwrap, type components } from '@dam/sdk-js';
import { keepPreviousData, useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useApiClient } from '../../api/apiClient';
import type { SearchRequestBody } from '../search/searchApi';

export type CollectionDto = components['schemas']['CollectionDto'];

export function useCollections() {
  const api = useApiClient();
  return useQuery({ queryKey: ['collections'], queryFn: async () => unwrap(await api.GET('/api/v1/collections')) });
}

export function useCollection(id: string) {
  const api = useApiClient();
  return useQuery({
    queryKey: ['collections', id],
    queryFn: async () => unwrap(await api.GET('/api/v1/collections/{id}', { params: { path: { id } } })),
  });
}

/** Both collection kinds resolve their contents through this one endpoint (spec ADR-0013): a static collection's
 * literal `assetIds`, or a smart collection's saved query re-run now - either way the response is the same
 * `SearchResultDto` shape search itself returns, so the console renders it identically either way. */
export function useCollectionContents(id: string, cursor: string | undefined) {
  const api = useApiClient();
  return useQuery({
    queryKey: ['collections', id, 'assets', cursor],
    queryFn: async () =>
      unwrap(await api.GET('/api/v1/collections/{id}/assets', { params: { path: { id }, query: { cursor, limit: 50 } } })),
    placeholderData: keepPreviousData,
  });
}

/** Accumulating variant of `useCollectionContents`: "Load more" appends the next page of hits. */
export function useCollectionContentsInfinite(id: string) {
  const api = useApiClient();
  return useInfiniteQuery({
    queryKey: ['collections', id, 'assets', 'infinite'],
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam }) =>
      unwrap(
        await api.GET('/api/v1/collections/{id}/assets', { params: { path: { id }, query: { cursor: pageParam, limit: 50 } } }),
      ),
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    placeholderData: keepPreviousData,
  });
}

function useInvalidateCollections() {
  const queryClient = useQueryClient();
  return () => void queryClient.invalidateQueries({ queryKey: ['collections'] });
}

export function useCreateStaticCollection() {
  const api = useApiClient();
  const invalidate = useInvalidateCollections();
  return useMutation({
    mutationFn: async ({ name, assetIds }: { name: string; assetIds: string[] }) =>
      unwrap(await api.POST('/api/v1/collections', { body: { name, type: 'static', assetIds, query: null } })),
    onSuccess: invalidate,
  });
}

/** A "saved search" (spec Step 1.8) is simply a named smart collection - there is no separate saved-search entity on
 * the backend (see ADR-0018): saving one from the Search page just names the current query and posts it here. */
export function useCreateSmartCollection() {
  const api = useApiClient();
  const invalidate = useInvalidateCollections();
  return useMutation({
    mutationFn: async ({ name, query }: { name: string; query: SearchRequestBody }) =>
      unwrap(await api.POST('/api/v1/collections', { body: { name, type: 'smart', assetIds: null, query } })),
    onSuccess: invalidate,
  });
}

export function useRenameCollection(id: string) {
  const api = useApiClient();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (name: string) =>
      unwrap(await api.PUT('/api/v1/collections/{id}', { params: { path: { id } }, body: { name } })),
    onSuccess: (dto) => {
      queryClient.setQueryData(['collections', id], dto);
      void queryClient.invalidateQueries({ queryKey: ['collections'] });
    },
  });
}

export function useShareCollection(id: string) {
  const api = useApiClient();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (visibility: 'private' | 'tenant') =>
      unwrap(await api.POST('/api/v1/collections/{id}/share', { params: { path: { id } }, body: { visibility } })),
    onSuccess: (dto) => {
      queryClient.setQueryData(['collections', id], dto);
      void queryClient.invalidateQueries({ queryKey: ['collections'] });
    },
  });
}

export function useDeleteCollection() {
  const api = useApiClient();
  const invalidate = useInvalidateCollections();
  return useMutation({
    mutationFn: async (id: string) => unwrap(await api.DELETE('/api/v1/collections/{id}', { params: { path: { id } } })),
    onSuccess: invalidate,
  });
}
