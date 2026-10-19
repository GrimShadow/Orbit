import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { axe } from 'vitest-axe';
import { setLanguage } from '../../i18n';
import { EventPicker } from './EventPicker';
import {
  EVENTS,
  MIN,
  ahead,
  channel,
  delivery,
  detail,
  install,
  json,
  problem,
  renderAt,
  urlOf,
  validation,
  webhook,
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

async function openMenu(webhookName: string, item: string) {
  await userEvent.click(await screen.findByRole('button', { name: `Actions for ${webhookName}` }));
  await userEvent.click(await screen.findByRole('menuitem', { name: item }));
}

describe('webhook list', () => {
  it('shows each webhook with its URL, events, 24-hour stats, last delivery and secret hint', async () => {
    install(
      world({
        webhooks: [
          webhook(),
          webhook({
            id: 'w2',
            name: 'Paused hook',
            active: false,
            events: ['*'],
            stats: { succeeded24h: 0, failed24h: 0, pending: 0, lastDeliveryAt: null, lastStatus: null },
          }),
        ],
      }),
    );
    renderAt('/admin/webhooks');

    expect(await screen.findByRole('heading', { level: 1, name: 'Webhooks' })).toBeInTheDocument();
    const card = (await screen.findByRole('heading', { name: 'Acme publishing hook' })).closest('article')!;
    expect(within(card).getByText('https://hooks.acme.test/orbit')).toBeInTheDocument();
    // Three chips, the rest collapsed into "+N".
    const chips = within(within(card).getByRole('list', { name: 'Events for Acme publishing hook' })).getAllByRole('listitem');
    expect(chips.map((c) => c.textContent)).toEqual([
      'asset.published',
      'asset.created',
      'workflow.*',
      '+1and 1 more: webhook.ping',
    ]);
    expect(within(card).getByText('12 succeeded')).toBeInTheDocument();
    expect(within(card).getByText('3 failed')).toBeInTheDocument();
    expect(within(card).getByText('1 pending')).toBeInTheDocument();
    expect(within(card).getByText('Succeeded')).toBeInTheDocument();
    expect(within(card).getByText('5 minutes ago')).toBeInTheDocument();
    expect(within(card).getByText('whsec_…a1b2')).toBeInTheDocument();
    expect(within(card).getByRole('switch', { name: 'Acme publishing hook: active' })).toHaveAttribute('aria-checked', 'true');

    const paused = screen.getByRole('heading', { name: 'Paused hook' }).closest('article')!;
    expect(within(paused).getByText('Paused')).toBeInTheDocument();
    expect(within(paused).getByText('All events')).toBeInTheDocument();
    expect(within(paused).getByText('No deliveries yet')).toBeInTheDocument();
    expect(within(paused).getByRole('switch')).toHaveAttribute('aria-checked', 'false');
  });

  it('copies the URL', async () => {
    const writeText = vi.fn(async () => {});
    vi.stubGlobal('navigator', { ...navigator, clipboard: { writeText } });
    install(world());
    renderAt('/admin/webhooks');
    await userEvent.click(await screen.findByRole('button', { name: 'Copy URL of Acme publishing hook' }));
    await waitFor(() => expect(writeText).toHaveBeenCalledWith('https://hooks.acme.test/orbit'));
    expect((await screen.findAllByText('Copied to the clipboard')).length).toBeGreaterThan(0);
  });

  it('invites you to add the first webhook', async () => {
    install(world({ webhooks: [] }));
    renderAt('/admin/webhooks');
    expect(await screen.findByText('No webhooks yet')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'New webhook' })).toBeInTheDocument();
  });

  it('shows a calm no-access state on 403 and an error with retry on failure', async () => {
    install(world(), (c) => (c.pathname === '/api/v1/webhooks' && c.method === 'GET' ? problem(403, 'Forbidden') : undefined));
    renderAt('/admin/webhooks');
    expect(await screen.findByText('You do not have access to webhooks')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'New webhook' })).not.toBeInTheDocument();
    cleanup();

    let fail = true;
    install(world(), (c) => (c.pathname === '/api/v1/webhooks' && c.method === 'GET' && fail ? problem(500, 'Boom') : undefined));
    renderAt('/admin/webhooks');
    expect(await screen.findByText('We could not load the webhooks', undefined, { timeout: 4000 })).toBeInTheDocument();
    fail = false;
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByRole('heading', { name: 'Acme publishing hook' })).toBeInTheDocument();
  });

  it('shows skeletons while loading', async () => {
    install(world(), (c) =>
      c.pathname === '/api/v1/webhooks' && c.method === 'GET' ? new Promise<Response>(() => {}) : undefined,
    );
    renderAt('/admin/webhooks');
    expect(await screen.findByText('Loading')).toBeInTheDocument();
  });

  it('turns a webhook off with the switch, sending the stored values with active=false', async () => {
    const calls = install(world());
    renderAt('/admin/webhooks');
    const sw = await screen.findByRole('switch', { name: 'Acme publishing hook: active' });
    await userEvent.click(sw);
    await waitFor(() => expect(writes(calls, 'PUT', '/api/v1/webhooks/w1')).toHaveLength(1));
    expect(await writes(calls, 'PUT', '/api/v1/webhooks/w1')[0]!.json()).toEqual({
      name: 'Acme publishing hook',
      url: 'https://hooks.acme.test/orbit',
      events: ['asset.published', 'asset.created', 'workflow.*', 'webhook.ping'],
      active: false,
    });
    await waitFor(() =>
      expect(screen.getByRole('switch', { name: 'Acme publishing hook: active' })).toHaveAttribute('aria-checked', 'false'),
    );
  });

  it('puts the switch back and says so when the change is refused', async () => {
    install(world(), (c) => (c.method === 'PUT' ? problem(409, 'Conflict', { detail: 'Try again later.' }) : undefined));
    renderAt('/admin/webhooks');
    await userEvent.click(await screen.findByRole('switch', { name: 'Acme publishing hook: active' }));
    expect(await screen.findByText('Could not change “Acme publishing hook”')).toBeInTheDocument();
    await waitFor(() =>
      expect(screen.getByRole('switch', { name: 'Acme publishing hook: active' })).toHaveAttribute('aria-checked', 'true'),
    );
  });
});

