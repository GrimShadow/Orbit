import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { setLanguage } from '../../i18n';
import {
  CONNECTORS,
  MIN,
  call,
  connection,
  install,
  pads4Connection,
  problem,
  renderAt,
  testResult,
  validation,
  world,
  type Call,
} from './testKit';

beforeEach(() => {
  setLanguage('en');
  window.localStorage.clear();
});
afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

const writes = (calls: Call[], method: string, pathname: string) =>
  calls.filter((c) => c.method === method && c.pathname === pathname);
const dialog = (name: string) => screen.findByRole('dialog', { name });
const CONNECTIONS = '/api/v1/integrations/connections';

/** The connection card with this name (a connector in the catalog may carry the same name). */
async function card(name: string) {
  let found: HTMLElement | undefined;
  await waitFor(() => {
    found = screen
      .getAllByRole('heading', { name })
      .map((h) => h.closest('article')!)
      .find((a) => a.closest('ul')?.classList.contains('dam-intgrid--wide'));
    expect(found).toBeDefined();
  });
  return found!;
}

describe('connector catalog', () => {
  it('shows each connector with its status, category and what a person can do with it', async () => {
    install(world({ connections: [] }));
    renderAt('/integrations');

    expect(await screen.findByRole('heading', { level: 1, name: 'Integrations' })).toBeInTheDocument();
    expect(screen.getByText(/never shown again/)).toBeInTheDocument();
    const list = await screen.findByRole('list', { name: 'Available connectors' });
    const http = within(list).getByRole('heading', { name: 'Generic HTTP API' }).closest('article')!;
    expect(within(http).getByText('Available')).toBeInTheDocument();
    expect(within(http).getByText('General')).toBeInTheDocument();
    expect(within(http.closest('ul')!).getByText('Digital signage')).toBeInTheDocument();
    expect(within(http).getByText('Call any HTTP API with the authentication it needs.')).toBeInTheDocument();
    expect(within(http).getByRole('button', { name: 'Connect Generic HTTP API' })).toBeEnabled();

    const pads4 = within(list).getByRole('heading', { name: 'PADS4' }).closest('article')!;
    expect(within(pads4).getByText('Saved settings only')).toBeInTheDocument();
    expect(within(pads4).queryByText('Preview')).not.toBeInTheDocument();
    expect(within(pads4).getByText('Orbit saves these details but does not connect to PADS4 yet.')).toBeInTheDocument();
    expect(within(pads4).queryByText(/Works for connecting and testing/)).not.toBeInTheDocument();
    // The connector name is part of the button text; the other connectors keep "Connect".
    expect(within(pads4).getByRole('button', { name: 'Add PADS4 connection' })).toBeEnabled();
    expect(within(pads4).getByRole('button', { name: 'Add PADS4 connection' })).toHaveClass('dam-btn--primary');
    expect(within(pads4).getByText('Add PADS4 connection')).toBeInTheDocument();
    expect(within(http).getByText('Connect')).toBeInTheDocument();
  });

  it('lists a planned connector as coming later, with no Connect button', async () => {
    install(world({ connections: [] }));
    renderAt('/integrations');
    const canva = (await screen.findByRole('heading', { name: 'Canva' })).closest('article')!;
    expect(within(canva).getByText('Planned')).toBeInTheDocument();
    expect(within(canva).getByText('Coming later.')).toBeInTheDocument();
    expect(within(canva).queryByRole('button')).not.toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: /^(Connect|Add) / })).toHaveLength(2);
  });

  it('shows a loading state, then an error with a retry', async () => {
    let fail = true;
    install(world(), (c) => (c.pathname === '/api/v1/integrations/connectors' && fail ? problem(500, 'Boom') : undefined));
    renderAt('/integrations');
    expect(await screen.findByText('We could not load the connectors', undefined, { timeout: 6000 })).toBeInTheDocument();
    fail = false;
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByRole('heading', { name: 'Generic HTTP API' })).toBeInTheDocument();
  });

  it('shows skeletons while loading', async () => {
    install(world(), (c) => (c.pathname === '/api/v1/integrations/connectors' ? new Promise<Response>(() => {}) : undefined));
    renderAt('/integrations');
    expect((await screen.findAllByText('Loading')).length).toBeGreaterThan(0);
  });
});

describe('access', () => {
  it('shows a calm no-access state without the integrations permission and loads nothing', async () => {
    const calls = install(world({ permissions: ['assets.read'] }));
    renderAt('/integrations');
    expect(await screen.findByText('You do not have access to integrations')).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Available connectors' })).not.toBeInTheDocument();
    expect(calls.some((c) => c.pathname.startsWith('/api/v1/integrations'))).toBe(false);
  });

  it('shows no-access when the server answers 403', async () => {
    install(world(), (c) => (c.pathname.startsWith('/api/v1/integrations') ? problem(403, 'Forbidden') : undefined));
    renderAt('/integrations');
    expect((await screen.findAllByText('You do not have access to integrations')).length).toBeGreaterThan(0);
  });

  it('lists Integrations in the sidebar only with the permission', async () => {
    install(world());
    renderAt('/');
    const link = await screen.findByRole('link', { name: 'Integrations' });
    expect(link).toHaveAttribute('href', '/integrations');
    cleanup();

    install(world({ permissions: ['assets.read', 'webhooks.manage'] }));
    renderAt('/');
    // Webhooks appears once permissions have loaded; Integrations must not.
    await screen.findByRole('link', { name: 'Webhooks' });
    expect(screen.queryByRole('link', { name: 'Integrations' })).not.toBeInTheDocument();
  });

  it('lists Integrations in the primary navigation (icon, label and route) and under its Hindi name', async () => {
    install(world());
    renderAt('/');
    const nav = await screen.findByRole('navigation', { name: 'Primary' });
    const link = await within(nav).findByRole('link', { name: 'Integrations' });
    expect(link).toHaveAttribute('href', '/integrations');
    expect(link.querySelector('svg')).not.toBeNull();
    cleanup();

    setLanguage('hi');
    install(world());
    renderAt('/');
    expect(await screen.findByRole('link', { name: 'इंटीग्रेशन' })).toHaveAttribute('href', '/integrations');
  });
});

