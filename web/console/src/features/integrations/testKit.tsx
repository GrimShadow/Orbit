// Test-only scaffolding for the Integrations screens: fixtures, a small in-memory API behind a fetch mock, and the console
// rendered at a route. Nothing in the app imports this. Names and hosts are fictional (Acme, example.test).
import { render } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { App, queryClient } from '../../App';
import { AuthContext } from '../../auth/AuthProvider';
import { json, mockApi, signedIn, type Call } from '../workflow/workflowTestKit';
import type { ActivityDto, ConnectionDto, ConnectionInput, ConnectorDto, FieldDto, TestResultDto } from './integrationsTypes';

export { json, type Call };

export const MIN = 60_000;
export const ago = (ms: number) => new Date(Date.now() - ms).toISOString();
export const ahead = (ms: number) => new Date(Date.now() + ms).toISOString();

const strategy = (equals: string[]) => ({ field: 'authStrategy', equals });

export const HTTP_FIELDS: FieldDto[] = [
  {
    key: 'baseUrl',
    label: 'Base address',
    type: 'url',
    required: true,
    help: 'The address every request starts from.',
    placeholder: 'https://api.example.test',
    group: 'connection',
  },
  {
    key: 'authStrategy',
    label: 'Authentication method',
    type: 'select',
    required: true,
    group: 'authentication',
    options: [
      { value: 'none', label: 'No authentication' },
      { value: 'api_key', label: 'API key' },
      { value: 'basic', label: 'Username and password' },
    ],
    default: 'api_key',
  },
  {
    key: 'apiKeyHeader',
    label: 'Header name',
    type: 'text',
    required: false,
    group: 'authentication',
    default: 'X-Api-Key',
    showWhen: strategy(['api_key']),
  },
  {
    key: 'apiKey',
    label: 'API key',
    type: 'secret',
    required: true,
    help: 'Sent with every request.',
    group: 'authentication',
    showWhen: strategy(['api_key']),
  },
  {
    key: 'username',
    label: 'Username',
    type: 'text',
    required: true,
    group: 'authentication',
    showWhen: strategy(['basic']),
  },
  {
    key: 'password',
    label: 'Password',
    type: 'secret',
    required: true,
    group: 'authentication',
    showWhen: strategy(['basic']),
  },
  {
    key: 'testPath',
    label: 'Test path',
    type: 'text',
    required: false,
    help: 'A GET path used by Test connection.',
    placeholder: '/health',
    group: 'advanced',
  },
  {
    key: 'apiKeyIn',
    label: 'Send the key in',
    type: 'select',
    required: false,
    group: 'authentication',
    options: [
      { value: 'header', label: 'A header' },
      { value: 'query', label: 'A query parameter' },
    ],
    default: 'header',
    showWhen: strategy(['api_key']),
  },
  { key: 'tokenPrefix', label: 'Token prefix', type: 'text', required: false, group: 'advanced', default: 'Bearer ' },
  { key: 'signingSecret', label: 'Signing secret', type: 'secret', required: false, group: 'advanced' },
  { key: 'timeoutSeconds', label: 'Timeout (seconds)', type: 'number', required: false, group: 'advanced', default: 30 },
  { key: 'sendJson', label: 'Send JSON', type: 'boolean', required: false, group: 'advanced', default: true },
  { key: 'notes', label: 'Notes', type: 'textarea', required: false, group: 'advanced' },
];

/** The fields of the shipped PADS4 connector, as the server lists them. */
export const PADS4_FIELDS: FieldDto[] = [
  {
    key: 'baseUrl',
    label: 'PADS4 server URL',
    type: 'url',
    required: true,
    help: 'The address of your PADS4 server, for example https://pads4.example.test.',
    placeholder: 'https://pads4.example.test',
    group: 'connection',
  },
  {
    key: 'domain',
    label: 'Domain',
    type: 'text',
    required: true,
    help: 'The PADS4 sign-in domain that the user name belongs to.',
    group: 'connection',
  },
  { key: 'username', label: 'Username', type: 'text', required: true, group: 'connection' },
  { key: 'password', label: 'Password', type: 'secret', required: true, group: 'connection' },
];