describe('creating a webhook', () => {
  it('creates with the chosen events and shows the secret once', async () => {
    const writeText = vi.fn(async () => {});
    vi.stubGlobal('navigator', { ...navigator, clipboard: { writeText } });
    const calls = install(world({ webhooks: [] }));
    renderAt('/admin/webhooks');

    await userEvent.click(await screen.findByRole('button', { name: 'New webhook' }));
    const form = await dialog('New webhook');
    await userEvent.type(within(form).getByLabelText('Name'), 'Release feed');
    await userEvent.type(within(form).getByLabelText('Endpoint URL'), 'https://hooks.acme.test/feed');
    await userEvent.click(within(form).getByRole('checkbox', { name: /^asset\.created/ }));
    await userEvent.click(within(form).getByRole('checkbox', { name: /^All workflow events/ }));
    await userEvent.click(within(form).getByRole('button', { name: 'Create webhook' }));

    await waitFor(() => expect(writes(calls, 'POST', '/api/v1/webhooks')).toHaveLength(1));
    expect(await writes(calls, 'POST', '/api/v1/webhooks')[0]!.json()).toEqual({
      name: 'Release feed',
      url: 'https://hooks.acme.test/feed',
      events: ['asset.created', 'workflow.*'],
      active: true,
    });

    // The one-time secret.
    const reveal = await dialog('Webhook created: copy your secret');
    const secret = within(reveal).getByLabelText('Signing secret');
    expect(secret).toHaveValue('whsec_TESTSECRETVALUE1234xyz9');
    expect(within(reveal).getByText(/only time the secret is shown/)).toBeInTheDocument();
    await userEvent.click(within(reveal).getByRole('button', { name: 'Copy secret' }));
    await waitFor(() => expect(writeText).toHaveBeenCalledWith('whsec_TESTSECRETVALUE1234xyz9'));

    // How to verify signatures: collapsed help with a snippet per language.
    await userEvent.click(within(reveal).getByText('How to verify signatures'));
    expect(within(reveal).getByText('X-Dam-Signature')).toBeInTheDocument();
    expect(within(reveal).getByRole('region', { name: 'Node.js' })).toHaveTextContent('createHmac');
    expect(within(reveal).getByRole('region', { name: 'Python' })).toHaveTextContent('compare_digest');

    // Confirming closes it for good: the secret is gone from the page and there is no way to bring it back.
    await userEvent.click(within(reveal).getByRole('button', { name: 'I’ve saved it' }));
    await waitFor(() => expect(screen.queryByDisplayValue('whsec_TESTSECRETVALUE1234xyz9')).not.toBeInTheDocument());
    expect(screen.queryByText('whsec_TESTSECRETVALUE1234xyz9')).not.toBeInTheDocument();
    expect(await screen.findByText('whsec_…xyz9')).toBeInTheDocument(); // only the last four remain, on the card
    expect(screen.queryByRole('menuitem', { name: /secret/i })).not.toBeInTheDocument();
  });

  it('checks the form before sending anything', async () => {
    const calls = install(world({ webhooks: [] }));
    renderAt('/admin/webhooks');
    await userEvent.click(await screen.findByRole('button', { name: 'New webhook' }));
    const form = await dialog('New webhook');
    await userEvent.click(within(form).getByRole('button', { name: 'Create webhook' }));
    expect(within(form).getByText('Enter a name.')).toBeInTheDocument();
    expect(within(form).getByText('Enter the URL to send events to.')).toBeInTheDocument();
    expect(within(form).getByText('Choose at least one event.')).toBeInTheDocument();

    // Typing into a field retires its message.
    await userEvent.type(within(form).getByLabelText('Name'), 'Feed');
    expect(within(form).queryByText('Enter a name.')).not.toBeInTheDocument();
    await userEvent.click(within(form).getByRole('checkbox', { name: /^All events/ }));
    expect(within(form).queryByText('Choose at least one event.')).not.toBeInTheDocument();
    await userEvent.click(within(form).getByRole('checkbox', { name: /^All events/ }));

    await userEvent.type(within(form).getByLabelText('Endpoint URL'), 'ftp://example.com');
    await userEvent.click(within(form).getByRole('button', { name: 'Create webhook' }));
    expect(within(form).getByText('Enter a full URL starting with http:// or https://.')).toBeInTheDocument();
    expect(writes(calls, 'POST', '/api/v1/webhooks')).toHaveLength(0);
  });

  it('shows the server’s message on the URL field when it refuses the address', async () => {
    install(world({ webhooks: [] }), (c) =>
      c.method === 'POST' && c.pathname === '/api/v1/webhooks'
        ? validation({ Url: ['That address is not allowed: it resolves to a private network.'] })
        : undefined,
    );
    renderAt('/admin/webhooks');
    await userEvent.click(await screen.findByRole('button', { name: 'New webhook' }));
    const form = await dialog('New webhook');
    await userEvent.type(within(form).getByLabelText('Name'), 'Internal');
    const url = within(form).getByLabelText('Endpoint URL');
    await userEvent.type(url, 'http://10.0.0.5/hook');
    await userEvent.click(within(form).getByRole('checkbox', { name: /^All events/ }));
    await userEvent.click(within(form).getByRole('button', { name: 'Create webhook' }));

    expect(await within(form).findByText('That address is not allowed: it resolves to a private network.')).toBeInTheDocument();
    expect(url).toHaveAttribute('aria-invalid', 'true');
    expect(url).toHaveAccessibleDescription(/private network/);
    // Nothing was created, so no secret is shown and the panel stays open for a fix.
    expect(screen.queryByRole('dialog', { name: 'Webhook created: copy your secret' })).not.toBeInTheDocument();
    expect(form).toHaveAttribute('open');
  });

  it('maps other field errors, and shows an unmapped one at the top of the form', async () => {
    install(world({ webhooks: [] }), (c) =>
      c.method === 'POST'
        ? validation({
            Name: ["'Name' must not be empty."],
            'Events[0]': ["'bogus.*' matches no known event."],
            Other: ['Something else.'],
          })
        : undefined,
    );
    renderAt('/admin/webhooks');
    await userEvent.click(await screen.findByRole('button', { name: 'New webhook' }));
    const form = await dialog('New webhook');
    await userEvent.type(within(form).getByLabelText('Name'), 'x');
    await userEvent.type(within(form).getByLabelText('Endpoint URL'), 'https://example.com/h');
    await userEvent.click(within(form).getByRole('checkbox', { name: /^All events/ }));
    await userEvent.click(within(form).getByRole('button', { name: 'Create webhook' }));
    expect(await within(form).findByText("'Name' must not be empty.")).toBeInTheDocument();
    expect(within(form).getByText("'bogus.*' matches no known event.")).toBeInTheDocument();
    expect(
      within(form)
        .getAllByRole('alert')
        .some((a) => a.textContent === 'Something else.'),
    ).toBe(true);
  });

  it('edits an existing webhook with its values filled in', async () => {
    const calls = install(world());
    renderAt('/admin/webhooks');
    await openMenu('Acme publishing hook', 'Edit');
    const form = await dialog('Edit webhook');
    expect(within(form).getByLabelText('Name')).toHaveValue('Acme publishing hook');
    expect(within(form).getByLabelText('Endpoint URL')).toHaveValue('https://hooks.acme.test/orbit');
    expect(within(form).getByRole('checkbox', { name: /^asset\.created/ })).toBeChecked();
    // Covered by workflow.* so it is shown ticked and cannot be unticked separately.
    expect(within(form).getByRole('checkbox', { name: /^workflow\.task\.created/ })).toBeDisabled();
    await userEvent.clear(within(form).getByLabelText('Name'));
    await userEvent.type(within(form).getByLabelText('Name'), 'Renamed');
    await userEvent.click(within(form).getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(writes(calls, 'PUT', '/api/v1/webhooks/w1')).toHaveLength(1));
    expect(await writes(calls, 'PUT', '/api/v1/webhooks/w1')[0]!.json()).toMatchObject({
      name: 'Renamed',
      events: ['asset.published', 'asset.created', 'workflow.*', 'webhook.ping'],
    });
    expect(await screen.findByText('Saved “Renamed”')).toBeInTheDocument();
    expect(screen.queryByText(/matches no known event/)).not.toBeInTheDocument();
  });
});

