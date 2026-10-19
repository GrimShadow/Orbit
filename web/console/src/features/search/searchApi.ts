import { unwrap, type components } from '@dam/sdk-js';
import { keepPreviousData, useInfiniteQuery, useQuery } from '@tanstack/react-query';
import { useApiClient } from '../../api/apiClient';

export type SearchHitDto = components['schemas']['SearchHitDto'];
export type SearchResultDto = components['schemas']['SearchResultDto'];
export type SearchRequestBody = components['schemas']['SearchRequestBody'];

/** The facet fields every search asks for - `type` and `status` are meaningful across every tenant's content model
 * without any per-asset-type schema knowledge (spec ADR-0011's own facet flags are per metadata field, which the
 * console would need each asset type's current schema to enumerate; that is future work, not this step's scope). */
export const SEARCH_FACET_FIELDS = ['type', 'status'];

/** `POST /search` (spec Step 1.7b) - the global, cross-folder query surface, distinct from the library screen's
 * folder-scoped `GET /assets` browsing (ADR-0012 already explains why the two stay separate: search has no
 * folder-prefix filter). */
export function useSearch(query: SearchRequestBody, enabled: boolean) {
  const api = useApiClient();
  return useQuery({
    queryKey: ['search', query],
    queryFn: async () => unwrap(await api.POST('/api/v1/search', { body: query })),
    enabled,
    placeholderData: keepPreviousData,
  });
}

/** The Search screen's accumulating variant: "Load more" appends the next page of hits (the first page also carries the
 * facet counts and the total). `query.cursor` is ignored - the page param owns it. */
export function useSearchInfinite(query: Omit<SearchRequestBody, 'cursor'>) {
  const api = useApiClient();
  return useInfiniteQuery({
    queryKey: ['search', 'infinite', query],
    initialPageParam: null as string | null,
    queryFn: async ({ pageParam }) => unwrap(await api.POST('/api/v1/search', { body: { ...query, cursor: pageParam } })),
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    placeholderData: keepPreviousData,
  });
}
