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

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

const unused = (id: string, status: string, lastUsedAt: string | null = null) => ({
  id,
  title: `Acme ${id}`,
  type: 'image',
  status,
  folderId: null,
  folderPath: null,
  createdAt: '2024-01-01T00:00:00Z',
  lastUsedAt,
  useCount: lastUsedAt ? 3 : 0,
  idleSince: lastUsedAt ?? '2024-01-01T00:00:00Z',
  idleDays: 400,
  sizeBytes: 1024,
});

interface Call {
  method: string;
  pathname: string;
  search: URLSearchParams;
  json: () => Promise<unknown>;
}

let calls: Call[] = [];

function mockApi(handler: (c: Call) => Response | undefined) {
  calls = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      if (!(input instanceof Request)) throw new Error('raw fetch');
      const u = new URL(input.url);
      const call: Call = { method: input.method, pathname: u.pathname, search: u.searchParams, json: () => input.clone().json() };
      calls.push(call);
      if (call.pathname === '/api/v1/assets/links/batch') return json({});
      if (call.pathname === '/api/v1/analytics/overview') return json({ title: 'x', status: 500 }, 500);
      return handler(call) ?? json({ title: 'Not found', status: 404 }, 404);
    }),
  );
}

function renderAt(route: string) {
  return render(
    <AuthContext.Provider value={signedIn}>
      <MemoryRouter initialEntries={[route]}>
        <App />
      </MemoryRouter>
    </AuthContext.Provider>,
  );
}

const page = (items: ReturnType<typeof unused>[], total = items.length) =>
  json({ items, total, totalIsExact: true, nextCursor: null, cutoff: '2026-01-01T00:00:00Z' });
const unusedCalls = () => calls.filter((c) => c.pathname === '/api/v1/analytics/unused-assets');

