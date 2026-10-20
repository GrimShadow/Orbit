// Test-only scaffolding for Step 2.2: fixtures and a small in-memory backend (one asset, "a1") behind the fetch mock, plus
// renderers for the metadata editor, the AI panel and the translate dialog. Nothing in the app imports this.
import { ToastProvider } from '@dam/ui-kit';
import { QueryClientProvider } from '@tanstack/react-query';
import { render } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { queryClient } from '../../App';
import { AuthContext } from '../../auth/AuthProvider';
import { AiPanel } from '../ai/asset/AiPanel';
import { MetadataForm } from '../asset/MetadataForm';
import { SaveBar } from '../asset/SaveBar';
import { useAsset, type AssetDto } from '../asset/assetApi';
import { useAssetDraft } from '../asset/useAssetDraft';
import { CommentsProvider } from '../comments/CommentsContext';
import { json, mockApi, signedIn, type Call } from '../workflow/workflowTestKit';
import { TranslationScope } from './TranslationScope';
import type { AcceptTranslationBody, TranslationJob } from './translationTypes';

export { json, type Call };

export const EDITOR = ['assets.read', 'assets.update'];
export const ADMIN = ['assets.read', 'assets.update', 'ai.manage'];
export const READER = ['assets.read'];

export const FIELDS = [
  {
    name: 'headline',
    label: 'Headline',
    type: 'ml_text',
    required: false,
    multi: false,
    searchable: true,
    facet: false,
    maxLength: 60,
  },
  { name: 'blurb', label: 'Blurb', type: 'ml_text', required: false, multi: false, searchable: true, facet: false },
  { name: 'credit', label: 'Credit', type: 'text', required: false, multi: false, searchable: true, facet: false },
];

export const schema = () => ({
  assetType: 'image',
  version: 1,
  isCurrent: true,
  changeNote: '',
  createdAt: '2026-01-01T00:00:00Z',
  fields: FIELDS,
});

export const assetDto = (metadata: Record<string, unknown> = {}, over: Record<string, unknown> = {}): AssetDto =>
  ({
    id: 'a1',
    folderId: null,
    type: 'image',
    status: 'draft',
    title: 'Acme spring hero',
    metadata,
    metadataSchemaVersion: 1,
    taxonomyTerms: [],
    tags: [],
    attributes: {},
    ownerId: null,
    currentVersionId: 'v1',
    checksumSha256: null,
    statusReason: '',
    publishAt: null,
    expireAt: null,
    retentionPolicyId: null,
    legalHold: false,
    rightsId: null,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-02T00:00:00Z',
    ...over,
  }) as unknown as AssetDto;

export const caps = (over: Record<string, unknown> = {}) => ({
  enabled: true,
  semanticSearch: true,
  enrichment: true,
  textVectors: true,
  imageVectors: false,
  provider: 'openai-compatible',
  sendsDataOffSite: true,
  translate: true,
  summarise: true,
  ...over,
});

export const translation = (id: string, field: string, language: string, text: string, over: Record<string, unknown> = {}) => ({
  id,
  kind: 'translation',
  value: text,
  confidence: 0,
  model: 'chat-model',
  status: 'pending',
  createdAt: '2026-01-02T10:00:03Z',
  decidedBy: null,
  decidedAt: null,
  field,
  language,
  sourceLanguage: 'en',
  sourceStale: false,
  ...over,
});

export const tag = (id: string, value: string) => ({
  id,
  kind: 'tag',
  value,
  confidence: 0.8,
  model: 'clip',
  status: 'pending',
  createdAt: '2026-01-02T10:00:03Z',
  decidedBy: null,
  decidedAt: null,
});

export type Suggestion = ReturnType<typeof translation> | ReturnType<typeof tag>;

export const aiDto = (over: Record<string, unknown> = {}) => ({
  status: 'ready',
  run: {
    id: 'r1',
    versionId: 'v1',
    status: 'succeeded',
    steps: [],
    error: null,
    attempts: 1,
    startedAt: '2026-01-02T10:00:00Z',
    finishedAt: '2026-01-02T10:00:04Z',
  },
  suggestions: [] as Suggestion[],
  language: null,
  ocrText: null,
  ocrTextTruncated: false,
  ocrLanguage: null,
  transcript: null,
  embeddingsReady: false,
  summary: null,
  ...over,
});

export const job = (over: Partial<TranslationJob> = {}): TranslationJob => ({
  id: 'j1',
  assetId: 'a1',
  status: 'succeeded',
  fields: ['headline'],
  targetLanguages: ['hi'],
  sourceLanguage: 'en',
  overwrite: true,
  requestedBy: 'u1',
  createdAt: '2026-01-02T10:00:00Z',
  finishedAt: '2026-01-02T10:00:05Z',
  items: [{ field: 'headline', language: 'hi', status: 'ok', reason: null }],
  ...over,
});

export interface World {
  asset: AssetDto;
  ai: ReturnType<typeof aiDto>;
  caps: ReturnType<typeof caps>;
  jobs: TranslationJob[];
  /** What the next translate request creates (its status is what the first GET of the jobs then shows). */
  nextJob: TranslationJob;
  glossaryHasTerms: boolean;
  /** Accepting into a language that already has text needs `replace`, like the server. */
  calls: Call[];
  override?: (c: Call) => Response | Promise<Response> | undefined;
}

export const newWorld = (over: Partial<World> = {}): World => ({
  asset: assetDto({ headline: { en: 'Spring is here', hi: '' } }),
  ai: aiDto(),
  caps: caps(),
  jobs: [],
  nextJob: job(),
  glossaryHasTerms: false,
  calls: [],
  ...over,
});

