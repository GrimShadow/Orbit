// Test-only scaffolding for the rights admin screens: fixtures, a small in-memory API behind a fetch mock (including the
// storage host the presigned upload goes to), and the console rendered at a route with a probe that exposes the URL.
// Nothing in the app imports this.
import { render } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { App, queryClient } from '../../../App';
import { AuthContext } from '../../../auth/AuthProvider';
import { json, mockApi, signedIn, type Call } from '../../workflow/workflowTestKit';
import type { Rights, RightsDocument } from '../rightsApi';

export { json, type Call };

const day = (offset: number) => new Date(Date.now() + offset * 86_400_000).toISOString().slice(0, 10);
export { day };

export const rights = (over: Partial<Rights> = {}): Rights => ({
  id: 'r1',
  name: 'Acme stock photography',
  licenceType: 'licensed',
  licensor: 'Acme Photos',
  channels: ['web', 'print'],
  regions: ['north'],
  validFrom: day(-30),
  validTo: day(200),
  watermarkRequired: false,
  notes: '',
  status: 'valid',
  daysRemaining: 200,
  assetCount: 0,
  documentCount: 0,
  createdBy: null,
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  pendingAssets: 0,
  ...over,
});

export const doc = (over: Partial<RightsDocument> = {}): RightsDocument => ({
  id: 'd1',
  rightsId: 'r1',
  name: 'release-form.pdf',
  contentType: 'application/pdf',
  sizeBytes: 123_456,
  uploadedBy: null,
  createdAt: new Date(Date.now() - 2 * 86_400_000).toISOString(),
  ...over,
});

export interface World {
  rights: Rights[];
  documents: Record<string, RightsDocument[]>;
  permissions: string[];
  /** Vocabulary terms; `null` for a tenant that has no vocabulary for the dimension. */
  channels: { code: string; label: string }[] | null;
  regions: { code: string; label: string }[] | null;
  /** Reasons the storage host refuses the PUT (status code), per call order. */
  storageStatus: number;
}

export const ADMIN = ['rights.manage'];

export function world(over: Partial<World> = {}): World {
  return {
    rights: [rights()],
    documents: {},
    permissions: ADMIN,
    channels: [
      { code: 'web', label: 'Website' },
      { code: 'print', label: 'Print' },
      { code: 'signage', label: 'Digital signage' },
    ],
    regions: [
      { code: 'global', label: 'Global' },
      { code: 'north', label: 'North' },
      { code: 'south', label: 'South' },
    ],
    storageStatus: 200,
    ...over,
  };
}

export const problem = (status: number, title: string, extra: Record<string, unknown> = {}) =>
  json({ title, status, ...extra }, status);
export const validation = (errors: Record<string, string[]>) =>
  problem(422, 'One or more validation errors occurred.', { errors });

