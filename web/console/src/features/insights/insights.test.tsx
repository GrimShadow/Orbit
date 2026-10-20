import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { queryClient } from '../../App';
import { setLanguage } from '../../i18n';
import { renderAt } from '../workflow/workflowTestKit';
import {
  READER,
  MANAGER,
  callsTo,
  emptyKpis,
  install,
  json,
  kpis,
  newWorld,
  storage,
  topAssets,
  type World,
} from './insightsTestKit';

beforeEach(() => {
  queryClient.clear();
  setLanguage('en');
  window.localStorage.clear();
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const kpiCalls = (w: World) => callsTo(w, '/api/v1/analytics/kpis');
const card = (name: string) => screen.findByRole('region', { name });

describe('Key results page', () => {
  it('shows the four key results with headline figures and supporting figures', async () => {
    const w = newWorld();
    install(w);
    renderAt('/insights/kpis');
    expect(await screen.findByRole('heading', { level: 1, name: 'Key results' })).toBeInTheDocument();

    const publishing = within(await card('Publishing effort'));
    expect(publishing.getByText('5.5 h')).toBeInTheDocument();
    expect(publishing.getByText('Across 12 assets first published')).toBeInTheDocument();
    expect(publishing.getByText('30 h')).toBeInTheDocument(); // p90
    expect(publishing.getByText('75%')).toBeInTheDocument();

    const dups = within(await card('Duplicate reduction'));
    expect(dups.getByText('12')).toBeInTheDocument();
    expect(dups.getByText('12 of 80 finished uploads were already in the library')).toBeInTheDocument();
    expect(dups.getByText('15%')).toBeInTheDocument();
    expect(dups.getByText('5.0 MB')).toBeInTheDocument();
    expect(dups.getByText('Duplicates still in the library (now)').nextElementSibling).toHaveTextContent('3');

    const reuse = within(await card('Asset reuse'));
    expect(reuse.getByText('65%')).toBeInTheDocument();
    expect(reuse.getByText('3.3')).toBeInTheDocument();
    expect(reuse.getByText('Used on two or more channels').nextElementSibling).toHaveTextContent('9');

    const sla = within(await card('Workflow on-time rate'));
    expect(sla.getByText('90%')).toBeInTheDocument();
    expect(sla.getByText('Open tasks overdue right now').nextElementSibling).toHaveTextContent('1');

    expect(kpiCalls(w)[0]!.search.get('days')).toBe('30');
    expect(
      screen.getByText(/Sep 21, 2026 to Oct 20, 2026 \(30 days\), compared with Aug 22, 2026 to Sep 20, 2026/),
    ).toBeInTheDocument();
  });

  it('says in words whether each change is better or worse, per metric (lower hours are better, lower reuse is worse)', async () => {
    install(newWorld());
    renderAt('/insights/kpis');

    // Median hours fell 11 -> 5.5 (-50%): good news because lower is better.
    const publishing = within(await card('Publishing effort'));
    expect(publishing.getByText('down 50% versus the previous 30 days')).toBeInTheDocument();
    expect(publishing.getByText('Better')).toBeInTheDocument();
    expect(publishing.getByText('Previous 30 days: 11 h')).toBeInTheDocument();

    // Duplicates prevented doubled: better.
    const dups = within(await card('Duplicate reduction'));
    expect(dups.getByText('up 100% versus the previous 30 days')).toBeInTheDocument();
    expect(dups.getByText('Better')).toBeInTheDocument();

    // Reuse fell 80% -> 65%: worse, though the arrow is "down" just like the good publishing one.
    const reuse = within(await card('Asset reuse'));
    expect(reuse.getByText('down 19% versus the previous 30 days')).toBeInTheDocument();
    expect(reuse.getByText('Worse')).toBeInTheDocument();
    expect(reuse.getByText('Previous 30 days: 80%')).toBeInTheDocument();

    const sla = within(await card('Workflow on-time rate'));
    expect(sla.getByText('Worse')).toBeInTheDocument();
  });

  it('reads a rise in publishing hours as worse and a rise in reuse as better', async () => {
    const k = kpis();
    install(
      newWorld({
        kpis: {
          ...k,
          publishingEffort: {
            ...k.publishingEffort,
            medianHours: 22,
            previous: { ...k.publishingEffort.previous, medianHours: 11 },
          },
          reuse: { ...k.reuse, rate: 0.9, previous: { ...k.reuse.previous, rate: 0.6 } },
        },
      }),
    );
    renderAt('/insights/kpis');
    expect(within(await card('Publishing effort')).getByText('Worse')).toBeInTheDocument();
    expect(within(await card('Asset reuse')).getByText('Better')).toBeInTheDocument();
  });

  it('shows "No data yet" with the reason instead of 0% when there is nothing to measure', async () => {
    install(newWorld({ kpis: emptyKpis() }));
    renderAt('/insights/kpis');

    const publishing = within(await card('Publishing effort'));
    expect(publishing.getAllByText('No data yet').length).toBeGreaterThanOrEqual(3); // headline, p90, first-pass
    expect(publishing.getByText('No assets were published for the first time in this period.')).toBeInTheDocument();
    expect(publishing.queryByText('0%')).not.toBeInTheDocument();

    const reuse = within(await card('Asset reuse'));
    expect(reuse.getByText('No assets were used in this period.')).toBeInTheDocument();
    expect(reuse.queryByText('0%')).not.toBeInTheDocument();
    expect(reuse.getByText('Nothing to compare yet')).toBeInTheDocument();

    const sla = within(await card('Workflow on-time rate'));
    expect(sla.getByText('No approval tasks with a due time were decided in this period.')).toBeInTheDocument();

    const dups = within(await card('Duplicate reduction'));
    expect(dups.getByText('No uploads finished in this period, so there is no rate yet.')).toBeInTheDocument();
    expect(dups.queryByText('0%')).not.toBeInTheDocument();
  });

  it('explains how each figure is measured in a disclosure that opens from the keyboard', async () => {
    install(newWorld());
    renderAt('/insights/kpis');
    const publishing = within(await card('Publishing effort'));
    const summary = publishing.getByText('How this is measured');
    expect(summary.closest('details')).not.toHaveAttribute('open');
    summary.focus();
    await userEvent.keyboard('{Enter}');
    // jsdom does not toggle <details> on Enter, so assert on the content being present in the document for assistive tech.
    expect(publishing.getByText(/hours from when it was created to when it first became published/)).toBeInTheDocument();
    expect(within(await card('Duplicate reduction')).getByText(/snapshot of today, not of the period/)).toBeInTheDocument();
    expect(within(await card('Asset reuse')).getByText(/Previews and thumbnails are not uses/)).toBeInTheDocument();
    expect(within(await card('Workflow on-time rate')).getByText(/decided on or before it/)).toBeInTheDocument();
  });

  it('asks again for 90 and 365 days and for a custom number of days, and keeps the choice', async () => {
    const w = newWorld();
    install(w);
    renderAt('/insights/kpis');
    await card('Publishing effort');
    expect(screen.getByRole('button', { name: '30 days', pressed: true })).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: '90 days' }));
    await waitFor(() => expect(kpiCalls(w).some((c) => c.search.get('days') === '90')).toBe(true));
    expect(window.localStorage.getItem('dam.insightsDays')).toBe('90');
    await userEvent.click(screen.getByRole('button', { name: '365 days' }));
    await waitFor(() => expect(kpiCalls(w).some((c) => c.search.get('days') === '365')).toBe(true));

    await userEvent.click(screen.getByRole('button', { name: 'Custom' }));
    const input = screen.getByRole('spinbutton', { name: 'Number of days' });
    await userEvent.clear(input);
    await userEvent.type(input, '45');
    await waitFor(() => expect(kpiCalls(w).some((c) => c.search.get('days') === '45')).toBe(true));
    await userEvent.clear(input);
    await userEvent.type(input, '400');
    expect(await screen.findByRole('alert')).toHaveTextContent('1 to 365');
    expect(kpiCalls(w).some((c) => c.search.get('days') === '400')).toBe(false);
  });

  it('opens on the period in the link and on the one remembered last time', async () => {
    const w = newWorld();
    install(w);
    renderAt('/insights/kpis?days=90');
    await card('Publishing effort');
    expect(kpiCalls(w)[0]!.search.get('days')).toBe('90');
    cleanup();
    queryClient.clear();
    window.localStorage.setItem('dam.insightsDays', '365');
    const w2 = newWorld();
    install(w2);
    renderAt('/insights/kpis');
    await card('Publishing effort');
    expect(kpiCalls(w2)[0]!.search.get('days')).toBe('365');
  });

  it('shows a loading status first, then an error with retry', async () => {
    const w = newWorld({ kpis: json({ title: 'Boom', status: 500 }, 500) });
    install(w);
    renderAt('/insights/kpis');
    const alert = await screen.findByRole('alert', {}, { timeout: 15_000 });
    expect(within(alert).getByText("Couldn't load key results")).toBeInTheDocument();
    w.kpis = kpis();
    await userEvent.click(within(alert).getByRole('button', { name: 'Try again' }));
    expect(await card('Publishing effort')).toBeInTheDocument();
  }, 30_000);

  it('tells someone without access instead of showing an error', async () => {
    install(newWorld({ kpis: json({ title: 'Forbidden', status: 403 }, 403) }), ['assets.read']);
    renderAt('/insights/kpis');
    expect(await screen.findByText("Insights aren't available to your role")).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    install(newWorld());
    const { container } = renderAt('/insights/kpis');
    await card('Publishing effort');
    expect(await axe(container)).toHaveNoViolations();
  });

  it('has no axe violations when there is no data', async () => {
    install(newWorld({ kpis: emptyKpis() }));
    const { container } = renderAt('/insights/kpis');
    await card('Publishing effort');
    expect(await axe(container)).toHaveNoViolations();
  });
});

