import { act, cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { queryClient } from '../../App';
import { setLanguage } from '../../i18n';
import { json, mockApi, renderAt, type Call } from '../workflow/workflowTestKit';
import { COUNT_POLL_MS, internalPath, type AppNotification, type NotificationPreferences } from './notificationsApi';

const NOTES = '/api/v1/notifications';

function note(over: Partial<AppNotification> = {}): AppNotification {
  return {
    id: 'n1',
    type: 'workflow.task.created',
    category: 'workflow_task',
    title: 'Approval needed: Acme banner',
    body: 'Acme banner is waiting for your decision at the Review step.',
    linkUrl: '/tasks',
    assetId: null,
    read: false,
    readAt: null,
    createdAt: new Date(Date.now() - 2 * 3_600_000).toISOString(),
    ...over,
  };
}

const READ = note({
  id: 'n2',
  category: 'workflow_outcome',
  title: 'Approved: Acme poster',
  body: 'Approved at the Review step.',
  linkUrl: '/library/assets/a2',
  read: true,
  readAt: new Date().toISOString(),
});

const PREFS: NotificationPreferences = {
  emailAvailable: true,
  categories: [
    {
      key: 'workflow_task',
      label: 'Approvals waiting for you',
      description: 'An asset needs your decision.',
      inApp: true,
      email: true,
      defaultInApp: true,
      defaultEmail: true,
    },
    {
      key: 'workflow_outcome',
      label: 'Decisions on your assets',
      description: 'Someone approved or rejected your asset.',
      inApp: true,
      email: false,
      defaultInApp: true,
      defaultEmail: false,
    },
    {
      key: 'sla',
      label: 'Overdue approvals',
      description: 'A deadline was missed.',
      inApp: false,
      email: true,
      defaultInApp: true,
      defaultEmail: true,
    },
    {
      key: 'asset_lifecycle',
      label: 'Asset lifecycle',
      description: 'An asset of yours expired or was archived.',
      inApp: true,
      email: false,
      defaultInApp: true,
      defaultEmail: false,
    },
  ],
};

interface Server {
  unread: number;
  items: AppNotification[];
  pages?: Record<string, { items: AppNotification[]; nextCursor: string | null }>;
  prefs: NotificationPreferences;
  fail?: boolean;
}

/** A tiny stateful notification backend: marking read changes what the next count and list answer. */
function serve(initial: Partial<Server> = {}) {
  const s: Server = { unread: 0, items: [], prefs: PREFS, ...initial };
  s.items = s.items.map((n) => ({ ...n })); // the fake marks them read; never mutate shared fixtures
  if (initial.unread === undefined) s.unread = s.items.filter((n) => !n.read).length;
  const calls = mockApi((c: Call) => {
    if (!c.pathname.startsWith(NOTES)) return undefined;
    if (s.fail && c.method === 'GET' && c.pathname === NOTES) return json({ title: 'Boom', status: 400 }, 400);
    if (c.pathname === `${NOTES}/unread-count`) return json({ count: s.unread });
    if (c.pathname === `${NOTES}/read-all`) {
      const marked = s.items.filter((n) => !n.read).length;
      s.items = s.items.map((n) => ({ ...n, read: true }));
      s.unread = 0;
      return json({ marked });
    }
    if (c.pathname === `${NOTES}/preferences`) {
      if (c.method === 'PUT')
        return (async () => {
          const body = (await c.json()) as { preferences: { category: string; inApp: boolean; email: boolean }[] };
          s.prefs = {
            ...s.prefs,
            categories: s.prefs.categories.map((cat) => {
              const p = body.preferences.find((x) => x.category === cat.key);
              return p ? { ...cat, inApp: p.inApp, email: p.email } : cat;
            }),
          };
          return json(s.prefs);
        })();
      return json(s.prefs);
    }
    const read = /^\/api\/v1\/notifications\/([^/]+)\/read$/.exec(c.pathname);
    if (read && c.method === 'POST') {
      const n = s.items.find((x) => x.id === read[1]);
      if (n && !n.read) {
        n.read = true;
        s.unread = Math.max(0, s.unread - 1);
      }
      return json(n ?? note({ id: read[1], read: true }));
    }
    if (c.pathname === NOTES) {
      const cursor = c.search.get('cursor');
      if (s.pages) return json(s.pages[cursor ?? ''] ?? { items: [], nextCursor: null });
      const unreadOnly = c.search.get('unread') === 'true';
      const limit = Number(c.search.get('limit') ?? 25);
      return json({ items: s.items.filter((n) => !unreadOnly || !n.read).slice(0, limit), nextCursor: null });
    }
    return undefined;
  });
  return { s, calls };
}

const listCalls = (calls: Call[]) => calls.filter((c) => c.pathname === NOTES && c.method === 'GET');
const bell = (name: RegExp | string) => screen.findByRole('button', { name });

beforeEach(() => {
  queryClient.clear();
  setLanguage('en');
});
afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('internalPath', () => {
  it('accepts console paths only', () => {
    expect(internalPath('/tasks')).toBe('/tasks');
    expect(internalPath('/library/assets/a1?x=1')).toBe('/library/assets/a1?x=1');
    for (const bad of [null, undefined, '', 'https://evil.test', '//evil.test', 'javascript:alert(1)', '/\\evil.test'])
      expect(internalPath(bad)).toBeNull();
  });
});

describe('notification bell - badge', () => {
  it('has no pill at zero and a plain name', async () => {
    serve({ items: [], unread: 0 });
    renderAt('/');
    const b = await bell('Notifications');
    expect(b).toHaveAccessibleName('Notifications');
    expect(b.querySelector('.dam-bell__pill')).toBeNull();
  });

  it('shows the unread count and puts it in the accessible name', async () => {
    serve({ items: [], unread: 3 });
    renderAt('/');
    const b = await bell('Notifications, 3 unread');
    expect(b).toHaveTextContent('3');
  });

  it('caps the pill at 99+ but keeps the real number in the name', async () => {
    serve({ items: [], unread: 100 });
    renderAt('/');
    const b = await bell('Notifications, 100 unread');
    expect(b).toHaveTextContent('99+');
  });

  it('polls the count every 30 seconds', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const { s, calls } = serve({ items: [], unread: 1 });
    renderAt('/');
    await bell('Notifications, 1 unread');
    s.unread = 4;
    expect(COUNT_POLL_MS).toBe(30_000);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(COUNT_POLL_MS + 500);
    });
    expect(await bell('Notifications, 4 unread')).toBeInTheDocument();
    expect(calls.filter((c) => c.pathname === `${NOTES}/unread-count`).length).toBeGreaterThanOrEqual(2);
  });

  it('is not shown when signed out', async () => {
    // The sign-in route renders the top bar without the bell (only signed-in users have notifications).
    serve();
    renderAt('/');
    await bell('Notifications');
    expect(screen.getAllByRole('button', { name: /^Notifications/ })).toHaveLength(1);
  });
});