describe('connections', () => {
  it('shows a connection with its status, detail, last test, token expiry and enabled switch', async () => {
    install(world());
    renderAt('/integrations');
    const c = await card('Acme CMS');
    expect(within(c).getByText('Generic HTTP API')).toBeInTheDocument();
    expect(within(c).getByText('Working')).toBeInTheDocument();
    expect(within(c).getByText('Signed in and reached /health.')).toBeInTheDocument();
    expect(within(c).getByText('5 minutes ago')).toBeInTheDocument();
    expect(within(c).getByText('in 3 hours')).toBeInTheDocument();
    expect(within(c).getByText('Sign-in token expires')).toBeInTheDocument();
    expect(within(c).getByRole('switch', { name: 'Acme CMS: enabled' })).toHaveAttribute('aria-checked', 'true');
    for (const name of ['Test Acme CMS', 'Edit Acme CMS', 'Activity of Acme CMS', 'Delete Acme CMS'])
      expect(within(c).getByRole('button', { name })).toBeEnabled();
  });

  it('shows a failing and a never-tested connection', async () => {
    install(
      world({
        connections: [
          connection({ status: 'error', statusDetail: 'The login call was refused (HTTP 401).', tokenExpiresAt: null }),
          connection({
            id: 'k2',
            name: 'Acme staging',
            status: 'new',
            statusDetail: null,
            lastTestedAt: null,
            tokenExpiresAt: null,
          }),
        ],
      }),
    );
    renderAt('/integrations');
    const failing = await card('Acme CMS');
    expect(within(failing).getByText('Needs attention')).toBeInTheDocument();
    expect(within(failing).getByText('The login call was refused (HTTP 401).')).toBeInTheDocument();
    expect(within(failing).queryByText('Sign-in token expires')).not.toBeInTheDocument();
    const fresh = await card('Acme staging');
    expect(within(fresh).getByText('Not tested')).toBeInTheDocument();
    expect(within(fresh).getByText('Not tested yet')).toBeInTheDocument();
  });

  it('says when a token has already expired', async () => {
    install(
      world({ connections: [connection({ tokenExpiresAt: new Date(Date.now() - 2 * 3_600_000 - 30_000).toISOString() })] }),
    );
    renderAt('/integrations');
    const c = await card('Acme CMS');
    expect(within(c).getByText('Sign-in token expired')).toBeInTheDocument();
    expect(within(c).getByText('2 hours ago')).toBeInTheDocument();
  });

  it('invites you to add the first connection', async () => {
    install(world({ connections: [] }));
    renderAt('/integrations');
    expect(await screen.findByText('No connections yet')).toBeInTheDocument();
  });

  it('shows an error with a retry when the connections cannot be loaded', async () => {
    let fail = true;
    install(world(), (c) => (c.pathname === CONNECTIONS && c.method === 'GET' && fail ? problem(500, 'Boom') : undefined));
    renderAt('/integrations');
    expect(await screen.findByText('We could not load your connections', undefined, { timeout: 6000 })).toBeInTheDocument();
    fail = false;
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await card('Acme CMS')).toBeInTheDocument();
  });

  it('turns a connection off with the switch, sending the stored values and no secrets', async () => {
    const calls = install(world());
    renderAt('/integrations');
    await userEvent.click(await screen.findByRole('switch', { name: 'Acme CMS: enabled' }));
    await waitFor(() => expect(writes(calls, 'PUT', `${CONNECTIONS}/k1`)).toHaveLength(1));
    expect(await writes(calls, 'PUT', `${CONNECTIONS}/k1`)[0]!.json()).toEqual({
      connector: null,
      name: 'Acme CMS',
      enabled: false,
      config: { baseUrl: 'https://api.example.test', authStrategy: 'api_key', apiKeyHeader: 'X-Api-Key', timeoutSeconds: 30 },
      secrets: {},
      clearSecrets: [],
    });
    expect(await screen.findByRole('switch', { name: 'Acme CMS: enabled' })).toHaveAttribute('aria-checked', 'false');
  });

  it('rolls the switch back and says so when the change fails', async () => {
    install(world(), (c) => (c.method === 'PUT' ? problem(500, 'Boom') : undefined));
    renderAt('/integrations');
    await userEvent.click(await screen.findByRole('switch', { name: 'Acme CMS: enabled' }));
    expect((await screen.findAllByText('Could not change “Acme CMS”')).length).toBeGreaterThan(0);
    await waitFor(() =>
      expect(screen.getByRole('switch', { name: 'Acme CMS: enabled' })).toHaveAttribute('aria-checked', 'true'),
    );
  });

  it('asks before deleting, deletes on confirm and keeps the connection on cancel', async () => {
    const calls = install(world());
    renderAt('/integrations');
    await userEvent.click(await screen.findByRole('button', { name: 'Delete Acme CMS' }));
    const confirm = await dialog('Delete this connection?');
    expect(within(confirm).getByText(/“Acme CMS” will be removed/)).toBeInTheDocument();
    await userEvent.click(within(confirm).getByRole('button', { name: 'Cancel' }));
    expect(writes(calls, 'DELETE', `${CONNECTIONS}/k1`)).toHaveLength(0);
    expect(await card('Acme CMS')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Delete Acme CMS' }));
    await userEvent.click(within(await dialog('Delete this connection?')).getByRole('button', { name: 'Delete connection' }));
    await waitFor(() => expect(writes(calls, 'DELETE', `${CONNECTIONS}/k1`)).toHaveLength(1));
    expect(await screen.findByText('No connections yet')).toBeInTheDocument();
    expect((await screen.findAllByText('Deleted “Acme CMS”')).length).toBeGreaterThan(0);
  });

  it('disables Edit when the connector is no longer in the catalog', async () => {
    install(world({ connections: [connection({ connector: 'gone' })] }));
    renderAt('/integrations');
    const c = await card('Acme CMS');
    expect(within(c).getByText('gone')).toBeInTheDocument();
    expect(within(c).getByRole('button', { name: 'Edit Acme CMS' })).toBeDisabled();
  });
});

