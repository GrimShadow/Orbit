// Test-only scaffolding for Step 2.7: fixtures for the analytics API and a small in-memory backend behind the fetch mock.
// Nothing in the app imports this.
import { json, mockApi, type Call } from '../workflow/workflowTestKit';
import type { AssetAnalyticsDto, KpiDto, ReportDto, ReportInfoDto, ReportScheduleDto } from './insightsTypes';

export { json, type Call };

export const READER = ['assets.read', 'analytics.read'];
export const MANAGER = ['assets.read', 'analytics.read', 'analytics.manage'];

export const kpis = (over: Partial<KpiDto> = {}): KpiDto => ({
  period: { from: '2026-09-21', to: '2026-10-20', days: 30 },
  previous: { from: '2026-08-22', to: '2026-09-20' },
  generatedAt: '2026-10-20T10:00:00Z',
  publishingEffort: {
    publishedCount: 12,
    medianHours: 5.5,
    p90Hours: 30,
    firstPassRate: 0.75,
    previous: { publishedCount: 10, medianHours: 11, p90Hours: 40, firstPassRate: 0.6 },
  },
  duplicates: {
    uploads: 80,
    duplicatesPrevented: 12,
    rate: 0.15,
    bytesNotStored: 5 * 1024 * 1024,
    duplicateAssetsInLibrary: 3,
    previous: { uploads: 60, duplicatesPrevented: 6, rate: 0.1, bytesNotStored: 1024, duplicateAssetsInLibrary: 5 },
  },
  reuse: {
    usedAssets: 40,
    reusedAssets: 26,
    rate: 0.65,
    avgUsesPerUsedAsset: 3.25,
    multiChannelAssets: 9,
    previous: { usedAssets: 30, reusedAssets: 24, rate: 0.8, avgUsesPerUsedAsset: 2.5, multiChannelAssets: 7 },
  },
  workflowSla: {
    decided: 20,
    onTime: 18,
    attainment: 0.9,
    breached: 2,
    overdueOpen: 1,
    previous: { decided: 10, onTime: 10, attainment: 1, breached: 0, overdueOpen: null },
  },
  ...over,
});

/** A period with nothing to measure: ratios and hours are null, counts are zero. */
export const emptyKpis = (): KpiDto => {
  const base = kpis();
  return kpis({
    publishingEffort: {
      publishedCount: 0,
      medianHours: null,
      p90Hours: null,
      firstPassRate: null,
      previous: { publishedCount: 0, medianHours: null, p90Hours: null, firstPassRate: null },
    },
    duplicates: {
      uploads: 0,
      duplicatesPrevented: 0,
      rate: null,
      bytesNotStored: 0,
      duplicateAssetsInLibrary: 0,
      previous: base.duplicates.previous,
    },
    reuse: {
      usedAssets: 0,
      reusedAssets: 0,
      rate: null,
      avgUsesPerUsedAsset: null,
      multiChannelAssets: 0,
      previous: { ...base.reuse.previous, rate: null },
    },
    workflowSla: {
      decided: 0,
      onTime: 0,
      attainment: null,
      breached: 0,
      overdueOpen: 0,
      previous: { decided: 0, onTime: 0, attainment: null, breached: 0, overdueOpen: null },
    },
  });
};

export const REPORT_LIST: ReportInfoDto[] = [
  { name: 'top-assets', title: 'Most used assets', description: '', namesAssets: true, defaultDays: 30, maxDays: 365 },
  {
    name: 'storage',
    title: 'Storage',
    description: 'Storage by folder and by asset type.',
    namesAssets: false,
    defaultDays: 30,
    maxDays: 365,
  },
  { name: 'kpis', title: 'Key results', description: 'The four key results.', namesAssets: false, defaultDays: 30, maxDays: 365 },
  {
    name: 'unused-assets',
    title: 'Unused assets',
    description: 'Assets not used for a while.',
    namesAssets: true,
    defaultDays: 30,
    maxDays: 3650,
  },
];

