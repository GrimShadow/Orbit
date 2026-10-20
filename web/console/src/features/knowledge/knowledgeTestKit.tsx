// Test-only scaffolding for Step 3.1: fixtures and a small in-memory knowledge service behind the fetch mock. Nothing in the app
// imports this. Every name in it is fictional ("Acme").
import { json, mockApi, type Call } from '../workflow/workflowTestKit';
import type {
  ChapterDto,
  KnowledgeDocumentDetailDto,
  KnowledgeDocumentDto,
  ResolveDto,
  VinImportResultDto,
  VinRuleDto,
  VinTestDto,
} from './knowledgeTypes';

export { json, type Call };

export const READER = ['assets.read'];
export const EDITOR = ['assets.read', 'assets.update'];
export const MANAGER = ['assets.read', 'assets.update', 'knowledge.manage'];

export const doc = (over: Partial<KnowledgeDocumentDto> = {}): KnowledgeDocumentDto => ({
  id: 'd1',
  assetId: 'a1',
  title: 'Acme Lumen owner manual',
  assetType: 'manual',
  assetStatus: 'published',
  docType: 'owner_manual',
  model: 'lumen',
  variant: 'sedan',
  modelYearFrom: 2021,
  modelYearTo: 2024,
  language: 'en',
  theme: 'ice',
  chapterCount: 3,
  chaptersSource: 'pdf',
  chaptersIndexedAt: new Date(Date.now() - 2 * 3_600_000).toISOString(),
  currentVersionId: 'v1',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: new Date(Date.now() - 3 * 86_400_000).toISOString(),
  ...over,
});

export const CHAPTERS: ChapterDto[] = [
  { title: 'Getting started', anchor: null, level: 1, pageFrom: 3, pageTo: 9 },
  { title: 'Charging', anchor: null, level: 2, pageFrom: 5, pageTo: 9 },
  { title: 'Warning lights', anchor: null, level: 1, pageFrom: 10, pageTo: 12 },
];

export const rule = (over: Partial<VinRuleDto> = {}): VinRuleDto => ({
  id: 'r1',
  name: 'Acme Lumen sedan',
  pattern: 'ACMLS1*',
  model: 'lumen',
  variant: 'sedan',
  modelYear: null,
  yearFromVin: true,
  theme: 'ice',
  priority: 10,
  enabled: true,
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-02T00:00:00Z',
  ...over,
});

export const VIN = 'ACMLS1AB1N0123456';

export const resolved = (over: Partial<ResolveDto> = {}): ResolveDto => ({
  vin: VIN,
  rule: { id: 'r1', name: 'Acme Lumen sedan', priority: 10 },
  vehicle: { model: 'lumen', variant: 'sedan', modelYear: 2022, theme: 'ice' },
  document: { ...doc(), chapters: CHAPTERS, versionNumber: 4 },
  otherLanguages: [{ language: 'hi', documentId: 'd2', assetId: 'a2' }],
  ...over,
});

export const problem = (status: number, title: string, detail?: string, extra: object = {}) =>
  json({ title, status, detail, ...extra }, status);

/** What the pickers search: manuals, documents and HTML5 bundles. */
export const ASSET_HITS = [
  { id: 'a9', type: 'manual', status: 'published', title: 'Acme Orbit owner manual' },
  { id: 'a10', type: 'html5', status: 'draft', title: 'Acme Orbit quick guide' },
];

export interface World {
  docs: KnowledgeDocumentDto[];
  chapters: Record<string, ChapterDto[]>;
  rules: VinRuleDto[];
  /** Every knowledge / search call, for assertions. */
  calls: Call[];
  /** The next write answers with this. */
  refuse?: Response;
  resolve?: (call: Call) => Response;
  test?: VinTestDto | Response;
  /** What an import answers, given the CSV and the flags. */
  importer?: (csv: string, dryRun: boolean, partial: boolean) => VinImportResultDto | Response;
  /** Fail the list of documents. */
  listFails?: boolean;
  pageSize?: number;
  hits?: typeof ASSET_HITS;
}

export const newWorld = (over: Partial<World> = {}): World => ({
  docs: [doc()],
  chapters: { d1: CHAPTERS },
  rules: [rule()],
  calls: [],
  ...over,
});

const detail = (w: World, d: KnowledgeDocumentDto): KnowledgeDocumentDetailDto => ({ ...d, chapters: w.chapters[d.id] ?? [] });

