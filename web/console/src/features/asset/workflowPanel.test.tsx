import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { queryClient } from '../../App';
import { setLanguage } from '../../i18n';
import { hoursFromNow, instance, json, mockApi, page, renderAt, step, task, type Call } from '../workflow/workflowTestKit';
import type { WorkflowInstanceView } from '../workflow/workflowApi';

beforeEach(() => {
  queryClient.clear();
  setLanguage('en');
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const asset = (status: string, extra: Record<string, unknown> = {}) => ({
  id: 'a1',
  folderId: null,
  type: 'image',
  status,
  title: 'Acme spring hero',
  metadata: {},
  metadataSchemaVersion: 1,
  taxonomyTerms: [],
  tags: [],
  attributes: {},
  ownerId: null,
  currentVersionId: 'v1',
  checksumSha256: null,
  statusReason: '',
  publishAt: null,
  expireAt: null,
  retentionPolicyId: null,
  legalHold: false,
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-02T00:00:00Z',
  ...extra,
});

/** The asset page's own endpoints, plus whatever the test scripts (workflow view, my pending tasks, a decision). */
function handlers(
  status: string,
  workflow: NonNullable<WorkflowInstanceView> | null | 'error',
  mine: ReturnType<typeof task>[] = [],
  more: (c: Call) => Response | Promise<Response> | undefined = () => undefined,
) {
  return (c: Call): Response | Promise<Response> | undefined => {
    const custom = more(c);
    if (custom) return custom;
    if (c.pathname === '/api/v1/assets/a1' && c.method === 'GET') return json(asset(status));
    if (c.pathname === '/api/v1/schemas/image')
      return json({
        assetType: 'image',
        version: 1,
        isCurrent: true,
        changeNote: '',
        createdAt: '2026-01-01T00:00:00Z',
        fields: [],
      });
    if (c.pathname === '/api/v1/assets/a1/versions') return json([]);
    if (c.pathname === '/api/v1/assets/a1/workflow')
      return workflow === 'error' ? json({ title: 'Boom', status: 500 }, 500) : json({ instance: workflow });
    if (c.pathname === '/api/v1/tasks' && c.method === 'GET') return json(page(mine));
    return undefined;
  };
}

const lifecycle = () => screen.findByRole('group', { name: 'Lifecycle actions' });

const RUNNING = () =>
  instance({
    steps: [
      step({
        id: 'review',
        name: 'Review',
        state: 'done',
        slaHours: 48,
        tasks: [
          task({
            id: 'done1',
            status: 'approved',
            decision: 'approve',
            decidedByName: 'Alex Reviewer',
            decidedAt: new Date(Date.now() - 2 * 3_600_000).toISOString(),
            comment: 'Colours check out',
          }),
        ],
      }),
      step({
        id: 'final',
        name: 'Final approval',
        state: 'active',
        slaHours: 24,
        tasks: [
          task({
            id: 'open1',
            stepId: 'final',
            stepName: 'Final approval',
            assigneeRoles: ['Librarian'],
            dueAt: hoursFromNow(5),
          }),
        ],
      }),
      step({ id: 'legal', name: 'Legal sign-off', state: 'waiting', mode: 'all' }),
      step({ id: 'extra', name: 'Regional check', state: 'skipped' }),
    ],
  });

describe('Approval panel on the asset page', () => {
  it('shows the running workflow as a stepper and swaps Approve / Reject for an "awaiting" note', async () => {
    mockApi(handlers('in_review', RUNNING()));
    renderAt('/library/assets/a1');

    const steps = await screen.findByRole('list', { name: 'Approval steps' });
    const items = within(steps)
      .getAllByRole('listitem')
      .filter((li) => li.classList.contains('dam-apv-step'));
    expect(items.map((li) => li.className.match(/dam-apv-step--(\w+)/)?.[1])).toEqual(['done', 'active', 'waiting', 'skipped']);
    expect(items[1]).toHaveAttribute('aria-current', 'step');

    expect(within(items[0]!).getByText('Done')).toBeInTheDocument();
    expect(within(items[0]!).getByText(/by Alex Reviewer/)).toBeInTheDocument();
    expect(within(items[0]!).getByText('Colours check out')).toBeInTheDocument();
    expect(within(items[0]!).getByText(/48 hours to decide/)).toBeInTheDocument();

    expect(within(items[1]!).getByText('Librarian')).toBeInTheDocument();
    expect(within(items[1]!).getByText('Due in 5 hours')).toBeInTheDocument();
    expect(within(items[2]!).getByText('Everyone must approve')).toBeInTheDocument();
    expect(within(items[3]!).getByText('Skipped')).toBeInTheDocument();
    expect(screen.getByText('Workflow: Two-level approval · Submitted by Sam Author 3 hours ago')).toBeInTheDocument();

    const actions = await lifecycle();
    expect(within(actions).getByText('Awaiting approval: Final approval')).toBeInTheDocument();
    expect(within(actions).queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument();
    expect(within(actions).queryByRole('button', { name: 'Reject' })).not.toBeInTheDocument();
    // Withdrawing is still the owner's way out.
    await userEvent.click(within(actions).getByRole('button', { name: 'Actions' }));
    const menu = await screen.findByRole('menu');
    expect(within(menu).getByRole('menuitem', { name: 'Withdraw' })).toBeInTheDocument();
    expect(within(menu).queryByRole('menuitem', { name: 'Approve' })).not.toBeInTheDocument();
    expect(within(menu).queryByRole('menuitem', { name: 'Reject' })).not.toBeInTheDocument();
  });

  it('offers no decision on the step to someone who is not one of its deciders', async () => {
    mockApi(handlers('in_review', RUNNING(), []));
    renderAt('/library/assets/a1');
    await screen.findByRole('list', { name: 'Approval steps' });
    expect(screen.queryByText('Your decision is needed here.')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument();
  });

  it('puts Approve / Reject on the active step when my inbox holds that task, and approving posts the decision', async () => {
    let body: unknown;
    const open = RUNNING().steps[1]!.tasks[0]!;
    mockApi(
      handlers('in_review', RUNNING(), [open, task({ id: 'other', assetId: 'a2' })], (c) => {
        if (c.pathname === '/api/v1/tasks/open1/decision')
          return (async () => {
            body = await c.json();
            return json({ ...open, status: 'approved', decision: 'approve' });
          })();
        return undefined;
      }),
    );
    renderAt('/library/assets/a1');
    const mine = await screen.findByText('Your decision is needed here.');
    const group = within(mine.parentElement!).getByRole('group', { name: 'Decision for Acme spring hero' });
    expect(within(group).getByRole('button', { name: 'Reject' })).toBeInTheDocument();
    // Only the one task for THIS asset counts: the other asset's task adds nothing.
    expect(screen.getAllByRole('button', { name: 'Approve' })).toHaveLength(1);

    await userEvent.click(within(group).getByRole('button', { name: 'Approve' }));
    await waitFor(() => expect(body).toEqual({ decision: 'approve', comment: null }));
    expect(await screen.findByText('Approved “Acme spring hero”')).toBeInTheDocument();
  });

  it("shows a rejected workflow with the reviewer's reason, on the draft it sent the asset back to", async () => {
    mockApi(
      handlers(
        'draft',
        instance({
          status: 'rejected',
          completedAt: new Date(Date.now() - 3_600_000).toISOString(),
          outcomeReason: 'Wrong logo version',
          steps: [
            step({
              state: 'done',
              tasks: [
                task({
                  status: 'rejected',
                  decision: 'reject',
                  decidedByName: 'Alex Reviewer',
                  decidedAt: new Date(Date.now() - 3_600_000).toISOString(),
                  comment: 'Wrong logo version',
                }),
              ],
            }),
            step({ id: 'final', name: 'Final approval', state: 'skipped' }),
          ],
        }),
      ),
    );
    renderAt('/library/assets/a1');
    const note = await screen.findByRole('note');
    expect(within(note).getByText('Rejected')).toBeInTheDocument();
    // The reviewer's comment is the reason: said once, on the step, not repeated in the banner.
    expect(within(note).queryByText('Wrong logo version')).not.toBeInTheDocument();
    expect(screen.getAllByText('Wrong logo version')).toHaveLength(1);
    const steps = screen.getByRole('list', { name: 'Approval steps' });
    expect(within(steps).getByText('Final approval')).toBeInTheDocument();
    expect(within(steps).getByText('Skipped')).toBeInTheDocument();
    // A finished workflow leaves the ordinary actions alone: the draft can be submitted again.
    const actions = await lifecycle();
    expect(within(actions).getByRole('button', { name: 'Submit for review' })).toBeInTheDocument();
    expect(within(actions).queryByText(/Awaiting approval/)).not.toBeInTheDocument();
  });

  it('shows an approved workflow outcome', async () => {
    mockApi(
      handlers(
        'approved',
        instance({
          status: 'approved',
          completedAt: new Date(Date.now() - 2 * 3_600_000).toISOString(),
          outcomeReason: 'Approved by workflow',
          steps: [step({ state: 'done' })],
        }),
      ),
    );
    renderAt('/library/assets/a1');
    const note = await screen.findByRole('note');
    expect(within(note).getByText('Approved')).toBeInTheDocument();
    expect(within(note).getByText('2 hours ago')).toBeInTheDocument();
    expect(within(note).getByText('Approved by workflow')).toBeInTheDocument(); // a reason no task carries stays in the banner
    expect(within(await lifecycle()).getByRole('button', { name: 'Publish' })).toBeInTheDocument();
  });

  it('shows a withdrawn workflow', async () => {
    mockApi(
      handlers(
        'draft',
        instance({ status: 'cancelled', completedAt: new Date().toISOString(), steps: [step({ state: 'waiting' })] }),
      ),
    );
    renderAt('/library/assets/a1');
    expect(within(await screen.findByRole('note')).getByText('Withdrawn')).toBeInTheDocument();
  });

  it('shows nothing, and keeps the direct Approve / Reject, for an asset no workflow applies to', async () => {
    mockApi(handlers('in_review', null));
    renderAt('/library/assets/a1');
    const actions = await lifecycle();
    expect(await within(actions).findByRole('button', { name: 'Approve' })).toBeInTheDocument();
    expect(screen.queryByText('Approval')).not.toBeInTheDocument();
    expect(screen.queryByRole('list', { name: 'Approval steps' })).not.toBeInTheDocument();
  });

  it('stays silent when the workflow cannot be loaded', async () => {
    mockApi(handlers('in_review', 'error'));
    renderAt('/library/assets/a1');
    const actions = await lifecycle();
    expect(await within(actions).findByRole('button', { name: 'Approve' })).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.queryByText('Approval')).not.toBeInTheDocument();
  });

  it('has no axe violations while a workflow runs', async () => {
    mockApi(handlers('in_review', RUNNING(), [RUNNING().steps[1]!.tasks[0]!]));
    const { container } = renderAt('/library/assets/a1');
    await screen.findByText('Your decision is needed here.');
    expect(await axe(container)).toHaveNoViolations();
  });
});