describe('notification bell - panel', () => {
  const two = () => [note(), READ];

  it('opens a panel with the latest items, links, unread marking and the footer links', async () => {
    const { calls } = serve({ items: two() });
    renderAt('/');
    const b = await bell('Notifications, 1 unread');
    expect(b).toHaveAttribute('aria-expanded', 'false');
    await userEvent.click(b);

    const panel = await screen.findByRole('dialog', { name: 'Notifications' });
    expect(b).toHaveAttribute('aria-expanded', 'true');
    expect(panel).toHaveFocus();
    expect(within(panel).getByRole('heading', { name: 'Notifications' })).toBeInTheDocument();
    const list = await within(panel).findByRole('list', { name: 'Notifications' });
    const rows = within(list).getAllByRole('listitem');
    expect(rows).toHaveLength(2);
    expect(within(rows[0]!).getByText('Approval needed: Acme banner')).toBeInTheDocument();
    expect(within(rows[0]!).getByText('Acme banner is waiting for your decision at the Review step.')).toBeInTheDocument();
    expect(within(rows[0]!).getByText('2 hours ago')).toBeInTheDocument();
    expect(within(rows[0]!).getByRole('link')).toHaveAttribute('href', '/tasks');
    expect(within(rows[0]!).getByText('Unread:')).toBeInTheDocument();
    expect(within(rows[1]!).queryByText('Unread:')).not.toBeInTheDocument();
    expect(within(rows[1]!).getByRole('link')).toHaveAttribute('href', '/library/assets/a2');
    expect(within(panel).getByRole('link', { name: 'See all notifications' })).toHaveAttribute('href', '/notifications');
    expect(within(panel).getByRole('link', { name: 'Notification preferences' })).toHaveAttribute(
      'href',
      '/notifications?tab=preferences',
    );
    expect(listCalls(calls).at(-1)?.search.get('limit')).toBe('8');
  });

  it('Escape closes it and puts focus back on the bell', async () => {
    serve({ items: two() });
    renderAt('/');
    const b = await bell('Notifications, 1 unread');
    await userEvent.click(b);
    await screen.findByRole('dialog', { name: 'Notifications' });
    await userEvent.keyboard('{Escape}');
    expect(screen.queryByRole('dialog', { name: 'Notifications' })).not.toBeInTheDocument();
    expect(b).toHaveFocus();
    expect(b).toHaveAttribute('aria-expanded', 'false');
  });

  it('clicking outside closes it, and the bell toggles it', async () => {
    serve({ items: two() });
    renderAt('/');
    const b = await bell('Notifications, 1 unread');
    await userEvent.click(b);
    await screen.findByRole('dialog', { name: 'Notifications' });
    await userEvent.click(b);
    expect(screen.queryByRole('dialog', { name: 'Notifications' })).not.toBeInTheDocument();
    await userEvent.click(b);
    await screen.findByRole('dialog', { name: 'Notifications' });
    await userEvent.click(document.body);
    expect(screen.queryByRole('dialog', { name: 'Notifications' })).not.toBeInTheDocument();
  });

  it('clicking an item marks it read (count drops at once), closes the panel and navigates', async () => {
    const { calls } = serve({ items: two() });
    renderAt('/');
    const b = await bell('Notifications, 1 unread');
    await userEvent.click(b);
    await userEvent.click(await screen.findByRole('link', { name: /Approval needed: Acme banner/ }));

    await bell('Notifications');
    expect(screen.queryByRole('dialog', { name: 'Notifications' })).not.toBeInTheDocument();
    expect(await screen.findByRole('heading', { name: 'Tasks' })).toBeInTheDocument();
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.pathname === `${NOTES}/n1/read`)).toBe(true));
    // Opening an already-read item does not post again.
    calls.length = 0;
    await userEvent.click(await bell('Notifications'));
    await userEvent.click(await screen.findByRole('link', { name: /Approved: Acme poster/ }));
    expect(calls.some((c) => c.method === 'POST')).toBe(false);
  });

  it('is operable from the keyboard: Enter on a focused item opens it', async () => {
    const { calls } = serve({ items: two() });
    renderAt('/');
    await userEvent.click(await bell('Notifications, 1 unread'));
    const link = await screen.findByRole('link', { name: /Approval needed: Acme banner/ });
    link.focus();
    await userEvent.keyboard('{Enter}');
    expect(await screen.findByRole('heading', { name: 'Tasks' })).toBeInTheDocument();
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.pathname === `${NOTES}/n1/read`)).toBe(true));
  });

  it('Mark all read posts once, clears the badge and the unread markers', async () => {
    const { calls } = serve({ items: [note(), note({ id: 'n3', title: 'Overdue: Acme logo', category: 'sla' })] });
    renderAt('/');
    await userEvent.click(await bell('Notifications, 2 unread'));
    const panel = await screen.findByRole('dialog', { name: 'Notifications' });
    await within(panel).findByRole('list');
    await userEvent.click(within(panel).getByRole('button', { name: 'Mark all read' }));
    await bell('Notifications');
    expect(within(panel).queryByText('Unread:')).not.toBeInTheDocument();
    expect(within(panel).getByRole('button', { name: 'Mark all read' })).toBeDisabled();
    expect(calls.filter((c) => c.method === 'POST' && c.pathname === `${NOTES}/read-all`)).toHaveLength(1);
  });

  it('shows a skeleton while loading, then the all-caught-up empty state', async () => {
    let release: () => void = () => {};
    const gate = new Promise<void>((r) => (release = r));
    mockApi((c) => {
      if (c.pathname === `${NOTES}/unread-count`) return json({ count: 0 });
      if (c.pathname === NOTES)
        return (async () => {
          await gate;
          return json({ items: [], nextCursor: null });
        })();
      return undefined;
    });
    renderAt('/');
    await userEvent.click(await bell('Notifications'));
    expect(await screen.findByText('Loading notifications')).toBeInTheDocument();
    release();
    expect(await screen.findByText("You're all caught up")).toBeInTheDocument();
  });

  it('shows an error note with a retry that fetches again', async () => {
    const { s } = serve({ items: two() });
    s.fail = true;
    renderAt('/');
    await userEvent.click(await bell(/^Notifications/));
    const panel = await screen.findByRole('dialog', { name: 'Notifications' });
    expect(await within(panel).findByRole('alert')).toHaveTextContent("Couldn't load notifications.");
    s.fail = false;
    await userEvent.click(within(panel).getByRole('button', { name: 'Try again' }));
    expect(await within(panel).findByText('Approval needed: Acme banner')).toBeInTheDocument();
  });

  it('refreshes the open list when the polled count goes up', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const { s } = serve({ items: [READ] });
    renderAt('/');
    await userEvent.click(await bell('Notifications'));
    await screen.findByText('Approved: Acme poster');
    s.items = [note({ id: 'n9', title: 'Approval needed: Acme flyer' }), READ];
    s.unread = 1;
    await act(async () => {
      await vi.advanceTimersByTimeAsync(COUNT_POLL_MS + 500);
    });
    expect(await screen.findByText('Approval needed: Acme flyer')).toBeInTheDocument();
  });

  it('has no axe violations while open', async () => {
    serve({ items: two() });
    const { container } = renderAt('/');
    await userEvent.click(await bell('Notifications, 1 unread'));
    await screen.findByRole('list', { name: 'Notifications' });
    expect(await axe(container)).toHaveNoViolations();
  });
});

