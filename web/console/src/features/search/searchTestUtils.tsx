// Shared helpers for the Search screen's tests (search.test.tsx, smartSearch.test.tsx). Not part of the app bundle.
import { render } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { App } from '../../App';
import { AuthContext, type AuthState } from '../../auth/AuthProvider';

export const signedIn: AuthState = {
  status: 'authenticated',
  name: 'Dev Admin',
  email: 'admin@dam.local',
  accessToken: 'tok',
  signIn: vi.fn(async () => {}),
  signOut: vi.fn(async () => {}),
};

/** Makes the router's current URL observable to assertions. */
function LocationProbe() {
  const { pathname, search } = useLocation();
  return <output data-testid="location">{pathname + search}</output>;
}

export function renderAt(route: string) {
  return render(
    <AuthContext.Provider value={signedIn}>
      <MemoryRouter initialEntries={[route]}>
        <App />
        <LocationProbe />
      </MemoryRouter>
    </AuthContext.Provider>,
  );
}

export const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

export interface Call {
  method: string;
  pathname: string;
  json: () => Promise<unknown>;
}

export const CAPS_OFF = {
  enabled: false,
  semanticSearch: false,
  enrichment: false,
  textVectors: false,
  imageVectors: false,
  provider: 'none',
  sendsDataOffSite: false,
};
export const CAPS_SMART = {
  ...CAPS_OFF,
  enabled: true,
  semanticSearch: true,
  enrichment: true,
  textVectors: true,
  imageVectors: true,
  provider: 'self-hosted',
};

/** `thumbnailLinks` answers the grid's own batch thumbnail-link request (defaulting every id to no thumbnail) and `caps`
 * answers `GET /ai/capabilities` (Smart search off unless a test says otherwise), so tests that don't care about either
 * don't each need to teach `handler` about a request they never asked for. */
export function mockApi(
  handler: (call: Call) => Response | Promise<Response> | undefined,
  thumbnailLinks: Record<string, string | null> = {},
  caps: typeof CAPS_OFF = CAPS_OFF,
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
        if (call.pathname === '/api/v1/ai/capabilities') return json(caps);
        const response = handler(call);
        if (response) return response;
        throw new Error(`unexpected request: ${call.method} ${call.pathname}`);
      }
      throw new Error(`unexpected raw fetch: ${String(input)}`);
    }),
  );
}

export function hit(over: Record<string, unknown> = {}) {
  return {
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
    ...over,
  };
}

export const RESULT = {
  hits: [hit()],
  total: 1,
  facets: [
    { field: 'type', values: [{ value: 'image', count: 1 }] },
    { field: 'status', values: [{ value: 'published', count: 1 }] },
  ],
  nextCursor: null,
};
