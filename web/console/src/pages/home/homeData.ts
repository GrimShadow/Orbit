import { unwrap } from '@dam/sdk-js';
import { useQuery } from '@tanstack/react-query';
import { useApiClient } from '../../api/apiClient';
import { useAuth } from '../../auth/AuthProvider';

/** Library-wide counts for the overview cards. `POST /search` with `limit: 1` returns the total and the status/type
 * facets without paging through assets (the same facet fields the Search page asks for). */
export function useLibrarySnapshot() {
  const api = useApiClient();
  const { status } = useAuth();
  return useQuery({
    queryKey: ['home', 'snapshot'],
    queryFn: async () =>
      unwrap(
        await api.POST('/api/v1/search', {
          body: {
            query: null,
            filters: null,
            facets: ['status', 'type'],
            sort: null,
            highlight: false,
            similarTo: null,
            semantic: false,
            cursor: null,
            limit: 1,
          },
        }),
      ),
    enabled: status === 'authenticated',
  });
}

export function facetCount(
  facets: { field: string; values: { value: string; count: number }[] }[] | undefined,
  field: string,
  value: string,
): number {
  return facets?.find((f) => f.field === field)?.values.find((v) => v.value === value)?.count ?? 0;
}

/** First name for the greeting: the first word of a display name, or the first part of an e-mail address. */
export function firstNameOf(name: string | undefined): string {
  const raw = (name ?? '').trim();
  if (!raw) return '';
  const base = raw.includes('@') ? (raw.split('@')[0] ?? '') : raw;
  const word = base.split(/[\s._-]+/).filter(Boolean)[0] ?? '';
  return raw.includes('@') ? word.charAt(0).toUpperCase() + word.slice(1) : word;
}

export type DayPart = 'morning' | 'afternoon' | 'evening';
export function dayPartOf(date: Date): DayPart {
  const h = date.getHours();
  return h < 12 ? 'morning' : h < 18 ? 'afternoon' : 'evening';
}
