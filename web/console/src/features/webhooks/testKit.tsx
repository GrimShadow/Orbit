// Test-only scaffolding for the webhooks screens: fixtures, a small in-memory API behind a fetch mock, and the console
// rendered at a route with a probe that exposes the current URL. Nothing in the app imports this.
import { render } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { App, queryClient } from '../../App';
import { AuthContext } from '../../auth/AuthProvider';
import { json, mockApi, signedIn, type Call } from '../workflow/workflowTestKit';
import type { ChannelDto, DeliveryDetail, DeliveryDto, WebhookDto, WebhookEventType } from './webhooksApi';

export { json, type Call };

export const EVENTS: WebhookEventType[] = [
  { name: 'asset.created', category: 'asset', description: 'An asset was created.', derived: false },
  { name: 'asset.status.changed', category: 'asset', description: 'An asset moved to another status.', derived: false },
  { name: 'asset.published', category: 'asset', description: 'An asset was published.', derived: true },
  // Filed under "asset" by the server although its name starts "rendition.": a wildcard must follow the name, not the group.
  { name: 'rendition.ready', category: 'asset', description: 'A rendition is ready.', derived: false },
  { name: 'workflow.task.created', category: 'workflow', description: 'A workflow task was created.', derived: false },
  { name: 'workflow.task.completed', category: 'workflow', description: 'A workflow task was decided.', derived: false },
  { name: 'webhook.ping', category: 'webhook', description: 'A test ping.', derived: false },
];

const ago = (ms: number) => new Date(Date.now() - ms).toISOString();
const ahead = (ms: number) => new Date(Date.now() + ms).toISOString();
export const MIN = 60_000;
export { ago, ahead };

export const webhook = (over: Partial<WebhookDto> = {}): WebhookDto => ({
  id: 'w1',
  name: 'Acme publishing hook',
  url: 'https://hooks.acme.test/orbit',
  secretLast4: 'a1b2',
  events: ['asset.published', 'asset.created', 'workflow.*', 'webhook.ping'],
  active: true,
  createdBy: null,
  createdAt: ago(86_400_000),
  updatedAt: ago(86_400_000),
  stats: { succeeded24h: 12, failed24h: 3, pending: 1, lastDeliveryAt: ago(5 * MIN), lastStatus: 'succeeded' },
  ...over,
});

export const delivery = (over: Partial<DeliveryDto> = {}): DeliveryDto => ({
  id: 'd1',
  webhookId: 'w1',
  webhookName: 'Acme publishing hook',
  eventId: 'e1',
  eventType: 'asset.published',
  status: 'succeeded',
  attempt: 1,
  maxAttempts: 8,
  createdAt: ago(2 * MIN),
  nextAttemptAt: null,
  deliveredAt: ago(2 * MIN),
  lastStatusCode: 200,
  lastError: null,
  lastDurationMs: 42,
  replayOf: null,
  ...over,
});

export const detail = (over: Partial<DeliveryDetail> = {}): DeliveryDetail => ({
  ...delivery(),
  payload: { id: 'e1', type: 'asset.published', data: { assetId: 'a1', title: 'Acme hero' } },
  responseExcerpt: 'ok',
  ...over,
});

export const channel = (over: Partial<ChannelDto> = {}): ChannelDto => ({
  id: 'c1',
  kind: 'slack',
  name: 'Acme releases',
  urlHint: 'https://hooks.slack.test/…z9y8',
  events: ['asset.published', 'workflow.task.created'],
  active: true,
  createdAt: ago(86_400_000),
  updatedAt: ago(86_400_000),
  ...over,
});

export interface World {
  webhooks: WebhookDto[];
  deliveries: DeliveryDto[];
  details: Record<string, DeliveryDetail>;
  channels: ChannelDto[];
  events: WebhookEventType[];
  permissions: string[];
  /** Deliveries per page; the rest are reached with the cursor. */
  pageSize: number;
}

export const ADMIN = ['webhooks.manage', 'notifications.manage'];

export function world(over: Partial<World> = {}): World {
  return {
    webhooks: [webhook()],
    deliveries: [],
    details: {},
    channels: [],
    events: EVENTS,
    permissions: ADMIN,
    pageSize: 50,
    ...over,
  };
}

