import type { TFunction } from 'i18next';
import { formatBytes } from '../../lib/format';
import type { DistributionSegment } from '@dam/ui-kit';
import { formatHours, formatRatio } from './kpiFormat';
import type { ReportColumn, ReportDto } from './insightsTypes';

const NUMERIC = new Set([
  'int',
  'integer',
  'long',
  'number',
  'count',
  'decimal',
  'double',
  'float',
  'bytes',
  'hours',
  'percent',
  'ratio',
  'rate',
]);

/** Whether a column holds a figure (right-aligned in the table). */
export function isNumericColumn(col: ReportColumn): boolean {
  return NUMERIC.has(col.type.toLowerCase());
}

/** A row's value for a column: rows are objects keyed by column key. */
export function cellOf(row: ReportDto['rows'][number], col: ReportColumn): unknown {
  return row[col.key];
}

/** A report cell as text, by the column's declared type. Unknown types and unreadable values fall back to plain text, so an
 * unexpected type degrades to something readable instead of breaking the table. */
export function formatCell(value: unknown, type: string, t: TFunction, locale: string): string {
  if (value === null || value === undefined || value === '') return '';
  const kind = type.toLowerCase();
  const num = typeof value === 'number' ? value : typeof value === 'string' && value.trim() !== '' ? Number(value) : NaN;
  switch (kind) {
    case 'bytes':
      return Number.isFinite(num) ? formatBytes(num) : String(value);
    case 'count':
      return Number.isFinite(num) ? new Intl.NumberFormat(locale).format(num) : String(value);
    case 'number':
      return Number.isFinite(num) ? new Intl.NumberFormat(locale, { maximumFractionDigits: 2 }).format(num) : String(value);
    case 'hours':
      return Number.isFinite(num) ? formatHours(num, t, locale) : String(value);
    case 'percent':
      // A ratio from 0 to 1, like the KPI figures.
      return Number.isFinite(num) ? formatRatio(num, locale) : String(value);
    case 'date':
    case 'datetime': {
      const d = new Date(String(value));
      if (Number.isNaN(d.getTime())) return String(value);
      return new Intl.DateTimeFormat(
        locale,
        kind === 'date' ? { dateStyle: 'medium', timeZone: 'UTC' } : { dateStyle: 'medium', timeStyle: 'short' },
      ).format(d);
    }
    default:
      return String(value);
  }
}

const MAX_SEGMENTS = 8;
const PATH_SEPARATOR = ' / ';

/** The folder rows of the `storage` report as bars. The report lists folders and asset types in one table (`group` says
 * which), each folder with its own figures and its subtree totals; a folder's path is its name ("Brand / Cars"). The bars
 * are the top-level folders sized by their subtree bytes (a subtree already includes its children, so counting the children
 * as well would double the bars). Returns nothing when the report is not shaped that way, and the table alone then carries it. */
export function storageSegments(report: ReportDto): DistributionSegment[] {
  const index = (key: string) => report.columns.findIndex((c) => c.key === key);
  const groupIdx = index('group');
  const nameIdx = index('name');
  const bytesIdx = index('subtreeBytes');
  if (groupIdx < 0 || nameIdx < 0 || bytesIdx < 0) return [];
  return report.rows
    .map((row) => ({
      group: String(cellOf(row, report.columns[groupIdx]!) ?? ''),
      name: String(cellOf(row, report.columns[nameIdx]!) ?? ''),
      bytes: Number(cellOf(row, report.columns[bytesIdx]!)),
    }))
    .filter(
      (r) => r.group === 'folder' && r.name !== '' && !r.name.includes(PATH_SEPARATOR) && Number.isFinite(r.bytes) && r.bytes > 0,
    )
    .sort((a, b) => b.bytes - a.bytes)
    .slice(0, MAX_SEGMENTS)
    .map((r, i) => ({ label: r.name, value: r.bytes, display: formatBytes(r.bytes), series: i }));
}
