import { ApiError, unwrap } from '@dam/sdk-js';
import { keepPreviousData, useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useApiClient } from '../../api/apiClient';
import { useAuth } from '../../auth/AuthProvider';
import type {
  ChapterDto,
  ChaptersDto,
  DocType,
  DocumentFilters,
  KnowledgeDocumentDetailDto,
  KnowledgeDocumentDto,
  KnowledgeDocumentInput,
  Page,
  ResolveDto,
  VinImportResultDto,
  VinRuleDto,
  VinRuleInput,
  VinTestDto,
} from './knowledgeTypes';

export const PAGE_SIZE = 25;

function useEnabled(enabled = true) {
  const { status } = useAuth();
  return enabled && status === 'authenticated';
}

const clean = (s: string) => (s.trim() === '' ? undefined : s.trim());

/** One page of documents matching the filters (empty filter = any); pages accumulate with "Load more". */
export function useKnowledgeDocuments(f: DocumentFilters) {
  const api = useApiClient();
  const enabled = useEnabled();
  return useInfiniteQuery({
    queryKey: ['knowledge', 'documents', f],
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam }) =>
      unwrap(
        await api.GET('/api/v1/knowledge/documents', {
          params: {
            query: {
              type: clean(f.type),
              model: clean(f.model),
              variant: clean(f.variant),
              year: /^\d{4}$/.test(f.year.trim()) ? Number(f.year.trim()) : undefined,
              lang: clean(f.lang),
              q: clean(f.q),
              cursor: pageParam,
              limit: PAGE_SIZE,
            },
          },
        }),
      ) as Page<KnowledgeDocumentDto>,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    placeholderData: keepPreviousData,
    enabled,
  });
}

/** The mapping of one asset, or null when it is not a knowledge document. NOTE for the contract: this asks the documents list
 * for `assetId` (one row per asset); ADR-0039 does not list that filter, so the API must accept it. */
export function useAssetKnowledge(assetId: string, enabled: boolean) {
  const api = useApiClient();
  const allowed = useEnabled(enabled);
  return useQuery({
    queryKey: ['knowledge', 'byAsset', assetId],
    queryFn: async () => {
      const page = unwrap(
        await api.GET('/api/v1/knowledge/documents', { params: { query: { assetId, limit: 1 } } }),
      ) as Page<KnowledgeDocumentDto>;
      return page.items.find((d) => d.assetId === assetId) ?? null;
    },
    enabled: allowed,
    staleTime: 30_000,
  });
}

export function useKnowledgeDocument(id: string | undefined) {
  const api = useApiClient();
  const enabled = useEnabled(!!id);
  return useQuery({
    queryKey: ['knowledge', 'document', id],
    queryFn: async () =>
      unwrap(await api.GET('/api/v1/knowledge/documents/{id}', { params: { path: { id: id! } } })) as KnowledgeDocumentDetailDto,
    enabled,
  });
}

function useInvalidateKnowledge() {
  const qc = useQueryClient();
  return () => void qc.invalidateQueries({ queryKey: ['knowledge'] });
}

export function useSaveKnowledgeDocument() {
  const api = useApiClient();
  const invalidate = useInvalidateKnowledge();
  return useMutation({
    mutationFn: async (v: { id?: string; body: KnowledgeDocumentInput }) => {
      if (v.id) {
        // the asset of a mapping never changes, so an update leaves `assetId` out (the generated type lists it as required)
        const rest = { ...v.body };
        delete rest.assetId;
        return unwrap(
          await api.PUT('/api/v1/knowledge/documents/{id}', {
            params: { path: { id: v.id } },
            body: rest as typeof rest & { assetId: null },
          }),
        ) as KnowledgeDocumentDto;
      }
      return unwrap(
        await api.POST('/api/v1/knowledge/documents', { body: { ...v.body, assetId: v.body.assetId ?? null } }),
      ) as KnowledgeDocumentDto;
    },
    onSuccess: invalidate,
  });
}

/** Removes the mapping only; the asset and its file are untouched. */
export function useDeleteKnowledgeDocument() {
  const api = useApiClient();
  const invalidate = useInvalidateKnowledge();
  return useMutation({
    mutationFn: async (id: string) => {
      unwrap(await api.DELETE('/api/v1/knowledge/documents/{id}', { params: { path: { id } } }));
    },
    onSuccess: invalidate,
  });
}

/** Replaces the chapters by hand (source becomes `manual`). */
export function useSaveChapters() {
  const api = useApiClient();
  const invalidate = useInvalidateKnowledge();
  return useMutation({
    mutationFn: async (v: { id: string; chapters: ChapterDto[] }) =>
      unwrap(
        await api.PUT('/api/v1/knowledge/documents/{id}/chapters', {
          params: { path: { id: v.id } },
          body: { chapters: v.chapters },
        }),
      ) as ChaptersDto | undefined,
    onSuccess: invalidate,
  });
}

/** Asks the worker to read the chapters again; `force` also replaces chapters entered by hand. */
export function useReindexChapters() {
  const api = useApiClient();
  const invalidate = useInvalidateKnowledge();
  return useMutation({
    mutationFn: async (v: { id: string; force: boolean }) => {
      unwrap(
        await api.POST('/api/v1/knowledge/documents/{id}/reindex', {
          params: { path: { id: v.id }, query: v.force ? { force: true } : {} },
        }),
      );
    },
    onSuccess: invalidate,
  });
}

