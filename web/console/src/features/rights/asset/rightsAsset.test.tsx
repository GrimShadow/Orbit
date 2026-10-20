import { ToastProvider } from '@dam/ui-kit';
import { QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { axe } from 'vitest-axe';
import { queryClient } from '../../../App';
import { AuthContext } from '../../../auth/AuthProvider';
import { setLanguage } from '../../../i18n';
import { json, mockApi, renderAt, signedIn, type Call } from '../../workflow/workflowTestKit';
import { BulkAssignDialog } from './BulkAssignDialog';

beforeEach(() => {
  queryClient.clear();
  setLanguage('en');
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const MANAGER = ['assets.read', 'rights.manage'];

const asset = (status: string, extra: Record<string, unknown> = {}) => ({
  id: 'a1',
  folderId: null,
  type: 'image',
  status,
  title: 'Acme spring hero',
  metadata: {},
  metadataSchemaVersion: 1,
  taxonomyTerms: [],
  tags: [],
  attributes: {},
  ownerId: null,
  currentVersionId: 'v1',
  checksumSha256: null,
  statusReason: '',
  publishAt: null,
  expireAt: null,
  retentionPolicyId: null,
  legalHold: false,
  rightsId: null,
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-02T00:00:00Z',
  ...extra,
});

/** An asset's rights summary as `GET /assets/{id}/rights` answers it. */
const summary = (over: Record<string, unknown> = {}) => ({
  id: 'r1',
  name: 'Acme stock licence',
  licenceType: 'licensed',
  channels: [],
  regions: [],
  validFrom: '2026-01-01',
  validTo: '2027-12-31',
  status: 'valid',
  daysRemaining: 400,
  watermarkRequired: false,
  documentsCount: 0,
  licensor: null,
  notes: null,
  ...over,
});

/** A record as `GET /rights` lists it. */
const record = (over: Record<string, unknown> = {}) => ({
  ...summary(),
  licensor: 'Acme Images',
  notes: '',
  assetCount: 3,
  documentCount: 0,
  createdBy: null,
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  pendingAssets: 0,
  ...over,
});

const RECORDS = [
  record({ id: 'r1', name: 'Acme stock licence' }),
  record({ id: 'r2', name: 'Acme web only', licenceType: 'owned', channels: ['web'], validTo: null, daysRemaining: null }),
  record({ id: 'r3', name: 'Acme old licence', status: 'expired', validTo: '2026-03-03', daysRemaining: -200 }),
];

const CHECK_ALLOWED = {
  allowed: true,
  reasons: [],
  status: 'valid',
  watermarkRequired: false,
  validTo: '2027-12-31',
  effectiveChannels: [],
  effectiveRegions: [],
  noChannelsAllowed: false,
  noRegionsAllowed: false,
};

interface Setup {
  status?: string;
  type?: string;
  attributes?: Record<string, unknown>;
  rights?: ReturnType<typeof summary> | null;
  /** Called for requests the scenario cares about, before the defaults. */
  more?: (c: Call) => Response | Promise<Response> | undefined;
}

/** The asset page's endpoints plus rights, channel vocabulary and a working link endpoint. */
function page({ status = 'draft', type = 'image', attributes = {}, rights = null, more = () => undefined }: Setup = {}) {
  let current = rights;
  return (c: Call): Response | Promise<Response> | undefined => {
    const custom = more(c);
    if (custom) return custom;
    const p = c.pathname;
    if (p === '/api/v1/assets/a1' && c.method === 'GET') return json(asset(status, { type, attributes }));
    if (p === `/api/v1/schemas/${type}`)
      return json({
        assetType: type,
        version: 1,
        isCurrent: true,
        changeNote: '',
        createdAt: '2026-01-01T00:00:00Z',
        fields: [
          { name: 'channel', label: 'Make available for', type: 'vocabulary', vocabulary: 'channel', accessDimension: 'channel' },
        ],
      });
    if (p === '/api/v1/vocabularies')
      return json([
        { id: 'vc', key: 'channel', name: 'Channels', description: '', hierarchical: false, levels: [], termCount: 2 },
      ]);
    if (p === '/api/v1/vocabularies/vc/terms')
      return json([
        { term: { code: 'web', labels: { en: 'Web' } }, children: [] },
        { term: { code: 'signage', labels: { en: 'Signage' } }, children: [] },
      ]);
    if (p === '/api/v1/assets/a1/versions') return json([]);
    if (p === '/api/v1/assets/a1/workflow') return json({ instance: null });
    if (p === '/api/v1/assets/a1/rights' && c.method === 'GET') return json({ assetId: 'a1', rights: current });
    if (p === '/api/v1/rights' && c.method === 'GET') return json(RECORDS);
    if (p === '/api/v1/assets/a1/rights/check') return json(CHECK_ALLOWED);
    if (p === '/api/v1/assets/a1/links')
      return json({ expiresAt: '2030-01-01T00:00:00Z', watermarked: false, links: { preview: 'https://cdn.test/p.jpg' } });
    if (p === '/api/v1/assets/a1/rights' && c.method === 'PUT')
      return (async () => {
        const body = (await c.json()) as { rightsId: string | null };
        current = body.rightsId ? summary({ id: body.rightsId, name: RECORDS.find((r) => r.id === body.rightsId)?.name }) : null;
        return json({ assetId: 'a1', rights: current });
      })();
    return undefined;
  };
}

const panel = () => screen.findByRole('region', { name: 'Rights' });

describe('Rights panel', () => {
  it('explains that an asset without rights is not restricted, with no controls for a reader', async () => {
    mockApi(page());
    renderAt('/library/assets/a1');
    const card = await panel();
    expect(await within(card).findByText('No rights assigned')).toBeInTheDocument();
    expect(within(card).getByText(/not restricted by any licence/)).toBeInTheDocument();
    expect(within(card).queryByRole('button')).not.toBeInTheDocument();
    expect(within(card).queryByText('Check availability')).not.toBeInTheDocument();
  });

  it('offers "Assign rights" to a rights manager', async () => {
    mockApi(page(), { permissions: MANAGER });
    renderAt('/library/assets/a1');
    const card = await panel();
    expect(await within(card).findByRole('button', { name: 'Assign rights' })).toBeInTheDocument();
    expect(within(card).queryByRole('button', { name: 'Remove' })).not.toBeInTheDocument();
  });

  it('shows a valid licence: name, type, status, validity, "all channels" and "worldwide"', async () => {
    mockApi(page({ rights: summary() }));
    renderAt('/library/assets/a1');
    const card = await panel();
    expect(await within(card).findByText('Acme stock licence')).toBeInTheDocument();
    expect(within(card).getByText('Licensed')).toBeInTheDocument();
    expect(within(card).getByText('Valid')).toBeInTheDocument();
    expect(within(card).getByText('Jan 1, 2026 to Dec 31, 2027')).toBeInTheDocument();
    expect(within(card).getByText('All channels')).toBeInTheDocument();
    expect(within(card).getByText('Worldwide')).toBeInTheDocument();
    expect(within(card).getByText('None')).toBeInTheDocument();
    // A healthy licence needs no header chip.
    expect(screen.queryByRole('button', { name: /^Licence/ })).not.toBeInTheDocument();
  });

  it.each([
    [7, 'Expires in 7 days', 'Licence expires in 7 days'],
    [1, 'Expires tomorrow', 'Licence expires tomorrow'],
    [0, 'Expires today', 'Licence expires today'],
  ])('shows an expiring licence with %i days left', async (days, text, headerText) => {
    mockApi(page({ rights: summary({ status: 'expiring', daysRemaining: days, validTo: '2026-11-01' }) }));
    renderAt('/library/assets/a1');
    const card = await panel();
    expect(await within(card).findByText(text)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: headerText })).toBeInTheDocument();
  });

  it('shows an expired licence with its end date, and a danger chip in the header', async () => {
    mockApi(page({ rights: summary({ status: 'expired', validTo: '2026-03-03', daysRemaining: -200 }) }));
    renderAt('/library/assets/a1');
    const card = await panel();
    expect(await within(card).findByText('Expired on Mar 3, 2026')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Licence expired' })).toBeInTheDocument();
  });

  it('shows a licence that has not begun with its start date', async () => {
    mockApi(page({ rights: summary({ status: 'not_yet_valid', validFrom: '2027-06-01', validTo: null, daysRemaining: null }) }));
    renderAt('/library/assets/a1');
    const card = await panel();
    expect(await within(card).findByText('Starts on Jun 1, 2027')).toBeInTheDocument();
    expect(within(card).getByText('From Jun 1, 2027, no end date')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Licence starts on Jun 1, 2027' })).toBeInTheDocument();
  });

  it('shows the licence channels and regions with their labels, and the watermark note', async () => {
    mockApi(
      page({ rights: summary({ channels: ['web', 'kiosk'], regions: ['eu'], watermarkRequired: true, documentsCount: 2 }) }),
    );
    renderAt('/library/assets/a1');
    const card = await panel();
    const channels = await within(card).findByRole('list', { name: 'Channels' });
    await waitFor(() =>
      expect(
        within(channels)
          .getAllByRole('listitem')
          .map((li) => li.textContent),
      ).toEqual(['Web', 'kiosk']),
    );
    expect(within(card).getByRole('list', { name: 'Regions' })).toHaveTextContent('eu');
    expect(within(card).getByText('Watermarked previews')).toBeInTheDocument();
    expect(within(card).getByText(/original file, video playback and PDF pages are withheld/)).toBeInTheDocument();
    expect(within(card).getByText('2 documents')).toBeInTheDocument();
  });

  it('says "restricted to nothing" in words and never shows the server marker', async () => {
    mockApi(
      page({
        rights: summary({ channels: ['web'] }),
        attributes: { channel: ['~restricted'], region: ['eu'] },
      }),
    );
    renderAt('/library/assets/a1');
    const card = await panel();
    expect(await within(card).findByText('Restricted - not available to any channel')).toBeInTheDocument();
    expect(within(card).getByText(/have nothing in common/)).toBeInTheDocument();
    expect(document.body.textContent).not.toContain('~restricted');
    // The other dimension is merely narrowed by the asset's own metadata: say what is effective.
    expect(within(card).getByText('Effective for this asset: eu')).toBeInTheDocument();
  });

  it('shows licensor and notes to a manager only', async () => {
    const rights = summary({ licensor: 'Acme Images', notes: 'Credit the photographer' });
    mockApi(page({ rights }), { permissions: MANAGER });
    renderAt('/library/assets/a1');
    const card = await panel();
    expect(await within(card).findByText('Acme Images')).toBeInTheDocument();
    expect(within(card).getByText('Credit the photographer')).toBeInTheDocument();
    expect(within(card).getByRole('button', { name: 'Change' })).toBeInTheDocument();
    expect(within(card).getByRole('button', { name: 'Remove' })).toBeInTheDocument();
  });

  it('shows a reader neither licensor, notes nor controls', async () => {
    mockApi(page({ rights: summary() }));
    renderAt('/library/assets/a1');
    const card = await panel();
    await within(card).findByText('Acme stock licence');
    expect(within(card).queryByText('Licensor')).not.toBeInTheDocument();
    expect(within(card).queryByText('Notes')).not.toBeInTheDocument();
    expect(within(card).queryByRole('button', { name: 'Change' })).not.toBeInTheDocument();
    expect(within(card).queryByRole('button', { name: 'Remove' })).not.toBeInTheDocument();
  });

  it('offers a retry when the rights cannot be loaded', async () => {
    mockApi((c) => (c.pathname === '/api/v1/assets/a1/rights' ? json({ title: 'Boom', status: 500 }, 500) : page()(c)), {
      permissions: MANAGER,
    });
    renderAt('/library/assets/a1');
    const card = await panel();
    expect(
      await within(card).findByText("Could not load this asset's rights.", undefined, { timeout: 5000 }),
    ).toBeInTheDocument();
    expect(within(card).getByRole('button', { name: 'Try again' })).toBeInTheDocument();
    // No assign controls while the current state is unknown.
    expect(within(card).queryByRole('button', { name: 'Assign rights' })).not.toBeInTheDocument();
  }, 10000);

  it('has no axe violations (licence with channels, check result and a manager)', async () => {
    mockApi(page({ rights: summary({ channels: ['web'], watermarkRequired: true, licensor: 'Acme Images', notes: 'n' }) }), {
      permissions: MANAGER,
    });
    const { container } = renderAt('/library/assets/a1');
    const card = await panel();
    await within(card).findByText('Acme stock licence');
    await userEvent.click(within(card).getByRole('button', { name: 'Check availability' }));
    await within(card).findByText('Allowed');
    expect(await axe(container)).toHaveNoViolations();
  });
});

describe('Assign and remove', () => {
  it('lists the records with their status, assigns the chosen one and says so', async () => {
    const puts: unknown[] = [];
    mockApi(
      page({
        more: (c) => {
          if (c.pathname === '/api/v1/assets/a1/rights' && c.method === 'PUT') {
            return (async () => {
              puts.push(await c.json());
              return json({ assetId: 'a1', rights: summary({ id: 'r2', name: 'Acme web only' }) });
            })();
          }
          return undefined;
        },
      }),
      { permissions: MANAGER },
    );
    renderAt('/library/assets/a1');
    await userEvent.click(await screen.findByRole('button', { name: 'Assign rights' }));

    const dialog = await screen.findByRole('dialog', { name: 'Assign rights' });
    const group = await within(dialog).findByRole('radiogroup', { name: 'Rights records' });
    expect(within(group).getAllByRole('radio')).toHaveLength(3);
    // An expired record is obviously expired before anyone picks it.
    expect(within(group).getByText('Expired on Mar 3, 2026')).toBeInTheDocument();
    expect(within(dialog).getByRole('link', { name: 'Create or edit rights records' })).toHaveAttribute('href', '/admin/rights');

    const assign = within(dialog).getByRole('button', { name: 'Assign' });
    expect(assign).toBeDisabled();
    await userEvent.click(within(group).getByRole('radio', { name: /Acme web only/ }));
    await userEvent.click(assign);

    await waitFor(() => expect(puts).toEqual([{ rightsId: 'r2' }]));
    expect(await screen.findByText('Rights assigned: Acme web only')).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Assign rights' })).not.toBeInTheDocument());
  });

  it('filters the records as you type', async () => {
    mockApi(page(), { permissions: MANAGER });
    renderAt('/library/assets/a1');
    await userEvent.click(await screen.findByRole('button', { name: 'Assign rights' }));
    const dialog = await screen.findByRole('dialog', { name: 'Assign rights' });
    await within(dialog).findByRole('radiogroup');
    await userEvent.type(within(dialog).getByRole('searchbox', { name: 'Search rights' }), 'old');
    expect(within(dialog).getAllByRole('radio')).toHaveLength(1);
    expect(within(dialog).getByRole('radio', { name: /Acme old licence/ })).toBeInTheDocument();
    await userEvent.clear(within(dialog).getByRole('searchbox'));
    await userEvent.type(within(dialog).getByRole('searchbox'), 'zzz');
    expect(within(dialog).getByText('No rights record matches "zzz".')).toBeInTheDocument();
  });

  it('shows the server reason inline when assigning is refused (409) and keeps the dialog open', async () => {
    mockApi(
      page({
        status: 'published',
        more: (c) =>
          c.pathname === '/api/v1/assets/a1/rights' && c.method === 'PUT'
            ? json({ title: "The licence 'Acme stock licence' ended on 2026-03-03.", status: 409 }, 409)
            : undefined,
      }),
      { permissions: MANAGER },
    );
    renderAt('/library/assets/a1');
    await userEvent.click(await screen.findByRole('button', { name: 'Assign rights' }));
    const dialog = await screen.findByRole('dialog', { name: 'Assign rights' });
    await userEvent.click(await within(dialog).findByRole('radio', { name: /Acme stock licence/ }));
    await userEvent.click(within(dialog).getByRole('button', { name: 'Assign' }));
    expect(await within(dialog).findByRole('alert')).toHaveTextContent("The licence 'Acme stock licence' ended on 2026-03-03.");
    expect(screen.getByRole('dialog', { name: 'Assign rights' })).toBeInTheDocument();
  });

  it('shows the first field message of a 422', async () => {
    mockApi(
      page({
        more: (c) =>
          c.pathname === '/api/v1/assets/a1/rights' && c.method === 'PUT'
            ? json(
                {
                  title: 'One or more validation errors occurred.',
                  status: 422,
                  errors: { rightsId: ['Unknown rights record.'] },
                },
                422,
              )
            : undefined,
      }),
      { permissions: MANAGER },
    );
    renderAt('/library/assets/a1');
    await userEvent.click(await screen.findByRole('button', { name: 'Assign rights' }));
    const dialog = await screen.findByRole('dialog', { name: 'Assign rights' });
    await userEvent.click(await within(dialog).findByRole('radio', { name: /Acme stock licence/ }));
    await userEvent.click(within(dialog).getByRole('button', { name: 'Assign' }));
    expect(await within(dialog).findByText('Unknown rights record.')).toBeInTheDocument();
  });

  it('disables records that are not in force for a published asset, with the reason', async () => {
    mockApi(page({ status: 'published' }), { permissions: MANAGER });
    renderAt('/library/assets/a1');
    await userEvent.click(await screen.findByRole('button', { name: 'Assign rights' }));
    const dialog = await screen.findByRole('dialog', { name: 'Assign rights' });
    const old = await within(dialog).findByRole('radio', { name: /Acme old licence/ });
    expect(old).toBeDisabled();
    expect(old).toHaveAccessibleDescription(/cannot be used on a published asset/);
    expect(within(dialog).getByRole('radio', { name: /Acme stock licence/ })).toBeEnabled();
  });

  it('marks the current record and changes it', async () => {
    const puts: unknown[] = [];
    mockApi(
      page({
        rights: summary(),
        more: (c) => {
          if (c.pathname === '/api/v1/assets/a1/rights' && c.method === 'PUT') {
            return (async () => {
              puts.push(await c.json());
              return json({ assetId: 'a1', rights: summary({ id: 'r2', name: 'Acme web only' }) });
            })();
          }
          return undefined;
        },
      }),
      { permissions: MANAGER },
    );
    renderAt('/library/assets/a1');
    await userEvent.click(await screen.findByRole('button', { name: 'Change' }));
    const dialog = await screen.findByRole('dialog', { name: 'Change rights' });
    await within(dialog).findByRole('radiogroup');
    expect(within(dialog).getByText('Current')).toBeInTheDocument();
    const submit = within(dialog).getByRole('button', { name: 'Change rights' });
    await userEvent.click(within(dialog).getByRole('radio', { name: /Acme stock licence/ }));
    expect(submit).toBeDisabled(); // the same record
    await userEvent.click(within(dialog).getByRole('radio', { name: /Acme web only/ }));
    await userEvent.click(submit);
    await waitFor(() => expect(puts).toEqual([{ rightsId: 'r2' }]));
  });

  it('asks before removing, then clears the rights', async () => {
    const puts: unknown[] = [];
    mockApi(
      page({
        rights: summary(),
        more: (c) => {
          if (c.pathname === '/api/v1/assets/a1/rights' && c.method === 'PUT') {
            return (async () => {
              puts.push(await c.json());
              return json({ assetId: 'a1', rights: null });
            })();
          }
          return undefined;
        },
      }),
      { permissions: MANAGER },
    );
    renderAt('/library/assets/a1');
    await userEvent.click(await screen.findByRole('button', { name: 'Remove' }));
    const dialog = await screen.findByRole('dialog', { name: 'Remove rights from this asset?' });
    expect(within(dialog).getByText(/"Acme stock licence" will no longer apply/)).toBeInTheDocument();
    expect(puts).toEqual([]);
    await userEvent.click(within(dialog).getByRole('button', { name: 'Remove rights' }));
    await waitFor(() => expect(puts).toEqual([{ rightsId: null }]));
    expect(await screen.findByText('Rights removed')).toBeInTheDocument();
  });

  it('cancelling the removal changes nothing', async () => {
    const puts: Call[] = [];
    mockApi(
      page({
        rights: summary(),
        more: (c) => {
          if (c.method === 'PUT') puts.push(c);
          return undefined;
        },
      }),
      { permissions: MANAGER },
    );
    renderAt('/library/assets/a1');
    await userEvent.click(await screen.findByRole('button', { name: 'Remove' }));
    const dialog = await screen.findByRole('dialog', { name: 'Remove rights from this asset?' });
    await userEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    expect(puts).toHaveLength(0);
  });

  it('has no axe violations with the assign dialog open', async () => {
    mockApi(page({ rights: summary() }), { permissions: MANAGER });
    const { container } = renderAt('/library/assets/a1');
    await userEvent.click(await screen.findByRole('button', { name: 'Change' }));
    const dialog = await screen.findByRole('dialog', { name: 'Change rights' });
    await within(dialog).findByRole('radiogroup');
    expect(await axe(container)).toHaveNoViolations();
  });
});

describe('Availability check', () => {
  const checks = (calls: Call[]) => calls.filter((c) => c.pathname === '/api/v1/assets/a1/rights/check');

  it('asks nothing until the button is pressed, then checks the chosen channel', async () => {
    const calls = mockApi(page({ rights: summary() }));
    renderAt('/library/assets/a1');
    const card = await panel();
    await within(card).findByText('Acme stock licence');
    expect(checks(calls)).toHaveLength(0);

    await userEvent.selectOptions(await within(card).findByRole('combobox', { name: 'Channel' }), 'web');
    expect(checks(calls)).toHaveLength(0);
    await userEvent.click(within(card).getByRole('button', { name: 'Check availability' }));

    expect(await within(card).findByText('Allowed')).toBeInTheDocument();
    expect(within(card).getByText('Checked for: Web')).toBeInTheDocument();
    expect(checks(calls)).toHaveLength(1);
    expect(checks(calls)[0]!.search.get('channel')).toBe('web');
    expect(checks(calls)[0]!.search.get('region')).toBeNull();
  });

  it('offers the tenant channel vocabulary and falls back to text for a region without one', async () => {
    mockApi(page({ rights: summary() }));
    renderAt('/library/assets/a1');
    const card = await panel();
    const channel = await within(card).findByRole('combobox', { name: 'Channel' });
    expect(
      within(channel)
        .getAllByRole('option')
        .map((o) => o.textContent),
    ).toEqual(['Any channel', 'Web', 'Signage']);
    expect(within(card).getByRole('textbox', { name: 'Region' })).toBeInTheDocument();
  });

  it('shows "Not allowed" with the server reasons, as sent', async () => {
    mockApi(
      page({
        rights: summary({ channels: ['web'] }),
        more: (c) =>
          c.pathname === '/api/v1/assets/a1/rights/check'
            ? json({
                ...CHECK_ALLOWED,
                allowed: false,
                reasons: ["The licence 'Acme stock licence' does not cover channel signage."],
                effectiveChannels: ['web'],
              })
            : undefined,
      }),
    );
    renderAt('/library/assets/a1');
    const card = await panel();
    await userEvent.selectOptions(await within(card).findByRole('combobox', { name: 'Channel' }), 'signage');
    await userEvent.click(within(card).getByRole('button', { name: 'Check availability' }));
    expect(await within(card).findByText('Not allowed')).toBeInTheDocument();
    expect(within(card).getByText("The licence 'Acme stock licence' does not cover channel signage.")).toBeInTheDocument();
    expect(within(card).getByRole('list', { name: 'Available to channels' })).toHaveTextContent('Web');
  });

  it('says that only the dates were checked when no channel or region is chosen, and notes the watermark', async () => {
    mockApi(
      page({
        rights: summary({ watermarkRequired: true }),
        more: (c) =>
          c.pathname === '/api/v1/assets/a1/rights/check' ? json({ ...CHECK_ALLOWED, watermarkRequired: true }) : undefined,
      }),
    );
    renderAt('/library/assets/a1');
    const card = await panel();
    await userEvent.click(await within(card).findByRole('button', { name: 'Check availability' }));
    expect(await within(card).findByText('Allowed')).toBeInTheDocument();
    expect(within(card).getByText('No channel or region chosen, so only the licence dates were checked.')).toBeInTheDocument();
    expect(within(card).getByText('Previews are watermarked and the original file is withheld.')).toBeInTheDocument();
    expect(within(card).getAllByText('All')).toHaveLength(2);
  });

  it('reports "none - restricted to nothing" instead of an empty list', async () => {
    mockApi(
      page({
        rights: summary({ channels: ['web'] }),
        more: (c) =>
          c.pathname === '/api/v1/assets/a1/rights/check'
            ? json({ ...CHECK_ALLOWED, allowed: false, reasons: ['No channel can use this asset.'], noChannelsAllowed: true })
            : undefined,
      }),
    );
    renderAt('/library/assets/a1');
    const card = await panel();
    await userEvent.click(await within(card).findByRole('button', { name: 'Check availability' }));
    expect(await within(card).findByText('None - restricted to nothing')).toBeInTheDocument();
  });

  it('re-checks by itself when a field changes after the first check', async () => {
    const calls = mockApi(page({ rights: summary() }));
    renderAt('/library/assets/a1');
    const card = await panel();
    const channel = await within(card).findByRole('combobox', { name: 'Channel' });
    await userEvent.click(within(card).getByRole('button', { name: 'Check availability' }));
    await within(card).findByText('Allowed');
    expect(checks(calls)).toHaveLength(1);

    await userEvent.selectOptions(channel, 'signage');
    await waitFor(() => expect(checks(calls)).toHaveLength(2));
    expect(checks(calls)[1]!.search.get('channel')).toBe('signage');

    await userEvent.type(within(card).getByRole('textbox', { name: 'Region' }), 'eu');
    await waitFor(() => expect(checks(calls)).toHaveLength(3));
    expect(checks(calls)[2]!.search.get('region')).toBe('eu');
  });

  it('shows the server message when the check itself fails', async () => {
    mockApi(
      page({
        rights: summary(),
        more: (c) =>
          c.pathname === '/api/v1/assets/a1/rights/check'
            ? json({ title: 'Channel is not valid.', status: 422 }, 422)
            : undefined,
      }),
    );
    renderAt('/library/assets/a1');
    const card = await panel();
    await userEvent.click(await within(card).findByRole('button', { name: 'Check availability' }));
    expect(await within(card).findByRole('alert')).toHaveTextContent('Channel is not valid.');
  });
});

describe('Lifecycle and header', () => {
  const lifecycle = () => screen.findByRole('group', { name: 'Lifecycle actions' });

  it('disables Publish for an expired licence, explains why and jumps to the rights panel', async () => {
    const calls = mockApi(
      page({ status: 'approved', rights: summary({ status: 'expired', validTo: '2026-03-03', daysRemaining: -200 }) }),
    );
    renderAt('/library/assets/a1?tab=versions');
    const actions = await lifecycle();
    const publish = await within(actions).findByRole('button', { name: 'Publish' });
    expect(publish).toBeDisabled();
    expect(publish).toHaveAttribute('title', 'Publishing is blocked: the licence "Acme stock licence" ended on Mar 3, 2026.');
    expect(publish).toHaveAccessibleDescription(/ended on Mar 3, 2026/);
    await userEvent.click(publish);
    expect(calls.filter((c) => c.pathname.includes('/transitions/'))).toHaveLength(0);

    await userEvent.click(within(actions).getByRole('button', { name: 'Review rights' }));
    const heading = await screen.findByRole('heading', { name: 'Rights' });
    await waitFor(() => expect(heading).toHaveFocus());
    expect(screen.getByRole('tab', { name: 'Details' })).toHaveAttribute('aria-selected', 'true');
  });

  it('says when the licence has not begun', async () => {
    mockApi(page({ status: 'approved', rights: summary({ status: 'not_yet_valid', validFrom: '2027-06-01', validTo: null }) }));
    renderAt('/library/assets/a1');
    const actions = await lifecycle();
    expect(await within(actions).findByText(/does not start until Jun 1, 2027/)).toBeInTheDocument();
    expect(within(actions).getByRole('button', { name: 'Publish' })).toBeDisabled();
  });

  it('keeps Publish available for a licence that is in force, or for an expiring one', async () => {
    mockApi(page({ status: 'approved', rights: summary({ status: 'expiring', daysRemaining: 5, validTo: '2026-11-01' }) }));
    renderAt('/library/assets/a1');
    const actions = await lifecycle();
    expect(await within(actions).findByRole('button', { name: 'Publish' })).toBeEnabled();
    expect(within(actions).queryByText(/Publishing is blocked/)).not.toBeInTheDocument();
  });

  it('keeps Publish available for an asset without rights', async () => {
    mockApi(page({ status: 'approved' }));
    renderAt('/library/assets/a1');
    expect(await within(await lifecycle()).findByRole('button', { name: 'Publish' })).toBeEnabled();
  });

  it('shows the server text, not "Conflict", when a refused publish comes back anyway', async () => {
    mockApi(
      page({
        status: 'approved',
        more: (c) =>
          c.pathname === '/api/v1/assets/a1/transitions/publish'
            ? json(
                {
                  title:
                    "The licence 'Acme stock licence' ended on 2026-03-03. Renew the licence or assign other rights before publishing.",
                  status: 409,
                },
                409,
              )
            : undefined,
      }),
    );
    renderAt('/library/assets/a1');
    await userEvent.click(await within(await lifecycle()).findByRole('button', { name: 'Publish' }));
    expect(await screen.findByText(/ended on 2026-03-03. Renew the licence/)).toBeInTheDocument();
    expect(screen.queryByText('Conflict')).not.toBeInTheDocument();
  });

  it('the header chip opens the rights panel', async () => {
    mockApi(page({ rights: summary({ status: 'expiring', daysRemaining: 5, validTo: '2026-11-01' }) }));
    renderAt('/library/assets/a1?tab=versions');
    await userEvent.click(await screen.findByRole('button', { name: 'Licence expires in 5 days' }));
    const heading = await screen.findByRole('heading', { name: 'Rights' });
    await waitFor(() => expect(heading).toHaveFocus());
  });
});

describe('Preview stage', () => {
  const blocked = (reason: string) =>
    json({ title: reason, status: 403, code: 'rights_restricted', reason, reasons: [reason, 'It ended.'] }, 403);

  it('shows a calm message with the reason when the licence blocks the preview', async () => {
    mockApi(
      page({
        rights: summary({ status: 'expired', validTo: '2026-03-03', daysRemaining: -200 }),
        more: (c) =>
          c.pathname === '/api/v1/assets/a1/links' ? blocked("The licence 'Acme stock licence' ended on 2026-03-03.") : undefined,
      }),
    );
    renderAt('/library/assets/a1');
    expect(await screen.findByText("This asset's licence doesn't currently allow previews")).toBeInTheDocument();
    expect(screen.getByText("The licence 'Acme stock licence' ended on 2026-03-03. It ended.")).toBeInTheDocument();
    expect(screen.queryByRole('img', { name: 'Acme spring hero' })).not.toBeInTheDocument();
    expect(screen.queryByText('Preview is not available for this asset.')).not.toBeInTheDocument();
  });

  it('keeps the plain message for any other failure', async () => {
    mockApi(
      page({ more: (c) => (c.pathname === '/api/v1/assets/a1/links' ? json({ title: 'No.', status: 403 }, 403) : undefined) }),
    );
    renderAt('/library/assets/a1');
    expect(await screen.findByText('Preview is not available for this asset.')).toBeInTheDocument();
    expect(screen.queryByText(/licence doesn't currently allow/)).not.toBeInTheDocument();
  });

  it('suggests reloading when the picture itself fails to load', async () => {
    mockApi(page());
    renderAt('/library/assets/a1');
    const img = await screen.findByRole('img', { name: 'Acme spring hero' });
    img.dispatchEvent(new Event('error'));
    expect(await screen.findByText(/If the asset's licence has just changed, reload the page/)).toBeInTheDocument();
  });

  it('asks a published watermark-required asset for the preview only (the origin refuses the original)', async () => {
    const links: unknown[] = [];
    mockApi(
      page({
        status: 'published',
        rights: summary({ watermarkRequired: true }),
        more: (c) => {
          if (c.pathname === '/api/v1/assets/a1/links') {
            return (async () => {
              links.push(await c.json());
              return json({ expiresAt: '2030-01-01T00:00:00Z', watermarked: true, links: { preview: 'https://cdn.test/p.jpg' } });
            })();
          }
          return undefined;
        },
      }),
    );
    renderAt('/library/assets/a1');
    expect(await screen.findByRole('img', { name: 'Acme spring hero' })).toHaveAttribute('src', 'https://cdn.test/p.jpg');
    expect((links[0] as { renditions: string[] }).renditions).toEqual(['preview']);
  });
});

describe('Bulk assign in the library', () => {
  const lib = (id: string, title: string, status = 'draft') => asset(status, { id, title, folderId: null });
  const LIBRARY = [lib('a1', 'Hero shot'), lib('a2', 'Second shot')];

  function libraryApi(assign: (body: { assetIds: string[]; replace: boolean }) => unknown, permissions = MANAGER) {
    const bodies: { rightsId: string; assetIds: string[]; replace: boolean }[] = [];
    mockApi(
      (c) => {
        if (c.pathname === '/api/v1/assets') return json({ items: LIBRARY, nextCursor: null });
        if (c.pathname === '/api/v1/folders') return json([]);
        if (c.pathname === '/api/v1/rights' && c.method === 'GET') return json(RECORDS);
        const m = /^\/api\/v1\/rights\/(r\d)\/assign$/.exec(c.pathname);
        if (m)
          return (async () => {
            const body = (await c.json()) as { assetIds: string[]; replace: boolean };
            bodies.push({ rightsId: m[1]!, ...body });
            return json(assign(body));
          })();
        return undefined;
      },
      { permissions },
    );
    return bodies;
  }

  const selectAll = async () => {
    await screen.findByText('Hero shot');
    await userEvent.click(screen.getByLabelText('Select all 2 assets on this page'));
    return screen.findByRole('region', { name: '2 assets selected' });
  };

  it('is offered to rights managers only', async () => {
    libraryApi(() => ({}), ['assets.read']);
    renderAt('/library');
    const bar = await selectAll();
    expect(within(bar).getByRole('button', { name: 'Save as collection' })).toBeInTheDocument();
    expect(within(bar).queryByRole('button', { name: 'Assign rights' })).not.toBeInTheDocument();
  });

  it('sends the chosen record, the selected ids and the replace flag, then summarises a partial result', async () => {
    const bodies = libraryApi((body) => ({
      assigned: 1,
      skipped: 1,
      failed: 0,
      items: [
        { assetId: body.assetIds[0], outcome: 'assigned', reason: null },
        { assetId: body.assetIds[1], outcome: 'skipped', reason: "Already has other rights: 'Acme old licence'." },
      ],
    }));
    renderAt('/library');
    const bar = await selectAll();
    await userEvent.click(within(bar).getByRole('button', { name: 'Assign rights' }));

    const dialog = await screen.findByRole('dialog', { name: 'Assign rights to selected assets' });
    expect(within(dialog).getByText('2 assets selected')).toBeInTheDocument();
    const submit = within(dialog).getByRole('button', { name: 'Assign to 2 assets' });
    expect(submit).toBeDisabled();
    await userEvent.click(await within(dialog).findByRole('radio', { name: /Acme stock licence/ }));
    await userEvent.click(within(dialog).getByRole('checkbox', { name: /Replace existing rights/ }));
    await userEvent.click(submit);

    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(bodies[0]).toEqual({ rightsId: 'r1', assetIds: ['a1', 'a2'], replace: true });

    expect(await within(dialog).findByText('Result')).toBeInTheDocument();
    const totals = within(dialog)
      .getAllByRole('term')
      .map((t) => t.textContent);
    expect(totals).toEqual(['Assigned', 'Skipped', 'Failed']);
    expect(
      within(dialog)
        .getAllByRole('definition')
        .map((d) => d.textContent),
    ).toEqual(['1', '1', '0']);
    expect(await screen.findByText('1 assigned, 1 skipped, 0 failed')).toBeInTheDocument();

    await userEvent.click(within(dialog).getByText('Show reasons'));
    expect(within(dialog).getByText(/Already has other rights/)).toBeVisible();
    expect(within(dialog).getByText('Second shot')).toBeInTheDocument();

    await userEvent.click(within(dialog).getByRole('button', { name: 'Done' }));
    await waitFor(() => expect(screen.queryByRole('region', { name: '2 assets selected' })).not.toBeInTheDocument());
  });

  it('sends replace=false by default and toasts a clean result', async () => {
    const bodies = libraryApi((body) => ({
      assigned: body.assetIds.length,
      skipped: 0,
      failed: 0,
      items: body.assetIds.map((assetId) => ({ assetId, outcome: 'assigned', reason: null })),
    }));
    renderAt('/library');
    const bar = await selectAll();
    await userEvent.click(within(bar).getByRole('button', { name: 'Assign rights' }));
    const dialog = await screen.findByRole('dialog');
    await userEvent.click(await within(dialog).findByRole('radio', { name: /Acme web only/ }));
    await userEvent.click(within(dialog).getByRole('button', { name: 'Assign to 2 assets' }));
    await waitFor(() => expect(bodies[0]).toEqual({ rightsId: 'r2', assetIds: ['a1', 'a2'], replace: false }));
    expect(await screen.findByText('Rights assigned to 2 assets')).toBeInTheDocument();
    expect(within(dialog).queryByText('Show reasons')).not.toBeInTheDocument();
  });

  it('keeps the dialog on the form with the server message when the request is refused', async () => {
    mockApi(
      (c) => {
        if (c.pathname === '/api/v1/assets') return json({ items: LIBRARY, nextCursor: null });
        if (c.pathname === '/api/v1/folders') return json([]);
        if (c.pathname === '/api/v1/rights' && c.method === 'GET') return json(RECORDS);
        if (c.pathname.endsWith('/assign')) return json({ title: 'You may not assign rights.', status: 403 }, 403);
        return undefined;
      },
      { permissions: MANAGER },
    );
    renderAt('/library');
    const bar = await selectAll();
    await userEvent.click(within(bar).getByRole('button', { name: 'Assign rights' }));
    const dialog = await screen.findByRole('dialog');
    await userEvent.click(await within(dialog).findByRole('radio', { name: /Acme stock licence/ }));
    await userEvent.click(within(dialog).getByRole('button', { name: 'Assign to 2 assets' }));
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('You may not assign rights.');
    expect(within(dialog).getByRole('button', { name: 'Assign to 2 assets' })).toBeEnabled();
  });

  it('has no axe violations on the form and on the result', async () => {
    libraryApi((body) => ({
      assigned: 0,
      skipped: 0,
      failed: 2,
      items: body.assetIds.map((assetId) => ({ assetId, outcome: 'failed', reason: 'Not found.' })),
    }));
    const { container } = renderAt('/library');
    const bar = await selectAll();
    await userEvent.click(within(bar).getByRole('button', { name: 'Assign rights' }));
    const dialog = await screen.findByRole('dialog');
    await userEvent.click(await within(dialog).findByRole('radio', { name: /Acme stock licence/ }));
    expect(await axe(container)).toHaveNoViolations();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Assign to 2 assets' }));
    await within(dialog).findByText('Result');
    await userEvent.click(within(dialog).getByText('Show reasons'));
    expect(await axe(container)).toHaveNoViolations();
  });
});

describe('Bulk assign with more than 500 assets', () => {
  function renderDialog(count: number) {
    const ids = Array.from({ length: count }, (_, i) => `id-${i}`);
    const bodies: { assetIds: string[] }[] = [];
    mockApi(
      (c) => {
        if (c.pathname === '/api/v1/rights' && c.method === 'GET') return json(RECORDS);
        if (c.pathname === '/api/v1/rights/r1/assign')
          return (async () => {
            const body = (await c.json()) as { assetIds: string[] };
            bodies.push(body);
            return json({
              assigned: body.assetIds.length,
              skipped: 0,
              failed: 0,
              items: body.assetIds.map((assetId) => ({ assetId, outcome: 'assigned', reason: null })),
            });
          })();
        return undefined;
      },
      { permissions: MANAGER },
    );
    const onClose = vi.fn();
    render(
      <AuthContext.Provider value={signedIn}>
        <QueryClientProvider client={queryClient}>
          <ToastProvider>
            <MemoryRouter>
              <BulkAssignDialog open selectedIds={ids} titles={new Map()} onClose={onClose} />
            </MemoryRouter>
          </ToastProvider>
        </QueryClientProvider>
      </AuthContext.Provider>,
    );
    return { bodies, onClose };
  }

  it('says it will send batches of 500 and does, merging the results', async () => {
    const { bodies, onClose } = renderDialog(1200);
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText('1200 assets selected')).toBeInTheDocument();
    expect(
      within(dialog).getByText(/500 assets at a time, so these 1200 assets will be assigned in 3 batches/),
    ).toBeInTheDocument();
    await userEvent.click(await within(dialog).findByRole('radio', { name: /Acme stock licence/ }));
    await userEvent.click(within(dialog).getByRole('button', { name: 'Assign to 1200 assets' }));
    await within(dialog).findByText('Result');
    expect(bodies.map((b) => b.assetIds.length)).toEqual([500, 500, 200]);
    expect(within(dialog).getAllByRole('definition')[0]).toHaveTextContent('1200');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Done' }));
    expect(onClose).toHaveBeenCalledWith(true);
  });

  it('sends one request for exactly 500 and shows no batch note', async () => {
    const { bodies } = renderDialog(500);
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).queryByText(/at a time/)).not.toBeInTheDocument();
    await userEvent.click(await within(dialog).findByRole('radio', { name: /Acme stock licence/ }));
    await userEvent.click(within(dialog).getByRole('button', { name: 'Assign to 500 assets' }));
    await within(dialog).findByText('Result');
    expect(bodies).toHaveLength(1);
  });

  it('cancelling reports that nothing was done', async () => {
    const { onClose } = renderDialog(3);
    const dialog = await screen.findByRole('dialog');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    expect(onClose).toHaveBeenCalledWith(false);
  });
});
