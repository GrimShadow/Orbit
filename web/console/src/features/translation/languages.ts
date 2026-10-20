import { unwrap } from '@dam/sdk-js';
import { useQuery } from '@tanstack/react-query';
import { useApiClient } from '../../api/apiClient';
import { useAuth } from '../../auth/AuthProvider';
import { languageName } from '../ai/asset/aiFormat';

/**
 * The languages a multilingual field falls back to when the tenant has not chosen any: the five the search index
 * analyzes (ADR-0011). Metadata content and the console's own UI language are independent.
 */
export const ENABLED_LANGUAGES: readonly string[] = ['en', 'hi', 'mr', 'ta', 'te'];

/** At most this many target languages in one translate request (ADR-0036). */
export const MAX_TARGET_LANGUAGES = 10;

/** The codes in a tenant's settings document (`languages`), lower-cased and de-duplicated; empty when it names none. */
export function tenantLanguages(settings: unknown): string[] {
  const raw = (settings as { languages?: unknown } | null | undefined)?.languages;
  if (!Array.isArray(raw)) return [];
  const codes = raw.filter((x): x is string => typeof x === 'string').map((x) => x.trim().toLowerCase());
  return [...new Set(codes.filter((c) => c.length > 0))];
}

/** The languages multilingual metadata may use for this tenant: its `languages` setting (the same list the server
 * validates against), else the five index languages. Until the tenant loads, or if it cannot, the fallback applies. */
export function useEnabledLanguages(): readonly string[] {
  const api = useApiClient();
  const { status } = useAuth();
  const tenant = useQuery({
    queryKey: ['tenant', 'current'],
    queryFn: async () => unwrap(await api.GET('/api/v1/tenants/current')),
    enabled: status === 'authenticated',
    staleTime: 5 * 60_000,
  });
  const configured = tenantLanguages(tenant.data?.settings);
  return configured.length > 0 ? configured : ENABLED_LANGUAGES;
}

export { languageName };

/** "Hindi (hi)" - the name in the console's language plus the code, which is what the data is stored under. */
export function languageLabel(code: string, locale: string): string {
  const name = languageName(code, locale);
  return name === code ? code : `${name} (${code})`;
}
