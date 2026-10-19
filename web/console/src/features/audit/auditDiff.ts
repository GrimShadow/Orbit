/** A readable diff of an audit entry's `before` and `after` JSON documents. Objects are flattened to dotted paths;
 * arrays and scalars are leaves (an array is changed or not as a whole), which keeps the output scannable. */

export type DiffKind = 'added' | 'removed' | 'changed' | 'unchanged';

export interface DiffRow {
  path: string;
  kind: DiffKind;
  before?: unknown;
  after?: unknown;
}

export interface DiffCounts {
  added: number;
  removed: number;
  changed: number;
  unchanged: number;
}

/** `diff`: both sides recorded. `after` / `before`: only one side recorded, so there is nothing to compare and the
 * fields are listed as recorded. `none`: no details at all. */
export type DiffMode = 'diff' | 'after' | 'before' | 'none';

export interface AuditDiff {
  mode: DiffMode;
  rows: DiffRow[];
  counts: DiffCounts;
}

const isPlainObject = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v);

/** Flattens a JSON value to `path -> leaf`. A root that is not an object is the single path `(value)`. */
export function flatten(value: unknown): Map<string, unknown> {
  const out = new Map<string, unknown>();
  const walk = (v: unknown, path: string) => {
    if (isPlainObject(v) && Object.keys(v).length > 0) {
      for (const [k, child] of Object.entries(v)) walk(child, path ? `${path}.${k}` : k);
    } else {
      out.set(path || '(value)', v);
    }
  };
  if (isPlainObject(value) && Object.keys(value).length === 0) return out; // an empty document has no fields
  walk(value, '');
  return out;
}

const same = (a: unknown, b: unknown): boolean => JSON.stringify(a) === JSON.stringify(b);
const hasDoc = (v: unknown): boolean => v !== null && v !== undefined;
const byPath = (a: DiffRow, b: DiffRow) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0);

export function buildDiff(before: unknown, after: unknown): AuditDiff {
  const counts: DiffCounts = { added: 0, removed: 0, changed: 0, unchanged: 0 };
  if (!hasDoc(before) && !hasDoc(after)) return { mode: 'none', rows: [], counts };

  if (!hasDoc(before) || !hasDoc(after)) {
    const only = hasDoc(after) ? after : before;
    const rows: DiffRow[] = [...flatten(only)]
      .map(([path, v]): DiffRow => ({ path, kind: 'unchanged', ...(hasDoc(after) ? { after: v } : { before: v }) }))
      .sort(byPath);
    counts.unchanged = rows.length;
    return { mode: hasDoc(after) ? 'after' : 'before', rows, counts };
  }

  const b = flatten(before);
  const a = flatten(after);
  const rows: DiffRow[] = [];
  for (const [path, bv] of b) {
    if (!a.has(path)) rows.push({ path, kind: 'removed', before: bv });
    else if (same(bv, a.get(path))) rows.push({ path, kind: 'unchanged', before: bv, after: a.get(path) });
    else rows.push({ path, kind: 'changed', before: bv, after: a.get(path) });
  }
  for (const [path, av] of a) if (!b.has(path)) rows.push({ path, kind: 'added', after: av });
  rows.sort(byPath);
  for (const r of rows) counts[r.kind]++;
  return { mode: 'diff', rows, counts };
}

/** One value as a compact JSON literal, cut at `max` characters. */
export function formatValue(value: unknown, max = 400): { text: string; truncated: boolean } {
  const text = value === undefined ? 'undefined' : (JSON.stringify(value) ?? String(value));
  return text.length > max ? { text: `${text.slice(0, max)}…`, truncated: true } : { text, truncated: false };
}

export const prettyJson = (value: unknown): string => JSON.stringify(value ?? null, null, 2);
