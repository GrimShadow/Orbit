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

const ASSET = {
  id: 'a1',
  folderId: 'f1',
  type: 'image',
  status: 'draft',
  title: 'Hero shot',
  metadata: { headline: 'Original headline' },
  metadataSchemaVersion: 1,
  taxonomyTerms: [],
  tags: ['hero'],
  attributes: {},
  ownerId: null,
  currentVersionId: 'v2',
  checksumSha256: null,
  statusReason: '',
  publishAt: null,
  expireAt: null,
  retentionPolicyId: null,
  legalHold: false,
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-02T00:00:00Z',
};

const SCHEMA = {
  assetType: 'image',
  version: 1,
  isCurrent: true,
  changeNote: '',
  createdAt: '2026-01-01T00:00:00Z',
  fields: [{ name: 'headline', label: 'Headline', type: 'text', required: false, multi: false, searchable: true, facet: false }],
};

const VERSIONS = [
  { id: 'v1', versionNo: 1, isCurrent: false, sizeBytes: 1024, mime: 'image/jpeg', createdAt: '2026-01-01T00:00:00Z' },
  {
    id: 'v2',
    versionNo: 2,
    isCurrent: true,
    sizeBytes: 2048,
    mime: 'image/jpeg',
    sha256: 'f'.repeat(64),
    technicalMetadata: { file: { fileName: 'hero.jpg' }, image: { width: 1920, height: 1080, hasAlpha: false } },
    createdAt: '2026-01-02T00:00:00Z',
  },
];

const LINKS = { expiresAt: '2026-01-01T01:00:00Z', watermarked: true, links: { preview: 'https://cdn.test/a1-preview.jpg' } };

/** Mirrors the real delivery-link handler's actual rule (`Delivery.cs`): the whole request 409s if `original` is asked
 * for on an unpublished asset, rather than silently degrading to just the renditions that are allowed. A stub that
 * always returned success regardless of what was requested wouldn't have caught the bug this guards against - the
 * console asked for `preview` and `original` together for every asset, so every freshly uploaded (draft) asset 409'd
 * and showed "Preview is not available" despite a perfectly good watermarked preview existing. */
async function linksResponse(call: Call, status: string) {
  const body = (await call.json()) as { renditions: string[] };
  if (status !== 'published' && body.renditions.includes('original')) return json({ error: 'conflict' }, 409);
  return json(LINKS);
}

function standardHandlers(
  overrides: Partial<Record<string, (call: Call) => Response | Promise<Response>>> = {},
  assetStatus = ASSET.status,
) {
  return (call: Call): Response | Promise<Response> | undefined => {
    const { method, pathname } = call;
    if (overrides[pathname]) return overrides[pathname]!(call);
    if (pathname === '/api/v1/assets/a1' && method === 'GET') return json({ ...ASSET, status: assetStatus });
    if (pathname === '/api/v1/schemas/image') return json(SCHEMA);
    if (pathname === '/api/v1/assets/a1/versions') return json(VERSIONS);
    if (pathname === '/api/v1/assets/a1/links') return linksResponse(call, assetStatus);
    return undefined;
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

describe('AssetDetailPage', () => {
  it('shows the asset header, an image preview, and a link back to the library', async () => {
    mockApi(standardHandlers());
    renderAt('/library/assets/a1');

    expect(await screen.findByRole('heading', { name: 'Hero shot' })).toBeInTheDocument();
    expect(screen.getByText('Draft')).toBeInTheDocument();
    expect(screen.getByText('Image')).toBeInTheDocument();
    expect(await screen.findByRole('img', { name: 'Hero shot' })).toHaveAttribute('src', 'https://cdn.test/a1-preview.jpg');
    expect(screen.getByRole('link', { name: 'Back to library' })).toHaveAttribute('href', '/library');
  });

  it('shows file information: summary line, size, dimensions and file name', async () => {
    mockApi(standardHandlers());
    renderAt('/library/assets/a1');

    const info = await screen.findByRole('region', { name: 'File information' });
    expect(within(info).getByText('hero.jpg')).toBeInTheDocument();
    expect(within(info).getByText('1920 × 1080 px')).toBeInTheDocument();
    expect(within(info).getByText('2.0 KB')).toBeInTheDocument();
    expect(within(info).getByText('16:9')).toBeInTheDocument();
    expect(screen.getByText('JPEG · 1920 × 1080 · 2.0 KB')).toBeInTheDocument();
  });

  it('shows a preview for a published asset requesting both preview and original', async () => {
    mockApi(standardHandlers({}, 'published'));
    renderAt('/library/assets/a1');

    expect(await screen.findByRole('img', { name: 'Hero shot' })).toHaveAttribute('src', 'https://cdn.test/a1-preview.jpg');
  });

  it('lets the schema-generated metadata form be edited and saved, sending only the metadata field', async () => {
    const patches: { title: unknown; tags: unknown; metadata: unknown }[] = [];
    mockApi(
      standardHandlers({
        '/api/v1/assets/a1': async (call) => {
          if (call.method === 'PATCH') {
            patches.push((await call.json()) as (typeof patches)[number]);
            return json(ASSET);
          }
          return json(ASSET);
        },
      }),
    );
    renderAt('/library/assets/a1');

    const headline = await screen.findByLabelText('Headline');
    expect(headline).toHaveValue('Original headline');

    await userEvent.clear(headline);
    await userEvent.type(headline, 'New headline');
    await userEvent.click(screen.getByRole('button', { name: 'Save metadata' }));

    await waitFor(() => expect(patches).toHaveLength(1));
    expect(patches[0]).toEqual({ title: null, tags: null, metadata: { headline: 'New headline' } });
    expect(screen.getByRole('button', { name: 'Save metadata' })).toBeDisabled();
  });

  it('offers only the draft lifecycle action (submit) and transitions on click', async () => {
    let transitioned = false;
    mockApi(
      standardHandlers({
        '/api/v1/assets/a1/transitions/submit': () => {
          transitioned = true;
          return json({ ...ASSET, status: 'in_review' });
        },
      }),
    );
    renderAt('/library/assets/a1');

    const actions = await screen.findByRole('group', { name: 'Lifecycle actions' });
    expect(within(actions).getByRole('button', { name: 'Submit for review' })).toBeInTheDocument();
    expect(within(actions).queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument();

    await userEvent.click(within(actions).getByRole('button', { name: 'Submit for review' }));
    await waitFor(() => expect(transitioned).toBe(true));
    expect(await screen.findByText('In review')).toBeInTheDocument();
  });

  it('lists versions and restores a non-current one', async () => {
    let restoredVersion: number | undefined;
    mockApi(
      standardHandlers({
        '/api/v1/assets/a1/versions/1/restore': () => {
          restoredVersion = 1;
          return json({ ...ASSET, currentVersionId: 'v1' });
        },
      }),
    );
    renderAt('/library/assets/a1');

    await userEvent.click(await screen.findByRole('tab', { name: 'Versions' }));
    expect(await screen.findByText('Version 2')).toBeInTheDocument();
    expect(screen.getByText('Version 1')).toBeInTheDocument();
    expect(screen.getByText('Current')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Restore' }));
    await waitFor(() => expect(restoredVersion).toBe(1));
  });

  it('shows a load-failure message with a way back to the library when the asset cannot be fetched', async () => {
    mockApi((call) => (call.pathname === '/api/v1/assets/missing' ? json({ error: 'not found' }, 404) : undefined));
    renderAt('/library/assets/missing');

    expect(await screen.findByText('Could not load this asset')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Go to the library' })).toBeInTheDocument();
  });
});
