import { ToastProvider } from '@dam/ui-kit';
import { QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { axe } from 'vitest-axe';
import { queryClient } from '../../App';
import { AuthContext } from '../../auth/AuthProvider';
import { setLanguage } from '../../i18n';
import { assetDto, backend, newWorld as newAssetWorld } from '../translation/translationTestKit';
import { renderAt, signedIn } from '../workflow/workflowTestKit';
import { KnowledgeCard } from './KnowledgeCard';
import { CHAPTERS, EDITOR, MANAGER, READER, callsTo, doc, install, newWorld, problem, type World } from './knowledgeTestKit';

const LIST = '/api/v1/knowledge/documents';
const ASSET = { id: 'a1', title: 'Acme Lumen owner manual', type: 'manual' };

/** A stand-in for IntersectionObserver that only reports "near the screen" when the test says so. */
class FakeObserver {
  static all: FakeObserver[] = [];
  constructor(private cb: IntersectionObserverCallback) {
    FakeObserver.all.push(this);
  }
  observe() {}
  unobserve() {}
  disconnect() {}
  takeRecords() {
    return [];
  }
  show() {
    this.cb([{ isIntersecting: true } as IntersectionObserverEntry], this as unknown as IntersectionObserver);
  }
}

beforeEach(() => {
  queryClient.clear();
  setLanguage('en');
  FakeObserver.all = [];
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function renderCard(w: World, permissions = MANAGER, asset: { id: string; title: string; type: string } = ASSET) {
  const requests = install(w, permissions);
  return {
    requests,
    ...render(
      <AuthContext.Provider value={signedIn}>
        <QueryClientProvider client={queryClient}>
          <ToastProvider>
            <MemoryRouter initialEntries={['/library/assets/a1']}>
              <KnowledgeCard asset={asset} />
            </MemoryRouter>
          </ToastProvider>
        </QueryClientProvider>
      </AuthContext.Provider>,
    ),
  };
}
const card = () => screen.findByRole('region', { name: 'Knowledge' });
const mapped = (over: Partial<World> = {}) => newWorld({ docs: [doc()], ...over });

describe('Knowledge card on the asset page', () => {
  it('shows what the asset is mapped to', async () => {
    renderCard(mapped(), EDITOR);
    const c = within(await card());
    const fact = (label: string) => c.getByText(label).nextElementSibling!;
    await c.findByText('Owner manual');
    expect(fact('Model')).toHaveTextContent('lumen');
    expect(fact('Variant')).toHaveTextContent('sedan');
    expect(fact('Model years')).toHaveTextContent('2021-2024');
    expect(fact('Language')).toHaveTextContent('English (en)');
    expect(fact('Theme')).toHaveTextContent('ice');
    expect(fact('Chapters from')).toHaveTextContent('PDF bookmarks');
    expect(fact('Chapters from')).toHaveTextContent('2 hours ago');
  });

  it('says "all variants" and "all years" for a document that is not narrowed', async () => {
    renderCard(mapped({ docs: [doc({ variant: null, modelYearFrom: null, modelYearTo: null, theme: null })] }), READER);
    const c = within(await card());
    await c.findByText('Owner manual');
    expect(c.getByText('Variant').nextElementSibling).toHaveTextContent('All variants');
    expect(c.getByText('Model years').nextElementSibling).toHaveTextContent('All years');
    expect(c.getByText('Theme').nextElementSibling).toHaveTextContent('None');
  });

  it('keeps the chapters collapsed until asked, then nests them and links the pages', async () => {
    renderCard(mapped(), READER);
    const c = within(await card());
    const toggle = await c.findByRole('button', { name: '3 chapters' });
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    expect(c.queryByRole('link', { name: 'Charging' })).not.toBeInTheDocument();
    await userEvent.click(toggle);
    expect(toggle).toHaveAttribute('aria-expanded', 'true');
    const list = within(c.getByRole('list', { name: 'Chapters' }));
    expect(list.getByRole('link', { name: 'Getting started' })).toHaveAttribute('href', '/library/assets/a1?page=3');
    const nested = within(list.getByRole('link', { name: 'Getting started' }).closest('li')!).getByRole('list');
    expect(within(nested).getByRole('link', { name: 'Charging' })).toHaveAttribute('href', '/library/assets/a1?page=5');
    await userEvent.click(toggle);
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
  });

  it('says when no chapters were found', async () => {
    renderCard(mapped({ docs: [doc({ chapterCount: 0, chaptersSource: 'none' })], chapters: { d1: [] } }), READER);
    const c = within(await card());
    await userEvent.click(await c.findByRole('button', { name: '0 chapters' }));
    expect(c.getByText(/No chapters were found in this file/)).toBeInTheDocument();
  });

  it('offers no changes without knowledge.manage', async () => {
    renderCard(mapped(), EDITOR);
    const c = within(await card());
    await c.findByText('Owner manual');
    expect(c.queryByRole('group', { name: 'Knowledge actions' })).not.toBeInTheDocument();
    expect(c.queryByRole('button', { name: 'Re-index chapters' })).not.toBeInTheDocument();
  });

  it('is not shown for other asset types, and asks nothing', async () => {
    const w = mapped();
    const { requests } = renderCard(w, MANAGER, { id: 'a1', title: 'Hero shot', type: 'image' });
    // Once the page's own user lookup has gone out, anything the card was going to ask for would have too.
    await waitFor(() => expect(requests.some((c) => c.pathname === '/api/v1/me')).toBe(true));
    expect(screen.queryByRole('region', { name: 'Knowledge' })).not.toBeInTheDocument();
    expect(callsTo(w, LIST)).toHaveLength(0);
  });

  it('applies to manuals, documents and HTML5 bundles', async () => {
    for (const type of ['manual', 'document', 'html5']) {
      cleanup();
      queryClient.clear();
      renderCard(mapped(), READER, { id: 'a1', title: 'Acme', type });
      expect(await card(), type).toBeInTheDocument();
    }
  });

  describe('loading after the page', () => {
    it('waits until the card is near the screen, showing a loading state meanwhile', async () => {
      vi.stubGlobal('IntersectionObserver', FakeObserver);
      const w = mapped();
      const { requests } = renderCard(w, READER);
      const c = within(await card());
      expect(c.getByRole('status')).toHaveTextContent('Loading the knowledge mapping');
      await waitFor(() => expect(requests.some((x) => x.pathname === '/api/v1/me')).toBe(true));
      expect(callsTo(w, LIST)).toHaveLength(0);

      FakeObserver.all[0]!.show();
      await c.findByText('Owner manual');
      expect(callsTo(w, LIST)).toHaveLength(1);
      expect(callsTo(w, LIST)[0]!.search.get('assetId')).toBe('a1');
      await waitFor(() => expect(callsTo(w, `${LIST}/d1`)).toHaveLength(1));
    });

    it('is requested after the asset itself on the real asset page', async () => {
      const tw = newAssetWorld({ asset: assetDto({}, { type: 'manual', title: 'Acme Lumen owner manual' }) });
      const w = mapped();
      install(w, MANAGER, backend(tw));
      renderAt('/library/assets/a1');
      const c = within(await card());
      await c.findByText('Owner manual');
      const order = [...tw.calls, ...w.calls].map((x) => x.pathname);
      expect(order.indexOf('/api/v1/assets/a1')).toBeGreaterThanOrEqual(0);
      expect(callsTo(w, LIST)).toHaveLength(1);
    });
  });

  describe('not mapped', () => {
    it('shows nothing to people who cannot map', async () => {
      const w = newWorld({ docs: [] });
      renderCard(w, EDITOR);
      await waitFor(() => expect(callsTo(w, LIST)).toHaveLength(1));
      await waitFor(() => expect(screen.queryByRole('region', { name: 'Knowledge' })).not.toBeInTheDocument());
    });

    it('offers "Add to knowledge" to managers, with the asset already chosen', async () => {
      const w = newWorld({
        docs: [],
        hits: [{ id: 'a1', type: 'manual', status: 'published', title: 'Acme Lumen owner manual' }],
      });
      renderCard(w, MANAGER);
      const c = within(await card());
      expect(await c.findByText('Not a knowledge document')).toBeInTheDocument();
      await userEvent.click(c.getByRole('button', { name: 'Add to knowledge' }));
      const dialog = within(await screen.findByRole('dialog', { name: 'Map a document' }));
      expect(dialog.getByRole('group', { name: 'Asset' })).toHaveTextContent('Acme Lumen owner manual');
      await userEvent.type(dialog.getByLabelText('Model'), 'lumen');
      await userEvent.click(dialog.getByRole('button', { name: 'Map document' }));
      await waitFor(() => expect(callsTo(w, LIST, 'POST')).toHaveLength(1));
      expect(await callsTo(w, LIST, 'POST')[0]!.json()).toMatchObject({ assetId: 'a1', model: 'lumen', docType: 'owner_manual' });
      expect(await c.findByText('Model')).toBeInTheDocument();
    });
  });

  describe('failures', () => {
    it('shows a note with a retry', async () => {
      const w = newWorld();
      let fail = true;
      install(w, MANAGER, (call) => (call.pathname === LIST && fail ? problem(500, 'Server error') : undefined));
      render(
        <AuthContext.Provider value={signedIn}>
          <QueryClientProvider client={queryClient}>
            <ToastProvider>
              <MemoryRouter>
                <KnowledgeCard asset={ASSET} />
              </MemoryRouter>
            </ToastProvider>
          </QueryClientProvider>
        </AuthContext.Provider>,
      );
      expect(await screen.findByText('The knowledge mapping could not be loaded.')).toBeInTheDocument();
      fail = false;
      await userEvent.click(screen.getByRole('button', { name: 'Try again' }));
      expect(await screen.findByText('Owner manual')).toBeInTheDocument();
    });

    it('disappears when the API refuses the lookup', async () => {
      const w = newWorld();
      install(w, MANAGER, (call) => (call.pathname === LIST ? problem(403, 'Forbidden') : undefined));
      render(
        <AuthContext.Provider value={signedIn}>
          <QueryClientProvider client={queryClient}>
            <ToastProvider>
              <MemoryRouter>
                <KnowledgeCard asset={ASSET} />
              </MemoryRouter>
            </ToastProvider>
          </QueryClientProvider>
        </AuthContext.Provider>,
      );
      await waitFor(() => expect(screen.queryByRole('region', { name: 'Knowledge' })).not.toBeInTheDocument());
    });
  });

  describe('managing the mapping', () => {
    it('edits the mapping', async () => {
      const w = mapped();
      renderCard(w);
      const c = within(await card());
      await userEvent.click(await c.findByRole('button', { name: 'Edit the mapping' }));
      const dialog = within(await screen.findByRole('dialog', { name: 'Edit the mapping' }));
      expect(dialog.getByLabelText('Model')).toHaveValue('lumen');
      await userEvent.clear(dialog.getByLabelText('Model'));
      await userEvent.type(dialog.getByLabelText('Model'), 'orbit');
      await userEvent.click(dialog.getByRole('button', { name: 'Save changes' }));
      await waitFor(() => expect(callsTo(w, `${LIST}/d1`, 'PUT')).toHaveLength(1));
      expect(await c.findByText('orbit')).toBeInTheDocument();
    });

    it('removes the mapping after explaining that the file stays', async () => {
      const w = mapped();
      renderCard(w);
      const c = within(await card());
      await userEvent.click(await c.findByRole('button', { name: 'Remove from knowledge' }));
      const dialog = within(await screen.findByRole('dialog', { name: 'Remove this mapping?' }));
      expect(dialog.getByText(/The file is not deleted/)).toBeInTheDocument();
      await userEvent.click(dialog.getByRole('button', { name: 'Remove the mapping' }));
      await waitFor(() => expect(callsTo(w, `${LIST}/d1`, 'DELETE')).toHaveLength(1));
      expect(await c.findByText('Not a knowledge document')).toBeInTheDocument();
    });

    it('re-indexes the chapters straight away when the PDF or HTML supplied them', async () => {
      const w = mapped();
      renderCard(w);
      const c = within(await card());
      await userEvent.click(await c.findByRole('button', { name: 'Re-index chapters' }));
      await waitFor(() => expect(callsTo(w, `${LIST}/d1/reindex`, 'POST')).toHaveLength(1));
      expect(callsTo(w, `${LIST}/d1/reindex`, 'POST')[0]!.search.has('force')).toBe(false);
      expect(await screen.findByText('Re-indexing the chapters. They update in a moment.')).toBeInTheDocument();
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });

    it('asks before replacing chapters that were entered by hand, then re-indexes with force', async () => {
      const w = mapped({ docs: [doc({ chaptersSource: 'manual' })] });
      renderCard(w);
      const c = within(await card());
      await userEvent.click(await c.findByRole('button', { name: 'Re-index chapters' }));
      const dialog = within(await screen.findByRole('dialog', { name: 'Replace the chapters you entered?' }));
      expect(callsTo(w, `${LIST}/d1/reindex`, 'POST')).toHaveLength(0);
      await userEvent.click(dialog.getByRole('button', { name: 'Cancel' }));
      await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
      expect(callsTo(w, `${LIST}/d1/reindex`, 'POST')).toHaveLength(0);

      await userEvent.click(c.getByRole('button', { name: 'Re-index chapters' }));
      await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Replace the chapters' }));
      await waitFor(() => expect(callsTo(w, `${LIST}/d1/reindex`, 'POST')).toHaveLength(1));
      expect(callsTo(w, `${LIST}/d1/reindex`, 'POST')[0]!.search.get('force')).toBe('true');
    });

    it('reports a failed re-index', async () => {
      const w = mapped();
      renderCard(w);
      const c = within(await card());
      const button = await c.findByRole('button', { name: 'Re-index chapters' });
      w.refuse = problem(500, 'Server error', 'The indexer is down.');
      await userEvent.click(button);
      expect(await c.findByText('The chapters could not be re-indexed. The indexer is down.')).toBeInTheDocument();
    });
  });

  describe('chapter editor', () => {
    const open = async (w = mapped()) => {
      renderCard(w);
      const c = within(await card());
      await userEvent.click(await c.findByRole('button', { name: 'Edit chapters' }));
      return { w, c, dialog: within(await screen.findByRole('dialog', { name: 'Edit the chapters' })) };
    };

    it('starts from the current chapters', async () => {
      const { dialog } = await open();
      expect(dialog.getByLabelText('Chapter 1 title')).toHaveValue('Getting started');
      expect(dialog.getByLabelText('Chapter 2 title')).toHaveValue('Charging');
      expect(dialog.getByLabelText('Chapter 2 level')).toHaveValue('2');
      expect(dialog.getByLabelText('Chapter 2 first page')).toHaveValue('5');
      expect(dialog.getByLabelText('Chapter 2 last page')).toHaveValue('9');
      expect(dialog.getByText('3 of 500 chapters.')).toBeInTheDocument();
    });

    it('adds, edits, reorders and removes rows, and saves them as the new chapters', async () => {
      const { w, dialog, c } = await open();
      await userEvent.click(dialog.getByRole('button', { name: 'Move chapter 3 up' }));
      expect(dialog.getByLabelText('Chapter 2 title')).toHaveValue('Warning lights');
      expect(dialog.getByLabelText('Chapter 3 title')).toHaveValue('Charging');
      await userEvent.click(dialog.getByRole('button', { name: 'Move chapter 1 down' }));
      expect(dialog.getByLabelText('Chapter 1 title')).toHaveValue('Warning lights');
      await userEvent.click(dialog.getByRole('button', { name: 'Remove chapter 1' }));
      expect(dialog.getByLabelText('Chapter 1 title')).toHaveValue('Getting started');

      await userEvent.click(dialog.getByRole('button', { name: 'Add a chapter' }));
      await userEvent.type(dialog.getByLabelText('Chapter 3 title'), 'Care and cleaning');
      await userEvent.selectOptions(dialog.getByLabelText('Chapter 3 level'), '2');
      await userEvent.type(dialog.getByLabelText('Chapter 3 first page'), '14');
      await userEvent.clear(dialog.getByLabelText('Chapter 1 title'));
      await userEvent.type(dialog.getByLabelText('Chapter 1 title'), 'Start here');
      await userEvent.click(dialog.getByRole('button', { name: 'Save chapters' }));

      await waitFor(() => expect(callsTo(w, `${LIST}/d1/chapters`, 'PUT')).toHaveLength(1));
      expect(await callsTo(w, `${LIST}/d1/chapters`, 'PUT')[0]!.json()).toEqual({
        chapters: [
          { title: 'Start here', anchor: null, level: 1, pageFrom: 3, pageTo: 9 },
          { title: 'Charging', anchor: null, level: 2, pageFrom: 5, pageTo: 9 },
          { title: 'Care and cleaning', anchor: null, level: 2, pageFrom: 14, pageTo: 14 },
        ],
      });
      expect(await screen.findByText('Saved the chapters')).toBeInTheDocument();
      expect(await c.findByText('Entered by hand')).toBeInTheDocument();
    });

    it('shows each row that needs fixing and sends nothing', async () => {
      const { w, dialog } = await open();
      await userEvent.clear(dialog.getByLabelText('Chapter 1 title'));
      await userEvent.clear(dialog.getByLabelText('Chapter 2 last page'));
      await userEvent.type(dialog.getByLabelText('Chapter 2 last page'), '2');
      await userEvent.click(dialog.getByRole('button', { name: 'Save chapters' }));
      expect(await dialog.findByText('Enter a title.')).toBeInTheDocument();
      expect(dialog.getByText('The last page cannot be before the first.')).toBeInTheDocument();
      expect(dialog.getByText('Fix 2 chapters before saving.')).toBeInTheDocument();
      expect(callsTo(w, `${LIST}/d1/chapters`, 'PUT')).toHaveLength(0);
    });

    it('can start a document with no chapters from nothing', async () => {
      const { w, dialog } = await open(
        mapped({ docs: [doc({ chapterCount: 0, chaptersSource: 'none' })], chapters: { d1: [] } }),
      );
      expect(dialog.getByText('There are no chapters yet.')).toBeInTheDocument();
      await userEvent.click(dialog.getByRole('button', { name: 'Add a chapter' }));
      await userEvent.type(dialog.getByLabelText('Chapter 1 title'), 'Overview');
      await userEvent.click(dialog.getByRole('button', { name: 'Save chapters' }));
      await waitFor(() => expect(callsTo(w, `${LIST}/d1/chapters`, 'PUT')).toHaveLength(1));
      expect(((await callsTo(w, `${LIST}/d1/chapters`, 'PUT')[0]!.json()) as { chapters: unknown[] }).chapters).toEqual([
        { title: 'Overview', anchor: null, level: 1, pageFrom: null, pageTo: null },
      ]);
    });

    it('keeps an HTML anchor on a row whose title is edited', async () => {
      const { w, dialog } = await open(
        mapped({ chapters: { d1: [{ title: 'Intro', anchor: 'intro', level: 1, pageFrom: null, pageTo: null }] } }),
      );
      await userEvent.type(dialog.getByLabelText('Chapter 1 title'), ' to Lumen');
      await userEvent.click(dialog.getByRole('button', { name: 'Save chapters' }));
      await waitFor(() => expect(callsTo(w, `${LIST}/d1/chapters`, 'PUT')).toHaveLength(1));
      expect(((await callsTo(w, `${LIST}/d1/chapters`, 'PUT')[0]!.json()) as { chapters: unknown[] }).chapters).toEqual([
        { title: 'Intro to Lumen', anchor: 'intro', level: 1, pageFrom: null, pageTo: null },
      ]);
    });

    it('reports a refused save', async () => {
      const { w, dialog } = await open();
      w.refuse = problem(422, 'Validation failed', undefined, { errors: { chapters: ['Too many chapters.'] } });
      await userEvent.click(dialog.getByRole('button', { name: 'Save chapters' }));
      expect(await dialog.findByText('The chapters could not be saved. Too many chapters.')).toBeInTheDocument();
    });
  });

  describe('accessibility and Hindi', () => {
    it('has no axe violations, collapsed, expanded or with the editor open', async () => {
      renderCard(mapped());
      const c = await card();
      await within(c).findByText('Owner manual');
      expect(await axe(c)).toHaveNoViolations();
      await userEvent.click(within(c).getByRole('button', { name: '3 chapters' }));
      expect(await axe(c)).toHaveNoViolations();
      await userEvent.click(within(c).getByRole('button', { name: 'Edit chapters' }));
      expect(await axe(await screen.findByRole('dialog', { name: 'Edit the chapters' }))).toHaveNoViolations();
    });

    it('renders in Hindi with no untranslated keys', async () => {
      setLanguage('hi');
      renderCard(mapped());
      const c = within(await screen.findByRole('region', { name: 'ज्ञान-भंडार' }));
      expect(await c.findByText('ओनर मैनुअल')).toBeInTheDocument();
      expect(c.getByText('पीडीएफ़ बुकमार्क', { exact: false })).toBeInTheDocument();
      expect(c.getByRole('button', { name: '3 अध्याय' })).toBeInTheDocument();
      expect(c.getByRole('button', { name: 'अध्याय दोबारा इंडेक्स करें' })).toBeInTheDocument();
      expect(document.body.textContent).not.toMatch(/knowledge\.[a-z]/i);
      expect(CHAPTERS).toHaveLength(3);
    });
  });
});
