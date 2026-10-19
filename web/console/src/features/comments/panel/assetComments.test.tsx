import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { queryClient } from '../../../App';
import { setLanguage } from '../../../i18n';
import { json, mockApi, renderAt } from '../../workflow/workflowTestKit';
import { ago, comment, serveComments, thread } from './commentsTestKit';

beforeEach(() => {
  queryClient.clear();
  setLanguage('en');
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const ASSET = {
  id: 'a1',
  folderId: null,
  type: 'image',
  status: 'draft',
  title: 'Acme spring hero',
  metadata: {},
  metadataSchemaVersion: 1,
  taxonomyTerms: [],
  tags: [],
  attributes: {},
  ownerId: null,
  currentVersionId: 'v2',
  checksumSha256: null,
  statusReason: '',
  publishAt: null,
  expireAt: null,
  retentionPolicyId: null,
  legalHold: false,
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-02T00:00:00Z',
};

function page(
  threads = [
    thread(comment({ id: 'r1' })),
    thread(comment({ id: 'r2', body: 'Resolved note', resolved: true, resolvedByName: 'Sam', resolvedAt: ago(2) })),
  ],
) {
  const { handler } = serveComments(threads);
  mockApi(
    (c) => {
      const custom = handler(c);
      if (custom) return custom;
      if (c.pathname === '/api/v1/assets/a1' && c.method === 'GET') return json(ASSET);
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
      if (c.pathname === '/api/v1/assets/a1/workflow') return json({ instance: null });
      if (c.pathname === '/api/v1/tasks' && c.method === 'GET') return json({ items: [], nextCursor: null });
      if (c.pathname === '/api/v1/assets/a1/activity') return json({ items: [], nextCursor: null });
      return undefined;
    },
    { permissions: ['assets.read', 'comments.write'] },
  );
}

describe('Asset page tabs', () => {
  it('shows the open thread count on the Comments tab and opens it from ?tab=comments', async () => {
    page();
    renderAt('/library/assets/a1?tab=comments');
    const tab = await screen.findByRole('tab', { name: 'Comments (1)' });
    await waitFor(() => expect(tab).toHaveAttribute('aria-selected', 'true'));
    expect(await screen.findByRole('textbox', { name: 'Add a comment' })).toBeInTheDocument();
  });

  it('opens the Comments tab on ?comment=<id> and lights that thread', async () => {
    page();
    renderAt('/library/assets/a1?comment=r2');
    expect(await screen.findByRole('tab', { name: /^Comments/ })).toHaveAttribute('aria-selected', 'true');
    const resolved = (await screen.findByText('Resolved note')).closest('article')!;
    await waitFor(() => expect(resolved).toHaveAttribute('data-selected', 'true'));
  });

  it('defaults to Details and switches tabs through the URL-backed Tabs', async () => {
    page();
    renderAt('/library/assets/a1');
    const user = userEvent.setup();
    const details = await screen.findByRole('tab', { name: 'Details' });
    expect(details).toHaveAttribute('aria-selected', 'true');
    await user.click(screen.getByRole('tab', { name: 'Activity' }));
    expect(await screen.findByText('Nothing has happened yet')).toBeInTheDocument();
    await user.click(screen.getByRole('tab', { name: 'Comments (1)' }));
    expect(
      await within(await screen.findByRole('tabpanel', { name: /Comments/ })).findByText('Please brighten the sky.'),
    ).toBeInTheDocument();
  });
});
