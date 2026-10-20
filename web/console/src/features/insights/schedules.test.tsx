import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { queryClient } from '../../App';
import { setLanguage } from '../../i18n';
import { renderAt } from '../workflow/workflowTestKit';
import { MANAGER, USERS, callsTo, install, json, newWorld, schedule, user, type World } from './insightsTestKit';
import type { ReportScheduleInput } from './insightsTypes';

beforeEach(() => {
  queryClient.clear();
  setLanguage('en');
  window.localStorage.clear();
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const SCHEDULES = '/api/v1/analytics/report-schedules';
const writes = (w: World, method: string) =>
  w.calls.filter((c) => c.method === method && c.pathname.startsWith(SCHEDULES) && !c.pathname.endsWith('/send-now'));
const openPage = async (w: World) => {
  install(w, MANAGER);
  renderAt('/insights/reports');
  return screen.findByRole('heading', { name: 'Scheduled reports' });
};

describe('Schedules list', () => {
  it('lists each schedule in words, with recipients as names, status, last sent and next run', async () => {
    const w = newWorld({ schedules: [schedule()] });
    await openPage(w);
    const list = await screen.findByRole('list', { name: 'Report schedules' });
    const row = within(within(list).getByRole('listitem'));
    expect(row.getByText('Most used assets')).toBeInTheDocument();
    expect(row.getByText('Every Monday at 08:00 UTC · covers the last 30 days')).toBeInTheDocument();
    expect(await row.findByText('Asha Rao, Ben Okafor')).toBeInTheDocument();
    expect(row.getByText('On')).toBeInTheDocument();
    expect(row.getByText(/Oct 12, 2026/)).toBeInTheDocument();
    expect(row.getByText(/Oct 26, 2026/)).toBeInTheDocument();
    expect(row.getByRole('switch', { name: 'Send Most used assets on schedule' })).toBeChecked();
  });

  it('describes daily and monthly schedules and shows paused ones as paused with no next run', async () => {
    const w = newWorld({
      schedules: [
        schedule({ id: 'd', frequency: 'daily', dayOfWeek: null, hourUtc: 6, report: 'storage' }),
        schedule({
          id: 'm',
          frequency: 'monthly',
          dayOfWeek: null,
          dayOfMonth: 15,
          hourUtc: 17,
          enabled: false,
          nextRunAt: null,
        }),
      ],
    });
    await openPage(w);
    expect(await screen.findByText('Every day at 06:00 UTC · covers the last 30 days')).toBeInTheDocument();
    expect(screen.getByText('On day 15 of every month at 17:00 UTC · covers the last 30 days')).toBeInTheDocument();
    const paused = within(screen.getAllByRole('listitem')[1]!);
    expect(paused.getByText('Paused', { selector: '.dam-badge' })).toBeInTheDocument();
    expect(paused.getByRole('switch')).not.toBeChecked();
  });

  it('shows the last error and when nothing has been sent yet', async () => {
    const w = newWorld({ schedules: [schedule({ lastSentAt: null, lastError: 'Email is not configured.' })] });
    await openPage(w);
    expect(await screen.findByText('Not sent yet')).toBeInTheDocument();
    expect(screen.getByRole('note')).toHaveTextContent('Last error: Email is not configured.');
  });

  it('explains plainly that recipients are people in this organisation and that the email links back here', async () => {
    await openPage(newWorld());
    expect(screen.getByText(/Recipients are people with an account here, never outside addresses/)).toBeInTheDocument();
    expect(screen.getByText(/links back to this page/)).toBeInTheDocument();
    expect(await screen.findByText('No scheduled reports yet')).toBeInTheDocument();
  });

  it('pauses and resumes from the switch, sending the whole schedule with only enabled changed', async () => {
    const w = newWorld({ schedules: [schedule()] });
    await openPage(w);
    await userEvent.click(await screen.findByRole('switch', { name: 'Send Most used assets on schedule' }));
    await waitFor(() => expect(writes(w, 'PUT')).toHaveLength(1));
    const body = (await writes(w, 'PUT')[0]!.json()) as ReportScheduleInput;
    expect(body).toEqual({
      report: 'top-assets',
      days: 30,
      frequency: 'weekly',
      hourUtc: 8,
      dayOfWeek: 1,
      dayOfMonth: null,
      recipientUserIds: ['u1', 'u2'],
      enabled: false,
    });
    await waitFor(() => expect(screen.getByRole('switch', { name: 'Send Most used assets on schedule' })).not.toBeChecked());
  });

  it('sends now and says so, or says why it could not', async () => {
    const w = newWorld({ schedules: [schedule()] });
    await openPage(w);
    await userEvent.click(await screen.findByRole('button', { name: 'Send Most used assets now' }));
    expect(await screen.findByText(/Sending Most used assets now/)).toBeInTheDocument();
    expect(callsTo(w, `${SCHEDULES}/s1/send-now`, 'POST')).toHaveLength(1);

    w.sendNow = json({ title: 'Conflict', status: 409, detail: 'Email is not configured.' }, 409);
    await userEvent.click(screen.getByRole('button', { name: 'Send Most used assets now' }));
    expect(await screen.findByText("Couldn't send the report: Email is not configured.")).toBeInTheDocument();
  });

  it('deletes only after confirming', async () => {
    const w = newWorld({ schedules: [schedule()] });
    await openPage(w);
    await userEvent.click(await screen.findByRole('button', { name: 'Delete schedule for Most used assets' }));
    let dialog = await screen.findByRole('dialog', { name: 'Delete this schedule?' });
    await userEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    expect(writes(w, 'DELETE')).toHaveLength(0);

    await userEvent.click(screen.getByRole('button', { name: 'Delete schedule for Most used assets' }));
    dialog = await screen.findByRole('dialog', { name: 'Delete this schedule?' });
    expect(within(dialog).getByText(/will no longer be emailed on this schedule/)).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Delete schedule' }));
    await waitFor(() => expect(writes(w, 'DELETE')).toHaveLength(1));
    expect(await screen.findByText('Schedule deleted')).toBeInTheDocument();
    expect(await screen.findByText('No scheduled reports yet')).toBeInTheDocument();
  });

  it('reports a failed load with a retry', async () => {
    const w = newWorld();
    install(w, MANAGER, (c) =>
      c.pathname === SCHEDULES && c.method === 'GET' ? json({ title: 'Boom', status: 500 }, 500) : undefined,
    );
    renderAt('/insights/reports');
    expect(await screen.findByText("Couldn't load the schedules", {}, { timeout: 15_000 })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeInTheDocument();
  }, 30_000);
});

describe('Schedule form', () => {
  const openForm = async () => {
    await userEvent.click((await screen.findAllByRole('button', { name: 'New schedule' }))[0]!);
    return screen.findByRole('dialog', { name: 'New scheduled report' });
  };
  const addPerson = async (dialog: HTMLElement, query: string, name: string) => {
    const box = within(dialog).getByRole('combobox', { name: 'Add a recipient' });
    await userEvent.type(box, query);
    await userEvent.click(await within(dialog).findByRole('option', { name: new RegExp(name) }));
  };

  it('refuses to save without a recipient or with bad days, and names what is wrong', async () => {
    const w = newWorld();
    await openPage(w);
    const dialog = await openForm();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create schedule' }));
    expect(await within(dialog).findByText('Add at least one recipient.')).toBeInTheDocument();
    expect(writes(w, 'POST')).toHaveLength(0);

    const days = within(dialog).getByRole('spinbutton', { name: 'Period covered (days)' });
    await userEvent.clear(days);
    await userEvent.type(days, '400');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create schedule' }));
    expect(await within(dialog).findByText('Enter a whole number of days from 1 to 365.')).toBeInTheDocument();
    expect(days).toHaveAttribute('aria-invalid', 'true');
    expect(writes(w, 'POST')).toHaveLength(0);
  });

  it('creates a weekly schedule with a picked recipient and shows it in the list', async () => {
    const w = newWorld();
    await openPage(w);
    const dialog = await openForm();
    expect(within(dialog).getByRole('combobox', { name: 'Report' })).toHaveValue('top-assets');
    await userEvent.selectOptions(within(dialog).getByRole('combobox', { name: 'On' }), '5');
    await addPerson(dialog, 'ash', 'Asha Rao');
    const chips = within(dialog).getByRole('list', { name: 'Selected recipients' });
    expect(within(chips).getByText('Asha Rao')).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create schedule' }));

    await waitFor(() => expect(writes(w, 'POST')).toHaveLength(1));
    expect(await writes(w, 'POST')[0]!.json()).toEqual({
      report: 'top-assets',
      days: 30,
      frequency: 'weekly',
      hourUtc: 8,
      dayOfWeek: 5,
      dayOfMonth: null,
      recipientUserIds: ['u1'],
      enabled: true,
    });
    expect(await screen.findByText('Schedule created')).toBeInTheDocument();
    expect(await screen.findByText(/Every Friday at 08:00 UTC/)).toBeInTheDocument();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('lets unused assets look back further than a year, and no other report', async () => {
    const w = newWorld();
    await openPage(w);
    const dialog = await openForm();
    const days = within(dialog).getByRole('spinbutton', { name: 'Period covered (days)' });
    await userEvent.clear(days);
    await userEvent.type(days, '1000');
    await addPerson(dialog, 'ash', 'Asha Rao');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create schedule' }));
    expect(await within(dialog).findByText('Enter a whole number of days from 1 to 365.')).toBeInTheDocument();
    await userEvent.selectOptions(within(dialog).getByRole('combobox', { name: 'Report' }), 'unused-assets');
    expect(within(dialog).queryByText('Enter a whole number of days from 1 to 365.')).not.toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create schedule' }));
    await waitFor(() => expect(writes(w, 'POST')).toHaveLength(1));
    expect(await writes(w, 'POST')[0]!.json()).toMatchObject({ report: 'unused-assets', days: 1000 });
  });

  it('asks for a day of the month only when monthly, and nothing extra when daily', async () => {
    const w = newWorld();
    await openPage(w);
    const dialog = await openForm();
    expect(within(dialog).getByRole('combobox', { name: 'On' })).toBeInTheDocument();
    await userEvent.selectOptions(within(dialog).getByRole('combobox', { name: 'How often' }), 'monthly');
    expect(within(dialog).queryByRole('combobox', { name: 'On' })).not.toBeInTheDocument();
    await userEvent.selectOptions(within(dialog).getByRole('combobox', { name: 'Day of the month' }), '15');
    await userEvent.selectOptions(within(dialog).getByRole('combobox', { name: 'At (UTC)' }), '17');
    await addPerson(dialog, 'ben', 'Ben Okafor');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create schedule' }));
    await waitFor(() => expect(writes(w, 'POST')).toHaveLength(1));
    expect(await writes(w, 'POST')[0]!.json()).toMatchObject({
      frequency: 'monthly',
      dayOfMonth: 15,
      dayOfWeek: null,
      hourUtc: 17,
    });
    await screen.findByText('On day 15 of every month at 17:00 UTC · covers the last 30 days');

    const again = await openForm();
    await userEvent.selectOptions(within(again).getByRole('combobox', { name: 'How often' }), 'daily');
    expect(within(again).queryByRole('combobox', { name: 'On' })).not.toBeInTheDocument();
    expect(within(again).queryByRole('combobox', { name: 'Day of the month' })).not.toBeInTheDocument();
  });

  it('edits an existing schedule, removing a recipient with its button', async () => {
    const w = newWorld({ schedules: [schedule()] });
    await openPage(w);
    await userEvent.click(await screen.findByRole('button', { name: 'Edit schedule for Most used assets' }));
    const dialog = await screen.findByRole('dialog', { name: 'Edit scheduled report' });
    expect(within(dialog).getByRole('combobox', { name: 'How often' })).toHaveValue('weekly');
    expect(within(dialog).getByRole('combobox', { name: 'On' })).toHaveValue('1');
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Remove Ben Okafor' }));
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(writes(w, 'PUT')).toHaveLength(1));
    expect(writes(w, 'PUT')[0]!.pathname).toBe(`${SCHEDULES}/s1`);
    expect(await writes(w, 'PUT')[0]!.json()).toMatchObject({ recipientUserIds: ['u1'], frequency: 'weekly', dayOfWeek: 1 });
    expect(await screen.findByText('Schedule saved')).toBeInTheDocument();
  });

  it('does not offer someone who is already a recipient, and finds people by name or email', async () => {
    const w = newWorld({ schedules: [schedule({ recipientUserIds: ['u1'] })] });
    await openPage(w);
    await userEvent.click(await screen.findByRole('button', { name: 'Edit schedule for Most used assets' }));
    const dialog = await screen.findByRole('dialog', { name: 'Edit scheduled report' });
    const box = within(dialog).getByRole('combobox', { name: 'Add a recipient' });
    await userEvent.type(box, 'a');
    expect(await within(dialog).findByRole('option', { name: /Chloe Zhang/ })).toBeInTheDocument();
    expect(within(dialog).queryByRole('option', { name: /Asha Rao/ })).not.toBeInTheDocument();
    await userEvent.clear(box);
    await userEvent.type(box, 'ben@acme');
    expect(await within(dialog).findByRole('option', { name: /Ben Okafor/ })).toBeInTheDocument();
    expect(within(dialog).getByText('Only people in this organisation can receive a report.')).toBeInTheDocument();
  });

  it('keeps the dialog open with the reason when the server refuses', async () => {
    const w = newWorld({ schedules: [schedule()] });
    await openPage(w);
    await userEvent.click(await screen.findByRole('button', { name: 'Edit schedule for Most used assets' }));
    const dialog = await screen.findByRole('dialog', { name: 'Edit scheduled report' });
    w.refuse = json({ title: 'Unprocessable', status: 422, detail: 'Recipients must be users of this tenant.' }, 422);
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save changes' }));
    expect(await within(dialog).findByRole('alert')).toHaveTextContent(
      "Couldn't save the schedule: Recipients must be users of this tenant.",
    );
    expect(screen.getByRole('dialog', { name: 'Edit scheduled report' })).toBeInTheDocument();
  });

  it('names and finds people through the directory for a role that cannot list users', async () => {
    const w = newWorld({ schedules: [schedule()] });
    const listed: string[] = [];
    install(w, MANAGER, (c) => {
      if (c.pathname === '/api/v1/users' || /^\/api\/v1\/users\/(?!directory$)[^/]+$/.test(c.pathname)) {
        listed.push(c.pathname);
        return json({ title: 'Forbidden', status: 403 }, 403);
      }
      return undefined;
    });
    renderAt('/insights/reports');
    const list = await screen.findByRole('list', { name: 'Report schedules' });
    expect(await within(list).findByText('Asha Rao, Ben Okafor')).toBeInTheDocument();

    await userEvent.click(await screen.findByRole('button', { name: 'Edit schedule for Most used assets' }));
    const dialog = await screen.findByRole('dialog', { name: 'Edit scheduled report' });
    expect(within(dialog).getByRole('list', { name: 'Selected recipients' })).toHaveTextContent('Ben Okafor');
    await userEvent.type(within(dialog).getByRole('combobox', { name: 'Add a recipient' }), 'chl');
    await userEvent.click(await within(dialog).findByRole('option', { name: /Chloe Zhang/ }));
    expect(within(dialog).getByRole('list', { name: 'Selected recipients' })).toHaveTextContent('Chloe Zhang');

    const directory = callsTo(w, '/api/v1/users/directory');
    expect(directory.length).toBeGreaterThan(0);
    expect(directory.some((c) => c.search.get('q') === 'chl')).toBe(true);
    expect(listed).toEqual([]);
    expect(within(dialog).queryByText(/Couldn't load the list of people/)).not.toBeInTheDocument();
    expect(screen.queryByText(/users\.manage/)).not.toBeInTheDocument();
  });

  it('looks up a recipient the directory does not list (for example a deactivated one) by id, else keeps a short id', async () => {
    const w = newWorld({ schedules: [schedule({ recipientUserIds: ['u1', 'u2', 'u9'] })], users: USERS.slice(0, 1) });
    install(w, MANAGER, (c) =>
      c.pathname === '/api/v1/users/u2'
        ? json(user('u2', 'Ben Okafor'))
        : /^\/api\/v1\/users\/(?!directory$)[^/]+$/.test(c.pathname)
          ? json({ title: 'Not found', status: 404 }, 404)
          : undefined,
    );
    renderAt('/insights/reports');
    const list = await screen.findByRole('list', { name: 'Report schedules' });
    expect(await within(list).findByText('Asha Rao, Ben Okafor, u9…')).toBeInTheDocument();
  });

  it('says quietly that names may show as codes when the people list cannot be loaded', async () => {
    const w = newWorld({ schedules: [schedule()] });
    install(w, MANAGER, (c) =>
      c.pathname === '/api/v1/users/directory' ? json({ title: 'Forbidden', status: 403 }, 403) : undefined,
    );
    renderAt('/insights/reports');
    await userEvent.click(await screen.findByRole('button', { name: 'Edit schedule for Most used assets' }));
    const dialog = await screen.findByRole('dialog', { name: 'Edit scheduled report' });
    expect(
      await within(dialog).findByText("Couldn't load the list of people, so names may show as short codes."),
    ).toBeInTheDocument();
    expect(within(dialog).getByRole('list', { name: 'Selected recipients' })).toHaveTextContent('u1…');
  });

  it('has no axe violations with the list and with the dialog open', async () => {
    const w = newWorld({ schedules: [schedule({ lastError: 'Email is not configured.' })] });
    await openPage(w);
    await screen.findByRole('list', { name: 'Report schedules' });
    expect(await axe(document.body)).toHaveNoViolations();
    await userEvent.click(await screen.findByRole('button', { name: 'Edit schedule for Most used assets' }));
    await screen.findByRole('dialog', { name: 'Edit scheduled report' });
    expect(await axe(document.body)).toHaveNoViolations();
  });
});

describe('Schedules in Hindi', () => {
  it('words the frequency with a localised weekday', async () => {
    setLanguage('hi');
    const w = newWorld({ schedules: [schedule()] });
    install(w, MANAGER);
    renderAt('/insights/reports');
    expect(await screen.findByText(/हर सोमवार को 08:00 UTC पर/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'नई समय-सारणी' })).toBeInTheDocument();
  });
});