describe('webhook actions', () => {
  it('rotates the secret after a confirmation and shows the new one once', async () => {
    const calls = install(world());
    renderAt('/admin/webhooks');
    await openMenu('Acme publishing hook', 'Rotate secret');
    const confirm = await dialog('Rotate the signing secret?');
    expect(within(confirm).getByText(/old secret stops working immediately/)).toBeInTheDocument();
    await userEvent.click(within(confirm).getByRole('button', { name: 'Cancel' }));
    expect(writes(calls, 'POST', '/api/v1/webhooks/w1/rotate-secret')).toHaveLength(0);

    await openMenu('Acme publishing hook', 'Rotate secret');
    await userEvent.click(within(await dialog('Rotate the signing secret?')).getByRole('button', { name: 'Rotate secret' }));
    const reveal = await dialog('New signing secret');
    expect(within(reveal).getByLabelText('Signing secret')).toHaveValue('whsec_ROTATEDSECRET5678abcd');
    expect(writes(calls, 'POST', '/api/v1/webhooks/w1/rotate-secret')).toHaveLength(1);
    await userEvent.click(within(reveal).getByRole('button', { name: 'I’ve saved it' }));
    await waitFor(() => expect(screen.queryByDisplayValue('whsec_ROTATEDSECRET5678abcd')).not.toBeInTheDocument());
  });

  it('sends a test ping and reports the outcome', async () => {
    const calls = install(world());
    renderAt('/admin/webhooks');
    await openMenu('Acme publishing hook', 'Send test ping');
    await waitFor(() => expect(writes(calls, 'POST', '/api/v1/webhooks/w1/ping')).toHaveLength(1));
    expect(await screen.findByText('Ping delivered to “Acme publishing hook”')).toBeInTheDocument();
    expect(screen.getByText('HTTP 200 in 42 ms')).toBeInTheDocument();
  });

  it('reports a ping the receiver rejected, and disables the ping for a paused webhook', async () => {
    install(world({ webhooks: [webhook(), webhook({ id: 'w2', name: 'Paused hook', active: false })] }), (c) =>
      c.pathname.endsWith('/ping')
        ? json(
            detail({
              status: 'pending',
              lastStatusCode: 500,
              lastError: 'The receiver answered HTTP 500.',
              nextAttemptAt: ahead(MIN),
            }),
          )
        : undefined,
    );
    renderAt('/admin/webhooks');
    await openMenu('Acme publishing hook', 'Send test ping');
    expect(await screen.findByText('Ping to “Acme publishing hook” failed')).toBeInTheDocument();
    expect(screen.getByText('The receiver answered HTTP 500.')).toBeInTheDocument();

    await userEvent.click(await screen.findByRole('button', { name: 'Actions for Paused hook' }));
    expect(await screen.findByRole('menuitem', { name: 'Send test ping' })).toBeDisabled();
  });

  it('deletes only after a confirmation', async () => {
    const calls = install(world());
    renderAt('/admin/webhooks');
    await openMenu('Acme publishing hook', 'Delete');
    const confirm = await dialog('Delete this webhook?');
    expect(within(confirm).getByText(/delivery history will be removed/)).toBeInTheDocument();
    await userEvent.click(within(confirm).getByRole('button', { name: 'Cancel' }));
    expect(writes(calls, 'DELETE', '/api/v1/webhooks/w1')).toHaveLength(0);

    await openMenu('Acme publishing hook', 'Delete');
    await userEvent.click(within(await dialog('Delete this webhook?')).getByRole('button', { name: 'Delete' }));
    await waitFor(() => expect(writes(calls, 'DELETE', '/api/v1/webhooks/w1')).toHaveLength(1));
    expect(await screen.findByText('Deleted “Acme publishing hook”')).toBeInTheDocument();
    expect(await screen.findByText('No webhooks yet')).toBeInTheDocument();
  });

  it('"View deliveries" opens the deliveries tab filtered to that webhook', async () => {
    install(world({ deliveries: [delivery()] }));
    renderAt('/admin/webhooks');
    await openMenu('Acme publishing hook', 'View deliveries');
    expect(await screen.findByRole('tab', { name: 'Deliveries', selected: true })).toBeInTheDocument();
    expect(await screen.findByLabelText('Webhook')).toHaveValue('w1');
    expect(urlOf().searchParams.get('tab')).toBe('deliveries');
    expect(urlOf().searchParams.get('webhook')).toBe('w1');
  });
});

