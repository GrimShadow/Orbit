import { useEffect } from 'react';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { axe } from 'vitest-axe';
import { App, queryClient } from '../../App';
import { AuthContext, type AuthState } from '../../auth/AuthProvider';
import { setLanguage } from '../../i18n';
import type { AuditEntry, AuditVerification } from './auditApi';

const signedIn: AuthState = {
  status: 'authenticated',
  name: 'Dev Admin',
  email: 'admin@dam.local',
  accessToken: 'tok-123',
  signIn: vi.fn(async () => {}),
  signOut: vi.fn(async () => {}),
};

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });

interface Call {
  method: string;
  pathname: string;
  search: URLSearchParams;
}

let calls: Call[] = [];
let rawCalls: { url: URL; headers: Headers }[] = [];
let currentUrl = '';

function Probe() {
  const l = useLocation();
  useEffect(() => {
    currentUrl = l.pathname + l.search;
  });
  return null;
}

/** Answers `handler` first; the page's other background requests (me, thumbnails...) get an empty 404. Plain
 * `fetch(url, init)` calls (the export download) go to `raw`. */
function mockApi(handler: (c: Call) => Response | undefined, raw?: (url: URL) => Response) {
  calls = [];
  rawCalls = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      if (!(input instanceof Request)) {
        const url = new URL(String(input), 'http://localhost');
        rawCalls.push({ url, headers: new Headers(init?.headers) });
        return raw ? raw(url) : json({ title: 'Not found', status: 404 }, 404);
      }
      const u = new URL(input.url);
      const call: Call = { method: input.method, pathname: u.pathname, search: u.searchParams };
      calls.push(call);
      return handler(call) ?? json({ title: 'Not found', status: 404 }, 404);
    }),
  );
}

function renderAt(route = '/admin/audit') {
  return render(
    <AuthContext.Provider value={signedIn}>
      <MemoryRouter initialEntries={[route]}>
        <App />
        <Probe />
      </MemoryRouter>
    </AuthContext.Provider>,
  );
}

const ADMIN = '01a0dee9-95fe-7c61-b8d9-604f095c27b9';
const ASSET = '01a154c4-6b71-74d7-b200-774e1591c29e';

function entry(chainNo: number, over: Partial<AuditEntry> = {}): AuditEntry {
  return {
    id: `00000000-0000-7000-8000-${String(chainNo).padStart(12, '0')}`,
    chainNo,
    occurredAt: new Date(Date.now() - (1000 - chainNo) * 60_000).toISOString(),
    actorId: 'idp-subject-1',
    actorUserId: ADMIN,
    actorName: 'Dev Admin',
    action: 'asset.status.changed',
    entityType: 'asset',
    entityId: ASSET,
    before: null,
    after: { To: 'deleted', From: 'draft', EntityType: 'asset' },
    ip: '203.0.113.7',
    hash: `h${chainNo}`.padEnd(64, '0'),
    prevHash: `h${chainNo - 1}`.padEnd(64, '0'),
    ...over,
  };
}

const ACTIONS = ['asset.created', 'asset.status.changed', 'folder.created'];
const auditPage = (items: AuditEntry[], nextCursor: string | null = null) => json({ items, nextCursor });
const listCalls = () => calls.filter((c) => c.pathname === '/api/v1/audit');

const OK: AuditVerification = {
  verified: true,
  checked: 9615,
  firstId: 'f',
  lastId: 'l',
  firstChainNo: 1,
  lastChainNo: 9615,
  firstHash: 'a'.repeat(64),
  lastHash: 'abcd1234'.padEnd(56, '0') + 'cafe5678',
  brokenAtId: null,
  brokenAtChainNo: null,
  reason: null,
  from: '2026-09-19T00:00:00+00:00',
  to: null,
};

function defaults(c: Call, items: AuditEntry[] = [entry(3), entry(2), entry(1)]) {
  if (c.pathname === '/api/v1/audit') return auditPage(items);
  if (c.pathname === '/api/v1/audit/actions') return json(ACTIONS);
  return undefined;
}

