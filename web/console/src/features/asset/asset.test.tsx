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

/** Mirrors the real delivery-link handler's actual rules (`Delivery.cs`): the whole request 409s if `original` is asked
 * for on an unpublished asset, and - ADR-0007 - if any video transcode (`preview`, `mobile`, ...) is asked for on an
 * unpublished video, rather than silently degrading to just the renditions that are allowed. A stub that always returned
 * success regardless of what was requested wouldn't have caught the bugs this guards against: the console asked for
 * `preview` + `original` for every asset and for a video's `preview` while it was still a draft, so those 409'd and showed
 * "Preview is not available" despite a perfectly good watermarked still existing. Every request is recorded. */
const linkRequests: string[][] = [];
async function linksResponse(call: Call, status: string, type: string) {
  const body = (await call.json()) as { renditions: string[] };
  linkRequests.push(body.renditions);
  if (status !== 'published') {
    if (body.renditions.includes('original')) return json({ error: 'conflict' }, 409);
    if (type === 'video' && body.renditions.some((r) => r !== 'poster' && r !== 'thumb')) return json({ error: 'conflict' }, 409);
  }
  const links: Record<string, string> = {};
  for (const r of body.renditions) links[r] = `https://cdn.test/a1-${r}.jpg`;
  return json({ ...LINKS, links });
}

function standardHandlers(
  overrides: Partial<Record<string, (call: Call) => Response | Promise<Response>>> = {},
  assetStatus = ASSET.status,
  assetPatch: Partial<typeof ASSET> = {},
) {
  const asset = { ...ASSET, ...assetPatch };
  return (call: Call): Response | Promise<Response> | undefined => {
    const { method, pathname } = call;
    if (overrides[pathname]) return overrides[pathname]!(call);
    if (pathname === '/api/v1/assets/a1' && method === 'GET') return json({ ...asset, status: assetStatus });
    if (pathname === `/api/v1/schemas/${asset.type}`) return json({ ...SCHEMA, assetType: asset.type });
    if (pathname === '/api/v1/assets/a1/versions') return json(VERSIONS);
    if (pathname === '/api/v1/assets/a1/links') return linksResponse(call, assetStatus, asset.type);
    return undefined;
  };
}

