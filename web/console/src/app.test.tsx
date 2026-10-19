import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { axe } from 'vitest-axe';
import { App, queryClient } from './App';
import { AuthContext, type AuthState } from './auth/AuthProvider';
import i18n, { setLanguage } from './i18n';
import { ErrorBoundary } from './shell/ErrorBoundary';

const signedIn: AuthState = {
  status: 'authenticated',
  name: 'Dev Admin',
  email: 'admin@dam.local',
  accessToken: 'tok',
  signIn: vi.fn(async () => {}),
  signOut: vi.fn(async () => {}),
};
const signedOut: AuthState = { status: 'unauthenticated', signIn: vi.fn(async () => {}), signOut: vi.fn(async () => {}) };

const ME = {
  userId: 'u1',
  email: 'admin@dam.local',
  displayName: 'Dev Admin',
  tenantId: '0197a000-0000-7000-8000-000000000001',
  roles: ['Admin', 'Editor'],
  groups: ['brand-approvers'],
  permissions: ['assets.read', 'assets.update'],
  attributes: { region: ['North'] },
  accessRuleCount: 1,
  inactiveRoles: [] as string[],
  status: 'active',
};

function renderAt(route: string, auth: AuthState) {
  return render(
    <AuthContext.Provider value={auth}>
      <MemoryRouter initialEntries={[route]}>
        <App />
      </MemoryRouter>
    </AuthContext.Provider>,
  );
}

function mockFetch(handler: (request: Request) => Response | Promise<Response>) {
  const f = vi.fn((request: Request) => Promise.resolve(handler(request)));
  vi.stubGlobal('fetch', f);
  return f;
}
const json = (body: unknown, status = 200, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json', ...headers },
  });

const GREETING = /^Good (morning|afternoon|evening), Dev$/;

const ASSET = (id: string, title: string, status: string, type = 'image') => ({
  id,
  title,
  status,
  type,
  folderId: null,
  updatedAt: new Date(Date.now() - 3 * 3600_000).toISOString(),
  createdAt: new Date(Date.now() - 3 * 3600_000).toISOString(),
});
const RECENT = [ASSET('a1', 'Acme spring hero', 'draft'), ASSET('a2', 'Acme brochure', 'in_review', 'document')];
const SNAPSHOT = {
  hits: [],
  total: 12,
  nextCursor: null,
  facets: [
    {
      field: 'status',
      values: [
        { value: 'draft', count: 4 },
        { value: 'in_review', count: 3 },
        { value: 'published', count: 5 },
      ],
    },
  ],
};

const day = (n: number) => new Date(Date.now() - n * 86_400_000).toISOString().slice(0, 10);
const OVERVIEW = {
  generatedAt: new Date().toISOString(),
  periodDays: 30,
  usage: {
    total: 42,
    previousTotal: 21,
    byDay: Array.from({ length: 30 }, (_, i) => ({ date: day(29 - i), count: i % 3 })),
    byType: [{ eventType: 'download', count: 40 }],
    byChannel: [{ channel: 'console', count: 42 }],
  },
  topAssets: [{ assetId: 'a1', title: 'Acme spring hero', type: 'image', count: 9 }],
  uploads: Array.from({ length: 12 }, (_, i) => ({ weekStart: day(77 - i * 7), count: 1 })),
  storage: { totalBytes: 5 * 1024 ** 3, byType: [{ type: 'image', count: 10, bytes: 5 * 1024 ** 3 }] },
};
const TASKS = {
  pendingForMe: 3,
  overdueForMe: 1,
  pendingTotal: 5,
  overdueTotal: 2,
  completed30d: 12,
  completedOnTime30d: 11,
  slaAttainmentPercent: 91.7,
};
const UNUSED = {
  items: [
    {
      id: 'a9',
      title: 'Acme old banner',
      type: 'image',
      status: 'published',
      folderId: null,
      folderPath: 'Brand',
      createdAt: '2024-01-01T00:00:00Z',
      lastUsedAt: null,
      useCount: 0,
      idleSince: '2024-01-01T00:00:00Z',
      idleDays: 640,
      sizeBytes: 2048,
    },
  ],
  total: 1,
  totalIsExact: true,
  nextCursor: null,
  cutoff: '2026-04-01T00:00:00Z',
};

