import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { queryClient } from '../../App';
import { setLanguage } from '../../i18n';
import { CAPS_OFF, CAPS_SMART, hit, json, mockApi, renderAt, RESULT, type Call } from './searchTestUtils';

type Body = { query: string | null; semantic: boolean; cursor: string | null };

beforeEach(() => {
  queryClient.clear();
  localStorage.clear();
  setLanguage('en');
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

/** Answers `/search` with `result` (or a function of the request body) and records every request body. */
function searchApi(result: object | ((body: Body) => object), caps = CAPS_SMART) {
  const bodies: Body[] = [];
  mockApi(
    (call: Call) => {
      if (call.pathname !== '/api/v1/search') return undefined;
      return (async () => {
        const body = (await call.json()) as Body;
        bodies.push(body);
        return json(typeof result === 'function' ? result(body) : result);
      })();
    },
    {},
    caps,
  );
  return bodies;
}

const smartResult = (hits: unknown[], extra: Record<string, unknown> = {}) => ({
  ...RESULT,
  hits,
  total: hits.length,
  semantic: true,
  semanticLegs: ['text', 'image'],
  semanticFallback: null,
  ...extra,
});

const toggle = () => screen.findByRole('switch', { name: 'Smart search' });

describe('Smart search toggle', () => {
  it('is not offered, and searches are plain, when the organisation cannot do Smart search', async () => {
    const bodies = searchApi(RESULT, CAPS_OFF);
    renderAt('/search?q=beach&smart=1');
    await screen.findByText('Hero shot');
    expect(screen.queryByRole('switch', { name: 'Smart search' })).not.toBeInTheDocument();
    expect(bodies.every((b) => b.semantic === false)).toBe(true);
    expect(screen.getByLabelText('Search assets')).toHaveAttribute('placeholder', 'Search by title, tag or type');
  });

  it('is offered, ON by default, and searches ask for meaning (one request, not a plain one first)', async () => {
    const bodies = searchApi(smartResult([hit()]));
    renderAt('/search?q=red%20SUV%20on%20a%20beach');
    const sw = await toggle();
    await screen.findByText('Hero shot');
    expect(sw).toHaveAttribute('aria-checked', 'true');
    expect(bodies).toHaveLength(1);
    expect(bodies[0]).toMatchObject({ query: 'red SUV on a beach', semantic: true });
    expect(screen.getByLabelText('Search assets')).toHaveAttribute('placeholder', "Describe what you're looking for");
    // The tooltip is the switch's accessible description.
    expect(sw).toHaveAccessibleDescription("Finds results by meaning, including what's in pictures and recordings");
  });

  it('switches off and on again, re-running the search and keeping the choice in the URL and this browser', async () => {
    const bodies = searchApi(smartResult([hit()]));
    renderAt('/search?q=beach');
    const sw = await toggle();
    await screen.findByText('Hero shot');

    await userEvent.click(sw);
    await waitFor(() => expect(bodies.at(-1)).toMatchObject({ query: 'beach', semantic: false }));
    expect(screen.getByTestId('location')).toHaveTextContent('/search?q=beach&smart=0');
    expect(localStorage.getItem('dam.search.smart')).toBe('0');
    expect(sw).toHaveAttribute('aria-checked', 'false');
    expect(screen.getByLabelText('Search assets')).toHaveAttribute('placeholder', 'Search by title, tag or type');

    await userEvent.click(sw);
    await waitFor(() => expect(sw).toHaveAttribute('aria-checked', 'true'));
    expect(screen.getByTestId('location')).toHaveTextContent('/search?q=beach&smart=1');
    expect(localStorage.getItem('dam.search.smart')).toBe('1');
  });

  it('remembers a previous choice in this browser, and lets the URL override it', async () => {
    localStorage.setItem('dam.search.smart', '0');
    const bodies = searchApi(RESULT);
    renderAt('/search?q=beach');
    expect(await toggle()).toHaveAttribute('aria-checked', 'false');
    await screen.findByText('Hero shot');
    expect(bodies.at(-1)?.semantic).toBe(false);

    cleanup();
    queryClient.clear();
    const bodies2 = searchApi(smartResult([hit()]));
    renderAt('/search?q=beach&smart=1');
    expect(await toggle()).toHaveAttribute('aria-checked', 'true');
    await screen.findByText('Hero shot');
    expect(bodies2.at(-1)?.semantic).toBe(true);
  });

  it('still works when browser storage is unavailable', async () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    const bodies = searchApi(smartResult([hit()]));
    renderAt('/search?q=beach');
    const sw = await toggle();
    await screen.findByText('Hero shot');
    expect(bodies.at(-1)?.semantic).toBe(true);
    await userEvent.click(sw);
    await waitFor(() => expect(bodies.at(-1)?.semantic).toBe(false));
    expect(screen.getByTestId('location')).toHaveTextContent('smart=0');
  });
});

describe('semantic fallback notes', () => {
  it.each([
    ['ai_disabled', "Smart search isn't switched on for your organisation. Showing regular results."],
    ['provider_not_configured', "Smart search isn't set up for your organisation yet. Showing regular results."],
    ['embeddings_disabled', "Smart search isn't switched on for your organisation. Showing regular results."],
    ['embeddings_unsupported', "Smart search isn't available with your organisation's current setup. Showing regular results."],
    ['provider_unavailable', 'Smart search is temporarily unavailable. Showing regular results.'],
    ['vectors_not_indexed', 'Smart search is still being prepared. Showing regular results.'],
  ])('says so quietly for %s', async (reason, sentence) => {
    searchApi({ ...RESULT, semantic: false, semanticLegs: null, semanticFallback: reason });
    renderAt('/search?q=beach');
    await screen.findByText('Hero shot');
    expect(screen.getByText(sentence)).toBeInTheDocument();
  });

  it.each(['empty_query', 'similar_to', 'something_new'])('stays silent for %s', async (reason) => {
    searchApi({ ...RESULT, semantic: false, semanticLegs: null, semanticFallback: reason });
    renderAt('/search?q=beach');
    await screen.findByText('Hero shot');
    expect(document.querySelector('.dam-searchnote')).toBeNull();
  });

  it('shows no note when Smart search is switched off, nor when it ran', async () => {
    searchApi({ ...RESULT, semantic: false, semanticFallback: 'ai_disabled' });
    renderAt('/search?q=beach&smart=0');
    await screen.findByText('Hero shot');
    expect(document.querySelector('.dam-searchnote')).toBeNull();

    cleanup();
    queryClient.clear();
    searchApi(smartResult([hit()]));
    renderAt('/search?q=beach');
    await screen.findByText('Hero shot');
    expect(document.querySelector('.dam-searchnote')).toBeNull();
  });
});

describe('why it matched', () => {
  it('shows a chip per way a hit matched, with accessible text, and none when matchedBy is absent', async () => {
    searchApi(
      smartResult([
        hit({ id: 'a1', title: 'Beach house', matchedBy: ['text', 'image'] }),
        hit({ id: 'a2', title: 'Price sheet', matchedBy: ['ocr'] }),
        hit({ id: 'a3', title: 'Keynote', matchedBy: ['transcript'] }),
        hit({ id: 'a4', title: 'Plain one' }),
      ]),
    );
    renderAt('/search?q=beach');
    await screen.findByText('Beach house');
    const chipsOf = (title: string) => {
      const card = screen.getByText(title).closest('li')!;
      return within(card)
        .queryAllByRole('listitem')
        .map((li) => li.textContent);
    };
    expect(chipsOf('Beach house')).toEqual(['Text', 'Visual match']);
    expect(chipsOf('Price sheet')).toEqual(['Text in image']);
    expect(chipsOf('Keynote')).toEqual(['Spoken in recording']);
    expect(chipsOf('Plain one')).toEqual([]);
    expect(within(screen.getByText('Beach house').closest('li')!).getByRole('list', { name: 'Matched by' })).toBeInTheDocument();
  });

  it('leaves out a lone "Text" chip on a plain search (it is true of every hit) but keeps the others', async () => {
    searchApi(
      {
        ...RESULT,
        hits: [
          hit({ id: 'a1', title: 'Only words', matchedBy: ['text'] }),
          hit({ id: 'a2', title: 'Read from image', matchedBy: ['text', 'ocr'] }),
        ],
      },
      CAPS_OFF,
    );
    renderAt('/search?q=beach');
    await screen.findByText('Only words');
    const only = screen.getByText('Only words').closest('li')!;
    expect(within(only).queryByRole('list', { name: 'Matched by' })).not.toBeInTheDocument();
    const read = screen.getByText('Read from image').closest('li')!;
    expect(
      within(read)
        .getAllByRole('listitem')
        .map((l) => l.textContent),
    ).toEqual(['Text', 'Text in image']);
  });

  it('keeps cards clean: chips and moments are outside the card button, so the button name is unchanged', async () => {
    searchApi(smartResult([hit({ matchedBy: ['image'], transcriptMatches: [{ startMs: 5000, text: 'hello' }] })]));
    renderAt('/search?q=beach');
    const button = await screen.findByRole('button', { name: /^Hero shot/ });
    expect(button).not.toHaveTextContent('Visual match');
    expect(button).not.toHaveTextContent('hello');
  });
});

describe('spoken moments', () => {
  const matches = [
    { startMs: 83500, text: 'the panoramic roof' },
    { startMs: 3_725_000, text: 'a second mention' },
  ];

  it('links each moment to the asset at that second, with the time and the matched text', async () => {
    searchApi(
      smartResult([
        hit({ id: 'v1', title: 'Launch recording', type: 'video', matchedBy: ['transcript'], transcriptMatches: matches }),
      ]),
    );
    renderAt('/search?q=panoramic');
    const list = await screen.findByRole('list', { name: 'Moments that match' });
    const links = within(list).getAllByRole('link');
    expect(links).toHaveLength(2);
    expect(links[0]).toHaveAttribute('href', '/library/assets/v1?t=83');
    expect(links[0]).toHaveTextContent('1:23');
    expect(links[0]).toHaveTextContent('the panoramic roof');
    expect(links[0]).toHaveAccessibleName(/Play from 1:23/);
    expect(links[1]).toHaveAttribute('href', '/library/assets/v1?t=3725');
    expect(links[1]).toHaveTextContent('1:02:05');
  });

  it('shows them in the list view too, under the title, next to the chips', async () => {
    searchApi(
      smartResult([
        hit({ id: 'v1', title: 'Launch recording', type: 'video', matchedBy: ['transcript'], transcriptMatches: matches }),
      ]),
    );
    renderAt('/search?q=panoramic&view=list');
    const table = await screen.findByRole('table', { name: 'Search results' });
    const row = within(table).getByText('Launch recording').closest('tr')!;
    expect(within(row).getByRole('link', { name: 'Launch recording' })).toHaveAttribute('href', '/library/assets/v1');
    expect(within(row).getByText('Spoken in recording')).toBeInTheDocument();
    expect(within(row).getAllByRole('link', { name: /Play from/ })[0]).toHaveAttribute('href', '/library/assets/v1?t=83');
  });
});

describe('views and paging', () => {
  it('switches between grid and list, keeping the view in the URL', async () => {
    searchApi(smartResult([hit({ matchedBy: ['image'] })]));
    renderAt('/search?q=beach');
    await screen.findByText('Hero shot');
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'List' }));
    expect(await screen.findByRole('table', { name: 'Search results' })).toBeInTheDocument();
    expect(screen.getByTestId('location')).toHaveTextContent('view=list');
    expect(screen.getByText('Visual match')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Grid' }));
    await waitFor(() => expect(screen.queryByRole('table')).not.toBeInTheDocument());
    expect(screen.getByTestId('location')).not.toHaveTextContent('view=');
  });

  it('loads more with the cursor, still asking for Smart search', async () => {
    const bodies = searchApi((body) =>
      body.cursor
        ? smartResult([hit({ id: 'a2', title: 'Second page' })], { nextCursor: null })
        : smartResult([hit({ id: 'a1', title: 'First page' })], { nextCursor: 'cursor-1', total: 2 }),
    );
    renderAt('/search?q=beach');
    await screen.findByText('First page');
    await userEvent.click(screen.getByRole('button', { name: 'Load more' }));
    await screen.findByText('Second page');
    expect(bodies.at(-1)).toMatchObject({ cursor: 'cursor-1', semantic: true, query: 'beach' });
    expect(screen.getByText('First page')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument();
  });

  describe('saving a search', () => {
    const saveFrom = async (route: string) => {
      let saved: { query: Body & { filters: unknown; facets: unknown; similarTo: unknown } } | undefined;
      mockApi(
        (call) => {
          if (call.pathname === '/api/v1/search') return json(smartResult([hit()]));
          if (call.pathname === '/api/v1/collections' && call.method === 'POST') {
            return (async () => {
              saved = (await call.json()) as typeof saved;
              return json(
                {
                  id: 'c1',
                  name: 'x',
                  type: 'smart',
                  query: saved!.query,
                  assetIds: [],
                  visibility: 'private',
                  ownerId: 'u',
                  createdAt: '2026-01-01T00:00:00Z',
                  updatedAt: '2026-01-01T00:00:00Z',
                },
                201,
              );
            })();
          }
          return undefined;
        },
        {},
        CAPS_SMART,
      );
      renderAt(route);
      await screen.findByText('Hero shot');
      await userEvent.click(screen.getByRole('button', { name: 'Save this search' }));
      const dialog = await screen.findByRole('dialog');
      await userEvent.type(within(dialog).getByLabelText('Name'), 'Beach');
      await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }));
      await waitFor(() => expect(saved).toBeDefined());
      return saved!;
    };

    it('stores the whole request, with semantic on when Smart search was on', async () => {
      const saved = await saveFrom('/search?q=beach&type=image&smart=1');
      expect(saved.query).toMatchObject({
        query: 'beach',
        filters: { type: ['image'] },
        semantic: true,
        cursor: null,
        similarTo: null,
      });
      expect(saved.query.facets).toEqual(['type', 'status']);
    });

    it('stores semantic off when Smart search was turned off', async () => {
      const saved = await saveFrom('/search?q=beach&smart=0');
      expect(saved.query).toMatchObject({ query: 'beach', semantic: false });
    });
  });
});