export const topAssets = (): ReportDto => ({
  name: 'top-assets',
  title: 'Most used assets',
  generatedAt: '2026-10-20T10:00:00Z',
  period: { from: '2026-09-21', to: '2026-10-20', days: 30 },
  columns: [
    { key: 'title', label: 'Asset', type: 'text' },
    { key: 'uses', label: 'Uses', type: 'count' },
    { key: 'lastUsedAt', label: 'Last used', type: 'datetime' },
    { key: 'sizeBytes', label: 'Size', type: 'bytes' },
    { key: 'share', label: 'Share', type: 'percent' },
  ],
  rows: [
    { title: 'Acme spring hero', uses: 1234, lastUsedAt: '2026-10-19T09:30:00Z', sizeBytes: 2048, share: 0.425 },
    { title: 'Acme summer teaser', uses: 12, lastUsedAt: null, sizeBytes: 3 * 1024 * 1024, share: 0.01 },
  ],
  truncated: false,
});

export const storage = (): ReportDto => ({
  name: 'storage',
  title: 'Storage',
  generatedAt: '2026-10-20T10:00:00Z',
  period: { from: '2026-10-20', to: '2026-10-20', days: 1 },
  columns: [
    { key: 'group', label: 'Group', type: 'text' },
    { key: 'name', label: 'Name', type: 'text' },
    { key: 'assets', label: 'Assets', type: 'count' },
    { key: 'bytes', label: 'Size', type: 'bytes' },
    { key: 'subtreeAssets', label: 'Assets with subfolders', type: 'count' },
    { key: 'subtreeBytes', label: 'Size with subfolders', type: 'bytes' },
  ],
  rows: [
    {
      group: 'folder',
      name: 'Acme Campaigns',
      assets: 30,
      bytes: 200 * 1024 * 1024,
      subtreeAssets: 40,
      subtreeBytes: 300 * 1024 * 1024,
    },
    {
      group: 'folder',
      name: 'Acme Campaigns / Spring',
      assets: 10,
      bytes: 100 * 1024 * 1024,
      subtreeAssets: 10,
      subtreeBytes: 100 * 1024 * 1024,
    },
    {
      group: 'folder',
      name: 'Acme Brand',
      assets: 5,
      bytes: 100 * 1024 * 1024,
      subtreeAssets: 5,
      subtreeBytes: 100 * 1024 * 1024,
    },
    { group: 'type', name: 'image', assets: 45, bytes: 400 * 1024 * 1024, subtreeAssets: 45, subtreeBytes: 400 * 1024 * 1024 },
  ],
  truncated: false,
});

export const schedule = (over: Partial<ReportScheduleDto> = {}): ReportScheduleDto => ({
  id: 's1',
  report: 'top-assets',
  days: 30,
  frequency: 'weekly',
  hourUtc: 8,
  dayOfWeek: 1,
  dayOfMonth: null,
  recipientUserIds: ['u1', 'u2'],
  enabled: true,
  lastSentAt: '2026-10-12T08:00:00Z',
  lastError: null,
  nextRunAt: '2026-10-26T08:00:00Z',
  createdAt: '2026-09-01T00:00:00Z',
  ...over,
});

export const user = (id: string, displayName: string, email = `${displayName.split(' ')[0]!.toLowerCase()}@acme.test`) => ({
  id,
  externalSubject: id,
  email,
  displayName,
  status: 'active',
  attributes: {},
  roles: [],
  groups: [],
  createdAt: '2026-01-01T00:00:00Z',
});

export const USERS = [user('u1', 'Asha Rao'), user('u2', 'Ben Okafor'), user('u3', 'Chloe Zhang')];

export const assetAnalytics = (over: Partial<AssetAnalyticsDto> = {}): AssetAnalyticsDto => {
  const byDay = Array.from({ length: 30 }, (_, i) => ({
    date: new Date(Date.UTC(2026, 8, 21 + i)).toISOString().slice(0, 10),
    count: 0,
  }));
  byDay[28]!.count = 9;
  byDay[29]!.count = 3;
  return {
    assetId: 'a1',
    title: 'Acme spring hero',
    type: 'image',
    period: { from: '2026-09-21', to: '2026-10-20', days: 30 },
    total: 12,
    previousTotal: 4,
    allTimeTotal: 40,
    firstUsedAt: '2026-03-02T00:00:00Z',
    lastUsedAt: '2026-10-20T08:00:00Z',
    byDay,
    byType: [
      { eventType: 'download', count: 8 },
      { eventType: 'share', count: 4 },
    ],
    byChannel: [
      { channel: 'console', count: 10 },
      { channel: 'unspecified', count: 2 },
    ],
    recent: Array.from({ length: 7 }, (_, i) => ({
      id: `e${i}`,
      actorId: 'u1',
      eventType: i % 2 ? 'share' : 'download',
      channel: i === 2 ? null : 'console',
      occurredAt: new Date(Date.now() - (i + 1) * 3_600_000).toISOString(),
    })),
    ...over,
  };
};

