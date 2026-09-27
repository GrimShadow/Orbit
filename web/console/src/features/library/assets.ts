import { unwrap, type components } from '@dam/sdk-js';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
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