describe('deliveries', () => {
  const rows = () => [
    delivery({ id: 'd1' }),
    delivery({
      id: 'd2',
      status: 'failed',
      attempt: 8,
      lastStatusCode: 500,
      lastError: 'The receiver answered HTTP 500.',
      eventType: 'workflow.task.created',
      lastDurationMs: 310,
      nextAttemptAt: null,
    }),
    delivery({
      id: 'd3',
      status: 'pending',
      attempt: 3,
      lastStatusCode: 503,
      nextAttemptAt: ahead(5 * MIN + 20_000),
      eventType: 'asset.created',
    }),
    delivery({
      id: 'd4',
      status: 'pending',
      attempt: 0,
      lastStatusCode: null,
      lastDurationMs: null,
      nextAttemptAt: ahead(2_000),
      eventType: 'asset.created',
    }),
    delivery({ id: 'd5', replayOf: 'd2', eventType: 'workflow.task.created' }),
  ];

  const table = async () => {
    await screen.findAllByRole('button', { name: /^Open / }); // rows have loaded
    return screen.getByRole('table', { name: 'Webhook deliveries, newest first' });
  };
  const rowOf = async (event: string, nth = 0) => {
    const buttons = within(await table()).getAllByRole('button', { name: new RegExp(`^Open ${event.replace('.', '\\.')} `) });
    return buttons[nth]!.closest('tr')!;
  };

  it('lists deliveries with status, retry countdown, attempt, HTTP status, duration and replay badge', async () => {
    install(world({ deliveries: rows() }));
    renderAt('/admin/webhooks?tab=deliveries');
    await table();

    const ok = await rowOf('asset.published');
    expect(within(ok).getByText('Succeeded')).toBeInTheDocument();
    expect(within(ok).getByText('1 of 8')).toBeInTheDocument();
    expect(within(ok).getByText('200')).toBeInTheDocument();
    expect(within(ok).getByText('42 ms')).toBeInTheDocument();

    const failed = await rowOf('workflow.task.created', 0);
    expect(within(failed).getByText('Failed')).toBeInTheDocument();
    expect(within(failed).getByText('8 of 8')).toBeInTheDocument();
    expect(within(failed).getByText('500')).toBeInTheDocument();

    const retry = await rowOf('asset.created', 0);
    expect(within(retry).getByText('Retrying')).toBeInTheDocument();
    expect(within(retry).getByText('Retry in 5 min')).toBeInTheDocument();
    expect(within(retry).getByText('3 of 8')).toBeInTheDocument();

    const fresh = await rowOf('asset.created', 1);
    expect(within(fresh).getByText('Pending')).toBeInTheDocument();
    expect(within(fresh).getByText('Sending now')).toBeInTheDocument();

    const replay = await rowOf('workflow.task.created', 1);
    expect(within(replay).getByText('Replay')).toBeInTheDocument();
  });

  it('keeps filters in the URL and sends them to the API', async () => {
    const calls = install(world({ deliveries: rows() }));
    renderAt('/admin/webhooks?tab=deliveries&status=failed');
    await table();
    expect(screen.getByLabelText('Status')).toHaveValue('failed');
    const first = calls.find((c) => c.pathname === '/api/v1/webhook-deliveries')!;
    expect(first.search.get('status')).toBe('failed');
    expect(first.search.get('limit')).toBe('25');

    await screen.findByRole('option', { name: 'All workflow.*' });
    await userEvent.selectOptions(screen.getByLabelText('Event type'), 'workflow.*');
    await waitFor(() => expect(urlOf().searchParams.get('event')).toBe('workflow.*'));
    await waitFor(() =>
      expect(calls.some((c) => c.pathname === '/api/v1/webhook-deliveries' && c.search.get('eventType') === 'workflow.*')).toBe(
        true,
      ),
    );

    await userEvent.selectOptions(screen.getByLabelText('Webhook'), 'w1');
    await waitFor(() => expect(calls.some((c) => c.search.get('webhookId') === 'w1')).toBe(true));

    fireEvent.change(screen.getByLabelText('From'), { target: { value: '2026-10-01' } });
    fireEvent.change(screen.getByLabelText('To'), { target: { value: '2026-10-05' } });
    await waitFor(() => expect(urlOf().searchParams.get('to')).toBe('2026-10-05'));
    await waitFor(() => {
      const last = calls.filter((c) => c.pathname === '/api/v1/webhook-deliveries').at(-1)!;
      expect(last.search.get('from')).toBe(new Date('2026-10-01T00:00:00').toISOString());
      expect(last.search.get('to')).toBe(new Date('2026-10-05T23:59:59.999').toISOString());
      expect(last.search.get('status')).toBe('failed');
    });
    expect(urlOf().searchParams.get('tab')).toBe('deliveries');

    await userEvent.click(screen.getByRole('button', { name: 'Clear filters' }));
    await waitFor(() => expect(urlOf().search).toBe('?tab=deliveries'));
    expect(screen.getByLabelText('Status')).toHaveValue('');
  });

  it('says so when no delivery matches the filters', async () => {
    install(world({ deliveries: [] }));
    renderAt('/admin/webhooks?tab=deliveries&status=failed');
    expect(await screen.findByText('No deliveries match these filters')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Clear filters' })).toBeInTheDocument();
    cleanup();
    install(world({ deliveries: [] }));
    renderAt('/admin/webhooks?tab=deliveries');
    expect(await screen.findByText('No deliveries yet')).toBeInTheDocument();
  });

  it('loads more with the cursor', async () => {
    const calls = install(
      world({
        deliveries: [
          delivery({ id: 'd1' }),
          delivery({ id: 'd2', eventType: 'asset.created' }),
          delivery({ id: 'd3', eventType: 'workflow.task.created' }),
        ],
        pageSize: 2,
      }),
    );
    renderAt('/admin/webhooks?tab=deliveries');
    const t = await table();
    expect(within(t).getAllByRole('button', { name: /^Open / })).toHaveLength(2);
    await userEvent.click(screen.getByRole('button', { name: 'Load more' }));
    await waitFor(() => expect(within(t).getAllByRole('button', { name: /^Open / })).toHaveLength(3));
    expect(
      calls
        .filter((c) => c.pathname === '/api/v1/webhook-deliveries')
        .at(-1)!
        .search.get('cursor'),
    ).toBe('2');
    expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument();
  });

  it('refreshes by itself every 10 seconds while a delivery is pending', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const calls = install(
      world({
        deliveries: [delivery({ id: 'd3', status: 'pending', attempt: 1, lastStatusCode: 500, nextAttemptAt: ahead(MIN) })],
      }),
    );
    renderAt('/admin/webhooks?tab=deliveries');
    await table();
    expect(screen.getByText(/refreshes every 10 seconds/)).toBeInTheDocument();
    const count = () => calls.filter((c) => c.pathname === '/api/v1/webhook-deliveries' && c.method === 'GET').length;
    const before = count();
    await vi.advanceTimersByTimeAsync(10_500);
    await waitFor(() => expect(count()).toBeGreaterThan(before));
  });

  it('refreshes on demand', async () => {
    const calls = install(world({ deliveries: [delivery()] }));
    renderAt('/admin/webhooks?tab=deliveries');
    await table();
    const count = () => calls.filter((c) => c.pathname === '/api/v1/webhook-deliveries' && c.method === 'GET').length;
    const before = count();
    await userEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    await waitFor(() => expect(count()).toBe(before + 1));
  });

  it('opens a delivery in a drawer with its payload, response and error, and replays it after a confirmation', async () => {
    const failed = delivery({
      id: 'd2',
      status: 'failed',
      attempt: 8,
      lastStatusCode: 500,
      lastError: 'The receiver answered HTTP 500.',
      nextAttemptAt: null,
    });
    const calls = install(
      world({
        deliveries: [failed],
        details: {
          d2: detail({
            ...failed,
            responseExcerpt: 'upstream exploded',
            payload: { id: 'e1', type: 'asset.published', data: { assetId: 'a1' } },
          }),
          'd-replay': detail({ id: 'd-replay', replayOf: 'd2' }),
        },
      }),
    );
    const writeText = vi.fn(async () => {});
    vi.stubGlobal('navigator', { ...navigator, clipboard: { writeText } });
    renderAt('/admin/webhooks?tab=deliveries');

    await userEvent.click(await screen.findByRole('button', { name: /^Open asset\.published/ }));
    const drawer = await dialog('Delivery');
    expect(urlOf().searchParams.get('delivery')).toBe('d2');
    expect(await within(drawer).findByText('upstream exploded')).toBeInTheDocument();
    expect(within(drawer).getByText('The receiver answered HTTP 500.')).toBeInTheDocument();
    expect(within(drawer).getByText('HTTP 500')).toBeInTheDocument();
    expect(within(drawer).getByText('8 of 8')).toBeInTheDocument();
    const payload = within(drawer).getByRole('region', { name: 'Request payload' });
    expect(payload).toHaveTextContent('"assetId": "a1"');
    await userEvent.click(within(drawer).getByRole('button', { name: 'Copy payload' }));
    await waitFor(() =>
      expect(writeText).toHaveBeenCalledWith(
        JSON.stringify({ id: 'e1', type: 'asset.published', data: { assetId: 'a1' } }, null, 2),
      ),
    );

    // Replay needs a confirmation, then reports the outcome and moves to the new delivery.
    await userEvent.click(within(drawer).getByRole('button', { name: 'Replay' }));
    const confirm = await dialog('Replay this delivery?');
    await userEvent.click(within(confirm).getByRole('button', { name: 'Cancel' }));
    expect(writes(calls, 'POST', '/api/v1/webhook-deliveries/d2/replay')).toHaveLength(0);

    await userEvent.click(within(drawer).getByRole('button', { name: 'Replay' }));
    await userEvent.click(within(await dialog('Replay this delivery?')).getByRole('button', { name: 'Replay' }));
    await waitFor(() => expect(writes(calls, 'POST', '/api/v1/webhook-deliveries/d2/replay')).toHaveLength(1));
    expect(await screen.findByText('Replay delivered', { selector: '.dam-toast__title' })).toBeInTheDocument();
    await waitFor(() => expect(urlOf().searchParams.get('delivery')).toBe('d-replay'));
    // The list was refreshed too.
    await waitFor(() =>
      expect(calls.filter((c) => c.pathname === '/api/v1/webhook-deliveries' && c.method === 'GET').length).toBeGreaterThan(1),
    );
  });

  it('explains a replay the receiver did not accept', async () => {
    install(
      world({
        deliveries: [delivery()],
        details: {
          d1: detail(),
          'd-replay': detail({
            id: 'd-replay',
            status: 'pending',
            lastStatusCode: 500,
            lastError: 'The receiver answered HTTP 500.',
            nextAttemptAt: ahead(MIN),
          }),
        },
      }),
      (c) =>
        c.pathname.endsWith('/replay')
          ? json(
              detail({
                id: 'd-replay',
                status: 'pending',
                lastStatusCode: 500,
                lastError: 'The receiver answered HTTP 500.',
                nextAttemptAt: ahead(MIN),
              }),
            )
          : undefined,
    );
    renderAt('/admin/webhooks?tab=deliveries&delivery=d1');
    const drawer = await dialog('Delivery');
    await userEvent.click(await within(drawer).findByRole('button', { name: 'Replay' }));
    await userEvent.click(within(await dialog('Replay this delivery?')).getByRole('button', { name: 'Replay' }));
    expect(
      await screen.findByText('Replay sent, receiver did not accept it yet', { selector: '.dam-toast__title' }),
    ).toBeInTheDocument();
    await waitFor(() =>
      expect(
        within(drawer)
          .getAllByRole('status')
          .map((n) => n.textContent),
      ).toContain('Replay sent, receiver did not accept it yet The receiver answered HTTP 500.'),
    );
  });

  it('shows the server’s reason when a replay is refused', async () => {
    install(world({ deliveries: [delivery()], details: { d1: detail() } }), (c) =>
      c.pathname.endsWith('/replay') ? problem(409, 'Conflict', { detail: 'The webhook is switched off.' }) : undefined,
    );
    renderAt('/admin/webhooks?tab=deliveries&delivery=d1');
    const drawer = await dialog('Delivery');
    await userEvent.click(await within(drawer).findByRole('button', { name: 'Replay' }));
    await userEvent.click(within(await dialog('Replay this delivery?')).getByRole('button', { name: 'Replay' }));
    expect(await screen.findByText('Could not replay the delivery', { selector: '.dam-toast__title' })).toBeInTheDocument();
    // The toast sits beneath an open drawer, so the drawer says it too.
    expect(within(drawer).getByRole('alert')).toHaveTextContent('Could not replay the delivery The webhook is switched off.');
  });

  it('shows the retry schedule of a pending delivery in the drawer', async () => {
    const d = delivery({
      id: 'd3',
      status: 'pending',
      attempt: 3,
      lastStatusCode: 503,
      lastError: 'The receiver answered HTTP 503.',
      nextAttemptAt: ahead(30 * MIN + 20_000),
    });
    install(world({ deliveries: [d], details: { d3: detail({ ...d, responseExcerpt: null }) } }));
    renderAt('/admin/webhooks?tab=deliveries&delivery=d3');
    const drawer = await dialog('Delivery');
    expect(await within(drawer).findByText('Retry in 30 min')).toBeInTheDocument();
    expect(within(drawer).getByText('Next attempt')).toBeInTheDocument();
    expect(within(drawer).getByText(/returned no body/)).toBeInTheDocument();
  });
});

