import { unwrap } from '@dam/sdk-js';
import type { useApiClient } from '../../api/apiClient';
import type { UnusedAsset, UnusedFilter } from './analyticsApi';

export const EXPORT_CAP = 5000;

/** Every unused asset matching the filter (up to `EXPORT_CAP`), for the CSV export - independent of how many pages the
 * screen has loaded. */
export async function fetchAllUnused(
  api: ReturnType<typeof useApiClient>,
  filter: UnusedFilter,
): Promise<{ items: UnusedAsset[]; capped: boolean }> {
  const items: UnusedAsset[] = [];
  let cursor: string | undefined;
  do {
    const page = unwrap(
      await api.GET('/api/v1/analytics/unused-assets', {
        params: { query: { days: filter.days, status: filter.status, type: filter.type, limit: 200, cursor } },
      }),
    );
    items.push(...page.items);
    cursor = page.nextCursor ?? undefined;
  } while (cursor && items.length < EXPORT_CAP);
  return { items: items.slice(0, EXPORT_CAP), capped: Boolean(cursor) };
}