beforeEach(() => {
  queryClient.clear();
  setLanguage('en');
  window.sessionStorage.clear();
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('audit page - results', () => {
  it('lists entries newest first with actor, action, entity and a count', async () => {
    mockApi((c) => defaults(c));
    renderAt();
    expect(await screen.findByRole('heading', { level: 1, name: 'Audit log' })).toBeInTheDocument();
    expect(screen.getByText(/tamper-evident, hash-chained log/)).toBeInTheDocument();
    const table = await screen.findByRole('table', { name: 'Audit log entries, newest first' });
    const rows = within(table).getAllByRole('row').slice(1);
    expect(rows).toHaveLength(3);
    expect(within(rows[0]!).getByText('Dev Admin')).toBeInTheDocument();
    expect(within(rows[0]!).getByText('asset.status.changed')).toBeInTheDocument();
    expect(within(rows[0]!).getByRole('link')).toHaveAttribute('href', `/library/assets/${ASSET}`);
    expect(within(rows[0]!).getByText('203.0.113.7')).toBeInTheDocument();
    expect(await screen.findByText('Showing 3 entries')).toBeInTheDocument();
    expect(listCalls()[0]!.search.get('limit')).toBe('200');
    expect(listCalls()[0]!.search.get('cursor')).toBeNull();
  });

  it('pages with the keyset cursor', async () => {
    mockApi((c) => {
      if (c.pathname === '/api/v1/audit')
        return c.search.get('cursor') === '2' ? auditPage([entry(1)]) : auditPage([entry(3), entry(2)], '2');
      return defaults(c);
    });
    renderAt();
    expect(await screen.findByText(/Showing 2 entries/)).toHaveTextContent('older entries are available');
    await userEvent.click(screen.getByRole('button', { name: 'Load more' }));
    expect(await screen.findByText('Showing 3 entries')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument();
    expect(listCalls().map((c) => c.search.get('cursor'))).toEqual([null, '2']);
  });

  it('shows an empty state that offers to clear the filters', async () => {
    mockApi((c) => (c.pathname === '/api/v1/audit' ? auditPage([]) : defaults(c)));
    renderAt('/admin/audit?action=nothing.here');
    expect(await screen.findByText('No entries match these filters')).toBeInTheDocument();
    await userEvent.click(screen.getAllByRole('button', { name: 'Clear filters' }).at(-1)!);
    await waitFor(() => expect(currentUrl).toBe('/admin/audit'));
  });

  it('shows an error note with a retry that refetches', async () => {
    let fail = true;
    mockApi((c) => {
      if (c.pathname === '/api/v1/audit' && fail) return json({ title: 'Boom', status: 500 }, 500);
      return defaults(c);
    });
    renderAt();
    expect(await screen.findByText("Couldn't load the audit log", undefined, { timeout: 5000 })).toBeInTheDocument();
    fail = false;
    await userEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByText('Showing 3 entries')).toBeInTheDocument();
  });

  it('explains missing access', async () => {
    mockApi((c) => (c.pathname === '/api/v1/audit' ? json({ title: 'Forbidden', status: 403 }, 403) : defaults(c)));
    renderAt();
    expect(await screen.findByText("The audit log isn't available to your role")).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Verify integrity' })).not.toBeInTheDocument();
  });
});

describe('audit page - filters', () => {
  it('reads the filters from the URL, sends them, and shows them as removable chips', async () => {
    mockApi((c) => defaults(c));
    renderAt(`/admin/audit?action=asset.&q=hero&entityType=asset&entityId=${ASSET}&actor=${ADMIN}`);
    await screen.findByText('Showing 3 entries');
    const q = listCalls()[0]!.search;
    expect(q.get('action')).toBe('asset.');
    expect(q.get('q')).toBe('hero');
    expect(q.get('entityType')).toBe('asset');
    expect(q.get('entityId')).toBe(ASSET);
    expect(q.get('actor')).toBe(ADMIN);
    expect(screen.getByRole('combobox', { name: /^Action/ })).toHaveValue('asset.');
    const chips = screen.getByRole('group', { name: 'Active filters' });
    expect(within(chips).getByText('asset.')).toBeInTheDocument();
    // The actor chip shows the person's name once a row has told us who the id is.
    expect(await within(chips).findByText('Dev Admin')).toBeInTheDocument();

    await userEvent.click(within(chips).getByRole('button', { name: /Remove filter Search/ }));
    await waitFor(() => expect(currentUrl).not.toContain('q=hero'));
    expect(currentUrl).toContain('action=asset.');
  });

  it('applies typed text to the URL after a pause and refetches, then clears everything', async () => {
    mockApi((c) => defaults(c));
    renderAt();
    await screen.findByText('Showing 3 entries');
    await userEvent.type(screen.getByRole('combobox', { name: /^Action/ }), 'folder.');
    await waitFor(() => expect(currentUrl).toBe('/admin/audit?action=folder.'));
    await waitFor(() => expect(listCalls().some((c) => c.search.get('action') === 'folder.')).toBe(true));

    await userEvent.type(within(screen.getByRole('search', { name: 'Audit filters' })).getByRole('searchbox'), 'x{Enter}');
    expect(currentUrl).toContain('q=x');

    await userEvent.click(screen.getByRole('button', { name: 'Clear filters' }));
    await waitFor(() => expect(currentUrl).toBe('/admin/audit'));
    expect(screen.getByRole('combobox', { name: /^Action/ })).toHaveValue('');
  });

  it('suggests the known actions from the server', async () => {
    mockApi((c) => defaults(c));
    const { container } = renderAt();
    await screen.findByText('Showing 3 entries');
    await waitFor(() => expect(container.querySelectorAll('datalist option').length).toBeGreaterThanOrEqual(ACTIONS.length));
    const options = [...container.querySelectorAll('datalist option')].map((o) => o.getAttribute('value'));
    expect(options).toEqual(expect.arrayContaining(ACTIONS));
  });

  it('quick ranges set an open-ended "from" and the date inputs follow', async () => {
    mockApi((c) => defaults(c));
    renderAt('/admin/audit?to=2026-10-01T00:00:00Z');
    await screen.findByText('Showing 3 entries');
    await userEvent.click(screen.getByRole('button', { name: 'Last 24 hours' }));
    await waitFor(() => expect(currentUrl).toMatch(/from=/));
    expect(currentUrl).not.toContain('to=');
    const from = new URLSearchParams(currentUrl.split('?')[1]).get('from')!;
    expect(Date.now() - Date.parse(from)).toBeGreaterThan(23.9 * 3_600_000);
    expect(Date.now() - Date.parse(from)).toBeLessThan(24.1 * 3_600_000);
    expect(screen.getByLabelText('From')).not.toHaveValue('');
    expect(screen.getByLabelText('To')).toHaveValue('');
  });

  it('filters by actor when a name in the table is clicked', async () => {
    mockApi((c) => defaults(c));
    renderAt();
    await screen.findByText('Showing 3 entries');
    const table = screen.getByRole('table');
    await userEvent.click(within(table).getAllByRole('button', { name: 'Show only entries by Dev Admin' })[0]!);
    await waitFor(() => expect(currentUrl).toBe(`/admin/audit?actor=${ADMIN}`));
    await waitFor(() => expect(listCalls().some((c) => c.search.get('actor') === ADMIN)).toBe(true));
  });

  it('holds back a half-typed entity id with an inline error', async () => {
    mockApi((c) => defaults(c));
    renderAt();
    await screen.findByText('Showing 3 entries');
    await userEvent.type(screen.getByLabelText('Entity ID'), '01a154');
    expect(await screen.findByText('Enter a full ID (a UUID).')).toBeInTheDocument();
    await new Promise((r) => setTimeout(r, 500));
    expect(currentUrl).toBe('/admin/audit');
  });
});

describe('audit page - detail drawer', () => {
  const changed = entry(3, {
    action: 'asset.metadata.updated',
    before: { title: 'Old title', size: 1, gone: true, nested: { keep: 1 } },
    after: { title: 'New title', size: 1, fresh: ['a'], nested: { keep: 1 } },
  });

  it('opens from a row with every field, the hashes and a before/after diff', async () => {
    mockApi((c) => defaults(c, [changed, entry(2), entry(1)]));
    renderAt();
    await screen.findByText('Showing 3 entries');
    const rows = within(screen.getByRole('table')).getAllByRole('row').slice(1);
    await userEvent.click(within(rows[0]!).getByText('203.0.113.7')); // anywhere on the row

    const dialog = await screen.findByRole('dialog', { name: 'asset.metadata.updated' });
    expect(within(dialog).getByText('#3')).toBeInTheDocument();
    expect(within(dialog).getByText(changed.hash)).toBeInTheDocument();
    expect(within(dialog).getByText(changed.prevHash)).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Copy Hash' })).toBeInTheDocument();
    expect(within(dialog).getByRole('link', { name: 'Open asset' })).toHaveAttribute('href', `/library/assets/${ASSET}`);

    expect(within(dialog).getByText('1 added · 1 changed · 1 removed')).toBeInTheDocument();
    const list = within(dialog).getByRole('list', { name: 'Fields' });
    const items = within(list).getAllByRole('listitem');
    expect(items.map((li) => li.textContent)).toEqual([
      expect.stringContaining('Added: fresh'),
      expect.stringContaining('Removed: gone'),
      expect.stringContaining('Changed: title'),
    ]);
    expect(items[2]).toHaveTextContent('"Old title"');
    expect(items[2]).toHaveTextContent('"New title"');
    expect(within(list).queryByText('nested.keep')).not.toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Show 2 unchanged' }));
    expect(within(list).getByText('nested.keep')).toBeInTheDocument();

    await userEvent.click(within(dialog).getByRole('button', { name: 'Raw JSON' }));
    expect(within(dialog).getByRole('heading', { name: 'Before' })).toBeInTheDocument();
    expect(within(dialog).getByLabelText('After')).toHaveTextContent('"title": "New title"');
  });

  it('lists the recorded fields when there is no before-state, and says so when there are none', async () => {
    mockApi((c) => defaults(c, [entry(2), entry(1, { after: null })]));
    renderAt();
    const buttons = await screen.findAllByRole('button', { name: /^View details/ });
    await userEvent.click(buttons[0]!);
    let dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText('3 fields recorded')).toBeInTheDocument();
    expect(within(dialog).getByText('To')).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Close' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());

    await userEvent.click(screen.getAllByRole('button', { name: /^View details/ })[1]!);
    dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText('No details were recorded for this entry.')).toBeInTheDocument();
  });

  it('collapses a large document behind "show more"', async () => {
    const big = Object.fromEntries(Array.from({ length: 40 }, (_, i) => [`field${String(i).padStart(2, '0')}`, i]));
    mockApi((c) => defaults(c, [entry(1, { before: {}, after: big })]));
    renderAt();
    await screen.findByText('Showing 1 entry');
    await userEvent.click(await screen.findByRole('button', { name: /^View details/ }));
    const dialog = await screen.findByRole('dialog');
    const list = within(dialog).getByRole('list', { name: 'Fields' });
    expect(within(list).getAllByRole('listitem')).toHaveLength(14);
    await userEvent.click(within(dialog).getByRole('button', { name: 'Show 26 more' }));
    expect(within(list).getAllByRole('listitem')).toHaveLength(40);
  });

  it('can filter by the entry actor', async () => {
    mockApi((c) => defaults(c));
    renderAt();
    await userEvent.click((await screen.findAllByRole('button', { name: /^View details/ }))[0]!);
    const dialog = await screen.findByRole('dialog');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Show only this actor' }));
    await waitFor(() => expect(currentUrl).toBe(`/admin/audit?actor=${ADMIN}`));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });
});

