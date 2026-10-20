import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { queryClient } from '../../App';
import { setLanguage } from '../../i18n';
import { renderAt } from '../workflow/workflowTestKit';
import { SAMPLE_CSV } from './knowledgeModel';
import { EDITOR, MANAGER, READER, VIN, callsTo, install, newWorld, problem, rule, type World } from './knowledgeTestKit';
import type { VinImportResultDto, VinTestDto } from './knowledgeTypes';

beforeEach(() => {
  queryClient.clear();
  setLanguage('en');
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const RULES = '/api/v1/knowledge/vin-rules';
const table = () => screen.findByRole('table', { name: 'VIN rules' });
const bodyRows = async () =>
  within(await table())
    .getAllByRole('row')
    .slice(1);

const TWO = [
  rule(),
  rule({
    id: 'r2',
    name: 'Acme Orbit 2024',
    pattern: 'ACMOR1????*',
    model: 'orbit',
    variant: null,
    modelYear: 2024,
    yearFromVin: false,
    theme: null,
    priority: 5,
    enabled: false,
  }),
];

async function openRules(w: World, permissions = MANAGER) {
  install(w, permissions);
  renderAt('/knowledge/vin-rules');
  return table();
}

describe('VIN rules tab', () => {
  it('lists the rules by priority, with the pattern in a monospace chip, the year source and an enabled switch', async () => {
    const w = newWorld({ rules: [TWO[1]!, TWO[0]!] });
    await openRules(w);
    const rows = await bodyRows();
    expect(rows).toHaveLength(2);
    expect(rows[0]).toHaveTextContent('Acme Lumen sedan');
    expect(within(rows[0]!).getByText('ACMLS1*').tagName).toBe('CODE');
    expect(rows[0]).toHaveTextContent('lumen');
    expect(rows[0]).toHaveTextContent('sedan');
    expect(rows[0]).toHaveTextContent('From VIN');
    expect(rows[0]).toHaveTextContent('ice');
    expect(rows[0]).toHaveTextContent('10');
    expect(within(rows[0]!).getByRole('switch', { name: 'Enable the rule Acme Lumen sedan' })).toBeChecked();
    expect(rows[1]).toHaveTextContent('Acme Orbit 2024');
    expect(rows[1]).toHaveTextContent('2024');
    expect(within(rows[1]!).getByRole('switch', { name: 'Enable the rule Acme Orbit 2024' })).not.toBeChecked();
    expect(screen.getByText(/2 of 2 rules shown\./)).toBeInTheDocument();
    expect(screen.getByText(/Up to 5,000 rules are allowed\./)).toBeInTheDocument();
  });

  it('filters the rules by name, pattern, model, variant or theme', async () => {
    await openRules(newWorld({ rules: TWO }));
    await bodyRows();
    await userEvent.type(screen.getByLabelText('Find a rule'), 'orbit');
    expect(await bodyRows()).toHaveLength(1);
    await userEvent.clear(screen.getByLabelText('Find a rule'));
    await userEvent.type(screen.getByLabelText('Find a rule'), 'acmls1');
    expect(await bodyRows()).toHaveLength(1);
    await userEvent.clear(screen.getByLabelText('Find a rule'));
    await userEvent.type(screen.getByLabelText('Find a rule'), 'zzz');
    expect(await screen.findByText('No rule matches "zzz".')).toBeInTheDocument();
  });

  it('shows 100 rules at a time', async () => {
    const many = Array.from({ length: 230 }, (_, i) =>
      rule({ id: `r${i}`, name: `Acme rule ${String(i).padStart(3, '0')}`, priority: 0 }),
    );
    await openRules(newWorld({ rules: many }));
    expect(await bodyRows()).toHaveLength(100);
    expect(screen.getByText(/100 of 230 rules shown\./)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Show 100 more' }));
    expect(await bodyRows()).toHaveLength(200);
    await userEvent.click(screen.getByRole('button', { name: 'Show 30 more' }));
    expect(await bodyRows()).toHaveLength(230);
  });

  it('has an empty state that points to adding or importing', async () => {
    install(newWorld({ rules: [] }), MANAGER);
    renderAt('/knowledge/vin-rules');
    expect(await screen.findByRole('heading', { name: 'No VIN rules yet' })).toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: 'Add a rule' }).length).toBeGreaterThan(0);
    expect(screen.getAllByRole('button', { name: 'Import from CSV' }).length).toBeGreaterThan(0);
  });

  it('lets editors read the rules but not change them, import them or test a VIN', async () => {
    await openRules(newWorld({ rules: TWO }), EDITOR);
    const rows = await bodyRows();
    expect(rows[0]).toHaveTextContent('Enabled');
    expect(rows[1]).toHaveTextContent('Disabled');
    expect(screen.queryByRole('switch')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Add a rule' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Import from CSV' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Edit the rule/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Delete the rule/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Test a VIN' })).not.toBeInTheDocument();
  });

  it('explains to those who may not see the rules, without asking the server for them', async () => {
    const w = newWorld();
    install(w, READER);
    renderAt('/knowledge/vin-rules');
    expect(await screen.findByRole('heading', { name: 'You do not have access to VIN rules' })).toBeInTheDocument();
    expect(callsTo(w, RULES)).toHaveLength(0);
  });

  it('shows a failure with a retry', async () => {
    const w = newWorld();
    let fail = true;
    install(w, MANAGER, (c) => (c.pathname === RULES && c.method === 'GET' && fail ? problem(500, 'Server error') : undefined));
    renderAt('/knowledge/vin-rules');
    expect(await screen.findByRole('heading', { name: 'The VIN rules could not be loaded' })).toBeInTheDocument();
    fail = false;
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await table()).toBeInTheDocument();
  });

  it('turns a rule off and on with its switch, sending the whole rule', async () => {
    const w = newWorld({ rules: TWO });
    await openRules(w);
    await bodyRows();
    await userEvent.click(screen.getByRole('switch', { name: 'Enable the rule Acme Lumen sedan' }));
    await waitFor(() => expect(callsTo(w, `${RULES}/r1`, 'PUT')).toHaveLength(1));
    expect(await callsTo(w, `${RULES}/r1`, 'PUT')[0]!.json()).toMatchObject({
      name: 'Acme Lumen sedan',
      pattern: 'ACMLS1*',
      enabled: false,
      priority: 10,
    });
    expect(await screen.findByText('Disabled Acme Lumen sedan')).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole('switch', { name: 'Enable the rule Acme Lumen sedan' })).not.toBeChecked());
  });

  it('stops adding at the limit of 5,000 and says why', async () => {
    const full = Array.from({ length: 5000 }, (_, i) => rule({ id: `r${i}`, name: `Acme rule ${i}`, priority: i % 7 }));
    await openRules(newWorld({ rules: full }));
    await bodyRows();
    expect(screen.getByText(/100 of 5,000 rules shown\./)).toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: 'Add a rule' })[0]).toBeDisabled();
    expect(screen.getByText(/that limit is reached/)).toBeInTheDocument();
  });
});