describe('account menu', () => {
  it('links to the notifications page', async () => {
    serve({ items: [] });
    renderAt('/');
    await userEvent.click(await screen.findByRole('button', { name: /^Account menu/ }));
    expect(await screen.findByRole('link', { name: 'Notifications' })).toHaveAttribute('href', '/notifications');
  });
});

describe('notifications page', () => {
  it('lists everything newest first with unread markers', async () => {
    serve({ items: [note(), READ] });
    renderAt('/notifications');
    expect(await screen.findByRole('heading', { name: 'Notifications', level: 1 })).toBeInTheDocument();
    const list = await screen.findByRole('list', { name: 'Notifications' });
    const rows = within(list).getAllByRole('listitem');
    expect(rows).toHaveLength(2);
    expect(within(rows[0]!).getByText('Unread:')).toBeInTheDocument();
    expect(within(rows[1]!).queryByText('Unread:')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'All', pressed: true })).toBeInTheDocument();
  });

  it('pages: Load more appends the next page using the cursor', async () => {
    const { calls } = serve({
      pages: {
        '': { items: [note()], nextCursor: 'c2' },
        c2: { items: [READ], nextCursor: null },
      },
      unread: 1,
    });
    renderAt('/notifications');
    await screen.findByText('Approval needed: Acme banner');
    await userEvent.click(screen.getByRole('button', { name: 'Load more' }));
    expect(await screen.findByText('Approved: Acme poster')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument();
    expect(listCalls(calls).some((c) => c.search.get('cursor') === 'c2')).toBe(true);
  });

  it('the Unread filter asks for unread only and is kept in the URL', async () => {
    const { calls } = serve({ items: [note(), READ] });
    renderAt('/notifications');
    await screen.findByText('Approved: Acme poster');
    await userEvent.click(screen.getByRole('button', { name: 'Unread' }));
    await waitFor(() => expect(screen.queryByText('Approved: Acme poster')).not.toBeInTheDocument());
    expect(screen.getByText('Approval needed: Acme banner')).toBeInTheDocument();
    expect(listCalls(calls).some((c) => c.search.get('unread') === 'true')).toBe(true);
    expect(screen.getByRole('button', { name: 'Unread', pressed: true })).toBeInTheDocument();
  });

  it('opens straight on the unread filter from the URL', async () => {
    const { calls } = serve({ items: [note(), READ] });
    renderAt('/notifications?filter=unread');
    await screen.findByText('Approval needed: Acme banner');
    expect(screen.queryByText('Approved: Acme poster')).not.toBeInTheDocument();
    expect(listCalls(calls)[0]?.search.get('unread')).toBe('true');
  });

  it('marks a single item read from its own button; read items have none', async () => {
    const { calls } = serve({ items: [note(), READ] });
    renderAt('/notifications');
    await screen.findByText('Approved: Acme poster');
    const buttons = screen.getAllByRole('button', { name: /^Mark as read/ });
    expect(buttons).toHaveLength(1);
    await userEvent.click(buttons[0]!);
    await waitFor(() => expect(screen.queryByRole('button', { name: /^Mark as read/ })).not.toBeInTheDocument());
    expect(calls.some((c) => c.method === 'POST' && c.pathname === `${NOTES}/n1/read`)).toBe(true);
    expect(await screen.findByRole('button', { name: 'Notifications' })).toBeInTheDocument();
  });

  it('Mark all read posts, toasts the number and disables itself', async () => {
    const { calls } = serve({ items: [note(), note({ id: 'n3', title: 'Overdue: Acme logo' })] });
    renderAt('/notifications');
    await screen.findByText('Overdue: Acme logo');
    await userEvent.click(screen.getByRole('button', { name: 'Mark all read' }));
    expect(await screen.findByText('2 notifications marked read')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Mark all read' })).toBeDisabled();
    expect(calls.filter((c) => c.method === 'POST' && c.pathname === `${NOTES}/read-all`)).toHaveLength(1);
  });

  it('has an empty state for no notifications at all, and another for nothing unread', async () => {
    serve({ items: [READ] });
    renderAt('/notifications?filter=unread');
    expect(await screen.findByRole('heading', { name: "You're all caught up" })).toBeInTheDocument();
    cleanup();
    queryClient.clear();
    serve({ items: [] });
    renderAt('/notifications');
    expect(await screen.findByRole('heading', { name: 'No notifications yet' })).toBeInTheDocument();
  });

  it('shows an error note with a retry', async () => {
    const { s } = serve({ items: [note()] });
    s.fail = true;
    renderAt('/notifications');
    expect(await screen.findByRole('alert')).toHaveTextContent("Couldn't load notifications.");
    s.fail = false;
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByText('Approval needed: Acme banner')).toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    serve({ items: [note(), READ] });
    const { container } = renderAt('/notifications');
    await screen.findByRole('list', { name: 'Notifications' });
    expect(await axe(container)).toHaveNoViolations();
  });
});

