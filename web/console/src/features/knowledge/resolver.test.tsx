import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { queryClient } from '../../App';
import { setLanguage } from '../../i18n';
import { renderAt } from '../workflow/workflowTestKit';
import { CHAPTERS, READER, VIN, callsTo, doc, install, newWorld, problem, resolved, type World } from './knowledgeTestKit';

beforeEach(() => {
  queryClient.clear();
  setLanguage('en');
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const RESOLVE = '/api/v1/knowledge/resolve';

function open(w: World = newWorld(), permissions = READER) {
  install(w, permissions);
  renderAt('/knowledge/resolver');
  return w;
}
const field = () => screen.findByLabelText('VIN');
async function resolve(vin = VIN) {
  await userEvent.type(await field(), vin);
  await userEvent.click(screen.getByRole('button', { name: 'Resolve' }));
}

describe('VIN resolver', () => {
  it('is for anyone who can read assets, and starts with an owner manual and the default language', async () => {
    open();
    expect(await screen.findByRole('heading', { level: 1, name: 'VIN resolver' })).toBeInTheDocument();
    expect(screen.getByLabelText('Document type')).toHaveValue('owner_manual');
    expect(screen.getByLabelText('Language')).toHaveValue('');
    expect(within(screen.getByLabelText('Language')).getByRole('option', { name: 'Your default language' })).toBeInTheDocument();
    expect(screen.queryByText('Matched by the rule', { exact: false })).not.toBeInTheDocument();
  });

  it('upper-cases the VIN and strips spaces as it is typed', async () => {
    open();
    await userEvent.type(await field(), 'acm ls1');
    expect(await field()).toHaveValue('ACMLS1');
  });

  it('flags wrong characters at once, and a wrong length once the field is left', async () => {
    const w = open();
    const input = await field();
    await userEvent.type(input, 'ACMLS1O');
    expect(await screen.findByText('A VIN never contains the letters I, O or Q.')).toBeInTheDocument();
    await userEvent.clear(input);
    await userEvent.type(input, 'ACM-LS1');
    expect(await screen.findByText('A VIN has letters and digits only.')).toBeInTheDocument();
    await userEvent.clear(input);
    await userEvent.type(input, 'ACMLS1');
    expect(screen.queryByText(/A VIN has 17 characters/)).not.toBeInTheDocument();
    await userEvent.tab();
    expect(await screen.findByText('A VIN has 17 characters; this has 6.')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Resolve' }));
    expect(callsTo(w, RESOLVE)).toHaveLength(0);
  });

  it('asks for a VIN when the box is empty', async () => {
    const w = open();
    await userEvent.click(await screen.findByRole('button', { name: 'Resolve' }));
    expect(await screen.findByText('Enter a VIN.')).toBeInTheDocument();
    expect(callsTo(w, RESOLVE)).toHaveLength(0);
  });

  it('asks the API for the VIN, the type and the language', async () => {
    const w = open();
    await userEvent.selectOptions(await screen.findByLabelText('Document type'), 'faq');
    await userEvent.selectOptions(screen.getByLabelText('Language'), 'hi');
    await resolve();
    await screen.findByRole('heading', { name: 'Vehicle' });
    const call = callsTo(w, RESOLVE)[0]!;
    expect(call.search.get('vin')).toBe(VIN);
    expect(call.search.get('type')).toBe('faq');
    expect(call.search.get('lang')).toBe('hi');
  });

  it('sends no language when the default is chosen', async () => {
    const w = open();
    await resolve();
    await screen.findByRole('heading', { name: 'Vehicle' });
    expect(callsTo(w, RESOLVE)[0]!.search.has('lang')).toBe(false);
    expect(callsTo(w, RESOLVE)[0]!.search.get('type')).toBe('owner_manual');
  });

  it('shows the matched rule, the vehicle and the document', async () => {
    open();
    await resolve();
    const vehicle = within(await screen.findByRole('region', { name: 'Vehicle' }));
    expect(vehicle.getByText('lumen')).toBeInTheDocument();
    expect(vehicle.getByText('sedan')).toBeInTheDocument();
    expect(vehicle.getByText('2022')).toBeInTheDocument();
    expect(vehicle.getByText('ice')).toBeInTheDocument();
    expect(vehicle.getByText('Matched by the rule "Acme Lumen sedan" (priority 10).')).toBeInTheDocument();

    const document_ = within(screen.getByRole('region', { name: 'Document' }));
    expect(document_.getByRole('link', { name: 'Acme Lumen owner manual' })).toHaveAttribute('href', '/library/assets/a1');
    expect(document_.getByText('Owner manual')).toBeInTheDocument();
    expect(document_.getByText('Published')).toBeInTheDocument();
    expect(document_.getByText('Version 4')).toBeInTheDocument();
    expect(document_.getByText('English (en)')).toBeInTheDocument();
  });

  it('nests the chapters by level and links each one to its page', async () => {
    open();
    await resolve();
    const chapters = within(await screen.findByRole('list', { name: 'Chapters' }));
    expect(screen.getByRole('heading', { name: '3 chapters' })).toBeInTheDocument();
    const top = chapters.getAllByRole('listitem').filter((li) => li.parentElement!.getAttribute('data-depth') === '0');
    expect(top.map((li) => within(li).getAllByRole('link')[0]!.textContent)).toEqual(['Getting started', 'Warning lights']);
    const nested = within(top[0]!).getByRole('list');
    expect(nested.getAttribute('data-depth')).toBe('1');
    expect(within(nested).getByRole('link', { name: 'Charging' })).toHaveAttribute('href', '/library/assets/a1?page=5');
    expect(within(top[0]!).getByRole('link', { name: 'Getting started' })).toHaveAttribute('href', '/library/assets/a1?page=3');
    expect(top[0]).toHaveTextContent('pp. 3-9');
    expect(top[1]).toHaveTextContent('pp. 10-12');
    expect(within(top[1]!).queryByRole('list')).not.toBeInTheDocument();
  });

  it('shows a chapter without a page as text, not a link', async () => {
    const w = newWorld({
      resolve: () =>
        Response.json(
          resolved({
            document: {
              ...doc(),
              versionNumber: 1,
              chapters: [
                { title: 'Intro', anchor: 'intro', level: 1, pageFrom: null, pageTo: null },
                { title: 'Care', anchor: null, level: 1, pageFrom: 2, pageTo: 2 },
              ],
            },
          }),
        ),
    });
    open(w);
    await resolve();
    const chapters = within(await screen.findByRole('list', { name: 'Chapters' }));
    expect(chapters.getByText('Intro').closest('a')).toBeNull();
    expect(chapters.getByRole('link', { name: 'Care' })).toHaveAttribute('href', '/library/assets/a1?page=2');
    expect(chapters.getByText('p. 2')).toBeInTheDocument();
  });

  it('says so when the document has no chapters', async () => {
    const w = newWorld({ resolve: () => Response.json(resolved({ document: { ...doc(), chapters: [], versionNumber: 2 } })) });
    open(w);
    await resolve();
    expect(await screen.findByText('This document has no chapters.')).toBeInTheDocument();
    expect(screen.queryByRole('list', { name: 'Chapters' })).not.toBeInTheDocument();
  });

  it('links the other languages to their assets', async () => {
    open();
    await resolve();
    const langs = within(await screen.findByRole('list', { name: 'Also available in' }));
    expect(langs.getByRole('link', { name: 'Hindi (hi)' })).toHaveAttribute('href', '/library/assets/a2');
  });

  it('shows no other-languages section when there are none', async () => {
    open(newWorld({ resolve: () => Response.json(resolved({ otherLanguages: [] })) }));
    await resolve();
    await screen.findByRole('heading', { name: 'Vehicle' });
    expect(screen.queryByText('Also available in')).not.toBeInTheDocument();
  });

  it('says a vehicle without a variant, year or theme has none', async () => {
    open(
      newWorld({
        resolve: () => Response.json(resolved({ vehicle: { model: 'orbit', variant: null, modelYear: null, theme: null } })),
      }),
    );
    await resolve();
    const vehicle = within(await screen.findByRole('region', { name: 'Vehicle' }));
    expect(vehicle.getAllByText('None')).toHaveLength(3);
  });

  it('explains a malformed VIN (422)', async () => {
    open(newWorld({ resolve: () => problem(422, 'Validation failed', 'The VIN is malformed.') }));
    await resolve();
    const alert = within(await screen.findByRole('alert'));
    expect(alert.getByText(`${VIN} is not a valid VIN`)).toBeInTheDocument();
    expect(alert.getByText('A VIN has 17 letters and digits, without I, O or Q.')).toBeInTheDocument();
  });

  it('explains an unknown VIN (404, no rule matches)', async () => {
    open(newWorld({ resolve: () => problem(404, 'Not found', 'No rule matches this VIN') }));
    await resolve();
    const alert = within(await screen.findByRole('alert'));
    expect(alert.getByText(`No rule matches ${VIN}`)).toBeInTheDocument();
    expect(alert.getByText(/no enabled VIN rule covers it/)).toBeInTheDocument();
  });

  it('explains a VIN with no document (404, nothing mapped or visible)', async () => {
    open(newWorld({ resolve: () => problem(404, 'Not found', 'No document found for this vehicle.') }));
    await resolve();
    const alert = within(await screen.findByRole('alert'));
    expect(alert.getByText(`No document found for ${VIN}`)).toBeInTheDocument();
    expect(alert.getByText(/you are not allowed to see it/)).toBeInTheDocument();
  });

  it('explains a refusal and a failure, and retries the failure', async () => {
    const w = newWorld({ resolve: () => problem(500, 'Server error') });
    open(w);
    await resolve();
    expect(await screen.findByText('The VIN could not be looked up')).toBeInTheDocument();
    w.resolve = () => Response.json(resolved());
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByRole('heading', { name: 'Vehicle' })).toBeInTheDocument();
    cleanup();
    queryClient.clear();

    open(newWorld({ resolve: () => problem(403, 'Forbidden') }));
    await resolve();
    expect(await screen.findByText('You are not allowed to look up VINs')).toBeInTheDocument();
  });

  it('asks again when the same question is asked again, and clears an old answer for a new one', async () => {
    const w = open();
    await resolve();
    await screen.findByRole('heading', { name: 'Vehicle' });
    await userEvent.click(screen.getByRole('button', { name: 'Resolve' }));
    await waitFor(() => expect(callsTo(w, RESOLVE)).toHaveLength(2));
    w.resolve = () => problem(404, 'Not found', 'No rule matches this VIN');
    await userEvent.clear(await field());
    await userEvent.type(await field(), 'ACMZZ1AB1N0123456');
    await userEvent.click(screen.getByRole('button', { name: 'Resolve' }));
    expect(await screen.findByText('No rule matches ACMZZ1AB1N0123456')).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Vehicle' })).not.toBeInTheDocument();
  });

  it('has no axe violations, with a result or an error', async () => {
    const w = open();
    const container = document.body;
    await resolve();
    await screen.findByRole('heading', { name: 'Vehicle' });
    expect(await axe(container)).toHaveNoViolations();
    w.resolve = () => problem(404, 'Not found', 'No rule matches this VIN');
    await userEvent.clear(await field());
    await userEvent.type(await field(), 'ACMZZ1AB1N0123456');
    await userEvent.click(screen.getByRole('button', { name: 'Resolve' }));
    await screen.findByRole('alert');
    expect(await axe(container)).toHaveNoViolations();
  });

  it('renders in Hindi with no untranslated keys', async () => {
    setLanguage('hi');
    open(newWorld({ resolve: () => Response.json(resolved({ document: { ...doc(), chapters: CHAPTERS, versionNumber: 4 } })) }));
    expect(await screen.findByRole('heading', { level: 1, name: 'वीआईएन खोजक' })).toBeInTheDocument();
    await userEvent.type(await screen.findByLabelText('वीआईएन'), VIN);
    await userEvent.click(screen.getByRole('button', { name: 'खोजें' }));
    expect(await screen.findByRole('heading', { name: 'वाहन' })).toBeInTheDocument();
    expect(screen.getByText('संस्करण 4')).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: '3 अध्याय' })).toBeInTheDocument();
    expect(screen.getByRole('list', { name: 'अध्याय' })).toBeInTheDocument();
    expect(document.body.textContent).not.toMatch(/knowledge\.[a-z]/i);
  });
});