beforeEach(() => {
  queryClient.clear();
  setLanguage('en');
  window.localStorage.clear();
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('unused assets page', () => {
  it('defaults to six months and published assets, and shows never-used and last-used assets', async () => {
    mockApi((c) =>
      c.pathname === '/api/v1/analytics/unused-assets'
        ? page([unused('a1', 'published'), unused('a2', 'published', '2025-06-01T00:00:00Z')])
        : undefined,
    );
    renderAt('/insights/unused');
    expect(await screen.findByRole('heading', { level: 1, name: 'Unused assets' })).toBeInTheDocument();
    expect(await screen.findByText(/published assets not used in the last 6 months/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '6 months', pressed: true })).toBeInTheDocument();
    expect(screen.getByText('Never used')).toBeInTheDocument();
    expect(unusedCalls()[0]!.search.get('days')).toBe('183');
    expect(unusedCalls()[0]!.search.get('status')).toBe('published');
  });

  it('switches period from the presets and remembers the choice', async () => {
    mockApi((c) => (c.pathname === '/api/v1/analytics/unused-assets' ? page([unused('a1', 'published')]) : undefined));
    renderAt('/insights/unused');
    await screen.findByText('Acme a1');
    await userEvent.click(screen.getByRole('button', { name: '1 year' }));
    await waitFor(() => expect(unusedCalls().some((c) => c.search.get('days') === '365')).toBe(true));
    await userEvent.click(screen.getByRole('button', { name: '30 days' }));
    await waitFor(() => expect(unusedCalls().some((c) => c.search.get('days') === '30')).toBe(true));
    expect(window.localStorage.getItem('dam.unusedPeriod')).toBe('30d');
  });

  it('takes a custom period as an amount and a unit, applied as you type, and rejects out-of-range values', async () => {
    mockApi((c) => (c.pathname === '/api/v1/analytics/unused-assets' ? page([unused('a1', 'published')]) : undefined));
    renderAt('/insights/unused?unused=c:90d');
    await screen.findByText('Acme a1');
    expect(unusedCalls()[0]!.search.get('days')).toBe('90');
    const group = screen.getByRole('group', { name: 'Custom period' });
    await userEvent.selectOptions(within(group).getByRole('combobox', { name: 'Unit' }), 'weeks');
    await waitFor(() => expect(unusedCalls().some((c) => c.search.get('days') === '630')).toBe(true));
    const amount = within(group).getByRole('spinbutton', { name: 'Amount' });
    await userEvent.clear(amount);
    await userEvent.type(amount, '2');
    await waitFor(() => expect(unusedCalls().some((c) => c.search.get('days') === '14')).toBe(true));
    await userEvent.clear(amount);
    await userEvent.type(amount, '999');
    expect(await screen.findByRole('alert')).toHaveTextContent('whole number');
  });

  it('asks for every status when "All statuses" is chosen', async () => {
    mockApi((c) => (c.pathname === '/api/v1/analytics/unused-assets' ? page([unused('a1', 'draft')]) : undefined));
    renderAt('/insights/unused');
    await screen.findByText('Acme a1');
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Status' }), 'all');
    await waitFor(() => expect(unusedCalls().some((c) => c.search.get('status') === 'all')).toBe(true));
    expect(await screen.findByText(/assets? not used in the last 6 months/)).toBeInTheDocument();
  });

  it('archives the selection after confirming, expiring published assets first', async () => {
    const transitions: string[] = [];
    mockApi((c) => {
      if (c.pathname === '/api/v1/analytics/unused-assets') return page([unused('a1', 'published'), unused('a2', 'expired')]);
      const m = /^\/api\/v1\/assets\/(\w+)\/transitions\/(\w+)$/.exec(c.pathname);
      if (m) {
        transitions.push(`${m[1]}:${m[2]}`);
        return json({});
      }
      return undefined;
    });
    renderAt('/insights/unused');
    await screen.findByText('Acme a1');
    await userEvent.click(screen.getByRole('checkbox', { name: 'Select Acme a1' }));
    await userEvent.click(screen.getByRole('checkbox', { name: 'Select Acme a2' }));
    expect(screen.getByText('2 assets selected')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Archive' }));
    const dialog = await screen.findByRole('dialog', { name: 'Archive 2 assets?' });
    await userEvent.click(within(dialog).getByRole('button', { name: 'Archive 2 assets' }));
    await waitFor(() => expect(transitions.sort()).toEqual(['a1:archive', 'a1:expire', 'a2:archive']));
    expect(await screen.findByText('2 assets archived')).toBeInTheDocument();
  });

  it('says so plainly when everything has been used', async () => {
    mockApi((c) => (c.pathname === '/api/v1/analytics/unused-assets' ? page([]) : undefined));
    renderAt('/insights/unused');
    expect(await screen.findByText('Everything has been used in the last 6 months')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Export CSV' })).toBeDisabled();
  });

  it('explains missing access instead of showing an empty tracker', async () => {
    mockApi((c) =>
      c.pathname === '/api/v1/analytics/unused-assets' ? json({ title: 'Forbidden', status: 403 }, 403) : undefined,
    );
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const u = new URL((input as Request).url);
        if (u.pathname === '/api/v1/analytics/overview') return json({ title: 'Forbidden', status: 403 }, 403);
        return json({ title: 'Not found', status: 404 }, 404);
      }),
    );
    renderAt('/insights/unused');
    expect(await screen.findByText("Usage analytics aren't available to your role")).toBeInTheDocument();
  });
});

describe('Hindi', () => {
  it('renders the unused assets page, its filters and its table in Hindi', async () => {
    setLanguage('hi');
    mockApi((c) => (c.pathname === '/api/v1/analytics/unused-assets' ? page([unused('a1', 'published')]) : undefined));
    renderAt('/insights/unused');
    expect(await screen.findByText('Acme a1')).toBeInTheDocument();
    expect(await screen.findByRole('heading', { level: 1, name: 'अप्रयुक्त एसेट' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '6 महीने', pressed: true })).toBeInTheDocument();
    expect(screen.getByText('कभी उपयोग नहीं हुआ')).toBeInTheDocument();
    expect(screen.getByRole('combobox', { name: 'स्थिति' })).toBeInTheDocument();
    expect(screen.queryByText('Never used')).not.toBeInTheDocument();
  });
});