export const posts = (w: World, suffix = '') => w.calls.filter((c) => c.method === 'POST' && c.pathname.endsWith(suffix));

/** The endpoints of asset "a1" and the AI/translation routes, stateful for decisions and saves. */
export function backend(w: World) {
  return (c: Call): Response | Promise<Response> | undefined => {
    w.calls.push(c);
    const custom = w.override?.(c);
    if (custom) return custom;
    const { method, pathname: p } = c;
    if (p === '/api/v1/assets/a1' && method === 'GET') return json(w.asset);
    if (p === '/api/v1/assets/a1' && method === 'PATCH')
      return c.json().then((b) => {
        const body = b as { metadata: Record<string, unknown> | null; tags: string[] | null };
        w.asset = { ...w.asset, metadata: body.metadata ?? w.asset.metadata, tags: body.tags ?? w.asset.tags } as AssetDto;
        return json(w.asset);
      });
    if (p === '/api/v1/schemas/image') return json(schema());
    if (p === '/api/v1/ai/capabilities') return json(w.caps);
    if (p === '/api/v1/assets/a1/ai' && method === 'GET') return json(w.ai);
    if (p === '/api/v1/glossary' && method === 'GET')
      return json({
        items: w.glossaryHasTerms ? [{ id: 'g1', term: 'Acme Lumen', mode: 'locked', translations: {}, note: '' }] : [],
        nextCursor: null,
      });
    if (p === '/api/v1/assets/a1/translate' && method === 'POST')
      return c.json().then((b) => {
        const body = b as { fields: string[]; targetLanguages: string[] };
        w.nextJob = { ...w.nextJob, fields: body.fields, targetLanguages: body.targetLanguages };
        w.jobs = [w.nextJob, ...w.jobs];
        return json({ ...w.nextJob, status: 'pending', items: [] }, 202);
      });
    if (p === '/api/v1/assets/a1/translations') return json({ jobs: w.jobs });
    const decide = /\/ai\/suggestions\/([^/]+)\/(accept|reject)$/.exec(p);
    if (decide && method === 'POST') {
      const s = w.ai.suggestions.find((x) => x.id === decide[1]) as ReturnType<typeof translation> | undefined;
      if (!s) return json({ title: 'Not found', status: 404 }, 404);
      return (c.method === 'POST' ? c.json().catch(() => ({})) : Promise.resolve({})).then((b) => {
        const body = (b ?? {}) as AcceptTranslationBody;
        if (decide[2] === 'reject') {
          s.status = 'rejected';
          return json({ suggestions: w.ai.suggestions, tags: [], changed: 1, notes: [] });
        }
        if (s.kind === 'translation') {
          const current = (w.asset.metadata as Record<string, Record<string, string>>)[s.field] ?? {};
          if ((current[s.language] ?? '').trim() !== '' && !body.replace)
            return json({ title: 'Conflict', status: 409, detail: 'The target language already has text.' }, 409);
          w.asset = {
            ...w.asset,
            metadata: { ...w.asset.metadata, [s.field]: { ...current, [s.language]: body.text ?? s.value } },
          } as AssetDto;
        }
        s.status = 'accepted';
        return json({ suggestions: w.ai.suggestions, tags: [], changed: 1, notes: [] });
      });
    }
    return undefined;
  };
}

const providers = (w: World, permissions: string[]) => {
  mockApi(backend(w), { permissions });
};

function Providers({ children, route }: { children: React.ReactNode; route?: string }) {
  return (
    <AuthContext.Provider value={signedIn}>
      <QueryClientProvider client={queryClient}>
        <ToastProvider>
          <MemoryRouter initialEntries={[route ?? '/library/assets/a1']}>{children}</MemoryRouter>
        </ToastProvider>
      </QueryClientProvider>
    </AuthContext.Provider>
  );
}

/** The Metadata tab of asset "a1" with its draft and save bar, reading the asset like the page does (so a refetch reaches it). */
function MetadataHarness() {
  const asset = useAsset('a1').data;
  return asset ? <Loaded asset={asset} /> : null;
}
function Loaded({ asset }: { asset: AssetDto }) {
  const draft = useAssetDraft(asset);
  return (
    <TranslationScope assetId={asset.id} assetType={asset.type} saved={asset.metadata}>
      <MetadataForm asset={asset} draft={draft} />
      {draft.dirty && <SaveBar saving={draft.saving} onSave={draft.save} onDiscard={draft.discard} />}
    </TranslationScope>
  );
}

export function renderMetadata(w: World, opts: { permissions?: string[]; route?: string } = {}) {
  queryClient.clear();
  providers(w, opts.permissions ?? EDITOR);
  return render(
    <Providers route={opts.route}>
      <MetadataHarness />
    </Providers>,
  );
}

export function renderPanel(w: World, opts: { permissions?: string[]; route?: string; asset?: AssetDto } = {}) {
  queryClient.clear();
  providers(w, opts.permissions ?? EDITOR);
  const a = opts.asset ?? w.asset;
  return render(
    <Providers route={opts.route}>
      <CommentsProvider assetId={a.id} assetType={a.type} versionId="v1">
        <AiPanel asset={a} />
      </CommentsProvider>
    </Providers>,
  );
}

export function renderUi(w: World, ui: React.ReactNode, opts: { permissions?: string[]; route?: string } = {}) {
  queryClient.clear();
  providers(w, opts.permissions ?? EDITOR);
  return render(<Providers route={opts.route}>{ui}</Providers>);
}