/** Everything Home asks for; `overrides` replaces one endpoint's response (by path) to script empty/failed sections. */
function homeFetch(overrides: Record<string, () => Response> = {}) {
  return mockFetch((request) => {
    const url = new URL(request.url);
    const custom = overrides[url.pathname];
    if (custom) return custom();
    switch (url.pathname) {
      case '/api/v1/me':
        return json(ME);
      case '/api/v1/search':
        return json(SNAPSHOT);
      case '/api/v1/assets':
        return json({
          items: url.searchParams.get('status') === 'in_review' ? RECENT.filter((a) => a.status === 'in_review') : RECENT,
          nextCursor: null,
        });
      case '/api/v1/assets/links/batch':
        return json({ a1: 'https://cdn.example/a1.jpg', a2: null, a9: null });
      case '/api/v1/tasks/summary':
        return json(TASKS);
      case '/api/v1/analytics/overview':
        return json(OVERVIEW);
      case '/api/v1/analytics/unused-assets':
        return json(UNUSED);
      case '/api/v1/collections':
        return json([
          {
            id: 'c1',
            name: 'Acme campaign',
            type: 'static',
            query: null,
            assetIds: ['a1', 'a2'],
            visibility: 'private',
            ownerId: 'u1',
            createdAt: '2026-01-01T00:00:00Z',
            updatedAt: '2026-01-02T00:00:00Z',
          },
        ]);
      default:
        return json({ title: 'Not found', status: 404 }, 404);
    }
  });
}

beforeEach(() => {
  queryClient.clear();
  setLanguage('en');
});
afterEach(() => vi.unstubAllGlobals());

