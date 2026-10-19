/** The audit screen's filters, their round trip through the URL, and the small pure helpers around them (presets,
 * date-input conversion, the default verification range, export file names). Everything here is side-effect free so it
 * can be unit tested without a DOM. */

export interface AuditFilters {
  /** ISO instants (UTC). Absent means open-ended. */
  from?: string;
  to?: string;
  /** The recorded identity subject or the Orbit user id (the API accepts either). */
  actor?: string;
  /** An action name or a prefix such as `asset.`. */
  action?: string;
  entityType?: string;
  /** A UUID. */
  entityId?: string;
  /** Free text over action, entity and actor name/email. */
  q?: string;
}

export type FilterKey = keyof AuditFilters;
export const FILTER_KEYS: readonly FilterKey[] = ['from', 'to', 'actor', 'action', 'entityType', 'entityId', 'q'];

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const MAX_TEXT = 200;

export const isGuid = (value: string): boolean => GUID.test(value.trim());

function cleanDate(value: string | null | undefined): string | undefined {
  if (!value) return undefined;
  const ms = Date.parse(value);
  return Number.isNaN(ms) ? undefined : new Date(ms).toISOString();
}

function cleanText(value: string | null | undefined): string | undefined {
  const v = value?.trim().slice(0, MAX_TEXT);
  return v ? v : undefined;
}

/** Reads the filters from a query string. Unparseable dates and a malformed entity id are dropped rather than sent to
 * the API, so a mangled link degrades to "no filter" instead of an error page. */
export function decodeFilters(params: URLSearchParams): AuditFilters {
  const entityId = cleanText(params.get('entityId'));
  const out: AuditFilters = {
    from: cleanDate(params.get('from')),
    to: cleanDate(params.get('to')),
    actor: cleanText(params.get('actor')),
    action: cleanText(params.get('action')),
    entityType: cleanText(params.get('entityType')),
    entityId: entityId && isGuid(entityId) ? entityId.toLowerCase() : undefined,
    q: cleanText(params.get('q')),
  };
  for (const key of FILTER_KEYS) if (out[key] === undefined) delete out[key];
  return out;
}

/** A copy of `params` with the patched filters set (a blank or undefined value removes the key). Keys that are not
 * audit filters are left alone. */
export function patchParams(params: URLSearchParams, patch: Partial<Record<FilterKey, string | undefined>>): URLSearchParams {
  const next = new URLSearchParams(params);
  for (const [key, value] of Object.entries(patch)) {
    const v = value?.trim();
    if (v) next.set(key, v);
    else next.delete(key);
  }
  return next;
}

/** The query the list/export/verify endpoints take: only the filters that are set. */
export function filtersToQuery(filters: AuditFilters): Partial<Record<FilterKey, string>> {
  const out: Partial<Record<FilterKey, string>> = {};
  for (const key of FILTER_KEYS) {
    const v = filters[key];
    if (v) out[key] = v;
  }
  return out;
}

export const activeFilterKeys = (filters: AuditFilters): FilterKey[] => FILTER_KEYS.filter((k) => Boolean(filters[k]));
export const hasFilters = (filters: AuditFilters): boolean => activeFilterKeys(filters).length > 0;

export type Preset = 'hour' | 'day' | 'week' | 'month';
const PRESET_MS: Record<Preset, number> = {
  hour: 3_600_000,
  day: 86_400_000,
  week: 7 * 86_400_000,
  month: 30 * 86_400_000,
};

/** "Last hour" etc: from now minus the span, open-ended at the top so entries written while the page is open still match. */
export function presetRange(preset: Preset, now: Date): { from: string; to: undefined } {
  return { from: new Date(now.getTime() - PRESET_MS[preset]).toISOString(), to: undefined };
}

const two = (n: number) => String(n).padStart(2, '0');

/** An ISO instant as the local `YYYY-MM-DDTHH:mm` a `datetime-local` input wants. */
export function toLocalInput(iso: string | undefined): string {
  if (!iso) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  return `${d.getFullYear()}-${two(d.getMonth() + 1)}-${two(d.getDate())}T${two(d.getHours())}:${two(d.getMinutes())}`;
}

/** A `datetime-local` value back to an ISO instant. The input only has minute precision, so an end bound is taken to be
 * the end of that minute (otherwise "to 15:25" would exclude an entry written at 15:25:21). */
export function fromLocalInput(value: string, edge: 'start' | 'end'): string | undefined {
  if (!value) return undefined;
  const ms = new Date(value).getTime();
  if (Number.isNaN(ms)) return undefined;
  return new Date(edge === 'end' ? ms + 59_999 : ms).toISOString();
}

export const rangeIsReversed = (f: AuditFilters): boolean => Boolean(f.from && f.to && Date.parse(f.from) > Date.parse(f.to));

/** The time range an integrity check covers: the filters' range, or the last 30 days when none is set. */
export function defaultVerifyRange(filters: AuditFilters, now: Date): { from: string; to?: string } {
  return { from: filters.from ?? presetRange('month', now).from, to: filters.to };
}

const stamp = (iso: string | undefined, fallback: string): string => {
  if (!iso) return fallback;
  const d = new Date(iso);
  return `${d.getFullYear()}${two(d.getMonth() + 1)}${two(d.getDate())}`;
};

/** `audit-<from>-<to>.csv|cef`; an open start reads `start`, an open end is today. */
export function exportFilename(filters: AuditFilters, format: 'csv' | 'cef', now: Date): string {
  return `audit-${stamp(filters.from, 'start')}-${stamp(filters.to, stamp(now.toISOString(), 'now'))}.${format}`;
}
