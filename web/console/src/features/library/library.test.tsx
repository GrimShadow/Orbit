import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
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
    label: 'Brand',
    name: 'brand',
    inheritedMetadata: {},
    defaultWorkflowId: null,
    createdAt: '2026-01-01T00:00:00Z',
  },
  {
    id: 'f2',
    parentId: 'f1',
    path: 'brand.roadster',
    label: 'Roadster',
    name: 'roadster',
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

function mockApi(assetsPage: { items: unknown[]; nextCursor: string | null } = { items: ASSETS, nextCursor: null }) {
  const calls: Request[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn((request: Request) => {
      calls.push(request);
      const url = new URL(request.url);
      if (url.pathname === '/api/v1/folders') return Promise.resolve(json(FOLDERS));
      if (url.pathname === '/api/v1/assets') return Promise.resolve(json(assetsPage));
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

  it('shows a next-page button only when the API reports more results, and it asks for the next cursor', async () => {
    const calls = mockApi({ items: ASSETS, nextCursor: 'a1' });
    renderAt('/library');
    await screen.findByText('Hero shot');

    const next = await screen.findByRole('button', { name: 'Next page' });
    await userEvent.click(next);
    await waitFor(() => {
      const last = calls.at(-1)!;
      expect(new URL(last.url).searchParams.get('cursor')).toBe('a1');
    });
  });

  it('shows an empty message when no assets match', async () => {
    mockApi({ items: [], nextCursor: null });
    renderAt('/library');
    expect(await screen.findByText('No assets match these filters.')).toBeInTheDocument();
  });
});
