import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { queryClient } from '../../../App';
import { setLanguage } from '../../../i18n';
import { hoursFromNow, json, mockApi, page, renderAt, summary, task, type Call } from '../workflowTestKit';

beforeEach(() => {
  queryClient.clear();
  setLanguage('en');
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const isTaskList = (c: Call) => c.pathname === '/api/v1/tasks' && c.method === 'GET';

const NO_DEADLINE = task({ id: 't-none', assetId: 'a-none', assetTitle: 'Acme brochure', createdAt: '2026-03-01T00:00:00Z' });
const SOON = task({ id: 't-soon', assetId: 'a-soon', assetTitle: 'Acme banner', dueAt: hoursFromNow(5) });
const LATE = task({
  id: 't-late',
  assetId: 'a-late',
  assetTitle: 'Acme logo',
  dueAt: hoursFromNow(-2),
  slaBreached: true,
  escalated: true,
});

describe('Tasks inbox - pending', () => {
  it('lists the work waiting for me: overdue first, then due soonest, then no deadline, with badges and links', async () => {
    mockApi((c) => (isTaskList(c) ? json(page([NO_DEADLINE, SOON, LATE])) : undefined), {
      summary: { pendingForMe: 3, overdueForMe: 1 },
    });
    renderAt('/tasks');

    expect(await screen.findByRole('heading', { name: 'Tasks' })).toBeInTheDocument();
    const list = await screen.findByRole('list', { name: 'Pending tasks' });
    const rows = within(list).getAllByRole('listitem');
    expect(rows.map((r) => within(r).getByRole('link').textContent)).toEqual(['Acme logo', 'Acme banner', 'Acme brochure']);

    expect(within(rows[0]!).getByText('Overdue by 2 hours')).toBeInTheDocument();
    expect(within(rows[0]!).getByText('Escalated')).toBeInTheDocument();
    expect(within(rows[1]!).getByText('Due in 5 hours')).toBeInTheDocument();
    expect(within(rows[2]!).getByText('No deadline')).toBeInTheDocument();
    expect(within(rows[0]!).getByText('Two-level approval · Review')).toBeInTheDocument();
    expect(within(rows[0]!).getByRole('link', { name: 'Acme logo' })).toHaveAttribute('href', '/library/assets/a-late');
    expect(within(rows[0]!).getByRole('button', { name: 'Approve' })).toBeInTheDocument();
    expect(within(rows[0]!).getByRole('button', { name: 'Reject' })).toBeInTheDocument();
    expect(await screen.findByText('3 tasks waiting for you · 1 overdue')).toBeInTheDocument();
  });

  it('treats a task past its due time as overdue even before the server has flagged the breach', async () => {
    mockApi((c) => (isTaskList(c) ? json(page([task({ dueAt: hoursFromNow(-3), slaBreached: false })])) : undefined));
    renderAt('/tasks');
    expect(await screen.findByText('Overdue by 3 hours')).toBeInTheDocument();
  });

  it('shows the asset thumbnail from the batch links', async () => {
    mockApi((c) => (isTaskList(c) ? json(page([SOON])) : undefined), { thumbs: { 'a-soon': 'https://cdn.test/soon.webp' } });
    const { container } = renderAt('/tasks');
    await screen.findByRole('link', { name: 'Acme banner' });
    await waitFor(() =>
      expect(container.querySelector('img.dam-taskrow__thumb')).toHaveAttribute('src', 'https://cdn.test/soon.webp'),
    );
  });

  it('says so when nothing is waiting', async () => {
    mockApi((c) => (isTaskList(c) ? json(page([])) : undefined));
    renderAt('/tasks');
    expect(await screen.findByRole('heading', { name: "You're all caught up" })).toBeInTheDocument();
  });

  it('shows an error note with a retry that fetches again', async () => {
    let fail = true;
    mockApi((c) => {
      if (!isTaskList(c)) return undefined;
      return fail ? json({ title: 'Boom', status: 400 }, 400) : json(page([SOON]));
    });
    renderAt('/tasks');
    expect(await screen.findByRole('alert')).toHaveTextContent("Couldn't load tasks.");
    fail = false;
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByRole('link', { name: 'Acme banner' })).toBeInTheDocument();
  });

  it('pages: Load more appends the next page using the cursor', async () => {
    const calls = mockApi((c) => {
      if (!isTaskList(c)) return undefined;
      return c.search.get('cursor') === 'c2' ? json(page([NO_DEADLINE])) : json(page([SOON], 'c2'));
    });
    renderAt('/tasks');
    await screen.findByRole('link', { name: 'Acme banner' });
    await userEvent.click(screen.getByRole('button', { name: 'Load more' }));
    expect(await screen.findByRole('link', { name: 'Acme brochure' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument();
    expect(calls.filter(isTaskList).some((c) => c.search.get('cursor') === 'c2')).toBe(true);
  });

  it('has no axe violations', async () => {
    mockApi((c) => (isTaskList(c) ? json(page([LATE, SOON, NO_DEADLINE])) : undefined), {
      summary: { pendingForMe: 3, overdueForMe: 1 },
    });
    const { container } = renderAt('/tasks');
    await screen.findByRole('list', { name: 'Pending tasks' });
    expect(await axe(container)).toHaveNoViolations();
  });
});

describe('Tasks inbox - deciding', () => {
  it('approves with one click, sends no comment, toasts, and refreshes the list and the nav count', async () => {
    let decided = false;
    let body: unknown;
    const calls = mockApi((c) => {
      if (isTaskList(c)) return json(page(decided ? [] : [SOON]));
      if (c.pathname === '/api/v1/tasks/summary') return json(summary({ pendingForMe: decided ? 0 : 1 }));
      if (c.pathname === '/api/v1/tasks/t-soon/decision' && c.method === 'POST')
        return (async () => {
          body = await c.json();
          decided = true;
          return json({ ...SOON, status: 'approved', decision: 'approve' });
        })();
      return undefined;
    });
    renderAt('/tasks');
    const nav = await screen.findByRole('link', { name: 'Tasks, 1 pending' });
    await userEvent.click(await screen.findByRole('button', { name: 'Approve' }));

    expect(await screen.findByText('Approved “Acme banner”')).toBeInTheDocument();
    expect(body).toEqual({ decision: 'approve', comment: null });
    expect(await screen.findByRole('heading', { name: "You're all caught up" })).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole('link', { name: /Tasks, \d+ pending/ })).not.toBeInTheDocument());
    expect(nav).toBeDefined();
    expect(calls.filter((c) => c.pathname === '/api/v1/tasks/summary').length).toBeGreaterThan(1);
  });

  it('approve with a comment sends the comment', async () => {
    let body: unknown;
    mockApi((c) => {
      if (isTaskList(c)) return json(page([SOON]));
      if (c.pathname === '/api/v1/tasks/t-soon/decision')
        return (async () => {
          body = await c.json();
          return json({ ...SOON, status: 'approved' });
        })();
      return undefined;
    });
    renderAt('/tasks');
    await userEvent.click(await screen.findByRole('button', { name: 'Approve with a comment' }));
    const dialog = await screen.findByRole('dialog', { name: 'Approve “Acme banner”' });
    await userEvent.type(within(dialog).getByLabelText('Comment (optional)'), 'Looks good');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Approve' }));
    await waitFor(() => expect(body).toEqual({ decision: 'approve', comment: 'Looks good' }));
  });

  it('reject needs a comment: the dialog refuses an empty one, then sends it', async () => {
    const posts: unknown[] = [];
    mockApi((c) => {
      if (isTaskList(c)) return json(page([SOON]));
      if (c.pathname === '/api/v1/tasks/t-soon/decision')
        return (async () => {
          posts.push(await c.json());
          return json({ ...SOON, status: 'rejected' });
        })();
      return undefined;
    });
    renderAt('/tasks');
    await userEvent.click(await screen.findByRole('button', { name: 'Reject' }));
    const dialog = await screen.findByRole('dialog', { name: 'Reject “Acme banner”' });

    await userEvent.click(within(dialog).getByRole('button', { name: 'Reject asset' }));
    expect(await within(dialog).findByText('Say why you are rejecting this asset.')).toBeInTheDocument();
    expect(posts).toHaveLength(0);

    await userEvent.type(within(dialog).getByLabelText('Reason for rejecting'), 'Wrong logo version');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Reject asset' }));
    await waitFor(() => expect(posts).toEqual([{ decision: 'reject', comment: 'Wrong logo version' }]));
    expect(await screen.findByText('Rejected “Acme banner”')).toBeInTheDocument();
  });

  it('reports a refused decision (someone else decided first) and refreshes the list', async () => {
    let lists = 0;
    mockApi((c) => {
      if (isTaskList(c)) {
        lists++;
        return json(page(lists === 1 ? [SOON] : []));
      }
      if (c.pathname === '/api/v1/tasks/t-soon/decision')
        return json({ title: 'Conflict', status: 409, detail: 'This task has already been decided.' }, 409);
      return undefined;
    });
    renderAt('/tasks');
    await userEvent.click(await screen.findByRole('button', { name: 'Approve' }));
    expect(await screen.findByText("Couldn't record your decision")).toBeInTheDocument();
    expect(screen.getByText('This task has already been decided.')).toBeInTheDocument();
    expect(await screen.findByRole('heading', { name: "You're all caught up" })).toBeInTheDocument();
  });
});

describe('Tasks inbox - decided and everyone', () => {
  const DECIDED = task({
    id: 'd1',
    assetTitle: 'Acme poster',
    status: 'rejected',
    decision: 'reject',
    decidedByName: 'Dev Admin',
    decidedAt: new Date(Date.now() - 2 * 3_600_000).toISOString(),
    comment: 'Wrong logo version',
  });
  const APPROVED = task({
    id: 'd2',
    assetTitle: 'Acme flyer',
    status: 'approved',
    decision: 'approve',
    decidedByName: 'Dev Admin',
    decidedAt: new Date(Date.now() - 5 * 3_600_000).toISOString(),
  });

  it('shows outcome, who, when and the comment for what I decided, newest decision first', async () => {
    const calls = mockApi((c) => (isTaskList(c) ? json(page([APPROVED, DECIDED])) : undefined));
    renderAt('/tasks?view=decided');
    const list = await screen.findByRole('list', { name: 'Decided tasks' });
    const rows = within(list).getAllByRole('listitem');
    expect(rows.map((r) => within(r).getByRole('link').textContent)).toEqual(['Acme poster', 'Acme flyer']);
    expect(within(rows[0]!).getByText('Rejected')).toBeInTheDocument();
    expect(within(rows[0]!).getByText(/by Dev Admin/)).toBeInTheDocument();
    expect(within(rows[0]!).getByText('2 hours ago')).toBeInTheDocument();
    expect(within(rows[0]!).getByText('Wrong logo version')).toBeInTheDocument();
    expect(within(rows[1]!).getByText('Approved')).toBeInTheDocument();
    expect(within(list).queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument();
    expect(calls.find(isTaskList)?.search.get('status')).toBe('decided');
  });

  it('switches between Pending and Decided with the segmented control', async () => {
    const calls = mockApi((c) =>
      isTaskList(c) ? json(page(c.search.get('status') === 'decided' ? [DECIDED] : [SOON])) : undefined,
    );
    renderAt('/tasks');
    await screen.findByRole('link', { name: 'Acme banner' });
    await userEvent.click(screen.getByRole('button', { name: 'Decided' }));
    expect(await screen.findByRole('link', { name: 'Acme poster' })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Pending' }));
    expect(await screen.findByRole('link', { name: 'Acme banner' })).toBeInTheDocument();
    expect(calls.filter(isTaskList).map((c) => c.search.get('status'))).toEqual(expect.arrayContaining(['pending', 'decided']));
  });

  it('has an empty state for no decisions yet', async () => {
    mockApi((c) => (isTaskList(c) ? json(page([])) : undefined));
    renderAt('/tasks?view=decided');
    expect(await screen.findByRole('heading', { name: 'No decisions yet' })).toBeInTheDocument();
  });

  it('hides the Mine / Everyone switch from people without workflow.manage', async () => {
    const calls = mockApi((c) => (isTaskList(c) ? json(page([SOON])) : undefined), {
      permissions: ['assets.read', 'assets.approve'],
    });
    renderAt('/tasks?scope=all');
    await screen.findByRole('link', { name: 'Acme banner' });
    await screen.findAllByText(/^Tenant 0197/); // /me has loaded
    expect(screen.queryByRole('group', { name: 'Whose tasks' })).not.toBeInTheDocument();
    // ?scope=all is ignored for them: every request still asks for their own tasks.
    expect(calls.filter(isTaskList).every((c) => c.search.get('assignee') === 'me')).toBe(true);
  });

  it('lets a manager look at everyone: requests assignee=all, shows who it is assigned to, offers no decisions', async () => {
    const calls = mockApi(
      (c) =>
        isTaskList(c)
          ? json(
              page([
                task({ id: 'x1', assetTitle: 'Acme deck', assigneeRoles: ['Approver', 'Librarian'], dueAt: hoursFromNow(30) }),
              ]),
            )
          : undefined,
      { permissions: ['assets.read', 'workflow.manage'] },
    );
    renderAt('/tasks');
    await screen.findByRole('link', { name: 'Acme deck' });
    expect(await screen.findByRole('group', { name: 'Whose tasks' })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Everyone' }));
    expect(await screen.findByText('Assigned to Approver, Librarian')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument();
    expect(calls.filter(isTaskList).some((c) => c.search.get('assignee') === 'all')).toBe(true);
  });
});

describe('Tasks inbox - Hindi', () => {
  it('renders the inbox, its tabs, the due badges and the decision dialog in Hindi', async () => {
    setLanguage('hi');
    mockApi((c) => (isTaskList(c) ? json(page([SOON, LATE])) : undefined), { summary: { pendingForMe: 2, overdueForMe: 1 } });
    renderAt('/tasks');

    expect(await screen.findByRole('heading', { level: 1, name: 'कार्य' })).toBeInTheDocument();
    const list = await screen.findByRole('list', { name: 'लंबित कार्य' });
    const rows = within(list).getAllByRole('listitem');
    expect(within(rows[0]!).getByText('आगे बढ़ाया गया')).toBeInTheDocument();
    expect(within(rows[0]!).getByRole('button', { name: 'स्वीकृत करें' })).toBeInTheDocument();
    expect(within(rows[0]!).getByRole('button', { name: 'अस्वीकार करें' })).toBeInTheDocument();
    expect(await screen.findByText('2 कार्य आपकी प्रतीक्षा में · 1 देर से')).toBeInTheDocument();
    expect(screen.queryByText('Escalated')).not.toBeInTheDocument();

    await userEvent.click(within(rows[1]!).getByRole('button', { name: 'अस्वीकार करें' }));
    const dialog = await screen.findByRole('dialog', { name: '“Acme banner” अस्वीकार करें' });
    await userEvent.click(within(dialog).getByRole('button', { name: 'एसेट अस्वीकार करें' }));
    expect(await within(dialog).findByText('बताएँ कि आप इस एसेट को क्यों अस्वीकार कर रहे हैं।')).toBeInTheDocument();
  });
});
