import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { setLanguage } from '../../../i18n';
import { MAX_DOCUMENT_BYTES, fileProblem } from './DocumentsPanel';
import { copyName, countByStatus, filterRights, formatDate, narrows, previewStatus, slugify } from './rightsLabels';
import {
  day,
  doc,
  install,
  json,
  problem,
  renderAt,
  rights,
  storageCalls,
  urlOf,
  validation,
  world,
  type Call,
} from './rightsTestKit';

beforeEach(() => {
  setLanguage('en');
  window.localStorage.clear();
});
afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const writes = (calls: Call[], method: string, pathname: string) =>
  calls.filter((c) => c.method === method && c.pathname === pathname);
const dialog = (name: string) => screen.findByRole('dialog', { name });
const card = async (name: string) => (await screen.findByRole('heading', { name })).closest('article')!;

async function openMenu(name: string, item: string) {
  await userEvent.click(await screen.findByRole('button', { name: `Actions for ${name}` }));
  await userEvent.click(await screen.findByRole('menuitem', { name: item }));
}

const setDate = (label: string, value: string) => fireEvent.change(screen.getByLabelText(label), { target: { value } });

describe('rights list', () => {
  it('shows each record with licence, licensor, validity, channels, regions, watermark, and asset and document counts', async () => {
    install(
      world({
        rights: [
          rights({ assetCount: 4, documentCount: 2, watermarkRequired: true, notes: 'x' }),
          rights({
            id: 'r2',
            name: 'Acme own photography',
            licenceType: 'owned',
            licensor: '',
            channels: [],
            regions: [],
            validFrom: null,
            validTo: null,
            daysRemaining: null,
            assetCount: 1,
            documentCount: 0,
          }),
        ],
      }),
    );
    renderAt('/settings/rights');

    expect(await screen.findByRole('heading', { level: 1, name: 'Rights' })).toBeInTheDocument();
    const first = await card('Acme stock photography');
    expect(within(first).getByText('Licensed')).toBeInTheDocument();
    expect(within(first).getByText('from Acme Photos')).toBeInTheDocument();
    expect(within(first).getByText(`${formatDate(day(-30), 'en')} to ${formatDate(day(200), 'en')}`)).toBeInTheDocument();
    const channels = within(within(first).getByRole('list', { name: 'Channels' })).getAllByRole('listitem');
    expect(channels.map((c) => c.textContent)).toEqual(['web', 'print']);
    expect(within(within(first).getByRole('list', { name: 'Regions' })).getByText('north')).toBeInTheDocument();
    expect(within(first).getByText('Watermarked')).toBeInTheDocument();
    expect(within(first).getByText('4 assets')).toBeInTheDocument();
    expect(within(first).getByText('2 documents')).toBeInTheDocument();

    const second = await card('Acme own photography');
    expect(within(second).getByText('Owned')).toBeInTheDocument();
    expect(within(second).getByText('All channels')).toBeInTheDocument();
    expect(within(second).getByText('Worldwide')).toBeInTheDocument();
    expect(within(second).getByText('No start or end date')).toBeInTheDocument();
    expect(within(second).getByText('1 asset')).toBeInTheDocument();
    expect(within(second).getByText('0 documents')).toBeInTheDocument();
    expect(within(second).queryByText('Watermarked')).not.toBeInTheDocument();
  });

  it('collapses long channel lists into "+N" with the full list for assistive technology', async () => {
    install(world({ rights: [rights({ channels: ['a1', 'b2', 'c3', 'd4', 'e5', 'f6'] })] }));
    renderAt('/settings/rights');
    const el = await card('Acme stock photography');
    const list = within(el).getByRole('list', { name: 'Channels' });
    expect(
      within(list)
        .getAllByRole('listitem')
        .map((c) => c.textContent),
    ).toEqual(['a1', 'b2', 'c3', 'd4', '+2and 2 more: e5, f6']);
  });

  it('shows the status chip and the days left for every status', async () => {
    install(
      world({
        rights: [
          rights({ id: 'a', name: 'A valid', status: 'valid', daysRemaining: 120 }),
          rights({ id: 'b', name: 'B soon', status: 'expiring', daysRemaining: 5, validTo: day(5) }),
          rights({ id: 'c', name: 'C one', status: 'expiring', daysRemaining: 1, validTo: day(1) }),
          rights({ id: 'd', name: 'D today', status: 'expiring', daysRemaining: 0, validTo: day(0) }),
          rights({ id: 'e', name: 'E gone', status: 'expired', daysRemaining: -3, validTo: day(-3) }),
          rights({ id: 'f', name: 'F later', status: 'not_yet_valid', validFrom: day(10), daysRemaining: 100 }),
        ],
      }),
    );
    renderAt('/settings/rights');
    await card('A valid');
    const text = async (name: string) => (await card(name)).textContent ?? '';
    expect(await text('A valid')).toContain('Valid');
    expect(await text('A valid')).not.toContain('day');
    expect(await text('B soon')).toContain('Expiring: 5 days left');
    expect(await text('C one')).toContain('Expiring: 1 day left');
    expect(await text('D today')).toContain('Expiring: last day');
    expect(await text('E gone')).toContain('Expired');
    expect(await text('F later')).toContain('Not yet valid');
  });

  it('summarises how many are valid, expiring and expired', async () => {
    install(
      world({
        rights: [
          rights({ id: 'a', name: 'A', status: 'valid' }),
          rights({ id: 'b', name: 'B', status: 'valid' }),
          rights({ id: 'c', name: 'C', status: 'expiring', daysRemaining: 3 }),
          rights({ id: 'd', name: 'D', status: 'expired', daysRemaining: -3 }),
          rights({ id: 'e', name: 'E', status: 'not_yet_valid' }),
        ],
      }),
    );
    renderAt('/settings/rights');
    const strip = await screen.findByRole('list', { name: 'Rights summary' });
    const counts = within(strip)
      .getAllByRole('listitem')
      .map((li) => li.textContent?.replace(/\s+/g, ' ').trim());
    expect(counts).toEqual(['Valid2In force today', 'Expiring1Within 30 days', 'Expired1Past their end date']);
  });

  it('filters by status and by name, keeps both in the URL, and keeps the summary on the whole list', async () => {
    install(
      world({
        rights: [
          rights({ id: 'a', name: 'Acme spring', status: 'valid' }),
          rights({ id: 'b', name: 'Acme summer', status: 'expiring', daysRemaining: 9 }),
          rights({ id: 'c', name: 'Beta autumn', status: 'expired', licensor: 'Zed Images', daysRemaining: -9 }),
        ],
      }),
    );
    renderAt('/settings/rights');
    await card('Acme spring');

    await userEvent.selectOptions(screen.getByLabelText('Status'), 'expiring');
    expect(urlOf().searchParams.get('status')).toBe('expiring');
    expect(screen.queryByRole('heading', { name: 'Acme spring' })).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Acme summer' })).toBeInTheDocument();
    expect(screen.getByText('Showing 1 of 3')).toBeInTheDocument();
    expect(within(screen.getByRole('list', { name: 'Rights summary' })).getAllByRole('listitem')).toHaveLength(3);

    await userEvent.selectOptions(screen.getByLabelText('Status'), '');
    expect(urlOf().searchParams.has('status')).toBe(false);

    await userEvent.type(screen.getByRole('searchbox', { name: 'Search rights' }), 'zed');
    expect(urlOf().searchParams.get('q')).toBe('zed');
    expect(screen.getByRole('heading', { name: 'Beta autumn' })).toBeInTheDocument(); // matched on the licensor
    expect(screen.queryByRole('heading', { name: 'Acme summer' })).not.toBeInTheDocument();

    await userEvent.clear(screen.getByRole('searchbox', { name: 'Search rights' }));
    await userEvent.type(screen.getByRole('searchbox', { name: 'Search rights' }), 'nothing like this');
    expect(await screen.findByText('No rights match')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Clear filters' }));
    expect(urlOf().search).toBe('');
    expect(await screen.findByRole('heading', { name: 'Acme spring' })).toBeInTheDocument();
  });

  it('applies filters found in the URL', async () => {
    install(
      world({
        rights: [
          rights({ id: 'a', name: 'Acme spring', status: 'valid' }),
          rights({ id: 'b', name: 'Acme summer', status: 'expired', daysRemaining: -1 }),
        ],
      }),
    );
    renderAt('/settings/rights?status=expired&q=acme');
    expect(await screen.findByRole('heading', { name: 'Acme summer' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Acme spring' })).not.toBeInTheDocument();
    expect(screen.getByLabelText('Status')).toHaveValue('expired');
    expect(screen.getByRole('searchbox', { name: 'Search rights' })).toHaveValue('acme');
  });

  it('invites you to add the first record', async () => {
    install(world({ rights: [] }));
    renderAt('/settings/rights');
    expect(await screen.findByText('No rights yet')).toBeInTheDocument();
    expect(screen.getByText(/describes a licence/)).toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: 'New rights' })).toHaveLength(1);
  });

  it('shows skeletons while loading', async () => {
    install(world(), (c) =>
      c.pathname === '/api/v1/rights' && c.method === 'GET' ? new Promise<Response>(() => {}) : undefined,
    );
    renderAt('/settings/rights');
    expect(await screen.findByText('Loading rights')).toBeInTheDocument();
  });

  it('shows a calm no-access state on 403 and an error with retry on failure', async () => {
    install(world(), (c) => (c.pathname === '/api/v1/rights' && c.method === 'GET' ? problem(403, 'Forbidden') : undefined));
    renderAt('/settings/rights');
    expect(await screen.findByText('You do not have access to rights')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'New rights' })).not.toBeInTheDocument();
    cleanup();

    let fail = true;
    install(world(), (c) => (c.pathname === '/api/v1/rights' && c.method === 'GET' && fail ? problem(500, 'Boom') : undefined));
    renderAt('/settings/rights');
    expect(await screen.findByText('We could not load the rights', undefined, { timeout: 4000 })).toBeInTheDocument();
    fail = false;
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByRole('heading', { name: 'Acme stock photography' })).toBeInTheDocument();
  });
});

