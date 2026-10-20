import { unwrap } from '@dam/sdk-js';
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef } from 'react';
import { useApiClient } from '../../api/apiClient';
import { useAuth } from '../../auth/AuthProvider';
import { invalidateAsset } from '../ai/aiApi';
import {
  jobActive,
  type AcceptTranslationBody,
  type AiReviewItem,
  type Page,
  type ReviewSummary,
  type TranslateRequest,
  type TranslationJob,
  type TranslationJobs,
} from './translationTypes';

const JOB_POLL_MS = 2500;

/** The latest translation jobs of an asset (newest first). Polls while one is pending or running, and when the last one
 * finishes it refreshes the AI view, so the suggestions it produced appear without a reload. */
export function useTranslationJobs(assetId: string, enabled = true) {
  const api = useApiClient();
  const { status } = useAuth();
  const qc = useQueryClient();
  const query = useQuery({
    queryKey: ['translations', assetId],
    queryFn: async () =>
      unwrap(await api.GET('/api/v1/assets/{id}/translations', { params: { path: { id: assetId } } })) as TranslationJobs,
    enabled: enabled && status === 'authenticated',
    staleTime: 10_000,
    refetchInterval: (q) => (q.state.data?.jobs.some(jobActive) ? JOB_POLL_MS : false),
  });
  const active = !!query.data?.jobs.some(jobActive);
  const wasActive = useRef(false);
  useEffect(() => {
    if (wasActive.current && !active) invalidateAsset(qc, assetId);
    wasActive.current = active;
  }, [active, assetId, qc]);
  return query;
}

export function useTranslateAsset(assetId: string) {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (body: TranslateRequest) =>
      unwrap(
        await api.POST('/api/v1/assets/{id}/translate', {
          params: { path: { id: assetId } },
          body: {
            fields: body.fields,
            targetLanguages: body.targetLanguages,
            sourceLanguage: body.sourceLanguage ?? null,
            overwrite: body.overwrite ?? null,
          },
        }),
      ) as TranslationJob,
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['translations', assetId] }),
  });
}

export interface TranslationDecision {
  suggestionId: string;
  decision: 'accept' | 'reject';
  /** Accept only. */
  body?: AcceptTranslationBody;
}

/** Accepts (optionally with edited text, or confirming a replacement) or rejects one translation suggestion. A decided
 * suggestion changes the asset, the AI view and the reviewer inbox, so all three refresh. */
export function useDecideTranslation(assetId: string) {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (v: TranslationDecision) => {
      const params = { path: { id: assetId, suggestionId: v.suggestionId } };
      if (v.decision === 'reject')
        return unwrap(await api.POST('/api/v1/assets/{id}/ai/suggestions/{suggestionId}/reject', { params }));
      const hasBody = v.body && Object.keys(v.body).length > 0;
      // The generated type marks both fields required (they carry server-side defaults); only the ones set are sent.
      const body = hasBody ? (v.body as { text: string | null; replace: boolean }) : null;
      return unwrap(await api.POST('/api/v1/assets/{id}/ai/suggestions/{suggestionId}/accept', { params, body }));
    },
    onSuccess: () => invalidateAsset(qc, assetId),
  });
}

/** Assets with translation suggestions waiting for me to review (assets I may edit), oldest first, paged. */
export function useAiReviews(opts: { enabled?: boolean; limit?: number } = {}) {
  const api = useApiClient();
  const { status } = useAuth();
  const { enabled = true, limit = 50 } = opts;
  return useInfiniteQuery({
    queryKey: ['ai', 'reviews', 'translation', limit],
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam }) =>
      unwrap(
        await api.GET('/api/v1/ai/reviews', { params: { query: { kind: 'translation', limit, cursor: pageParam } } }),
      ) as Page<AiReviewItem>,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    enabled: enabled && status === 'authenticated',
  });
}

/** How many assets wait for a translation review - any signed-in user may ask (it is 0 without edit rights). Feeds the nav badge. */
export function useReviewSummary(enabled = true) {
  const api = useApiClient();
  const { status } = useAuth();
  return useQuery({
    queryKey: ['ai', 'reviews', 'summary'],
    queryFn: async () => unwrap(await api.GET('/api/v1/ai/reviews/summary')) as ReviewSummary,
    enabled: enabled && status === 'authenticated',
    refetchInterval: 60_000,
    staleTime: 30_000,
  });
}
