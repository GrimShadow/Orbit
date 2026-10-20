import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { App, queryClient } from '../../App';
import { AuthContext, type AuthState } from '../../auth/AuthProvider';
import { setLanguage } from '../../i18n';

const signedIn: AuthState = {
  status: 'authenticated',
  name: 'Dev Admin',
  email: 'admin@dam.local',
  accessToken: 'tok',
  signIn: vi.fn(async () => {}),
  signOut: vi.fn(async () => {}),
};

const FOLDERS = [
  {
    id: 'f1',
    parentId: null,
    path: 'brand',
    label: 'brand',
    name: 'Brand',
    inheritedMetadata: {},
    defaultWorkflowId: null,
    createdAt: '2026-01-01T00:00:00Z',
  },
  {
    id: 'f2',
    parentId: 'f1',
    path: 'brand.roadster',
    label: 'roadster',
    name: 'Roadster',
    inheritedMetadata: {},
    defaultWorkflowId: null,
    createdAt: '2026-01-01T00:00:00Z',
  },
];

interface TestAsset {
  id: string;
  folderId: string | null;
  type: string;
  status: string;
  title: string;
  metadata: Record<string, never>;
  metadataSchemaVersion: number;
  taxonomyTerms: string[];
  tags: string[];
  attributes: Record<string, string[]>;
  ownerId: string | null;
  currentVersionId: string | null;
  checksumSha256: string | null;
  statusReason: string;
  publishAt: string | null;
  expireAt: string | null;
  retentionPolicyId: string | null;
  legalHold: boolean;
  createdAt: string;
  updatedAt: string;
}

function asset(overrides: Partial<TestAsset>): TestAsset {
  return {
    id: 'a1',
    folderId: 'f1',
    type: 'image',
    status: 'published',
    title: 'Hero shot',
    metadata: {},
    metadataSchemaVersion: 1,
    taxonomyTerms: [],
    tags: [],
    attributes: {},
    ownerId: null,
    currentVersionId: null,
    checksumSha256: null,
    statusReason: '',
    publishAt: null,
    expireAt: null,
    retentionPolicyId: null,
    legalHold: false,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-02T00:00:00Z',
    ...overrides,
  };
}
const ASSETS = [asset({})];

function renderAt(route: string) {
  return render(
    <AuthContext.Provider value={signedIn}>
      <MemoryRouter initialEntries={[route]}>
        <App />
      </MemoryRouter>
    </AuthContext.Provider>,
  );
}

const json = (body: unknown) =>
  new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

/** The asset ids of each `links/batch` request the last `mockApi` saw. */
let batchIds: string[][] = [];

function mockApi(
  assetsPage: { items: unknown[]; nextCursor: string | null } = { items: ASSETS, nextCursor: null },
  thumbnailLinks: Record<string, string | null> = {},
) {
  const calls: Request[] = [];
  batchIds = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      calls.push(request);
      const url = new URL(request.url);
      if (url.pathname === '/api/v1/folders') return json(FOLDERS);
      if (url.pathname === '/api/v1/assets') return json(assetsPage);
      if (url.pathname === '/api/v1/assets/links/batch') {
        const { assetIds } = (await request.json()) as { assetIds: string[] };
        batchIds.push(assetIds);
        return json(Object.fromEntries(assetIds.map((id) => [id, thumbnailLinks[id] ?? null])));
      }
      throw new Error(`unexpected request: ${url.pathname}`);
    }),
  );
  return calls;
}

