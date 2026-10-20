import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { queryClient } from '../../../App';
import { setLanguage } from '../../../i18n';
import type { AiReviewItem } from '../../translation/translationTypes';
import { json, mockApi, page, renderAt, task, type Call } from '../workflowTestKit';

beforeEach(() => {
  queryClient.clear();
  setLanguage('en');
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const EDITOR = ['assets.read', 'assets.update'];
const isReviews = (c: Call) => c.pathname === '/api/v1/ai/reviews' && c.method === 'GET';
const isTaskList = (c: Call) => c.pathname === '/api/v1/tasks' && c.method === 'GET';

const review = (over: Partial<AiReviewItem> = {}): AiReviewItem => ({
  assetId: 'a1',
  title: 'Acme spring hero',
  assetType: 'image',
  pending: 3,
  languages: ['hi', 'ta'],
  fields: ['headline', 'blurb'],
  requestedBy: 'Sam Author',
  oldestAt: new Date(Date.now() - 3 * 3_600_000).toISOString(),
  ...over,
});
const reviewPage = (items: AiReviewItem[], nextCursor: string | null = null) => json({ items, nextCursor });

describe('Tasks page: the Translations tab', () => {
  it('keeps the approval tasks as the default tab, unchanged', async () => {
    mockApi((c) => (isTaskList(c) ? json(page([task({ assetTitle: 'Acme logo' })])) : undefined), { permissions: EDITOR });
    renderAt('/tasks');
    expect(await screen.findByRole('link', { name: 'Acme logo' })).toBeInTheDocument();
    const tabs = await screen.findByRole('tablist', { name: 'Task lists' });
    expect(within(tabs).getByRole('tab', { name: 'Approvals' })).toHaveAttribute('aria-selected', 'true');
    expect(within(tabs).getByRole('tab', { name: 'Translations' })).toHaveAttribute('aria-selected', 'false');
  });

  it('has no tabs for someone who neither edits assets nor has translations waiting', async () => {
    mockApi((c) => (isTaskList(c) ? json(page([task({ assetTitle: 'Acme logo' })])) : undefined));
    renderAt('/tasks');
    await screen.findByRole('link', { name: 'Acme logo' });
    expect(screen.queryByRole('tablist')).not.toBeInTheDocument();
  });

  it('lists the assets with translations to review: title, type, count, languages, fields, requester and age', async () => {
    const calls = mockApi(
      (c) => {
        if (isTaskList(c)) return json(page([]));
        if (isReviews(c))
          return reviewPage([
            review(),
            review({
              assetId: 'a2',
              title: 'Acme manual',
              assetType: 'document',
              pending: 1,
              languages: ['mr'],
              fields: ['title'],
              requestedBy: null,
            }),
          ]);
        if (c.pathname === '/api/v1/ai/reviews/summary') return json({ translation: 2 });
        return undefined;
      },
      { permissions: EDITOR, thumbs: { a1: 'https://cdn.test/a1.webp' } },
    );
    renderAt('/tasks?tab=translations');
    const list = await screen.findByRole('list', { name: 'Assets with translations to review' });
    const rows = Array.from(list.querySelectorAll<HTMLElement>(':scope > li'));
    expect(rows).toHaveLength(2);
    expect(within(rows[0]!).getByRole('link', { name: 'Acme spring hero' })).toHaveAttribute(
      'href',
      '/library/assets/a1?focus=translations',
    );
    expect(rows[0]).toHaveTextContent('Image · headline, blurb');
    expect(rows[0]).toHaveTextContent('3 suggestions');
    expect(within(rows[0]!).getByRole('list', { name: 'Languages' })).toHaveTextContent('HITA');
    expect(rows[0]).toHaveTextContent('Requested by Sam Author');
    expect(rows[0]).toHaveTextContent('waiting 3 hours ago');
    expect(within(rows[0]!).getByRole('link', { name: 'Review the translations of Acme spring hero' })).toHaveAttribute(
      'href',
      '/library/assets/a1?focus=translations',
    );
    expect(rows[1]).toHaveTextContent('1 suggestion');
    expect(rows[1]).not.toHaveTextContent('Requested by');
    await waitFor(() => expect(rows[0]!.querySelector('img')).toHaveAttribute('src', 'https://cdn.test/a1.webp'));
    expect(calls.find(isReviews)!.search.get('kind')).toBe('translation');
    // The tab carries the count.
    expect(screen.getByRole('tab', { name: 'Translations (2)' })).toHaveAttribute('aria-selected', 'true');
  });

  it('sends a kind of file without an AI panel to its Metadata tab', async () => {
    mockApi(
      (c) =>
        isReviews(c)
          ? reviewPage([review({ assetType: 'archive', title: 'Acme bundle' })])
          : isTaskList(c)
            ? json(page([]))
            : undefined,
      {
        permissions: EDITOR,
      },
    );
    renderAt('/tasks?tab=translations');
    expect(await screen.findByRole('link', { name: 'Acme bundle' })).toHaveAttribute(
      'href',
      '/library/assets/a1?tab=metadata&focus=translations',
    );
  });

  it('opens from the tab and goes back to approvals', async () => {
    mockApi(
      (c) => {
        if (isTaskList(c)) return json(page([task({ assetTitle: 'Acme logo' })]));
        if (isReviews(c)) return reviewPage([review()]);
        return undefined;
      },
      { permissions: EDITOR },
    );
    renderAt('/tasks');
    await userEvent.click(await screen.findByRole('tab', { name: 'Translations' }));
    expect(await screen.findByRole('link', { name: 'Acme spring hero' })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Acme logo' })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('tab', { name: 'Approvals' }));
    expect(await screen.findByRole('link', { name: 'Acme logo' })).toBeInTheDocument();
  });

  it('shows the Translations tab to a reader who has something waiting', async () => {
    mockApi(
      (c) => {
        if (isTaskList(c)) return json(page([]));
        if (c.pathname === '/api/v1/ai/reviews/summary') return json({ translation: 4 });
        return undefined;
      },
      { permissions: ['assets.read'] },
    );
    renderAt('/tasks');
    expect(await screen.findByRole('tab', { name: 'Translations (4)' })).toBeInTheDocument();
  });

  it('says so when there is nothing to review', async () => {
    mockApi((c) => (isReviews(c) ? reviewPage([]) : isTaskList(c) ? json(page([])) : undefined), { permissions: EDITOR });
    renderAt('/tasks?tab=translations');
    expect(await screen.findByRole('heading', { name: 'No translations to review' })).toBeInTheDocument();
  });

  it('shows a loading state, then the list', async () => {
    let release: () => void = () => {};
    const gate = new Promise<void>((r) => (release = r));
    mockApi((c) => (isReviews(c) ? gate.then(() => reviewPage([review()])) : isTaskList(c) ? json(page([])) : undefined), {
      permissions: EDITOR,
    });
    renderAt('/tasks?tab=translations');
    expect(await screen.findByRole('status', { name: '' })).toHaveTextContent('Loading translations');
    release();
    expect(await screen.findByRole('link', { name: 'Acme spring hero' })).toBeInTheDocument();
  });

  it('shows an error with a retry that fetches again', async () => {
    let fail = true;
    mockApi(
      (c) => {
        if (isTaskList(c)) return json(page([]));
        if (!isReviews(c)) return undefined;
        return fail ? json({ title: 'Boom', status: 400 }, 400) : reviewPage([review()]);
      },
      { permissions: EDITOR },
    );
    renderAt('/tasks?tab=translations');
    expect(await screen.findByRole('alert')).toHaveTextContent("Couldn't load the translations.");
    fail = false;
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByRole('link', { name: 'Acme spring hero' })).toBeInTheDocument();
  });

  it('pages with Load more using the cursor', async () => {
    const calls = mockApi(
      (c) => {
        if (isTaskList(c)) return json(page([]));
        if (!isReviews(c)) return undefined;
        return c.search.get('cursor') === 'c2'
          ? reviewPage([review({ assetId: 'a2', title: 'Acme banner' })])
          : reviewPage([review()], 'c2');
      },
      { permissions: EDITOR },
    );
    renderAt('/tasks?tab=translations');
    await screen.findByRole('link', { name: 'Acme spring hero' });
    await userEvent.click(screen.getByRole('button', { name: 'Load more' }));
    expect(await screen.findByRole('link', { name: 'Acme banner' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument();
    expect(calls.filter(isReviews).some((c) => c.search.get('cursor') === 'c2')).toBe(true);
  });

  it('does not show a requester that is only an id', async () => {
    mockApi(
      (c) =>
        isReviews(c)
          ? reviewPage([review({ requestedBy: '0197a000-0000-7000-8000-000000000009' })])
          : isTaskList(c)
            ? json(page([]))
            : undefined,
      {
        permissions: EDITOR,
      },
    );
    renderAt('/tasks?tab=translations');
    const row = (await screen.findAllByRole('listitem')).find((r) => r.textContent?.includes('Acme spring hero'))!;
    expect(row).not.toHaveTextContent('Requested by');
  });

  it('has no axe violations', async () => {
    mockApi((c) => (isReviews(c) ? reviewPage([review()]) : isTaskList(c) ? json(page([])) : undefined), { permissions: EDITOR });
    const { container } = renderAt('/tasks?tab=translations');
    await screen.findByRole('link', { name: 'Acme spring hero' });
    expect(await axe(container)).toHaveNoViolations();
  });

  it('renders in Hindi', async () => {
    setLanguage('hi');
    mockApi((c) => (isReviews(c) ? reviewPage([review()]) : isTaskList(c) ? json(page([])) : undefined), { permissions: EDITOR });
    renderAt('/tasks?tab=translations');
    expect(await screen.findByRole('tab', { name: 'अनुवाद' })).toHaveAttribute('aria-selected', 'true');
    expect(await screen.findByText('3 सुझाव')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Acme spring hero के अनुवादों की समीक्षा करें' })).toBeInTheDocument();
  });
});