const noContent = () => new Response(null, { status: 204 });
const term = (code: string, label: string) => ({
  term: {
    id: `t-${code}`,
    vocabularyId: '',
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

/** What reached the fake storage host, in order: `{ url, method, contentType, size }`. */
export const storageCalls: { url: string; method: string; contentType: string | null; size: number }[] = [];

/** Installs the fetch mock. `extra` answers first (use it to fail or observe a call); the rest is a tiny in-memory API. */
export function install(w: World, extra: (c: Call) => Response | Promise<Response> | undefined = () => undefined) {
  storageCalls.length = 0;
  let nextDoc = 1;
  const calls = mockApi(
    (c) => {
      const custom = extra(c);
      if (custom) return custom;
      const { method, pathname: p } = c;
      const seg = p.split('/');
      const id = seg[4];
      const docId = seg[6];
      if (method === 'GET' && p === '/api/v1/vocabularies') {
        const list = [];
        if (w.channels)
          list.push({
            id: 'v-channel',
            key: 'channel',
            name: 'Channel',
            description: '',
            hierarchical: false,
            levels: [],
            termCount: w.channels.length,
          });
        if (w.regions)
          list.push({
            id: 'v-region',
            key: 'region',
            name: 'Region',
            description: '',
            hierarchical: true,
            levels: [],
            termCount: w.regions.length,
          });
        return json(list);
      }
      if (method === 'GET' && p === '/api/v1/vocabularies/v-channel/terms')
        return json((w.channels ?? []).map((t) => term(t.code, t.label)));
      if (method === 'GET' && p === '/api/v1/vocabularies/v-region/terms')
        return json((w.regions ?? []).map((t) => term(t.code, t.label)));
      if (method === 'GET' && p === '/api/v1/schemas') return json([{ assetType: 'image', currentVersion: 1, fieldCount: 1 }]);
      if (method === 'GET' && p === '/api/v1/schemas/image')
        return json({
          assetType: 'image',
          version: 1,
          isCurrent: true,
          changeNote: '',
          createdAt: '2026-01-01T00:00:00Z',
          fields: [
            {
              name: 'channels',
              label: 'Channels',
              type: 'vocabulary',
              required: false,
              multi: true,
              vocabulary: 'channel',
              options: null,
              min: null,
              max: null,
              wholeNumbers: false,
              maxLength: null,
              searchable: false,
              facet: false,
              accessDimension: 'channel',
            },
          ],
        });

      if (method === 'GET' && p === '/api/v1/rights') return json(w.rights);
      if (method === 'POST' && p === '/api/v1/rights') {
        return c.json().then((b) => {
          const body = b as Partial<Rights>;
          if (w.rights.some((r) => r.name.toLowerCase() === (body.name ?? '').toLowerCase()))
            return validation({ name: ['A rights record with this name already exists.'] });
          const created = rights({ ...body, id: `r-new-${w.rights.length}`, assetCount: 0, documentCount: 0 });
          w.rights = [...w.rights, created];
          return json(created, 201);
        });
      }
      if (method === 'PUT' && /^\/api\/v1\/rights\/[^/]+$/.test(p)) {
        return c.json().then((b) => {
          const updated = { ...w.rights.find((r) => r.id === id)!, ...(b as Partial<Rights>) };
          w.rights = w.rights.map((r) => (r.id === id ? updated : r));
          return json(updated);
        });
      }
      if (method === 'DELETE' && /^\/api\/v1\/rights\/[^/]+$/.test(p)) {
        const target = w.rights.find((r) => r.id === id);
        if (target && target.assetCount > 0)
          return problem(409, 'Conflict', { detail: `Assigned to ${target.assetCount} assets.`, assetCount: target.assetCount });
        w.rights = w.rights.filter((r) => r.id !== id);
        return noContent();
      }
      if (method === 'GET' && /^\/api\/v1\/rights\/[^/]+\/documents$/.test(p)) return json(w.documents[id!] ?? []);
      if (method === 'POST' && /^\/api\/v1\/rights\/[^/]+\/documents$/.test(p))
        return json(
          {
            documentId: `doc-${nextDoc++}`,
            uploadUrl: `https://storage.test/rights/${id}/doc-${nextDoc - 1}?sig=abc`,
            method: 'PUT',
            headers: { 'x-amz-meta-test': '1' },
            expiresAt: new Date(Date.now() + 900_000).toISOString(),
            maxBytes: 25 * 1024 * 1024,
          },
          201,
        );
      if (method === 'POST' && p.endsWith('/complete')) {
        return c.json().then((b) => {
          const body = b as { fileName: string; contentType: string; size: number };
          const created = doc({
            id: docId,
            rightsId: id,
            name: body.fileName,
            contentType: body.contentType,
            sizeBytes: body.size,
            createdAt: new Date().toISOString(),
          });
          w.documents[id!] = [...(w.documents[id!] ?? []), created];
          w.rights = w.rights.map((r) => (r.id === id ? { ...r, documentCount: r.documentCount + 1 } : r));
          return json(created, 201);
        });
      }
      if (method === 'GET' && p.endsWith('/link'))
        return json({
          url: `https://storage.test/download/${docId}?sig=xyz`,
          expiresAt: new Date(Date.now() + 300_000).toISOString(),
        });
      if (method === 'DELETE' && /^\/api\/v1\/rights\/[^/]+\/documents\/[^/]+$/.test(p)) {
        w.documents[id!] = (w.documents[id!] ?? []).filter((d) => d.id !== docId);
        w.rights = w.rights.map((r) => (r.id === id ? { ...r, documentCount: Math.max(0, r.documentCount - 1) } : r));
        return noContent();
      }
      return undefined;
    },
    { permissions: w.permissions },
  );
  // The presigned upload goes straight to storage with a plain `fetch(url, init)`, not through the API client.
  const api = globalThis.fetch as unknown as (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;
  vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
    if (typeof input === 'string' && input.startsWith('https://storage.test/')) {
      const headers = new Headers(init?.headers);
      storageCalls.push({
        url: input,
        method: init?.method ?? 'GET',
        contentType: headers.get('Content-Type'),
        size: init?.body instanceof Blob ? init.body.size : 0,
      });
      return new Response(null, { status: w.storageStatus });
    }
    return api(input, init);
  });
  return calls;
}

function Probe() {
  const l = useLocation();
  return <output data-testid="url">{`${l.pathname}${l.search}`}</output>;
}

export function renderAt(route: string) {
  queryClient.clear();
  return render(
    <AuthContext.Provider value={signedIn}>
      <MemoryRouter initialEntries={[route]}>
        <App />
        <Probe />
      </MemoryRouter>
    </AuthContext.Provider>,
  );
}

export const urlOf = () => new URL(document.querySelector<HTMLElement>('[data-testid="url"]')!.textContent!, 'http://x');