describe('audit page - integrity', () => {
  it('never verifies by itself', async () => {
    mockApi((c) => defaults(c));
    renderAt();
    await screen.findByText('Showing 3 entries');
    expect(screen.getByText(/Not verified yet in this session/)).toBeInTheDocument();
    expect(calls.some((c) => c.pathname === '/api/v1/audit/verify')).toBe(false);
  });

  it('reports an intact chain with the count and last hash, and remembers it', async () => {
    mockApi((c) => (c.pathname === '/api/v1/audit/verify' ? json(OK) : defaults(c)));
    const { unmount } = renderAt();
    await userEvent.click(await screen.findByRole('button', { name: 'Verify integrity' }));
    expect(await screen.findByText(/9,615 entries checked, chain intact, last hash/)).toBeInTheDocument();
    expect(screen.getByText('abcd1234…cafe5678')).toBeInTheDocument();
    expect(screen.getByText('Chain intact')).toBeInTheDocument();
    const verify = calls.find((c) => c.pathname === '/api/v1/audit/verify')!;
    // No range set: the last 30 days.
    expect(Date.now() - Date.parse(verify.search.get('from')!)).toBeGreaterThan(29.9 * 86_400_000);
    expect(verify.search.get('to')).toBeNull();

    unmount();
    cleanup();
    mockApi((c) => defaults(c));
    renderAt();
    expect(await screen.findByText(/9,615 entries checked, chain intact/)).toBeInTheDocument();
    expect(screen.getByText(/checked/, { selector: 'time' })).toBeInTheDocument();
  });

  it("verifies the filters' date range", async () => {
    mockApi((c) => (c.pathname === '/api/v1/audit/verify' ? json(OK) : defaults(c)));
    renderAt('/admin/audit?from=2026-10-01T00:00:00Z&to=2026-10-05T00:00:00Z');
    await userEvent.click(await screen.findByRole('button', { name: 'Verify integrity' }));
    await screen.findByText('Chain intact');
    const verify = calls.find((c) => c.pathname === '/api/v1/audit/verify')!;
    expect(verify.search.get('from')).toBe('2026-10-01T00:00:00.000Z');
    expect(verify.search.get('to')).toBe('2026-10-05T00:00:00.000Z');
  });

  it('shows a running state while the check is in flight', async () => {
    let release!: (r: Response) => void;
    mockApi((c) =>
      c.pathname === '/api/v1/audit/verify'
        ? (new Promise<Response>((res) => (release = res)) as unknown as Response)
        : defaults(c),
    );
    renderAt();
    await userEvent.click(await screen.findByRole('button', { name: 'Verify integrity' }));
    expect(await screen.findByText('Verifying the chain…')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Verifying…' })).toBeDisabled();
    release(json(OK));
    expect(await screen.findByText('Chain intact')).toBeInTheDocument();
  });

  it.each([
    ['gap', 'An entry is missing.'],
    ['prev_hash_mismatch', "An entry doesn't link to the previous one."],
    ['hash_mismatch', "An entry's content was changed."],
  ])('names the first broken entry and why (%s)', async (reason, text) => {
    const broken = entry(2, { action: 'folder.deleted' });
    mockApi((c) => {
      if (c.pathname === '/api/v1/audit/verify')
        return json({ ...OK, verified: false, checked: 2, brokenAtId: broken.id, brokenAtChainNo: 2, reason });
      if (c.pathname === '/api/v1/audit' && c.search.get('cursor') === '3') return auditPage([broken]);
      return defaults(c);
    });
    renderAt();
    await userEvent.click(await screen.findByRole('button', { name: 'Verify integrity' }));
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Chain broken at entry #2');
    expect(alert).toHaveTextContent(text);
    expect(screen.queryByText('Chain intact')).not.toBeInTheDocument();

    await userEvent.click(within(alert).getByRole('button', { name: 'Show this entry' }));
    const dialog = await screen.findByRole('dialog', { name: 'folder.deleted' });
    expect(within(dialog).getByText('#2')).toBeInTheDocument();
    const lookup = listCalls().find((c) => c.search.get('cursor') === '3')!;
    expect(lookup.search.get('limit')).toBe('1');
    expect(lookup.search.get('action')).toBeNull();
  });

  it('says when the check itself could not run', async () => {
    mockApi((c) => (c.pathname === '/api/v1/audit/verify' ? json({ title: 'Boom', status: 500 }, 500) : defaults(c)));
    renderAt();
    await userEvent.click(await screen.findByRole('button', { name: 'Verify integrity' }));
    expect(await screen.findByText('Verification could not run')).toBeInTheDocument();
    expect(screen.queryByText('Chain intact')).not.toBeInTheDocument();
  });

  it('says so when the range has nothing to check', async () => {
    mockApi((c) =>
      c.pathname === '/api/v1/audit/verify' ? json({ ...OK, checked: 0, firstHash: null, lastHash: null }) : defaults(c),
    );
    renderAt();
    await userEvent.click(await screen.findByRole('button', { name: 'Verify integrity' }));
    expect(await screen.findByText('Nothing to check')).toBeInTheDocument();
  });
});

describe('audit page - export', () => {
  function stubDownload() {
    const created: Blob[] = [];
    const saved: { download: string; href: string }[] = [];
    URL.createObjectURL = vi.fn((b: Blob | MediaSource) => {
      created.push(b as Blob);
      return `blob:test/${created.length}`;
    });
    URL.revokeObjectURL = vi.fn();
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
      saved.push({ download: this.download, href: this.href });
    });
    return { created, saved };
  }

  it('fetches the file with the bearer token and saves it with the current filters in the name', async () => {
    const { created, saved } = stubDownload();
    const body = '# Orbit audit export (CSV)\n# chain-verification: VERIFIED\nid,chain_no\n1,1\n# end of export: rows=1\n';
    mockApi(
      (c) => defaults(c),
      () => new Response(body, { status: 200, headers: { 'Content-Type': 'text/csv' } }),
    );
    renderAt('/admin/audit?action=asset.&from=2026-10-12T12:00:00Z&to=2026-10-19T12:00:00Z');
    await screen.findByText('Showing 3 entries');
    expect(screen.getByText(/starts and ends with a chain-integrity statement/)).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Export' }));
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Export CSV' }));

    await waitFor(() => expect(saved).toHaveLength(1));
    expect(rawCalls).toHaveLength(1);
    const { url, headers } = rawCalls[0]!;
    expect(url.pathname).toBe('/api/v1/audit/export');
    expect(url.searchParams.get('format')).toBe('csv');
    expect(url.searchParams.get('action')).toBe('asset.');
    expect(url.searchParams.get('from')).toBe('2026-10-12T12:00:00.000Z');
    expect(headers.get('Authorization')).toBe('Bearer tok-123');
    expect(saved[0]!.download).toBe('audit-20261012-20261019.csv');
    expect(saved[0]!.href).toBe('blob:test/1');
    expect(created[0]).toBeInstanceOf(Blob);
    expect(created[0]!.size).toBe(body.length);
    expect(await screen.findByText('Saved audit-20261012-20261019.csv')).toBeInTheDocument();
  });

  it('exports CEF, disables the button while preparing, and toasts a failure', async () => {
    const { saved } = stubDownload();
    let release!: (r: Response) => void;
    mockApi(
      (c) => defaults(c),
      () => new Promise<Response>((res) => (release = res)) as unknown as Response,
    );
    renderAt();
    await screen.findByText('Showing 3 entries');
    await userEvent.click(screen.getByRole('button', { name: 'Export' }));
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Export CEF' }));
    const busy = await screen.findByRole('button', { name: /Preparing export/ });
    expect(busy).toBeDisabled();
    expect(rawCalls[0]!.url.searchParams.get('format')).toBe('cef');
    release(json({ title: 'Forbidden', status: 403 }, 403));
    expect(await screen.findByText('Export failed')).toBeInTheDocument();
    expect(saved).toHaveLength(0);
    expect(await screen.findByRole('button', { name: 'Export' })).toBeEnabled();
  });
});

describe('audit page - accessibility', () => {
  it('has no axe violations on the page or with the drawer open', async () => {
    mockApi((c) =>
      c.pathname === '/api/v1/audit/verify' ? json(OK) : defaults(c, [entry(2, { before: { a: 1 }, after: { a: 2 } }), entry(1)]),
    );
    const { container } = renderAt('/admin/audit?q=x');
    await screen.findByText('Showing 2 entries');
    await userEvent.click(screen.getByRole('button', { name: 'Verify integrity' }));
    await screen.findByText('Chain intact');
    expect(await axe(container)).toHaveNoViolations();

    await userEvent.click(screen.getAllByRole('button', { name: /^View details/ })[0]!);
    const dialog = await screen.findByRole('dialog');
    expect(await axe(dialog)).toHaveNoViolations();
  });
});
