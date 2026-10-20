import { ToastProvider } from '@dam/ui-kit';
import { QueryClientProvider } from '@tanstack/react-query';
import { act, cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useEffect, useState } from 'react';
import { MemoryRouter } from 'react-router-dom';
import { axe } from 'vitest-axe';
import { queryClient } from '../../../App';
import { AuthContext } from '../../../auth/AuthProvider';
import { setLanguage } from '../../../i18n';
import type { AssetDto } from '../../asset/assetApi';
import { CommentsProvider, useCommentsUi } from '../../comments/CommentsContext';
import { json, mockApi, renderAt, signedIn, type Call } from '../../workflow/workflowTestKit';
import { AiPanel } from './AiPanel';
import { useDeepLinkSeek } from './useDeepLinkSeek';

beforeEach(() => {
  queryClient.clear();
  setLanguage('en');
  stage.seeks = [];
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const EDITOR = ['assets.read', 'assets.update'];
const READER = ['assets.read'];

// ---- fixtures -----------------------------------------------------------------------------------------------------

const asset = (over: Record<string, unknown> = {}): AssetDto =>
  ({
    id: 'a1',
    folderId: null,
    type: 'image',
    status: 'draft',
    title: 'Acme spring hero',
    metadata: {},
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
  }) as AssetDto;

const step = (name: string, over: Record<string, unknown> = {}) => ({
  name,
  status: 'ok',
  durationMs: 420,
  model: 'clip-vit-b32',
  detail: null,
  ...over,
});

const run = (over: Record<string, unknown> = {}) => ({
  id: 'r1',
  versionId: 'v1',
  status: 'succeeded',
  steps: [
    step('tags'),
    step('ocr', { model: 'ocr-engine', durationMs: 1800 }),
    step('image-embedding'),
    step('language'),
    step('text-embedding'),
  ],
  error: null,
  attempts: 1,
  startedAt: '2026-01-02T10:00:00Z',
  finishedAt: '2026-01-02T10:00:04Z',
  ...over,
});

const sug = (id: string, value: string, confidence: number, status = 'pending', kind = 'tag') => ({
  id,
  kind,
  value,
  confidence,
  model: 'clip-vit-b32',
  status,
  createdAt: '2026-01-02T10:00:03Z',
  decidedBy: null,
  decidedAt: null,
});

type Sug = ReturnType<typeof sug>;
const ai = (over: Record<string, unknown> = {}) => ({
  status: 'ready',
  run: run(),
  suggestions: [] as Sug[],
  language: null,
  ocrText: null,
  ocrTextTruncated: false,
  ocrLanguage: null,
  transcript: null,
  embeddingsReady: false,
  ...over,
});

const caps = (over: Record<string, unknown> = {}) => ({
  enabled: true,
  semanticSearch: true,
  enrichment: true,
  textVectors: true,
  imageVectors: true,
  provider: 'self-hosted',
  sendsDataOffSite: false,
  ...over,
});

const SEGMENTS = [
  { startMs: 0, endMs: 4000, text: 'Welcome to the Acme launch.' },
  { startMs: 4000, endMs: 9000, text: 'Today we show the new line.' },
  { startMs: 12000, endMs: 15000, text: 'Questions are welcome.' },
  { startMs: 83400, endMs: 87000, text: 'Thank you for coming.' },
];
const transcript = (over: Record<string, unknown> = {}) => ({
  language: 'en',
  durationMs: 87000,
  model: 'whisper-small',
  segments: SEGMENTS,
  ...over,
});

const TAGS_READY = () =>
  ai({
    suggestions: [sug('s1', 'sunset', 0.82), sug('s2', 'beach', 0.64), sug('s3', 'palm tree', 0.31)],
  });

// ---- a fake backend + a fake video stage --------------------------------------------------------------------------

interface World {
  ai: ReturnType<typeof ai>;
  caps: ReturnType<typeof caps>;
  calls: Call[];
  /** Replaces the default answer for one request. */
  override?: (c: Call) => Response | Promise<Response> | undefined;
}

/** The AI endpoints of one asset ("a1"), stateful for decisions so the panel sees what the server would answer next. */
function backend(world: World) {
  return (c: Call): Response | Promise<Response> | undefined => {
    if (!c.pathname.startsWith('/api/v1/ai') && !c.pathname.startsWith('/api/v1/assets/a1/ai')) return undefined;
    world.calls.push(c);
    const custom = world.override?.(c);
    if (custom) return custom;
    if (c.pathname === '/api/v1/ai/capabilities') return json(world.caps);
    if (c.pathname === '/api/v1/assets/a1/ai' && c.method === 'GET') return json(world.ai);
    if (c.pathname === '/api/v1/assets/a1/ai/enrich') {
      world.ai = ai({ status: 'pending', run: run({ status: 'pending', steps: [] }) });
      return json(world.ai, 202);
    }
    const decide = /\/ai\/suggestions\/([^/]+)\/(accept|reject)$/.exec(c.pathname);
    if (decide) {
      const s = world.ai.suggestions.find((x) => x.id === decide[1]);
      if (!s) return json({ title: 'Not found', status: 404 }, 404);
      s.status = decide[2] === 'accept' ? 'accepted' : 'rejected';
      return json({ suggestions: world.ai.suggestions, tags: [], changed: 1, notes: [] });
    }
    if (c.pathname === '/api/v1/assets/a1/ai/suggestions/accept-all') {
      const pending = world.ai.suggestions.filter((x) => x.kind === (c.search.get('kind') ?? 'tag') && x.status === 'pending');
      pending.forEach((x) => (x.status = 'accepted'));
      return json({ suggestions: world.ai.suggestions, tags: [], changed: pending.length, notes: [] });
    }
    return json({ title: 'Not found', status: 404 }, 404);
  };
}

const newWorld = (over: Partial<World> = {}): World => ({ ai: ai(), caps: caps(), calls: [], ...over });
const posts = (w: World) => w.calls.filter((c) => c.method === 'POST');

/** Stands in for the video stage: registers a seek handler, mirrors a playhead, and lets a test move it. */
const stage: { seeks: number[]; setHead: (ms: number | null) => void } = {
  seeks: [],
  setHead: () => {},
};
function FakeStage() {
  const { registerSeek, setPlayheadMs } = useCommentsUi();
  const [head, setHead] = useState<number | null>(0);
  useEffect(() => {
    stage.setHead = setHead;
    registerSeek((ms) => {
      stage.seeks.push(ms);
      setHead(ms);
    });
    return () => registerSeek(null);
  }, [registerSeek]);
  useEffect(() => setPlayheadMs(head), [head, setPlayheadMs]);
  return null;
}
/** The asset page mounts this; the panel tests that care about `?t=` do the same. */
function DeepLinkSeeker({ assetId }: { assetId: string }) {
  useDeepLinkSeek(assetId);
  return null;
}

function renderPanel(
  world: World,
  opts: { asset?: AssetDto; permissions?: string[]; player?: boolean; route?: string; seeker?: boolean } = {},
) {
  const a = opts.asset ?? asset();
  mockApi(backend(world), { permissions: opts.permissions ?? EDITOR });
  return render(
    <AuthContext.Provider value={signedIn}>
      <QueryClientProvider client={queryClient}>
        <ToastProvider>
          <MemoryRouter initialEntries={[opts.route ?? '/library/assets/a1']}>
            <CommentsProvider assetId={a.id} assetType={a.type} versionId="v1">
              {opts.player && <FakeStage />}
              {opts.seeker && <DeepLinkSeeker assetId={a.id} />}
              <AiPanel asset={a} />
            </CommentsProvider>
          </MemoryRouter>
        </ToastProvider>
      </QueryClientProvider>
    </AuthContext.Provider>,
  );
}

const panel = () => screen.findByRole('region', { name: 'AI insights' });

// ---- visibility and states ----------------------------------------------------------------------------------------

describe('AI panel: when it shows', () => {
  it('is not there when AI is off and the asset has nothing from it', async () => {
    const w = newWorld({ caps: caps({ enabled: false, enrichment: false }), ai: ai({ status: 'disabled', run: null }) });
    const { container } = renderPanel(w);
    await waitFor(() => expect(w.calls.some((c) => c.pathname === '/api/v1/assets/a1/ai')).toBe(true));
    await waitFor(() => expect(w.calls.some((c) => c.pathname === '/api/v1/ai/capabilities')).toBe(true));
    await new Promise((r) => setTimeout(r, 50));
    expect(screen.queryByText('AI insights')).not.toBeInTheDocument();
    expect(container.querySelector('section')).toBeNull();
  });

  it('is not there for a kind of file the AI does not look at', async () => {
    const w = newWorld({ ai: ai({ status: 'none', run: null }) });
    const { container } = renderPanel(w, { asset: asset({ type: 'archive' }) });
    await new Promise((r) => setTimeout(r, 50));
    expect(w.calls.filter((c) => c.pathname === '/api/v1/assets/a1/ai')).toHaveLength(0);
    expect(container.querySelector('section')).toBeNull();
  });

  it('stays quiet when the lookup fails and the organisation does not use AI', async () => {
    const w = newWorld({
      caps: caps({ enabled: false, enrichment: false }),
      override: (c) => (c.pathname === '/api/v1/assets/a1/ai' ? json({ title: 'Nope', status: 403 }, 403) : undefined),
    });
    const { container } = renderPanel(w);
    await new Promise((r) => setTimeout(r, 100));
    expect(container.querySelector('section')).toBeNull();
  });

  it('says so, with a retry, when the lookup fails and AI is on', async () => {
    let fail = true;
    const w = newWorld({
      ai: ai({ status: 'ready' }),
      override: (c) => (fail && c.pathname === '/api/v1/assets/a1/ai' ? json({ title: 'Nope', status: 404 }, 404) : undefined),
    });
    renderPanel(w);
    expect(await screen.findByText('Could not load the AI insights for this asset.')).toBeInTheDocument();
    fail = false;
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByText('Analysed')).toBeInTheDocument();
  });

  it('still shows what was found earlier after AI is turned off, without any way to re-run it', async () => {
    const w = newWorld({
      caps: caps({ enabled: false, enrichment: false }),
      ai: ai({ status: 'disabled', run: null, ocrText: 'SPRING SALE', embeddingsReady: true }),
    });
    renderPanel(w);
    const card = await panel();
    expect(within(card).getByText('AI is off')).toBeInTheDocument();
    expect(within(card).getByText(/turned off for your organisation/)).toBeInTheDocument();
    expect(within(card).getByRole('button', { name: /Text found in the picture/ })).toBeInTheDocument();
    expect(within(card).queryByRole('button', { name: /Analyse/ })).not.toBeInTheDocument();
  });

  it('shows results but no "Analyse again" when enrichment is off for the organisation', async () => {
    const w = newWorld({ caps: caps({ enrichment: false }), ai: TAGS_READY() });
    renderPanel(w);
    const card = await panel();
    expect(within(card).getByText('sunset')).toBeInTheDocument();
    expect(within(card).queryByRole('button', { name: 'Analyse again' })).not.toBeInTheDocument();
  });
});

describe('AI panel: run states', () => {
  it('offers "Analyse now" for an asset that has not been analysed, and shows the run starting', async () => {
    const w = newWorld({ ai: ai({ status: 'none', run: null }) });
    renderPanel(w);
    const card = await panel();
    expect(within(card).getByText('Not analysed yet')).toBeInTheDocument();
    expect(within(card).getByText('Not analysed')).toBeInTheDocument();
    expect(within(card).getByText('Analysis runs on your own servers')).toBeInTheDocument();
    await userEvent.click(within(card).getByRole('button', { name: 'Analyse now' }));
    expect(await screen.findByText('Analysis started')).toBeInTheDocument();
    await waitFor(() => expect(posts(w).map((c) => c.pathname)).toEqual(['/api/v1/assets/a1/ai/enrich']));
    expect(await within(card).findByText('Waiting for its turn')).toBeInTheDocument();
  });

  it('keeps looking for a run on a file that has just arrived, so it does not stay on "not analysed"', async () => {
    const w = newWorld({ ai: ai({ status: 'none', run: null }) });
    renderPanel(w, { asset: asset({ updatedAt: new Date().toISOString() }) });
    const card = await panel();
    expect(await within(card).findByText('Not analysed yet')).toBeInTheDocument();
    // The worker starts the run on its own; the next look finds it.
    w.ai = ai({ status: 'running', run: run({ status: 'running', steps: [], finishedAt: null }) });
    expect(await within(card).findByText('Analysing this file', undefined, { timeout: 8000 })).toBeInTheDocument();
  }, 15_000);

  it('tells a reader it has not been analysed, without a button', async () => {
    renderPanel(newWorld({ ai: ai({ status: 'none', run: null }) }), { permissions: READER });
    const card = await panel();
    expect(within(card).getByText('This asset has not been analysed yet.')).toBeInTheDocument();
    expect(within(card).queryByRole('button', { name: 'Analyse now' })).not.toBeInTheDocument();
  });

  it('explains a skipped run instead of offering to analyse', async () => {
    const w = newWorld({
      ai: ai({ status: 'none', run: run({ status: 'skipped', steps: [], error: 'every AI feature is off' }) }),
    });
    renderPanel(w);
    const card = await panel();
    expect(within(card).getByText('Nothing to analyse for this file: every AI feature is off')).toBeInTheDocument();
  });

  it('shows a pending run calmly, with the reason it is waiting to try again', async () => {
    const w = newWorld({
      ai: ai({ status: 'pending', run: run({ status: 'pending', steps: [], error: 'the AI service is busy' }) }),
    });
    renderPanel(w);
    const card = await panel();
    expect(within(card).getByRole('progressbar', { name: 'Analysis progress' })).toBeInTheDocument();
    expect(within(card).getByText('Waiting for its turn')).toBeInTheDocument();
    expect(within(card).getByText('Waiting to try again: the AI service is busy')).toBeInTheDocument();
    expect(within(card).getByText('This page updates by itself. You can keep working.')).toBeInTheDocument();
    expect(within(card).queryByRole('button', { name: /Analyse|Try again/ })).not.toBeInTheDocument();
  });

  it('shows the step a running analysis is on, and fills in by itself when it finishes', async () => {
    const w = newWorld({
      ai: ai({ status: 'running', run: run({ status: 'running', steps: [step('tags'), step('ocr')], finishedAt: null }) }),
    });
    renderPanel(w);
    const card = await panel();
    expect(within(card).getByText('Analysing this file')).toBeInTheDocument();
    expect(within(card).getByText('Now: Preparing picture search · 2 of 5 steps done')).toBeInTheDocument();
    const bar = within(card).getByRole('progressbar');
    expect(bar).toHaveAttribute('aria-valuenow', '2');
    expect(bar).toHaveAttribute('aria-valuemax', '5');

    // The hook polls every 3 s while the run is going; the next answer says it is done.
    w.ai = TAGS_READY();
    expect(await within(card).findByText('sunset', undefined, { timeout: 6000 })).toBeInTheDocument();
    expect(within(card).getByText('Analysed')).toBeInTheDocument();
    expect(within(card).queryByRole('progressbar')).not.toBeInTheDocument();
  }, 15_000);

  it('shows a failed run with the reason and lets an editor try again', async () => {
    const w = newWorld({
      ai: ai({
        status: 'failed',
        run: run({ status: 'failed', steps: [], error: 'Gave up after 5 attempts: the AI service is unreachable' }),
      }),
    });
    renderPanel(w);
    const card = await panel();
    const alert = within(card).getByRole('alert');
    expect(alert).toHaveTextContent('The analysis did not finish');
    expect(alert).toHaveTextContent('Gave up after 5 attempts: the AI service is unreachable');
    await userEvent.click(within(alert).getByRole('button', { name: 'Try again' }));
    await waitFor(() => expect(posts(w).map((c) => c.pathname)).toEqual(['/api/v1/assets/a1/ai/enrich']));
    expect(await within(card).findByText('Waiting for its turn')).toBeInTheDocument();
  });

  it("shows a refused re-run inline with the server's reason", async () => {
    const w = newWorld({
      ai: ai({ status: 'failed', run: run({ status: 'failed', steps: [], error: 'boom' }) }),
      override: (c) =>
        c.pathname.endsWith('/ai/enrich')
          ? json({ title: 'Conflict', status: 409, detail: 'A run is already in progress.' }, 409)
          : undefined,
    });
    renderPanel(w);
    await userEvent.click(await screen.findByRole('button', { name: 'Try again' }));
    expect(await screen.findByText('Could not start the analysis: A run is already in progress.')).toBeInTheDocument();
  });

  it('hides the retry from a reader', async () => {
    const w = newWorld({ ai: ai({ status: 'failed', run: run({ status: 'failed', steps: [], error: 'boom' }) }) });
    renderPanel(w, { permissions: READER });
    const card = await panel();
    expect(within(card).getByText('boom')).toBeInTheDocument();
    expect(within(card).queryByRole('button', { name: 'Try again' })).not.toBeInTheDocument();
  });

  it('offers "Analyse again" on a finished analysis', async () => {
    const w = newWorld({ ai: TAGS_READY() });
    renderPanel(w);
    await userEvent.click(await screen.findByRole('button', { name: 'Analyse again' }));
    await waitFor(() => expect(posts(w).map((c) => c.pathname)).toEqual(['/api/v1/assets/a1/ai/enrich']));
  });

  it('does not offer to analyse a deleted asset', async () => {
    renderPanel(newWorld({ ai: TAGS_READY() }), { asset: asset({ status: 'deleted' }) });
    const card = await panel();
    expect(within(card).getByText('sunset')).toBeInTheDocument();
    expect(within(card).queryByRole('button', { name: 'Analyse again' })).not.toBeInTheDocument();
    expect(within(card).queryByRole('button', { name: /Add tag/ })).not.toBeInTheDocument();
  });
});

// ---- suggestions --------------------------------------------------------------------------------------------------

describe('Suggested tags', () => {
  it('lists each suggestion with its confidence and a named Add and Dismiss button', async () => {
    renderPanel(newWorld({ ai: TAGS_READY() }));
    const list = await screen.findByRole('list', { name: 'Suggested tags' });
    const items = within(list).getAllByRole('listitem');
    expect(items).toHaveLength(3);
    expect(items[0]).toHaveTextContent('sunset');
    expect(items[0]).toHaveTextContent('Confidence 82%');
    expect(items[2]).toHaveTextContent('palm tree');
    expect(items[2]).toHaveTextContent('31%');
    expect(within(items[0]!).getByRole('button', { name: 'Add tag sunset' })).toBeInTheDocument();
    expect(within(items[0]!).getByRole('button', { name: 'Dismiss tag sunset' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Add all 3 suggested tags' })).toHaveTextContent('Add all (3)');
  });

  it('adds a tag: posts the accept, confirms with a toast, and moves it to the decided list', async () => {
    const w = newWorld({ ai: TAGS_READY() });
    renderPanel(w);
    await userEvent.click(await screen.findByRole('button', { name: 'Add tag sunset' }));
    expect(await screen.findByText('Added tag "sunset"')).toBeInTheDocument();
    expect(posts(w).map((c) => c.pathname)).toEqual(['/api/v1/assets/a1/ai/suggestions/s1/accept']);

    const list = screen.getByRole('list', { name: 'Suggested tags' });
    await waitFor(() => expect(within(list).queryByText('sunset')).not.toBeInTheDocument());
    expect(within(list).getAllByRole('listitem')).toHaveLength(2);

    await userEvent.click(screen.getByRole('button', { name: 'Decided (1)' }));
    const decided = screen.getByRole('list', { name: 'Decided suggestions' });
    expect(decided).toHaveTextContent('sunset');
    expect(decided).toHaveTextContent('Added');
  });

  it('dismisses a tag: posts the reject and lists it as dismissed', async () => {
    const w = newWorld({ ai: TAGS_READY() });
    renderPanel(w);
    await userEvent.click(await screen.findByRole('button', { name: 'Dismiss tag beach' }));
    await waitFor(() => expect(posts(w).map((c) => c.pathname)).toEqual(['/api/v1/assets/a1/ai/suggestions/s2/reject']));
    await userEvent.click(await screen.findByRole('button', { name: 'Decided (1)' }));
    expect(screen.getByRole('list', { name: 'Decided suggestions' })).toHaveTextContent('beachDismissed');
  });

  it('adds every pending tag at once and reports how many', async () => {
    const w = newWorld({ ai: TAGS_READY() });
    renderPanel(w);
    await userEvent.click(await screen.findByRole('button', { name: 'Add all 3 suggested tags' }));
    expect(await screen.findByText('Added 3 tags')).toBeInTheDocument();
    expect(posts(w).map((c) => `${c.pathname}?kind=${c.search.get('kind')}`)).toEqual([
      '/api/v1/assets/a1/ai/suggestions/accept-all?kind=tag',
    ]);
    expect(await screen.findByText('Every suggestion has been reviewed.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Add all/ })).not.toBeInTheDocument();
  });

  it("shows the server's note when some tags did not fit under the limit", async () => {
    const w = newWorld({
      ai: TAGS_READY(),
      override: (c) =>
        c.pathname.endsWith('/accept-all')
          ? json({
              suggestions: [],
              tags: [],
              changed: 1,
              notes: ['2 tag(s) were left pending: an asset can have at most 50 tags.'],
            })
          : undefined,
    });
    renderPanel(w);
    await userEvent.click(await screen.findByRole('button', { name: 'Add all 3 suggested tags' }));
    expect(await screen.findByText('Added 1 tag')).toBeInTheDocument();
    expect(await screen.findByText('2 tag(s) were left pending: an asset can have at most 50 tags.')).toBeInTheDocument();
  });

  it('shows an already-decided conflict inline and refreshes what is true now', async () => {
    const w = newWorld({
      ai: TAGS_READY(),
      override: (c) =>
        c.pathname.endsWith('/s1/accept')
          ? json({ title: 'Conflict', status: 409, detail: 'This suggestion was already rejected.' }, 409)
          : undefined,
    });
    renderPanel(w);
    const before = w.calls.filter((c) => c.pathname === '/api/v1/assets/a1/ai').length;
    await userEvent.click(await screen.findByRole('button', { name: 'Add tag sunset' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'That suggestion could not be updated: This suggestion was already rejected.',
    );
    await waitFor(() => expect(w.calls.filter((c) => c.pathname === '/api/v1/assets/a1/ai').length).toBeGreaterThan(before));
    // Still pending in the list, and usable again.
    expect(screen.getByRole('button', { name: 'Add tag sunset' })).toBeEnabled();
  });

  it('shows the 50-tag limit refusal as it is written', async () => {
    const w = newWorld({
      ai: TAGS_READY(),
      override: (c) =>
        c.pathname.endsWith('/s2/accept')
          ? json({ title: 'Conflict', status: 409, detail: 'An asset can have at most 50 tags.' }, 409)
          : undefined,
    });
    renderPanel(w);
    await userEvent.click(await screen.findByRole('button', { name: 'Add tag beach' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('An asset can have at most 50 tags.');
  });

  it('moves keyboard focus to the next suggestion after one is decided', async () => {
    renderPanel(newWorld({ ai: TAGS_READY() }));
    const add = await screen.findByRole('button', { name: 'Add tag sunset' });
    add.focus();
    await userEvent.keyboard('{Enter}');
    await waitFor(() => expect(screen.getByRole('button', { name: 'Add tag beach' })).toHaveFocus());
  });

  it('shows a reader the suggestions and their confidence but no decision buttons', async () => {
    renderPanel(newWorld({ ai: TAGS_READY() }), { permissions: READER });
    const list = await screen.findByRole('list', { name: 'Suggested tags' });
    expect(within(list).getAllByRole('listitem')).toHaveLength(3);
    expect(screen.queryByRole('button', { name: /Add tag|Dismiss tag|Add all/ })).not.toBeInTheDocument();
    expect(screen.getByText('Only people who can edit this asset can add or dismiss suggestions.')).toBeInTheDocument();
  });

  it('says when a tagging step found nothing new', async () => {
    renderPanel(newWorld({ ai: ai({ suggestions: [] }) }));
    expect(await screen.findByText('No new tag suggestions for this file.')).toBeInTheDocument();
  });
});

describe('Language suggestion', () => {
  const withLanguage = (status = 'pending') => ai({ suggestions: [sug('l1', 'en', 0.93, status, 'language')], language: 'en' });

  it('asks "Looks like English" with a way to accept or dismiss it', async () => {
    const w = newWorld({ ai: withLanguage() });
    renderPanel(w);
    expect(await screen.findByText('Looks like English')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Set language to English' }));
    expect(await screen.findByText('Language set to English')).toBeInTheDocument();
    expect(posts(w).map((c) => c.pathname)).toEqual(['/api/v1/assets/a1/ai/suggestions/l1/accept']);
    await waitFor(() => expect(screen.queryByText('Looks like English')).not.toBeInTheDocument());
  });

  it('dismisses it', async () => {
    const w = newWorld({ ai: withLanguage() });
    renderPanel(w);
    await userEvent.click(await screen.findByRole('button', { name: 'Dismiss language suggestion English' }));
    await waitFor(() => expect(posts(w).map((c) => c.pathname)).toEqual(['/api/v1/assets/a1/ai/suggestions/l1/reject']));
  });

  it("relays the server's note when the schema has nowhere to put the language", async () => {
    const w = newWorld({
      ai: withLanguage(),
      override: (c) =>
        c.pathname.endsWith('/l1/accept')
          ? json({
              suggestions: [],
              tags: [],
              changed: 1,
              notes: ["Language 'en' was recorded as accepted, but the image schema has no 'language' field to put it in."],
            })
          : undefined,
    });
    renderPanel(w);
    await userEvent.click(await screen.findByRole('button', { name: 'Set language to English' }));
    expect(await screen.findByText(/recorded as accepted, but the image schema has no 'language' field/)).toBeInTheDocument();
  });

  it('keeps a decided language in the decided list, not as a question', async () => {
    renderPanel(newWorld({ ai: withLanguage('accepted') }));
    await userEvent.click(await screen.findByRole('button', { name: 'Decided (1)' }));
    expect(screen.getByRole('list', { name: 'Decided suggestions' })).toHaveTextContent('Language EnglishAdded');
    expect(screen.queryByText('Looks like English')).not.toBeInTheDocument();
  });

  it('shows a reader the question without buttons', async () => {
    renderPanel(newWorld({ ai: withLanguage() }), { permissions: READER });
    expect(await screen.findByText('Looks like English')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Set language|Dismiss language/ })).not.toBeInTheDocument();
  });
});

// ---- transcript ---------------------------------------------------------------------------------------------------

describe('Transcript', () => {
  const withTranscript = () =>
    ai({ run: run({ steps: [step('transcript', { model: 'whisper-small' }), step('language')] }), transcript: transcript() });
  const videoAsset = () => asset({ type: 'video' });

  it('lists the timestamped lines with the language, length, and model', async () => {
    renderPanel(newWorld({ ai: withTranscript() }), { asset: videoAsset() });
    const list = await screen.findByRole('list', { name: 'Transcript lines' });
    const rows = within(list).getAllByRole('listitem');
    expect(rows).toHaveLength(4);
    expect(rows[0]).toHaveTextContent('0:00Welcome to the Acme launch.');
    expect(rows[3]).toHaveTextContent('1:23Thank you for coming.');
    expect(screen.getByText('English · 1:27 · 4 lines · whisper-small')).toBeInTheDocument();
  });

  it('filters the lines as you type, marks the matches, and says how many are left', async () => {
    renderPanel(newWorld({ ai: withTranscript() }), { asset: videoAsset() });
    const box = await screen.findByRole('searchbox', { name: 'Search transcript' });
    await userEvent.type(box, 'WELCOME');
    const list = screen.getByRole('list', { name: 'Transcript lines' });
    expect(within(list).getAllByRole('listitem')).toHaveLength(2);
    expect(screen.getByText('2 of 4 lines')).toBeInTheDocument();
    const marks = list.querySelectorAll('mark');
    expect(Array.from(marks).map((m) => m.textContent)).toEqual(['Welcome', 'welcome']);

    await userEvent.clear(box);
    await userEvent.type(box, 'zebra');
    expect(screen.queryByRole('list', { name: 'Transcript lines' })).not.toBeInTheDocument();
    expect(screen.getByText('No line mentions "zebra".')).toBeInTheDocument();
  });

  it('seeks the player when a line is clicked', async () => {
    renderPanel(newWorld({ ai: withTranscript() }), { asset: videoAsset(), player: true });
    const line = await screen.findByRole('button', { name: /Questions are welcome/ });
    expect(line).toHaveAttribute('title', 'Jump to 0:12');
    await userEvent.click(line);
    expect(stage.seeks).toEqual([12000]);
  });

  it('shows plain timestamps, with no seeking, when there is no player', async () => {
    renderPanel(newWorld({ ai: withTranscript() }), { asset: asset({ type: 'audio' }) });
    const list = await screen.findByRole('list', { name: 'Transcript lines' });
    expect(within(list).queryByRole('button')).not.toBeInTheDocument();
    expect(screen.queryByText(/jump|seek/i)).not.toBeInTheDocument();
    expect(list).toHaveAttribute('tabindex', '0');
    expect(within(list).getAllByRole('listitem')[2]).toHaveTextContent('0:12Questions are welcome.');
  });

  it('highlights the line being played and keeps it in view', async () => {
    // jsdom has no layout: give each row a position so the centring can be seen.
    vi.spyOn(HTMLElement.prototype, 'offsetTop', 'get').mockImplementation(function (this: HTMLElement) {
      return Number(this.dataset.index ?? 0) * 40;
    });
    vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(40);
    vi.spyOn(HTMLElement.prototype, 'clientHeight', 'get').mockReturnValue(100);

    renderPanel(newWorld({ ai: withTranscript() }), { asset: videoAsset(), player: true });
    const list = await screen.findByRole('list', { name: 'Transcript lines' });
    // The stage reports 0 before anything plays: no line is "current" yet.
    expect(list.querySelector('[aria-current]')).toBeNull();

    act(() => stage.setHead(5000));
    await waitFor(() => expect(screen.getByRole('button', { name: /Today we show/ })).toHaveAttribute('aria-current', 'true'));
    expect(list.querySelectorAll('[aria-current]')).toHaveLength(1);
    expect(list.scrollTop).toBe(40 - 30);

    act(() => stage.setHead(84000));
    await waitFor(() =>
      expect(screen.getByRole('button', { name: /Thank you for coming/ })).toHaveAttribute('aria-current', 'true'),
    );
    expect(screen.getByRole('button', { name: /Today we show/ })).not.toHaveAttribute('aria-current');
    expect(list.scrollTop).toBe(120 - 30);

    // In a pause between lines nothing is highlighted.
    act(() => stage.setHead(10000));
    await waitFor(() => expect(list.querySelector('[aria-current]')).toBeNull());
  });

  it('leaves the list alone while the pointer is over it', async () => {
    vi.spyOn(HTMLElement.prototype, 'offsetTop', 'get').mockImplementation(function (this: HTMLElement) {
      return Number(this.dataset.index ?? 0) * 40;
    });
    renderPanel(newWorld({ ai: withTranscript() }), { asset: videoAsset(), player: true });
    const list = await screen.findByRole('list', { name: 'Transcript lines' });
    await userEvent.hover(list);
    act(() => stage.setHead(84000));
    await waitFor(() =>
      expect(screen.getByRole('button', { name: /Thank you for coming/ })).toHaveAttribute('aria-current', 'true'),
    );
    expect(list.scrollTop).toBe(0);
  });

  it('copies the transcript with a time before each line', async () => {
    const user = userEvent.setup();
    renderPanel(newWorld({ ai: withTranscript() }), { asset: videoAsset() });
    await user.click(await screen.findByRole('button', { name: 'Copy transcript' }));
    expect(await screen.findByText('Transcript copied')).toBeInTheDocument();
    const text = await navigator.clipboard.readText();
    expect(text.split('\n')).toHaveLength(4);
    expect(text.split('\n')[3]).toBe('[1:23] Thank you for coming.');
  });

  it('tells the person when the transcript could not be copied', async () => {
    const user = userEvent.setup();
    renderPanel(newWorld({ ai: withTranscript() }), { asset: videoAsset() });
    vi.spyOn(navigator.clipboard, 'writeText').mockRejectedValue(new Error('denied'));
    await user.click(await screen.findByRole('button', { name: 'Copy transcript' }));
    expect(await screen.findByText('Could not copy the transcript')).toBeInTheDocument();
  });
});

describe('?t= deep link', () => {
  const withTranscript = () => ai({ transcript: transcript() });
  const video = () => asset({ type: 'video' });

  it('seeks the player to the start of the linked line, highlights it, and scrolls to it', async () => {
    vi.spyOn(HTMLElement.prototype, 'offsetTop', 'get').mockImplementation(function (this: HTMLElement) {
      return Number(this.dataset.index ?? 0) * 40;
    });
    vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(40);
    vi.spyOn(HTMLElement.prototype, 'clientHeight', 'get').mockReturnValue(100);
    const reveal = vi.fn();
    Element.prototype.scrollIntoView = reveal;

    renderPanel(newWorld({ ai: withTranscript() }), {
      asset: video(),
      player: true,
      seeker: true,
      route: '/library/assets/a1?t=83',
    });
    const list = await screen.findByRole('list', { name: 'Transcript lines' });
    // 83 s lands in the pause before the line that starts at 83.4 s: the seek snaps to that line's start.
    await waitFor(() => expect(stage.seeks).toEqual([83400]));
    await waitFor(() =>
      expect(screen.getByRole('button', { name: /Thank you for coming/ })).toHaveAttribute('aria-current', 'true'),
    );
    expect(list.scrollTop).toBe(120 - 30);
    expect(reveal).toHaveBeenCalled();
    delete (Element.prototype as { scrollIntoView?: unknown }).scrollIntoView;
  });

  it('seeks to the time itself when it falls in a pause between lines', async () => {
    renderPanel(newWorld({ ai: withTranscript() }), {
      asset: video(),
      player: true,
      seeker: true,
      route: '/library/assets/a1?t=10',
    });
    await screen.findByRole('list', { name: 'Transcript lines' });
    await waitFor(() => expect(stage.seeks).toEqual([10000]));
  });

  it('seeks only once, however often the page re-renders', async () => {
    renderPanel(newWorld({ ai: withTranscript() }), {
      asset: video(),
      player: true,
      seeker: true,
      route: '/library/assets/a1?t=5',
    });
    await waitFor(() => expect(stage.seeks).toEqual([4000]));
    act(() => stage.setHead(20000));
    await new Promise((r) => setTimeout(r, 50));
    expect(stage.seeks).toEqual([4000]);
  });

  it('highlights the linked line even with no player to seek (audio, or a draft video)', async () => {
    renderPanel(newWorld({ ai: withTranscript() }), {
      asset: asset({ type: 'audio' }),
      seeker: true,
      route: '/library/assets/a1?t=83',
    });
    const list = await screen.findByRole('list', { name: 'Transcript lines' });
    const current = list.querySelector('[aria-current]');
    expect(current).toHaveTextContent('Thank you for coming.');
    expect(stage.seeks).toEqual([]);
  });

  it('ignores a link that is not a number of seconds', async () => {
    renderPanel(newWorld({ ai: withTranscript() }), {
      asset: video(),
      player: true,
      seeker: true,
      route: '/library/assets/a1?t=soon',
    });
    const list = await screen.findByRole('list', { name: 'Transcript lines' });
    await new Promise((r) => setTimeout(r, 50));
    expect(stage.seeks).toEqual([]);
    expect(list.querySelector('[aria-current]')).toBeNull();
  });

  it('still seeks to the plain time when the AI lookup is not available', async () => {
    const w = newWorld({
      override: (c) => (c.pathname === '/api/v1/assets/a1/ai' ? json({ title: 'Nope', status: 403 }, 403) : undefined),
    });
    renderPanel(w, { asset: video(), player: true, seeker: true, route: '/library/assets/a1?t=7' });
    await waitFor(() => expect(stage.seeks).toEqual([7000]));
  });
});

// ---- text in the picture ------------------------------------------------------------------------------------------

describe('Text found in the picture', () => {
  const OCR = 'SPRING SALE\nUp to 40% off\nAcme Outdoor';

  it('is folded away, then shows the text as it is with its language', async () => {
    renderPanel(newWorld({ ai: ai({ ocrText: OCR, ocrLanguage: 'en' }) }));
    const toggle = await screen.findByRole('button', { name: /Text found in the picture/ });
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByText(/SPRING SALE/)).not.toBeVisible();
    await userEvent.click(toggle);
    expect(toggle).toHaveAttribute('aria-expanded', 'true');
    const region = screen.getByRole('region', { name: 'Text found in the picture' });
    expect(region.textContent).toBe(OCR);
    expect(screen.getByText('Language: English')).toBeInTheDocument();
    expect(screen.queryByText(/first part only/)).not.toBeInTheDocument();
  });

  it('says so when the text was cut short', async () => {
    renderPanel(newWorld({ ai: ai({ ocrText: OCR, ocrTextTruncated: true }) }));
    await userEvent.click(await screen.findByRole('button', { name: /Text found in the picture/ }));
    expect(screen.getByText('Showing the first part only. The rest is searchable but not shown here.')).toBeInTheDocument();
  });

  it('copies the text even while it is folded', async () => {
    const user = userEvent.setup();
    renderPanel(newWorld({ ai: ai({ ocrText: OCR }) }));
    await user.click(await screen.findByRole('button', { name: 'Copy text' }));
    expect(await screen.findByText('Text copied')).toBeInTheDocument();
    expect(await navigator.clipboard.readText()).toBe(OCR);
  });

  it('calls it the text in the pages for a document', async () => {
    renderPanel(newWorld({ ai: ai({ ocrText: OCR }) }), { asset: asset({ type: 'document' }) });
    expect(await screen.findByRole('button', { name: /Text found in the pages/ })).toBeInTheDocument();
  });

  it('is a plain read-only block for a reader too', async () => {
    renderPanel(newWorld({ ai: ai({ ocrText: OCR }) }), { permissions: READER });
    await userEvent.click(await screen.findByRole('button', { name: /Text found in the picture/ }));
    expect(screen.getByRole('region', { name: 'Text found in the picture' }).tagName).toBe('PRE');
    expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
  });
});

// ---- run details and footer ---------------------------------------------------------------------------------------

describe('How this was analysed', () => {
  it('lists the steps with status, model, and time, and what went wrong', async () => {
    const w = newWorld({
      ai: ai({
        run: run({
          attempts: 2,
          steps: [
            step('tags'),
            step('ocr', { status: 'failed', model: 'ocr-engine', durationMs: 1800, detail: 'The OCR service refused the file' }),
            step('image-embedding', { status: 'skipped', model: null, durationMs: 0, detail: 'embeddings are off' }),
            step('transcript', { durationMs: 125_000, model: 'whisper-small' }),
          ],
        }),
      }),
    });
    renderPanel(w);
    const toggle = await screen.findByRole('button', { name: 'How this was analysed' });
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await userEvent.click(toggle);
    const steps = screen.getByRole('list', { name: 'Analysis steps' });
    const items = within(steps).getAllByRole('listitem');
    expect(items).toHaveLength(4);
    expect(items[0]).toHaveTextContent('TagsDone');
    expect(items[0]).toHaveTextContent('clip-vit-b32 · 420 ms');
    expect(items[1]).toHaveTextContent('Text in the pictureFailed');
    expect(items[1]).toHaveTextContent('ocr-engine · 1.8 s');
    expect(items[1]).toHaveTextContent('The OCR service refused the file');
    expect(items[2]).toHaveTextContent('Picture searchSkipped');
    expect(items[2]).toHaveTextContent('embeddings are off');
    expect(items[3]).toHaveTextContent('2 min 5 s');
    expect(screen.getByText(/Took 4\.0 s · 2 attempts/)).toBeInTheDocument();
  });

  it('says where the analysis happened: on your own servers, or outside the network', async () => {
    const own = renderPanel(newWorld({ ai: ai({ embeddingsReady: true }) }));
    const card = await panel();
    expect(within(card).getByText('Analysed on your own servers')).toBeInTheDocument();
    expect(within(card).getByText('Included in Smart search')).toBeInTheDocument();
    own.unmount();
    queryClient.clear();

    renderPanel(newWorld({ caps: caps({ provider: 'openai-compatible', sendsDataOffSite: true }), ai: ai() }));
    expect(await screen.findByText('Analysed by an external AI service outside your network')).toBeInTheDocument();
    expect(screen.queryByText('Included in Smart search')).not.toBeInTheDocument();
  });
});

// ---- header chip and page wiring ----------------------------------------------------------------------------------

describe('On the asset page', () => {
  const page = (w: World, over: Record<string, unknown> = {}) => {
    const base = backend(w);
    return (c: Call): Response | Promise<Response> | undefined => {
      const mine = base(c);
      if (mine) return mine;
      if (c.pathname === '/api/v1/assets/a1' && c.method === 'GET') return json(asset(over));
      if (c.pathname === '/api/v1/schemas/image' || c.pathname === '/api/v1/schemas/video')
        return json({
          assetType: 'image',
          version: 1,
          isCurrent: true,
          changeNote: '',
          createdAt: '2026-01-01T00:00:00Z',
          fields: [],
        });
      if (c.pathname === '/api/v1/assets/a1/versions') return json([]);
      if (c.pathname === '/api/v1/assets/a1/rights') return json({ assetId: 'a1', rights: null });
      if (c.pathname === '/api/v1/assets/a1/workflow') return json({ instance: null });
      return undefined;
    };
  };

  it('puts the panel on the Details tab and an "AI suggestions" chip beside the status that jumps to it', async () => {
    const w = newWorld({ ai: TAGS_READY() });
    mockApi(page(w), { permissions: EDITOR });
    renderAt('/library/assets/a1');
    const card = await panel();
    expect(within(card).getByText('sunset')).toBeInTheDocument();
    const chip = await screen.findByRole('button', { name: '3 AI suggestions' });
    await userEvent.click(chip);
    expect(screen.getByRole('heading', { name: 'AI insights' })).toHaveFocus();
  });

  it('shows no chip when nothing is waiting, or to a reader', async () => {
    const w = newWorld({ ai: ai({ suggestions: [sug('s1', 'sunset', 0.8, 'accepted')] }) });
    mockApi(page(w), { permissions: EDITOR });
    const first = renderAt('/library/assets/a1');
    await panel();
    expect(screen.queryByRole('button', { name: /AI suggestion/ })).not.toBeInTheDocument();
    first.unmount();
    queryClient.clear();

    const w2 = newWorld({ ai: TAGS_READY() });
    mockApi(page(w2), { permissions: READER });
    renderAt('/library/assets/a1');
    await panel();
    expect(screen.queryByRole('button', { name: /AI suggestion/ })).not.toBeInTheDocument();
  });

  it('highlights the linked transcript line on a draft video (no player), without trying to seek', async () => {
    const w = newWorld({ ai: ai({ transcript: transcript() }) });
    mockApi(page(w, { type: 'video' }), { permissions: EDITOR });
    renderAt('/library/assets/a1?t=83');
    const list = await screen.findByRole('list', { name: 'Transcript lines' });
    expect(list.querySelector('[aria-current]')).toHaveTextContent('Thank you for coming.');
  });

  it('leaves the rest of the Details tab alone when AI is off', async () => {
    const w = newWorld({ caps: caps({ enabled: false, enrichment: false }), ai: ai({ status: 'disabled', run: null }) });
    mockApi(page(w), { permissions: EDITOR });
    renderAt('/library/assets/a1');
    expect(await screen.findByRole('region', { name: 'Rights' })).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'AI insights' })).not.toBeInTheDocument();
  });
});

// ---- accessibility ------------------------------------------------------------------------------------------------

describe('Accessibility', () => {
  it('has no axe violations when ready, with everything open', async () => {
    const w = newWorld({
      ai: ai({
        suggestions: [
          sug('s1', 'sunset', 0.82),
          sug('s2', 'beach', 0.64),
          sug('l1', 'en', 0.9, 'pending', 'language'),
          sug('s3', 'sea', 0.3, 'accepted'),
        ],
        ocrText: 'SPRING SALE',
        ocrLanguage: 'en',
        transcript: transcript(),
        embeddingsReady: true,
      }),
    });
    const { container } = renderPanel(w, { asset: asset({ type: 'video' }), player: true });
    await screen.findByRole('list', { name: 'Transcript lines' });
    for (const name of [/Text found in the picture/, 'How this was analysed', /Decided/])
      await userEvent.click(screen.getByRole('button', { name }));
    expect(await axe(container)).toHaveNoViolations();
  });

  it('has no axe violations for a reader, with a plain transcript', async () => {
    const w = newWorld({ ai: ai({ suggestions: [sug('s1', 'sunset', 0.82)], transcript: transcript() }) });
    const { container } = renderPanel(w, { asset: asset({ type: 'audio' }), permissions: READER });
    await screen.findByRole('list', { name: 'Transcript lines' });
    expect(await axe(container)).toHaveNoViolations();
  });

  it.each([
    ['not analysed', () => ai({ status: 'none', run: null })],
    ['waiting', () => ai({ status: 'pending', run: run({ status: 'pending', steps: [], error: 'busy' }) })],
    ['running', () => ai({ status: 'running', run: run({ status: 'running', steps: [step('tags')] }) })],
    ['failed', () => ai({ status: 'failed', run: run({ status: 'failed', steps: [], error: 'boom' }) })],
    ['turned off', () => ai({ status: 'disabled', run: null, ocrText: 'x' })],
  ])('has no axe violations when %s', async (_name, make) => {
    const { container } = renderPanel(newWorld({ ai: make() }));
    await panel();
    expect(await axe(container)).toHaveNoViolations();
  });
});