beforeEach(() => {
  queryClient.clear();
  setLanguage('en');
  linkRequests.length = 0;
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const openTab = async (name: string) => userEvent.click(await screen.findByRole('tab', { name }));

describe('AssetDetailPage', () => {
  it('shows the asset header, an image preview, and a breadcrumb link back to the library', async () => {
    mockApi(standardHandlers());
    renderAt('/library/assets/a1');

    const title = await screen.findByRole('heading', { name: 'Hero shot' });
    const header = title.closest('header')!;
    expect(within(header).getByText('Draft')).toBeInTheDocument();
    expect(within(header).getByText('Image')).toBeInTheDocument();
    expect(await screen.findByRole('img', { name: 'Hero shot' })).toHaveAttribute('src', 'https://cdn.test/a1-preview.jpg');
    expect(within(screen.getByRole('navigation', { name: 'Breadcrumb' })).getByRole('link', { name: 'Library' })).toHaveAttribute(
      'href',
      '/library',
    );
    expect(document.title).toBe('Hero shot - Orbit');
  });

  it('links each folder in the breadcrumb back to that folder in the library', async () => {
    mockApi(
      standardHandlers({
        '/api/v1/folders': () =>
          json([
            { id: 'f0', parentId: null, name: 'Brand assets', label: 'brand', path: 'brand' },
            { id: 'f1', parentId: 'f0', name: 'Campaigns', label: 'campaigns', path: 'brand.campaigns' },
          ]),
      }),
    );
    renderAt('/library/assets/a1');

    const crumbs = await screen.findByRole('navigation', { name: 'Breadcrumb' });
    expect(await within(crumbs).findByRole('link', { name: 'Brand assets' })).toHaveAttribute('href', '/library?folder=f0');
    expect(within(crumbs).getByRole('link', { name: 'Campaigns' })).toHaveAttribute('href', '/library?folder=f1');
    expect(within(crumbs).getByText('Hero shot')).toHaveAttribute('aria-current', 'page');
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

  it('asks only for the preview of an unpublished image, and for preview + original once it is published', async () => {
    mockApi(standardHandlers());
    renderAt('/library/assets/a1');
    expect(await screen.findByRole('img', { name: 'Hero shot' })).toHaveAttribute('src', 'https://cdn.test/a1-preview.jpg');
    expect(linkRequests).toEqual([['preview']]);

    cleanup();
    queryClient.clear();
    linkRequests.length = 0;
    mockApi(standardHandlers({}, 'published'));
    renderAt('/library/assets/a1');
    expect(await screen.findByRole('img', { name: 'Hero shot' })).toHaveAttribute('src', 'https://cdn.test/a1-preview.jpg');
    expect(linkRequests).toEqual([['preview', 'original']]);
  });

  it('shows the poster of a draft video with a note, and never asks for a transcode the origin would refuse', async () => {
    mockApi(standardHandlers({}, 'draft', { type: 'video', title: 'Teaser' }));
    renderAt('/library/assets/a1');

    expect(await screen.findByRole('img', { name: 'Teaser' })).toHaveAttribute('src', 'https://cdn.test/a1-poster.jpg');
    expect(screen.getByText('Video playback is available once the asset is published.')).toBeInTheDocument();
    expect(document.querySelector('video')).toBeNull();
    expect(linkRequests).toEqual([['poster']]);
  });

  it('plays a published video with its poster as the poster frame', async () => {
    mockApi(standardHandlers({}, 'published', { type: 'video', title: 'Teaser' }));
    renderAt('/library/assets/a1');

    await waitFor(() => expect(document.querySelector('video')).not.toBeNull());
    const video = document.querySelector('video')!;
    expect(video).toHaveAttribute('src', 'https://cdn.test/a1-mobile.jpg');
    expect(video).toHaveAttribute('poster', 'https://cdn.test/a1-poster.jpg');
    expect(video).toHaveAttribute('controls');
    expect(screen.queryByText('Video playback is available once the asset is published.')).not.toBeInTheDocument();
  });

  it('shows a published PDF as page images (not a frame) and pages through them', async () => {
    mockApi(
      standardHandlers(
        {
          '/api/v1/assets/a1/renditions': () =>
            json([
              { profile: 'preview', pageNo: null, mime: 'image/webp', status: 'ready' },
              ...[1, 2, 3].map((n) => ({ profile: 'pdf_pages', pageNo: n, mime: 'image/webp', status: 'ready' })),
            ]),
        },
        'published',
        { type: 'document', title: 'Guide' },
      ),
    );
    renderAt('/library/assets/a1');

    expect(await screen.findByRole('img', { name: 'Guide, page 1' })).toHaveAttribute(
      'src',
      'https://cdn.test/a1-pdf_pages-1.jpg',
    );
    expect(document.querySelector('iframe')).toBeNull();
    expect(screen.getByText('Page 1 of 3')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Previous page' })).toBeDisabled();

    await userEvent.click(screen.getByRole('button', { name: 'Next page' }));
    expect(await screen.findByRole('img', { name: 'Guide, page 2' })).toHaveAttribute(
      'src',
      'https://cdn.test/a1-pdf_pages-2.jpg',
    );
    expect(screen.getByText('Page 2 of 3')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Next page' }));
    expect(await screen.findByText('Page 3 of 3')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Next page' })).toBeDisabled();
  });

  it('shows only the watermarked first page of a draft PDF, with a note, and never asks for page rasters or the original', async () => {
    mockApi(
      standardHandlers(
        {
          '/api/v1/assets/a1/renditions': () =>
            json([
              { profile: 'preview', pageNo: null, mime: 'image/webp', status: 'ready' },
              ...[1, 2, 3].map((n) => ({ profile: 'pdf_pages', pageNo: n, mime: 'image/webp', status: 'ready' })),
            ]),
        },
        'draft',
        { type: 'document', title: 'Guide' },
      ),
    );
    renderAt('/library/assets/a1');

    expect(await screen.findByRole('img', { name: 'Guide' })).toHaveAttribute('src', 'https://cdn.test/a1-preview.jpg');
    expect(screen.getByText('Only the first page is previewed until the asset is published.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Next page' })).not.toBeInTheDocument();
    expect(document.querySelector('iframe')).toBeNull();
    expect(linkRequests).toEqual([['preview']]);
  });

  it('toggles between fit and 100% and copies a delivery link with a toast', async () => {
    const writeText = vi.fn(async () => {});
    vi.stubGlobal('navigator', { ...navigator, clipboard: { writeText } });
    mockApi(standardHandlers());
    renderAt('/library/assets/a1');

    await screen.findByRole('img', { name: 'Hero shot' });
    const stage = document.querySelector('.dam-stage')!;
    expect(stage).toHaveAttribute('data-zoom', 'fit');
    await userEvent.click(screen.getByRole('button', { name: '100%' }));
    expect(stage).toHaveAttribute('data-zoom', 'actual');
    expect(screen.getByRole('link', { name: 'Open in new tab' })).toHaveAttribute('href', 'https://cdn.test/a1-preview.jpg');

    await userEvent.click(screen.getByRole('button', { name: 'Copy link' }));
    await waitFor(() => expect(writeText).toHaveBeenCalledWith('https://cdn.test/a1-preview.jpg'));
    expect(await screen.findByText('Link copied')).toBeInTheDocument();
  });

  it('renames the asset in place: Enter saves only the title, Escape puts the old one back', async () => {
    const patches: unknown[] = [];
    mockApi(
      standardHandlers({
        '/api/v1/assets/a1': async (call) => {
          if (call.method === 'PATCH') {
            const body = (await call.json()) as { title: string };
            patches.push(body);
            return json({ ...ASSET, title: body.title });
          }
          return json(ASSET);
        },
      }),
    );
    renderAt('/library/assets/a1');
    await screen.findByRole('heading', { name: 'Hero shot' });

    await userEvent.click(screen.getByRole('button', { name: 'Rename' }));
    await userEvent.keyboard('{Control>}a{/Control}Discarded title{Escape}');
    expect(await screen.findByRole('heading', { name: 'Hero shot' })).toBeInTheDocument();
    expect(patches).toHaveLength(0);

    await userEvent.click(screen.getByRole('button', { name: 'Rename' }));
    const input = screen.getByRole('textbox', { name: 'Title' });
    await userEvent.clear(input);
    await userEvent.type(input, 'Hero shot final{Enter}');

    expect(await screen.findByRole('heading', { name: 'Hero shot final' })).toBeInTheDocument();
    expect(patches).toEqual([{ title: 'Hero shot final', tags: null, metadata: null }]);
    expect(document.title).toBe('Hero shot final - Orbit');
  });

  it('lets the schema-generated metadata form be edited and saved from the unsaved-changes bar', async () => {
    const patches: { title: unknown; tags: unknown; metadata: unknown }[] = [];
    mockApi(
      standardHandlers({
        '/api/v1/assets/a1': async (call) => {
          if (call.method === 'PATCH') {
            const body = (await call.json()) as (typeof patches)[number];
            patches.push(body);
            return json({ ...ASSET, metadata: body.metadata });
          }
          return json(ASSET);
        },
      }),
    );
    renderAt('/library/assets/a1');

    await screen.findByRole('heading', { name: 'Hero shot' });
    expect(screen.queryByRole('button', { name: 'Save changes' })).not.toBeInTheDocument(); // nothing to save yet
    await openTab('Metadata');
    const headline = await screen.findByLabelText('Headline');
    expect(headline).toHaveValue('Original headline');

    await userEvent.clear(headline);
    await userEvent.type(headline, 'New headline');
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    await waitFor(() => expect(patches).toHaveLength(1));
    expect(patches[0]).toEqual({ title: null, tags: null, metadata: { headline: 'New headline' } });
    expect(await screen.findByText('Changes saved')).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Save changes' })).not.toBeInTheDocument());
  });

  it('discards unsaved edits, and keeps them while another tab is showing', async () => {
    mockApi(standardHandlers());
    renderAt('/library/assets/a1');
    await screen.findByRole('heading', { name: 'Hero shot' });

    await openTab('Metadata');
    await userEvent.type(await screen.findByLabelText('Headline'), ' edited');
    await openTab('Versions');
    expect(screen.getByText('You have unsaved changes')).toBeInTheDocument();
    await openTab('Metadata');
    expect(await screen.findByLabelText('Headline')).toHaveValue('Original headline edited');

    await userEvent.click(screen.getByRole('button', { name: 'Discard' }));
    expect(screen.getByLabelText('Headline')).toHaveValue('Original headline');
    expect(screen.queryByText('You have unsaved changes')).not.toBeInTheDocument();
  });

  it('edits tags as chips on the Details tab and saves only the tags', async () => {
    const patches: unknown[] = [];
    mockApi(
      standardHandlers({
        '/api/v1/assets/a1': async (call) => {
          if (call.method === 'PATCH') {
            const body = (await call.json()) as { tags: string[] };
            patches.push(body);
            return json({ ...ASSET, tags: body.tags });
          }
          return json(ASSET);
        },
      }),
    );
    renderAt('/library/assets/a1');

    const tags = await screen.findByRole('textbox', { name: 'Tags' });
    expect(screen.getByRole('button', { name: 'Remove hero' })).toBeInTheDocument();
    await userEvent.type(tags, 'Spring{Enter}');
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    await waitFor(() => expect(patches).toEqual([{ title: null, tags: ['hero', 'spring'], metadata: null }]));
  });

  it('picks vocabulary terms with checkboxes, marks required fields, and saves the whole document keeping unknown keys', async () => {
    const patches: { metadata: Record<string, unknown> }[] = [];
    const schema = {
      ...SCHEMA,
      fields: [
        { name: 'headline', label: 'Headline', type: 'text', required: true, multi: false },
        { name: 'campaign', label: 'Campaign', type: 'vocabulary', vocabulary: 'campaign', required: false, multi: true },
      ],
    };
    mockApi(
      standardHandlers(
        {
          '/api/v1/schemas/image': () => json(schema),
          '/api/v1/vocabularies': () => json([{ id: 'v-camp', key: 'campaign', name: 'Campaign' }]),
          '/api/v1/vocabularies/v-camp/terms': () =>
            json([
              {
                term: { code: 'spring', labels: { en: 'Spring launch' } },
                children: [{ term: { code: 'spring-eu', labels: { en: 'Spring EU' } }, children: [] }],
              },
              { term: { code: 'festive', labels: { en: 'Festive offer' } }, children: [] },
            ]),
          '/api/v1/assets/a1': async (call) => {
            if (call.method === 'PATCH') {
              const body = (await call.json()) as { metadata: Record<string, unknown> };
              patches.push(body);
              return json({ ...ASSET, metadata: body.metadata });
            }
            return json({ ...ASSET, metadata: { headline: '', legacy: 'kept' } });
          },
        },
        'draft',
      ),
    );
    renderAt('/library/assets/a1');

    await screen.findByRole('heading', { name: 'Hero shot' });
    await openTab('Metadata');
    expect(await screen.findByText(/1 required field is empty/)).toBeInTheDocument();

    const group = await screen.findByRole('group', { name: 'Campaign' });
    const festive = within(group).getByRole('checkbox', { name: 'Festive offer' });
    expect(within(group).getByRole('checkbox', { name: 'Spring EU' })).toBeInTheDocument(); // child term, indented
    await userEvent.click(festive);

    const headline = screen.getByLabelText('Headline *');
    expect(headline).toHaveAttribute('aria-required', 'true');
    await userEvent.type(headline, 'x');
    await userEvent.clear(headline);
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Headline is required before this asset can be submitted for review.',
    );

    await userEvent.type(headline, 'Done');
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(patches).toHaveLength(1));
    expect(patches[0]!.metadata).toEqual({ headline: 'Done', legacy: 'kept', campaign: ['festive'] });
  });

  it('offers the next lifecycle step as the prominent button and the rest in an overflow menu', async () => {
    mockApi(standardHandlers({}, 'in_review'));
    renderAt('/library/assets/a1');

    const actions = await screen.findByRole('group', { name: 'Lifecycle actions' });
    expect(within(actions).getByRole('button', { name: 'Approve' })).toHaveClass('dam-btn--primary');
    expect(within(actions).queryByRole('button', { name: 'Reject' })).not.toBeInTheDocument();

    await userEvent.click(within(actions).getByRole('button', { name: 'More actions' }));
    const menu = await screen.findByRole('menu', { name: 'More actions' });
    expect(within(menu).getByRole('menuitem', { name: 'Reject' })).toBeInTheDocument();
    expect(within(menu).getByRole('menuitem', { name: 'Withdraw' })).toBeInTheDocument();
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
    expect(within(actions).queryByRole('button', { name: 'More actions' })).not.toBeInTheDocument(); // nothing else to offer

    await userEvent.click(within(actions).getByRole('button', { name: 'Submit for review' }));
    await waitFor(() => expect(transitioned).toBe(true));
    expect(await screen.findByText('In review')).toBeInTheDocument();
    expect(await screen.findByText('Status is now In review')).toBeInTheDocument();
  });

  it('lists versions as a timeline with the current one marked, and restores a non-current one', async () => {
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

    await openTab('Versions');
    const items = within(await screen.findByRole('list', { name: 'Versions' })).getAllByRole('listitem');
    expect(items).toHaveLength(2);
    expect(within(items[0]!).getByText('Version 2')).toBeInTheDocument(); // newest first
    expect(within(items[0]!).getByText('Current')).toBeInTheDocument();
    expect(within(items[1]!).getByText('Version 1')).toBeInTheDocument();
    expect(within(items[0]!).queryByRole('button', { name: 'Restore' })).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Restore' }));
    await waitFor(() => expect(restoredVersion).toBe(1));
    expect(await screen.findByText('Version 1 restored')).toBeInTheDocument();
  });

  it('shows a load-failure message with a way back to the library when the asset cannot be fetched', async () => {
    mockApi((call) => (call.pathname === '/api/v1/assets/missing' ? json({ error: 'not found' }, 404) : undefined));
    renderAt('/library/assets/missing');

    expect(await screen.findByText('Could not load this asset')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Go to the library' })).toBeInTheDocument();
  });
});
