import { unwrap, type components } from '@dam/sdk-js';
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useCallback } from 'react';
import { ApiError } from '../../api/client';
import { useApiClient } from '../../api/apiClient';
import { useAuth } from '../../auth/AuthProvider';
import { runBulk, type BulkResult } from '../library/bulkOrchestrator';

export type AnalyticsOverview = components['schemas']['AnalyticsOverviewDto'];
export type UnusedAsset = components['schemas']['UnusedAssetDto'];
export type UsageEventType = 'view' | 'download' | 'share' | 'embed' | 'place' | 'play';

/** 403 means the caller's role lacks `analytics.read` - the dashboard then simply omits the analytics panels. */
export function isForbidden(error: unknown): boolean {
  return error instanceof ApiError && error.status === 403;
}

export function useOverview(days: number) {
  const api = useApiClient();
  const { status } = useAuth();
  return useQuery({
    queryKey: ['analytics', 'overview', days],
    queryFn: async () => unwrap(await api.GET('/api/v1/analytics/overview', { params: { query: { days } } })),
    enabled: status === 'authenticated',
    staleTime: 60_000,
  });
}

export interface UnusedFilter {
  days: number;
  /** `published` (default), `all`, or a single status. */
  status: string;
  type?: string;
}

export function useUnusedAssets(filter: UnusedFilter, limit: number, enabled = true) {
  const api = useApiClient();
  const { status } = useAuth();
  return useInfiniteQuery({
    queryKey: ['analytics', 'unused', filter, limit],
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam }) =>
      unwrap(
        await api.GET('/api/v1/analytics/unused-assets', {
          params: { query: { days: filter.days, status: filter.status, type: filter.type, limit, cursor: pageParam } },
        }),
      ),
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    enabled: enabled && status === 'authenticated',
    staleTime: 30_000,
  });
}

/** Records one usage event. Fire-and-forget: a failed beacon must never get in the way of the action that caused it,
 * so errors are swallowed. Previews and thumbnails are deliberately NOT recorded - only deliberate use is. */
export function useRecordUsage() {
  const api = useApiClient();
  const queryClient = useQueryClient();
  return useCallback(
    (assetId: string, eventType: UsageEventType, rendition?: string) => {
      void api
        .POST('/api/v1/usage/events', {
          body: {
            events: [
              {
                clientEventId: null,
                assetId,
                eventType,
                channel: 'console',
                rendition: rendition ?? null,
                occurredAt: null,
                context: null,
              },
            ],
          },
        })
        .then(() => queryClient.invalidateQueries({ queryKey: ['analytics'] }))
        .catch(() => undefined);
    },
    [api, queryClient],
  );
}

/** Retires assets from active use. The lifecycle only allows archiving an *expired* asset, so a published one is expired
 * first and then archived; anything already archived or still pre-publication is reported as failed by the server. */
export function useRetireAssets() {
  const api = useApiClient();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (assets: Pick<UnusedAsset, 'id' | 'status'>[]): Promise<BulkResult> =>
      runBulk(
        assets.map((a) => a.id),
        async (id) => {
          const status = assets.find((a) => a.id === id)!.status;
          const step = async (action: string) =>
            unwrap(
              await api.POST('/api/v1/assets/{id}/transitions/{action}', {
                params: { path: { id, action } },
                body: { reason: 'unused' },
              }),
            );
          if (status === 'published') await step('expire');
          if (status === 'published' || status === 'expired') await step('archive');
          else throw new Error(`Cannot archive an asset that is ${status}.`);
        },
      ),
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: ['analytics'] });
      void queryClient.invalidateQueries({ queryKey: ['assets'] });
      void queryClient.invalidateQueries({ queryKey: ['home'] });
    },
  });
}