describe('chat channels', () => {
  const open = () => renderAt('/admin/webhooks?tab=channels');

  it('lists channels with kind, hint, events and the active switch', async () => {
    install(
      world({
        channels: [channel(), channel({ id: 'c2', kind: 'teams', name: 'Acme approvals', active: false, events: ['*'] })],
      }),
    );
    open();
    const card = (await screen.findByRole('heading', { name: 'Acme releases' })).closest('article')!;
    expect(within(card).getByText('Slack')).toBeInTheDocument();
    expect(within(card).getByText('https://hooks.slack.test/…z9y8')).toBeInTheDocument();
    expect(within(card).getByText('asset.published')).toBeInTheDocument();
    expect(within(card).getByRole('switch', { name: 'Acme releases: active' })).toHaveAttribute('aria-checked', 'true');
    const teams = screen.getByRole('heading', { name: 'Acme approvals' }).closest('article')!;
    expect(within(teams).getByText('Teams')).toBeInTheDocument();
    expect(within(teams).getByRole('switch')).toHaveAttribute('aria-checked', 'false');
  });

  it('creates a channel: the URL is required, sent as typed, and explained for the chosen kind', async () => {
    const calls = install(world());
    open();
    await userEvent.click(await screen.findByRole('button', { name: 'New channel' }));
    const form = await dialog('New chat channel');
    expect(within(form).getByText(/Stored encrypted and never shown again/)).toBeInTheDocument();
    expect(within(form).getByText(/create an app, turn on Incoming Webhooks/)).toBeInTheDocument();
    await userEvent.selectOptions(within(form).getByLabelText('Kind'), 'teams');
    expect(within(form).getByText(/open the channel’s Workflows/)).toBeInTheDocument();

    await userEvent.click(within(form).getByRole('button', { name: 'Create channel' }));
    expect(within(form).getByText('Enter the URL to send events to.')).toBeInTheDocument();
    expect(writes(calls, 'POST', '/api/v1/notification-channels')).toHaveLength(0);

    await userEvent.type(within(form).getByLabelText('Name'), 'Approvals');
    await userEvent.type(within(form).getByLabelText('Incoming webhook URL'), 'https://example.webhook.test/abc');
    await userEvent.click(within(form).getByRole('checkbox', { name: /^workflow\.task\.created/ }));
    await userEvent.click(within(form).getByRole('button', { name: 'Create channel' }));
    await waitFor(() => expect(writes(calls, 'POST', '/api/v1/notification-channels')).toHaveLength(1));
    expect(await writes(calls, 'POST', '/api/v1/notification-channels')[0]!.json()).toEqual({
      kind: 'teams',
      name: 'Approvals',
      url: 'https://example.webhook.test/abc',
      events: ['workflow.task.created'],
      active: true,
    });
    expect(await screen.findByText('Saved “Approvals”')).toBeInTheDocument();
    // The URL is never shown again, only the server's hint.
    expect(screen.queryByText('https://example.webhook.test/abc')).not.toBeInTheDocument();
  });

  it('keeps the stored URL when editing with the field left empty, and sends a new one when typed', async () => {
    const calls = install(world({ channels: [channel()] }));
    open();
    await userEvent.click(await screen.findByRole('button', { name: 'Edit Acme releases' }));
    const form = await dialog('Edit chat channel');
    const url = within(form).getByLabelText('Incoming webhook URL');
    expect(url).toHaveValue('');
    expect(url).toHaveAttribute('placeholder', 'unchanged');
    expect(within(form).getByText(/currently https:\/\/hooks\.slack\.test\/…z9y8/)).toBeInTheDocument();
    await userEvent.clear(within(form).getByLabelText('Name'));
    await userEvent.type(within(form).getByLabelText('Name'), 'Acme launches');
    await userEvent.click(within(form).getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(writes(calls, 'PUT', '/api/v1/notification-channels/c1')).toHaveLength(1));
    expect(await writes(calls, 'PUT', '/api/v1/notification-channels/c1')[0]!.json()).toEqual({
      kind: 'slack',
      name: 'Acme launches',
      url: null,
      events: ['asset.published', 'workflow.task.created'],
      active: true,
    });
    expect(await screen.findByText('Saved “Acme launches”')).toBeInTheDocument();

    await userEvent.click(await screen.findByRole('button', { name: 'Edit Acme launches' }));
    const again = await dialog('Edit chat channel');
    await userEvent.type(within(again).getByLabelText('Incoming webhook URL'), 'https://hooks.slack.test/new');
    await userEvent.click(within(again).getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(writes(calls, 'PUT', '/api/v1/notification-channels/c1')).toHaveLength(2));
    expect(await writes(calls, 'PUT', '/api/v1/notification-channels/c1')[1]!.json()).toMatchObject({
      url: 'https://hooks.slack.test/new',
    });
  });

  it('turns a channel off without touching its URL', async () => {
    const calls = install(world({ channels: [channel()] }));
    open();
    await userEvent.click(await screen.findByRole('switch', { name: 'Acme releases: active' }));
    await waitFor(() => expect(writes(calls, 'PUT', '/api/v1/notification-channels/c1')).toHaveLength(1));
    expect(await writes(calls, 'PUT', '/api/v1/notification-channels/c1')[0]!.json()).toMatchObject({ url: null, active: false });
  });

  it('maps a refused URL to the field', async () => {
    install(world(), (c) => (c.method === 'POST' ? validation({ Url: ['Only http and https URLs are allowed.'] }) : undefined));
    open();
    await userEvent.click(await screen.findByRole('button', { name: 'New channel' }));
    const form = await dialog('New chat channel');
    await userEvent.type(within(form).getByLabelText('Name'), 'x');
    await userEvent.type(within(form).getByLabelText('Incoming webhook URL'), 'http://example.test/');
    await userEvent.click(within(form).getByRole('checkbox', { name: /^All events/ }));
    await userEvent.click(within(form).getByRole('button', { name: 'Create channel' }));
    expect(await within(form).findByText('Only http and https URLs are allowed.')).toBeInTheDocument();
    expect(within(form).getByLabelText('Incoming webhook URL')).toHaveAttribute('aria-invalid', 'true');
  });

  it('sends a test message and keeps the result on the card', async () => {
    const calls = install(world({ channels: [channel()] }));
    open();
    await userEvent.click(await screen.findByRole('button', { name: 'Send a test message to Acme releases' }));
    await waitFor(() => expect(writes(calls, 'POST', '/api/v1/notification-channels/c1/test')).toHaveLength(1));
    expect(await screen.findByText('Test message sent to “Acme releases”')).toBeInTheDocument();
    expect(screen.getAllByText('HTTP 200 in 87 ms').length).toBeGreaterThan(0);
    expect(screen.getByText('Test OK')).toBeInTheDocument();
  });

  it('shows what the chat service answered when the test fails', async () => {
    install(world({ channels: [channel()] }), (c) =>
      c.pathname.endsWith('/test') ? json({ ok: false, statusCode: 404, error: 'no_service', durationMs: 20 }) : undefined,
    );
    open();
    await userEvent.click(await screen.findByRole('button', { name: 'Send a test message to Acme releases' }));
    expect(await screen.findByText('Test message to “Acme releases” failed')).toBeInTheDocument();
    expect(screen.getAllByText(/no_service/).length).toBeGreaterThan(0);
    expect(screen.getByText('Test failed')).toBeInTheDocument();
  });

  it('deletes only after a confirmation', async () => {
    const calls = install(world({ channels: [channel()] }));
    open();
    await userEvent.click(await screen.findByRole('button', { name: 'Delete Acme releases' }));
    const confirm = await dialog('Delete this chat channel?');
    await userEvent.click(within(confirm).getByRole('button', { name: 'Cancel' }));
    expect(writes(calls, 'DELETE', '/api/v1/notification-channels/c1')).toHaveLength(0);
    await userEvent.click(await screen.findByRole('button', { name: 'Delete Acme releases' }));
    await userEvent.click(within(await dialog('Delete this chat channel?')).getByRole('button', { name: 'Delete' }));
    await waitFor(() => expect(writes(calls, 'DELETE', '/api/v1/notification-channels/c1')).toHaveLength(1));
    expect(await screen.findByText('Deleted “Acme releases”')).toBeInTheDocument();
    expect(await screen.findByText('No chat channels yet')).toBeInTheDocument();
  });

  it('shows a no-access state on 403', async () => {
    install(world(), (c) => (c.pathname === '/api/v1/notification-channels' ? problem(403, 'Forbidden') : undefined));
    open();
    expect(await screen.findByText('You do not have access to chat channels')).toBeInTheDocument();
  });
});