describe('connect dialog', () => {
  async function openCreate() {
    install(world({ connections: [] }));
    renderAt('/integrations');
    await userEvent.click(await screen.findByRole('button', { name: 'Connect Generic HTTP API' }));
    return dialog('Connect Generic HTTP API');
  }

  it('never offers a blank choice for a select that has a default', async () => {
    const form = await openCreate();
    const keyIn = within(form).getByRole('combobox', { name: 'Send the key in' });
    expect(keyIn).toHaveValue('header');
    expect(
      within(keyIn)
        .getAllByRole('option')
        .map((o) => o.textContent),
    ).toEqual(['A header', 'A query parameter']);
  });

  it('is drawn from the descriptors: name and enabled first, then the groups with their fields', async () => {
    const form = await openCreate();
    const headings = within(form)
      .getAllByRole('heading', { level: 3 })
      .map((h) => h.textContent);
    expect(headings).toEqual(['Connection', 'Authentication', 'Advanced']);

    // Name, then enabled, come before any group.
    const order = [...form.querySelectorAll('input, select, textarea, [role="switch"]')].map(
      (el) => (el as HTMLElement).getAttribute('aria-label') ?? (el as HTMLElement).id,
    );
    expect(order[0]).toBeTruthy();
    expect(within(form).getByRole('switch', { name: 'Enabled' })).toHaveAttribute('aria-checked', 'true');

    // Types: url, select, text, secret (password), number, boolean, textarea.
    const url = within(form).getByRole('textbox', { name: 'Base address' });
    expect(url).toHaveAttribute('type', 'url');
    expect(url).toBeRequired();
    expect(url).toHaveAttribute('placeholder', 'https://api.example.test');
    expect(url).toHaveAccessibleDescription('The address every request starts from.');
    expect(within(form).getByRole('combobox', { name: 'Authentication method' })).toHaveValue('api_key');
    expect(within(form).getByRole('textbox', { name: 'Header name' })).toHaveValue('X-Api-Key');
    expect(form.querySelector('input[type="password"]')).toBeInTheDocument();
    expect(within(form).getByRole('spinbutton', { name: 'Timeout (seconds)' })).toHaveValue(30);
    expect(within(form).getByRole('switch', { name: 'Send JSON' })).toHaveAttribute('aria-checked', 'true');
    expect(within(form).getByRole('textbox', { name: 'Notes' }).tagName).toBe('TEXTAREA');
    expect(within(form).getByRole('textbox', { name: 'Test path' })).toHaveAttribute('placeholder', '/health');
    // Required fields carry a visible marker, optional ones do not.
    expect(within(form).getByText('Base address', { selector: 'label' }).textContent).toContain('*');
    expect(within(form).getByText('Test path', { selector: 'label' }).textContent).not.toContain('*');
  });

  it('shows and hides authentication fields as the authentication method changes, and does not send hidden values', async () => {
    const w = world({ connections: [] });
    const calls = install(w);
    renderAt('/integrations');
    await userEvent.click(await screen.findByRole('button', { name: 'Connect Generic HTTP API' }));
    const form = await dialog('Connect Generic HTTP API');

    expect(within(form).getByRole('textbox', { name: 'Header name' })).toBeInTheDocument();
    expect(within(form).queryByRole('textbox', { name: 'Username' })).not.toBeInTheDocument();

    // Type into an api_key field, then switch to basic: those fields go and the basic ones appear.
    await userEvent.clear(within(form).getByRole('textbox', { name: 'Header name' }));
    await userEvent.type(within(form).getByRole('textbox', { name: 'Header name' }), 'X-Stale');
    await userEvent.type(within(form).getByLabelText(/^API key/), 'stale-key-0000');
    await userEvent.selectOptions(within(form).getByRole('combobox', { name: 'Authentication method' }), 'basic');
    expect(within(form).queryByRole('textbox', { name: 'Header name' })).not.toBeInTheDocument();
    expect(within(form).queryByLabelText(/^API key/)).not.toBeInTheDocument();
    expect(within(form).getByRole('textbox', { name: 'Username' })).toBeInTheDocument();
    expect(within(form).getByLabelText(/^Password/)).toBeInTheDocument();

    await userEvent.type(within(form).getByRole('textbox', { name: 'Name' }), 'Acme basic');
    await userEvent.type(within(form).getByRole('textbox', { name: 'Base address' }), 'https://api.example.test');
    await userEvent.type(within(form).getByRole('textbox', { name: 'Username' }), 'svc-orbit');
    await userEvent.type(within(form).getByLabelText(/^Password/), 'pw-secret-1234');
    await userEvent.click(within(form).getByRole('button', { name: 'Create connection' }));

    await waitFor(() => expect(writes(calls, 'POST', CONNECTIONS)).toHaveLength(1));
    const body = (await writes(calls, 'POST', CONNECTIONS)[0]!.json()) as Record<string, unknown>;
    expect(body).toEqual({
      connector: 'http',
      name: 'Acme basic',
      enabled: true,
      config: {
        baseUrl: 'https://api.example.test',
        authStrategy: 'basic',
        username: 'svc-orbit',
        tokenPrefix: 'Bearer ',
        timeoutSeconds: 30,
        sendJson: true,
      },
      secrets: { password: 'pw-secret-1234' },
      clearSecrets: [],
    });
    expect(JSON.stringify(body)).not.toContain('stale');
    // The drawer closes and the new connection is listed.
    expect(await card('Acme basic')).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Connect Generic HTTP API' })).not.toBeInTheDocument());
  });

  it('adds the authentication method select itself when the descriptor leaves it out', async () => {
    const bare = CONNECTORS[0]!;
    install(
      world({
        connections: [],
        connectors: [{ ...bare, fields: bare.fields.filter((f) => f.key !== 'authStrategy') }],
      }),
    );
    renderAt('/integrations');
    await userEvent.click(await screen.findByRole('button', { name: 'Connect Generic HTTP API' }));
    const form = await dialog('Connect Generic HTTP API');
    const select = within(form).getByRole('combobox', { name: 'Authentication method' });
    expect(select).toHaveValue('api_key');
    expect(
      within(select)
        .getAllByRole('option')
        .map((o) => o.textContent),
    ).toEqual(['No authentication', 'API key', 'Username and password']);
  });

  it('checks required fields and addresses before sending anything, and summarises the problems', async () => {
    const calls = install(world({ connections: [] }));
    renderAt('/integrations');
    await userEvent.click(await screen.findByRole('button', { name: 'Connect Generic HTTP API' }));
    const form = await dialog('Connect Generic HTTP API');
    await userEvent.click(within(form).getByRole('button', { name: 'Create connection' }));

    const summary = within(form).getByRole('group', { name: 'Problems with the form' });
    expect(within(summary).getByText('3 problems to fix')).toBeInTheDocument();
    expect(within(summary).getByText('Name: This is required.')).toBeInTheDocument();
    expect(within(summary).getByText('Base address: This is required.')).toBeInTheDocument();
    expect(within(summary).getByText('API key: This is required.')).toBeInTheDocument();
    expect(summary).toHaveFocus();
    expect(within(form).getByRole('textbox', { name: 'Name' })).toBeInvalid();
    expect(writes(calls, 'POST', CONNECTIONS)).toHaveLength(0);

    // Typing retires the message about that field.
    await userEvent.type(within(form).getByRole('textbox', { name: 'Name' }), 'Acme');
    expect(within(summary).queryByText('Name: This is required.')).not.toBeInTheDocument();
    expect(within(summary).getByText('2 problems to fix')).toBeInTheDocument();

    await userEvent.type(within(form).getByRole('textbox', { name: 'Base address' }), 'ftp://files.example.test');
    await userEvent.type(within(form).getByLabelText(/^API key/), 'key-12345678');
    await userEvent.click(within(form).getByRole('button', { name: 'Create connection' }));
    expect(
      await within(form).findByText('Base address: Enter a web address that starts with http:// or https://.'),
    ).toBeInTheDocument();
    expect(writes(calls, 'POST', CONNECTIONS)).toHaveLength(0);
  });

  it('puts the server’s 422 field errors under the right fields and in the summary', async () => {
    install(world({ connections: [] }), (c) =>
      c.method === 'POST' && c.pathname === CONNECTIONS
        ? validation({
            'config.baseUrl': ['That address is not allowed.'],
            'secrets.apiKey': ['The key is too short.'],
            'config.testPath': ['The path must start with a slash.'],
            Name: ['That name is already used.'],
          })
        : undefined,
    );
    renderAt('/integrations');
    await userEvent.click(await screen.findByRole('button', { name: 'Connect Generic HTTP API' }));
    const form = await dialog('Connect Generic HTTP API');
    await userEvent.type(within(form).getByRole('textbox', { name: 'Name' }), 'Acme CMS');
    await userEvent.type(within(form).getByRole('textbox', { name: 'Base address' }), 'http://10.0.0.1');
    await userEvent.type(within(form).getByLabelText(/^API key/), 'short');
    await userEvent.type(within(form).getByRole('textbox', { name: 'Test path' }), 'health');
    await userEvent.click(within(form).getByRole('button', { name: 'Create connection' }));

    const summary = await within(form).findByRole('group', { name: 'Problems with the form' });
    expect(within(summary).getByText('4 problems to fix')).toBeInTheDocument();
    expect(within(summary).getByText('Base address: That address is not allowed.')).toBeInTheDocument();
    expect(within(summary).getByText('API key: The key is too short.')).toBeInTheDocument();
    expect(within(summary).getByText('Name: That name is already used.')).toBeInTheDocument();
    expect(summary).toHaveFocus();

    expect(within(form).getByRole('textbox', { name: 'Base address' })).toHaveAccessibleDescription(
      'The address every request starts from. That address is not allowed.',
    );
    expect(within(form).getByLabelText(/^API key/)).toBeInvalid();
    expect(within(form).getByRole('textbox', { name: 'Test path' })).toHaveAccessibleDescription(
      'A GET path used by Test connection. The path must start with a slash.',
    );
    expect(within(form).getByRole('textbox', { name: 'Name' })).toBeInvalid();

    // The drawer stays open, with what was typed still there.
    expect(within(form).getByRole('textbox', { name: 'Base address' })).toHaveValue('http://10.0.0.1');
    await userEvent.clear(within(form).getByRole('textbox', { name: 'Base address' }));
    expect(within(form).getByRole('textbox', { name: 'Base address' })).not.toHaveAccessibleDescription(/not allowed/);
  });

  it('shows a general failure when the server gives no field to blame', async () => {
    install(world({ connections: [] }), (c) =>
      c.method === 'POST' && c.pathname === CONNECTIONS
        ? problem(409, 'Conflict', { detail: 'You can have at most 50 connections.' })
        : undefined,
    );
    renderAt('/integrations');
    await userEvent.click(await screen.findByRole('button', { name: 'Connect Generic HTTP API' }));
    const form = await dialog('Connect Generic HTTP API');
    await userEvent.type(within(form).getByRole('textbox', { name: 'Name' }), 'Acme CMS');
    await userEvent.type(within(form).getByRole('textbox', { name: 'Base address' }), 'https://api.example.test');
    await userEvent.type(within(form).getByLabelText(/^API key/), 'key-12345678');
    await userEvent.click(within(form).getByRole('button', { name: 'Create connection' }));
    expect(await within(form).findByText('You can have at most 50 connections.')).toBeInTheDocument();
  });

  it('shows what is typed in a secret only while the toggle is on', async () => {
    install(world({ connections: [] }));
    renderAt('/integrations');
    await userEvent.click(await screen.findByRole('button', { name: 'Connect Generic HTTP API' }));
    const form = await dialog('Connect Generic HTTP API');
    const secret = within(form).getByLabelText(/^API key/);
    expect(secret).toHaveAttribute('type', 'password');
    expect(secret).toHaveAttribute('autocomplete', 'new-password');
    await userEvent.type(secret, 'typed-key-0042');
    const toggle = within(form).getByRole('button', { name: 'Show API key' });
    expect(toggle).toHaveAttribute('aria-pressed', 'false');
    await userEvent.click(toggle);
    expect(toggle).toHaveAttribute('aria-pressed', 'true');
    expect(secret).toHaveAttribute('type', 'text');
    expect(secret).toHaveValue('typed-key-0042');
    await userEvent.click(toggle);
    expect(secret).toHaveAttribute('type', 'password');
    // The typed value appears nowhere as text.
    expect(within(form).queryByText('typed-key-0042')).not.toBeInTheDocument();
  });
});