describe('Add and edit a rule', () => {
  const openAdd = async (w: World) => {
    await openRules(w);
    await bodyRows();
    await userEvent.click(screen.getAllByRole('button', { name: 'Add a rule' })[0]!);
    return within(await screen.findByRole('dialog', { name: 'Add a VIN rule' }));
  };

  it('explains the pattern language with examples, on demand', async () => {
    const dialog = await openAdd(newWorld());
    const summary = dialog.getByText('How patterns work');
    await userEvent.click(summary);
    const help = summary.closest('details')!;
    expect(help).toHaveAttribute('open');
    expect(within(help).getByText('[ABC]')).toBeInTheDocument();
    expect(within(help).getByText('[A-C]')).toBeInTheDocument();
    expect(within(help).getByText('?')).toBeInTheDocument();
    expect(within(help).getAllByText('*').length).toBeGreaterThan(0);
    expect(within(help).getByText(/Without a final star the pattern must be exactly as long as the VIN/)).toBeInTheDocument();
    expect(within(help).getByText('ACM[LS]1*')).toBeInTheDocument();
  });

  it('checks the pattern as it is typed', async () => {
    const dialog = await openAdd(newWorld());
    const pattern = dialog.getByLabelText('Pattern');
    await userEvent.type(pattern, 'acm[[');
    expect(pattern).toHaveValue('ACM[');
    expect(await dialog.findByText('A [ has no closing ].')).toBeInTheDocument();
    await userEvent.type(pattern, ']');
    expect(await dialog.findByText('A set such as [ABC] cannot be empty.')).toBeInTheDocument();
    await userEvent.clear(pattern);
    await userEvent.type(pattern, 'AC*M');
    expect(await dialog.findByText('A * can only be the last character.')).toBeInTheDocument();
    await userEvent.clear(pattern);
    await userEvent.type(pattern, 'AC M#');
    expect(pattern).toHaveValue('ACM#'); // spaces are dropped as it is typed
    expect(await dialog.findByText(/"#" is not allowed/)).toBeInTheDocument();

    await userEvent.clear(pattern);
    await userEvent.type(pattern, 'ACM*');
    expect(
      await dialog.findByText('Matches VINs whose first 3 characters fit this pattern, whatever follows.'),
    ).toBeInTheDocument();
    expect(dialog.queryByText(/cannot match a 17-character VIN/)).not.toBeInTheDocument();
    await userEvent.clear(pattern);
    await userEvent.type(pattern, 'ACM');
    expect(await dialog.findByText(/has no final \*, so it cannot match a 17-character VIN/)).toBeInTheDocument();
    await userEvent.clear(pattern);
    await userEvent.type(pattern, 'ACMLS1AB1N0123456');
    expect(await dialog.findByText('Matches a VIN of exactly 17 characters.')).toBeInTheDocument();
  });

  it('asks for the name, the pattern and the model', async () => {
    const w = newWorld();
    const dialog = await openAdd(w);
    await userEvent.click(dialog.getByRole('button', { name: 'Add a rule' }));
    expect(await dialog.findByText('Give the rule a name.')).toBeInTheDocument();
    expect(dialog.getByText('Enter a pattern.')).toBeInTheDocument();
    expect(dialog.getByText('Enter the model.')).toBeInTheDocument();
    expect(callsTo(w, RULES, 'POST')).toHaveLength(0);
  });

  it('asks for the year only when it is fixed', async () => {
    const dialog = await openAdd(newWorld());
    expect(dialog.queryByLabelText('Model year')).not.toBeInTheDocument();
    expect(dialog.getByRole('radio', { name: /Read it from the tenth character/ })).toBeChecked();
    await userEvent.click(dialog.getByRole('radio', { name: 'Use a fixed year' }));
    await userEvent.type(dialog.getByLabelText('Name'), 'Acme');
    await userEvent.type(dialog.getByLabelText('Pattern'), 'ACM*');
    await userEvent.type(dialog.getByLabelText('Model'), 'lumen');
    await userEvent.click(dialog.getByRole('button', { name: 'Add a rule' }));
    expect(await dialog.findByText('Enter the model year.')).toBeInTheDocument();
  });

  it('adds a rule and shows it in the list', async () => {
    const w = newWorld({ rules: [] });
    install(w, MANAGER);
    renderAt('/knowledge/vin-rules');
    await userEvent.click((await screen.findAllByRole('button', { name: 'Add a rule' }))[0]!);
    const dialog = within(await screen.findByRole('dialog', { name: 'Add a VIN rule' }));
    await userEvent.type(dialog.getByLabelText('Name'), 'Acme Orbit sport');
    await userEvent.type(dialog.getByLabelText('Pattern'), 'acmos1*');
    await userEvent.type(dialog.getByLabelText('Model'), 'Orbit');
    await userEvent.type(dialog.getByLabelText('Variant'), 'sport');
    await userEvent.click(dialog.getByRole('radio', { name: 'Use a fixed year' }));
    await userEvent.type(dialog.getByLabelText('Model year'), '2024');
    await userEvent.type(dialog.getByLabelText('Theme'), 'EV');
    await userEvent.clear(dialog.getByLabelText('Priority'));
    await userEvent.type(dialog.getByLabelText('Priority'), '20');
    await userEvent.click(dialog.getByRole('button', { name: 'Add a rule' }));

    await waitFor(() => expect(callsTo(w, RULES, 'POST')).toHaveLength(1));
    expect(await callsTo(w, RULES, 'POST')[0]!.json()).toEqual({
      name: 'Acme Orbit sport',
      pattern: 'ACMOS1*',
      model: 'orbit',
      variant: 'sport',
      modelYear: 2024,
      yearFromVin: false,
      theme: 'ev',
      priority: 20,
      enabled: true,
    });
    expect(await screen.findByText('Added the rule Acme Orbit sport')).toBeInTheDocument();
    expect((await bodyRows())[0]).toHaveTextContent('Acme Orbit sport');
  });

  it('says so when the name is taken or the limit is reached', async () => {
    const w = newWorld();
    const dialog = await openAdd(w);
    await userEvent.type(dialog.getByLabelText('Name'), 'Acme Lumen sedan');
    await userEvent.type(dialog.getByLabelText('Pattern'), 'ACM*');
    await userEvent.type(dialog.getByLabelText('Model'), 'lumen');
    w.refuse = problem(409, 'Conflict', 'A rule with this name exists.');
    await userEvent.click(dialog.getByRole('button', { name: 'Add a rule' }));
    expect(await dialog.findByText('A rule named "Acme Lumen sedan" already exists.')).toBeInTheDocument();

    w.refuse = problem(422, 'Limit reached', 'A tenant can hold at most 5000 VIN rules.');
    await userEvent.click(dialog.getByRole('button', { name: 'Add a rule' }));
    expect(await dialog.findByText(/can hold up to 5,000 VIN rules, and that limit is reached/)).toBeInTheDocument();
  });

  it('edits a rule from its stored values', async () => {
    const w = newWorld({ rules: TWO });
    await openRules(w);
    await bodyRows();
    await userEvent.click(screen.getByRole('button', { name: 'Edit the rule Acme Orbit 2024' }));
    const dialog = within(await screen.findByRole('dialog', { name: 'Edit the VIN rule' }));
    expect(dialog.getByLabelText('Name')).toHaveValue('Acme Orbit 2024');
    expect(dialog.getByLabelText('Pattern')).toHaveValue('ACMOR1????*');
    expect(dialog.getByRole('radio', { name: 'Use a fixed year' })).toBeChecked();
    expect(dialog.getByLabelText('Model year')).toHaveValue('2024');
    expect(dialog.getByRole('switch', { name: 'Enable this rule' })).not.toBeChecked();
    await userEvent.click(dialog.getByRole('switch', { name: 'Enable this rule' }));
    await userEvent.click(dialog.getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(callsTo(w, `${RULES}/r2`, 'PUT')).toHaveLength(1));
    expect(await callsTo(w, `${RULES}/r2`, 'PUT')[0]!.json()).toMatchObject({
      enabled: true,
      modelYear: 2024,
      yearFromVin: false,
    });
    expect(await screen.findByText('Saved the rule Acme Orbit 2024')).toBeInTheDocument();
  });

  it('confirms before deleting a rule', async () => {
    const w = newWorld({ rules: TWO });
    await openRules(w);
    await bodyRows();
    await userEvent.click(screen.getByRole('button', { name: 'Delete the rule Acme Orbit 2024' }));
    const dialog = within(await screen.findByRole('dialog', { name: 'Delete this rule?' }));
    expect(dialog.getByText(/Acme Orbit 2024/)).toBeInTheDocument();
    expect(callsTo(w, `${RULES}/r2`, 'DELETE')).toHaveLength(0);
    await userEvent.click(dialog.getByRole('button', { name: 'Delete the rule' }));
    await waitFor(() => expect(callsTo(w, `${RULES}/r2`, 'DELETE')).toHaveLength(1));
    expect(await screen.findByText('Deleted the rule Acme Orbit 2024')).toBeInTheDocument();
    await waitFor(async () => expect(await bodyRows()).toHaveLength(1));
  });
});