describe('permissions', () => {
  it('shows all three tabs to someone who can manage both', async () => {
    install(world());
    renderAt('/admin/webhooks');
    expect(await screen.findAllByRole('tab')).toHaveLength(3);
    expect(screen.getAllByRole('tab').map((t) => t.textContent)).toEqual(['Webhooks', 'Deliveries', 'Chat channels']);
    await userEvent.click(screen.getByRole('tab', { name: 'Chat channels' }));
    expect(urlOf().searchParams.get('tab')).toBe('channels');
    expect(await screen.findByText('No chat channels yet')).toBeInTheDocument();
  });

  it('shows no chat channels tab without notifications.manage', async () => {
    const calls = install(world({ permissions: ['webhooks.manage'] }));
    renderAt('/admin/webhooks?tab=channels');
    expect(await screen.findByRole('heading', { name: 'Acme publishing hook' })).toBeInTheDocument();
    expect(screen.getAllByRole('tab').map((t) => t.textContent)).toEqual(['Webhooks', 'Deliveries']);
    expect(calls.some((c) => c.pathname === '/api/v1/notification-channels')).toBe(false);
  });

  it('shows only the chat channels tab without webhooks.manage', async () => {
    const calls = install(world({ permissions: ['notifications.manage'], channels: [channel()] }));
    renderAt('/admin/webhooks');
    expect(await screen.findByRole('heading', { name: 'Acme releases' })).toBeInTheDocument();
    expect(screen.getAllByRole('tab').map((t) => t.textContent)).toEqual(['Chat channels']);
    expect(calls.some((c) => c.pathname === '/api/v1/webhooks' || c.pathname === '/api/v1/webhook-deliveries')).toBe(false);
  });

  it('shows a calm message to someone with neither permission', async () => {
    install(world({ permissions: ['assets.read'] }));
    renderAt('/admin/webhooks');
    expect(await screen.findByText('You do not have access to webhooks')).toBeInTheDocument();
    expect(screen.queryByRole('tab')).not.toBeInTheDocument();
  });
});

