import { cleanup, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { queryClient } from '../../App';
import { setLanguage } from '../../i18n';
import { isSmartSavedSearch, savedSearchHref } from './savedSearch';
import { CAPS_SMART, hit, json, mockApi, renderAt, RESULT, type Call } from './searchTestUtils';

beforeEach(() => {
  queryClient.clear();
  localStorage.clear();
  setLanguage('en');
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

// What GET /collections returns for a saved search (captured from the API): the server's own search type, PascalCase.
const WIRE_SMART = {
  Text: 'green hills',
  Filters: { type: ['image'] },
  Facets: null,
  Sort: null,
  Highlight: false,
  SimilarTo: null,
  Cursor: null,
  Limit: null,
  Semantic: true,
};

describe('saved search address', () => {
  it("reads the API's own PascalCase shape", () => {
    expect(isSmartSavedSearch(WIRE_SMART)).toBe(true);
    expect(isSmartSavedSearch({ ...WIRE_SMART, Semantic: false })).toBe(false);
    expect(savedSearchHref(WIRE_SMART)).toBe('/search?q=green+hills&type=image&smart=1');
    expect(savedSearchHref({ ...WIRE_SMART, Semantic: false })).toBe('/search?q=green+hills&type=image&smart=0');
  });

  it('carries the words, the filters and the Smart search choice', () => {
    const href = savedSearchHref({
      query: ' red car ',
      filters: { type: ['image', 'video'], status: ['published'] },
      semantic: true,
    });
    expect(href).toBe('/search?q=red+car&type=image&type=video&status=published&smart=1');
  });

  it('opens an older saved search (no semantic key) with Smart search off, and ignores what it cannot read', () => {
    expect(savedSearchHref({ query: 'logo', filters: null })).toBe('/search?q=logo&smart=0');
    expect(savedSearchHref(null)).toBe('/search?smart=0');
    expect(savedSearchHref({ query: 5, filters: { type: 'image', region: ['eu'] }, semantic: 'yes' })).toBe('/search?smart=0');
    expect(isSmartSavedSearch({ semantic: true })).toBe(true);
    expect(isSmartSavedSearch({})).toBe(false);
    expect(isSmartSavedSearch(undefined)).toBe(false);
  });
});

const NOW = '2026-01-01T00:00:00Z';
const collection = (id: string, name: string, query: unknown) => ({
  id,
  name,
  type: 'smart',
  query,
  assetIds: [],
  visibility: 'private',
  ownerId: 'u1',
  createdAt: NOW,
  updatedAt: NOW,
});
const SMART = collection('c1', 'Beach scenes', { Text: 'beach', Filters: { type: ['image'] }, Semantic: true });
const OLD = collection('c2', 'Old logos', { Text: 'logo', Filters: null });

function api(searches: { semantic: boolean; query: string | null }[] = []) {
  mockApi(
    (call: Call) => {
      if (call.pathname === '/api/v1/collections') return json([SMART, OLD]);
      const one = /^\/api\/v1\/collections\/(c\d)$/.exec(call.pathname);
      if (one) return json(one[1] === 'c1' ? SMART : OLD);
      if (/^\/api\/v1\/collections\/c\d\/assets$/.test(call.pathname)) return json({ ...RESULT, hits: [hit()] });
      if (call.pathname === '/api/v1/search')
        return (async () => {
          searches.push((await call.json()) as (typeof searches)[number]);
          return json({ ...RESULT, semantic: true });
        })();
      return undefined;
    },
    {},
    CAPS_SMART,
  );
  return searches;
}

describe('saved searches in the console', () => {
  it('labels a Smart saved search in the collections list, and only that one', async () => {
    api();
    renderAt('/collections');
    await screen.findByRole('link', { name: 'Beach scenes' });
    expect(screen.getAllByText('Smart search')).toHaveLength(1);
    const card = screen.getByRole('link', { name: 'Beach scenes' }).closest('li')!;
    expect(card).toHaveTextContent('Smart search');
    expect(screen.getByRole('link', { name: 'Old logos' }).closest('li')).not.toHaveTextContent('Smart search');
  });

  it('opens a Smart saved search in Search with the Smart toggle on and the saved words and filters', async () => {
    const searches = api();
    renderAt('/collections/c1');
    expect(await screen.findByText('Smart search')).toBeInTheDocument();
    await userEvent.click(await screen.findByRole('link', { name: 'Open in Search' }));
    expect(await screen.findByRole('switch', { name: 'Smart search' })).toBeChecked();
    expect(screen.getByTestId('location')).toHaveTextContent('/search?q=beach&type=image&smart=1');
    await waitFor(() => expect(searches.at(-1)).toMatchObject({ query: 'beach', semantic: true }));
  });

  it('opens an older saved search (without the key) with Smart search off, even if it was on in this browser', async () => {
    localStorage.setItem('dam.search.smart', '1');
    const searches = api();
    renderAt('/collections/c2');
    await screen.findByRole('heading', { name: 'Old logos' });
    expect(screen.queryByText('Smart search')).not.toBeInTheDocument();
    await userEvent.click(await screen.findByRole('link', { name: 'Open in Search' }));
    expect(await screen.findByRole('switch', { name: 'Smart search' })).not.toBeChecked();
    expect(screen.getByTestId('location')).toHaveTextContent('/search?q=logo&smart=0');
    await waitFor(() => expect(searches.at(-1)).toMatchObject({ query: 'logo', semantic: false }));
  });

  it('shows the Open in Search link on smart collections only', async () => {
    mockApi(
      (call) => {
        if (call.pathname === '/api/v1/collections/c3')
          return json({ ...collection('c3', 'Static one', null), type: 'static', assetIds: ['a1'] });
        if (call.pathname === '/api/v1/collections/c3/assets') return json(RESULT);
        return undefined;
      },
      {},
      CAPS_SMART,
    );
    renderAt('/collections/c3');
    await screen.findByRole('heading', { name: 'Static one' });
    expect(screen.queryByRole('link', { name: 'Open in Search' })).not.toBeInTheDocument();
  });
});
