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

function mockApi(handler: (call: Call) => Response | Promise<Response | undefined> | undefined) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      if (input instanceof Request) {
        const call: Call = { method: input.method, pathname: new URL(input.url).pathname, json: () => input.json() };
        const response = await handler(call);
        if (response) return response;
        if (call.pathname === '/api/v1/folders') return json([]);
        throw new Error(`unexpected request: ${call.method} ${call.pathname}`);
      }
      // Presigned S3 part upload: any absolute non-API URL.
      return new Response(null, { status: 200, headers: { ETag: '"etag1"' } });
    }),
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

const SCHEMA = {
  assetType: 'image',
  version: 1,
  isCurrent: true,
  changeNote: '',
  createdAt: '2026-01-01T00:00:00Z',
  fields: [{ name: 'credit', label: 'Credit', type: 'text', required: false, multi: false, searchable: true, facet: false }],
};

const uploadStart = (n: number, name: string) => ({
  upload: {
    id: `u${n}`,
    assetId: `a${n}`,
    batchId: 'b1',
    status: 'open',
    fileName: name,
    declaredSize: 3,
    contentType: 'image/jpeg',
    partSize: 16,
    partCount: 1,
    sha256: null,
    sizeBytes: null,
    duplicateOfAssetId: null,
    error: '',
    createdAt: '2026-01-01T00:00:00Z',
    expiresAt: '2026-01-01T01:00:00Z',
    receivedParts: null,
  },
  parts: [{ partNumber: 1, url: `https://s3.test/p${n}`, expiresAt: '2026-01-01T01:00:00Z' }],
});
const okItem = (n: number, name: string) => ({ fileName: name, assetId: `a${n}`, upload: uploadStart(n, name), error: null });
const bulk = (items: unknown[]) => json({ batchId: 'b1', created: items.length, failed: 0, items });

interface BulkBody {
  folderId: string | null;
  tags: string[] | null;
  metadataByType: Record<string, Record<string, unknown>> | null;
  files: { fileName: string; assetType: string; title: string | null }[];
}

const choose = async (...files: File[]) => {
  const input = document.querySelector('input[type="file"]') as HTMLInputElement;
  await userEvent.upload(input, files);
};
const file = (name: string, content = 'abc', type = '') => new File([content], name, { type, lastModified: 1 });