/** The knowledge routes (and the asset search the picker uses) as an in-memory service. */
export function install(w: World, permissions = MANAGER, extra?: (c: Call) => Response | Promise<Response> | undefined) {
  let nextId = 100;
  return mockApi(
    (c) => {
      const custom = extra?.(c);
      if (custom) return custom;
      const p = c.pathname;
      if (p === '/api/v1/search' && c.method === 'POST') {
        w.calls.push(c);
        return c.json().then((b) => {
          const q = ((b as { query: string | null }).query ?? '').toLowerCase();
          const hits = (w.hits ?? ASSET_HITS).filter((h) => h.title.toLowerCase().includes(q));
          return json({ hits, total: hits.length, nextCursor: null, facets: {}, semanticFallback: null });
        });
      }
      if (!p.startsWith('/api/v1/knowledge/')) return undefined;
      w.calls.push(c);

      if (p === '/api/v1/knowledge/resolve') return w.resolve ? w.resolve(c) : json(resolved());

      if (p === '/api/v1/knowledge/vin-rules/test') {
        return c.json().then((b) => {
          const { vin } = b as { vin: string };
          const t = w.test;
          return t instanceof Response ? t : json(t ?? { vin, valid: true, chosen: null, vehicle: null, candidates: [] });
        });
      }
      if (p === '/api/v1/knowledge/vin-rules/import') {
        return c.text().then((csv) => {
          const dryRun = c.search.get('dryRun') === 'true';
          const result = w.importer?.(csv, dryRun, c.search.get('partial') === 'true');
          if (result instanceof Response) return result;
          return json(result ?? { created: 0, updated: 0, skipped: 0, errors: [], dryRun });
        });
      }
      if (p === '/api/v1/knowledge/vin-rules' || p.startsWith('/api/v1/knowledge/vin-rules/')) {
        if (c.method === 'GET') return json({ items: w.rules, nextCursor: null });
        if (w.refuse) {
          const r = w.refuse;
          w.refuse = undefined;
          return r;
        }
        const id = p.split('/')[5];
        if (c.method === 'POST')
          return c.json().then((b) => {
            const created = { ...rule(), ...(b as object), id: `n${nextId++}` } as VinRuleDto;
            w.rules = [...w.rules, created];
            return json(created, 201);
          });
        if (c.method === 'PUT')
          return c.json().then((b) => {
            w.rules = w.rules.map((r) => (r.id === id ? { ...r, ...(b as object) } : r));
            return json(w.rules.find((r) => r.id === id));
          });
        if (c.method === 'DELETE') {
          w.rules = w.rules.filter((r) => r.id !== id);
          return new Response(null, { status: 204 });
        }
      }

      const parts = p.split('/'); // '', api, v1, knowledge, documents, {id}, chapters|reindex
      if (parts[4] === 'documents') {
        const id = parts[5];
        const sub = parts[6];
        if (!id) {
          if (c.method === 'GET') {
            if (w.listFails) return problem(500, 'Server error');
            const s = c.search;
            const year = s.get('year') ? Number(s.get('year')) : null;
            const q = (s.get('q') ?? '').toLowerCase();
            const matching = w.docs.filter(
              (d) =>
                (!s.get('type') || d.docType === s.get('type')) &&
                (!s.get('model') || d.model === s.get('model')) &&
                (!s.get('variant') || d.variant === s.get('variant')) &&
                (!s.get('lang') || d.language === s.get('lang')) &&
                (!s.get('assetId') || d.assetId === s.get('assetId')) &&
                (year === null || ((d.modelYearFrom ?? -Infinity) <= year && year <= (d.modelYearTo ?? Infinity))) &&
                d.title.toLowerCase().includes(q),
            );
            const size = Math.min(w.pageSize ?? 25, Number(s.get('limit') ?? 25));
            const from = Number(s.get('cursor') ?? 0);
            return json({
              items: matching.slice(from, from + size),
              nextCursor: from + size < matching.length ? String(from + size) : null,
            });
          }
          if (c.method === 'POST') {
            if (w.refuse) {
              const r = w.refuse;
              w.refuse = undefined;
              return r;
            }
            return c.json().then((b) => {
              const body = b as { assetId: string };
              if (w.docs.some((d) => d.assetId === body.assetId)) return problem(409, 'Conflict', 'Already mapped.');
              const hit = (w.hits ?? ASSET_HITS).find((h) => h.id === body.assetId);
              const created = doc({
                ...(b as object),
                id: `n${nextId++}`,
                title: hit?.title ?? 'Mapped asset',
                chapterCount: 0,
                chaptersSource: 'none',
              } as Partial<KnowledgeDocumentDto>);
              w.docs = [...w.docs, created];
              w.chapters[created.id] = [];
              return json(created, 201);
            });
          }
        } else {
          const current = w.docs.find((d) => d.id === id);
          if (!current) return problem(404, 'Not found');
          if (!sub && c.method === 'GET') return json(detail(w, current));
          if (w.refuse) {
            const r = w.refuse;
            w.refuse = undefined;
            return r;
          }
          if (sub === 'chapters' && c.method === 'PUT')
            return c.json().then((b) => {
              const { chapters } = b as { chapters: ChapterDto[] };
              w.chapters[id] = chapters;
              w.docs = w.docs.map((d) =>
                d.id === id ? { ...d, chapterCount: chapters.length, chaptersSource: 'manual' as const } : d,
              );
              return json(
                detail(
                  w,
                  w.docs.find((d) => d.id === id)!,
                ),
              );
            });
          if (sub === 'reindex' && c.method === 'POST') return new Response(null, { status: 202 });
          if (!sub && c.method === 'PUT')
            return c.json().then((b) => {
              w.docs = w.docs.map((d) => (d.id === id ? { ...d, ...(b as object) } : d));
              return json(w.docs.find((d) => d.id === id));
            });
          if (!sub && c.method === 'DELETE') {
            w.docs = w.docs.filter((d) => d.id !== id);
            return new Response(null, { status: 204 });
          }
        }
      }
      return undefined;
    },
    { permissions },
  );
}

export const callsTo = (w: World, pathname: string, method = 'GET') =>
  w.calls.filter((c) => c.pathname === pathname && c.method === method);
