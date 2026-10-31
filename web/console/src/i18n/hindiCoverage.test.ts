import i18n from './index';

/** Hindi must cover everything English has. The per-area checks below name the areas that used to fall back to English
 * (analytics, the AI panels, tasks, webhooks, workflows, rights, audit, comments, notifications, smart search, ...); the
 * last test covers the WHOLE merged resource, so a future English string cannot ship without a Hindi counterpart. */
const NAMESPACES = [
  'analytics',
  'aiAsset',
  'aiAdmin',
  'tasks',
  'assetWorkflow',
  'webhooks',
  'workflows',
  'rights',
  'rightsAsset',
  'audit',
  'comments',
  'annotations',
  'notifications',
  'smartSearch',
  'integrations',
];
const EXTRA_PREFIXES = ['home.stats.', 'nav.', 'upload.', 'item.'];

/** Keys whose Hindi value is deliberately the same as the English, because there is nothing to translate: the product
 * name, a product word that Hindi UI keeps in Latin script, a data-format name, or a code-like example value. Anything
 * else that matches the English word for word is a missing translation. Keep this list short. */
const SAME_AS_ENGLISH = new Set([
  'app.name', // the product name
  'webhooks.channels.slack', // product names
  'webhooks.channels.teams',
  'webhooks.deliveries.col.http', // the HTTP column header
  'workflows.designer.view.json', // the JSON view tab
  'rights.scope.channel.placeholder', // example channel codes, which are codes not words
  'rights.scope.region.placeholder', // example region codes
  'glossary.import.example', // a sample import file (hi=/ta= language columns)
]);

type Dict = Record<string, unknown>;

function flatten(obj: Dict, prefix = ''): Map<string, string> {
  const out = new Map<string, string>();
  for (const [k, v] of Object.entries(obj)) {
    const key = prefix ? `${prefix}.${k}` : k;
    if (v && typeof v === 'object') for (const [ck, cv] of flatten(v as Dict, key)) out.set(ck, cv);
    else out.set(key, String(v));
  }
  return out;
}

const placeholders = (s: string): string[] => [...s.matchAll(/\{\{\s*([^},\s]+)[^}]*\}\}/g)].map((m) => m[1]!).sort();

const en = flatten(i18n.getResourceBundle('en', 'translation') as Dict);
const hi = flatten(i18n.getResourceBundle('hi', 'translation') as Dict);
const inScope = (key: string) =>
  NAMESPACES.some((ns) => key.startsWith(`${ns}.`)) || EXTRA_PREFIXES.some((p) => key.startsWith(p));
const scoped = [...en.keys()].filter(inScope);

/** Same text as the English with a real word in it (counters, "{{ms}} ms", "v{{n}}" have none to translate). */
const copiedFromEnglish = (keys: Iterable<string>) =>
  [...keys].filter((k) => {
    if (!hi.has(k) || SAME_AS_ENGLISH.has(k)) return false;
    const h = hi.get(k)!;
    return h === en.get(k) && /[A-Za-z]{4,}/.test(h.replace(/\{\{[^}]*\}\}/g, ''));
  });

describe('Hindi coverage of the named console areas', () => {
  it('has English keys to compare, for every area', () => {
    for (const ns of NAMESPACES) expect(scoped.filter((k) => k.startsWith(`${ns}.`)).length).toBeGreaterThan(10);
  });

  it('has a Hindi string for every English key', () => {
    const missing = scoped.filter((k) => !hi.has(k) || hi.get(k)!.trim() === '');
    expect(missing).toEqual([]);
  });

  it('keeps the same {{placeholders}} in every Hindi string', () => {
    const mismatched = scoped
      .filter((k) => hi.has(k))
      .filter((k) => placeholders(en.get(k)!).join() !== placeholders(hi.get(k)!).join());
    expect(mismatched).toEqual([]);
  });

  it('has no Hindi key without an English one (no stale or misspelt keys)', () => {
    const orphans = [...hi.keys()].filter(inScope).filter((k) => !en.has(k));
    expect(orphans).toEqual([]);
  });

  it('translates the words, not just copies the English', () => {
    expect(copiedFromEnglish(scoped)).toEqual([]);
  });
});

describe('Hindi coverage of the whole console', () => {
  it('has a Hindi string for every English key, so a new English string cannot ship without one', () => {
    const missing = [...en.keys()].filter((k) => !hi.has(k) || hi.get(k)!.trim() === '');
    expect(missing).toEqual([]);
  });

  it('keeps the same {{placeholders}} everywhere', () => {
    const mismatched = [...en.keys()]
      .filter((k) => hi.has(k))
      .filter((k) => placeholders(en.get(k)!).join() !== placeholders(hi.get(k)!).join());
    expect(mismatched).toEqual([]);
  });

  it('has no Hindi key without an English one', () => {
    expect([...hi.keys()].filter((k) => !en.has(k))).toEqual([]);
  });

  it('leaves a string identical to the English only where it is on the explicit allow-list', () => {
    expect(copiedFromEnglish(en.keys())).toEqual([]);
  });

  it('keeps the allow-list honest: every entry exists and really is identical', () => {
    for (const k of SAME_AS_ENGLISH) {
      expect(en.has(k), `${k} is not an English key`).toBe(true);
      expect(hi.get(k), `${k} differs from the English, so it does not need to be allow-listed`).toBe(en.get(k));
    }
  });
});
