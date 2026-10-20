// Response shapes of the Step 2.7 analytics API, written by hand from docs/adr/0037 and src/Dam.Shared/Content/AnalyticsDtos.cs
// because the generated schema (web/sdk-js/src/schema.ts) did not yet contain these routes. Once it does, replace each type
// here with `components['schemas'][...]` (see insightsApi.ts, which holds the one cast to delete).
//
// Conventions from the ADR: every ratio (`rate`, `attainment`, `firstPassRate`) is a fraction from 0 to 1, not a percentage;
// hours are decimal hours; a ratio or hours figure is `null` when there is nothing to measure, never 0.

export interface KpiPeriod {
  /** First UTC day, `yyyy-MM-dd`. */
  from: string;
  /** Last UTC day (today for a KPI request), `yyyy-MM-dd`. */
  to: string;
  days: number;
}

export interface PublishingEffortMetrics {
  publishedCount: number;
  medianHours: number | null;
  p90Hours: number | null;
  firstPassRate: number | null;
}
export interface DuplicateMetrics {
  uploads: number;
  duplicatesPrevented: number;
  rate: number | null;
  bytesNotStored: number;
  duplicateAssetsInLibrary: number;
}
export interface ReuseMetrics {
  usedAssets: number;
  reusedAssets: number;
  rate: number | null;
  avgUsesPerUsedAsset: number | null;
  multiChannelAssets: number;
}
export interface WorkflowSlaMetrics {
  decided: number;
  onTime: number;
  attainment: number | null;
  breached: number;
  /** A snapshot of right now, so `null` in the previous period's figures. */
  overdueOpen: number | null;
}

export interface KpiDto {
  period: KpiPeriod;
  previous: { from: string; to: string };
  generatedAt: string;
  publishingEffort: PublishingEffortMetrics & { previous: PublishingEffortMetrics };
  duplicates: DuplicateMetrics & { previous: DuplicateMetrics };
  reuse: ReuseMetrics & { previous: ReuseMetrics };
  workflowSla: WorkflowSlaMetrics & { previous: WorkflowSlaMetrics };
}

export interface ReportInfoDto {
  name: string;
  title: string;
  description: string;
  /** Rows name individual assets, so only assets the reader may see appear. */
  namesAssets: boolean;
  defaultDays: number;
  /** Longest period; for `unused-assets` the longest idle window (3650). */
  maxDays: number;
}

export interface ReportColumn {
  key: string;
  label: string;
  /** `text`, `count`, `number`, `percent` (a ratio from 0 to 1), `bytes`, `hours` or `datetime` (ISO 8601). */
  type: string;
}

export interface ReportDto {
  name: string;
  title: string;
  generatedAt: string;
  period: KpiPeriod;
  columns: ReportColumn[];
  /** One object per row, keyed by column key; a value is a string, a number or null. */
  rows: Record<string, unknown>[];
  /** The report has more rows than are listed (top and unused assets are capped). */
  truncated: boolean;
}

export type ScheduleFrequency = 'daily' | 'weekly' | 'monthly';

export interface ReportScheduleDto {
  id: string;
  report: string;
  days: number;
  frequency: ScheduleFrequency;
  hourUtc: number;
  /** Weekly only: 1 (Monday) to 7 (Sunday). */
  dayOfWeek: number | null;
  /** Monthly only: 1 to 31; a day past the end of a short month means its last day. */
  dayOfMonth: number | null;
  recipientUserIds: string[];
  enabled: boolean;
  lastSentAt: string | null;
  lastError: string | null;
  nextRunAt: string | null;
  createdAt: string;
}

/** Body of POST and PUT /analytics/report-schedules. */
export interface ReportScheduleInput {
  report: string;
  days: number;
  frequency: ScheduleFrequency;
  hourUtc: number;
  dayOfWeek: number | null;
  dayOfMonth: number | null;
  recipientUserIds: string[];
  enabled: boolean;
}

export interface UsageDay {
  date: string;
  count: number;
}
export interface UsageByType {
  eventType: string;
  count: number;
}
export interface UsageByChannel {
  /** Uses with no channel are reported as `unspecified`. */
  channel: string;
  count: number;
}
export interface UsageRecentEvent {
  id: string;
  actorId: string | null;
  eventType: string;
  channel: string | null;
  occurredAt: string;
}

/** GET /analytics/assets/{id}?days. */
export interface AssetAnalyticsDto {
  assetId: string;
  title: string;
  type: string;
  period: KpiPeriod;
  /** Uses in the period. */
  total: number;
  previousTotal: number;
  allTimeTotal: number;
  /** First use ever; `null` when never used. */
  firstUsedAt: string | null;
  lastUsedAt: string | null;
  /** One entry per UTC day of the period, zero-filled. */
  byDay: UsageDay[];
  byType: UsageByType[];
  byChannel: UsageByChannel[];
  /** The 20 most recent uses, whatever the period. */
  recent: UsageRecentEvent[];
}
