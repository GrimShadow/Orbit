import { ApiError, unwrap, type components } from '@dam/sdk-js';
import { useInfiniteQuery, useMutation, useQuery } from '@tanstack/react-query';
import { useCallback, useState } from 'react';
import { useApiClient } from '../../api/apiClient';
import { useAuth } from '../../auth/AuthProvider';
import { exportFilename, filtersToQuery, type AuditFilters } from './auditFilters';

export type AuditEntry = components['schemas']['AuditEntryDto'];
export type AuditVerification = components['schemas']['AuditVerificationDto'];
export type ExportFormat = 'csv' | 'cef';

export const AUDIT_PAGE_SIZE = 200;

/** The audit log, newest first, paged by keyset cursor (`nextCursor` is the last chain position seen). The filters are
 * part of the query key, so changing one starts a fresh list. */
export function useAuditEntries(filters: AuditFilters) {
  const api = useApiClient();
  const { status } = useAuth();
  const query = filtersToQuery(filters);
  return useInfiniteQuery({
    queryKey: ['audit', 'list', query],
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam }) =>
      unwrap(await api.GET('/api/v1/audit', { params: { query: { ...query, limit: AUDIT_PAGE_SIZE, cursor: pageParam } } })),
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    enabled: status === 'authenticated',
    staleTime: 15_000,
  });
}

/** Distinct action names, for the action filter's suggestions. Best effort: the filter still works as free text. */
export function useAuditActions() {
  const api = useApiClient();
  const { status } = useAuth();
  return useQuery({
    queryKey: ['audit', 'actions'],
    queryFn: async () => unwrap(await api.GET('/api/v1/audit/actions')),
    enabled: status === 'authenticated',
    staleTime: 5 * 60_000,
  });
}

/** Recomputes the hash chain over a time range on the server. A mutation, not a query: it is O(n) and must only run
 * when a person asks. */
export function useVerifyAudit() {
  const api = useApiClient();
  return useMutation({
    mutationFn: async (range: { from: string; to?: string }): Promise<AuditVerification> =>
      unwrap(await api.GET('/api/v1/audit/verify', { params: { query: { from: range.from, to: range.to } } })),
  });
}

/** One entry by its chain position, or null when there is none (used to jump to the entry a verification flagged).
 * The list is newest first and its cursor is "the last position seen", so asking for positions below `chainNo + 1`
 * with a limit of one returns exactly that entry. */
export function useFetchEntryByChainNo() {
  const api = useApiClient();
  return useCallback(
    async (chainNo: number): Promise<AuditEntry | null> => {
      const page = unwrap(await api.GET('/api/v1/audit', { params: { query: { limit: 1, cursor: String(chainNo + 1) } } }));
      const entry = page.items[0];
      return entry && entry.chainNo === chainNo ? entry : null;
    },
    [api],
  );
}

/** The export endpoint streams a file and needs the bearer token, so a plain link cannot fetch it. */
export function exportUrl(format: ExportFormat, filters: AuditFilters): string {
  const params = new URLSearchParams({ format, ...filtersToQuery(filters) });
  return `/api/v1/audit/export?${params.toString()}`;
}

async function problemFrom(res: Response): Promise<ApiError> {
  let title = res.statusText;
  let detail: string | undefined;
  try {
    const body = (await res.json()) as { title?: string; detail?: string };
    title = body.title ?? title;
    detail = body.detail;
  } catch {
    /* not a problem document */
  }
  return new ApiError(res.status, title, detail);
}

/** Streams the response into memory chunk by chunk (so progress can be shown and the UI thread is never handed one
 * enormous buffer), then returns it as a Blob. */
async function readToBlob(res: Response, type: string, onProgress: (bytes: number) => void): Promise<Blob> {
  const reader = res.body?.getReader?.();
  if (!reader) {
    const blob = await res.blob();
    onProgress(blob.size);
    return blob;
  }
  const chunks: Uint8Array[] = [];
  let bytes = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    chunks.push(value);
    bytes += value.byteLength;
    onProgress(bytes);
  }
  return new Blob(chunks as BlobPart[], { type });
}

/** Hands a Blob to the browser as a download. */
export function saveBlob(blob: Blob, filename: string) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  a.rel = 'noopener';
  a.style.display = 'none';
  document.body.appendChild(a);
  a.click();
  a.remove();
  // Revoked on a later tick so the browser has started the download before the URL goes away.
  setTimeout(() => URL.revokeObjectURL(url), 10_000);
}

export interface ExportState {
  busy: boolean;
  format: ExportFormat | null;
  bytes: number;
}

/** Downloads the export for the current filters with an authenticated fetch. Resolves to the saved file's name and size;
 * rejects with an `ApiError` (or a network error) so the caller can toast. */
export function useAuditExport() {
  const { accessToken } = useAuth();
  const [state, setState] = useState<ExportState>({ busy: false, format: null, bytes: 0 });
  const run = useCallback(
    async (format: ExportFormat, filters: AuditFilters): Promise<{ filename: string; bytes: number }> => {
      setState({ busy: true, format, bytes: 0 });
      try {
        const res = await fetch(exportUrl(format, filters), {
          headers: accessToken ? { Authorization: `Bearer ${accessToken}` } : {},
        });
        if (!res.ok) throw await problemFrom(res);
        const type = format === 'csv' ? 'text/csv;charset=utf-8' : 'text/plain;charset=utf-8';
        const blob = await readToBlob(res, type, (bytes) => setState({ busy: true, format, bytes }));
        const filename = exportFilename(filters, format, new Date());
        saveBlob(blob, filename);
        return { filename, bytes: blob.size };
      } finally {
        setState({ busy: false, format: null, bytes: 0 });
      }
    },
    [accessToken],
  );
  return { state, run };
}