export const connector = (over: Partial<ConnectorDto> = {}): ConnectorDto => ({
  key: 'http',
  name: 'Generic HTTP API',
  description: 'Call any HTTP API with the authentication it needs.',
  category: 'General',
  status: 'available',
  authStrategies: ['none', 'api_key', 'basic'],
  defaultAuthStrategy: 'api_key',
  capabilities: ['test'],
  fields: HTTP_FIELDS,
  ...over,
});

export const CONNECTORS: ConnectorDto[] = [
  connector(),
  // Mirrors the server's PADS4 descriptor: four fields, no authentication strategy, no capabilities (it only stores the settings).
  connector({
    key: 'pads4',
    name: 'PADS4',
    description: 'Save the details of a PADS4 server. Orbit stores them but does not connect to PADS4 yet.',
    category: 'Digital signage',
    status: 'preview',
    authStrategies: [],
    defaultAuthStrategy: null,
    capabilities: [],
    fields: PADS4_FIELDS,
  }),
  connector({
    key: 'canva',
    name: 'Canva',
    description: 'Bring Orbit assets into Canva designs.',
    category: 'Design',
    status: 'planned',
    fields: [],
    authStrategies: [],
    defaultAuthStrategy: null,
  }),
];

export const connection = (over: Partial<ConnectionDto> = {}): ConnectionDto => ({
  id: 'k1',
  connector: 'http',
  name: 'Acme CMS',
  enabled: true,
  config: { baseUrl: 'https://api.example.test', authStrategy: 'api_key', apiKeyHeader: 'X-Api-Key', timeoutSeconds: 30 },
  secrets: { apiKey: { set: true, hint: 'wxyz' }, signingSecret: { set: true, hint: 'ab12' } },
  status: 'ok',
  statusDetail: 'Signed in and reached /health.',
  lastTestedAt: ago(5 * MIN),
  lastOkAt: ago(5 * MIN),
  tokenExpiresAt: ahead(3 * 3_600_000 + 30_000),
  createdAt: ago(86_400_000),
  updatedAt: ago(86_400_000),
  ...over,
});

/** A saved PADS4 connection: the server keeps status `new` and says it only saved the settings. */
export const pads4Connection = (over: Partial<ConnectionDto> = {}): ConnectionDto => ({
  id: 'p1',
  connector: 'pads4',
  name: 'PADS4',
  enabled: true,
  config: { baseUrl: 'http://192.168.1.20:8080', domain: 'acme', username: 'ann' },
  secrets: { password: { set: true, hint: 'x9z1' } },
  status: 'new',
  statusDetail: 'Saved. Orbit does not connect to PADS4 yet.',
  lastTestedAt: null,
  lastOkAt: null,
  tokenExpiresAt: null,
  createdAt: ago(86_400_000),
  updatedAt: ago(86_400_000),
  ...over,
});

export const call = (over: Partial<ActivityDto> = {}): ActivityDto => ({
  id: 'a1',
  kind: 'test',
  method: 'GET',
  target: 'api.example.test/health',
  statusCode: 200,
  ok: true,
  durationMs: 41,
  error: null,
  createdAt: ago(2 * MIN),
  ...over,
});

export const testResult = (over: Partial<TestResultDto> = {}): TestResultDto => ({
  ok: true,
  durationMs: 130,
  testedAt: new Date().toISOString(),
  steps: [
    { name: 'Authenticate', ok: true, detail: 'API key attached.', durationMs: 4 },
    { name: 'GET /health', ok: true, detail: 'HTTP 200', durationMs: 126 },
  ],
  ...over,
});

