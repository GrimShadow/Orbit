import { ApiError, unwrap } from '@dam/sdk-js';
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useApiClient } from '../../api/apiClient';
import { useAuth } from '../../auth/AuthProvider';
import type { GlossaryTerm, GlossaryTermInput, Page } from '../translation/translationTypes';

/** Terms a tenant may hold (ADR-0036). */
export const GLOSSARY_LIMIT = 5000;
export const PAGE_SIZE = 50;

/** One page of terms matching `q` (empty: all), in term order; pages accumulate. */
export function useGlossary(q: string) {
  const api = useApiClient();
  const { status } = useAuth();
  const search = q.trim();
  return useInfiniteQuery({
    queryKey: ['glossary', 'list', search],
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam }) =>
      unwrap(
        await api.GET('/api/v1/glossary', {
          params: { query: { q: search || undefined, limit: PAGE_SIZE, cursor: pageParam } },
        }),
      ) as Page<GlossaryTerm>,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    enabled: status === 'authenticated',
  });
}

/** Whether the glossary holds anything at all - any signed-in reader may ask (`assets.read`). Decides whether a translation
 * suggestion says that glossary terms were protected. */
export function useGlossaryHasTerms() {
  const api = useApiClient();
  const { status } = useAuth();
  return useQuery({
    queryKey: ['glossary', 'any'],
    queryFn: async () => {
      const page = unwrap(await api.GET('/api/v1/glossary', { params: { query: { limit: 1 } } })) as Page<GlossaryTerm>;
      return page.items.length > 0;
    },
    enabled: status === 'authenticated',
    staleTime: 5 * 60_000,
  });
}

export function useSaveGlossaryTerm() {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (v: { id?: string; body: GlossaryTermInput }) =>
      (v.id
        ? unwrap(await api.PUT('/api/v1/glossary/{id}', { params: { path: { id: v.id } }, body: v.body }))
        : unwrap(await api.POST('/api/v1/glossary', { body: v.body }))) as GlossaryTerm,
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['glossary'] }),
  });
}

export function useDeleteGlossaryTerm() {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => {
      unwrap(await api.DELETE('/api/v1/glossary/{id}', { params: { path: { id } } }));
    },
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['glossary'] }),
  });
}

/** Creates the terms one after another (the API has no bulk route), reporting each outcome, and stops early when the
 * tenant's term limit is reached. */
export function useImportGlossary() {
  const api = useApiClient();
  const qc = useQueryClient();
  return async (entries: GlossaryTermInput[], onProgress: (done: number, total: number) => void): Promise<ImportOutcome> => {
    const outcome: ImportOutcome = { created: 0, failed: [], limitReached: false, skipped: 0 };
    for (const [i, entry] of entries.entries()) {
      try {
        unwrap(await api.POST('/api/v1/glossary', { body: entry }));
        outcome.created += 1;
      } catch (e) {
        if (isLimitError(e)) {
          outcome.limitReached = true;
          outcome.skipped = entries.length - i;
          break;
        }
        outcome.failed.push({ term: entry.term, reason: e instanceof ApiError ? e.detail || e.title : String(e) });
      }
      onProgress(i + 1, entries.length);
    }
    void qc.invalidateQueries({ queryKey: ['glossary'] });
    return outcome;
  };
}

export interface ImportOutcome {
  created: number;
  failed: { term: string; reason: string }[];
  /** The 5,000-term limit stopped the import. */
  limitReached: boolean;
  /** How many were not tried because of that. */
  skipped: number;
}

/** The server refuses a term beyond the tenant's limit with a conflict or validation answer naming the limit. The
 * contract does not fix the status code, so both are recognised, and only when the message talks about the limit. */
export function isLimitError(e: unknown): boolean {
  if (!(e instanceof ApiError) || (e.status !== 409 && e.status !== 422)) return false;
  const text = `${e.title} ${e.detail ?? ''} ${JSON.stringify(e.problem?.errors ?? {})}`;
  return /5,?000|limit|too many|maximum/i.test(text);
}
