// Shared scaffolding for the workflow designer tests: a fetch mock that records every call, the console rendered at a
// route, and a small set of fixtures. Test-only; nothing in the app imports this.
import { render } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { App, queryClient } from '../../../App';
import { AuthContext, type AuthState } from '../../../auth/AuthProvider';
import { twoLevelApproval } from '../definition';

export const signedIn: AuthState = {
  status: 'authenticated',
  name: 'Dev Admin',
  email: 'admin@dam.local',
  accessToken: 'tok',
  signIn: vi.fn(async () => {}),
  signOut: vi.fn(async () => {}),
};

export const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

export interface Call {
  method: string;
  pathname: string;
  search: URLSearchParams;
  body: unknown;
}

export let calls: Call[] = [];
export const callsTo = (method: string, pathname: string) => calls.filter((c) => c.method === method && c.pathname === pathname);

export function mockApi(handler: (c: Call) => Response | undefined) {
  calls = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      if (!(input instanceof Request)) throw new Error('raw fetch');
      const u = new URL(input.url);
      const text = input.method === 'GET' || input.method === 'DELETE' ? '' : await input.clone().text();
      const call: Call = {
        method: input.method,
        pathname: u.pathname,
        search: u.searchParams,
        body: text ? JSON.parse(text) : undefined,
      };
      calls.push(call);
      return handler(call) ?? json({ title: 'Not found', status: 404 }, 404);
    }),
  );
}

export function renderAt(route: string) {
  queryClient.clear();
  return render(
    <AuthContext.Provider value={signedIn}>
      <MemoryRouter initialEntries={[route]}>
        <App />
      </MemoryRouter>
    </AuthContext.Provider>,
  );
}

export const roles = ['Admin', 'Approver', 'Librarian', 'Editor'].map((name, i) => ({
  id: `r${i}`,
  name,
  description: '',
  isBuiltIn: true,
  permissions: [],
  attributeRestricted: false,
  requiredAttributes: [],
}));

export const asha = {
  id: '11111111-1111-4111-8111-111111111111',
  externalSubject: 'asha',
  email: 'asha@acme.test',
  displayName: 'Asha Rao',
  status: 'active',
  attributes: {},
  roles: [],
  groups: [],
  createdAt: '2026-01-01T00:00:00Z',
  lastSeenAt: '2026-01-01T00:00:00Z',
};

export const workflow = (over: Record<string, unknown> = {}) => ({
  id: 'w1',
  name: 'Two-level approval',
  description: 'Review, then final approval.',
  definition: twoLevelApproval(),
  usedByFolders: 1,
  bindings: 2,
  runningInstances: 3,
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  ...over,
});

export const folder = (over: Record<string, unknown> = {}) => ({
  id: 'f1',
  parentId: null,
  path: 'campaigns',
  label: 'campaigns',
  name: 'Campaigns',
  inheritedMetadata: {},
  defaultWorkflowId: null,
  createdAt: '2026-01-01T00:00:00Z',
  ...over,
});

/** Answers the endpoints the designer reads; a test passes what differs and its own handlers for writes. */
export function standardApi(
  opts: {
    workflows?: unknown[];
    bindings?: unknown[];
    folders?: unknown[];
    users?: unknown[];
    rolesStatus?: number;
  } = {},
  extra: (c: Call) => Response | undefined = () => undefined,
) {
  mockApi((c) => {
    const mine = extra(c);
    if (mine) return mine;
    if (c.method === 'GET' && c.pathname === '/api/v1/workflows') return json(opts.workflows ?? [workflow()]);
    if (c.method === 'GET' && c.pathname === '/api/v1/workflows/bindings') return json(opts.bindings ?? []);
    if (c.method === 'GET' && c.pathname === '/api/v1/folders') return json(opts.folders ?? []);
    if (c.method === 'GET' && c.pathname === '/api/v1/roles')
      return opts.rolesStatus ? json({ title: 'Forbidden', status: opts.rolesStatus }, opts.rolesStatus) : json(roles);
    // The user directory (GET /users needs users.manage, so the pickers never call it; a call to it here is a 404).
    if (c.method === 'GET' && c.pathname === '/api/v1/users/directory') {
      const q = c.search.get('q')?.toLowerCase();
      const all = (opts.users ?? [asha]) as (typeof asha)[];
      const items = (q ? all.filter((u) => u.displayName.toLowerCase().includes(q) || u.email.includes(q)) : all).map(
        ({ id, displayName, email }) => ({ id, displayName, email }),
      );
      return json({ items, nextCursor: null });
    }
    return undefined;
  });
}
