import { unwrap, type components } from '@dam/sdk-js';
import { keepPreviousData, useInfiniteQuery, useQueries, useQuery } from '@tanstack/react-query';
import { useApiClient } from '../../api/apiClient';

export type AssetDto = components['schemas']['AssetDto'];
export type PageOfAssetDto = components['schemas']['PageOfAssetDto'];

export interface AssetListParams {
  folderId?: string;
  includeSubfolders?: boolean;
  status?: string;
  type?: string;
  cursor?: string;
  limit?: number;
}

/** The interim listing (spec Step 1.7's own note: search replaces this for the global search bar, but folder-scoped
 * browsing has no equivalent in `POST /search` yet - it has no folder-prefix filter, only exact-field terms - so the
 * library screen still uses this for "what's in this folder" until that lands). */
export function useAssets(params: AssetListParams) {
  const api = useApiClient();
  return useQuery({
    queryKey: ['assets', params],
    queryFn: async () =>
      unwrap(
        await api.GET('/api/v1/assets', {
          params: {
            query: {
              folderId: params.folderId,
              includeSubfolders: params.includeSubfolders,
              status: params.status,
              type: params.type,
              cursor: params.cursor,
              limit: params.limit,
            },
          },
        }),
      ),
    placeholderData: keepPreviousData,
  });
}

/** The library's accumulating listing: each "Load more" appends the next page (cursor-paged by the server) instead of
 * replacing the one on screen, so scroll position, selection and thumbnails of what was already loaded survive. */
export function useAssetsInfinite(params: Omit<AssetListParams, 'cursor'>) {
  const api = useApiClient();
  return useInfiniteQuery({
    queryKey: ['assets', 'infinite', params],
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam }) =>
      unwrap(
        await api.GET('/api/v1/assets', {
          params: {
            query: {
              folderId: params.folderId,
              includeSubfolders: params.includeSubfolders,
              status: params.status,
              type: params.type,
              cursor: pageParam,
              limit: params.limit,
            },
          },
        }),
      ),
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    placeholderData: keepPreviousData,
  });
}

const THUMBNAIL_CHUNK = 50;

/** Signed thumbnail links for an accumulating list. The server accepts at most 200 ids per batch, so the ids are asked
 * for in chunks of 50 (aligned to the pages the library loads - an earlier chunk's key never changes when more pages
 * arrive, so it is not re-requested). The query keys match `useThumbnailLinks`, so both share one cache. */
export function useThumbnailMap(ids: string[]): Record<string, string | null> {
  const api = useApiClient();
  const chunks: string[][] = [];
  for (let i = 0; i < ids.length; i += THUMBNAIL_CHUNK) chunks.push(ids.slice(i, i + THUMBNAIL_CHUNK));
  return useQueries({
    queries: chunks.map((chunk) => ({
      queryKey: ['thumbnailLinks', [...chunk].sort().join(',')],
      queryFn: async () =>
        unwrap(await api.POST('/api/v1/assets/links/batch', { body: { assetIds: chunk } })) as Record<string, string | null>,
      staleTime: 4 * 60 * 1000,
    })),
    combine: (results) => Object.assign({}, ...results.map((r) => r.data ?? {})) as Record<string, string | null>,
  });
}
