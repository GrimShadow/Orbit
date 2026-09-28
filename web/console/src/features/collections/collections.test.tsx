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

function renderAt(route: string) {
  return render(
    <AuthContext.Provider value={signedIn}>
      <MemoryRouter initialEntries={[route]}>
        <App />
      </MemoryRouter>
    </AuthContext.Provider>,
  );
}

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

interface Call {
  method: string;
  pathname: string;
  json: () => Promise<unknown>;
}

function mockApi(handler: (call: Call) => Response | Promise<Response> | undefined) {
  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL) => {
      if (input instanceof Request) {
        const call: Call = { method: input.method, pathname: new URL(input.url).pathname, json: () => input.json() };
        const response = handler(call);
        if (response) return Promise.resolve(response);
        throw new Error(`unexpected request: ${call.method} ${call.pathname}`);
      }
      throw new Error(`unexpected raw fetch: ${String(input)}`);
    }),
  );
}

function collection(overrides: Record<string, unknown>) {
  return {
    id: 'c1',
    name: 'Campaign hero shots',
    type: 'static',
    query: null,
    assetIds: ['a1'],
    visibility: 'private',
    ownerId: 'u1',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

beforeEach(() => {
  queryClient.clear();
  setLanguage('en');
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('CollectionsPage', () => {
  it('lists collections with their type and visibility', async () => {
    mockApi((call) => (call.pathname === '/api/v1/collections' && call.method === 'GET' ? json([collection({})]) : undefined));
    renderAt('/collections');

    expect(await screen.findByRole('heading', { name: 'Collections' })).toBeInTheDocument();
    expect(await screen.findByRole('link', { name: 'Campaign hero shots' })).toBeInTheDocument();
    expect(screen.getByText('Static')).toBeInTheDocument();
    expect(screen.getByText('Private')).toBeInTheDocument();
  });

  it('shows an empty message when there are none', async () => {
    mockApi((call) => (call.pathname === '/api/v1/collections' && call.method === 'GET' ? json([]) : undefined));
    renderAt('/collections');
    expect(await screen.findByText('No collections yet.')).toBeInTheDocument();
  });

  it('deletes a collection after confirming', async () => {
    let deleted = false;
    mockApi((call) => {
      if (call.pathname === '/api/v1/collections' && call.method === 'GET') return json(deleted ? [] : [collection({})]);
      if (call.pathname === '/api/v1/collections/c1' && call.method === 'DELETE') {
        deleted = true;
        return json(null);
      }
      return undefined;
    });
    renderAt('/collections');
    await screen.findByRole('link', { name: 'Campaign hero shots' });

    await userEvent.click(screen.getByRole('button', { name: 'Delete' }));
    const dialog = await screen.findByRole('dialog', { name: 'Delete this collection?' });
    expect(
      within(dialog).getByText('This deletes "Campaign hero shots" for everyone it is shared with. This cannot be undone.'),
    ).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Delete' }));

    await waitFor(() => expect(deleted).toBe(true));
  });
});

describe('CollectionDetailPage', () => {
  it('shows the collection, its contents, and lets it be renamed', async () => {
    let currentName = 'Campaign hero shots';
    mockApi((call) => {
      if (call.pathname === '/api/v1/collections/c1' && call.method === 'GET') return json(collection({ name: currentName }));
      if (call.pathname === '/api/v1/collections/c1/assets')
        return json({
          hits: [
            {
              id: 'a1',
              type: 'image',
              status: 'published',
              title: 'Hero shot',
              folderId: null,
              folderPath: null,
              tags: [],
              taxonomyTerms: [],
              ownerId: null,
              publishAt: null,
              expireAt: null,
              createdAt: '2026-01-01T00:00:00Z',
              updatedAt: '2026-01-01T00:00:00Z',
              score: 0,
              highlights: null,
            },
          ],
          total: 1,
          facets: [],
          nextCursor: null,
        });
      if (call.pathname === '/api/v1/collections/c1' && call.method === 'PUT') {
        return (async () => {
          const body = (await call.json()) as { name: string };
          currentName = body.name;
          return json(collection({ name: currentName }));
        })();
      }
      return undefined;
    });
    renderAt('/collections/c1');

    expect(await screen.findByRole('heading', { name: 'Campaign hero shots' })).toBeInTheDocument();
    expect(await screen.findByText('Hero shot')).toBeInTheDocument();

    const nameInput = screen.getByLabelText('Name');
    await userEvent.clear(nameInput);
    await userEvent.type(nameInput, 'Renamed collection');
    await userEvent.click(screen.getByRole('button', { name: 'Rename' }));

    expect(await screen.findByRole('heading', { name: 'Renamed collection' })).toBeInTheDocument();
  });

  it('changes visibility via the share control', async () => {
    let visibility = 'private';
    mockApi((call) => {
      if (call.pathname === '/api/v1/collections/c1' && call.method === 'GET') return json(collection({ visibility }));
      if (call.pathname === '/api/v1/collections/c1/assets') return json({ hits: [], total: 0, facets: [], nextCursor: null });
      if (call.pathname === '/api/v1/collections/c1/share') {
        return (async () => {
          const body = (await call.json()) as { visibility: string };
          visibility = body.visibility;
          return json(collection({ visibility }));
        })();
      }
      return undefined;
    });
    renderAt('/collections/c1');
    await screen.findByRole('heading', { name: 'Campaign hero shots' });

    await userEvent.selectOptions(screen.getByLabelText('Visibility'), 'Tenant');
    await waitFor(() => expect(visibility).toBe('tenant'));
  });

  it('deletes the collection and navigates back to the list', async () => {
    mockApi((call) => {
      if (call.pathname === '/api/v1/collections/c1' && call.method === 'GET') return json(collection({}));
      if (call.pathname === '/api/v1/collections/c1/assets') return json({ hits: [], total: 0, facets: [], nextCursor: null });
      if (call.pathname === '/api/v1/collections/c1' && call.method === 'DELETE') return json(null);
      if (call.pathname === '/api/v1/collections' && call.method === 'GET') return json([]);
      return undefined;
    });
    renderAt('/collections/c1');
    await screen.findByRole('heading', { name: 'Campaign hero shots' });

    await userEvent.click(screen.getByRole('button', { name: 'Delete' }));
    const dialog = await screen.findByRole('dialog', { name: 'Delete this collection?' });
    await userEvent.click(within(dialog).getByRole('button', { name: 'Delete' }));

    expect(await screen.findByRole('heading', { name: 'Collections' })).toBeInTheDocument();
  });
});
