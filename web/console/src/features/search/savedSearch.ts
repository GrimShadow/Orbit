import { SEARCH_FACET_FIELDS } from './searchApi';

/** What a saved search (a smart collection) stores is the search it was saved with, and the API returns it as the server's own
 * search type: PascalCase keys (`Text`, `Filters`, `Semantic`), not the camelCase request body (`query`, `filters`, `semantic`) it
 * was saved from. The generated type calls it an opaque object, so this is the part of it the console reads; both spellings are
 * accepted. Saved searches from before Smart search have no `Semantic`. */
type Stored = Record<string, unknown>;

const stored = (query: unknown): Stored => (query && typeof query === 'object' ? (query as Stored) : {});
const pick = (s: Stored, ...keys: string[]): unknown => keys.map((k) => s[k]).find((v) => v !== undefined && v !== null);

/** Whether the search was saved with Smart search on. Absent (an older saved search) means off. */
export const isSmartSavedSearch = (query: unknown): boolean => pick(stored(query), 'Semantic', 'semantic') === true;

/** The Search page address that re-opens a saved search: the words, the facet filters and the Smart search choice. `smart` is
 * always written (`0` for an older saved search), so the page shows what was saved instead of this browser's own preference. */
export function savedSearchHref(query: unknown): string {
  const s = stored(query);
  const params = new URLSearchParams();
  const text = pick(s, 'Text', 'query', 'text');
  if (typeof text === 'string' && text.trim()) params.set('q', text.trim());
  const rawFilters = pick(s, 'Filters', 'filters');
  const filters = rawFilters && typeof rawFilters === 'object' ? (rawFilters as Record<string, unknown>) : {};
  for (const field of SEARCH_FACET_FIELDS) {
    const values = filters[field];
    if (Array.isArray(values)) for (const v of values) if (typeof v === 'string') params.append(field, v);
  }
  params.set('smart', isSmartSavedSearch(query) ? '1' : '0');
  return `/search?${params.toString()}`;
}
