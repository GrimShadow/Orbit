import { createApiClient } from '@dam/sdk-js';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { uploadFile, type UploadStart } from './uploadOrchestrator';

function jsonResponse(body: unknown, init: ResponseInit = {}) {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' }, ...init });
}

function api() {
  return createApiClient({ baseUrl: 'https://api.test', getAccessToken: () => 'tok' });
}

/** Normalises both calling conventions this code makes: openapi-fetch always calls `fetch(request: Request)`, while the
 * orchestrator's own S3 part uploads call plain `fetch(url, init)` directly (there is no OpenAPI operation for "PUT
 * bytes to a presigned URL"). */
interface Call {
  method: string;
  pathname: string;
  json: () => Promise<unknown>;
}

function mockFetch(handler: (call: Call) => Response | Promise<Response>) {
  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      if (input instanceof Request)
        return handler({ method: input.method, pathname: new URL(input.url).pathname, json: () => input.json() });
      const url = new URL(String(input));
      const body = init?.body;
      return handler({
        method: init?.method ?? 'GET',
        pathname: url.pathname,
        json: () => Promise.resolve(typeof body === 'string' ? JSON.parse(body) : body),
      });
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe('uploadFile', () => {
  it('uploads every part, completes, and returns the terminal status when complete already settles it', async () => {
    const putPaths: string[] = [];
    mockFetch(({ method, pathname }) => {
      if (method === 'PUT') {
        putPaths.push(pathname);
        return new Response(null, { status: 200, headers: { ETag: '"abc123"' } });
      }
      if (pathname === '/api/v1/uploads/u1/complete') return jsonResponse({ id: 'u1', status: 'ingested', error: '' });
      throw new Error(`unexpected request: ${method} ${pathname}`);
    });

    const start: UploadStart = {
      upload: { id: 'u1', partSize: 5, partCount: 2 },
      parts: [
        { partNumber: 1, url: 'https://s3.test/p1' },
        { partNumber: 2, url: 'https://s3.test/p2' },
      ],
    };
    const file = new File(['0123456789'], 'a.jpg'); // 10 bytes, 2 parts of 5
    const progress: number[] = [];

    const outcome = await uploadFile(api(), file, start, (f) => progress.push(f));

    expect(outcome).toEqual({ status: 'ingested', error: undefined });
    expect(putPaths).toEqual(['/p1', '/p2']);
    expect(progress).toEqual([0.5, 1]);
  });

  it('requests presigned URLs for parts beyond the ones already given', async () => {
    const requestedPartNumbers: number[][] = [];
    mockFetch(async ({ method, pathname, json }) => {
      if (method === 'PUT') return new Response(null, { status: 200, headers: { ETag: '"x"' } });
      if (pathname === '/api/v1/uploads/u1/parts') {
        const body = (await json()) as { partNumbers: number[] };
        requestedPartNumbers.push(body.partNumbers);
        return jsonResponse(
          body.partNumbers.map((n) => ({ partNumber: n, url: `https://s3.test/late-${n}`, expiresAt: '2026-01-01T00:00:00Z' })),
        );
      }
      if (pathname === '/api/v1/uploads/u1/complete') return jsonResponse({ id: 'u1', status: 'ingested', error: '' });
      throw new Error(`unexpected request: ${method} ${pathname}`);
    });

    // Part 2's URL is deliberately missing from the initial batch (spec: bulk-upload only hands back the first few).
    const start: UploadStart = {
      upload: { id: 'u1', partSize: 5, partCount: 2 },
      parts: [{ partNumber: 1, url: 'https://s3.test/p1' }],
    };
    const file = new File(['0123456789'], 'a.jpg');

    await uploadFile(api(), file, start, () => {});
    expect(requestedPartNumbers).toEqual([[2]]);
  });

  it('polls until the upload reaches a terminal status', async () => {
    vi.useFakeTimers();
    let polls = 0;
    mockFetch(({ method, pathname }) => {
      if (method === 'PUT') return new Response(null, { status: 200, headers: { ETag: '"x"' } });
      if (pathname === '/api/v1/uploads/u1/complete') return jsonResponse({ id: 'u1', status: 'processing', error: '' });
      if (pathname === '/api/v1/uploads/u1') {
        polls++;
        return jsonResponse({ id: 'u1', status: polls < 2 ? 'processing' : 'blocked', error: 'Infected file.' });
      }
      throw new Error(`unexpected request: ${method} ${pathname}`);
    });

    const start: UploadStart = {
      upload: { id: 'u1', partSize: 10, partCount: 1 },
      parts: [{ partNumber: 1, url: 'https://s3.test/p1' }],
    };
    const file = new File(['x'], 'a.jpg');

    const resultPromise = uploadFile(api(), file, start, () => {});
    await vi.advanceTimersByTimeAsync(2000);
    const outcome = await resultPromise;

    expect(outcome).toEqual({ status: 'blocked', error: 'Infected file.' });
    expect(polls).toBe(2);
  });

  it('throws when a part upload is not accepted', async () => {
    mockFetch(({ method }) => (method === 'PUT' ? new Response('nope', { status: 500 }) : jsonResponse({})));
    const start: UploadStart = {
      upload: { id: 'u1', partSize: 10, partCount: 1 },
      parts: [{ partNumber: 1, url: 'https://s3.test/p1' }],
    };
    await expect(uploadFile(api(), new File(['x'], 'a.jpg'), start, () => {})).rejects.toThrow(/part 1/i);
  });
});
