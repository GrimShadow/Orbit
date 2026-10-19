import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { setLanguage } from '../../../i18n';
import { json, mockApi } from '../../workflow/workflowTestKit';
import { activity, ago, renderPanel } from './commentsTestKit';
import { ActivityTab } from './ActivityTab';
import type { ActivityItem } from '../commentsApi';

beforeEach(() => setLanguage('en'));
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const FEED: ActivityItem[] = [
  activity({
    id: 'e1',
    kind: 'comment',
    summary: 'added an annotation',
    commentId: 'c1',
    versionNo: 2,
    detail: 'Crop @Sam Reviewer out of this',
    occurredAt: ago(5),
  }),
  activity({ id: 'e2', kind: 'resolved', summary: 'resolved a comment thread', commentId: 'c0', occurredAt: ago(50) }),
  activity({
    id: 'e3',
    kind: 'status_changed',
    actorName: 'Sam Reviewer',
    summary: 'changed the status to in review',
    detail: 'from draft',
    occurredAt: ago(300),
  }),
  activity({ id: 'e4', kind: 'version_added', summary: 'uploaded version 2', occurredAt: ago(1500) }),
  activity({
    id: 'e5',
    kind: 'workflow_decision',
    actorName: 'Sam Reviewer',
    summary: 'approved at the Review step',
    detail: 'Looks good',
    occurredAt: ago(1600),
  }),
  activity({
    id: 'e6',
    kind: 'created',
    actorName: 'System',
    actorId: null,
    summary: 'created this asset',
    occurredAt: ago(4000),
  }),
];

function serve(pages: Record<string, { items: ActivityItem[]; nextCursor: string | null }>, fail = false) {
  return mockApi((c) => {
    if (c.pathname !== '/api/v1/assets/a1/activity') return undefined;
    if (fail) return json({ title: 'Boom', status: 500 }, 500);
    return json(pages[c.search.get('cursor') ?? ''] ?? { items: [], nextCursor: null });
  });
}

describe('Activity tab', () => {
  it('lists what happened, newest first, as "<actor> <summary>" with detail and relative time', async () => {
    serve({ '': { items: FEED, nextCursor: null } });
    renderPanel(<ActivityTab assetId="a1" />);
    const list = await screen.findByRole('list', { name: 'Activity' });
    const rows = within(list).getAllByRole('listitem');
    expect(rows).toHaveLength(6);
    expect(rows[0]).toHaveTextContent('Priya Natarajan added an annotation');
    expect(rows[0]).toHaveTextContent('Crop @Sam Reviewer out of this'); // token-free excerpt
    expect(rows[0]).toHaveTextContent('5 minutes ago on v2');
    expect(rows[2]).toHaveTextContent('Sam Reviewer changed the status to in review');
    expect(rows[2]).toHaveTextContent('from draft');
    expect(rows[3]).toHaveTextContent('uploaded version 2');
    expect(rows[4]).toHaveTextContent('Looks good');
    expect(rows[5]).toHaveTextContent('System created this asset');
    expect(rows.map((r) => r.getAttribute('data-kind'))).toEqual([
      'comment',
      'resolved',
      'status_changed',
      'version_added',
      'workflow_decision',
      'created',
    ]);
    const time = within(rows[1]!).getByText('50 minutes ago');
    expect(time).toHaveAttribute('title', new Date(FEED[1]!.occurredAt).toLocaleString('en'));
  });

  it('copes with a kind it does not know', async () => {
    serve({ '': { items: [activity({ id: 'x', kind: 'something_new', summary: 'did a new thing' })], nextCursor: null } });
    renderPanel(<ActivityTab assetId="a1" />);
    expect(await screen.findByText('did a new thing')).toBeInTheDocument();
  });

  it('opens the thread from a comment item (Comments tab + comment in the URL) but not from other items', async () => {
    serve({ '': { items: FEED, nextCursor: null } });
    renderPanel(<ActivityTab assetId="a1" />);
    const user = userEvent.setup();
    const list = await screen.findByRole('list', { name: 'Activity' });
    expect(within(list).getAllByRole('button')).toHaveLength(2); // the comment and the resolution
    await user.click(screen.getByRole('button', { name: /added an annotation/ }));
    const where = screen.getByTestId('where').textContent;
    expect(where).toContain('tab=comments');
    expect(where).toContain('comment=c1');
  });

  it('loads more with the cursor', async () => {
    const calls = serve({
      '': { items: FEED.slice(0, 2), nextCursor: 'cur1' },
      cur1: { items: FEED.slice(2, 4), nextCursor: null },
    });
    renderPanel(<ActivityTab assetId="a1" />);
    const user = userEvent.setup();
    await screen.findByText('resolved a comment thread');
    await user.click(screen.getByRole('button', { name: 'Load more' }));
    expect(await screen.findByText('uploaded version 2')).toBeInTheDocument();
    expect(calls.some((c) => c.search.get('cursor') === 'cur1')).toBe(true);
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Load more' })).toBeNull());
    expect(screen.getAllByRole('listitem')).toHaveLength(4);
  });

  it('shows a skeleton, then an empty state', async () => {
    serve({ '': { items: [], nextCursor: null } });
    renderPanel(<ActivityTab assetId="a1" />);
    expect(screen.getByText('Loading activity')).toBeInTheDocument();
    expect(await screen.findByText('Nothing has happened yet')).toBeInTheDocument();
  });

  it('shows an error with a retry', async () => {
    serve({}, true);
    renderPanel(<ActivityTab assetId="a1" />);
    expect(await screen.findByText('Activity could not be loaded.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    serve({ '': { items: FEED, nextCursor: 'more' } });
    const { container } = renderPanel(<ActivityTab assetId="a1" />);
    await screen.findByText('uploaded version 2');
    expect(await axe(container)).toHaveNoViolations();
  });
});