describe('Test a VIN', () => {
  const result = (over: Partial<VinTestDto> = {}): VinTestDto => ({
    vin: VIN,
    valid: true,
    chosen: { ruleId: 'r1', name: 'Acme Lumen sedan' },
    vehicle: { model: 'lumen', variant: 'sedan', modelYear: 2022, theme: 'ice' },
    candidates: [
      { ruleId: 'r1', name: 'Acme Lumen sedan', priority: 10, specificity: 6, matched: true, reason: 'Matched: prefix ACMLS1' },
      {
        ruleId: 'r2',
        name: 'Acme Lumen any',
        priority: 5,
        specificity: 3,
        matched: true,
        reason: 'Matched, but a lower priority',
      },
      {
        ruleId: 'r3',
        name: 'Acme Orbit 2024',
        priority: 5,
        specificity: 6,
        matched: false,
        reason: 'Position 4 is L, expected O',
      },
    ],
    ...over,
  });
  const panel = async (w: World) => {
    await openRules(w);
    await bodyRows();
    return within(screen.getByRole('region', { name: 'Test a VIN' }));
  };

  it('shows the rule that wins, the vehicle, and why each candidate matched or not', async () => {
    const w = newWorld({ test: result() });
    const p = await panel(w);
    await userEvent.type(p.getByLabelText('VIN'), 'acm ls1ab1n0123456');
    expect(p.getByLabelText('VIN')).toHaveValue(VIN);
    await userEvent.click(p.getByRole('button', { name: 'Test' }));
    expect(await p.findByText('The rule "Acme Lumen sedan" would be used.')).toBeInTheDocument();
    expect(await callsTo(w, `${RULES}/test`, 'POST')[0]!.json()).toEqual({ vin: VIN });
    const facts = within(p.getByText('Model year').closest('dl')!);
    expect(facts.getByText('2022')).toBeInTheDocument();
    expect(facts.getByText('ice')).toBeInTheDocument();

    const rows = within(p.getByRole('table', { name: 'Rules compared for this VIN' }))
      .getAllByRole('row')
      .slice(1);
    expect(rows).toHaveLength(3);
    expect(rows[0]).toHaveTextContent('Acme Lumen sedan');
    expect(rows[0]).toHaveTextContent('Chosen');
    expect(rows[0]).toHaveTextContent('Yes');
    expect(rows[0]).toHaveTextContent('Matched: prefix ACMLS1');
    expect(rows[1]).toHaveTextContent('Matched, but a lower priority');
    expect(rows[1]).not.toHaveTextContent('Chosen');
    expect(rows[2]).toHaveTextContent('No');
    expect(rows[2]).toHaveTextContent('Position 4 is L, expected O');
  });

  it('says when no rule matches', async () => {
    const p = await panel(newWorld({ test: result({ chosen: null, vehicle: null, candidates: [] }) }));
    await userEvent.type(p.getByLabelText('VIN'), VIN);
    await userEvent.click(p.getByRole('button', { name: 'Test' }));
    expect(await p.findByText('No rule matches this VIN.')).toBeInTheDocument();
    expect(p.getByText('There are no enabled rules to compare.')).toBeInTheDocument();
  });

  it('flags wrong characters while typing and the length once tried, and sends nothing until it is a VIN', async () => {
    const w = newWorld({ test: result() });
    const p = await panel(w);
    const input = p.getByLabelText('VIN');
    await userEvent.type(input, 'ACMLS1O');
    expect(await p.findByText('A VIN never contains the letters I, O or Q.')).toBeInTheDocument();
    await userEvent.clear(input);
    await userEvent.type(input, 'ACMLS1');
    expect(p.queryByText(/A VIN has 17 characters/)).not.toBeInTheDocument();
    await userEvent.click(p.getByRole('button', { name: 'Test' }));
    expect(await p.findByText('A VIN has 17 characters; this has 6.')).toBeInTheDocument();
    expect(callsTo(w, `${RULES}/test`, 'POST')).toHaveLength(0);
  });

  it('reads a VIN the server calls invalid, and a failure', async () => {
    const w = newWorld({ test: result({ valid: false, chosen: null, vehicle: null, candidates: [] }) });
    const p = await panel(w);
    await userEvent.type(p.getByLabelText('VIN'), VIN);
    await userEvent.click(p.getByRole('button', { name: 'Test' }));
    expect(await p.findByText(`${VIN} is not a valid VIN.`)).toBeInTheDocument();
    w.test = problem(500, 'Server error');
    await userEvent.click(p.getByRole('button', { name: 'Test' }));
    expect(await p.findByText(/The VIN could not be tested\./)).toBeInTheDocument();
  });
});

