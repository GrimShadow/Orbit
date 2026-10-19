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
        if (call.pathname === '/api/v1/folders') return Promise.resolve(json(FOLDERS));
        throw new Error(`unexpected request: ${call.method} ${call.pathname}`);
      }
      throw new Error(`unexpected raw fetch: ${String(input)}`);
    }),
  );
}

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
];

function asset(overrides: Record<string, unknown>) {
  return {
    id: 'a1',
    folderId: 'f1',
    type: 'image',
    status: 'draft',
    title: 'Hero shot',
    metadata: {},
    metadataSchemaVersion: 1,
    taxonomyTerms: [],
    tags: ['existing'],
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

const ASSETS = [
  asset({ id: 'a1', title: 'Hero shot', status: 'draft', tags: ['existing'] }),
  asset({ id: 'a2', title: 'Second shot', status: 'draft', tags: [] }),
];

beforeEach(() => {
  queryClient.clear();
  setLanguage('en');
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('bulk edit', () => {
  it('selects assets in grid view and shows the bulk edit bar with the count', async () => {
    mockApi((call) => (call.pathname === '/api/v1/assets' ? json({ items: ASSETS, nextCursor: null }) : undefined));
    renderAt('/library');

    await screen.findByText('Hero shot');
    expect(screen.queryByRole('region', { name: /selected/ })).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole('checkbox', { name: 'Select Hero shot' }));
    expect(await screen.findByRole('region', { name: '1 asset selected' })).toBeInTheDocument();

    await userEvent.click(screen.getByRole('checkbox', { name: 'Select Second shot' }));
    expect(await screen.findByRole('region', { name: '2 assets selected' })).toBeInTheDocument();
  });

  it("adds a tag to every selected asset, preserving each one's existing tags", async () => {
    const patches: { id: string; tags: string[] }[] = [];
    mockApi((call) => {
      if (call.pathname === '/api/v1/assets') return json({ items: ASSETS, nextCursor: null });
      const m = /\/api\/v1\/assets\/(a\d)$/.exec(call.pathname);
      if (m && call.method === 'PATCH') {
        return (async () => {
          const body = (await call.json()) as { tags: string[] };
          patches.push({ id: m[1]!, tags: body.tags });
          return json(asset({ id: m[1], tags: body.tags }));
        })();
      }
      return undefined;
    });
    renderAt('/library');

    await screen.findByText('Hero shot');
    await userEvent.click(screen.getByLabelText('Select all 2 assets on this page'));
    expect(await screen.findByRole('region', { name: '2 assets selected' })).toBeInTheDocument();

    const bar = screen.getByRole('region', { name: '2 assets selected' });
    await userEvent.click(within(bar).getByRole('button', { name: 'Tags' })); // opens the tags panel
    await userEvent.type(within(bar).getByLabelText('Tags'), 'campaign{Enter}'); // Enter turns the text into a chip
    await userEvent.click(within(bar).getByRole('button', { name: 'Add tags' }));

    await waitFor(() => expect(patches).toHaveLength(2));
    expect(patches.find((p) => p.id === 'a1')?.tags.sort()).toEqual(['campaign', 'existing']);
    expect(patches.find((p) => p.id === 'a2')?.tags).toEqual(['campaign']);
    expect(await screen.findByText('2 assets updated')).toBeInTheDocument();
    // Selection clears once the bulk action settles.
    expect(screen.queryByRole('region', { name: /selected/ })).not.toBeInTheDocument();
  });

  it('moves selected assets to a chosen folder', async () => {
    const moves: { id: string; folderId: unknown }[] = [];
    mockApi((call) => {
      if (call.pathname === '/api/v1/assets') return json({ items: ASSETS, nextCursor: null });
      const m = /\/api\/v1\/assets\/(a\d)\/move$/.exec(call.pathname);
      if (m) {
        return (async () => {
          const body = (await call.json()) as { folderId: unknown };
          moves.push({ id: m[1]!, folderId: body.folderId });
          return json(asset({ id: m[1], folderId: body.folderId }));
        })();
      }
      return undefined;
    });
    renderAt('/library');

    await screen.findByText('Hero shot');
    await userEvent.click(screen.getByRole('checkbox', { name: 'Select Hero shot' }));
    await screen.findByRole('region', { name: '1 asset selected' });

    await userEvent.click(screen.getByRole('button', { name: 'Move to…' })); // opens the move panel
    expect(screen.getByRole('button', { name: 'Move' })).toBeDisabled();
    await userEvent.selectOptions(screen.getByLabelText('Move to folder'), 'Brand');
    expect(screen.getByRole('button', { name: 'Move' })).toBeEnabled();
    await userEvent.click(screen.getByRole('button', { name: 'Move' }));

    await waitFor(() => expect(moves).toEqual([{ id: 'a1', folderId: 'f1' }]));
    expect(await screen.findByText('1 asset updated')).toBeInTheDocument();
  });

  it('only offers lifecycle actions valid for every selected asset, and transitions them all', async () => {
    const transitioned: string[] = [];
    mockApi((call) => {
      if (call.pathname === '/api/v1/assets')
        return json({
          items: [
            asset({ id: 'a1', title: 'Hero shot', status: 'draft' }),
            asset({ id: 'a2', title: 'Second shot', status: 'in_review' }),
          ],
          nextCursor: null,
        });
      const m = /\/api\/v1\/assets\/(a\d)\/transitions\/(\w+)$/.exec(call.pathname);
      if (m) {
        transitioned.push(`${m[1]}:${m[2]}`);
        return json(asset({ id: m[1], status: 'in_review' }));
      }
      return undefined;
    });
    renderAt('/library');

    await screen.findByText('Hero shot');
    await userEvent.click(screen.getByLabelText('Select all 2 assets on this page'));
    const bar = await screen.findByRole('region', { name: '2 assets selected' });

    // draft's only action is "submit"; in_review's are "approve"/"reject"/"withdraw" - no action is common to both.
    expect(within(bar).queryByRole('button', { name: 'Submit for review' })).not.toBeInTheDocument();
    expect(within(bar).queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument();
    expect(transitioned).toEqual([]);
  });

  it('saves the current selection as a static collection', async () => {
    let created: { name: string; type: string; assetIds: string[] } | undefined;
    mockApi((call) => {
      if (call.pathname === '/api/v1/assets') return json({ items: ASSETS, nextCursor: null });
      if (call.pathname === '/api/v1/collections' && call.method === 'POST') {
        return (async () => {
          created = (await call.json()) as typeof created;
          return json(
            {
              id: 'c1',
              name: created!.name,
              type: 'static',
              query: null,
              assetIds: created!.assetIds,
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
    renderAt('/library');

    await screen.findByText('Hero shot');
    await userEvent.click(screen.getByLabelText('Select all 2 assets on this page'));
    const bar = await screen.findByRole('region', { name: '2 assets selected' });

    await userEvent.click(within(bar).getByRole('button', { name: 'Save as collection' }));
    const dialog = await screen.findByRole('dialog', { name: 'Save selection as a collection' });
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Hero shots');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(created?.name).toBe('Hero shots'));
    expect(created?.type).toBe('static');
    expect(created?.assetIds.sort()).toEqual(['a1', 'a2']);
    expect(await screen.findByText('Collection "Hero shots" created')).toBeInTheDocument();
    // Selection clears once the collection is created.
    expect(screen.queryByRole('region', { name: /selected/ })).not.toBeInTheDocument();
  });

  it('offers a shared lifecycle action when every selected asset is in the same status', async () => {
    const transitioned: string[] = [];
    mockApi((call) => {
      if (call.pathname === '/api/v1/assets')
        return json({
          items: [
            asset({ id: 'a1', title: 'Hero shot', status: 'draft' }),
            asset({ id: 'a2', title: 'Second shot', status: 'draft' }),
          ],
          nextCursor: null,
        });
      const m = /\/api\/v1\/assets\/(a\d)\/transitions\/(\w+)$/.exec(call.pathname);
      if (m) {
        transitioned.push(`${m[1]}:${m[2]}`);
        return json(asset({ id: m[1], status: 'in_review' }));
      }
      return undefined;
    });
    renderAt('/library');

    await screen.findByText('Hero shot');
    await userEvent.click(screen.getByLabelText('Select all 2 assets on this page'));
    const bar = await screen.findByRole('region', { name: '2 assets selected' });

    await userEvent.click(within(bar).getByRole('button', { name: 'Submit for review' }));
    await waitFor(() => expect(transitioned.sort()).toEqual(['a1:submit', 'a2:submit']));
    expect(await screen.findByText('2 assets updated')).toBeInTheDocument();
  });
  it('says why when the server refuses an approve because a workflow is running', async () => {
    mockApi((call) => {
      if (call.pathname === '/api/v1/assets')
        return json({
          items: [
            asset({ id: 'a1', title: 'Hero shot', status: 'in_review' }),
            asset({ id: 'a2', title: 'Second shot', status: 'in_review' }),
          ],
          nextCursor: null,
        });
      if (/\/transitions\/approve$/.test(call.pathname))
        return new Response(
          JSON.stringify({ title: 'Conflict', status: 409, detail: 'A workflow is running: decide via the workflow task.' }),
          { status: 409, headers: { 'Content-Type': 'application/problem+json' } },
        );
      return undefined;
    });
    renderAt('/library');
    await screen.findByText('Hero shot');
    await userEvent.click(screen.getByLabelText('Select all 2 assets on this page'));
    const bar = await screen.findByRole('region', { name: '2 assets selected' });
    await userEvent.click(within(bar).getByRole('button', { name: 'Approve' }));
    expect(await screen.findByText('A workflow is running: decide via the workflow task.')).toBeInTheDocument();
  });
});
