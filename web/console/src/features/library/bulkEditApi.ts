import { unwrap } from '@dam/sdk-js';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useApiClient } from '../../api/apiClient';
import type { AssetDto } from './assets';
import { runBulk, type BulkResult } from './bulkOrchestrator';

/** Full-replace tag edit across many assets at once - there is no bulk-edit endpoint (only `PATCH /assets/{id}`,
 * itself a full-replace of `tags`; see ADR-0017), so each asset's own current tag list is read back from the row
 * data already on screen and the requested tags are added to or removed from it before sending. */
export function useBulkEditTags() {
  const api = useApiClient();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ assets, tags, remove }: { assets: AssetDto[]; tags: string[]; remove: boolean }): Promise<BulkResult> =>
      runBulk(
        assets.map((a) => a.id),
        async (id) => {
          const asset = assets.find((a) => a.id === id)!;
          const nextTags = remove ? asset.tags.filter((t) => !tags.includes(t)) : [...new Set([...asset.tags, ...tags])];
          unwrap(
            await api.PATCH('/api/v1/assets/{id}', {
              params: { path: { id } },
              body: { title: null, metadata: null, tags: nextTags },
            }),
          );
        },
      ),
    onSettled: () => void queryClient.invalidateQueries({ queryKey: ['assets'] }),
  });
}

export function useBulkMove() {
  const api = useApiClient();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ ids, folderId }: { ids: string[]; folderId: string | null }): Promise<BulkResult> =>
      runBulk(ids, async (id) => {
        unwrap(await api.POST('/api/v1/assets/{id}/move', { params: { path: { id } }, body: { folderId } }));
      }),
    onSettled: () => void queryClient.invalidateQueries({ queryKey: ['assets'] }),
  });
}

/** Fans out the same per-asset transition (spec Step 1.6, `POST /assets/{id}/transitions/{action}`) across every
 * selected asset. The caller only offers an action here once it is valid for every selected asset's current status
 * (see `commonActions` in `BulkEditBar.tsx`, reusing `../asset/lifecycle`'s table) - the server still re-validates
 * each one independently regardless. */
export function useBulkTransition() {
  const api = useApiClient();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ ids, action }: { ids: string[]; action: string }): Promise<BulkResult> =>
      runBulk(ids, async (id) => {
        unwrap(
          await api.POST('/api/v1/assets/{id}/transitions/{action}', {
            params: { path: { id, action } },
            body: { reason: null },
          }),
        );
      }),
    onSettled: () => void queryClient.invalidateQueries({ queryKey: ['assets'] }),
  });
}
