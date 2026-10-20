import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { queryClient } from '../../App';
import { setLanguage } from '../../i18n';
import { json, mockApi, renderAt, type Call } from '../workflow/workflowTestKit';
import type { GlossaryTerm } from '../translation/translationTypes';

beforeEach(() => {
  queryClient.clear();
  setLanguage('en');
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const MANAGER = ['assets.read', 'glossary.manage'];
const term = (over: Partial<GlossaryTerm> = {}): GlossaryTerm => ({
  id: 't1',
  term: 'Acme Lumen',
  mode: 'locked',
  translations: {},
  note: 'Product name',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  ...over,
});

interface World {
  terms: GlossaryTerm[];
  calls: Call[];
  /** The next write is refused with this answer. */
  refuse?: Response;
  /** Pages the list answers with: the cursor and what follows. */
  pageSize?: number;
}

function install(w: World, permissions = MANAGER) {
  let nextId = 100;
  return mockApi(
    (c) => {
      if (!c.pathname.startsWith('/api/v1/glossary')) return undefined;
      w.calls.push(c);
      if (c.method === 'GET') {
        const q = (c.search.get('q') ?? '').toLowerCase();
        const matching = w.terms.filter((t) => t.term.toLowerCase().includes(q));
        const size = w.pageSize ?? 50;
        const from = Number(c.search.get('cursor') ?? 0);
        const items = matching.slice(from, from + size);
        return json({ items, nextCursor: from + size < matching.length ? String(from + size) : null });
      }
      if (w.refuse) {
        const r = w.refuse;
        w.refuse = undefined;
        return r;
      }
      if (c.method === 'POST')
        return c.json().then((b) => {
          const body = b as Omit<GlossaryTerm, 'id' | 'createdAt' | 'updatedAt'>;
          if (w.terms.some((t) => t.term === body.term))
            return json({ title: 'Conflict', status: 409, detail: 'Term exists.' }, 409);
          const created = { ...body, id: `n${nextId++}`, createdAt: '2026-02-01T00:00:00Z', updatedAt: '2026-02-01T00:00:00Z' };
          w.terms.push(created);
          return json(created, 201);
        });
      const id = c.pathname.split('/').pop()!;
      if (c.method === 'PUT')
        return c.json().then((b) => {
          const i = w.terms.findIndex((t) => t.id === id);
          w.terms[i] = { ...w.terms[i]!, ...(b as object) };
          return json(w.terms[i]);
        });
      if (c.method === 'DELETE') {
        w.terms = w.terms.filter((t) => t.id !== id);
        return new Response(null, { status: 204 });
      }
      return undefined;
    },
    { permissions },
  );
}

const writes = (w: World, method: string) => w.calls.filter((c) => c.method === method);
const table = () => screen.findByRole('table', { name: 'Glossary terms' });

describe('Glossary page', () => {
  it('explains locked and mapped terms in one paragraph, and lists the terms', async () => {
    const w: World = {
      terms: [
        term(),
        term({ id: 't2', term: 'Acme Cloud', mode: 'mapped', translations: { hi: 'ऐक्मे क्लाउड', ta: 'ஏக்மி' }, note: null }),
      ],
      calls: [],
    };
    install(w);
    renderAt('/admin/glossary');
    expect(await screen.findByRole('heading', { level: 1, name: 'Glossary' })).toBeInTheDocument();
    expect(screen.getByText(/A locked term is kept exactly as written in every language/)).toBeInTheDocument();
    const rows = within(await table())
      .getAllByRole('row')
      .slice(1);
    expect(rows).toHaveLength(2);
    expect(rows[0]).toHaveTextContent('Acme Lumen');
    expect(rows[0]).toHaveTextContent('Locked');
    expect(rows[0]).toHaveTextContent('All languages');
    expect(rows[0]).toHaveTextContent('Product name');
    expect(rows[1]).toHaveTextContent('Acme Cloud');
    expect(rows[1]).toHaveTextContent('Mapped');
    expect(rows[1]).toHaveTextContent('HI');
    expect(rows[1]).toHaveTextContent('TA');
    expect(screen.getByText(/2 terms shown\. A glossary holds up to 5,000 terms\./)).toBeInTheDocument();
  });

  it('is behind the glossary.manage permission: no nav entry and an explanation for those without it', async () => {
    install({ terms: [term()], calls: [] }, ['assets.read']);
    renderAt('/admin/glossary');
    expect(await screen.findByText('You do not have access to the glossary')).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Glossary' })).not.toBeInTheDocument();
  });

  it('shows the nav entry to those who can manage it', async () => {
    install({ terms: [term()], calls: [] });
    renderAt('/admin/glossary');
    const nav = await screen.findByRole('navigation', { name: 'Primary' });
    expect(await within(nav).findByRole('link', { name: 'Glossary' })).toHaveAttribute('href', '/admin/glossary');
  });

  it('shows an empty state with the ways to add terms', async () => {
    install({ terms: [], calls: [] });
    renderAt('/admin/glossary');
    expect(await screen.findByRole('heading', { name: 'No glossary terms yet' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Add term' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Paste terms' })).toBeInTheDocument();
  });

  it('shows a failure with a retry', async () => {
    let fail = true;
    const w: World = { terms: [term()], calls: [] };
    install(w);
    // Fail the list once.
    const base = globalThis.fetch;
    vi.stubGlobal('fetch', async (input: Request) => {
      if (fail && input.url.includes('/api/v1/glossary')) return json({ title: 'Boom', status: 500 }, 500);
      return (base as typeof fetch)(input);
    });
    renderAt('/admin/glossary');
    expect(await screen.findByText('Could not load the glossary', undefined, { timeout: 8000 })).toBeInTheDocument();
    fail = false;
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await table()).toBeInTheDocument();
  }, 15_000);

  it('searches by term, and says when nothing matches', async () => {
    const w: World = { terms: [term(), term({ id: 't2', term: 'Acme Cloud', note: null })], calls: [] };
    install(w);
    renderAt('/admin/glossary');
    await table();
    await userEvent.type(screen.getByRole('searchbox', { name: 'Search terms' }), 'cloud');
    await waitFor(() => expect(w.calls.some((c) => c.search.get('q') === 'cloud')).toBe(true));
    await waitFor(() => expect(within(screen.getByRole('table')).getAllByRole('row')).toHaveLength(2));
    expect(screen.queryByText('Acme Lumen')).not.toBeInTheDocument();
    await userEvent.clear(screen.getByRole('searchbox', { name: 'Search terms' }));
    await userEvent.type(screen.getByRole('searchbox', { name: 'Search terms' }), 'zzz');
    expect(await screen.findByText('No terms match "zzz".')).toBeInTheDocument();
    // The way to add one is still there.
    expect(screen.getByRole('button', { name: 'Add term' })).toBeInTheDocument();
  });

  it('pages with Load more', async () => {
    const terms = Array.from({ length: 3 }, (_, i) => term({ id: `t${i}`, term: `Acme ${i}`, note: null }));
    const w: World = { terms, calls: [], pageSize: 2 };
    install(w);
    renderAt('/admin/glossary');
    await table();
    expect(screen.getByText('Acme 1')).toBeInTheDocument();
    expect(screen.queryByText('Acme 2')).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Load more' }));
    expect(await screen.findByText('Acme 2')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    install({ terms: [term()], calls: [] });
    const { container } = renderAt('/admin/glossary');
    await table();
    expect(await axe(container)).toHaveNoViolations();
  });
});

describe('Glossary form', () => {
  const openAdd = async () => {
    await userEvent.click(await screen.findByRole('button', { name: 'Add term' }));
    return screen.findByRole('dialog');
  };

  it('adds a locked term without renderings', async () => {
    const w: World = { terms: [term()], calls: [] };
    install(w);
    renderAt('/admin/glossary');
    const dialog = await openAdd();
    expect(within(dialog).queryByText('Rendering in each language')).not.toBeInTheDocument();
    await userEvent.type(within(dialog).getByRole('textbox', { name: 'Term' }), 'Acme Hub');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add term' }));
    await waitFor(() => expect(writes(w, 'POST')).toHaveLength(1));
    expect(await writes(w, 'POST')[0]!.json()).toEqual({ term: 'Acme Hub', mode: 'locked', translations: {}, note: null });
    expect(await screen.findByText('Added Acme Hub')).toBeInTheDocument();
    expect(await screen.findByText('Acme Hub')).toBeInTheDocument();
  });

  it('asks for the term', async () => {
    const w: World = { terms: [term()], calls: [] };
    install(w);
    renderAt('/admin/glossary');
    const dialog = await openAdd();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add term' }));
    expect(within(dialog).getByText('Enter the term.')).toBeInTheDocument();
    expect(writes(w, 'POST')).toHaveLength(0);
  });

  it('needs a rendering for a mapped term, and sends one per language', async () => {
    const w: World = { terms: [term()], calls: [] };
    install(w);
    renderAt('/admin/glossary');
    const dialog = await openAdd();
    await userEvent.type(within(dialog).getByRole('textbox', { name: 'Term' }), 'Acme Cloud');
    await userEvent.click(within(dialog).getByRole('radio', { name: /Mapped/ }));
    // Choosing "mapped" starts with one row to fill in; submitting it empty is refused.
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add term' }));
    expect(within(dialog).getByText('Give each rendering a language and its text.')).toBeInTheDocument();
    expect(writes(w, 'POST')).toHaveLength(0);

    expect(within(dialog).getByRole('combobox', { name: 'Language of rendering 1' })).toHaveValue('en');
    await userEvent.selectOptions(within(dialog).getByRole('combobox', { name: 'Language of rendering 1' }), 'hi');
    await userEvent.type(within(dialog).getByRole('textbox', { name: 'Rendering 1' }), 'ऐक्मे क्लाउड');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add a language' }));
    // The second row offers only languages not used yet.
    const second = within(dialog).getByRole('combobox', { name: 'Language of rendering 2' });
    expect(within(second).queryByRole('option', { name: /Hindi/ })).not.toBeInTheDocument();
    await userEvent.selectOptions(second, 'ta');
    await userEvent.type(within(dialog).getByRole('textbox', { name: 'Rendering 2' }), 'ஏக்மி');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add term' }));
    await waitFor(() => expect(writes(w, 'POST')).toHaveLength(1));
    expect(await writes(w, 'POST')[0]!.json()).toEqual({
      term: 'Acme Cloud',
      mode: 'mapped',
      translations: { hi: 'ऐक्मे क्लाउड', ta: 'ஏக்மி' },
      note: null,
    });
  });

  it('removes a rendering row, and offers the language again', async () => {
    install({ terms: [term()], calls: [] });
    renderAt('/admin/glossary');
    const dialog = await openAdd();
    await userEvent.click(within(dialog).getByRole('radio', { name: /Mapped/ }));
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add a language' }));
    expect(within(dialog).getAllByRole('textbox', { name: /^Rendering/ })).toHaveLength(2);
    await userEvent.click(within(dialog).getByRole('button', { name: 'Remove rendering 1' }));
    expect(within(dialog).getAllByRole('textbox', { name: /^Rendering/ })).toHaveLength(1);
    await userEvent.click(within(dialog).getByRole('button', { name: 'Remove rendering 1' }));
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add term' }));
    expect(within(dialog).getByText('Enter the term.')).toBeInTheDocument();
    expect(within(dialog).getByText('A mapped term needs a rendering in at least one language.')).toBeInTheDocument();
  });

  it('edits a term starting from what it holds', async () => {
    const w: World = {
      terms: [term({ id: 't2', term: 'Acme Cloud', mode: 'mapped', translations: { hi: 'ऐक्मे क्लाउड' }, note: 'Brand' })],
      calls: [],
    };
    install(w);
    renderAt('/admin/glossary');
    await userEvent.click(await screen.findByRole('button', { name: 'Edit Acme Cloud' }));
    const dialog = await screen.findByRole('dialog', { name: 'Edit glossary term' });
    expect(within(dialog).getByRole('textbox', { name: 'Term' })).toHaveValue('Acme Cloud');
    expect(within(dialog).getByRole('radio', { name: /Mapped/ })).toBeChecked();
    expect(within(dialog).getByRole('textbox', { name: 'Rendering 1' })).toHaveValue('ऐक्मे क्लाउड');
    expect(within(dialog).getByRole('textbox', { name: 'Note' })).toHaveValue('Brand');
    await userEvent.clear(within(dialog).getByRole('textbox', { name: 'Rendering 1' }));
    await userEvent.type(within(dialog).getByRole('textbox', { name: 'Rendering 1' }), 'एक्मे क्लाउड');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save term' }));
    await waitFor(() => expect(writes(w, 'PUT')).toHaveLength(1));
    expect(writes(w, 'PUT')[0]!.pathname).toBe('/api/v1/glossary/t2');
    expect(await writes(w, 'PUT')[0]!.json()).toEqual({
      term: 'Acme Cloud',
      mode: 'mapped',
      translations: { hi: 'एक्मे क्लाउड' },
      note: 'Brand',
    });
    expect(await screen.findByText('Saved Acme Cloud')).toBeInTheDocument();
  });

  it('says a term is already there when the server refuses a duplicate', async () => {
    const w: World = { terms: [term()], calls: [] };
    install(w);
    renderAt('/admin/glossary');
    const dialog = await openAdd();
    await userEvent.type(within(dialog).getByRole('textbox', { name: 'Term' }), 'Acme Lumen');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add term' }));
    expect(await within(dialog).findByText('"Acme Lumen" is already in the glossary.')).toBeInTheDocument();
  });

  it('shows the 5,000-term limit message when the glossary is full', async () => {
    const w: World = {
      terms: [term()],
      calls: [],
      refuse: json({ title: 'Conflict', status: 409, detail: 'A tenant may have at most 5000 glossary terms.' }, 409),
    };
    install(w);
    renderAt('/admin/glossary');
    const dialog = await openAdd();
    await userEvent.type(within(dialog).getByRole('textbox', { name: 'Term' }), 'Acme Hub');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add term' }));
    expect(await within(dialog).findByText(/The glossary is full\. A glossary holds up to 5,000 terms/)).toBeInTheDocument();
  });
});

describe('Glossary delete', () => {
  it('confirms first, then deletes and says so', async () => {
    const w: World = { terms: [term(), term({ id: 't2', term: 'Acme Cloud', note: null })], calls: [] };
    install(w);
    renderAt('/admin/glossary');
    await userEvent.click(await screen.findByRole('button', { name: 'Delete Acme Lumen' }));
    const dialog = await screen.findByRole('dialog', { name: 'Delete this term?' });
    expect(dialog).toHaveTextContent('Acme Lumen will no longer be protected when text is translated.');
    expect(writes(w, 'DELETE')).toHaveLength(0);
    await userEvent.click(within(dialog).getByRole('button', { name: 'Delete term' }));
    await waitFor(() => expect(writes(w, 'DELETE')).toHaveLength(1));
    expect(writes(w, 'DELETE')[0]!.pathname).toBe('/api/v1/glossary/t1');
    expect(await screen.findByText('Deleted Acme Lumen')).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Delete Acme Lumen' })).not.toBeInTheDocument());
  });

  it('cancelling deletes nothing', async () => {
    const w: World = { terms: [term()], calls: [] };
    install(w);
    renderAt('/admin/glossary');
    await userEvent.click(await screen.findByRole('button', { name: 'Delete Acme Lumen' }));
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Cancel' }));
    expect(writes(w, 'DELETE')).toHaveLength(0);
    expect(screen.getByText('Acme Lumen')).toBeInTheDocument();
  });
});

describe('Glossary paste import', () => {
  const openImport = async () => {
    await userEvent.click(await screen.findByRole('button', { name: 'Paste terms' }));
    return screen.findByRole('dialog', { name: 'Paste glossary terms' });
  };
  // user-event treats `{` and `[` as keys: paste the text instead.
  const paste = async (area: HTMLElement, text: string) => {
    await userEvent.click(area);
    await userEvent.paste(text);
  };

  it('previews what will be created and each problem line before anything is sent', async () => {
    const w: World = { terms: [term()], calls: [] };
    install(w);
    renderAt('/admin/glossary');
    const dialog = await openImport();
    expect(within(dialog).getByRole('button', { name: 'Import 0 terms' })).toBeDisabled();
    await paste(
      within(dialog).getByRole('textbox', { name: 'Terms' }),
      'Acme Hub\nAcme Cloud | hi=ऐक्मे क्लाउड | ta=ஏக்மி\nAcme Bad | xx=y\nAcme Hub',
    );
    expect(within(dialog).getByText('2 terms will be created: 1 locked, 1 mapped.')).toBeInTheDocument();
    const preview = within(dialog).getByRole('list', { name: 'Terms that will be created' });
    expect(within(preview).getAllByRole('listitem')).toHaveLength(2);
    expect(within(preview).getByText('hi: ऐक्मे क्लाउड · ta: ஏக்மி')).toBeInTheDocument();
    expect(within(dialog).getByText('2 lines have problems and will be skipped')).toBeInTheDocument();
    expect(within(dialog).getByText(/Line 3:/).parentElement).toHaveTextContent('"xx" is not a language this organisation uses.');
    expect(within(dialog).getByText(/Line 4:/).parentElement).toHaveTextContent('This term repeats line 1.');
    expect(writes(w, 'POST')).toHaveLength(0);
  });

  it('creates the valid lines one by one and reports the result', async () => {
    const w: World = { terms: [term()], calls: [] };
    install(w);
    renderAt('/admin/glossary');
    const dialog = await openImport();
    await paste(within(dialog).getByRole('textbox', { name: 'Terms' }), 'Acme Hub\nAcme Cloud | hi=ऐक्मे क्लाउड\nAcme Lumen');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Import 3 terms' }));
    await waitFor(() => expect(writes(w, 'POST')).toHaveLength(3));
    expect(await writes(w, 'POST')[1]!.json()).toEqual({
      term: 'Acme Cloud',
      mode: 'mapped',
      translations: { hi: 'ऐक्मे क्लाउड' },
      note: null,
    });
    // "Acme Lumen" already exists: the server refuses it, the others go through.
    expect(await within(dialog).findByText('2 terms were added.')).toBeInTheDocument();
    expect(within(dialog).getByText('1 term could not be added')).toBeInTheDocument();
    expect(within(dialog).getByText('Term exists.')).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Done' }));
    expect(await screen.findByText('Acme Hub')).toBeInTheDocument();
  });

  it('stops and says so when the 5,000-term limit is reached', async () => {
    const w: World = { terms: [term()], calls: [] };
    install(w);
    const limit = json({ title: 'Conflict', status: 409, detail: 'At most 5000 terms.' }, 409);
    renderAt('/admin/glossary');
    const dialog = await openImport();
    await paste(within(dialog).getByRole('textbox', { name: 'Terms' }), 'Acme One\nAcme Two\nAcme Three');
    w.refuse = limit;
    await userEvent.click(within(dialog).getByRole('button', { name: 'Import 3 terms' }));
    expect(
      await within(dialog).findByText(/The glossary reached its limit of 5,000 terms\. 3 terms were not added\./),
    ).toBeInTheDocument();
    expect(writes(w, 'POST')).toHaveLength(1);
  });

  it('shows a one-line example', async () => {
    install({ terms: [term()], calls: [] });
    renderAt('/admin/glossary');
    const dialog = await openImport();
    expect(within(dialog).getByLabelText('Example of pasted terms')).toHaveTextContent('Acme Cloud | hi=');
  });
});

describe('Glossary in Hindi', () => {
  it('renders the page, the table and the form in Hindi', async () => {
    setLanguage('hi');
    install({
      terms: [term(), term({ id: 't2', term: 'Acme Cloud', mode: 'mapped', translations: { hi: 'x' }, note: null })],
      calls: [],
    });
    renderAt('/admin/glossary');
    expect(await screen.findByRole('heading', { level: 1, name: 'शब्दावली' })).toBeInTheDocument();
    expect(screen.getByText(/लॉक किया हुआ शब्द हर भाषा में ठीक वैसा ही रहता है/)).toBeInTheDocument();
    expect(await screen.findByRole('table', { name: 'शब्दावली के शब्द' })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'शब्द जोड़ें' }));
    const dialog = await screen.findByRole('dialog', { name: 'शब्दावली में शब्द जोड़ें' });
    expect(within(dialog).getByRole('radio', { name: /मैप किया हुआ/ })).toBeInTheDocument();
    expect(await screen.findByRole('link', { name: 'शब्दावली' })).toHaveAttribute('href', '/admin/glossary');
  });
});