/** Every rule of the tenant: the list is paged (up to 500 a page), so pages are followed until there are no more. */
export function useVinRules(enabled: boolean) {
  const api = useApiClient();
  const allowed = useEnabled(enabled);
  return useQuery({
    queryKey: ['knowledge', 'vin-rules'],
    queryFn: async () => {
      const all: VinRuleDto[] = [];
      let cursor: string | undefined;
      do {
        const page = unwrap(
          await api.GET('/api/v1/knowledge/vin-rules', { params: { query: { limit: 500, cursor } } }),
        ) as Page<VinRuleDto>;
        all.push(...page.items);
        cursor = page.nextCursor ?? undefined;
      } while (cursor);
      return all;
    },
    enabled: allowed,
  });
}

export function useSaveVinRule() {
  const api = useApiClient();
  const invalidate = useInvalidateKnowledge();
  return useMutation({
    mutationFn: async (v: { id?: string; body: VinRuleInput }) =>
      (v.id
        ? unwrap(await api.PUT('/api/v1/knowledge/vin-rules/{id}', { params: { path: { id: v.id } }, body: v.body }))
        : unwrap(await api.POST('/api/v1/knowledge/vin-rules', { body: v.body }))) as VinRuleDto,
    onSuccess: invalidate,
  });
}

export function useDeleteVinRule() {
  const api = useApiClient();
  const invalidate = useInvalidateKnowledge();
  return useMutation({
    mutationFn: async (id: string) => {
      unwrap(await api.DELETE('/api/v1/knowledge/vin-rules/{id}', { params: { path: { id } } }));
    },
    onSuccess: invalidate,
  });
}

/** Dry-runs a VIN against the rules: which one wins, and why each candidate matched or not. Writes nothing. */
export function useTestVin() {
  const api = useApiClient();
  return useMutation({
    mutationFn: async (vin: string) =>
      unwrap(await api.POST('/api/v1/knowledge/vin-rules/test', { body: { vin } })) as VinTestDto,
  });
}

/** Sends the CSV text as `text/csv`. `dryRun` validates only; `partial` imports the valid lines even when others fail. */
export function useImportVinRules() {
  const api = useApiClient();
  const invalidate = useInvalidateKnowledge();
  return useMutation({
    mutationFn: async (v: { csv: string; dryRun: boolean; partial: boolean }) =>
      unwrap(
        await api.POST('/api/v1/knowledge/vin-rules/import', {
          params: { query: { dryRun: v.dryRun || undefined, partial: v.partial || undefined } },
          body: v.csv,
          bodySerializer: (b) => b as string,
          headers: { 'Content-Type': 'text/csv' },
        }),
      ) as VinImportResultDto,
    onSuccess: (result) => {
      if (!result.dryRun) invalidate();
    },
  });
}

export interface ResolveParams {
  vin: string;
  type: DocType;
  lang: string;
}

/** What an app would be shown for this VIN, as the signed-in user. 404 and 422 are meaningful answers, not retried. */
export function useResolve(params: ResolveParams | null) {
  const api = useApiClient();
  const enabled = useEnabled(params !== null);
  return useQuery({
    queryKey: ['knowledge', 'resolve', params],
    queryFn: async () =>
      unwrap(
        await api.GET('/api/v1/knowledge/resolve', {
          params: { query: { vin: params!.vin, type: params!.type, lang: clean(params!.lang) } },
        }),
      ) as ResolveDto,
    enabled,
    retry: false,
    staleTime: 0,
    gcTime: 0,
  });
}

// ---- errors ---------------------------------------------------------------------------------------------------------

export const isForbidden = (e: unknown): boolean => e instanceof ApiError && e.status === 403;

/** The per-field messages of a problem+json 422 (`errors: { field: [messages] }`), flattened. */
export function problemMessages(e: unknown): string[] {
  if (!(e instanceof ApiError)) return [];
  const errors = e.problem?.errors;
  if (!errors || typeof errors !== 'object') return [];
  return Object.values(errors as Record<string, unknown>)
    .flatMap((v) => (Array.isArray(v) ? v : [v]))
    .filter((m): m is string => typeof m === 'string' && m.trim() !== '');
}

/** The server's own words for a failed write: field messages, else the detail, else the title. */
export function errorReason(e: unknown): string {
  if (!(e instanceof ApiError)) return e instanceof Error ? e.message : '';
  const fields = problemMessages(e);
  return fields.length > 0 ? fields.join(' ') : e.detail || e.title;
}

export type ResolveFailure = 'malformed' | 'unknownVin' | 'noDocument' | 'forbidden' | 'failed';

/** Which of the resolver's plain-language messages an error calls for. The server tags its answers with a stable `code`
 * (`knowledge.invalid_vin`, `knowledge.no_rule`, `knowledge.no_document`); without one the status and wording decide. */
export function classifyResolveError(e: unknown): ResolveFailure {
  if (!(e instanceof ApiError)) return 'failed';
  const code = typeof e.problem?.code === 'string' ? e.problem.code : '';
  if (code === 'knowledge.invalid_vin') return 'malformed';
  if (code === 'knowledge.no_rule') return 'unknownVin';
  if (code === 'knowledge.no_document') return 'noDocument';
  if (e.status === 422) return 'malformed';
  if (e.status === 403) return 'forbidden';
  if (e.status === 404) return /\brules?\b/i.test(`${e.title} ${e.detail ?? ''}`) ? 'unknownVin' : 'noDocument';
  return 'failed';
}