describe('UploadPage', () => {
  it('queues a file with an editable title, uploads it with that title and type, and links to the new asset', async () => {
    let sent: BulkBody | undefined;
    mockApi(async (call) => {
      if (call.pathname === '/api/v1/schemas/image') return json(SCHEMA);
      if (call.pathname === '/api/v1/assets/bulk-upload') {
        sent = (await call.json()) as BulkBody;
        return bulk([okItem(1, 'hero.jpg')]);
      }
      if (call.pathname === '/api/v1/uploads/u1/complete')
        return json({ id: 'u1', assetId: 'a1', status: 'ingested', error: '' });
      return undefined;
    });

    renderAt('/upload');
    expect(await screen.findByRole('heading', { name: 'Upload' })).toBeInTheDocument();
    await choose(file('hero.jpg', 'abc', 'image/jpeg'));

    const title = await screen.findByLabelText('Title for hero.jpg');
    expect(title).toHaveValue('hero'); // defaults to the file name without its extension
    await userEvent.clear(title);
    await userEvent.type(title, 'Hero shot');
    expect(screen.getByText('1 file ready to upload')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Upload 1 file' }));

    await waitFor(() => expect(screen.getByText('Ready')).toBeInTheDocument());
    expect(sent?.files).toEqual([
      { fileName: 'hero.jpg', size: 3, contentType: 'image/jpeg', assetType: 'image', title: 'Hero shot' },
    ]);
    expect(screen.getByRole('link', { name: 'Open' })).toHaveAttribute('href', '/library/assets/a1');
    expect(await screen.findByRole('link', { name: 'View in library' })).toHaveAttribute('href', '/library');
    expect(screen.getByRole('heading', { name: '1 asset uploaded' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Upload more' })).toBeInTheDocument();
    expect(document.title).toBe('Upload - Orbit');
  });

  it('flags a file the server would refuse before anything is sent, and will not upload it', async () => {
    mockApi(() => undefined);
    renderAt('/upload');
    await choose(file('virus.exe'));

    expect(await screen.findByText('This file type is not allowed and will be skipped.')).toBeInTheDocument();
    expect(screen.getByText('1 file will be skipped')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Upload 0 files' })).toBeDisabled();
  });

  it('lets an ambiguous file choose its type, and sends the choice', async () => {
    let sent: BulkBody | undefined;
    mockApi(async (call) => {
      if (call.pathname === '/api/v1/assets/bulk-upload') {
        sent = (await call.json()) as BulkBody;
        return bulk([okItem(1, 'site.zip')]);
      }
      if (call.pathname === '/api/v1/uploads/u1/complete')
        return json({ id: 'u1', assetId: 'a1', status: 'ingested', error: '' });
      return undefined;
    });
    renderAt('/upload');
    await choose(file('site.zip'));

    const type = await screen.findByLabelText('Type of site.zip');
    expect(
      within(type)
        .getAllByRole('option')
        .map((o) => o.getAttribute('value')),
    ).toEqual(['manual', '3d', 'html5', 'other']);
    await userEvent.selectOptions(type, 'html5');
    await userEvent.click(screen.getByRole('button', { name: 'Upload 1 file' }));

    await waitFor(() => expect(sent).toBeDefined());
    expect(sent?.files[0]).toMatchObject({ fileName: 'site.zip', assetType: 'html5' });
  });

  it("offers the type's metadata form and sends only what was filled in, per type", async () => {
    let sent: BulkBody | undefined;
    mockApi(async (call) => {
      if (call.pathname === '/api/v1/schemas/image') return json(SCHEMA);
      if (call.pathname === '/api/v1/assets/bulk-upload') {
        sent = (await call.json()) as BulkBody;
        return bulk([okItem(1, 'a.jpg')]);
      }
      if (call.pathname === '/api/v1/uploads/u1/complete')
        return json({ id: 'u1', assetId: 'a1', status: 'ingested', error: '' });
      return undefined;
    });
    renderAt('/upload');
    await choose(file('a.jpg'));

    await userEvent.type(await screen.findByLabelText('Credit'), 'Acme Studio');
    await userEvent.type(screen.getByLabelText('Tags'), 'spring, launch');
    await userEvent.click(screen.getByRole('button', { name: 'Upload 1 file' }));

    await waitFor(() => expect(sent).toBeDefined());
    expect(sent?.metadataByType).toEqual({ image: { credit: 'Acme Studio' } });
    expect(sent?.tags).toEqual(['spring', 'launch']);
  });

  it('skips a file that is already in the list and says so', async () => {
    mockApi(() => undefined);
    renderAt('/upload');
    await choose(file('a.jpg'));
    await screen.findByLabelText('Title for a.jpg');
    await choose(file('a.jpg'));
    expect(await screen.findByText(/1 file was already in the list and was skipped/)).toBeInTheDocument();
    expect(screen.getAllByLabelText('Title for a.jpg')).toHaveLength(1);
  });

  it('lets a queued file be removed before starting', async () => {
    mockApi(() => undefined);
    renderAt('/upload');
    await choose(file('hero.jpg'));
    expect(await screen.findByLabelText('Title for hero.jpg')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Remove hero.jpg' }));
    expect(screen.queryByLabelText('Title for hero.jpg')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Upload \d/ })).not.toBeInTheDocument(); // the action bar goes with the queue
  });

  it('shows a server-side rejection per file and sends just the failed files back to the form for review', async () => {
    mockApi(async (call) => {
      if (call.pathname === '/api/v1/assets/bulk-upload')
        return json({
          batchId: 'b1',
          created: 1,
          failed: 1,
          items: [
            okItem(1, 'good.jpg'),
            { fileName: 'bad.jpg', assetId: null, upload: null, error: 'Batch metadata is not valid for image assets.' },
          ],
        });
      if (call.pathname === '/api/v1/uploads/u1/complete')
        return json({ id: 'u1', assetId: 'a1', status: 'ingested', error: '' });
      return undefined;
    });
    renderAt('/upload');
    await choose(file('good.jpg'), file('bad.jpg'));
    await userEvent.click(await screen.findByRole('button', { name: 'Upload 2 files' }));

    expect(await screen.findByText('Batch metadata is not valid for image assets.')).toBeInTheDocument();
    expect(screen.getByText('Rejected')).toBeInTheDocument();
    expect(await screen.findByText('Upload finished with problems')).toBeInTheDocument();
    expect(screen.getByText(/1 ready/)).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Review 1 failed file' }));
    expect(await screen.findByLabelText('Title for bad.jpg')).toBeInTheDocument();
    expect(screen.queryByLabelText('Title for good.jpg')).not.toBeInTheDocument();
  });

  it('links a duplicate to the asset that already holds the same file', async () => {
    mockApi(async (call) => {
      if (call.pathname === '/api/v1/assets/bulk-upload') return bulk([okItem(1, 'a.jpg')]);
      if (call.pathname === '/api/v1/uploads/u1/complete')
        return json({ id: 'u1', assetId: 'a1', status: 'duplicate', duplicateOfAssetId: 'existing1', error: '' });
      return undefined;
    });
    renderAt('/upload');
    await choose(file('a.jpg'));
    await userEvent.click(await screen.findByRole('button', { name: 'Upload 1 file' }));

    expect(await screen.findByText('Already in the library')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'View existing' })).toHaveAttribute('href', '/library/assets/existing1');
  });

  it('says what the drop zone accepts and its limits before anything is chosen', async () => {
    mockApi(() => undefined);
    renderAt('/upload');

    const kinds = await screen.findByRole('list', { name: /Images, Video/ });
    expect(
      within(kinds)
        .getAllByRole('listitem')
        .map((li) => li.textContent),
    ).toEqual(['Images', 'Video', 'Audio', 'Documents', '3D models', 'Web bundles']);
    expect(screen.getByText(/Up to 500 files at a time/)).toBeInTheDocument();
    expect(screen.getByText('Choose files')).toBeInTheDocument();
  });

  it('turns batch tags into removable chips and sends them', async () => {
    let sent: BulkBody | undefined;
    mockApi(async (call) => {
      if (call.pathname === '/api/v1/assets/bulk-upload') {
        sent = (await call.json()) as BulkBody;
        return bulk([okItem(1, 'a.jpg')]);
      }
      if (call.pathname === '/api/v1/uploads/u1/complete')
        return json({ id: 'u1', assetId: 'a1', status: 'ingested', error: '' });
      return undefined;
    });
    renderAt('/upload');
    await choose(file('a.jpg'));

    const tags = await screen.findByLabelText('Tags');
    await userEvent.type(tags, 'Spring{Enter}launch{Enter}draft{Enter}');
    expect(screen.getByRole('button', { name: 'Remove spring' })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Remove draft' }));
    await userEvent.click(screen.getByRole('button', { name: 'Upload 1 file' }));

    await waitFor(() => expect(sent).toBeDefined());
    expect(sent?.tags).toEqual(['spring', 'launch']);
  });

  it('reports a batch of only duplicates as nothing new, not as an upload', async () => {
    mockApi(async (call) => {
      if (call.pathname === '/api/v1/assets/bulk-upload') return bulk([okItem(1, 'a.jpg')]);
      if (call.pathname === '/api/v1/uploads/u1/complete')
        return json({ id: 'u1', assetId: 'a1', status: 'duplicate', duplicateOfAssetId: 'existing1', error: '' });
      return undefined;
    });
    renderAt('/upload');
    await choose(file('a.jpg'));
    await userEvent.click(await screen.findByRole('button', { name: 'Upload 1 file' }));

    expect(await screen.findByRole('heading', { name: 'No new assets' })).toBeInTheDocument();
  });
});
