import { cleanup, render, screen, waitFor } from '@testing-library/react';
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
        if (call.pathname === '/api/v1/folders') return Promise.resolve(json([]));
        throw new Error(`unexpected request: ${call.method} ${call.pathname}`);
      }
      // Presigned S3 part upload: any absolute non-API URL.
      return Promise.resolve(new Response(null, { status: 200, headers: { ETag: '"etag1"' } }));
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

describe('UploadPage', () => {
  it('lists a chosen file, then uploads it and shows it as ready', async () => {
    mockApi(({ pathname }) => {
      if (pathname === '/api/v1/assets/bulk-upload')
        return json({
          batchId: 'b1',
          created: 1,
          failed: 0,
          items: [
            {
              fileName: 'hero.jpg',
              assetId: 'a1',
              upload: {
                upload: {
                  id: 'u1',
                  assetId: 'a1',
                  batchId: 'b1',
                  status: 'open',
                  fileName: 'hero.jpg',
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
                parts: [{ partNumber: 1, url: 'https://s3.test/p1', expiresAt: '2026-01-01T01:00:00Z' }],
              },
              error: null,
            },
          ],
        });
      if (pathname === '/api/v1/uploads/u1/complete') return json({ id: 'u1', assetId: 'a1', status: 'ingested', error: '' });
      return undefined;
    });

    renderAt('/upload');
    expect(await screen.findByRole('heading', { name: 'Upload' })).toBeInTheDocument();

    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    const file = new File(['abc'], 'hero.jpg', { type: 'image/jpeg' });
    await userEvent.upload(input, file);

    expect(await screen.findByText('hero.jpg')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Start upload' }));

    await waitFor(() => expect(screen.getByText('Ready')).toBeInTheDocument());
    expect(await screen.findByRole('link', { name: 'Go to the library' })).toBeInTheDocument();
  });

  it('shows a per-file error when the batch call rejects a file', async () => {
    mockApi(({ pathname }) => {
      if (pathname === '/api/v1/assets/bulk-upload')
        return json({
          batchId: 'b1',
          created: 0,
          failed: 1,
          items: [{ fileName: 'virus.exe', assetId: null, upload: null, error: 'This file type is not accepted.' }],
        });
      return undefined;
    });

    renderAt('/upload');
    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    await userEvent.upload(input, new File(['x'], 'virus.exe'));
    await userEvent.click(await screen.findByRole('button', { name: 'Start upload' }));

    expect(await screen.findByText('This file type is not accepted.')).toBeInTheDocument();
    expect(screen.getByText('Rejected')).toBeInTheDocument();
  });

  it('lets a queued file be removed before starting', async () => {
    mockApi(() => undefined);
    renderAt('/upload');
    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    await userEvent.upload(input, new File(['x'], 'hero.jpg'));
    expect(await screen.findByText('hero.jpg')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Remove' }));
    expect(screen.queryByText('hero.jpg')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Start upload' })).toBeDisabled();
  });
});
