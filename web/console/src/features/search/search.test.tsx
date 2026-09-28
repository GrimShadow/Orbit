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

const RESULT = {
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
      updatedAt: '2026-01-02T00:00:00Z',
      score: 1,
      highlights: null,
    },
  ],
  total: 1,
  facets: [
    { field: 'type', values: [{ value: 'image', count: 1 }] },
    { field: 'status', values: [{ value: 'published', count: 1 }] },
  ],
  nextCursor: null,
};

beforeEach(() => {
  queryClient.clear();
  setLanguage('en');
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('SearchPage', () => {
  it('runs an initial browse-all search and shows results with facets', async () => {
    mockApi((call) => (call.pathname === '/api/v1/search' ? json(RESULT) : undefined));
    renderAt('/search');

    expect(await screen.findByRole('heading', { name: 'Search' })).toBeInTheDocument();
    expect(await screen.findByText('Hero shot')).toBeInTheDocument();
    expect(screen.getByRole('group', { name: 'Type' })).toBeInTheDocument();
    expect(screen.getByRole('checkbox', { name: /Image/ })).toBeInTheDocument();
  });

  it('re-queries with the typed text', async () => {
    const queries: (string | null)[] = [];
    mockApi((call) => {
      if (call.pathname === '/api/v1/search') {
        return (async () => {
          const body = (await call.json()) as { query: string | null };
          queries.push(body.query);
          return json(RESULT);
        })();
      }
      return undefined;
    });
    renderAt('/search');
    await screen.findByText('Hero shot');

    await userEvent.type(screen.getByLabelText('Search assets'), 'roadster');
    await waitFor(() => expect(queries.at(-1)).toBe('roadster'));
  });

  it('filters by a facet checkbox', async () => {
    const filterCalls: unknown[] = [];
    mockApi((call) => {
      if (call.pathname === '/api/v1/search') {
        return (async () => {
          const body = (await call.json()) as { filters: unknown };
          filterCalls.push(body.filters);
          return json(RESULT);
        })();
      }
      return undefined;
    });
    renderAt('/search');
    await screen.findByText('Hero shot');

    await userEvent.click(screen.getByRole('checkbox', { name: /Image/ }));
    await waitFor(() => expect(filterCalls.at(-1)).toEqual({ type: ['image'] }));
  });

  it('saves the current query as a named smart collection', async () => {
    let created: { name: string; type: string; query: unknown } | undefined;
    mockApi((call) => {
      if (call.pathname === '/api/v1/search') return json(RESULT);
      if (call.pathname === '/api/v1/collections' && call.method === 'POST') {
        return (async () => {
          created = (await call.json()) as typeof created;
          return json(
            {
              id: 'c1',
              name: created!.name,
              type: 'smart',
              query: created!.query,
              assetIds: [],
              visibility: 'private',
              ownerId: 'u1',
              createdAt: '2026-01-01T00:00:00Z',
              updatedAt: '2026-01-01T00:00:00Z',
            },
            201,
          );
        })();
      }
      return undefined;
    });
    renderAt('/search');
    await screen.findByText('Hero shot');

    await userEvent.click(screen.getByRole('button', { name: 'Save this search' }));
    const dialog = await screen.findByRole('dialog', { name: 'Save this search as a collection' });
    await userEvent.type(within(dialog).getByLabelText('Name'), 'My saved search');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(created?.name).toBe('My saved search'));
    expect(created?.type).toBe('smart');
    expect(await screen.findByText('Saved search "My saved search" created')).toBeInTheDocument();
  });

  it('shows an empty message when nothing matches', async () => {
    mockApi((call) =>
      call.pathname === '/api/v1/search' ? json({ hits: [], total: 0, facets: [], nextCursor: null }) : undefined,
    );
    renderAt('/search');
    expect(await screen.findByText('No assets match this search.')).toBeInTheDocument();
  });

  it('shows a real thumbnail image once the batch link request resolves', async () => {
    mockApi((call) => (call.pathname === '/api/v1/search' ? json(RESULT) : undefined), { a1: 'https://cdn.test/a1-thumb.webp' });
    const { container } = renderAt('/search');
    await screen.findByText('Hero shot');

    const img = await waitFor(() => {
      const el = container.querySelector('img.dam-thumb__img');
      if (!el) throw new Error('thumbnail image not rendered yet');
      return el;
    });
    expect(img).toHaveAttribute('src', 'https://cdn.test/a1-thumb.webp');
  });
});
