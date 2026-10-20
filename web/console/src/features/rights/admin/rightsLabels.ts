import type { TFunction } from 'i18next';
import type { BadgeTone } from '../../../ui';
import { LICENCE_TYPES, type Rights, type RightsStatus } from '../rightsApi';

export const STATUSES: readonly RightsStatus[] = ['valid', 'expiring', 'expired', 'not_yet_valid'];

/** How long before the end of a licence it counts as "expiring" (the server's rule; used only to preview a status). */
export const EXPIRING_DAYS = 30;

export const STATUS_TONE: Record<RightsStatus, BadgeTone> = {
  valid: 'success',
  expiring: 'warning',
  expired: 'danger',
  not_yet_valid: 'info',
};

export const isStatus = (s: string | null | undefined): s is RightsStatus => STATUSES.includes(s as RightsStatus);

/** An unknown status string from a newer server is shown as "valid" rather than breaking the page. */
export const asStatus = (s: string): RightsStatus => (isStatus(s) ? s : 'valid');

export function licenceLabel(t: TFunction, type: string): string {
  return (LICENCE_TYPES as readonly string[]).includes(type) ? t(`rights.licence.${type}`) : type;
}

export const licenceOptions = (t: TFunction) => LICENCE_TYPES.map((v) => ({ value: v, label: t(`rights.licence.${v}`) }));

export function statusLabel(t: TFunction, status: RightsStatus, daysRemaining: number | null): string {
  if (status === 'expiring') {
    if (daysRemaining === 0) return t('rights.status.expiringToday');
    return t('rights.status.expiring', { count: daysRemaining ?? 0 });
  }
  return t(`rights.status.${status}`);
}

/** Today as a UTC calendar date (`YYYY-MM-DD`), which is the day the server judges validity by. */
export const todayUtc = (now: Date = new Date()) => now.toISOString().slice(0, 10);

const DAY_MS = 86_400_000;
const dayNumber = (iso: string) => Math.floor(Date.parse(`${iso}T00:00:00Z`) / DAY_MS);

/** The status and days left a licence with these dates has on `today` - the server's rules, so the form can preview
 * the outcome while someone is still typing the dates. Empty or unparseable dates are open ends. */
export function previewStatus(
  validFrom: string | null | undefined,
  validTo: string | null | undefined,
  today: string = todayUtc(),
): { status: RightsStatus; daysRemaining: number | null } {
  const from = validFrom && !Number.isNaN(Date.parse(validFrom)) ? validFrom : null;
  const to = validTo && !Number.isNaN(Date.parse(validTo)) ? validTo : null;
  const daysRemaining = to ? dayNumber(to) - dayNumber(today) : null;
  if (from && today < from) return { status: 'not_yet_valid', daysRemaining };
  if (daysRemaining !== null && daysRemaining < 0) return { status: 'expired', daysRemaining };
  if (daysRemaining !== null && daysRemaining <= EXPIRING_DAYS) return { status: 'expiring', daysRemaining };
  return { status: 'valid', daysRemaining };
}

/** A calendar date (`2026-10-19`) in the reader's language. It has no time or zone, so it is formatted in UTC. */
export function formatDate(iso: string, locale: string): string {
  const d = new Date(`${iso}T00:00:00Z`);
  if (Number.isNaN(d.getTime())) return iso;
  return new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeZone: 'UTC' }).format(d);
}

/** "1 Jan 2026 - 31 Dec 2026", "From ...", "Until ..." or "No end date" - whichever ends are set. */
export function validityText(t: TFunction, locale: string, from: string | null, to: string | null): string {
  if (from && to) return t('rights.validity.between', { from: formatDate(from, locale), to: formatDate(to, locale) });
  if (from) return t('rights.validity.from', { from: formatDate(from, locale) });
  if (to) return t('rights.validity.until', { to: formatDate(to, locale) });
  return t('rights.validity.always');
}

/** "Name copy", then "Name copy 2" ... - the first name the tenant is not already using (names are unique ignoring case). */
export function copyName(name: string, taken: readonly string[], suffix: string): string {
  const used = new Set(taken.map((n) => n.toLowerCase()));
  let candidate = `${name} ${suffix}`;
  for (let n = 2; used.has(candidate.toLowerCase()); n++) candidate = `${name} ${suffix} ${n}`;
  return candidate;
}

/** Turns what someone typed into the slug the server accepts for a channel or region code (`[a-z0-9_-]{1,50}`). */
export function slugify(raw: string): string {
  return raw
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9_-]+/g, '-')
    .replace(/-{2,}/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 50);
}

/** True when the new list takes something away that the old one allowed: restricting an open list, or dropping a value. */
export function narrows(before: readonly string[], after: readonly string[]): boolean {
  if (after.length === 0) return false; // empty = everything: never narrower
  if (before.length === 0) return true;
  return before.some((v) => !after.includes(v));
}

export const sameSet = (a: readonly string[], b: readonly string[]) => a.length === b.length && a.every((v) => b.includes(v));

export type RightsFilter = { status: RightsStatus | null; q: string };

/** The list the page shows: the status filter and the search, both applied to what the server returned. */
export function filterRights(all: readonly Rights[], f: RightsFilter): Rights[] {
  const q = f.q.trim().toLowerCase();
  return all.filter(
    (r) =>
      (!f.status || asStatus(r.status) === f.status) &&
      (!q || r.name.toLowerCase().includes(q) || r.licensor.toLowerCase().includes(q)),
  );
}

export function countByStatus(all: readonly Rights[]): Record<RightsStatus, number> {
  const out: Record<RightsStatus, number> = { valid: 0, expiring: 0, expired: 0, not_yet_valid: 0 };
  for (const r of all) out[asStatus(r.status)]++;
  return out;
}