const noContent = () => new Response(null, { status: 204 });
export const problem = (status: number, title: string, extra: Record<string, unknown> = {}) =>
  json({ title, status, ...extra }, status);
export const validation = (errors: Record<string, string[]>) =>
  problem(422, 'One or more validation errors occurred.', { errors });

/** Installs the fetch mock. `extra` answers first (use it to fail or observe a call); the rest is a tiny in-memory API. */
export function install(w: World, extra: (c: Call) => Response | Promise<Response> | undefined = () => undefined) {
  return mockApi(
    (c) => {
      // Synchronous on purpose: `mockApi` falls back to its own answers (`/me`, ...) only when this returns undefined.
      const custom = extra(c);
      if (custom) return custom;
      const { method, pathname: p } = c;
      const id = p.split('/')[4];
      if (method === 'GET' && p === '/api/v1/webhooks/events') return json(w.events);
      if (method === 'GET' && p === '/api/v1/webhooks') return json(w.webhooks);
      if (method === 'POST' && p === '/api/v1/webhooks') {
        return c.json().then((b) => {
          const created = webhook({ ...(b as Partial<WebhookDto>), id: 'w-new', secretLast4: 'xyz9' });
          w.webhooks = [...w.webhooks, created];
          return json({ webhook: created, secret: 'whsec_TESTSECRETVALUE1234xyz9' }, 201);
        });
      }
      if (method === 'PUT' && /^\/api\/v1\/webhooks\/[^/]+$/.test(p)) {
        return c.json().then((b) => {
          const updated = { ...w.webhooks.find((x) => x.id === id)!, ...(b as Partial<WebhookDto>) };
          w.webhooks = w.webhooks.map((x) => (x.id === id ? updated : x));
          return json(updated);
        });
      }
      if (method === 'DELETE' && /^\/api\/v1\/webhooks\/[^/]+$/.test(p)) {
        w.webhooks = w.webhooks.filter((x) => x.id !== id);
        return noContent();
      }
      if (method === 'POST' && p.endsWith('/rotate-secret'))
        return json({ webhook: w.webhooks.find((x) => x.id === id), secret: 'whsec_ROTATEDSECRET5678abcd' });
      if (method === 'POST' && p.endsWith('/ping')) return json(detail({ id: 'd-ping', eventType: 'webhook.ping' }));
      if (method === 'GET' && p === '/api/v1/webhook-deliveries') {
        const start = Number(c.search.get('cursor') ?? 0);
        const items = w.deliveries.slice(start, start + w.pageSize);
        const next = start + w.pageSize < w.deliveries.length ? String(start + w.pageSize) : null;
        return json({ items, nextCursor: next });
      }
      if (method === 'GET' && /^\/api\/v1\/webhook-deliveries\/[^/]+$/.test(p)) {
        const d = w.details[p.split('/')[4]!];
        return d ? json(d) : problem(404, 'Not found');
      }
      if (method === 'POST' && p.endsWith('/replay')) return json(detail({ id: 'd-replay', replayOf: id }));
      if (method === 'GET' && p === '/api/v1/notification-channels') return json(w.channels);
      if (method === 'POST' && p === '/api/v1/notification-channels') {
        return c.json().then((b) => {
          const created = channel({ ...(b as Partial<ChannelDto>), id: 'c-new', urlHint: 'https://hooks.slack.test/…new1' });
          w.channels = [...w.channels, created];
          return json(created, 201);
        });
      }
      if (method === 'PUT' && /^\/api\/v1\/notification-channels\/[^/]+$/.test(p)) {
        return c.json().then((b) => {
          const { url, ...rest } = b as Partial<ChannelDto> & { url?: string | null };
          void url; // never echoed back
          const updated = { ...w.channels.find((x) => x.id === id)!, ...rest };
          w.channels = w.channels.map((x) => (x.id === id ? updated : x));
          return json(updated);
        });
      }
      if (method === 'DELETE' && /^\/api\/v1\/notification-channels\/[^/]+$/.test(p)) {
        w.channels = w.channels.filter((x) => x.id !== id);
        return noContent();
      }
      if (method === 'POST' && p.endsWith('/test')) return json({ ok: true, statusCode: 200, error: null, durationMs: 87 });
      return undefined;
    },
    { permissions: w.permissions },
  );
}

/** Renders the URL the page is at, so a test can assert what is kept in it. */
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