describe('event picker', () => {
  function Harness({ start = [] as string[] }) {
    const [value, setValue] = useState(start);
    return (
      <>
        <EventPicker events={EVENTS} value={value} onChange={setValue} />
        <output aria-label="value">{JSON.stringify(value)}</output>
      </>
    );
  }
  const value = () => JSON.parse(screen.getByLabelText('value').textContent!) as string[];

  it('groups events by category, marks derived ones and collapses names into a wildcard', async () => {
    render(<Harness />);
    expect(screen.getByRole('group', { name: 'Asset' })).toBeInTheDocument();
    expect(screen.getByRole('group', { name: 'Workflow' })).toBeInTheDocument();
    expect(screen.getAllByRole('img', { name: /Derived event/ })).toHaveLength(1);

    await userEvent.click(screen.getByRole('checkbox', { name: /^asset\.created/ }));
    await userEvent.click(screen.getByRole('checkbox', { name: /^asset\.status\.changed/ }));
    expect(value()).toEqual(['asset.created', 'asset.status.changed']);
    await userEvent.click(screen.getByRole('checkbox', { name: /^All asset events/ }));
    expect(value()).toEqual(['asset.*']);
    expect(screen.getByRole('checkbox', { name: /^asset\.published/ })).toBeDisabled();
    expect(screen.getByRole('checkbox', { name: /^asset\.published/ })).toBeChecked();
    // `asset.*` matches names, so an event filed under "asset" with another prefix is not covered.
    expect(screen.getByRole('checkbox', { name: /^rendition\.ready/ })).not.toBeChecked();
    await userEvent.click(screen.getByRole('checkbox', { name: /^All asset events/ }));
    expect(value()).toEqual([]);
  });

  it('"All events" replaces everything and disables the rest', async () => {
    render(<Harness start={['asset.created']} />);
    await userEvent.click(screen.getByRole('checkbox', { name: /^All events/ }));
    expect(value()).toEqual(['*']);
    expect(screen.getByRole('checkbox', { name: /^workflow\.task\.created/ })).toBeDisabled();
    await userEvent.click(screen.getByRole('checkbox', { name: /^All events/ }));
    expect(value()).toEqual([]);
  });

  it('keeps custom prefix patterns as chips beside the checkboxes', async () => {
    render(<Harness start={['asset.created', 'workflow.task.*']} />);
    expect(screen.getByRole('checkbox', { name: /^workflow\.task\.completed/ })).toBeChecked(); // covered by the prefix
    expect(screen.getByRole('button', { name: 'Remove workflow.task.*' })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Remove workflow.task.*' }));
    expect(value()).toEqual(['asset.created']);
    await userEvent.type(screen.getByLabelText('Custom patterns'), 'asset.status.*{Enter}');
    expect(value()).toEqual(['asset.created', 'asset.status.*']);
  });
});

describe('accessibility', () => {
  it('has no violations on the page, the form, the secret dialog and the delivery drawer', async () => {
    install(
      world({
        deliveries: [
          delivery(),
          delivery({ id: 'd2', status: 'failed', lastStatusCode: 500, lastError: 'boom', eventType: 'asset.created' }),
        ],
        details: { d1: detail() },
        channels: [channel()],
      }),
    );
    const { container } = renderAt('/admin/webhooks');
    await screen.findByRole('heading', { name: 'Acme publishing hook' });
    expect(await axe(container)).toHaveNoViolations();

    // Form.
    await userEvent.click(screen.getByRole('button', { name: 'New webhook' }));
    const form = await dialog('New webhook');
    expect(await axe(form)).toHaveNoViolations();
    await userEvent.type(within(form).getByLabelText('Name'), 'Feed');
    await userEvent.type(within(form).getByLabelText('Endpoint URL'), 'https://example.com/h');
    await userEvent.click(within(form).getByRole('checkbox', { name: /^All events/ }));
    await userEvent.click(within(form).getByRole('button', { name: 'Create webhook' }));

    // Secret dialog (with the help open).
    const reveal = await dialog('Webhook created: copy your secret');
    await userEvent.click(within(reveal).getByText('How to verify signatures'));
    expect(await axe(reveal)).toHaveNoViolations();
    await userEvent.click(within(reveal).getByRole('button', { name: 'I’ve saved it' }));
    cleanup();

    // Deliveries and the drawer.
    const second = renderAt('/admin/webhooks?tab=deliveries&delivery=d1');
    const drawer = await dialog('Delivery');
    await within(drawer).findByRole('region', { name: 'Request payload' });
    expect(await axe(drawer)).toHaveNoViolations();
    expect(await axe(second.container)).toHaveNoViolations();
    cleanup();

    // Chat channels, with the form open.
    const third = renderAt('/admin/webhooks?tab=channels');
    await screen.findByRole('heading', { name: 'Acme releases' });
    expect(await axe(third.container)).toHaveNoViolations();
    await userEvent.click(screen.getByRole('button', { name: 'Edit Acme releases' }));
    expect(await axe(await dialog('Edit chat channel'))).toHaveNoViolations();
  });
});
