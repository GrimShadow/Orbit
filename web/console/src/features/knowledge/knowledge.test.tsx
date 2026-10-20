import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { queryClient } from '../../App';
import { setLanguage } from '../../i18n';
import { renderAt } from '../workflow/workflowTestKit';
import { EDITOR, MANAGER, READER, callsTo, doc, install, newWorld, problem, type World } from './knowledgeTestKit';

beforeEach(() => {
  queryClient.clear();
  setLanguage('en');
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const LIST = '/api/v1/knowledge/documents';
const table = () => screen.findByRole('table', { name: 'Knowledge documents' });
/** The data rows, once the list has loaded (while it loads the table holds four skeleton rows). */
const bodyRows = async () => {
  await screen.findByText(/\d+ documents? shown\./);
  return within(await table())
    .getAllByRole('row')
    .slice(1);
};
const rowCount = () => within(screen.getByRole('table', { name: 'Knowledge documents' })).getAllByRole('row').length - 1;
const lists = (w: World) => callsTo(w, LIST);

const THREE = [
  doc(),
  doc({
    id: 'd2',
    assetId: 'a2',
    title: 'Acme Orbit service guide',
    docType: 'service',
    model: 'orbit',
    variant: null,
    modelYearFrom: 2022,
    modelYearTo: 2022,
    language: 'hi',
    theme: null,
    assetStatus: 'in_review',
    chapterCount: 0,
  }),
  doc({
    id: 'd3',
    assetId: 'a3',
    title: 'Acme Orbit FAQ',
    docType: 'faq',
    model: 'orbit',
    variant: 'sport',
    modelYearFrom: null,
    modelYearTo: null,
    theme: 'ev',
  }),
];

describe('Documents tab', () => {
  it('lists each document with its type, model and variant, years, language, theme, status, chapters and age', async () => {
    const w = newWorld({ docs: THREE });
    install(w, READER);
    renderAt('/knowledge');
    expect(await screen.findByRole('heading', { level: 1, name: 'Documents' })).toBeInTheDocument();
    const rows = await bodyRows();
    expect(rows).toHaveLength(3);

    expect(within(rows[0]!).getByRole('link', { name: 'Acme Lumen owner manual' })).toHaveAttribute('href', '/library/assets/a1');
    expect(rows[0]).toHaveTextContent('Owner manual');
    expect(rows[0]).toHaveTextContent('lumen / sedan');
    expect(rows[0]).toHaveTextContent('2021-2024');
    expect(rows[0]).toHaveTextContent('English (en)');
    expect(rows[0]).toHaveTextContent('ice');
    expect(rows[0]).toHaveTextContent('Published');
    expect(rows[0]).toHaveTextContent('3 days ago');

    expect(rows[1]).toHaveTextContent('Service document');
    expect(rows[1]).toHaveTextContent('orbit / All variants');
    expect(rows[1]).toHaveTextContent(/2022/);
    expect(rows[1]).not.toHaveTextContent('2022-2022');
    expect(rows[1]).toHaveTextContent('In review');
    expect(rows[2]).toHaveTextContent('All years');
    expect(rows[2]).toHaveTextContent('FAQ');
    expect(await screen.findByText('3 documents shown.')).toBeInTheDocument();
  });

  it('shows a loading status first and asks for the first page of 25', async () => {
    const w = newWorld();
    install(w, READER);
    renderAt('/knowledge');
    await table();
    await waitFor(() => expect(lists(w)).toHaveLength(1));
    expect(lists(w)[0]!.search.get('limit')).toBe('25');
    expect(lists(w)[0]!.search.get('type')).toBeNull();
  });

  it('filters by type and language at once, and by the text boxes after a pause', async () => {
    const w = newWorld({ docs: THREE });
    install(w, READER);
    renderAt('/knowledge');
    await table();
    const filters = screen.getByRole('search', { name: 'Filter documents' });

    await userEvent.selectOptions(within(filters).getByLabelText('Document type'), 'faq');
    await waitFor(() => expect(lists(w).at(-1)!.search.get('type')).toBe('faq'));
    await waitFor(() => expect(rowCount()).toBe(1));
    await bodyRows();
    expect((await bodyRows())[0]).toHaveTextContent('Acme Orbit FAQ');

    await userEvent.selectOptions(within(filters).getByLabelText('Document type'), '');
    await userEvent.type(within(filters).getByLabelText('Model'), 'orbit');
    await userEvent.type(within(filters).getByLabelText('Variant'), 'sport');
    await userEvent.type(within(filters).getByLabelText('Model year'), '2023');
    await userEvent.selectOptions(within(filters).getByLabelText('Language'), 'en');
    await userEvent.type(within(filters).getByLabelText('Search'), 'faq');
    await waitFor(() => {
      const last = lists(w).at(-1)!.search;
      expect(last.get('model')).toBe('orbit');
      expect(last.get('variant')).toBe('sport');
      expect(last.get('year')).toBe('2023');
      expect(last.get('lang')).toBe('en');
      expect(last.get('q')).toBe('faq');
      expect(last.get('type')).toBeNull();
    });
    await waitFor(() => expect(rowCount()).toBe(1));
    await bodyRows();
  });

  it('says so when nothing matches the filters, and keeps the filters on screen', async () => {
    install(newWorld({ docs: THREE }), READER);
    renderAt('/knowledge');
    await table();
    await userEvent.type(screen.getByLabelText('Model'), 'nothing');
    expect(await screen.findByText('No document matches these filters.')).toBeInTheDocument();
    expect(screen.getByLabelText('Model')).toHaveValue('nothing');
  });

  it('pages: "Load more" appends the next page', async () => {
    const w = newWorld({
      docs: Array.from({ length: 3 }, (_, i) => doc({ id: `d${i}`, assetId: `a${i}`, title: `Acme manual ${i}` })),
      pageSize: 2,
    });
    install(w, READER);
    renderAt('/knowledge');
    expect(await bodyRows()).toHaveLength(2);
    expect(screen.getByText('2 documents shown.')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Load more' }));
    await waitFor(() => expect(rowCount()).toBe(3));
    expect(lists(w).at(-1)!.search.get('cursor')).toBe('2');
    expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument();
    expect(screen.getByText('3 documents shown.')).toBeInTheDocument();
  });

  it('shows a readable empty state, with the Map action only for people who can manage', async () => {
    install(newWorld({ docs: [] }), READER);
    renderAt('/knowledge');
    expect(await screen.findByRole('heading', { name: 'No knowledge documents yet' })).toBeInTheDocument();
    expect(screen.getByText(/An administrator or librarian can map manuals/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Map a document' })).not.toBeInTheDocument();
    cleanup();
    queryClient.clear();

    install(newWorld({ docs: [] }), MANAGER);
    renderAt('/knowledge');
    expect(await screen.findByRole('button', { name: 'Map a document' })).toBeInTheDocument();
  });

  it('shows a failure with a retry that loads again', async () => {
    const w = newWorld({ listFails: true });
    install(w, READER);
    renderAt('/knowledge');
    expect(await screen.findByRole('heading', { name: 'The documents could not be loaded' })).toBeInTheDocument();
    w.listFails = false;
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await table()).toBeInTheDocument();
  });

  it('explains a refusal instead of an error', async () => {
    const w = newWorld();
    install(w, READER, (c) => (c.pathname === LIST ? problem(403, 'Forbidden') : undefined));
    renderAt('/knowledge');
    expect(await screen.findByRole('heading', { name: 'You do not have access to knowledge documents' })).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('is read-only without knowledge.manage', async () => {
    install(newWorld(), EDITOR);
    renderAt('/knowledge');
    await table();
    expect(screen.queryByRole('button', { name: 'Map a document' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Edit the mapping of/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Remove the mapping of/ })).not.toBeInTheDocument();
  });
});

describe('Map a document', () => {
  const open = async (w: World) => {
    install(w, MANAGER);
    renderAt('/knowledge');
    await table();
    await userEvent.click(await screen.findByRole('button', { name: 'Map a document' }));
    return within(await screen.findByRole('dialog', { name: 'Map a document' }));
  };

  it('asks for the asset and the model before it sends anything', async () => {
    const w = newWorld();
    const dialog = await open(w);
    await userEvent.click(dialog.getByRole('button', { name: 'Map document' }));
    expect(await dialog.findByText('Choose the asset to map.')).toBeInTheDocument();
    expect(dialog.getByText('Enter the model.')).toBeInTheDocument();
    expect(callsTo(w, LIST, 'POST')).toHaveLength(0);
  });

  it('checks the codes and the year range', async () => {
    const w = newWorld();
    const dialog = await open(w);
    await userEvent.type(dialog.getByLabelText('Model'), 'Acme Lumen!');
    await userEvent.type(dialog.getByLabelText('From year'), '2022');
    await userEvent.click(dialog.getByRole('button', { name: 'Map document' }));
    expect(await dialog.findByText(/Use lower-case letters, digits, dots, dashes and underscores only/)).toBeInTheDocument();
    expect(dialog.getByText('Enter both years, or leave both empty.')).toBeInTheDocument();
    await userEvent.type(dialog.getByLabelText('To year'), '2020');
    expect(await dialog.findByText('The first year cannot be later than the last.')).toBeInTheDocument();
    expect(callsTo(w, LIST, 'POST')).toHaveLength(0);
  });

  it('finds a manual, document or HTML5 asset, maps it and refreshes the list', async () => {
    const w = newWorld();
    const dialog = await open(w);
    await userEvent.type(dialog.getByLabelText('Find an asset'), 'orbit owner');
    await dialog.findByRole('button', { name: /Acme Orbit owner manual/ });
    await waitFor(() => expect(dialog.queryByRole('button', { name: /quick guide/ })).not.toBeInTheDocument());
    const search = callsTo(w, '/api/v1/search', 'POST').at(-1)!;
    expect(await search.json()).toMatchObject({ filters: { type: ['manual', 'document', 'html5'] }, query: 'orbit owner' });
    await userEvent.click(dialog.getByRole('button', { name: /Acme Orbit owner manual/ }));
    expect(await dialog.findByRole('button', { name: 'Change' })).toBeInTheDocument();
    expect(dialog.getByRole('group', { name: 'Asset' })).toHaveTextContent('Acme Orbit owner manual');

    await userEvent.selectOptions(dialog.getByLabelText('Document type'), 'faq');
    await userEvent.type(dialog.getByLabelText('Model'), 'Orbit');
    await userEvent.type(dialog.getByLabelText('From year'), '2023');
    await userEvent.type(dialog.getByLabelText('To year'), '2025');
    await userEvent.selectOptions(dialog.getByLabelText('Language'), 'hi');
    await userEvent.type(dialog.getByLabelText('Theme'), 'EV');
    await userEvent.click(dialog.getByRole('button', { name: 'Map document' }));

    await waitFor(() => expect(callsTo(w, LIST, 'POST')).toHaveLength(1));
    expect(await callsTo(w, LIST, 'POST')[0]!.json()).toEqual({
      assetId: 'a9',
      docType: 'faq',
      model: 'orbit',
      variant: null,
      modelYearFrom: 2023,
      modelYearTo: 2025,
      language: 'hi',
      theme: 'ev',
    });
    expect(await screen.findByText('Acme Orbit owner manual is now a knowledge document')).toBeInTheDocument();
    await waitFor(() => expect(rowCount()).toBe(2));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('lets the person change the chosen asset', async () => {
    const dialog = await open(newWorld());
    await userEvent.click(await dialog.findByRole('button', { name: /Acme Orbit owner manual/ }));
    await userEvent.click(await dialog.findByRole('button', { name: 'Change' }));
    expect(await dialog.findByLabelText('Find an asset')).toBeInTheDocument();
  });

  it('says plainly when the asset is already mapped', async () => {
    const w = newWorld({ docs: [doc({ assetId: 'a9' })] });
    const dialog = await open(w);
    await userEvent.click(await dialog.findByRole('button', { name: /Acme Orbit owner manual/ }));
    await userEvent.type(await dialog.findByLabelText('Model'), 'orbit');
    await userEvent.click(dialog.getByRole('button', { name: 'Map document' }));
    expect(await dialog.findByText(/already mapped as a knowledge document/)).toBeInTheDocument();
  });

  it('reports a server refusal with its reason', async () => {
    const w = newWorld();
    const dialog = await open(w);
    await userEvent.click(await dialog.findByRole('button', { name: /Acme Orbit owner manual/ }));
    await userEvent.type(await dialog.findByLabelText('Model'), 'orbit');
    w.refuse = problem(422, 'Validation failed', undefined, { errors: { language: ['Unknown language.'] } });
    await userEvent.click(dialog.getByRole('button', { name: 'Map document' }));
    expect(await dialog.findByText('The mapping could not be saved. Unknown language.')).toBeInTheDocument();
  });
});

describe('Editing and removing a mapping', () => {
  it('opens the mapping, keeps the asset fixed and sends the change without the asset id', async () => {
    const w = newWorld();
    install(w, MANAGER);
    renderAt('/knowledge');
    await table();
    await userEvent.click(await screen.findByRole('button', { name: 'Edit the mapping of Acme Lumen owner manual' }));
    const dialog = within(await screen.findByRole('dialog', { name: 'Edit the mapping' }));
    expect(dialog.getByText('Acme Lumen owner manual')).toBeInTheDocument();
    expect(dialog.queryByLabelText('Find an asset')).not.toBeInTheDocument();
    expect(dialog.getByLabelText('Model')).toHaveValue('lumen');
    expect(dialog.getByLabelText('Variant')).toHaveValue('sedan');
    expect(dialog.getByLabelText('From year')).toHaveValue('2021');
    expect(dialog.getByLabelText('Theme')).toHaveValue('ice');

    await userEvent.clear(dialog.getByLabelText('Theme'));
    await userEvent.clear(dialog.getByLabelText('Variant'));
    await userEvent.click(dialog.getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(callsTo(w, `${LIST}/d1`, 'PUT')).toHaveLength(1));
    const body = (await callsTo(w, `${LIST}/d1`, 'PUT')[0]!.json()) as Record<string, unknown>;
    expect(body).toMatchObject({ model: 'lumen', variant: null, theme: null, modelYearFrom: 2021, modelYearTo: 2024 });
    expect(body).not.toHaveProperty('assetId');
    expect(await screen.findByText('Saved the mapping of Acme Lumen owner manual')).toBeInTheDocument();
  });

  it('explains that the file is not deleted, then removes only the mapping', async () => {
    const w = newWorld();
    install(w, MANAGER);
    renderAt('/knowledge');
    await table();
    await userEvent.click(await screen.findByRole('button', { name: 'Remove the mapping of Acme Lumen owner manual' }));
    const dialog = within(await screen.findByRole('dialog', { name: 'Remove this mapping?' }));
    expect(dialog.getByText(/The file is not deleted/)).toBeInTheDocument();
    await userEvent.click(dialog.getByRole('button', { name: 'Remove the mapping' }));
    await waitFor(() => expect(callsTo(w, `${LIST}/d1`, 'DELETE')).toHaveLength(1));
    expect(await screen.findByText('Removed the mapping of Acme Lumen owner manual')).toBeInTheDocument();
    expect(callsTo(w, '/api/v1/assets/a1', 'DELETE')).toHaveLength(0);
    expect(await screen.findByRole('heading', { name: 'No knowledge documents yet' })).toBeInTheDocument();
  });

  it('keeps the confirmation open and says why when the removal fails', async () => {
    const w = newWorld();
    install(w, MANAGER);
    renderAt('/knowledge');
    await table();
    await userEvent.click(await screen.findByRole('button', { name: 'Remove the mapping of Acme Lumen owner manual' }));
    const dialog = within(await screen.findByRole('dialog', { name: 'Remove this mapping?' }));
    w.refuse = problem(403, 'Forbidden', 'Not allowed.');
    await userEvent.click(dialog.getByRole('button', { name: 'Remove the mapping' }));
    expect(await dialog.findByText('The mapping could not be removed. Not allowed.')).toBeInTheDocument();
  });
});

describe('Knowledge navigation', () => {
  it('is in the sidebar for anyone who can read assets', async () => {
    install(newWorld(), READER);
    renderAt('/knowledge');
    const nav = await screen.findByRole('navigation', { name: 'Primary' });
    expect(await within(nav).findByRole('link', { name: 'Knowledge' })).toHaveAttribute('href', '/knowledge');
  });

  it('links the pages as tabs, and offers VIN rules only to those who may see them', async () => {
    install(newWorld(), READER);
    renderAt('/knowledge');
    const tabs = await screen.findByRole('navigation', { name: 'Knowledge pages' });
    expect(within(tabs).getByRole('link', { name: 'Documents' })).toHaveAttribute('aria-current', 'page');
    expect(within(tabs).getByRole('link', { name: 'VIN resolver' })).toHaveAttribute('href', '/knowledge/resolver');
    await screen.findByRole('table');
    expect(within(tabs).queryByRole('link', { name: 'VIN rules' })).not.toBeInTheDocument();
    cleanup();
    queryClient.clear();

    install(newWorld(), EDITOR);
    renderAt('/knowledge');
    const withRules = await screen.findByRole('navigation', { name: 'Knowledge pages' });
    expect(await within(withRules).findByRole('link', { name: 'VIN rules' })).toHaveAttribute('href', '/knowledge/vin-rules');
  });

  it('moves between the pages', async () => {
    install(newWorld(), MANAGER);
    renderAt('/knowledge');
    const tabs = await screen.findByRole('navigation', { name: 'Knowledge pages' });
    await userEvent.click(await within(tabs).findByRole('link', { name: 'VIN resolver' }));
    expect(await screen.findByRole('heading', { level: 1, name: 'VIN resolver' })).toBeInTheDocument();
    await userEvent.click(
      within(screen.getByRole('navigation', { name: 'Knowledge pages' })).getByRole('link', { name: 'VIN rules' }),
    );
    expect(await screen.findByRole('heading', { level: 1, name: 'VIN rules' })).toBeInTheDocument();
  });
});

describe('Accessibility and Hindi', () => {
  it('has no axe violations on the list or in the Map dialog', async () => {
    install(newWorld({ docs: THREE }), MANAGER);
    const { container } = renderAt('/knowledge');
    await table();
    expect(await axe(container)).toHaveNoViolations();
    await userEvent.click(await screen.findByRole('button', { name: 'Map a document' }));
    const dialog = await screen.findByRole('dialog', { name: 'Map a document' });
    expect(await axe(dialog)).toHaveNoViolations();
  });

  it('renders in Hindi with no untranslated keys', async () => {
    setLanguage('hi');
    install(newWorld({ docs: THREE }), MANAGER);
    renderAt('/knowledge');
    expect(await screen.findByRole('heading', { level: 1, name: 'दस्तावेज़' })).toBeInTheDocument();
    const rows = await within(await screen.findByRole('table', { name: 'ज्ञान-भंडार के दस्तावेज़' })).findAllByRole('row');
    expect(rows[1]).toHaveTextContent('ओनर मैनुअल');
    expect(rows[3]).toHaveTextContent('सभी वर्ष');
    expect(screen.getByRole('button', { name: 'दस्तावेज़ जोड़ें' })).toBeInTheDocument();
    expect(document.body.textContent).not.toMatch(/knowledge\.[a-z]/i);
    expect(await screen.findByText('3 दस्तावेज़ दिखाए गए।')).toBeInTheDocument();
  });
});