describe('Insights navigation', () => {
  it('lists Insights in the sidebar for analytics.read and opens Key results', async () => {
    install(newWorld());
    renderAt('/me');
    const link = await screen.findByRole('link', { name: 'Insights' });
    expect(link).toHaveAttribute('href', '/insights');
    await userEvent.click(link);
    expect(await screen.findByRole('heading', { level: 1, name: 'Key results' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Insights' })).toHaveAttribute('aria-current', 'page');
  });

  it('keeps the sidebar entry out for roles without analytics.read', async () => {
    install(newWorld(), ['assets.read', 'glossary.manage']);
    renderAt('/me');
    // The glossary entry only appears once the permissions have loaded, so by then Insights would be there too.
    expect(await screen.findByRole('link', { name: 'Glossary' })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Insights' })).not.toBeInTheDocument();
  });

  it('shows the three pages as tabs on each page, marks the current one and carries the period between them', async () => {
    install(newWorld());
    renderAt('/insights/kpis?days=90');
    const nav = await screen.findByRole('navigation', { name: 'Insights pages' });
    const links = within(nav).getAllByRole('link');
    expect(links.map((l) => l.textContent)).toEqual(['Key results', 'Unused assets', 'Reports']);
    expect(within(nav).getByRole('link', { name: 'Key results' })).toHaveAttribute('aria-current', 'page');
    expect(within(nav).getByRole('link', { name: 'Reports' })).toHaveAttribute('href', '/insights/reports?days=90');
    expect(within(nav).getByRole('link', { name: 'Unused assets' })).toHaveAttribute('href', '/insights/unused');

    await userEvent.click(within(nav).getByRole('link', { name: 'Reports' }));
    expect(await screen.findByRole('heading', { level: 1, name: 'Reports' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '90 days', pressed: true })).toBeInTheDocument();
    expect(
      within(screen.getByRole('navigation', { name: 'Insights pages' })).getByRole('link', { name: 'Reports' }),
    ).toHaveAttribute('aria-current', 'page');
  });

  it('keeps the unused-assets page at its URL with the tabs above it', async () => {
    install(newWorld(), READER, (c) =>
      c.pathname === '/api/v1/analytics/unused-assets'
        ? json({ items: [], total: 0, totalIsExact: true, nextCursor: null, cutoff: '2026-01-01T00:00:00Z' })
        : undefined,
    );
    renderAt('/insights/unused');
    expect(await screen.findByRole('heading', { level: 1, name: 'Unused assets' })).toBeInTheDocument();
    expect(
      within(screen.getByRole('navigation', { name: 'Insights pages' })).getByRole('link', { name: 'Unused assets' }),
    ).toHaveAttribute('aria-current', 'page');
  });

  it('redirects /insights to Key results', async () => {
    install(newWorld());
    renderAt('/insights');
    expect(await screen.findByRole('heading', { level: 1, name: 'Key results' })).toBeInTheDocument();
  });
});

describe('Reports page', () => {
  it('lists the reports and previews the first as a table formatted by column type', async () => {
    const w = newWorld();
    install(w);
    renderAt('/insights/reports');
    expect(await screen.findByRole('heading', { level: 1, name: 'Reports' })).toBeInTheDocument();
    const select = await screen.findByRole('combobox', { name: 'Report' });
    expect(
      within(select)
        .getAllByRole('option')
        .map((o) => o.textContent),
    ).toEqual(['Most used assets', 'Storage', 'Key results', 'Unused assets']);

    const table = await screen.findByRole('table', { name: 'Most used assets report' });
    expect(
      within(table)
        .getAllByRole('columnheader')
        .map((h) => h.textContent),
    ).toEqual(['Asset', 'Uses', 'Last used', 'Size', 'Share']);
    const row = within(table).getByRole('row', { name: /Acme spring hero/ });
    expect(within(row).getByText('1,234')).toBeInTheDocument();
    expect(within(row).getByText('2.0 KB')).toBeInTheDocument();
    expect(within(row).getByText('42.5%')).toBeInTheDocument();
    expect(within(row).getByText(/Oct 19, 2026/)).toBeInTheDocument();
    expect(within(table).getByText('3.0 MB')).toBeInTheDocument();
    expect(callsTo(w, '/api/v1/analytics/reports/top-assets')[0]!.search.get('days')).toBe('30');
    expect(screen.queryByRole('group', { name: /Storage by folder/ })).not.toBeInTheDocument();
  });

  it('runs the chosen report for the chosen period', async () => {
    const w = newWorld();
    install(w);
    renderAt('/insights/reports');
    await screen.findByRole('table', { name: 'Most used assets report' });
    await userEvent.click(screen.getByRole('button', { name: '90 days' }));
    await waitFor(() =>
      expect(callsTo(w, '/api/v1/analytics/reports/top-assets').some((c) => c.search.get('days') === '90')).toBe(true),
    );
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Report' }), 'kpis');
    await waitFor(() => expect(callsTo(w, '/api/v1/analytics/reports/kpis').length).toBeGreaterThan(0));
    expect(callsTo(w, '/api/v1/analytics/reports/kpis')[0]!.search.get('days')).toBe('90');
  });

  it('shows storage by folder as a bar view above the table for the storage report only', async () => {
    install(newWorld());
    renderAt('/insights/reports?report=storage');
    const group = await screen.findByRole('group', {
      name: 'Storage by folder, top 2: Acme Campaigns 300.0 MB, Acme Brand 100.0 MB',
    });
    expect(within(group).getByText('Acme Campaigns')).toBeInTheDocument();
    expect(within(group).getByText('300.0 MB')).toBeInTheDocument();
    expect(within(group).queryByText('Acme Campaigns / Spring')).not.toBeInTheDocument();
    expect(screen.getByRole('table', { name: 'Storage report' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Storage by folder' })).toBeInTheDocument();
  });

  it('downloads the CSV through the authenticated client as <report>-<date>.csv, keeping the byte-order mark', async () => {
    const w = newWorld();
    install(w);
    const blobs: Blob[] = [];
    URL.createObjectURL = vi.fn((b: Blob | MediaSource) => {
      blobs.push(b as Blob);
      return 'blob:csv';
    });
    URL.revokeObjectURL = vi.fn();
    const downloads: string[] = [];
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
      downloads.push(this.download);
    });
    renderAt('/insights/reports');
    await screen.findByRole('table', { name: 'Most used assets report' });
    await userEvent.click(screen.getByRole('button', { name: '90 days' }));
    await userEvent.click(screen.getByRole('button', { name: 'Download CSV' }));

    await waitFor(() => expect(downloads).toHaveLength(1));
    expect(downloads[0]).toBe(`top-assets-${new Date().toISOString().slice(0, 10)}.csv`);
    const csvCall = callsTo(w, '/api/v1/analytics/reports/top-assets').find((c) => c.search.get('format') === 'csv')!;
    expect(csvCall.search.get('days')).toBe('90');
    const bytes = new Uint8Array(await blobs[0]!.arrayBuffer());
    expect([...bytes.slice(0, 3)]).toEqual([0xef, 0xbb, 0xbf]);
    expect(await screen.findByText(/Downloaded top-assets-/)).toBeInTheDocument();
  });

  it('says so when the download fails', async () => {
    install(newWorld({ csv: json({ title: 'Boom', status: 500 }, 500) }));
    renderAt('/insights/reports');
    await screen.findByRole('table', { name: 'Most used assets report' });
    await userEvent.click(screen.getByRole('button', { name: 'Download CSV' }));
    expect(await screen.findByText("Couldn't download the report")).toBeInTheDocument();
  });

  it('says plainly when the report has no rows', async () => {
    install(newWorld({ report: { 'top-assets': { ...topAssets(), rows: [] } } }));
    renderAt('/insights/reports');
    expect(await screen.findByText('Nothing to report for this period.')).toBeInTheDocument();
  });

  it('previews the first 100 rows and points at the CSV for the rest', async () => {
    const rows = Array.from({ length: 130 }, (_, i) => ({
      title: `Acme ${i}`,
      uses: i,
      lastUsedAt: null,
      sizeBytes: 1,
      share: 0,
    }));
    install(newWorld({ report: { 'top-assets': { ...topAssets(), rows } } }));
    renderAt('/insights/reports');
    expect(await screen.findByText('Showing the first 100 of 130 rows. Download the CSV for all of them.')).toBeInTheDocument();
    expect(screen.getByText('Acme 99')).toBeInTheDocument();
    expect(screen.queryByText('Acme 100')).not.toBeInTheDocument();
  });

  it('tells the reader when the server capped the rows it listed', async () => {
    install(newWorld({ report: { 'top-assets': { ...topAssets(), truncated: true } } }));
    renderAt('/insights/reports');
    expect(await screen.findByText(/lists only its first rows/)).toBeInTheDocument();
  });

  it('shows an error with retry when a report cannot run, and keeps the picker working', async () => {
    const w = newWorld({ report: { 'top-assets': json({ title: 'Boom', status: 500 }, 500) } });
    install(w);
    renderAt('/insights/reports');
    const alert = await screen.findByRole('alert', {}, { timeout: 15_000 });
    expect(within(alert).getByText("Couldn't run this report")).toBeInTheDocument();
    w.report = {};
    await userEvent.click(within(alert).getByRole('button', { name: 'Try again' }));
    expect(await screen.findByRole('table', { name: 'Most used assets report' })).toBeInTheDocument();
  }, 30_000);

  it('shows an empty state when there are no reports and a no-access state on 403', async () => {
    install(newWorld({ reports: [] }));
    renderAt('/insights/reports');
    expect(await screen.findByText('No reports are available')).toBeInTheDocument();
    cleanup();
    queryClient.clear();
    install(newWorld({ reports: json({ title: 'Forbidden', status: 403 }, 403) }), ['assets.read']);
    renderAt('/insights/reports');
    expect(await screen.findByText("Insights aren't available to your role")).toBeInTheDocument();
  });

  it('shows the schedules section only to people who can manage reports', async () => {
    const w = newWorld();
    install(w, READER);
    renderAt('/insights/reports');
    await screen.findByRole('table', { name: 'Most used assets report' });
    expect(screen.queryByRole('heading', { name: 'Scheduled reports' })).not.toBeInTheDocument();
    expect(callsTo(w, '/api/v1/analytics/report-schedules')).toHaveLength(0);
    cleanup();
    queryClient.clear();
    const w2 = newWorld();
    install(w2, MANAGER);
    renderAt('/insights/reports');
    expect(await screen.findByRole('heading', { name: 'Scheduled reports' })).toBeInTheDocument();
  });

  it('has no axe violations (report table, storage bars and schedules)', async () => {
    install(newWorld({ schedules: [] }), MANAGER);
    const { container } = renderAt('/insights/reports?report=storage');
    await screen.findByRole('group', { name: /Storage by folder/ });
    await screen.findByText('No scheduled reports yet');
    expect(await axe(container)).toHaveNoViolations();
  });
});

describe('Insights in Hindi', () => {
  it('renders the sidebar entry, key results and the disclosure in Hindi', async () => {
    setLanguage('hi');
    install(newWorld());
    renderAt('/insights/kpis');
    expect(await screen.findByRole('heading', { level: 1, name: 'मुख्य परिणाम' })).toBeInTheDocument();
    expect(await screen.findByRole('region', { name: 'प्रकाशन में लगा प्रयास' })).toBeInTheDocument();
    expect(screen.getAllByText('इसे कैसे मापा जाता है').length).toBe(4);
    expect(screen.getByRole('button', { name: '30 दिन', pressed: true })).toBeInTheDocument();
    expect(await screen.findByRole('link', { name: 'अंतर्दृष्टि' })).toHaveAttribute('href', '/insights');
    expect(screen.getAllByText('बेहतर').length).toBeGreaterThan(0);
  });

  it('renders the reports page and schedules in Hindi', async () => {
    setLanguage('hi');
    install(newWorld({ schedules: [] }), MANAGER);
    renderAt('/insights/reports');
    expect(await screen.findByRole('heading', { level: 1, name: 'रिपोर्ट' })).toBeInTheDocument();
    expect(await screen.findByRole('button', { name: 'CSV डाउनलोड करें' })).toBeInTheDocument();
    expect(await screen.findByRole('heading', { name: 'निर्धारित रिपोर्ट' })).toBeInTheDocument();
  });
});

describe('Home dashboard', () => {
  it('offers a Key results link in the header for people with analytics access', async () => {
    install(newWorld());
    renderAt('/');
    const link = await screen.findByRole('link', { name: 'Key results' });
    expect(link).toHaveAttribute('href', '/insights/kpis');
  });
});

void storage;