describe('creating rights', () => {
  it('sends exactly what was entered', async () => {
    const calls = install(world());
    renderAt('/settings/rights');
    await userEvent.click(await screen.findByRole('button', { name: 'New rights' }));
    const form = await dialog('New rights');

    await userEvent.type(within(form).getByLabelText('Name'), '  Acme spring licence ');
    await userEvent.selectOptions(within(form).getByLabelText('Licence type'), 'royalty_free');
    await userEvent.type(within(form).getByLabelText('Licensor'), 'Acme Photos');
    await userEvent.click(await within(form).findByRole('checkbox', { name: 'Website' }));
    await userEvent.click(within(form).getByRole('checkbox', { name: 'North' }));
    setDate('Valid from', '2026-01-01');
    setDate('Valid to', '2026-12-31');
    await userEvent.click(within(form).getByRole('switch', { name: 'Watermark required' }));
    await userEvent.type(within(form).getByLabelText('Notes'), 'Credit: Acme');
    await userEvent.click(within(form).getByRole('button', { name: 'Create rights' }));

    await waitFor(() => expect(writes(calls, 'POST', '/api/v1/rights')).toHaveLength(1));
    expect(await writes(calls, 'POST', '/api/v1/rights')[0]!.json()).toEqual({
      name: 'Acme spring licence',
      licenceType: 'royalty_free',
      licensor: 'Acme Photos',
      channels: ['web'],
      regions: ['north'],
      validFrom: '2026-01-01',
      validTo: '2026-12-31',
      watermarkRequired: true,
      notes: 'Credit: Acme',
    });
    expect((await screen.findAllByText('Created “Acme spring licence”')).length).toBeGreaterThan(0);
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'New rights' })).not.toBeInTheDocument());
    expect(await screen.findByRole('heading', { name: 'Acme spring licence' })).toBeInTheDocument();
  });

  it('sends empty lists and null dates when nothing is restricted, and watermark off by default', async () => {
    const calls = install(world());
    renderAt('/settings/rights');
    await userEvent.click(await screen.findByRole('button', { name: 'New rights' }));
    const form = await dialog('New rights');
    await userEvent.type(within(form).getByLabelText('Name'), 'Open licence');
    expect(within(form).getByRole('switch', { name: 'Watermark required' })).toHaveAttribute('aria-checked', 'false');
    await userEvent.click(within(form).getByRole('button', { name: 'Create rights' }));
    await waitFor(() => expect(writes(calls, 'POST', '/api/v1/rights')).toHaveLength(1));
    expect(await writes(calls, 'POST', '/api/v1/rights')[0]!.json()).toEqual({
      name: 'Open licence',
      licenceType: 'licensed',
      licensor: '',
      channels: [],
      regions: [],
      validFrom: null,
      validTo: null,
      watermarkRequired: false,
      notes: '',
    });
  });

  it('explains the watermark switch', async () => {
    install(world());
    renderAt('/settings/rights');
    await userEvent.click(await screen.findByRole('button', { name: 'New rights' }));
    const form = await dialog('New rights');
    expect(within(form).getByText('Previews are always watermarked; the original and video are withheld.')).toBeInTheDocument();
    const toggle = within(form).getByRole('switch', { name: 'Watermark required' });
    await userEvent.click(toggle);
    expect(toggle).toHaveAttribute('aria-checked', 'true');
  });

  it('offers the tenant’s channel and region terms, without "global"', async () => {
    install(world());
    renderAt('/settings/rights');
    await userEvent.click(await screen.findByRole('button', { name: 'New rights' }));
    const form = await dialog('New rights');
    const channels = await within(form).findByRole('group', { name: 'Allowed channels' });
    expect(
      within(channels)
        .getAllByRole('checkbox')
        .map((c) => (c as HTMLInputElement).value),
    ).toEqual(['web', 'print', 'signage']);
    const regions = within(form).getByRole('group', { name: 'Allowed regions' });
    expect(
      within(regions)
        .getAllByRole('checkbox')
        .map((c) => (c as HTMLInputElement).value),
    ).toEqual(['north', 'south']);
    expect(within(form).getByText('Nothing selected means every channel.')).toBeInTheDocument();
    expect(within(form).getByText('Nothing selected means worldwide.')).toBeInTheDocument();
  });

  it('falls back to free entry of lower-case codes when the tenant has no vocabulary', async () => {
    const calls = install(world({ channels: null, regions: null }));
    renderAt('/settings/rights');
    await userEvent.click(await screen.findByRole('button', { name: 'New rights' }));
    const form = await dialog('New rights');
    await userEvent.type(await within(form).findByLabelText('Allowed channels'), 'Web Site,Print{Enter}');
    await userEvent.type(within(form).getByLabelText('Allowed regions'), 'EMEA{Enter}');
    expect(within(form).getByRole('list', { name: 'Allowed channels (selected)' })).toHaveTextContent('web-siteprint');
    await userEvent.type(within(form).getByLabelText('Name'), 'Free entry');
    await userEvent.click(within(form).getByRole('button', { name: 'Create rights' }));
    await waitFor(() => expect(writes(calls, 'POST', '/api/v1/rights')).toHaveLength(1));
    const body = (await writes(calls, 'POST', '/api/v1/rights')[0]!.json()) as { channels: string[]; regions: string[] };
    expect(body.channels).toEqual(['web-site', 'print']);
    expect(body.regions).toEqual(['emea']);
  });

  it('checks the name and the dates before sending anything, and previews the status live', async () => {
    const calls = install(world());
    renderAt('/settings/rights');
    await userEvent.click(await screen.findByRole('button', { name: 'New rights' }));
    const form = await dialog('New rights');

    await userEvent.click(within(form).getByRole('button', { name: 'Create rights' }));
    expect(await within(form).findByText('Enter a name.')).toBeInTheDocument();

    await userEvent.type(within(form).getByLabelText('Name'), 'Dates');
    setDate('Valid from', '2026-06-10');
    setDate('Valid to', '2026-06-01');
    expect(await within(form).findByText('The end date cannot be before the start date.')).toBeInTheDocument();
    await userEvent.click(within(form).getByRole('button', { name: 'Create rights' }));
    expect(writes(calls, 'POST', '/api/v1/rights')).toHaveLength(0);

    setDate('Valid from', day(-5));
    setDate('Valid to', day(7));
    expect(within(form).queryByText('The end date cannot be before the start date.')).not.toBeInTheDocument();
    expect(within(form).getByRole('status')).toHaveTextContent('Status todayExpiring: 7 days left');
    setDate('Valid to', day(-1));
    expect(within(form).getByRole('status')).toHaveTextContent('Expired');
    setDate('Valid from', day(3));
    setDate('Valid to', day(60));
    expect(within(form).getByRole('status')).toHaveTextContent('Not yet valid');
    setDate('Valid to', '');
    setDate('Valid from', '');
    expect(within(form).getByRole('status')).toHaveTextContent('Valid');
  });

  it('shows the server’s messages on the fields they name and the rest at the top', async () => {
    install(world(), (c) =>
      c.method === 'POST' && c.pathname === '/api/v1/rights'
        ? validation({
            name: ['A rights record with this name already exists.'],
            validTo: ['The end date cannot be before the start date.'],
            channels: ['Unknown channel: tv. Known: web, print.'],
            regions: ["Leave the list empty to allow everything; 'global' is not a value."],
            somethingElse: ['Not a form field.'],
          })
        : undefined,
    );
    renderAt('/settings/rights');
    await userEvent.click(await screen.findByRole('button', { name: 'New rights' }));
    const form = await dialog('New rights');
    await userEvent.type(within(form).getByLabelText('Name'), 'Acme');
    await userEvent.click(within(form).getByRole('button', { name: 'Create rights' }));

    expect(await within(form).findByText('A rights record with this name already exists.')).toBeInTheDocument();
    expect(within(form).getByLabelText('Name')).toHaveAttribute('aria-invalid', 'true');
    expect(within(form).getByText('The end date cannot be before the start date.')).toBeInTheDocument();
    expect(within(form).getByText('Unknown channel: tv. Known: web, print.')).toBeInTheDocument();
    expect(within(form).getByText("Leave the list empty to allow everything; 'global' is not a value.")).toBeInTheDocument();
    expect(within(form).getByText('Not a form field.')).toBeInTheDocument();
    // Editing a field retires its message.
    await userEvent.type(within(form).getByLabelText('Name'), '2');
    expect(within(form).queryByText('A rights record with this name already exists.')).not.toBeInTheDocument();
  });

  it('reports a duplicate name returned by the server on the name field', async () => {
    install(world());
    renderAt('/settings/rights');
    await userEvent.click(await screen.findByRole('button', { name: 'New rights' }));
    const form = await dialog('New rights');
    await userEvent.type(within(form).getByLabelText('Name'), 'acme STOCK photography');
    await userEvent.click(within(form).getByRole('button', { name: 'Create rights' }));
    expect(await within(form).findByText('A rights record with this name already exists.')).toBeInTheDocument();
  });

  it('shows a general message when the failure names no field', async () => {
    install(world(), (c) => (c.method === 'POST' && c.pathname === '/api/v1/rights' ? problem(500, 'Boom') : undefined));
    renderAt('/settings/rights');
    await userEvent.click(await screen.findByRole('button', { name: 'New rights' }));
    const form = await dialog('New rights');
    await userEvent.type(within(form).getByLabelText('Name'), 'Acme');
    await userEvent.click(within(form).getByRole('button', { name: 'Create rights' }));
    expect(await within(form).findByRole('alert')).toHaveTextContent('Boom');
  });

  it('tells you to save before attaching documents', async () => {
    install(world());
    renderAt('/settings/rights');
    await userEvent.click(await screen.findByRole('button', { name: 'New rights' }));
    const form = await dialog('New rights');
    expect(within(form).getByText(/Save the rights first/)).toBeInTheDocument();
    expect(within(form).queryByText('Choose files')).not.toBeInTheDocument();
  });
});

