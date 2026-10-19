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

/** `thumbnailLinks` answers the grid's own batch thumbnail-link request (defaulting every id to no thumbnail) so
 * tests that don't care about it don't each need to teach `handler` about a request they never asked for. */
function mockApi(
  handler: (call: Call) => Response | Promise<Response> | undefined,
  thumbnailLinks: Record<string, string | null> = {},
) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      if (input instanceof Request) {
        const call: Call = { method: input.method, pathname: new URL(input.url).pathname, json: () => input.json() };
        if (call.pathname === '/api/v1/assets/links/batch') {
          const { assetIds } = (await call.json()) as { assetIds: string[] };
          return json(Object.fromEntries(assetIds.map((id) => [id, thumbnailLinks[id] ?? null])));
        }
        const response = handler(call);
        if (response) return response;
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
    expect(await screen.findByRole('heading', { name: 'No collections yet' })).toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: 'New collection' }).length).toBeGreaterThan(0);
  });

  it('shows a cover from the first assets of a static collection and a count', async () => {
    mockApi(
      (call) =>
        call.pathname === '/api/v1/collections' && call.method === 'GET'
          ? json([collection({ assetIds: ['a1', 'a2'] })])
          : undefined,
      { a1: 'https://cdn.test/a1-thumb.webp', a2: 'https://cdn.test/a2-thumb.webp' },
    );
    const { container } = renderAt('/collections');
    await screen.findByRole('link', { name: 'Campaign hero shots' });
    await waitFor(() => expect(container.querySelectorAll('.dam-cover img')).toHaveLength(2));
    expect(screen.getByText(/2 assets/)).toBeInTheDocument();
  });

  it('creates an empty static collection from the New collection dialog and opens it', async () => {
    let created: { name: string; type: string; assetIds: string[] } | undefined;
    mockApi((call) => {
      if (call.pathname === '/api/v1/collections' && call.method === 'GET') return json([]);
      if (call.pathname === '/api/v1/collections' && call.method === 'POST') {
        return (async () => {
          created = (await call.json()) as typeof created;
          return json(collection({ id: 'c9', name: created!.name, assetIds: [] }), 201);
        })();
      }
      if (call.pathname === '/api/v1/collections/c9' && call.method === 'GET')
        return json(collection({ id: 'c9', name: 'Spring review', assetIds: [] }));
      if (call.pathname === '/api/v1/collections/c9/assets') return json({ hits: [], total: 0, facets: [], nextCursor: null });
      return undefined;
    });
    renderAt('/collections');
    await screen.findByRole('heading', { name: 'No collections yet' });

    await userEvent.click(screen.getAllByRole('button', { name: 'New collection' })[0]!);
    const dialog = await screen.findByRole('dialog', { name: 'New collection' });
    expect(within(dialog).getByRole('button', { name: 'Create' })).toBeDisabled();
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Spring review');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create' }));

    await waitFor(() => expect(created).toMatchObject({ name: 'Spring review', type: 'static', assetIds: [] }));
    expect(await screen.findByRole('heading', { name: 'Spring review' })).toBeInTheDocument();
    expect(await screen.findByRole('heading', { name: 'This collection is empty' })).toBeInTheDocument();
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

    await userEvent.click(screen.getByRole('button', { name: 'Actions for Campaign hero shots' }));
    await userEvent.click(screen.getByRole('menuitem', { name: 'Delete…' }));
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

    await userEvent.click(screen.getByRole('button', { name: 'Rename collection' }));
    const nameInput = screen.getByLabelText('Name');
    await userEvent.clear(nameInput);
    await userEvent.type(nameInput, 'Renamed collection');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByRole('heading', { name: 'Renamed collection' })).toBeInTheDocument();
  });

  it('shows a real thumbnail image once the batch link request resolves', async () => {
    mockApi(
      (call) => {
        if (call.pathname === '/api/v1/collections/c1' && call.method === 'GET') return json(collection({}));
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
        return undefined;
      },
      { a1: 'https://cdn.test/a1-thumb.webp' },
    );
    const { container } = renderAt('/collections/c1');
    await screen.findByText('Hero shot');

    const img = await waitFor(() => {
      const el = container.querySelector('img.dam-thumb__img');
      if (!el) throw new Error('thumbnail image not rendered yet');
      return el;
    });
    expect(img).toHaveAttribute('src', 'https://cdn.test/a1-thumb.webp');
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

    await userEvent.click(within(screen.getByRole('group', { name: 'Visibility' })).getByRole('button', { name: 'Tenant' }));
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

    await userEvent.click(screen.getByRole('button', { name: 'More actions' }));
    await userEvent.click(screen.getByRole('menuitem', { name: 'Delete…' }));
    const dialog = await screen.findByRole('dialog', { name: 'Delete this collection?' });
    await userEvent.click(within(dialog).getByRole('button', { name: 'Delete' }));

    expect(await screen.findByRole('heading', { name: 'Collections' })).toBeInTheDocument();
  });
});