describe('routing and auth', () => {
  it('sends anonymous users to the sign-in page and offers sign in', async () => {
    renderAt('/me', signedOut);
    expect(await screen.findByRole('heading', { name: 'Sign in to Orbit' })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    expect(signedOut.signIn).toHaveBeenCalledWith('/me'); // returns the user to where they were headed
  });

  it('shows a loading status while auth is resolving', () => {
    renderAt('/me', { ...signedOut, status: 'loading' });
    expect(screen.getByRole('status')).toHaveTextContent('Loading');
  });

  it('renders the account page from GET /api/v1/me with the bearer token', async () => {
    const f = mockFetch(() => json(ME));
    renderAt('/me', signedIn);
    expect(await screen.findByRole('heading', { name: 'My account' })).toBeInTheDocument();
    expect(await screen.findByText(ME.tenantId)).toBeInTheDocument();
    expect(screen.getByText('Editor')).toBeInTheDocument();
    expect(screen.getByText('brand-approvers')).toBeInTheDocument();
    expect(screen.getByText('2 permissions')).toBeInTheDocument();
    expect(screen.getByText('region')).toBeInTheDocument();
    expect(screen.getByText('North')).toBeInTheDocument();
    expect(screen.queryByText(/switched off/)).not.toBeInTheDocument();
    const [request] = f.mock.calls[0]!;
    expect(new URL(request.url).pathname).toBe('/api/v1/me');
    expect(request.headers.get('Authorization')).toBe('Bearer tok');
  });

  it('warns when a role is switched off because an attribute is missing', async () => {
    mockFetch(() => json({ ...ME, roles: ['Dealer'], permissions: [], inactiveRoles: ['Dealer'] }));
    renderAt('/me', signedIn);
    expect(await screen.findByRole('alert')).toHaveTextContent('switched off until the missing attribute');
    expect(screen.getByText('0 permissions')).toBeInTheDocument();
  });

  it('shows the problem title and correlation id when the API fails', async () => {
    mockFetch(() => json({ title: 'No tenant', status: 403, correlationId: 'abc123' }, 403));
    renderAt('/me', signedIn);
    expect(await screen.findByRole('alert')).toHaveTextContent('403 No tenant');
    expect(screen.getByText('Reference: abc123')).toBeInTheDocument();
  });

  it('shows a not-found page for unknown routes', async () => {
    renderAt('/nope', signedIn);
    expect(await screen.findByRole('heading', { name: 'Page not found' })).toBeInTheDocument();
  });
});

describe('shell', () => {
  it('has landmarks, a skip link, the tenant switcher and a working user menu', async () => {
    homeFetch();
    renderAt('/', signedIn);
    expect(await screen.findByRole('heading', { name: GREETING })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Skip to main content' })).toHaveAttribute('href', '#main');
    expect(screen.getAllByRole('banner').length).toBeGreaterThan(0); // the top bar (and the page header's <header>)
    expect(screen.getByRole('navigation', { name: 'Primary' })).toBeInTheDocument();
    expect(screen.getByRole('main')).toBeInTheDocument();

    const trigger = screen.getByRole('button', { name: /Account menu/ });
    expect(trigger).toHaveAttribute('aria-expanded', 'false');
    await userEvent.click(trigger);
    expect(await screen.findByLabelText('Tenant')).toBeDisabled(); // tenant and language live in the account menu
    await userEvent.click(screen.getByRole('button', { name: 'Sign out' }));
    expect(signedIn.signOut).toHaveBeenCalled();
  });

  it('closes the user menu with Escape and returns focus to the trigger', async () => {
    homeFetch();
    renderAt('/', signedIn);
    const trigger = await screen.findByRole('button', { name: /Account menu/ });
    await userEvent.click(trigger);
    await userEvent.keyboard('{Escape}');
    expect(trigger).toHaveAttribute('aria-expanded', 'false');
    expect(trigger).toHaveFocus();
  });

  it('switches language to Hindi, updates <html lang> and remembers the choice', async () => {
    mockFetch(() => json(ME));
    renderAt('/me', signedIn);
    await screen.findByRole('heading', { name: 'My account' });
    await userEvent.click(screen.getByRole('button', { name: /Account menu/ }));
    await userEvent.selectOptions(screen.getByLabelText('Language'), 'hi');
    expect(await screen.findByRole('heading', { name: 'मेरा खाता' })).toBeInTheDocument();
    expect(document.documentElement.lang).toBe('hi');
    expect(localStorage.getItem('dam.lang')).toBe('hi');
    expect(i18n.language).toBe('hi');
  });

  it.each([
    ['/login', signedOut],
    ['/me', signedIn],
    ['/', signedIn],
    ['/nope', signedIn],
  ] as const)('has no axe violations on %s', async (route, auth) => {
    homeFetch();
    const { container } = renderAt(route, auth);
    await waitFor(() => expect(screen.getByRole('main').textContent).not.toBe(''));
    if (route === '/me') await screen.findByText(ME.tenantId);
    if (route === '/') await screen.findByRole('link', { name: /Acme spring hero/ });
    expect(await axe(container)).toHaveNoViolations();
  });
});

describe('home', () => {
  it('greets by first name and shows the library overview, recent assets, review queue and collections', async () => {
    const f = homeFetch();
    renderAt('/', signedIn);
    expect(await screen.findByRole('heading', { name: GREETING })).toBeInTheDocument();

    const stats = await screen.findByRole('list', { name: 'Library overview' });
    expect(await within(stats).findByRole('link', { name: /Total assets.*12/ })).toHaveAttribute('href', '/library');
    expect(within(stats).getByRole('link', { name: /Total assets.*5 published/ })).toBeInTheDocument(); // the published count lives here now
    expect(within(stats).queryByRole('link', { name: /^Published/ })).not.toBeInTheDocument();
    const openTasks = await within(stats).findByRole('link', { name: /Open tasks.*3.*1 overdue.*92% on time/ });
    expect(openTasks).toHaveAttribute('href', '/tasks');
    expect(await within(stats).findByText('5.0 GB')).toBeInTheDocument();
    expect(within(stats).getByText(/Uses, last 30 days/)).toBeInTheDocument();
    expect(screen.getByRole('img', { name: 'Uses per day over the last 30 days' })).toBeInTheDocument();
    expect(await screen.findByText('9 uses')).toBeInTheDocument();
    expect(await screen.findByText(/published asset not used in the last 6 months/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Acme old banner' })).toHaveAttribute('href', '/library/assets/a9');
    expect(screen.getByText('Never used')).toBeInTheDocument();

    // The review queue lives behind the Activity card's second tab.
    await userEvent.click(screen.getByRole('button', { name: /Needs review/ }));
    const review = await screen.findByRole('list', { name: 'Waiting for review' });
    expect(within(review).getByRole('link', { name: 'Open Acme brochure' })).toHaveAttribute('href', '/library/assets/a2');

    // One screen, no scrolling: Recently added and Collections are not on the dashboard.
    expect(screen.queryByText('Recently added')).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Collections' })).not.toBeInTheDocument();

    expect(within(screen.getByRole('main')).getByRole('link', { name: 'New collection' })).toHaveAttribute(
      'href',
      '/collections',
    );
    expect(f).toHaveBeenCalled();
    expect(document.title).toBe('Home - Orbit');
  });

  it('says so plainly when nothing is waiting for review or has been used', async () => {
    homeFetch({
      '/api/v1/assets': () => json({ items: [], nextCursor: null }),
      '/api/v1/search': () => json({ ...SNAPSHOT, total: 0, facets: [] }),
      '/api/v1/analytics/overview': () => json({ ...OVERVIEW, usage: { ...OVERVIEW.usage, total: 0 }, topAssets: [] }),
    });
    renderAt('/', signedIn);
    expect(await screen.findByText('No usage recorded in this period yet.')).toBeInTheDocument();
    expect(await screen.findByText('Nothing has been used yet')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Needs review' }));
    expect(await screen.findByText('Nothing is waiting for review')).toBeInTheDocument();
  });

  it('degrades quietly when one request fails: that card says so, the rest still render', async () => {
    homeFetch({ '/api/v1/assets': () => json({ title: 'Boom', status: 500 }, 500) });
    renderAt('/', signedIn);
    expect(await screen.findByText('Acme old banner')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: /Needs review/ }));
    expect(await screen.findByText('This could not be loaded right now.', undefined, { timeout: 8000 })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  }, 15000);

  it('shows unavailable counts instead of an error when the overview cannot be loaded', async () => {
    homeFetch({ '/api/v1/search': () => json({ title: 'Forbidden', status: 403 }, 403) });
    renderAt('/', signedIn);
    const stats = await screen.findByRole('list', { name: 'Library overview' });
    expect(await within(stats).findAllByText('–', { selector: '.dam-stat__value' })).toHaveLength(1);
    expect(await within(stats).findByRole('link', { name: /Open tasks.*3/ })).toBeInTheDocument(); // independent of the library search
  });

  it('shows "None overdue" and leaves the SLA figure out when the caller is not allowed the tenant-wide numbers', async () => {
    homeFetch({
      '/api/v1/tasks/summary': () =>
        json({
          ...TASKS,
          overdueForMe: 0,
          pendingTotal: null,
          overdueTotal: null,
          completed30d: null,
          completedOnTime30d: null,
          slaAttainmentPercent: null,
        }),
    });
    renderAt('/', signedIn);
    const stats = await screen.findByRole('list', { name: 'Library overview' });
    const card = await within(stats).findByRole('link', { name: /Open tasks/ });
    expect(card).toHaveTextContent('None overdue');
    expect(card).not.toHaveTextContent('on time');
    expect(card.querySelector('.dam-stat--danger')).toBeNull();
  });

  it('marks the Open tasks card when something is overdue, and says when nothing is waiting', async () => {
    homeFetch();
    const { unmount } = renderAt('/', signedIn);
    const stats = await screen.findByRole('list', { name: 'Library overview' });
    const card = await within(stats).findByRole('link', { name: /Open tasks/ });
    expect(card.querySelector('.dam-stat--danger')).not.toBeNull();
    unmount();
    queryClient.clear();

    homeFetch({
      '/api/v1/tasks/summary': () => json({ ...TASKS, pendingForMe: 0, overdueForMe: 0, slaAttainmentPercent: null }),
    });
    renderAt('/', signedIn);
    expect(await screen.findByRole('link', { name: /Open tasks.*0.*Nothing waiting for you/ })).toBeInTheDocument();
  });

  it('shows the Open tasks card with a dash and a note when the summary cannot be loaded', async () => {
    homeFetch({ '/api/v1/tasks/summary': () => json({ title: 'Forbidden', status: 403 }, 403) });
    renderAt('/', signedIn);
    expect(await screen.findByRole('link', { name: /Open tasks.*–.*Couldn't load/ })).toBeInTheDocument();
  });

  it('omits the usage panels, and shows review and draft counts instead, for a role without analytics access', async () => {
    homeFetch({
      '/api/v1/analytics/overview': () => json({ title: 'Forbidden', status: 403 }, 403),
      '/api/v1/analytics/unused-assets': () => json({ title: 'Forbidden', status: 403 }, 403),
    });
    renderAt('/', signedIn);
    const stats = await screen.findByRole('list', { name: 'Library overview' });
    expect(await within(stats).findByRole('link', { name: /In review.*3/ })).toBeInTheDocument();
    expect(within(stats).getByRole('link', { name: /Drafts.*4/ })).toBeInTheDocument();
    expect(within(stats).getByRole('link', { name: /Open tasks.*3/ })).toHaveAttribute('href', '/tasks');
    expect(screen.queryByRole('heading', { name: 'Not used' })).not.toBeInTheDocument();
    expect(screen.queryByText('Most used')).not.toBeInTheDocument();
  });
});

describe('sign-in page', () => {
  it('shows the brand panel beside the sign-in card and a note about the organisation account', async () => {
    renderAt('/login', signedOut);
    expect(await screen.findByRole('heading', { name: 'Sign in to Orbit' })).toBeInTheDocument();
    const panel = screen.getByRole('complementary', { name: 'About Orbit' });
    expect(within(panel).getByText(/Manage, review and publish every digital asset/)).toBeInTheDocument();
    expect(within(panel).getAllByRole('listitem')).toHaveLength(3);
    expect(screen.getByText('Sign-in uses your organisation account.')).toBeInTheDocument();
    expect(document.title).toBe('Sign in - Orbit');
  });

  it('keeps the button busy after click and reports a failed start', async () => {
    const failing: AuthState = { ...signedOut, signIn: vi.fn(async () => Promise.reject(new Error('no idp'))) };
    renderAt('/login', failing);
    await userEvent.click(await screen.findByRole('button', { name: 'Sign in' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Sign-in could not be started');
    expect(screen.getByRole('button', { name: 'Sign in' })).toBeEnabled();
  });
});

describe('account page details', () => {
  it('groups permissions behind a disclosure and copies the tenant id with a confirmation toast', async () => {
    homeFetch();
    const writeText = vi.fn(async () => {});
    vi.stubGlobal('navigator', { ...navigator, clipboard: { writeText } });
    renderAt('/me', signedIn);
    await screen.findByText(ME.tenantId);
    expect(document.title).toBe('My account - Orbit');

    expect(screen.getByRole('img', { name: 'Dev Admin' })).toBeInTheDocument(); // the avatar
    await userEvent.click(screen.getByText('Show all permissions'));
    expect(screen.getByText('assets.read')).toBeInTheDocument();
    expect(screen.getByText('assets.update')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Copy tenant ID' }));
    expect(writeText).toHaveBeenCalledWith(ME.tenantId);
    expect(await screen.findByText('Tenant ID copied')).toBeInTheDocument();
  });

  it('marks a switched-off role as such and explains an empty permission set', async () => {
    mockFetch(() => json({ ...ME, roles: ['Dealer'], permissions: [], inactiveRoles: ['Dealer'] }));
    renderAt('/me', signedIn);
    expect(await screen.findByText('Some roles are switched off')).toBeInTheDocument();
    expect(screen.getByText('Switched off')).toBeInTheDocument();
    expect(screen.getByText('No permissions are active.')).toBeInTheDocument();
  });
});

describe('status pages', () => {
  it('offers a way home and to the library from the not-found page', async () => {
    homeFetch();
    renderAt('/nope', signedIn);
    expect(await screen.findByRole('heading', { level: 1, name: 'Page not found' })).toBeInTheDocument();
    const main = within(screen.getByRole('main'));
    expect(main.getByRole('link', { name: 'Back to home' })).toHaveAttribute('href', '/');
    expect(main.getByRole('link', { name: 'Open the library' })).toHaveAttribute('href', '/library');
    expect(document.title).toBe('Page not found - Orbit');
  });
});

describe('keyboard shortcuts', () => {
  it('navigates on a single-key shortcut', async () => {
    mockFetch((request) => {
      const path = new URL(request.url).pathname;
      if (path === '/api/v1/me') return json(ME);
      if (path === '/api/v1/folders') return json([]);
      if (path === '/api/v1/assets') return json({ items: [], nextCursor: null });
      if (path === '/api/v1/search') return json(SNAPSHOT);
      if (path === '/api/v1/collections') return json([]);
      throw new Error(`unexpected request: ${path}`);
    });
    renderAt('/', signedIn);
    await screen.findByRole('heading', { name: GREETING });

    await userEvent.keyboard('l');
    expect(await screen.findByRole('heading', { name: 'Library' })).toBeInTheDocument();
  });

  it('does not hijack typing into a text field', async () => {
    mockFetch((request) => {
      const path = new URL(request.url).pathname;
      if (path === '/api/v1/me') return json(ME);
      if (path === '/api/v1/folders') return json([]);
      throw new Error(`unexpected request: ${path}`);
    });
    renderAt('/upload', signedIn);
    await screen.findByRole('heading', { name: 'Upload' });
    // The destination form (with its Tags field) appears once there is a file to put somewhere.
    await userEvent.upload(
      document.querySelector('input[type="file"]') as HTMLInputElement,
      new File(['x'], 'a.jpg', { lastModified: 1 }),
    );

    await userEvent.type(await screen.findByLabelText('Tags'), 'library');
    expect(screen.getByRole('heading', { name: 'Upload' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Library' })).not.toBeInTheDocument();
  });

  it('opens and closes the shortcuts help with "?"', async () => {
    homeFetch();
    renderAt('/', signedIn);
    await screen.findByRole('heading', { name: GREETING });

    await userEvent.keyboard('?');
    const dialog = await screen.findByRole('dialog', { name: 'Keyboard shortcuts' });
    expect(within(dialog).getByText('Go to Library')).toBeInTheDocument();

    await userEvent.keyboard('?');
    expect(screen.queryByRole('dialog', { name: 'Keyboard shortcuts' })).not.toBeInTheDocument();
  });

  it('also opens the help dialog from the visible button', async () => {
    homeFetch();
    renderAt('/', signedIn);
    await userEvent.click(await screen.findByRole('button', { name: 'Keyboard shortcuts' }));
    expect(await screen.findByRole('dialog', { name: 'Keyboard shortcuts' })).toBeInTheDocument();
  });
});

describe('ErrorBoundary', () => {
  it('shows a recoverable message instead of a blank screen', () => {
    const Bomb = () => {
      throw new Error('boom');
    };
    vi.spyOn(console, 'error').mockImplementation(() => {});
    render(
      <ErrorBoundary>
        <Bomb />
      </ErrorBoundary>,
    );
    expect(screen.getByRole('alert')).toHaveTextContent('Something went wrong');
    expect(screen.getByRole('button', { name: 'Reload' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Go to home' })).toHaveAttribute('href', '/');
    vi.restoreAllMocks();
  });
});
