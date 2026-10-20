import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { act } from 'react';
import { axe } from 'vitest-axe';
import { queryClient } from '../../App';
import { setLanguage } from '../../i18n';
import { backend, newWorld as newAssetWorld } from '../translation/translationTestKit';
import { renderAt } from '../workflow/workflowTestKit';
import { READER, assetAnalytics, install, json, newWorld, type World } from './insightsTestKit';

const ANALYTICS = '/api/v1/analytics/assets/a1';

beforeEach(() => {
  queryClient.clear();
  setLanguage('en');
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

/** The asset page (asset "a1" and its neighbours from the translation kit) plus the analytics routes. */
function openAsset(w: World, permissions = READER) {
  const assetWorld = newAssetWorld();
  const calls = install(w, permissions, backend(assetWorld));
  renderAt('/library/assets/a1');
  return calls;
}
const usageCalls = (w: World) => w.calls.filter((c) => c.pathname === ANALYTICS);
const usageCard = () => screen.findByRole('region', { name: 'Usage' });

describe('Asset usage card', () => {
  it('shows uses in the last 30 days with a chart summary, first and last use, kinds, channels and the 5 latest events', async () => {
    const w = newWorld();
    openAsset(w);
    const card = within(await usageCard());
    expect(await card.findByText('12')).toBeInTheDocument();
    expect(card.getByText(/uses in the last 30 days/)).toBeInTheDocument();
    expect(
      card.getByRole('img', { name: 'Uses per day over the last 30 days: 12 in total. Busiest day Oct 19 with 9.' }),
    ).toBeInTheDocument();
    expect(card.getByText('First used').nextElementSibling).toHaveTextContent('Mar 2, 2026');
    expect(card.getByText('Last used').nextElementSibling).toHaveTextContent('Oct 20, 2026');

    const byType = card.getByRole('group', { name: 'By kind of use' });
    expect(within(byType).getByText('Download')).toBeInTheDocument();
    expect(within(byType).getByText('8')).toBeInTheDocument();
    const byChannel = card.getByRole('group', { name: 'By channel' });
    expect(within(byChannel).getByText('console')).toBeInTheDocument();
    expect(within(byChannel).getByText('No channel')).toBeInTheDocument();

    const recent = card.getByRole('list', { name: 'Most recent uses' });
    expect(within(recent).getAllByRole('listitem')).toHaveLength(5);
    expect(within(recent).getAllByText('1 hour ago').length).toBe(1);
    expect(within(recent).getByText('No channel')).toBeInTheDocument(); // a use with no channel
    expect(usageCalls(w)[0]!.search.get('days')).toBe('30');
  });

  it('switches to 90 days', async () => {
    const w = newWorld();
    openAsset(w);
    const card = within(await usageCard());
    await card.findByText('12');
    expect(card.getByRole('button', { name: '30 days', pressed: true })).toBeInTheDocument();
    await userEvent.click(card.getByRole('button', { name: '90 days' }));
    await waitFor(() => expect(usageCalls(w).some((c) => c.search.get('days') === '90')).toBe(true));
    expect(await card.findByRole('button', { name: '90 days', pressed: true })).toBeInTheDocument();
  });

  it('says "Not used yet" calmly when there has never been a use', async () => {
    const w = newWorld({
      asset: assetAnalytics({
        total: 0,
        allTimeTotal: 0,
        firstUsedAt: null,
        lastUsedAt: null,
        byType: [],
        byChannel: [],
        recent: [],
      }),
    });
    openAsset(w);
    const card = within(await usageCard());
    expect(await card.findByText('Not used yet')).toBeInTheDocument();
    expect(card.getByText(/No downloads, shares, embeds or playback/)).toBeInTheDocument();
    expect(card.queryByRole('img')).not.toBeInTheDocument();
    expect(card.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('says when the last use was before the chosen period', async () => {
    const w = newWorld({
      asset: assetAnalytics({
        total: 0,
        allTimeTotal: 5,
        byType: [],
        byChannel: [],
        recent: [],
        lastUsedAt: '2026-02-03T00:00:00Z',
      }),
    });
    openAsset(w);
    const card = within(await usageCard());
    expect(await card.findByText('Not used in the last 30 days')).toBeInTheDocument();
    expect(card.getByText('Last used on Feb 3, 2026.')).toBeInTheDocument();
  });

  it('shows an error with retry', async () => {
    const w = newWorld({ asset: json({ title: 'Boom', status: 500 }, 500) });
    openAsset(w);
    const card = within(await usageCard());
    expect(await card.findByRole('alert')).toHaveTextContent("Couldn't load usage.");
    w.asset = assetAnalytics();
    await userEvent.click(card.getByRole('button', { name: 'Try again' }));
    expect(await card.findByText('12')).toBeInTheDocument();
  });

  it('is absent without analytics.read, and asks for nothing', async () => {
    const w = newWorld();
    openAsset(w, ['assets.read']);
    await screen.findByRole('heading', { level: 1, name: 'Acme spring hero' });
    await screen.findByRole('tab', { name: 'Details' });
    // Let the permissions settle: the AI panel (ai capabilities) renders only after /me has loaded too.
    await new Promise((r) => setTimeout(r, 50));
    expect(screen.queryByRole('region', { name: 'Usage' })).not.toBeInTheDocument();
    expect(usageCalls(w)).toHaveLength(0);
  });

  it('is hidden when the server refuses with 403', async () => {
    const w = newWorld({ asset: json({ title: 'Forbidden', status: 403 }, 403) });
    openAsset(w);
    await waitFor(() => expect(usageCalls(w).length).toBeGreaterThan(0));
    await waitFor(() => expect(screen.queryByRole('region', { name: 'Usage' })).not.toBeInTheDocument());
  });

  it('loads lazily: only after the asset has rendered and the card is near the screen', async () => {
    let reveal: (() => void) | undefined;
    class FakeObserver {
      constructor(private cb: IntersectionObserverCallback) {}
      observe() {
        reveal = () => this.cb([{ isIntersecting: true } as IntersectionObserverEntry], this as unknown as IntersectionObserver);
      }
      disconnect() {}
      unobserve() {}
      takeRecords() {
        return [];
      }
    }
    vi.stubGlobal('IntersectionObserver', FakeObserver);
    const w = newWorld();
    const calls = openAsset(w);
    await screen.findByRole('heading', { level: 1, name: 'Acme spring hero' });
    const card = within(await usageCard());
    // The card is on the page (loading its skeleton) but has not asked the API for anything.
    expect(card.getByText('Loading usage')).toBeInTheDocument();
    await new Promise((r) => setTimeout(r, 50));
    expect(usageCalls(w)).toHaveLength(0);
    expect(reveal).toBeDefined();

    act(() => reveal!());
    expect(await card.findByText('12')).toBeInTheDocument();
    const order = calls.map((c) => c.pathname);
    expect(order.indexOf('/api/v1/assets/a1')).toBeGreaterThanOrEqual(0);
    expect(order.indexOf(ANALYTICS)).toBeGreaterThan(order.indexOf('/api/v1/assets/a1'));
  });

  it('renders in Hindi', async () => {
    setLanguage('hi');
    const w = newWorld({
      asset: assetAnalytics({
        total: 0,
        allTimeTotal: 0,
        lastUsedAt: null,
        firstUsedAt: null,
        byType: [],
        byChannel: [],
        recent: [],
      }),
    });
    openAsset(w);
    const card = within(await screen.findByRole('region', { name: 'उपयोग' }));
    expect(await card.findByText('अभी उपयोग नहीं हुआ')).toBeInTheDocument();
    expect(card.getByRole('button', { name: '30 दिन', pressed: true })).toBeInTheDocument();
  });

  it('has no axe violations when populated', async () => {
    const w = newWorld();
    openAsset(w);
    const region = await usageCard();
    await within(region).findByText('12');
    expect(await axe(region)).toHaveNoViolations();
  });
});
