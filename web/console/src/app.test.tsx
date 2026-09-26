import { render, screen, waitFor } from '@testing-library/react';
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

function mockFetch(handler: (url: string, init?: RequestInit) => Response | Promise<Response>) {
  const f = vi.fn((url: string, init?: RequestInit) => Promise.resolve(handler(url, init)));
  vi.stubGlobal('fetch', f);
  return f;
}
const json = (body: unknown, status = 200, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json', ...headers },
  });

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
    expect(screen.getByText(ME.tenantId)).toBeInTheDocument();
    expect(screen.getByText('Editor')).toBeInTheDocument();
    expect(screen.getByText('brand-approvers')).toBeInTheDocument();
    expect(screen.getByText('2 permissions')).toBeInTheDocument();
    expect(screen.getByText('region: North')).toBeInTheDocument();
    expect(screen.queryByText(/switched off/)).not.toBeInTheDocument();
    const [url, init] = f.mock.calls[0]!;
    expect(url).toBe('/api/v1/me');
    expect((init!.headers as Record<string, string>).Authorization).toBe('Bearer tok');
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
    mockFetch(() => json(ME));
    renderAt('/', signedIn);
    expect(await screen.findByRole('heading', { name: 'Welcome, Dev Admin' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Skip to main content' })).toHaveAttribute('href', '#main');
    expect(screen.getByRole('banner')).toBeInTheDocument();
    expect(screen.getByRole('navigation', { name: 'Primary' })).toBeInTheDocument();
    expect(screen.getByRole('main')).toBeInTheDocument();
    expect(await screen.findByLabelText('Tenant')).toBeDisabled();

    const trigger = screen.getByRole('button', { name: /Account menu/ });
    expect(trigger).toHaveAttribute('aria-expanded', 'false');
    await userEvent.click(trigger);
    await userEvent.click(screen.getByRole('button', { name: 'Sign out' }));
    expect(signedIn.signOut).toHaveBeenCalled();
  });

  it('closes the user menu with Escape and returns focus to the trigger', async () => {
    mockFetch(() => json(ME));
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
    await userEvent.selectOptions(screen.getByLabelText('Language'), 'hi');
    expect(await screen.findByRole('heading', { name: 'मेरा खाता' })).toBeInTheDocument();
    expect(document.documentElement.lang).toBe('hi');
    expect(localStorage.getItem('dam.lang')).toBe('hi');
    expect(i18n.language).toBe('hi');
  });

  it.each([
    ['/login', signedOut],
    ['/me', signedIn],
  ] as const)('has no axe violations on %s', async (route, auth) => {
    mockFetch(() => json(ME));
    const { container } = renderAt(route, auth);
    await waitFor(() => expect(screen.getByRole('main').textContent).not.toBe(''));
    if (route === '/me') await screen.findByRole('heading', { name: 'My account' });
    expect(await axe(container)).toHaveNoViolations();
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
    vi.restoreAllMocks();
  });
});