describe('Import VIN rules from CSV', () => {
  const GOOD =
    'name,pattern,model,variant,year,theme,priority,enabled\nAcme A,ACMA*,lumen,,vin,,0,true\nAcme B,ACMB*,orbit,,2024,,0,true\n';
  const preview = (over: Partial<VinImportResultDto> = {}): VinImportResultDto => ({
    created: 2,
    updated: 0,
    skipped: 0,
    errors: [],
    dryRun: true,
    ...over,
  });
  const openImport = async (w: World) => {
    await openRules(w);
    await bodyRows();
    await userEvent.click(screen.getAllByRole('button', { name: 'Import from CSV' })[0]!);
    return within(await screen.findByRole('dialog', { name: 'Import VIN rules' }));
  };
  const paste = (dialog: ReturnType<typeof within>, text: string) =>
    fireEvent.change(dialog.getByLabelText('Or paste the CSV text'), { target: { value: text } });

  it('documents the columns and offers a sample CSV generated in the page, with fictional Acme rows', async () => {
    const dialog = await openImport(newWorld());
    const cols = within(dialog.getByRole('list', { name: 'Columns, in this order, with a header line' }));
    for (const c of ['name', 'pattern', 'model', 'variant', 'year', 'theme', 'priority', 'enabled'])
      expect(cols.getByText(c)).toBeInTheDocument();
    expect(cols.getAllByText(/Required/)).toHaveLength(3);
    const link = dialog.getByRole('link', { name: 'Download a sample CSV' });
    expect(link).toHaveAttribute('download', 'vin-rules-sample.csv');
    const href = link.getAttribute('href')!;
    expect(href.startsWith('data:text/csv')).toBe(true);
    expect(decodeURIComponent(href)).toContain('Acme Lumen sedan');
    expect(decodeURIComponent(href)).toContain(SAMPLE_CSV.split('\r\n')[0]);
  });

  it('checks first (a dry run), shows the counts, and only then imports', async () => {
    const w = newWorld({
      importer: (_csv, dryRun) => (dryRun ? preview() : preview({ dryRun: false })),
    });
    const dialog = await openImport(w);
    expect(dialog.getByRole('button', { name: 'Check the file' })).toBeDisabled();
    expect(dialog.getByRole('button', { name: 'Import 0 rules' })).toBeDisabled();
    paste(dialog, GOOD);
    await userEvent.click(dialog.getByRole('button', { name: 'Check the file' }));

    expect(await dialog.findByText('What the import would do')).toBeInTheDocument();
    const counts = within(dialog.getByRole('list', { name: 'Import result' }));
    expect(counts.getByText('2 rules to create')).toBeInTheDocument();
    expect(counts.getByText('0 rules to update')).toBeInTheDocument();
    expect(counts.getByText('0 lines skipped')).toBeInTheDocument();
    const dry = callsTo(w, `${RULES}/import`, 'POST');
    expect(dry).toHaveLength(1);
    expect(dry[0]!.search.get('dryRun')).toBe('true');
    expect(await dry[0]!.text()).toBe(GOOD);

    await userEvent.click(dialog.getByRole('button', { name: 'Import 2 rules' }));
    expect(await dialog.findByText('The import finished.')).toBeInTheDocument();
    const real = callsTo(w, `${RULES}/import`, 'POST')[1]!;
    expect(real.search.get('dryRun')).toBeNull();
    expect(real.search.get('partial')).toBeNull();
    await userEvent.click(dialog.getByRole('button', { name: 'Done' }));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('sends the CSV as text/csv', async () => {
    const w = newWorld({ importer: () => preview() });
    install(w, MANAGER);
    const requests: Request[] = [];
    const served = globalThis.fetch;
    vi.stubGlobal('fetch', (input: RequestInfo | URL, init?: RequestInit) => {
      if (input instanceof Request) requests.push(input.clone());
      return served(input, init);
    });
    renderAt('/knowledge/vin-rules');
    await table();
    await userEvent.click(screen.getAllByRole('button', { name: 'Import from CSV' })[0]!);
    const dialog = within(await screen.findByRole('dialog', { name: 'Import VIN rules' }));
    paste(dialog, GOOD);
    await userEvent.click(dialog.getByRole('button', { name: 'Check the file' }));
    await dialog.findByText('What the import would do');
    const sent = requests.find((r) => r.url.includes('/vin-rules/import'))!;
    expect(sent.headers.get('Content-Type')).toBe('text/csv');
    expect(sent.method).toBe('POST');
  });

  it('lists every line that cannot be imported and holds the import back', async () => {
    const w = newWorld({
      importer: () =>
        preview({
          created: 1,
          skipped: 2,
          errors: [
            { line: 3, message: 'The pattern has an unclosed [.' },
            { line: 5, message: 'The model is required.' },
          ],
        }),
    });
    const dialog = await openImport(w);
    paste(dialog, GOOD);
    await userEvent.click(dialog.getByRole('button', { name: 'Check the file' }));
    const errors = within(await dialog.findByRole('alert'));
    expect(errors.getByText('2 lines have errors')).toBeInTheDocument();
    expect(errors.getByText('Line 3:')).toBeInTheDocument();
    expect(errors.getByText('The pattern has an unclosed [.')).toBeInTheDocument();
    expect(errors.getByText('Line 5:')).toBeInTheDocument();
    expect(
      dialog.getByText('Fix the lines above and check again, or choose to import the valid lines only.'),
    ).toBeInTheDocument();
    expect(dialog.getByRole('button', { name: 'Import 1 rule' })).toBeDisabled();
  });

  it('can import just the valid lines, which sends partial=true and needs a fresh check', async () => {
    const w = newWorld({
      importer: (_csv, dryRun) =>
        preview({ created: 1, skipped: 1, errors: [{ line: 3, message: 'The model is required.' }], dryRun }),
    });
    const dialog = await openImport(w);
    paste(dialog, GOOD);
    await userEvent.click(dialog.getByRole('button', { name: 'Check the file' }));
    await dialog.findByText('Line 3:');
    expect(dialog.getByRole('button', { name: 'Import 1 rule' })).toBeDisabled();

    await userEvent.click(dialog.getByRole('checkbox', { name: /Import the valid lines even if some lines have errors/ }));
    // Changing the option invalidates the check.
    expect(dialog.queryByText('What the import would do')).not.toBeInTheDocument();
    expect(dialog.getByRole('button', { name: 'Import 0 rules' })).toBeDisabled();
    await userEvent.click(dialog.getByRole('button', { name: 'Check the file' }));
    await dialog.findByText('Line 3:');
    expect(callsTo(w, `${RULES}/import`, 'POST').at(-1)!.search.get('partial')).toBe('true');
    await userEvent.click(dialog.getByRole('button', { name: 'Import 1 rule' }));
    expect(await dialog.findByText('The import finished.')).toBeInTheDocument();
    expect(dialog.getByText('Line 3:')).toBeInTheDocument(); // the skipped line is still reported
    const real = callsTo(w, `${RULES}/import`, 'POST').at(-1)!;
    expect(real.search.get('partial')).toBe('true');
    expect(real.search.get('dryRun')).toBeNull();
  });

  it('forgets the check when the text changes', async () => {
    const dialog = await openImport(newWorld({ importer: () => preview() }));
    paste(dialog, GOOD);
    await userEvent.click(dialog.getByRole('button', { name: 'Check the file' }));
    await dialog.findByText('What the import would do');
    expect(dialog.getByRole('button', { name: 'Import 2 rules' })).toBeEnabled();
    await userEvent.type(dialog.getByLabelText('Or paste the CSV text'), 'x');
    expect(dialog.queryByText('What the import would do')).not.toBeInTheDocument();
    expect(dialog.getByRole('button', { name: 'Import 0 rules' })).toBeDisabled();
  });

  it('reads a chosen CSV file into the text area', async () => {
    const dialog = await openImport(newWorld({ importer: () => preview() }));
    const file = new File([GOOD], 'rules.csv', { type: 'text/csv' });
    await userEvent.upload(dialog.getByLabelText('CSV file'), file);
    await waitFor(() => expect(dialog.getByLabelText('Or paste the CSV text')).toHaveValue(GOOD));
    expect(dialog.getByText('Loaded rules.csv.')).toBeInTheDocument();
    await userEvent.click(dialog.getByRole('button', { name: 'Check the file' }));
    expect(await dialog.findByText('What the import would do')).toBeInTheDocument();
  });

  it('says there is nothing to import, and reports a limit or a failure', async () => {
    const w = newWorld({ importer: () => preview({ created: 0, updated: 0 }) });
    const dialog = await openImport(w);
    paste(dialog, 'name,pattern,model\n');
    await userEvent.click(dialog.getByRole('button', { name: 'Check the file' }));
    expect(await dialog.findByText('There is nothing to import in this file.')).toBeInTheDocument();

    w.importer = () => problem(409, 'Conflict', 'This would exceed the limit of 5000 rules.');
    await userEvent.click(dialog.getByRole('button', { name: 'Check the file' }));
    expect(await dialog.findByText(/can hold up to 5,000 VIN rules/)).toBeInTheDocument();

    w.importer = () => problem(500, 'Server error');
    await userEvent.click(dialog.getByRole('button', { name: 'Check the file' }));
    expect(await dialog.findByText(/The import failed\./)).toBeInTheDocument();
  });
});

describe('Accessibility and Hindi', () => {
  it('has no axe violations on the page or in the rule and import dialogs', async () => {
    install(newWorld({ rules: TWO }), MANAGER);
    const { container } = renderAt('/knowledge/vin-rules');
    await table();
    expect(await axe(container)).toHaveNoViolations();

    await userEvent.click(screen.getAllByRole('button', { name: 'Add a rule' })[0]!);
    const add = await screen.findByRole('dialog', { name: 'Add a VIN rule' });
    await userEvent.click(within(add).getByText('How patterns work'));
    expect(await axe(add)).toHaveNoViolations();
    await userEvent.click(within(add).getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());

    await userEvent.click(screen.getAllByRole('button', { name: 'Import from CSV' })[0]!);
    const imp = await screen.findByRole('dialog', { name: 'Import VIN rules' });
    expect(await axe(imp)).toHaveNoViolations();
  });

  it('renders in Hindi with no untranslated keys, including the dialogs', async () => {
    setLanguage('hi');
    install(newWorld({ rules: TWO }), MANAGER);
    renderAt('/knowledge/vin-rules');
    expect(await screen.findByRole('heading', { level: 1, name: 'वीआईएन नियम' })).toBeInTheDocument();
    await waitFor(() =>
      expect(within(screen.getByRole('table', { name: 'वीआईएन नियम' })).getAllByRole('row')[1]).toHaveTextContent('वीआईएन से'),
    );
    expect(screen.getByRole('region', { name: 'वीआईएन जाँचें' })).toBeInTheDocument();
    await userEvent.click(screen.getAllByRole('button', { name: 'नियम जोड़ें' })[0]!);
    const add = within(await screen.findByRole('dialog', { name: 'वीआईएन नियम जोड़ें' }));
    await userEvent.type(add.getByLabelText('पैटर्न'), 'ACM');
    expect(await add.findByText(/अंत में \* नहीं है/)).toBeInTheDocument();
    expect(document.body.textContent).not.toMatch(/knowledge\.[a-z]/i);
  });
});