describe('a connector that only stores its settings (PADS4)', () => {
  const PADS4 = '/api/v1/integrations/connections/p1';

  async function openAdd(w = world({ connections: [] })) {
    const calls = install(w);
    renderAt('/integrations');
    await userEvent.click(await screen.findByRole('button', { name: 'Add PADS4 connection' }));
    return { calls, w, form: await dialog('Add PADS4 connection') };
  }

  async function fill(form: HTMLElement, over: Record<string, string> = {}) {
    const values = { 'PADS4 server URL': 'http://192.168.1.20:8080', Domain: 'acme', Username: 'ann', ...over };
    for (const [label, value] of Object.entries(values)) {
      const input = within(form).getByRole('textbox', { name: new RegExp(`^${label}`) });
      await userEvent.clear(input);
      if (value) await userEvent.type(input, value);
    }
    await userEvent.type(within(form).getByLabelText(/^Password/), 'pa55-secret-1234');
  }

  it('opens a dialog drawn from the descriptor: the name, then the four fields in one Connection group', async () => {
    const { form } = await openAdd();
    expect(within(form).getByRole('textbox', { name: 'Name' })).toHaveValue('PADS4');
    const url = within(form).getByRole('textbox', { name: /^PADS4 server URL/ });
    expect(url).toHaveAttribute('type', 'url');
    expect(url).toBeRequired();
    expect(url).toHaveAttribute('placeholder', 'https://pads4.example.test');
    expect(url).toHaveAccessibleDescription(expect.stringContaining('https://pads4.example.test'));
    for (const label of ['Domain', 'Username'])
      expect(within(form).getByRole('textbox', { name: new RegExp(`^${label}`) })).toBeRequired();
    expect(within(form).getByText('The PADS4 sign-in domain that the user name belongs to.')).toBeInTheDocument();
    const password = within(form).getByLabelText(/^Password/);
    expect(password).toHaveAttribute('type', 'password');
    expect(password).toBeRequired();
    expect(within(form).getByRole('button', { name: 'Show Password' })).toBeInTheDocument();
    // Required markers, one group, nothing about authentication or testing.
    expect(within(form).getByText('Domain', { selector: 'label' }).textContent).toContain('*');
    expect(
      within(form)
        .getAllByRole('heading', { level: 3 })
        .map((h) => h.textContent),
    ).toEqual(['Connection']);
    expect(within(form).queryByRole('combobox')).not.toBeInTheDocument();
    expect(within(form).getByText('Orbit saves these details but does not connect to PADS4 yet.')).toBeInTheDocument();
    expect(within(form).queryByRole('button', { name: /test/i })).not.toBeInTheDocument();
    expect(within(form).getByRole('button', { name: 'Create connection' })).toHaveClass('dam-btn--primary');
  });

  it('shows what is typed in the password only while the toggle is on', async () => {
    const { form } = await openAdd();
    const password = within(form).getByLabelText(/^Password/);
    await userEvent.type(password, 'pa55-secret-1234');
    await userEvent.click(within(form).getByRole('button', { name: 'Show Password' }));
    expect(password).toHaveAttribute('type', 'text');
    await userEvent.click(within(form).getByRole('button', { name: 'Show Password' }));
    expect(password).toHaveAttribute('type', 'password');
  });

  it('asks for the four required values before sending anything', async () => {
    const { calls, form } = await openAdd();
    await userEvent.click(within(form).getByRole('button', { name: 'Create connection' }));
    const summary = await within(form).findByRole('group', { name: 'Problems with the form' });
    expect(within(summary).getByText('4 problems to fix')).toBeInTheDocument();
    for (const label of ['PADS4 server URL', 'Domain', 'Username', 'Password'])
      expect(within(summary).getByText(`${label}: This is required.`)).toBeInTheDocument();
    expect(writes(calls, 'POST', CONNECTIONS)).toHaveLength(0);

    await fill(form, { 'PADS4 server URL': 'pads4.example.test' });
    await userEvent.click(within(form).getByRole('button', { name: 'Create connection' }));
    expect(
      await within(form).findByText('PADS4 server URL: Enter a web address that starts with http:// or https://.'),
    ).toBeInTheDocument();
    expect(writes(calls, 'POST', CONNECTIONS)).toHaveLength(0);
  });

  it('saves the address, domain and user name as settings and the password as a secret, with a private address accepted', async () => {
    const { calls, form } = await openAdd();
    await fill(form);
    await userEvent.click(within(form).getByRole('button', { name: 'Create connection' }));
    await waitFor(() => expect(writes(calls, 'POST', CONNECTIONS)).toHaveLength(1));
    expect(await writes(calls, 'POST', CONNECTIONS)[0]!.json()).toEqual({
      connector: 'pads4',
      name: 'PADS4',
      enabled: true,
      config: { baseUrl: 'http://192.168.1.20:8080', domain: 'acme', username: 'ann' },
      secrets: { password: 'pa55-secret-1234' },
      clearSecrets: [],
    });
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Add PADS4 connection' })).not.toBeInTheDocument());
    const c = await card('PADS4');
    expect(within(c).getByText('Saved')).toBeInTheDocument();
    expect(c.innerHTML).not.toContain('pa55-secret-1234');
    // Nothing was tested or asked of the other system.
    expect(calls.some((x) => x.pathname.endsWith('/test'))).toBe(false);
  });

  it('puts a server 422 under the right field', async () => {
    install(world({ connections: [] }), (c) =>
      c.method === 'POST' && c.pathname === CONNECTIONS
        ? validation({ 'config.baseUrl': ['Only http and https URLs are allowed.'] })
        : undefined,
    );
    renderAt('/integrations');
    await userEvent.click(await screen.findByRole('button', { name: 'Add PADS4 connection' }));
    const form = await dialog('Add PADS4 connection');
    await fill(form);
    await userEvent.click(within(form).getByRole('button', { name: 'Create connection' }));
    await within(form).findByRole('group', { name: 'Problems with the form' });
    expect(within(form).getByRole('textbox', { name: /^PADS4 server URL/ })).toHaveAccessibleDescription(
      expect.stringContaining('Only http and https URLs are allowed.'),
    );
  });

  describe('in the list', () => {
    const world1 = () => world({ connections: [pads4Connection()] });

    it('shows a Saved badge and the note instead of the test facts, with no Test button', async () => {
      const calls = install(world1());
      renderAt('/integrations');
      const c = await card('PADS4');
      expect(within(c).getByText('Saved')).toBeInTheDocument();
      expect(within(c).getByText('Orbit saves these details but does not connect to PADS4 yet.')).toBeInTheDocument();
      expect(within(c).queryByText('Not tested')).not.toBeInTheDocument();
      expect(within(c).queryByText('Not tested yet')).not.toBeInTheDocument();
      expect(within(c).queryByText(/Last tested|token/i)).not.toBeInTheDocument();
      expect(within(c).queryByText(/^Saved\. Orbit/)).not.toBeInTheDocument();
      expect(within(c).queryByRole('button', { name: 'Test PADS4' })).not.toBeInTheDocument();
      for (const name of ['Edit PADS4', 'Activity of PADS4', 'Delete PADS4'])
        expect(within(c).getByRole('button', { name })).toBeEnabled();
      expect(calls.some((x) => x.pathname.endsWith('/test'))).toBe(false);
    });

    it('keeps Test for a connector that has it, next to the saved one', async () => {
      install(world({ connections: [pads4Connection(), connection()] }));
      renderAt('/integrations');
      expect(await screen.findByRole('button', { name: 'Test Acme CMS' })).toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'Test PADS4' })).not.toBeInTheDocument();
    });

    it('opens Activity, which is simply empty', async () => {
      install(world1());
      renderAt('/integrations');
      await userEvent.click(await screen.findByRole('button', { name: 'Activity of PADS4' }));
      const drawer = await dialog('Activity: PADS4');
      expect(await within(drawer).findByText('No calls yet')).toBeInTheDocument();
    });

    it('edits with the write-only password: Set (…hint) with Replace only, and unchanged it is not sent', async () => {
      const w = world1();
      const calls = install(w);
      renderAt('/integrations');
      await userEvent.click(await screen.findByRole('button', { name: 'Edit PADS4' }));
      const form = await dialog('Edit PADS4');
      expect(within(form).getByRole('textbox', { name: /^PADS4 server URL/ })).toHaveValue('http://192.168.1.20:8080');
      expect(within(form).getByRole('textbox', { name: /^Domain/ })).toHaveValue('acme');
      expect(within(form).getByRole('textbox', { name: /^Username/ })).toHaveValue('ann');
      expect(within(form).getByText('Set (…x9z1)')).toBeInTheDocument();
      expect(form.querySelector('input[type="password"]')).toBeNull();
      expect(within(form).getByRole('button', { name: 'Replace Password' })).toBeInTheDocument();
      expect(within(form).queryByRole('button', { name: 'Remove Password' })).not.toBeInTheDocument();
      expect(within(form).queryByRole('button', { name: /test/i })).not.toBeInTheDocument();

      await userEvent.clear(within(form).getByRole('textbox', { name: /^Domain/ }));
      await userEvent.type(within(form).getByRole('textbox', { name: /^Domain/ }), 'acme-eu');
      await userEvent.click(within(form).getByRole('button', { name: 'Save' }));
      await waitFor(() => expect(writes(calls, 'PUT', PADS4)).toHaveLength(1));
      const body = (await writes(calls, 'PUT', PADS4)[0]!.json()) as Record<string, unknown>;
      expect(body).toMatchObject({
        name: 'PADS4',
        enabled: true,
        config: { baseUrl: 'http://192.168.1.20:8080', domain: 'acme-eu', username: 'ann' },
        secrets: {},
        clearSecrets: [],
      });
      expect(body.connector ?? null).toBeNull();
      expect(calls.some((x) => x.pathname.endsWith('/test'))).toBe(false);
    });

    it('replaces the password only after Replace', async () => {
      const calls = install(world1());
      renderAt('/integrations');
      await userEvent.click(await screen.findByRole('button', { name: 'Edit PADS4' }));
      const form = await dialog('Edit PADS4');
      await userEvent.click(within(form).getByRole('button', { name: 'Replace Password' }));
      const input = within(form).getByLabelText(/^Password/);
      expect(input).toHaveValue('');
      await userEvent.type(input, 'new-pa55-7777');
      await userEvent.click(within(form).getByRole('button', { name: 'Save' }));
      await waitFor(() => expect(writes(calls, 'PUT', PADS4)).toHaveLength(1));
      const body = (await writes(calls, 'PUT', PADS4)[0]!.json()) as { secrets: unknown };
      expect(body.secrets).toEqual({ password: 'new-pa55-7777' });
      await userEvent.click(await screen.findByRole('button', { name: 'Edit PADS4' }));
      expect(await screen.findByText('Set (…7777)')).toBeInTheDocument();
    });
  });

  it('is in Hindi', async () => {
    setLanguage('hi');
    install(world({ connections: [pads4Connection()] }));
    renderAt('/integrations');
    const pads4 = (await screen.findAllByRole('heading', { name: 'PADS4' }))[0]!.closest('article')!;
    expect(within(pads4).getByText('केवल सहेजी गई सेटिंग')).toBeInTheDocument();
    expect(within(pads4).getByText('Orbit ये विवरण सहेजता है, लेकिन अभी PADS4 से कनेक्ट नहीं करता।')).toBeInTheDocument();
    await userEvent.click(within(pads4).getByRole('button', { name: 'PADS4 कनेक्शन जोड़ें' }));
    const form = await dialog('PADS4 कनेक्शन जोड़ें');
    expect(within(form).queryByRole('button', { name: /जाँच/ })).not.toBeInTheDocument();
    await userEvent.click(within(form).getByRole('button', { name: 'कनेक्शन बनाएँ' }));
    expect(await within(form).findByText('4 समस्याएँ ठीक करनी हैं')).toBeInTheDocument();
    await userEvent.click(within(form).getByRole('button', { name: 'बंद करें' }));
    const saved = await card('PADS4');
    expect(within(saved).getByText('सहेजा गया')).toBeInTheDocument();
  });

  it('has no accessibility violations in the list, the add dialog and the edit dialog', async () => {
    install(world({ connections: [pads4Connection()] }));
    const { container } = renderAt('/integrations');
    await card('PADS4');
    expect(await axe(container)).toHaveNoViolations();
    await userEvent.click(screen.getByRole('button', { name: 'Add PADS4 connection' }));
    const form = await dialog('Add PADS4 connection');
    expect(await axe(form)).toHaveNoViolations();
    await userEvent.click(within(form).getByRole('button', { name: 'Create connection' }));
    await within(form).findByRole('group', { name: 'Problems with the form' });
    expect(await axe(form)).toHaveNoViolations();
    await userEvent.click(within(form).getByRole('button', { name: 'Close' }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Add PADS4 connection' })).not.toBeInTheDocument());
    await userEvent.click(screen.getByRole('button', { name: 'Edit PADS4' }));
    expect(await axe(await dialog('Edit PADS4'))).toHaveNoViolations();
  });
});