beforeEach(() => {
  queryClient.clear();
  setLanguage('en');
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('LibraryPage', () => {
  it('shows the folder tree and the current folder’s assets as a grid by default', async () => {
    mockApi();
    renderAt('/library');
    expect(await screen.findByRole('heading', { name: 'Library' })).toBeInTheDocument();
    expect(await screen.findByRole('treeitem', { name: 'Brand' })).toBeInTheDocument();
    expect(await screen.findByRole('list', { name: 'Assets' })).toBeInTheDocument();
    expect(await screen.findByText('Hero shot')).toBeInTheDocument();
  });

  it('re-fetches assets scoped to the selected folder', async () => {
    const calls = mockApi();
    renderAt('/library');
    await screen.findByText('Hero shot');

    await userEvent.click(await screen.findByRole('treeitem', { name: 'Brand' })); // expands it, revealing the child
    // Once expanded, an ancestor treeitem's accessible name recursively includes its descendants' text (a treeitem's
    // accname is not scoped to its own row) - "Brand" itself now computes to "Brand Roadster" - so a role+name query
    // for "Roadster" alone would match both; its own rendered text is unambiguous instead.
    await userEvent.click((await screen.findByText('Roadster')).closest('[role="treeitem"]')!);
    await waitFor(() => {
      const last = calls.at(-1)!;
      expect(new URL(last.url).searchParams.get('folderId')).toBe('f2');
    });
  });

  it('switches to a list view showing the same assets in a table', async () => {
    mockApi();
    renderAt('/library');
    await screen.findByText('Hero shot');

    await userEvent.click(screen.getByRole('button', { name: 'List' }));
    const table = await screen.findByRole('table');
    expect(within(table).getByText('Hero shot')).toBeInTheDocument();
    expect(within(table).getByText('Published')).toBeInTheDocument();
  });

  it('filters by status', async () => {
    const calls = mockApi();
    renderAt('/library');
    await screen.findByText('Hero shot');

    await userEvent.selectOptions(screen.getByLabelText('Status'), 'draft');
    await waitFor(() => {
      const last = calls.at(-1)!;
      expect(new URL(last.url).searchParams.get('status')).toBe('draft');
    });
  });

  it('shows a Load more button only when the API reports more results, and it asks for the next cursor', async () => {
    const calls = mockApi({ items: ASSETS, nextCursor: 'a1' });
    renderAt('/library');
    await screen.findByText('Hero shot');
    expect(await screen.findByText('1+ assets')).toBeInTheDocument();

    const next = await screen.findByRole('button', { name: 'Load more' });
    await userEvent.click(next);
    await waitFor(() => {
      const last = calls.filter((c) => new URL(c.url).pathname === '/api/v1/assets').at(-1)!;
      expect(new URL(last.url).searchParams.get('cursor')).toBe('a1');
    });
  });

  it('invites an upload when the library has nothing in it', async () => {
    mockApi({ items: [], nextCursor: null });
    renderAt('/library');
    expect(await screen.findByRole('heading', { name: 'Your library is empty' })).toBeInTheDocument();
    expect(screen.getAllByRole('link', { name: 'Upload' }).length).toBeGreaterThan(0);
  });

  it('offers to clear the filters when they leave nothing, and clearing them restores the URL state', async () => {
    const calls = mockApi({ items: [], nextCursor: null });
    renderAt('/library?status=draft&type=video');
    expect(await screen.findByRole('heading', { name: 'No assets match these filters' })).toBeInTheDocument();
    // Each active filter is a removable chip.
    const chips = screen.getByRole('group', { name: 'Active filters' });
    expect(within(chips).getByText('Draft')).toBeInTheDocument();
    expect(within(chips).getByText('Video')).toBeInTheDocument();

    await userEvent.click(screen.getAllByRole('button', { name: 'Clear filters' })[0]!);
    await waitFor(() => {
      const last = new URL(calls.filter((c) => new URL(c.url).pathname === '/api/v1/assets').at(-1)!.url);
      expect(last.searchParams.get('status')).toBeNull();
      expect(last.searchParams.get('type')).toBeNull();
    });
    expect(screen.queryByRole('group', { name: 'Active filters' })).not.toBeInTheDocument();
  });

  it('restores folder, status, type and view from the URL', async () => {
    const calls = mockApi();
    renderAt('/library?folder=f2&status=draft&type=image&view=list');
    expect(await screen.findByRole('table')).toBeInTheDocument();
    const assetCall = calls.find((c) => new URL(c.url).pathname === '/api/v1/assets')!;
    const q = new URL(assetCall.url).searchParams;
    expect([q.get('folderId'), q.get('status'), q.get('type')]).toEqual(['f2', 'draft', 'image']);
    // The breadcrumb walks up from the restored folder.
    const crumbs = await screen.findByRole('navigation', { name: 'Folders' });
    expect(within(crumbs).getByRole('button', { name: 'Brand' })).toBeInTheDocument();
    expect(within(crumbs).getByText('Roadster')).toHaveAttribute('aria-current', 'page');
    expect(screen.getByLabelText('Status')).toHaveValue('draft');
    expect(document.title).toBe('Library - Orbit');
  });

  it('writes filter changes to the URL so back/forward can restore them', async () => {
    mockApi();
    renderAt('/library');
    await screen.findByText('Hero shot');
    await userEvent.selectOptions(screen.getByLabelText('Type'), 'video');
    expect(await screen.findByRole('group', { name: 'Active filters' })).toHaveTextContent('Video');
  });

  it('colours the status badge by lifecycle state', async () => {
    mockApi({ items: [asset({}), asset({ id: 'a2', title: 'Second', status: 'in_review' })], nextCursor: null });
    renderAt('/library');
    await screen.findByText('Hero shot');
    const grid = screen.getByRole('list', { name: 'Assets' });
    expect(within(grid).getByText('Published')).toHaveClass('dam-badge--success');
    expect(within(grid).getByText('In review')).toHaveClass('dam-badge--info');
  });

  it('selects every asset with one control, which shows the mixed state while some are selected', async () => {
    mockApi({ items: [asset({}), asset({ id: 'a2', title: 'Second' })], nextCursor: null });
    renderAt('/library');
    await screen.findByText('Hero shot');
    const all = screen.getByLabelText('Select all 2 assets on this page') as HTMLInputElement;
    await userEvent.click(screen.getByRole('checkbox', { name: 'Select Hero shot' }));
    expect(all.indeterminate).toBe(true);
    await userEvent.click(all);
    expect(await screen.findByRole('region', { name: '2 assets selected' })).toBeInTheDocument();
    expect(all.checked).toBe(true);
  });

  it('shows a real thumbnail image once the batch link request resolves', async () => {
    mockApi(undefined, { a1: 'https://cdn.test/a1-thumb.webp' });
    const { container } = renderAt('/library');
    await screen.findByText('Hero shot');

    const img = await waitFor(() => {
      const el = container.querySelector('img.dam-thumb__img');
      if (!el) throw new Error('thumbnail image not rendered yet');
      return el;
    });
    expect(img).toHaveAttribute('src', 'https://cdn.test/a1-thumb.webp');
  });

  it('does not ask for a thumbnail of an audio asset and draws its file-type icon instead of an image', async () => {
    mockApi({
      items: [asset({ id: 'img1', title: 'Hero shot' }), asset({ id: 'aud1', type: 'audio', title: 'Radio spot' })],
      nextCursor: null,
    });
    const { container } = renderAt('/library');
    await screen.findByText('Radio spot');
    await waitFor(() => expect(batchIds.length).toBeGreaterThan(0));
    expect(batchIds.flat()).toEqual(['img1']);

    const card = screen.getByText('Radio spot').closest('button')!;
    expect(card.querySelector('img')).toBeNull();
    const placeholder = card.querySelector('.dam-thumb__img--empty');
    expect(placeholder).not.toBeNull();
    expect(placeholder!.querySelector('svg')).not.toBeNull();
    expect(container.querySelectorAll('img.dam-thumb__img')).toHaveLength(0); // the image asset has no link in this test
  });

  it('never leaves a broken image: a thumbnail that fails to load becomes the file-type icon', async () => {
    mockApi(undefined, { a1: 'https://cdn.test/missing-thumb.webp' });
    const { container } = renderAt('/library');
    await screen.findByText('Hero shot');
    const img = await waitFor(() => {
      const el = container.querySelector('img.dam-thumb__img');
      if (!el) throw new Error('thumbnail image not rendered yet');
      return el;
    });
    fireEvent.error(img);
    await waitFor(() => expect(container.querySelector('img.dam-thumb__img')).toBeNull());
    expect(container.querySelector('.dam-thumb__img--empty svg')).not.toBeNull();
  });

  it('shows the thumbnail in the list view without asking for the links a second time', async () => {
    const calls = mockApi(undefined, { a1: 'https://cdn.test/a1-thumb.webp' });
    const { container } = renderAt('/library');
    await screen.findByText('Hero shot');
    const isBatchCall = (r: Request) => new URL(r.url).pathname === '/api/v1/assets/links/batch';
    await waitFor(() => expect(calls.some(isBatchCall)).toBe(true));

    const batchCallsInGrid = calls.filter(isBatchCall).length;
    await userEvent.click(screen.getByRole('button', { name: 'List' }));
    await screen.findByRole('table');
    const thumb = await waitFor(() => {
      const el = container.querySelector('img.dam-table__thumb');
      if (!el) throw new Error('list thumbnail not rendered yet');
      return el;
    });
    expect(thumb).toHaveAttribute('src', 'https://cdn.test/a1-thumb.webp');
    expect(calls.filter(isBatchCall)).toHaveLength(batchCallsInGrid); // the same cached batch serves both views
  });

  it('offers Open plus the valid lifecycle actions in a card menu, and runs one against that asset', async () => {
    const transitions: string[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (request: Request) => {
        const url = new URL(request.url);
        if (url.pathname === '/api/v1/folders') return json(FOLDERS);
        if (url.pathname === '/api/v1/assets') return json({ items: [asset({ status: 'draft' })], nextCursor: null });
        if (url.pathname === '/api/v1/assets/links/batch') return json({ a1: null });
        if (url.pathname === '/api/v1/assets/a1/transitions/submit') {
          transitions.push(url.pathname);
          return json({ ...asset({ status: 'in_review' }) });
        }
        throw new Error(`unexpected request: ${url.pathname}`);
      }),
    );
    renderAt('/library');
    await screen.findByText('Hero shot');

    await userEvent.click(screen.getByRole('button', { name: 'Actions for Hero shot' }));
    expect(screen.getByRole('menuitem', { name: 'Open details' })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('menuitem', { name: 'Submit for review' }));

    await waitFor(() => expect(transitions).toHaveLength(1));
  });
});
