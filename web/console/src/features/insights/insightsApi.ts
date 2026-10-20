import { unwrap } from '@dam/sdk-js';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useApiClient } from '../../api/apiClient';
import { useAuth } from '../../auth/AuthProvider';
import type {
  AssetAnalyticsDto,
  KpiDto,
  ReportDto,
  ReportInfoDto,
  ReportScheduleDto,
  ReportScheduleInput,
} from './insightsTypes';

// The generated schema types statuses and modes as plain strings and report rows as loose objects; the console casts each
// response to the narrower types in insightsTypes.ts.
function useEnabled(enabled = true) {
  const { status } = useAuth();
  return enabled && status === 'authenticated';
}

/** The four KPIs for the last `days` UTC days, with the same figures for the equal period before. */
export function useKpis(days: number) {
  const api = useApiClient();
  const enabled = useEnabled();
  return useQuery({
    queryKey: ['insights', 'kpis', days],
    queryFn: async () => unwrap(await api.GET('/api/v1/analytics/kpis', { params: { query: { days } } })) as KpiDto,
    enabled,
    staleTime: 60_000,
  });
}

export function useReportList() {
  const api = useApiClient();
  const enabled = useEnabled();
  return useQuery({
    queryKey: ['insights', 'reports'],
    queryFn: async () => unwrap(await api.GET('/api/v1/analytics/reports')) as ReportInfoDto[],
    enabled,
    staleTime: 5 * 60_000,
  });
}

export function useReport(name: string | undefined, days: number) {
  const api = useApiClient();
  const enabled = useEnabled(!!name);
  return useQuery({
    queryKey: ['insights', 'report', name, days],
    queryFn: async () =>
      unwrap(
        await api.GET('/api/v1/analytics/reports/{name}', { params: { path: { name: name! }, query: { days, format: 'json' } } }),
      ) as ReportDto,
    enabled,
    staleTime: 60_000,
  });
}

/** Fetches a report as CSV through the authenticated client. A blob, not text: the server's UTF-8 byte-order mark (which
 * Excel needs to read Hindi titles) is dropped by a text decode. */
export function useDownloadReportCsv() {
  const api = useApiClient();
  return useMutation({
    mutationFn: async (v: { name: string; days: number }) =>
      unwrap(
        await api.GET('/api/v1/analytics/reports/{name}', {
          params: { path: { name: v.name }, query: { days: v.days, format: 'csv' } },
          parseAs: 'blob',
        }),
      ) as Blob,
  });
}

export function useSchedules(enabled: boolean) {
  const api = useApiClient();
  const allowed = useEnabled(enabled);
  return useQuery({
    queryKey: ['insights', 'schedules'],
    queryFn: async () => unwrap(await api.GET('/api/v1/analytics/report-schedules')) as ReportScheduleDto[],
    enabled: allowed,
    staleTime: 30_000,
  });
}

export function useSaveSchedule() {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (v: { id?: string; body: ReportScheduleInput }) =>
      unwrap(
        v.id
          ? await api.PUT('/api/v1/analytics/report-schedules/{id}', { params: { path: { id: v.id } }, body: v.body })
          : await api.POST('/api/v1/analytics/report-schedules', { body: v.body }),
      ) as ReportScheduleDto,
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['insights', 'schedules'] }),
  });
}

export function useDeleteSchedule() {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) =>
      unwrap(await api.DELETE('/api/v1/analytics/report-schedules/{id}', { params: { path: { id } } })) as unknown,
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['insights', 'schedules'] }),
  });
}

export function useSendScheduleNow() {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) =>
      unwrap(await api.POST('/api/v1/analytics/report-schedules/{id}/send-now', { params: { path: { id } } })) as unknown,
    onSettled: () => void qc.invalidateQueries({ queryKey: ['insights', 'schedules'] }),
  });
}

/** Per-asset usage for the asset page. `enabled` lets the caller hold the request back until the page has settled. */
export function useAssetAnalytics(assetId: string, days: number, enabled: boolean) {
  const api = useApiClient();
  const allowed = useEnabled(enabled);
  return useQuery({
    queryKey: ['insights', 'asset', assetId, days],
    queryFn: async () =>
      unwrap(
        await api.GET('/api/v1/analytics/assets/{id}', { params: { path: { id: assetId }, query: { days } } }),
      ) as AssetAnalyticsDto,
    enabled: allowed,
    staleTime: 60_000,
    retry: false,
  });
}