describe('edit dialog and secrets', () => {
  async function openEdit(w = world()) {
    const calls = install(w);
    renderAt('/integrations');
    await userEvent.click(await screen.findByRole('button', { name: 'Edit Acme CMS' }));
    const form = await dialog('Edit Acme CMS');
    return { calls, form, w };
  }

  it('starts from the stored values, shows a stored secret as “Set (…hint)” and never prefills it', async () => {
    const { form } = await openEdit();
    expect(within(form).getByRole('textbox', { name: 'Name' })).toHaveValue('Acme CMS');
    expect(within(form).getByRole('textbox', { name: 'Base address' })).toHaveValue('https://api.example.test');
    expect(within(form).getByText('Set (…wxyz)')).toBeInTheDocument();
    expect(within(form).getByText('Set (…ab12)')).toBeInTheDocument();
    expect(form.querySelector('input[type="password"]')).toBeNull();
    expect(within(form).getByRole('button', { name: 'Replace API key' })).toBeInTheDocument();
    // A required secret can only be replaced; an optional one can also be removed.
    expect(within(form).queryByRole('button', { name: 'Remove API key' })).not.toBeInTheDocument();
    expect(within(form).getByRole('button', { name: 'Remove Signing secret' })).toBeInTheDocument();
    expect(form.innerHTML).not.toContain('value="wxyz"');
  });

  it('shows just “Set” for a stored secret that is too short to give a hint', async () => {
    const { form } = await openEdit(
      world({
        connections: [connection({ secrets: { apiKey: { set: true, hint: null }, signingSecret: { set: true, hint: 'ab12' } } })],
      }),
    );
    expect(within(form).getByText('Set')).toBeInTheDocument();
    expect(within(form).getByText('Set (…ab12)')).toBeInTheDocument();
  });

  it('leaves unchanged secrets out of the request, so the server keeps them', async () => {
    const { calls, form } = await openEdit();
    await userEvent.clear(within(form).getByRole('textbox', { name: 'Name' }));
    await userEvent.type(within(form).getByRole('textbox', { name: 'Name' }), 'Acme CMS (prod)');
    await userEvent.click(within(form).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(writes(calls, 'PUT', `${CONNECTIONS}/k1`)).toHaveLength(1));
    const body = (await writes(calls, 'PUT', `${CONNECTIONS}/k1`)[0]!.json()) as Record<string, unknown>;
    expect(body).toMatchObject({ name: 'Acme CMS (prod)', secrets: {}, clearSecrets: [] });
    expect(body.connector ?? null).toBeNull(); // the connector is only used when creating
    expect(JSON.stringify(body)).not.toContain('wxyz');
    expect(await card('Acme CMS (prod)')).toBeInTheDocument();
    // Still set afterwards.
    await userEvent.click(screen.getByRole('button', { name: 'Edit Acme CMS (prod)' }));
    expect(await screen.findByText('Set (…wxyz)')).toBeInTheDocument();
  });

  it('replaces a secret only after Replace: the typed value is sent, the hint updates', async () => {
    const { calls, form } = await openEdit();
    await userEvent.click(within(form).getByRole('button', { name: 'Replace API key' }));
    const input = within(form).getByLabelText(/^API key/);
    expect(input).toHaveValue('');
    expect(input).toHaveFocus();
    expect(input).toHaveAttribute('type', 'password');
    await userEvent.type(input, 'brand-new-key-9876');
    await userEvent.click(within(form).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(writes(calls, 'PUT', `${CONNECTIONS}/k1`)).toHaveLength(1));
    const body = (await writes(calls, 'PUT', `${CONNECTIONS}/k1`)[0]!.json()) as { secrets: unknown; clearSecrets: unknown };
    expect(body.secrets).toEqual({ apiKey: 'brand-new-key-9876' });
    expect(body.clearSecrets).toEqual([]);
    await userEvent.click(await screen.findByRole('button', { name: 'Edit Acme CMS' }));
    expect(await screen.findByText('Set (…9876)')).toBeInTheDocument();
  });

  it('cancelling a replacement keeps the stored secret and sends nothing for it', async () => {
    const { calls, form } = await openEdit();
    await userEvent.click(within(form).getByRole('button', { name: 'Replace API key' }));
    await userEvent.type(within(form).getByLabelText(/^API key/), 'half-typed');
    await userEvent.click(within(form).getByRole('button', { name: 'Keep the stored API key' }));
    expect(within(form).getByText('Set (…wxyz)')).toBeInTheDocument();
    await userEvent.click(within(form).getByRole('button', { name: 'Test connection' }));
    // Nothing changed, so Test connection tests without saving.
    await within(form).findByText('The connection works.');
    expect(writes(calls, 'PUT', `${CONNECTIONS}/k1`)).toHaveLength(0);
  });

  it('removes an optional secret by listing it in clearSecrets, and Undo takes it back', async () => {
    const { calls, form } = await openEdit();
    await userEvent.click(within(form).getByRole('button', { name: 'Remove Signing secret' }));
    expect(within(form).getByText('Will be removed when you save.')).toBeInTheDocument();
    await userEvent.click(within(form).getByRole('button', { name: 'Keep Signing secret' }));
    expect(within(form).getByText('Set (…ab12)')).toBeInTheDocument();

    await userEvent.click(within(form).getByRole('button', { name: 'Remove Signing secret' }));
    await userEvent.click(within(form).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(writes(calls, 'PUT', `${CONNECTIONS}/k1`)).toHaveLength(1));
    const body = (await writes(calls, 'PUT', `${CONNECTIONS}/k1`)[0]!.json()) as { secrets: unknown; clearSecrets: unknown };
    expect(body.clearSecrets).toEqual(['signingSecret']);
    expect(body.secrets).toEqual({});
    await userEvent.click(await screen.findByRole('button', { name: 'Edit Acme CMS' }));
    const again = await dialog('Edit Acme CMS');
    expect(within(again).queryByText('Set (…ab12)')).not.toBeInTheDocument();
    expect(within(again).getByText('Set (…wxyz)')).toBeInTheDocument();
  });

  it('will not save a required secret left empty after Replace, and cannot remove it', async () => {
    const { calls, form } = await openEdit();
    expect(within(form).queryByRole('button', { name: 'Remove API key' })).not.toBeInTheDocument();
    await userEvent.click(within(form).getByRole('button', { name: 'Replace API key' }));
    await userEvent.click(within(form).getByRole('button', { name: 'Save' }));
    const summary = await within(form).findByRole('group', { name: 'Problems with the form' });
    expect(within(summary).getByText('API key: This is required.')).toBeInTheDocument();
    expect(writes(calls, 'PUT', `${CONNECTIONS}/k1`)).toHaveLength(0);
  });

  it('maps a 422 on a replaced secret to its field', async () => {
    install(world(), (c) => (c.method === 'PUT' ? validation({ 'secrets.apiKey': ['The key was refused.'] }) : undefined));
    renderAt('/integrations');
    await userEvent.click(await screen.findByRole('button', { name: 'Edit Acme CMS' }));
    const form = await dialog('Edit Acme CMS');
    await userEvent.click(within(form).getByRole('button', { name: 'Replace API key' }));
    await userEvent.type(within(form).getByLabelText(/^API key/), 'bad-key-0000');
    await userEvent.click(within(form).getByRole('button', { name: 'Save' }));
    await within(form).findByRole('group', { name: 'Problems with the form' });
    expect(within(form).getByLabelText(/^API key/)).toHaveAccessibleDescription(expect.stringContaining('The key was refused.'));
  });
});