export interface World {
  connectors: ConnectorDto[];
  connections: ConnectionDto[];
  activity: ActivityDto[];
  permissions: string[];
  /** Calls per activity page; the rest are reached with the cursor. */
  pageSize: number;
  result: TestResultDto;
}

export const ADMIN = ['integrations.manage'];

export function world(over: Partial<World> = {}): World {
  return {
    connectors: CONNECTORS,
    connections: [connection()],
    activity: [],
    permissions: ADMIN,
    pageSize: 25,
    result: testResult(),
    ...over,
  };
}

export const problem = (status: number, title: string, extra: Record<string, unknown> = {}) =>
  json({ title, status, ...extra }, status);
export const validation = (errors: Record<string, string[]>) =>
  problem(422, 'One or more validation errors occurred.', { errors });

/** Applies a create/update body to a connection the way the server does: config replaced, a secret sent is stored (hint =
 * its last four characters), one left out is kept, `clearSecrets` removes. */
export function applyInput(base: ConnectionDto, b: ConnectionInput): ConnectionDto {
  const secrets = { ...base.secrets };
  for (const [k, v] of Object.entries(b.secrets)) secrets[k] = { set: true, hint: v.slice(-4) };
  for (const k of b.clearSecrets) delete secrets[k];
  return { ...base, name: b.name, enabled: b.enabled, config: b.config, secrets, updatedAt: new Date().toISOString() };
}

/** Installs the fetch mock. `extra` answers first (use it to fail or observe a call); the rest is a tiny in-memory API. */
export function install(w: World, extra: (c: Call) => Response | Promise<Response> | undefined = () => undefined) {
  return mockApi(
    (c) => {
      const custom = extra(c);
      if (custom) return custom;
      const { method, pathname: p } = c;
      const id = p.split('/')[5];
      if (method === 'GET' && p === '/api/v1/integrations/connectors') return json(w.connectors);
      if (method === 'GET' && p === '/api/v1/integrations/connections') return json(w.connections);
      if (method === 'POST' && p === '/api/v1/integrations/connections') {
        return c.json().then((b) => {
          const input = b as ConnectionInput;
          const created = applyInput(
            connection({
              id: 'k-new',
              connector: input.connector ?? 'http',
              secrets: {},
              status: 'new',
              statusDetail: input.connector === 'pads4' ? 'Saved. Orbit does not connect to PADS4 yet.' : null,
              lastTestedAt: null,
              tokenExpiresAt: null,
            }),
            input,
          );
          w.connections = [...w.connections, created];
          return json(created, 201);
        });
      }
      if (method === 'PUT' && /^\/api\/v1\/integrations\/connections\/[^/]+$/.test(p)) {
        return c.json().then((b) => {
          const updated = applyInput(
            w.connections.find((x) => x.id === id)!,
            b as ConnectionInput,
          );
          w.connections = w.connections.map((x) => (x.id === id ? updated : x));
          return json(updated);
        });
      }
      if (method === 'DELETE' && /^\/api\/v1\/integrations\/connections\/[^/]+$/.test(p)) {
        w.connections = w.connections.filter((x) => x.id !== id);
        return new Response(null, { status: 204 });
      }
      if (method === 'POST' && p.endsWith('/test')) {
        w.connections = w.connections.map((x) =>
          x.id === id
            ? {
                ...x,
                status: w.result.ok ? 'ok' : 'error',
                statusDetail: w.result.ok ? 'Signed in.' : 'The test failed.',
                lastTestedAt: w.result.testedAt,
              }
            : x,
        );
        return json(w.result);
      }
      if (method === 'GET' && p.endsWith('/activity')) {
        const start = Number(c.search.get('cursor') ?? 0);
        const items = w.activity.slice(start, start + w.pageSize);
        const next = start + w.pageSize < w.activity.length ? String(start + w.pageSize) : null;
        return json({ items, nextCursor: next });
      }
      return undefined;
    },
    { permissions: w.permissions },
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