describe('notification preferences', () => {
  const sw = (category: string, channel: 'In-app' | 'Email') => screen.findByRole('switch', { name: `${category}: ${channel}` });

  it('renders the category catalogue from the server with the stored values', async () => {
    serve();
    renderAt('/notifications?tab=preferences');
    expect(await screen.findByRole('heading', { name: 'How you are notified' })).toBeInTheDocument();
    const list = screen.getByRole('list', { name: 'Notification categories' });
    const rows = within(list).getAllByRole('listitem');
    expect(rows).toHaveLength(4);
    expect(within(rows[0]!).getByText('Approvals waiting for you')).toBeInTheDocument();
    expect(within(rows[0]!).getByText('An asset needs your decision.')).toBeInTheDocument();
    expect(await sw('Approvals waiting for you', 'In-app')).toBeChecked();
    expect(await sw('Approvals waiting for you', 'Email')).toBeChecked();
    expect(await sw('Overdue approvals', 'In-app')).not.toBeChecked();
    expect(await sw('Decisions on your assets', 'Email')).not.toBeChecked();
    expect(screen.queryByRole('note')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save preferences' })).toBeDisabled();
  });

  it('is reachable from the Preferences tab on the inbox page', async () => {
    serve({ items: [] });
    renderAt('/notifications');
    await userEvent.click(await screen.findByRole('link', { name: 'Preferences' }));
    expect(await screen.findByRole('heading', { name: 'How you are notified' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Preferences' })).toHaveAttribute('aria-current', 'page');
  });

  it('toggling enables Save, which sends every category and toasts', async () => {
    const { calls } = serve();
    renderAt('/notifications?tab=preferences');
    await userEvent.click(await sw('Overdue approvals', 'In-app'));
    await userEvent.click(await sw('Approvals waiting for you', 'Email'));
    expect(await sw('Overdue approvals', 'In-app')).toBeChecked();
    await userEvent.click(screen.getByRole('button', { name: 'Save preferences' }));

    expect(await screen.findByText('Notification preferences saved')).toBeInTheDocument();
    const put = calls.find((c) => c.method === 'PUT' && c.pathname === `${NOTES}/preferences`);
    expect(put).toBeDefined();
    expect(await put!.json()).toEqual({
      preferences: [
        { category: 'workflow_task', inApp: true, email: false },
        { category: 'workflow_outcome', inApp: true, email: false },
        { category: 'sla', inApp: true, email: true },
        { category: 'asset_lifecycle', inApp: true, email: false },
      ],
    });
    await waitFor(() => expect(screen.getByRole('button', { name: 'Save preferences' })).toBeDisabled());
    expect(await sw('Overdue approvals', 'In-app')).toBeChecked();
  });

  it('discard puts the stored values back', async () => {
    serve();
    renderAt('/notifications?tab=preferences');
    await userEvent.click(await sw('Overdue approvals', 'In-app'));
    await userEvent.click(screen.getByRole('button', { name: 'Discard changes' }));
    expect(await sw('Overdue approvals', 'In-app')).not.toBeChecked();
    expect(screen.getByRole('button', { name: 'Save preferences' })).toBeDisabled();
  });

  it('says so when saving fails and keeps the edits', async () => {
    mockApi((c) => {
      if (c.pathname === `${NOTES}/preferences`)
        return c.method === 'PUT' ? json({ title: 'Boom', status: 500 }, 500) : json(PREFS);
      return undefined;
    });
    renderAt('/notifications?tab=preferences');
    await userEvent.click(await sw('Overdue approvals', 'In-app'));
    await userEvent.click(screen.getByRole('button', { name: 'Save preferences' }));
    expect(await screen.findByText("Couldn't save your preferences")).toBeInTheDocument();
    expect(await sw('Overdue approvals', 'In-app')).toBeChecked();
  });

  it('explains calmly when email is not configured and disables the email switches (values still shown)', async () => {
    serve({ prefs: { ...PREFS, emailAvailable: false } });
    renderAt('/notifications?tab=preferences');
    expect(await screen.findByRole('note')).toHaveTextContent("Email delivery isn't set up for your organisation");
    const email = await sw('Approvals waiting for you', 'Email');
    expect(email).toBeDisabled();
    expect(email).toBeChecked();
    expect(await sw('Decisions on your assets', 'Email')).toBeDisabled();
    expect(await sw('Decisions on your assets', 'Email')).not.toBeChecked();
    expect(await sw('Approvals waiting for you', 'In-app')).toBeEnabled();
  });

  it('has no axe violations', async () => {
    serve({ prefs: { ...PREFS, emailAvailable: false } });
    const { container } = renderAt('/notifications?tab=preferences');
    await sw('Overdue approvals', 'In-app');
    expect(await axe(container)).toHaveNoViolations();
  });
});

describe('Hindi', () => {
  it('renders the bell, the inbox and the preferences in Hindi', async () => {
    setLanguage('hi');
    serve({ items: [note(), READ] });
    renderAt('/notifications');
    expect(await screen.findByRole('heading', { level: 1, name: 'सूचनाएँ' })).toBeInTheDocument();
    expect(await bell('सूचनाएँ, 1 अपठित')).toBeInTheDocument();
    expect(await screen.findByRole('button', { name: 'सभी पढ़ा हुआ चिह्नित करें' })).toBeInTheDocument();
    cleanup();

    serve({ prefs: { ...PREFS, emailAvailable: false } });
    renderAt('/notifications?tab=preferences');
    expect(await screen.findByText('आपको सूचना कैसे मिलती है')).toBeInTheDocument();
    expect(screen.getByText(/ईमेल डिलीवरी अभी सेट नहीं है/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'प्राथमिकताएँ सहेजें' })).toBeInTheDocument();
  });
});