describe('editing rights', () => {
  it('opens with the stored values and replaces the record', async () => {
    const calls = install(world({ rights: [rights({ notes: 'Old note', watermarkRequired: true })] }));
    renderAt('/settings/rights');
    await openMenu('Acme stock photography', 'Edit');
    const form = await dialog('Edit rights');
    expect(within(form).getByLabelText('Name')).toHaveValue('Acme stock photography');
    expect(within(form).getByLabelText('Licence type')).toHaveValue('licensed');
    expect(within(form).getByLabelText('Licensor')).toHaveValue('Acme Photos');
    expect(await within(form).findByRole('checkbox', { name: 'Website' })).toBeChecked();
    expect(within(form).getByRole('checkbox', { name: 'Print' })).toBeChecked();
    expect(within(form).getByRole('checkbox', { name: 'Digital signage' })).not.toBeChecked();
    expect(within(form).getByRole('checkbox', { name: 'North' })).toBeChecked();
    expect(within(form).getByLabelText('Valid from')).toHaveValue(day(-30));
    expect(within(form).getByLabelText('Valid to')).toHaveValue(day(200));
    expect(within(form).getByRole('switch', { name: 'Watermark required' })).toHaveAttribute('aria-checked', 'true');
    expect(within(form).getByLabelText('Notes')).toHaveValue('Old note');

    await userEvent.click(within(form).getByRole('switch', { name: 'Watermark required' }));
    await userEvent.click(within(form).getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(writes(calls, 'PUT', '/api/v1/rights/r1')).toHaveLength(1));
    expect(await writes(calls, 'PUT', '/api/v1/rights/r1')[0]!.json()).toEqual({
      name: 'Acme stock photography',
      licenceType: 'licensed',
      licensor: 'Acme Photos',
      channels: ['web', 'print'],
      regions: ['north'],
      validFrom: day(-30),
      validTo: day(200),
      watermarkRequired: false,
      notes: 'Old note',
    });
    expect((await screen.findAllByText('Saved “Acme stock photography”')).length).toBeGreaterThan(0);
  });

  it('opens from the name, too', async () => {
    install(world());
    renderAt('/settings/rights');
    await userEvent.click(await screen.findByRole('button', { name: 'Acme stock photography' }));
    expect(await dialog('Edit rights')).toBeInTheDocument();
  });

  it('warns, with the number of assigned assets, before a change that narrows where assets can be used', async () => {
    install(world({ rights: [rights({ channels: ['web', 'print'], regions: [], assetCount: 12 })] }));
    renderAt('/settings/rights');
    await openMenu('Acme stock photography', 'Edit');
    const form = await dialog('Edit rights');
    const web = await within(form).findByRole('checkbox', { name: 'Website' });
    expect(within(form).queryByText(/narrows where the licence applies/)).not.toBeInTheDocument();

    // Widening (adding a channel) is quiet.
    await userEvent.click(within(form).getByRole('checkbox', { name: 'Digital signage' }));
    expect(within(form).queryByText(/narrows where the licence applies/)).not.toBeInTheDocument();

    // Dropping one is not.
    await userEvent.click(web);
    expect(within(form).getByText(/narrows where the licence applies. It is assigned to 12 assets/)).toBeInTheDocument();
    await userEvent.click(web);
    expect(within(form).queryByText(/narrows/)).not.toBeInTheDocument();

    // Restricting an open list is.
    await userEvent.click(within(form).getByRole('checkbox', { name: 'North' }));
    expect(within(form).getByText(/12 assets/)).toBeInTheDocument();
  });

  it('does not warn when no asset uses the record, and uses the singular for one', async () => {
    install(
      world({ rights: [rights({ id: 'u', name: 'Unused', assetCount: 0 }), rights({ id: 'o', name: 'One', assetCount: 1 })] }),
    );
    renderAt('/settings/rights');
    await openMenu('Unused', 'Edit');
    let form = await dialog('Edit rights');
    await userEvent.click(await within(form).findByRole('checkbox', { name: 'Website' }));
    expect(within(form).queryByText(/narrows/)).not.toBeInTheDocument();
    await userEvent.click(within(form).getByRole('button', { name: 'Cancel' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Discard changes' }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Edit rights' })).not.toBeInTheDocument());

    await openMenu('One', 'Edit');
    form = await dialog('Edit rights');
    await userEvent.click(await within(form).findByRole('checkbox', { name: 'Website' }));
    expect(within(form).getByText(/assigned to 1 asset, and any/)).toBeInTheDocument();
  });

  it('says calmly when some assigned assets are still being updated', async () => {
    install(world({ rights: [rights({ assetCount: 900 })] }), (c) =>
      c.method === 'PUT' && c.pathname === '/api/v1/rights/r1'
        ? c.json().then((b) => json(rights({ ...(b as object), assetCount: 900, pendingAssets: 400 })))
        : undefined,
    );
    renderAt('/settings/rights');
    await openMenu('Acme stock photography', 'Edit');
    const form = await dialog('Edit rights');
    await userEvent.click(await within(form).findByRole('checkbox', { name: 'Website' }));
    await userEvent.click(within(form).getByRole('button', { name: 'Save changes' }));
    expect(
      (await screen.findAllByText('400 assigned assets are still being updated in the background; they follow within the hour.'))
        .length,
    ).toBeGreaterThan(0);
  });

  it('asks before throwing away unsaved edits', async () => {
    const calls = install(world());
    renderAt('/settings/rights');
    await openMenu('Acme stock photography', 'Edit');
    const form = await dialog('Edit rights');

    // Nothing changed: closes at once.
    await userEvent.click(within(form).getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Edit rights' })).not.toBeInTheDocument());

    await openMenu('Acme stock photography', 'Edit');
    const again = await dialog('Edit rights');
    await userEvent.type(within(again).getByLabelText('Licensor'), ' Ltd');
    await userEvent.click(within(again).getByRole('button', { name: 'Close' }));
    const ask = await dialog('Discard your changes?');
    await userEvent.click(within(ask).getByRole('button', { name: 'Keep editing' }));
    expect(screen.getByRole('dialog', { name: 'Edit rights' })).toBeInTheDocument();
    expect(within(again).getByLabelText('Licensor')).toHaveValue('Acme Photos Ltd');

    fireEvent(again.closest('dialog')!, new Event('cancel', { cancelable: true }));
    const ask2 = await dialog('Discard your changes?');
    await userEvent.click(within(ask2).getByRole('button', { name: 'Discard changes' }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Edit rights' })).not.toBeInTheDocument());
    expect(writes(calls, 'PUT', '/api/v1/rights/r1')).toHaveLength(0);
  });
});

describe('duplicating and deleting', () => {
  it('duplicates as "<name> copy" with the same terms, then "copy 2"', async () => {
    const calls = install(world({ rights: [rights({ watermarkRequired: true, notes: 'n', assetCount: 3, documentCount: 1 })] }));
    renderAt('/settings/rights');
    await openMenu('Acme stock photography', 'Duplicate');
    await waitFor(() => expect(writes(calls, 'POST', '/api/v1/rights')).toHaveLength(1));
    expect(await writes(calls, 'POST', '/api/v1/rights')[0]!.json()).toEqual({
      name: 'Acme stock photography copy',
      licenceType: 'licensed',
      licensor: 'Acme Photos',
      channels: ['web', 'print'],
      regions: ['north'],
      validFrom: day(-30),
      validTo: day(200),
      watermarkRequired: true,
      notes: 'n',
    });
    expect(await screen.findByRole('heading', { name: 'Acme stock photography copy' })).toBeInTheDocument();
    await openMenu('Acme stock photography', 'Duplicate');
    expect(await screen.findByRole('heading', { name: 'Acme stock photography copy 2' })).toBeInTheDocument();
  });

  it('reports a duplicate that fails', async () => {
    install(world(), (c) =>
      c.method === 'POST' && c.pathname === '/api/v1/rights' ? problem(500, 'Boom', { detail: 'Disk full' }) : undefined,
    );
    renderAt('/settings/rights');
    await openMenu('Acme stock photography', 'Duplicate');
    expect((await screen.findAllByText('Could not duplicate the rights')).length).toBeGreaterThan(0);
    expect(screen.getByText('Disk full')).toBeInTheDocument();
  });

  it('deletes only after a confirmation', async () => {
    const calls = install(world({ rights: [rights({ documentCount: 2 })] }));
    renderAt('/settings/rights');
    await openMenu('Acme stock photography', 'Delete');
    const confirm = await dialog('Delete these rights?');
    expect(within(confirm).getByText('“Acme stock photography” will be deleted. This cannot be undone.')).toBeInTheDocument();
    expect(within(confirm).getByText('Its 2 documents are deleted with it.')).toBeInTheDocument();
    expect(writes(calls, 'DELETE', '/api/v1/rights/r1')).toHaveLength(0);
    await userEvent.click(within(confirm).getByRole('button', { name: 'Delete' }));
    await waitFor(() => expect(writes(calls, 'DELETE', '/api/v1/rights/r1')).toHaveLength(1));
    expect(await screen.findByText('No rights yet')).toBeInTheDocument();
    expect((await screen.findAllByText('Deleted “Acme stock photography”')).length).toBeGreaterThan(0);
  });

  it('explains that rights still assigned to assets cannot be deleted', async () => {
    install(world({ rights: [rights({ assetCount: 7 })] }));
    renderAt('/settings/rights');
    await openMenu('Acme stock photography', 'Delete');
    const confirm = await dialog('Delete these rights?');
    await userEvent.click(within(confirm).getByRole('button', { name: 'Delete' }));
    expect(await within(confirm).findByRole('alert')).toHaveTextContent('Still assigned to 7 assets. Reassign them first.');
    expect(screen.getByRole('heading', { name: 'Acme stock photography' })).toBeInTheDocument();
    await userEvent.click(within(confirm).getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Delete these rights?' })).not.toBeInTheDocument());
  });

  it('uses the singular for one asset', async () => {
    install(world({ rights: [rights({ assetCount: 1 })] }));
    renderAt('/settings/rights');
    await openMenu('Acme stock photography', 'Delete');
    const confirm = await dialog('Delete these rights?');
    await userEvent.click(within(confirm).getByRole('button', { name: 'Delete' }));
    expect(await within(confirm).findByRole('alert')).toHaveTextContent('Still assigned to 1 asset. Reassign it first.');
  });
});

describe('documents', () => {
  const pdf = (name = 'release.pdf', size = 2048) => {
    const f = new File(['x'.repeat(16)], name, { type: 'application/pdf' });
    Object.defineProperty(f, 'size', { value: size });
    return f;
  };
  const picker = (form: HTMLElement) => within(form).getByLabelText('Choose documents to upload') as HTMLInputElement;

  async function openEdit(w = world({ rights: [rights({ documentCount: 1 })], documents: { r1: [doc({ uploadedBy: 'u1' })] } })) {
    const calls = install(w);
    renderAt('/settings/rights');
    await openMenu('Acme stock photography', 'Edit');
    return { calls, form: await dialog('Edit rights'), w };
  }

  it('lists the documents with type, size, age and who added them, and says they are not scanned', async () => {
    const { form } = await openEdit();
    const row = (await within(form).findByText('release-form.pdf')).closest('li')!;
    expect(row).toHaveTextContent('PDF · 120.6 KB · added 2 days ago by you');
    expect(within(form).getByText('Documents are not virus-scanned. Only upload files you trust.')).toBeInTheDocument();
    expect(within(form).getByText(/PDF, PNG, JPEG, DOCX or TXT, up to 25.0 MB each/)).toBeInTheDocument();
  });

  it('shows an empty state', async () => {
    const { form } = await openEdit(world());
    expect(await within(form).findByText('No documents attached yet.')).toBeInTheDocument();
  });

  it('uploads through presign, a PUT straight to storage, then complete - one file at a time', async () => {
    const { calls, form, w } = await openEdit(world());
    await within(form).findByText('No documents attached yet.');
    const png = new File(['png'], 'scan.png', { type: 'image/png' });
    await userEvent.upload(picker(form), [pdf('release.pdf', 2048), png]);

    await waitFor(() => expect(writes(calls, 'POST', '/api/v1/rights/r1/documents/doc-2/complete')).toHaveLength(1));
    const presigns = writes(calls, 'POST', '/api/v1/rights/r1/documents');
    expect(await presigns[0]!.json()).toEqual({ fileName: 'release.pdf', contentType: 'application/pdf', size: 2048 });
    expect(await presigns[1]!.json()).toEqual({ fileName: 'scan.png', contentType: 'image/png', size: 3 });
    expect(storageCalls.map((s) => [s.method, s.url.split('?')[0], s.contentType])).toEqual([
      ['PUT', 'https://storage.test/rights/r1/doc-1', 'application/pdf'],
      ['PUT', 'https://storage.test/rights/r1/doc-2', 'image/png'],
    ]);
    // Sequential: the second presign came after the first complete.
    const order = calls
      .filter((c) => c.pathname.startsWith('/api/v1/rights/r1/documents') && c.method === 'POST')
      .map((c) => c.pathname.replace('/api/v1/rights/r1/documents', '') || 'presign');
    expect(order).toEqual(['presign', '/doc-1/complete', 'presign', '/doc-2/complete']);
    expect(await writes(calls, 'POST', '/api/v1/rights/r1/documents/doc-1/complete')[0]!.json()).toEqual({
      fileName: 'release.pdf',
      contentType: 'application/pdf',
      size: 2048,
    });
    expect(w.documents.r1).toHaveLength(2);
    expect(await within(form).findByText('release.pdf')).toBeInTheDocument();
    expect(await within(form).findByText('scan.png')).toBeInTheDocument();
    expect((await screen.findAllByText('2 documents uploaded')).length).toBeGreaterThan(0);
  });

  it('gives a .docx its type when the browser leaves it empty', async () => {
    const { calls, form } = await openEdit(world());
    await within(form).findByText('No documents attached yet.');
    fireEvent.change(picker(form), { target: { files: [new File(['d'], 'terms.docx', { type: '' })] } });
    await waitFor(() => expect(writes(calls, 'POST', '/api/v1/rights/r1/documents')).toHaveLength(1));
    expect(await writes(calls, 'POST', '/api/v1/rights/r1/documents')[0]!.json()).toMatchObject({
      fileName: 'terms.docx',
      contentType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
    });
  });

  it('turns away a wrong type, a mismatched extension, an empty file and an oversize file without sending them', async () => {
    const { calls, form } = await openEdit(world());
    await within(form).findByText('No documents attached yet.');
    const big = new File(['x'], 'huge.pdf', { type: 'application/pdf' });
    Object.defineProperty(big, 'size', { value: MAX_DOCUMENT_BYTES + 1 });
    const empty = new File([], 'empty.txt', { type: 'text/plain' });
    fireEvent.change(picker(form), {
      target: {
        files: [
          new File(['m'], 'movie.mp4', { type: 'video/mp4' }),
          new File(['x'], 'photo.png', { type: 'application/pdf' }),
          empty,
          big,
        ],
      },
    });
    expect(await within(form).findByText('movie.mp4: only PDF, PNG, JPEG, DOCX and TXT files are accepted.')).toBeInTheDocument();
    expect(within(form).getByText('photo.png: the file extension does not match the file type.')).toBeInTheDocument();
    expect(within(form).getByText('empty.txt: the file is empty.')).toBeInTheDocument();
    expect(within(form).getByText('huge.pdf: too large; the limit is 25.0 MB.')).toBeInTheDocument();
    expect(writes(calls, 'POST', '/api/v1/rights/r1/documents')).toHaveLength(0);
    expect(storageCalls).toHaveLength(0);

    await userEvent.click(within(form).getByRole('button', { name: 'Dismiss movie.mp4' }));
    expect(within(form).queryByText(/movie.mp4/)).not.toBeInTheDocument();
  });

  it('accepts a dropped file', async () => {
    const { calls, form } = await openEdit(world());
    await within(form).findByText('No documents attached yet.');
    const zone = within(form).getByText('Drop files here, or').closest('div')!;
    fireEvent.drop(zone, { dataTransfer: { files: [pdf('dropped.pdf')] } });
    await waitFor(() => expect(writes(calls, 'POST', '/api/v1/rights/r1/documents/doc-1/complete')).toHaveLength(1));
  });

  it('shows the server’s message when it refuses the file, and carries on with the next one', async () => {
    let first = true;
    install(world(), (c) => {
      if (c.method === 'POST' && c.pathname === '/api/v1/rights/r1/documents' && first) {
        first = false;
        return validation({ fileName: ["A 'application/pdf' file must end in .pdf."] });
      }
      return undefined;
    });
    renderAt('/settings/rights');
    await openMenu('Acme stock photography', 'Edit');
    const form = await dialog('Edit rights');
    await within(form).findByText('No documents attached yet.');
    await userEvent.upload(picker(form), [pdf('bad.pdf'), pdf('good.pdf')]);
    expect(await within(form).findByText("A 'application/pdf' file must end in .pdf.")).toBeInTheDocument();
    expect(await within(form).findByText('good.pdf', { selector: '.dam-rdoc__name' })).toBeInTheDocument();
  });

  it('says so when storage refuses the file', async () => {
    const { form, w } = await openEdit(world());
    w.storageStatus = 403;
    await within(form).findByText('No documents attached yet.');
    await userEvent.upload(picker(form), pdf());
    expect(await within(form).findByText('Upload failed (403)')).toBeInTheDocument();
    expect(within(form).queryByText('release.pdf', { selector: '.dam-rdoc__name' })).not.toBeInTheDocument();
  });

  it('downloads through a fresh five-minute link opened in a new tab', async () => {
    const open = vi.spyOn(window, 'open').mockReturnValue(null);
    const { calls, form } = await openEdit();
    await userEvent.click(await within(form).findByRole('button', { name: 'Download release-form.pdf' }));
    await waitFor(() => expect(open).toHaveBeenCalledTimes(1));
    expect(open).toHaveBeenCalledWith('https://storage.test/download/d1?sig=xyz', '_blank', 'noopener,noreferrer');
    expect(writes(calls, 'GET', '/api/v1/rights/r1/documents/d1/link')).toHaveLength(1);
    await userEvent.click(within(form).getByRole('button', { name: 'Download release-form.pdf' }));
    await waitFor(() => expect(open).toHaveBeenCalledTimes(2));
    expect(writes(calls, 'GET', '/api/v1/rights/r1/documents/d1/link')).toHaveLength(2); // never a stored URL
  });

  it('reports a download link that could not be made', async () => {
    const open = vi.spyOn(window, 'open').mockReturnValue(null);
    install(world({ rights: [rights({ documentCount: 1 })], documents: { r1: [doc()] } }), (c) =>
      c.pathname.endsWith('/link') ? problem(500, 'Boom') : undefined,
    );
    renderAt('/settings/rights');
    await openMenu('Acme stock photography', 'Edit');
    const form = await dialog('Edit rights');
    await userEvent.click(await within(form).findByRole('button', { name: 'Download release-form.pdf' }));
    expect((await screen.findAllByText('Could not create the download link')).length).toBeGreaterThan(0);
    expect(open).not.toHaveBeenCalled();
  });

  it('deletes a document only after a confirmation', async () => {
    const { calls, form } = await openEdit();
    await userEvent.click(await within(form).findByRole('button', { name: 'Delete release-form.pdf' }));
    const confirm = await dialog('Delete this document?');
    expect(writes(calls, 'DELETE', '/api/v1/rights/r1/documents/d1')).toHaveLength(0);
    await userEvent.click(within(confirm).getByRole('button', { name: 'Cancel' }));
    expect(writes(calls, 'DELETE', '/api/v1/rights/r1/documents/d1')).toHaveLength(0);

    await userEvent.click(within(form).getByRole('button', { name: 'Delete release-form.pdf' }));
    await userEvent.click(within(await dialog('Delete this document?')).getByRole('button', { name: 'Delete document' }));
    await waitFor(() => expect(writes(calls, 'DELETE', '/api/v1/rights/r1/documents/d1')).toHaveLength(1));
    expect(await within(form).findByText('No documents attached yet.')).toBeInTheDocument();
    expect((await screen.findAllByText('Document deleted')).length).toBeGreaterThan(0);
  });
});

describe('permissions', () => {
  it('is only a list of cards with an add button for someone who can manage rights', async () => {
    install(world({ permissions: ['rights.manage'] }));
    renderAt('/settings/rights');
    expect(await screen.findByRole('button', { name: 'New rights' })).toBeInTheDocument();
  });
});

describe('helpers', () => {
  it('previews the status the way the server derives it', () => {
    expect(previewStatus(null, null, '2026-10-19')).toEqual({ status: 'valid', daysRemaining: null });
    expect(previewStatus('2026-10-01', '2026-12-31', '2026-10-19')).toEqual({ status: 'valid', daysRemaining: 73 });
    expect(previewStatus(null, '2026-11-18', '2026-10-19')).toEqual({ status: 'expiring', daysRemaining: 30 });
    expect(previewStatus(null, '2026-11-19', '2026-10-19')).toEqual({ status: 'valid', daysRemaining: 31 });
    expect(previewStatus(null, '2026-10-19', '2026-10-19')).toEqual({ status: 'expiring', daysRemaining: 0 });
    expect(previewStatus(null, '2026-10-18', '2026-10-19')).toEqual({ status: 'expired', daysRemaining: -1 });
    expect(previewStatus('2026-10-20', null, '2026-10-19').status).toBe('not_yet_valid');
    expect(previewStatus('2026-10-19', null, '2026-10-19').status).toBe('valid');
  });

  it('knows when a change narrows a list', () => {
    expect(narrows([], ['web'])).toBe(true);
    expect(narrows(['web', 'print'], ['web'])).toBe(true);
    expect(narrows(['web'], ['print'])).toBe(true);
    expect(narrows(['web'], ['web', 'print'])).toBe(false);
    expect(narrows(['web'], [])).toBe(false);
    expect(narrows([], [])).toBe(false);
  });

  it('slugifies free-entry codes the way the server accepts them', () => {
    expect(slugify('  Web Site ')).toBe('web-site');
    expect(slugify('EMEA/UK')).toBe('emea-uk');
    expect(slugify('--a__b--')).toBe('a__b');
    expect(slugify('!!!')).toBe('');
    expect(slugify('x'.repeat(80))).toHaveLength(50);
  });

  it('names copies uniquely', () => {
    expect(copyName('A', ['A'], 'copy')).toBe('A copy');
    expect(copyName('A', ['A', 'a COPY'], 'copy')).toBe('A copy 2');
  });

  it('judges a file the way the server will', () => {
    const f = (name: string, type: string, size = 10) => {
      const file = new File(['x'], name, { type });
      Object.defineProperty(file, 'size', { value: size });
      return file;
    };
    expect(fileProblem(f('a.pdf', 'application/pdf'))).toBeNull();
    expect(fileProblem(f('a.JPG', 'image/jpeg'))).toBeNull();
    expect(fileProblem(f('a.txt', 'text/plain;charset=utf-8'))).toBeNull();
    expect(fileProblem(f('a.docx', ''))).toBeNull();
    expect(fileProblem(f('a.pdf', 'image/png'))).toBe('mismatch');
    expect(fileProblem(f('a.exe', 'application/pdf'))).toBe('type');
    expect(fileProblem(f('noext', ''))).toBe('type');
    expect(fileProblem(f('a.pdf', 'application/pdf', 0))).toBe('empty');
    expect(fileProblem(f('a.pdf', 'application/pdf', MAX_DOCUMENT_BYTES))).toBeNull();
    expect(fileProblem(f('a.pdf', 'application/pdf', MAX_DOCUMENT_BYTES + 1))).toBe('size');
  });

  it('filters and counts', () => {
    const all = [
      rights({ id: 'a', name: 'Alpha', status: 'valid' }),
      rights({ id: 'b', name: 'Beta', status: 'expired', licensor: 'Zed' }),
    ];
    expect(filterRights(all, { status: 'expired', q: '' }).map((r) => r.id)).toEqual(['b']);
    expect(filterRights(all, { status: null, q: ' ZED ' }).map((r) => r.id)).toEqual(['b']);
    expect(countByStatus(all)).toEqual({ valid: 1, expiring: 0, expired: 1, not_yet_valid: 0 });
  });
});

describe('accessibility', () => {
  it('has no violations on the list, the form and the documents', async () => {
    install(
      world({
        rights: [
          rights({ documentCount: 1, assetCount: 3, watermarkRequired: true }),
          rights({ id: 'r2', name: 'Soon', status: 'expiring', daysRemaining: 4, channels: [], regions: [] }),
          rights({ id: 'r3', name: 'Past', status: 'expired', daysRemaining: -2 }),
          rights({ id: 'r4', name: 'Later', status: 'not_yet_valid' }),
        ],
        documents: { r1: [doc({ uploadedBy: 'u1' })] },
      }),
    );
    const { container } = renderAt('/settings/rights');
    await card('Acme stock photography');
    expect(await axe(container)).toHaveNoViolations();

    await userEvent.click(screen.getByRole('button', { name: 'New rights' }));
    const create = await dialog('New rights');
    await within(create).findByRole('checkbox', { name: 'Website' });
    expect(await axe(create)).toHaveNoViolations();
    await userEvent.click(within(create).getByRole('button', { name: 'Close' }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'New rights' })).not.toBeInTheDocument());

    await openMenu('Acme stock photography', 'Edit');
    const edit = await dialog('Edit rights');
    await within(edit).findByText('release-form.pdf');
    // With an upload error showing, too.
    fireEvent.change(within(edit).getByLabelText('Choose documents to upload'), {
      target: { files: [new File(['x'], 'nope.exe', { type: '' })] },
    });
    await within(edit).findByText('nope.exe: only PDF, PNG, JPEG, DOCX and TXT files are accepted.');
    expect(await axe(edit)).toHaveNoViolations();
  });

  it('has no violations on the empty, no-access and delete states', async () => {
    install(world({ rights: [] }));
    const empty = renderAt('/settings/rights');
    await screen.findByText('No rights yet');
    expect(await axe(empty.container)).toHaveNoViolations();
    cleanup();

    install(world(), (c) => (c.pathname === '/api/v1/rights' && c.method === 'GET' ? problem(403, 'Forbidden') : undefined));
    const denied = renderAt('/settings/rights');
    await screen.findByText('You do not have access to rights');
    expect(await axe(denied.container)).toHaveNoViolations();
    cleanup();

    install(world({ rights: [rights({ assetCount: 2 })] }));
    renderAt('/settings/rights');
    await openMenu('Acme stock photography', 'Delete');
    const confirm = await dialog('Delete these rights?');
    await userEvent.click(within(confirm).getByRole('button', { name: 'Delete' }));
    await within(confirm).findByRole('alert');
    expect(await axe(confirm)).toHaveNoViolations();
  });
});

describe('Hindi', () => {
  it('renders the rights list and the form in Hindi', async () => {
    setLanguage('hi');
    install(world({ rights: [rights({ assetCount: 4, documentCount: 2, watermarkRequired: true })] }));
    renderAt('/settings/rights');
    expect(await screen.findByRole('heading', { level: 1, name: 'अधिकार' })).toBeInTheDocument();
    const c = await card('Acme stock photography');
    expect(within(c).getByText('4 एसेट')).toBeInTheDocument();
    expect(within(c).getByText('2 दस्तावेज़')).toBeInTheDocument();
    expect(within(c).getByText('वॉटरमार्क सहित')).toBeInTheDocument();

    await userEvent.click(await screen.findByRole('button', { name: 'नए अधिकार' }));
    const form = await dialog('नए अधिकार');
    expect(within(form).getByLabelText('लाइसेंस का प्रकार')).toBeInTheDocument();
    expect(within(form).getByRole('button', { name: 'अधिकार बनाएँ' })).toBeInTheDocument();
  });
});