export interface World {
  kpis?: KpiDto | Response;
  reports?: ReportInfoDto[] | Response;
  report?: Record<string, ReportDto | Response>;
  csv?: Response;
  schedules: ReportScheduleDto[];
  users?: ReturnType<typeof user>[];
  asset?: AssetAnalyticsDto | Response;
  /** Every analytics / user call, for assertions. */
  calls: Call[];
  /** The next schedule write answers with this. */
  refuse?: Response;
  sendNow?: Response;
}

export const newWorld = (over: Partial<World> = {}): World => ({ schedules: [], calls: [], ...over });

const asResponse = (v: unknown) => (v instanceof Response ? v : json(v));

/** The analytics routes (and the user directory) as an in-memory service. */
export function install(w: World, permissions = READER, extra?: (c: Call) => Response | Promise<Response> | undefined) {
  let nextId = 100;
  return mockApi(
    (c) => {
      const custom = extra?.(c);
      if (custom) return custom;
      const p = c.pathname;
      if (p === '/api/v1/users/directory') {
        w.calls.push(c);
        const q = (c.search.get('q') ?? '').toLowerCase();
        const all = w.users ?? USERS;
        return json({
          items: all
            .filter((u) => !q || u.displayName.toLowerCase().includes(q) || u.email.includes(q))
            .map(({ id, displayName, email }) => ({ id, displayName, email })),
          nextCursor: null,
        });
      }
      if (!p.startsWith('/api/v1/analytics/')) return undefined;
      w.calls.push(c);
      if (p === '/api/v1/analytics/kpis') return asResponse(w.kpis ?? kpis());
      if (p === '/api/v1/analytics/reports') return asResponse(w.reports ?? REPORT_LIST);
      const report = /^\/api\/v1\/analytics\/reports\/([^/]+)$/.exec(p);
      if (report) {
        if (c.search.get('format') === 'csv')
          return (
            w.csv ??
            new Response('﻿Asset,Uses\r\nAcme spring hero,1234\r\n', { status: 200, headers: { 'Content-Type': 'text/csv' } })
          );
        const r = w.report?.[report[1]!] ?? (report[1] === 'storage' ? storage() : topAssets());
        return asResponse(r);
      }
      if (/^\/api\/v1\/analytics\/assets\/[^/]+$/.test(p)) return asResponse(w.asset ?? assetAnalytics());
      if (p === '/api/v1/analytics/overview') return json({ title: 'x', status: 500 }, 500);
      if (p === '/api/v1/analytics/report-schedules' || p.startsWith('/api/v1/analytics/report-schedules/')) {
        if (c.method === 'GET') return json(w.schedules);
        if (w.refuse) {
          const r = w.refuse;
          w.refuse = undefined;
          return r;
        }
        const id = p.split('/')[5];
        if (p.endsWith('/send-now')) return w.sendNow ?? new Response(null, { status: 202 });
        if (c.method === 'POST')
          return c.json().then((b) => {
            const created = {
              ...schedule(),
              ...(b as object),
              id: `n${nextId++}`,
              lastSentAt: null,
              lastError: null,
            } as ReportScheduleDto;
            w.schedules = [...w.schedules, created];
            return json(created, 201);
          });
        if (c.method === 'PUT')
          return c.json().then((b) => {
            w.schedules = w.schedules.map((s) => (s.id === id ? { ...s, ...(b as object) } : s));
            return json(w.schedules.find((s) => s.id === id));
          });
        if (c.method === 'DELETE') {
          w.schedules = w.schedules.filter((s) => s.id !== id);
          return new Response(null, { status: 204 });
        }
      }
      return undefined;
    },
    { permissions },
  );
}

export const callsTo = (w: World, pathname: string, method = 'GET') =>
  w.calls.filter((c) => c.pathname === pathname && c.method === method);
