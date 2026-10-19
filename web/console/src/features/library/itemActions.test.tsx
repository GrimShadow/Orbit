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

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

const FOLDER = (id: string, name: string, parentId: string | null = null) => ({
  id,
  parentId,
  path: name.toLowerCase(),
  label: name.toLowerCase(),
  name,
  inheritedMetadata: {},
  defaultWorkflowId: null,
  createdAt: '2026-01-01T00:00:00Z',
});
const FOLDERS = [FOLDER('f1', 'Brand'), FOLDER('f2', 'Roadster', 'f1')];

const asset = (over: Record<string, unknown> = {}) => ({
  id: 'a1',
  folderId: 'f1',
  type: 'image',
  status: 'draft',
  title: 'Hero shot',
  metadata: { channels: ['canva'] },
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
  ...over,
});

const SCHEMA = {
  assetType: 'image',
  version: 1,
  isCurrent: true,
  changeNote: '',
  createdAt: '2026-01-01T00:00:00Z',
  fields: [
    {
      name: 'headline',
      label: 'Headline',
      type: 'text',
      required: false,
      multi: false,
      searchable: true,
      facet: false,
      accessDimension: null,
    },
    {
      name: 'channels',
      label: 'Channels',
      type: 'vocabulary',
      vocabulary: 'channel',
      required: false,
      multi: true,
      searchable: false,
      facet: true,
      accessDimension: 'channel',
    },
  ],
};
const term = (code: string, label: string) => ({
  term: {
    id: code,
    vocabularyId: 'v1',
    parentId: null,
    code,
    path: code,
    labels: { en: label },
    sortOrder: 0,
    status: 'active',
    attributes: {},
  },
  children: [],
});

interface Call {
  method: string;
  pathname: string;
  search: URLSearchParams;
  json: () => Promise<unknown>;
}
type Handler = (call: Call) => Response | Promise<Response | undefined> | undefined;

function mockApi(extra: Handler = () => undefined, items = [asset()]) {
  const calls: Call[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      const url = new URL(request.url);
      const call: Call = { method: request.method, pathname: url.pathname, search: url.searchParams, json: () => request.json() };
      calls.push(call);
      const custom = await extra(call);
      if (custom) return custom;
      if (url.pathname === '/api/v1/folders' && call.method === 'GET') return json(FOLDERS);
      if (url.pathname === '/api/v1/assets' && call.method === 'GET') return json({ items, nextCursor: null });
      if (url.pathname === '/api/v1/assets/links/batch') return json({});
      if (url.pathname === '/api/v1/schemas/image') return json(SCHEMA);
      if (url.pathname === '/api/v1/vocabularies')
        return json([
          { id: 'v1', key: 'channel', name: 'Channels', description: '', hierarchical: false, levels: [], createdAt: '' },
        ]);
      if (url.pathname === '/api/v1/vocabularies/v1/terms') return json([term('canva', 'Canva'), term('web_api', 'Web API')]);
      if (url.pathname === '/api/v1/assets/a1' && call.method === 'GET') return json(asset());
      throw new Error(`unexpected request: ${call.method} ${url.pathname}`);
    }),
  );
  return calls;
}

function renderLibrary() {
  return render(
    <AuthContext.Provider value={signedIn}>
      <MemoryRouter initialEntries={['/library']}>
        <App />
      </MemoryRouter>
    </AuthContext.Provider>,
  );
}