describe('empty states and examples', () => {
  it('suggests describing it differently when a Smart search finds nothing', async () => {
    searchApi(smartResult([], { facets: [] }));
    renderAt('/search?q=zzzz');
    expect(await screen.findByRole('heading', { name: 'Nothing matches that description' })).toBeInTheDocument();
    expect(screen.getByText(/describing it a different way/)).toBeInTheDocument();
    expect(screen.getByText(/haven't been analysed yet/)).toBeInTheDocument();
  });

  it('keeps the regular copy when a plain search finds nothing', async () => {
    searchApi({ hits: [], total: 0, facets: [], nextCursor: null }, CAPS_OFF);
    renderAt('/search?q=zzzz');
    expect(await screen.findByRole('heading', { name: 'No assets match this search' })).toBeInTheDocument();
  });

  it('offers three example descriptions on an empty Search page, and a click runs one', async () => {
    const bodies = searchApi(smartResult([hit()]));
    renderAt('/search');
    const group = await screen.findByRole('group', { name: 'Try' });
    expect(
      within(group)
        .getAllByRole('button')
        .map((b) => b.textContent),
    ).toEqual(['a car on a mountain road', 'photos of people smiling', 'signed contract']);
    await userEvent.click(within(group).getByRole('button', { name: 'signed contract' }));
    await waitFor(() => expect(bodies.at(-1)).toMatchObject({ query: 'signed contract', semantic: true }));
    expect(screen.getByLabelText('Search assets')).toHaveValue('signed contract');
    expect(screen.queryByRole('group', { name: 'Try' })).not.toBeInTheDocument();
  });

  it('offers no examples when Smart search is unavailable or switched off', async () => {
    searchApi(RESULT, CAPS_OFF);
    renderAt('/search');
    await screen.findByText('Hero shot');
    expect(screen.queryByRole('group', { name: 'Try' })).not.toBeInTheDocument();

    cleanup();
    queryClient.clear();
    searchApi(RESULT);
    renderAt('/search?smart=0');
    await screen.findByText('Hero shot');
    expect(screen.queryByRole('group', { name: 'Try' })).not.toBeInTheDocument();
  });
});

describe('global search box', () => {
  it('keeps Enter -> /search?q= and the standard placeholder when Smart search is unavailable', async () => {
    mockApi((call) => (call.pathname === '/api/v1/search' ? json(RESULT) : undefined), {}, CAPS_OFF);
    renderAt('/nope');
    const box = await screen.findByLabelText('Search everything');
    expect(box).toHaveAttribute('placeholder', 'Search assets, tags, folders…');
    await userEvent.type(box, 'red car{Enter}');
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/search?q=red%20car'));
  });

  it('invites a description when Smart search is available, and carries the Search page choice along', async () => {
    mockApi((call) => (call.pathname === '/api/v1/search' ? json(smartResult([hit()])) : undefined), {}, CAPS_SMART);
    renderAt('/search?q=beach&smart=0');
    const box = await screen.findByLabelText('Search everything');
    await waitFor(() => expect(box).toHaveAttribute('placeholder', "Search by name, tag, or describe what you're looking for"));
    await userEvent.clear(box);
    await userEvent.type(box, 'green hills{Enter}');
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/search?q=green%20hills&smart=0'));
  });
});

describe('global search box on a phone', () => {
  it('keeps the short standard placeholder, which fits, even when Smart search is available', async () => {
    vi.stubGlobal('matchMedia', (query: string) => ({
      matches: false,
      media: query,
      addEventListener: () => {},
      removeEventListener: () => {},
    }));
    mockApi((call) => (call.pathname === '/api/v1/search' ? json(smartResult([hit()])) : undefined), {}, CAPS_SMART);
    renderAt('/search?q=beach');
    await screen.findByText('Hero shot');
    expect(screen.getByLabelText('Search everything')).toHaveAttribute('placeholder', 'Search assets, tags, folders…');
  });
});

describe('accessibility', () => {
  it('has no axe violations on smart results with chips and moments (grid and list)', async () => {
    searchApi(
      smartResult([
        hit({ id: 'a1', title: 'Beach house', matchedBy: ['text', 'image'] }),
        hit({
          id: 'v1',
          title: 'Launch recording',
          type: 'video',
          matchedBy: ['transcript', 'ocr'],
          transcriptMatches: [{ startMs: 83500, text: 'the panoramic roof' }],
        }),
      ]),
    );
    const { container } = renderAt('/search?q=beach');
    await screen.findByText('Launch recording');
    expect(await axe(container)).toHaveNoViolations();
    await userEvent.click(screen.getByRole('button', { name: 'List' }));
    await screen.findByRole('table');
    expect(await axe(container)).toHaveNoViolations();
  });

  it('has no axe violations on the empty page with examples and on a fallback note', async () => {
    searchApi({ ...RESULT, semantic: false, semanticFallback: 'provider_unavailable' });
    const { container } = renderAt('/search?q=beach');
    await screen.findByText(/temporarily unavailable/);
    expect(await axe(container)).toHaveNoViolations();
    cleanup();
    queryClient.clear();
    searchApi(smartResult([hit()]));
    const second = renderAt('/search');
    await screen.findByRole('group', { name: 'Try' });
    expect(await axe(second.container)).toHaveNoViolations();
  });
});
