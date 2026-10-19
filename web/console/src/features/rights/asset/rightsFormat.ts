import { ApiError } from '@dam/sdk-js';
import type { useTranslation } from 'react-i18next';
import type { BadgeTone } from '../../../ui';
import { errorMessage } from '../../asset/assetApi';
import { RESTRICTED_MARKER, type RightsStatus } from '../rightsApi';

type T = ReturnType<typeof useTranslation>['t'];

/** What the status text needs from either a rights record or an asset's summary of one. */
export interface StatusFacts {
  status: string;
  daysRemaining: number | null;
  validFrom: string | null;
  validTo: string | null;
}

/** A calendar date ("2026-03-03", UTC, no time) in the reader's language. Parsed as UTC and formatted as UTC, so no timezone
 * can move it to the neighbouring day. */
export function formatDay(day: string, lang: string): string {
  const d = new Date(`${day.slice(0, 10)}T00:00:00Z`);
  return Number.isNaN(d.getTime()) ? day : new Intl.DateTimeFormat(lang, { dateStyle: 'medium', timeZone: 'UTC' }).format(d);
}

export function statusTone(status: string): BadgeTone {
  switch (status as RightsStatus) {
    case 'valid':
      return 'success';
    case 'expiring':
      return 'warning';
    case 'expired':
      return 'danger';
    case 'not_yet_valid':
      return 'info';
    default:
      return 'neutral';
  }
}

/** "Valid", "Expires in 7 days", "Expired on 3 Mar 2026", "Starts on 1 Jun 2026". */
export function statusText(t: T, lang: string, r: StatusFacts): string {
  switch (r.status) {
    case 'expiring': {
      const days = r.daysRemaining ?? 0;
      if (days <= 0) return t('rightsAsset.status.expiresToday');
      if (days === 1) return t('rightsAsset.status.expiresTomorrow');
      return t('rightsAsset.status.expiresIn', { count: days });
    }
    case 'expired':
      return r.validTo
        ? t('rightsAsset.status.expiredOn', { date: formatDay(r.validTo, lang) })
        : t('rightsAsset.header.expired');
    case 'not_yet_valid':
      return r.validFrom ? t('rightsAsset.status.startsOn', { date: formatDay(r.validFrom, lang) }) : r.status;
    case 'valid':
      return t('rightsAsset.status.valid');
    default:
      return r.status;
  }
}

/** The shorter wording for the asset header: it already says "Licence". */
export function headerText(t: T, lang: string, r: StatusFacts): string | null {
  switch (r.status) {
    case 'expiring': {
      const days = r.daysRemaining ?? 0;
      if (days <= 0) return t('rightsAsset.header.expiresToday');
      if (days === 1) return t('rightsAsset.header.expiresTomorrow');
      return t('rightsAsset.header.expiresIn', { count: days });
    }
    case 'expired':
      return t('rightsAsset.header.expired');
    case 'not_yet_valid':
      return r.validFrom ? t('rightsAsset.header.startsOn', { date: formatDay(r.validFrom, lang) }) : null;
    default:
      return null;
  }
}

/** "1 Jan 2026 to 31 Dec 2026", "From 1 Jan 2026, no end date", "Until 31 Dec 2026", "No start or end date". */
export function validityText(t: T, lang: string, r: Pick<StatusFacts, 'validFrom' | 'validTo'>): string {
  const from = r.validFrom ? formatDay(r.validFrom, lang) : null;
  const to = r.validTo ? formatDay(r.validTo, lang) : null;
  if (from && to) return t('rightsAsset.validity.range', { from, to });
  if (from) return t('rightsAsset.validity.from', { from });
  if (to) return t('rightsAsset.validity.until', { to });
  return t('rightsAsset.validity.none');
}

export const licenceLabel = (t: T, type: string): string => t(`rightsAsset.licence.${type}`, { defaultValue: type });

/** The server refuses to publish (409) while the licence is over or not begun; the lifecycle UI pre-empts that. */
export const blocksPublishing = (status: string | undefined): boolean => status === 'expired' || status === 'not_yet_valid';

/** The sentences a `403 rights_restricted` carries (`reasons`, else the one-line `reason`/title), or null for any other error. */
export function rightsBlockReasons(error: unknown): string[] | null {
  if (!(error instanceof ApiError) || error.status !== 403) return null;
  const p = error.problem ?? {};
  if (p.code !== 'rights_restricted') return null;
  const many = Array.isArray(p.reasons) ? p.reasons.filter((x): x is string => typeof x === 'string' && x.length > 0) : [];
  if (many.length > 0) return many;
  return [typeof p.reason === 'string' && p.reason ? p.reason : error.title];
}

/** The message to show for a failed rights call: the first field problem of a 422, else the server's own explanation. */
export function problemText(e: unknown): string {
  if (e instanceof ApiError && e.status === 422) {
    const errors = e.problem?.errors;
    if (errors && typeof errors === 'object') {
      for (const v of Object.values(errors as Record<string, unknown>)) {
        const first = Array.isArray(v) ? v[0] : v;
        if (typeof first === 'string' && first) return first;
      }
    }
  }
  return errorMessage(e);
}

const norm = (s: string) => s.trim().toLowerCase();

/** An asset's access attributes in one dimension, as the server derived them (metadata narrowed by the licence). */
export function effectiveAccess(attributes: unknown, dimension: 'channel' | 'region'): { none: boolean; list: string[] | null } {
  const raw = (attributes as Record<string, unknown> | null | undefined)?.[dimension];
  const values = Array.isArray(raw) ? raw.filter((x): x is string => typeof x === 'string') : [];
  if (values.includes(RESTRICTED_MARKER)) return { none: true, list: [] };
  const real = values.filter((v) => norm(v) !== 'global');
  return { none: false, list: real.length > 0 ? real : null };
}

export const sameSet = (a: string[], b: string[]) => a.length === b.length && a.every((x) => b.some((y) => norm(x) === norm(y)));