beforeEach(() => {
  queryClient.clear();
  setLanguage('en');
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const rightClickCard = async () => {
  const title = await screen.findByText('Hero shot');
  fireEvent.contextMenu(title.closest('li')!, { clientX: 50, clientY: 50 });
};

describe('right-click menu on an asset', () => {
  it('offers open, rename, make available for, lifecycle and delete', async () => {
    mockApi();
    renderLibrary();
    await rightClickCard();
    const menu = await screen.findByRole('menu', { name: 'Actions for Hero shot' });
    const names = within(menu)
      .getAllByRole('menuitem')
      .map((i) => i.textContent);
    expect(names).toEqual(['Open details', 'Rename…', 'Make available for', 'Submit for review', 'Delete…']);
  });

  it("lists the systems with the asset's current ones checked, and toggles one by saving the whole channels list", async () => {
    let patched: { metadata: Record<string, unknown> } | undefined;
    const calls = mockApi(async (c) => {
      if (c.pathname === '/api/v1/assets/a1' && c.method === 'PATCH') {
        patched = (await c.json()) as typeof patched;
        return json(asset({ metadata: patched!.metadata }));
      }
      return undefined;
    });
    renderLibrary();
    await rightClickCard();
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Make available for' }));

    const canva = await screen.findByRole('menuitemcheckbox', { name: 'Canva' });
    expect(canva).toHaveAttribute('aria-checked', 'true');
    const webApi = screen.getByRole('menuitemcheckbox', { name: 'Web API' });
    expect(webApi).toHaveAttribute('aria-checked', 'false');

    await userEvent.click(webApi);
    await waitFor(() => expect(patched).toBeDefined());
    expect(patched?.metadata.channels).toEqual(['canva', 'web_api']);
    expect(await screen.findByText('“Hero shot” is now available for Web API')).toBeInTheDocument();
    expect(calls.filter((c) => c.method === 'PATCH')).toHaveLength(1);
  });

  it('removes a system when its checked entry is chosen', async () => {
    let patched: { metadata: Record<string, unknown> } | undefined;
    mockApi(async (c) => {
      if (c.pathname === '/api/v1/assets/a1' && c.method === 'PATCH') {
        patched = (await c.json()) as typeof patched;
        return json(asset({ metadata: patched!.metadata }));
      }
      return undefined;
    });
    renderLibrary();
    await rightClickCard();
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Make available for' }));
    await userEvent.click(await screen.findByRole('menuitemcheckbox', { name: 'Canva' }));
    await waitFor(() => expect(patched).toBeDefined());
    expect(patched?.metadata.channels).toEqual([]);
    expect(await screen.findByText('“Hero shot” is no longer available for Canva')).toBeInTheDocument();
  });

  it('renames the asset through a dialog', async () => {
    let patched: { title: string } | undefined;
    mockApi(async (c) => {
      if (c.pathname === '/api/v1/assets/a1' && c.method === 'PATCH') {
        patched = (await c.json()) as typeof patched;
        return json(asset({ title: patched!.title }));
      }
      return undefined;
    });
    renderLibrary();
    await rightClickCard();
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Rename…' }));

    const dialog = await screen.findByRole('dialog', { name: 'Rename asset' });
    const name = within(dialog).getByLabelText('Name');
    expect(name).toHaveValue('Hero shot');
    await userEvent.clear(name);
    await userEvent.type(name, 'Spring hero');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(patched?.title).toBe('Spring hero'));
    expect(await screen.findByText('Renamed')).toBeInTheDocument();
  });

  it('asks before deleting, and only deletes on confirmation', async () => {
    let deleted = 0;
    mockApi(async (c) => {
      if (c.pathname === '/api/v1/assets/a1' && c.method === 'DELETE') {
        deleted++;
        return new Response(null, { status: 204 });
      }
      return undefined;
    });
    renderLibrary();
    await rightClickCard();
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Delete…' }));

    const dialog = await screen.findByRole('dialog', { name: 'Delete this asset?' });
    expect(within(dialog).getByText(/“Hero shot” will be moved to the trash/)).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    expect(deleted).toBe(0);

    await rightClickCard();
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Delete…' }));
    await userEvent.click(
      within(await screen.findByRole('dialog', { name: 'Delete this asset?' })).getByRole('button', { name: 'Delete' }),
    );
    await waitFor(() => expect(deleted).toBe(1));
    expect(await screen.findByText('Moved to the trash')).toBeInTheDocument();
  });

  it('offers only "Open details" for an asset already in the trash', async () => {
    mockApi(undefined, [asset({ status: 'deleted' })]);
    renderLibrary();
    await rightClickCard();
    const menu = await screen.findByRole('menu');
    expect(
      within(menu)
        .getAllByRole('menuitem')
        .map((i) => i.textContent),
    ).toEqual(['Open details']);
  });
});

describe('folders', () => {
  const rightClickFolder = async (name: string) => {
    const row = within(await screen.findByRole('tree'))
      .getByText(name)
      .closest('.dam-treeitem__row')!;
    fireEvent.contextMenu(row, { clientX: 20, clientY: 20 });
  };

  it("shows the folder's display name, not its path key", async () => {
    mockApi();
    renderLibrary();
    expect(await screen.findByRole('treeitem', { name: /Brand/ })).toBeInTheDocument();
  });

  it('creates a top-level folder from the New folder button', async () => {
    let created: { name: string; parentId: string | null } | undefined;
    mockApi(async (c) => {
      if (c.pathname === '/api/v1/folders' && c.method === 'POST') {
        created = (await c.json()) as typeof created;
        return json({ folder: FOLDER('f9', created!.name), ancestors: [], effectiveMetadata: {}, childCount: 0 }, 201);
      }
      return undefined;
    });
    renderLibrary();
    await userEvent.click(await screen.findByRole('button', { name: 'New folder' }));
    const dialog = await screen.findByRole('dialog', { name: 'New folder' });
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Summer campaign');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create' }));

    await waitFor(() => expect(created).toEqual(expect.objectContaining({ name: 'Summer campaign', parentId: null })));
    expect(await screen.findByText('Folder “Summer campaign” created')).toBeInTheDocument();
  });

  it('creates a subfolder under the right-clicked folder', async () => {
    let created: { name: string; parentId: string | null } | undefined;
    mockApi(async (c) => {
      if (c.pathname === '/api/v1/folders' && c.method === 'POST') {
        created = (await c.json()) as typeof created;
        return json(
          { folder: FOLDER('f9', created!.name, created!.parentId), ancestors: [], effectiveMetadata: {}, childCount: 0 },
          201,
        );
      }
      return undefined;
    });
    renderLibrary();
    await rightClickFolder('Brand');
    await userEvent.click(await screen.findByRole('menuitem', { name: 'New subfolder…' }));
    const dialog = await screen.findByRole('dialog', { name: 'New folder' });
    expect(within(dialog).getByLabelText('Parent folder')).toHaveValue('f1');
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Sedan');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create' }));
    await waitFor(() => expect(created).toEqual(expect.objectContaining({ name: 'Sedan', parentId: 'f1' })));
  });

  it('renames a folder', async () => {
    let patched: { name: string } | undefined;
    mockApi(async (c) => {
      if (c.pathname === '/api/v1/folders/f1' && c.method === 'PATCH') {
        patched = (await c.json()) as typeof patched;
        return json({ folder: FOLDER('f1', patched!.name), ancestors: [], effectiveMetadata: {}, childCount: 1 });
      }
      return undefined;
    });
    renderLibrary();
    await rightClickFolder('Brand');
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Rename…' }));
    const dialog = await screen.findByRole('dialog', { name: 'Rename folder' });
    const name = within(dialog).getByLabelText('Name');
    expect(name).toHaveValue('Brand');
    await userEvent.clear(name);
    await userEvent.type(name, 'Brand assets');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(patched?.name).toBe('Brand assets'));
  });

  it("shows the server's reason when a folder cannot be deleted", async () => {
    mockApi(async (c) => {
      if (c.pathname === '/api/v1/folders/f1' && c.method === 'DELETE')
        return json({ title: 'Conflict', status: 409, detail: 'The folder still holds assets. Move or delete them first.' }, 409);
      return undefined;
    });
    renderLibrary();
    await rightClickFolder('Brand');
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Delete…' }));
    await userEvent.click(
      within(await screen.findByRole('dialog', { name: 'Delete this folder?' })).getByRole('button', { name: 'Delete' }),
    );
    expect(await screen.findByText(/still holds assets/)).toBeInTheDocument();
  });

  it('returns to all folders when the folder being viewed is deleted', async () => {
    const calls = mockApi(async (c) =>
      c.pathname === '/api/v1/folders/f1' && c.method === 'DELETE' ? new Response(null, { status: 204 }) : undefined,
    );
    renderLibrary();
    await userEvent.click(await screen.findByRole('treeitem', { name: /Brand/ }));
    await waitFor(() =>
      expect(calls.some((c) => c.pathname === '/api/v1/assets' && c.search.get('folderId') === 'f1')).toBe(true),
    );

    await rightClickFolder('Brand');
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Delete…' }));
    await userEvent.click(
      within(await screen.findByRole('dialog', { name: 'Delete this folder?' })).getByRole('button', { name: 'Delete' }),
    );

    expect(await screen.findByText('Folder deleted')).toBeInTheDocument();
    await waitFor(() =>
      expect(calls.filter((c) => c.pathname === '/api/v1/assets' && c.search.get('folderId') === null).length).toBeGreaterThan(0),
    );
  });

  it('adds a system to every asset in a folder after confirming', async () => {
    const patched: { id: string; channels: unknown }[] = [];
    mockApi(
      async (c) => {
        if (c.pathname.startsWith('/api/v1/assets/') && c.method === 'PATCH') {
          const body = (await c.json()) as { metadata: { channels: unknown } };
          patched.push({ id: c.pathname.split('/').pop()!, channels: body.metadata.channels });
          return json(asset());
        }
        return undefined;
      },
      [
        asset({ id: 'a1', metadata: { channels: ['canva'] } }),
        asset({ id: 'a2', title: 'Second', metadata: {} }),
        asset({ id: 'a3', title: 'Third', metadata: { channels: ['web_api'] } }),
      ],
    );
    renderLibrary();
    await rightClickFolder('Brand');
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Make available for' }));
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Web API' }));

    const dialog = await screen.findByRole('dialog', { name: 'Make available for Web API?' });
    expect(within(dialog).getByText(/every asset in “Brand”, including its sub-folders/)).toBeInTheDocument();
    expect(patched).toHaveLength(0); // nothing happens until confirmed
    await userEvent.click(within(dialog).getByRole('button', { name: 'Make available' }));

    await waitFor(() => expect(patched).toHaveLength(2));
    expect(patched).toEqual(
      expect.arrayContaining([
        { id: 'a1', channels: ['canva', 'web_api'] },
        { id: 'a2', channels: ['web_api'] },
      ]),
    ); // a3 already had it
    expect(await screen.findByText('3 assets are now available for Web API')).toBeInTheDocument();
  });
});