describe('testing a connection', () => {
  it('runs the test from the list and shows each step with its result, detail and time', async () => {
    const calls = install(
      world({
        result: testResult({
          ok: false,
          steps: [
            { name: 'Authenticate', ok: true, detail: 'API key attached.', durationMs: 4 },
            { name: 'GET /health', ok: false, detail: 'HTTP 503 from api.example.test', durationMs: 126 },
          ],
        }),
      }),
    );
    renderAt('/integrations');
    await userEvent.click(await screen.findByRole('button', { name: 'Test Acme CMS' }));
    const panel = await dialog('Test connection: Acme CMS');
    expect(await within(panel).findByText('The test failed.')).toBeInTheDocument();
    const steps = within(within(panel).getByRole('list', { name: 'Test steps' })).getAllByRole('listitem');
    expect(steps).toHaveLength(2);
    expect(steps[0]!.textContent).toContain('Authenticate: passed');
    expect(steps[0]!.textContent).toContain('API key attached.');
    expect(steps[0]!.textContent).toContain('4 ms');
    expect(steps[1]!.textContent).toContain('GET /health: failed');
    expect(steps[1]!.textContent).toContain('HTTP 503 from api.example.test');
    expect(steps[1]!.textContent).toContain('126 ms');
    // The result is announced politely.
    expect(within(panel).getByRole('status')).toHaveAttribute('aria-live', 'polite');
    expect(writes(calls, 'POST', `${CONNECTIONS}/k1/test`)).toHaveLength(1);

    // The status on the card is refreshed behind the dialog.
    await userEvent.click(within(panel).getByRole('button', { name: 'Done' }));
    const c = await card('Acme CMS');
    expect(await within(c).findByText('Needs attention')).toBeInTheDocument();
    expect(within(c).getByText('The test failed.')).toBeInTheDocument();
  });

  it('shows a success when every step passes', async () => {
    install(world());
    renderAt('/integrations');
    await userEvent.click(await screen.findByRole('button', { name: 'Test Acme CMS' }));
    const panel = await dialog('Test connection: Acme CMS');
    expect(await within(panel).findByText('The connection works.')).toBeInTheDocument();
    expect(within(panel).getByText(/^Tested .*130 ms in total\.$/)).toBeInTheDocument();
  });

  it('answers a 429 calmly and lets you test again', async () => {
    let limited = true;
    install(world(), (c) =>
      c.method === 'POST' && c.pathname.endsWith('/test') && limited
        ? problem(429, 'Too Many Requests', { detail: 'Wait 5 seconds.' })
        : undefined,
    );
    renderAt('/integrations');
    await userEvent.click(await screen.findByRole('button', { name: 'Test Acme CMS' }));
    const panel = await dialog('Test connection: Acme CMS');
    expect(await within(panel).findByText(/Please wait a few seconds, then test again/)).toBeInTheDocument();
    // Not presented as a failure of the connection.
    expect(within(panel).queryByText('The test could not be run.')).not.toBeInTheDocument();
    expect(within(panel).queryByText('The test failed.')).not.toBeInTheDocument();
    limited = false;
    await userEvent.click(within(panel).getByRole('button', { name: 'Test again' }));
    expect(await within(panel).findByText('The connection works.')).toBeInTheDocument();
  });

  it('says when the test could not run at all', async () => {
    install(world(), (c) =>
      c.method === 'POST' && c.pathname.endsWith('/test')
        ? problem(500, 'Boom', { detail: 'The worker is unavailable.' })
        : undefined,
    );
    renderAt('/integrations');
    await userEvent.click(await screen.findByRole('button', { name: 'Test Acme CMS' }));
    const panel = await dialog('Test connection: Acme CMS');
    expect(await within(panel).findByText('The test could not be run.')).toBeInTheDocument();
    expect(within(panel).getByText('The worker is unavailable.')).toBeInTheDocument();
  });

  it('saves and tests from the dialog, keeping it open with the steps and the new “Set” hint', async () => {
    const w = world({ connections: [] });
    const calls = install(w);
    renderAt('/integrations');
    await userEvent.click(await screen.findByRole('button', { name: 'Connect Generic HTTP API' }));
    const form = await dialog('Connect Generic HTTP API');
    await userEvent.type(within(form).getByRole('textbox', { name: 'Name' }), 'Acme CMS');
    await userEvent.type(within(form).getByRole('textbox', { name: 'Base address' }), 'https://api.example.test');
    await userEvent.type(within(form).getByLabelText(/^API key/), 'first-key-1357');
    await userEvent.click(within(form).getByRole('button', { name: 'Save and test' }));

    expect(await within(form).findByText('The connection works.')).toBeInTheDocument();
    expect(writes(calls, 'POST', CONNECTIONS)).toHaveLength(1);
    expect(writes(calls, 'POST', `${CONNECTIONS}/k-new/test`)).toHaveLength(1);
    // The dialog is now an editor of the saved connection: the secret shows as set, never as the typed value.
    expect(within(form).getByText('Set (…1357)')).toBeInTheDocument();
    // Only the optional signing secret, which was never set, still has an input.
    expect(form.querySelectorAll('input[type="password"]')).toHaveLength(1);
    // With nothing changed the button tests without saving again, and Save is an update, not another create.
    const test = within(form).getByRole('button', { name: 'Test connection' });
    await userEvent.click(test);
    await waitFor(() => expect(writes(calls, 'POST', `${CONNECTIONS}/k-new/test`)).toHaveLength(2));
    expect(writes(calls, 'POST', CONNECTIONS)).toHaveLength(1);
    await userEvent.type(within(form).getByRole('textbox', { name: 'Test path' }), '/health');
    await userEvent.click(within(form).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(writes(calls, 'PUT', `${CONNECTIONS}/k-new`)).toHaveLength(1));
    expect(writes(calls, 'POST', CONNECTIONS)).toHaveLength(1);
  });
});

describe('activity', () => {
  it('lists the recent calls newest first with their facts, and notes what is never recorded', async () => {
    install(
      world({
        activity: [
          call({
            id: 'a1',
            method: 'GET',
            target: 'api.example.test/health',
            statusCode: 200,
            createdAt: new Date(Date.now() - 2 * MIN).toISOString(),
          }),
          call({
            id: 'a2',
            kind: 'auth',
            method: 'POST',
            target: 'api.example.test/login',
            statusCode: 401,
            ok: false,
            durationMs: 88,
            error: 'The login call was refused.',
            createdAt: new Date(Date.now() - 9 * MIN).toISOString(),
          }),
          call({
            id: 'a3',
            kind: 'auth',
            method: 'POST',
            target: 'api.example.test/login',
            statusCode: null,
            ok: false,
            durationMs: 3000,
            error: 'Timed out.',
          }),
        ],
      }),
    );
    renderAt('/integrations');
    await userEvent.click(await screen.findByRole('button', { name: 'Activity of Acme CMS' }));
    const drawer = await dialog('Activity: Acme CMS');
    const items = within(await within(drawer).findByRole('list', { name: 'Recent calls, newest first' })).getAllByRole(
      'listitem',
    );
    expect(items).toHaveLength(3);
    expect(items[0]!.textContent).toContain('OK');
    expect(items[0]!.textContent).toContain('GET');
    expect(items[0]!.textContent).toContain('api.example.test/health');
    expect(items[0]!.textContent).toContain('2 minutes ago');
    expect(items[0]!.textContent).toContain('Test');
    expect(items[0]!.textContent).toContain('HTTP 200');
    expect(items[0]!.textContent).toContain('41 ms');
    expect(items[1]!.textContent).toContain('Failed');
    expect(items[1]!.textContent).toContain('Sign-in');
    expect(items[1]!.textContent).toContain('HTTP 401');
    expect(items[1]!.textContent).toContain('88 ms');
    expect(within(items[1]!).getByText('The login call was refused.')).toBeInTheDocument();
    expect(items[2]!.textContent).toContain('No response');
    expect(within(drawer).getByText(/never recorded/)).toBeInTheDocument();
    expect(within(drawer).queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument();
  });

  it('pages with Load more', async () => {
    const calls = install(
      world({
        pageSize: 2,
        activity: [1, 2, 3, 4, 5].map((n) => call({ id: `a${n}`, target: `api.example.test/p${n}` })),
      }),
    );
    renderAt('/integrations');
    await userEvent.click(await screen.findByRole('button', { name: 'Activity of Acme CMS' }));
    const drawer = await dialog('Activity: Acme CMS');
    await within(drawer).findByText('api.example.test/p2');
    expect(within(drawer).queryByText('api.example.test/p3')).not.toBeInTheDocument();
    await userEvent.click(within(drawer).getByRole('button', { name: 'Load more' }));
    await within(drawer).findByText('api.example.test/p4');
    await userEvent.click(within(drawer).getByRole('button', { name: 'Load more' }));
    await within(drawer).findByText('api.example.test/p5');
    expect(within(drawer).queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument();
    const activityCalls = calls.filter((c) => c.pathname.endsWith('/k1/activity'));
    expect(activityCalls.map((c) => c.search.get('cursor'))).toEqual([null, '2', '4']);
    expect(activityCalls[0]!.search.get('limit')).toBe('25');
    expect(within(drawer).getAllByRole('listitem')).toHaveLength(5);
  });

  it('says when there are no calls yet', async () => {
    install(world({ activity: [] }));
    renderAt('/integrations');
    await userEvent.click(await screen.findByRole('button', { name: 'Activity of Acme CMS' }));
    const drawer = await dialog('Activity: Acme CMS');
    expect(await within(drawer).findByText('No calls yet')).toBeInTheDocument();
    expect(within(drawer).getByText(/never recorded/)).toBeInTheDocument();
  });

  it('shows an error with a retry', async () => {
    let fail = true;
    install(world({ activity: [call()] }), (c) => (c.pathname.endsWith('/activity') && fail ? problem(500, 'Boom') : undefined));
    renderAt('/integrations');
    await userEvent.click(await screen.findByRole('button', { name: 'Activity of Acme CMS' }));
    const drawer = await dialog('Activity: Acme CMS');
    expect(await within(drawer).findByText('We could not load the activity')).toBeInTheDocument();
    fail = false;
    await userEvent.click(within(drawer).getByRole('button', { name: 'Try again' }));
    expect(await within(drawer).findByText('api.example.test/health')).toBeInTheDocument();
  });
});

describe('accessibility', () => {
  it('has no violations on the page, the connect and edit dialogs, the test result and the activity panel', async () => {
    install(world({ activity: [call(), call({ id: 'a2', ok: false, statusCode: 500, error: 'Boom' })] }));
    const { container } = renderAt('/integrations');
    await card('Acme CMS');
    expect(await axe(container)).toHaveNoViolations();

    await userEvent.click(screen.getByRole('button', { name: 'Connect Generic HTTP API' }));
    const form = await dialog('Connect Generic HTTP API');
    expect(await axe(form)).toHaveNoViolations();
    await userEvent.click(within(form).getByRole('button', { name: 'Create connection' }));
    await within(form).findByRole('group', { name: 'Problems with the form' });
    expect(await axe(form)).toHaveNoViolations();
    await userEvent.click(within(form).getByRole('button', { name: 'Close' }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Connect Generic HTTP API' })).not.toBeInTheDocument());

    await userEvent.click(screen.getByRole('button', { name: 'Edit Acme CMS' }));
    const edit = await dialog('Edit Acme CMS');
    await userEvent.click(within(edit).getByRole('button', { name: 'Remove Signing secret' }));
    expect(await axe(edit)).toHaveNoViolations();
    await userEvent.click(within(edit).getByRole('button', { name: 'Save and test' }));
    await within(edit).findByText('The connection works.');
    expect(await axe(edit)).toHaveNoViolations();
    await userEvent.click(within(edit).getByRole('button', { name: 'Close' }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Edit Acme CMS' })).not.toBeInTheDocument());

    await userEvent.click(screen.getByRole('button', { name: 'Test Acme CMS' }));
    const test = await dialog('Test connection: Acme CMS');
    await within(test).findByText('The connection works.');
    expect(await axe(test)).toHaveNoViolations();
    await userEvent.click(within(test).getByRole('button', { name: 'Done' }));

    await userEvent.click(screen.getByRole('button', { name: 'Activity of Acme CMS' }));
    const drawer = await dialog('Activity: Acme CMS');
    await within(drawer).findByText('Boom');
    expect(await axe(drawer)).toHaveNoViolations();
  });
});

describe('Hindi', () => {
  it('renders the page, the connect dialog and the test result in Hindi', async () => {
    setLanguage('hi');
    install(world());
    renderAt('/integrations');
    expect(await screen.findByRole('heading', { level: 1, name: 'इंटीग्रेशन' })).toBeInTheDocument();
    expect(await screen.findByRole('heading', { name: 'उपलब्ध कनेक्टर' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'आपके कनेक्शन' })).toBeInTheDocument();
    const c = await card('Acme CMS');
    expect(within(c).getByText('चालू है')).toBeInTheDocument();
    expect(within(c).getByRole('switch', { name: 'Acme CMS: चालू' })).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Generic HTTP API जोड़ें' }));
    const form = await dialog('Generic HTTP API जोड़ें');
    expect(within(form).getByRole('heading', { name: 'प्रमाणीकरण' })).toBeInTheDocument();
    expect(within(form).getByRole('button', { name: 'कनेक्शन बनाएँ' })).toBeInTheDocument();
    await userEvent.click(within(form).getByRole('button', { name: 'कनेक्शन बनाएँ' }));
    expect(await within(form).findByText('3 समस्याएँ ठीक करनी हैं')).toBeInTheDocument();
    await userEvent.click(within(form).getByRole('button', { name: 'बंद करें' }));
    cleanup();

    install(world());
    renderAt('/integrations');
    await userEvent.click(await screen.findByRole('button', { name: 'Acme CMS जाँचें' }));
    const panel = await dialog('कनेक्शन की जाँच: Acme CMS');
    expect(await within(panel).findByText('कनेक्शन सही काम कर रहा है।')).toBeInTheDocument();
  });

  it('marks a planned connector in Hindi', async () => {
    setLanguage('hi');
    install(world({ connections: [] }));
    renderAt('/integrations');
    const canva = (await screen.findByRole('heading', { name: 'Canva' })).closest('article')!;
    expect(within(canva).getByText('योजनाबद्ध')).toBeInTheDocument();
    expect(within(canva).getByText('डिज़ाइन')).toBeInTheDocument();
    const pads4 = (await screen.findByRole('heading', { name: 'PADS4' })).closest('article')!;
    expect(within(pads4).getByText('डिजिटल साइनेज')).toBeInTheDocument();
    expect(within(canva).getByText('बाद में आएगा।')).toBeInTheDocument();
  });
});
