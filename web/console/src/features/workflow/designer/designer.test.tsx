import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { setLanguage } from '../../../i18n';
import { blankDefinition, twoLevelApproval } from '../definition';
import { asha, callsTo, json, renderAt, standardApi, workflow } from './testApi';

beforeEach(() => {
  setLanguage('en');
  window.localStorage.clear();
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const ok = () => json({ valid: true, errors: [] });
const stepButton = (name: string) => screen.getByRole('button', { name });
const inspector = () => screen.getByRole('region', { name: /^Step \d+:/ });

/** The API calls a save makes, answered successfully. */
const savingApi = (extra: Parameters<typeof standardApi>[0] = {}) => {
  const list = extra.workflows ?? [workflow()];
  return standardApi({ ...extra, workflows: list }, (c) => {
    if (c.method === 'POST' && c.pathname === '/api/v1/workflows/validate') return ok();
    if (c.method === 'POST' && c.pathname === '/api/v1/workflows') {
      const b = c.body as { name: string; description: string | null; definition: unknown };
      const created = workflow({ id: 'w-new', name: b.name, description: b.description ?? '', definition: b.definition });
      list.push(created);
      return json(created, 201);
    }
    if (c.method === 'PUT' && c.pathname === '/api/v1/workflows/w1') {
      const b = c.body as { name: string; description: string | null; definition: unknown };
      return json(workflow({ name: b.name, description: b.description ?? '', definition: b.definition }));
    }
    return undefined;
  });
};

describe('designer: opening a workflow', () => {
  it('lays the steps out as cards between Submitted and Approved', async () => {
    savingApi();
    renderAt('/settings/workflows/w1');
    expect(await screen.findByRole('heading', { level: 1, name: 'Two-level approval' })).toBeInTheDocument();
    const review = stepButton('Step 1: Review');
    expect(review).toHaveTextContent('Approver');
    expect(review).toHaveTextContent('Any one');
    expect(review).toHaveTextContent('48 h');
    expect(review).toHaveTextContent('Escalates to Admin');
    expect(review).toHaveTextContent('Rejected → back to draft');
    expect(stepButton('Step 2: Final approval')).toHaveTextContent('Librarian');
    expect(screen.getByText('Submitted')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Approved/ })).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'Name' })).toHaveValue('Two-level approval');
    // Nothing selected: the inspector shows the workflow-level settings.
    expect(screen.getByRole('heading', { name: 'Workflow settings' })).toBeInTheDocument();
    expect(screen.queryByText('Unsaved changes')).not.toBeInTheDocument();
  });

  it('names assignees through the user directory, never the admin-only user list, and keeps a short id for someone unknown', async () => {
    const stranger = '99999999-9999-4999-8999-999999999999';
    const def = twoLevelApproval();
    def.steps[0]!.assignees = { roles: [], users: [asha.id, stranger] };
    standardApi({ workflows: [workflow({ definition: def })], users: [asha] }, (c) =>
      /^\/api\/v1\/users\/(?!directory$)[^/]+$/.test(c.pathname) ? json({ title: 'Forbidden', status: 403 }, 403) : undefined,
    );
    renderAt('/settings/workflows/w1');
    const review = await screen.findByRole('button', { name: 'Step 1: Review' });
    await waitFor(() => expect(review).toHaveTextContent('Asha Rao'));
    expect(review).toHaveTextContent('99999999…');
    expect(callsTo('GET', '/api/v1/users')).toHaveLength(0);
    expect(callsTo('GET', '/api/v1/users/directory').length).toBeGreaterThan(0);
  });

  it('says so when the workflow does not exist', async () => {
    savingApi();
    renderAt('/settings/workflows/missing');
    expect(await screen.findByText('That workflow does not exist')).toBeInTheDocument();
  });

  it('shows a no-permission state on 403', async () => {
    standardApi({}, (c) => (c.pathname === '/api/v1/workflows' ? json({ title: 'Forbidden', status: 403 }, 403) : undefined));
    renderAt('/settings/workflows/w1');
    expect(await screen.findByText('You do not have access to workflows')).toBeInTheDocument();
  });
});

describe('designer: editing the flow', () => {
  it('adds a step after another, selects it and chains it into the flow', async () => {
    savingApi();
    renderAt('/settings/workflows/w1');
    await screen.findByRole('heading', { level: 1, name: 'Two-level approval' });
    await userEvent.click(screen.getByRole('button', { name: 'Add a step after Review' }));
    const added = stepButton('Step 2: Untitled step');
    expect(added).toHaveAttribute('aria-pressed', 'true');
    expect(stepButton('Step 3: Final approval')).toBeInTheDocument();
    expect(inspector()).toBeInTheDocument();
    expect(screen.getByText('Unsaved changes')).toBeInTheDocument();
    // The blank step has nobody to decide it, so the card is flagged.
    expect(added).toHaveTextContent('No one assigned');
    expect(added).toHaveTextContent('2 problems');
  });

  it('inserts a step at the start and at the end', async () => {
    savingApi();
    renderAt('/settings/workflows/w1');
    await screen.findByRole('heading', { level: 1, name: 'Two-level approval' });
    await userEvent.click(screen.getByRole('button', { name: 'Add a step at the start' }));
    expect(stepButton('Step 1: Untitled step')).toHaveAttribute('aria-pressed', 'true');
    expect(stepButton('Step 2: Review')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Add a step at the end' }));
    expect(stepButton('Step 4: Untitled step')).toBeInTheDocument();
    expect(stepButton('Step 3: Final approval')).toBeInTheDocument();
  });

  it('edits flow through the inspector onto the card', async () => {
    savingApi();
    renderAt('/settings/workflows/w1');
    await userEvent.click(await screen.findByRole('button', { name: 'Step 1: Review' }));
    const panel = inspector();

    const name = within(panel).getByRole('textbox', { name: 'Step name' });
    await userEvent.clear(name);
    await userEvent.type(name, 'Regional review');
    expect(stepButton('Step 1: Regional review')).toBeInTheDocument();
    expect(screen.getByRole('heading', { level: 2, name: 'Step 1: Regional review' })).toBeInTheDocument();

    const who = within(panel).getByRole('group', { name: 'Who decides' });
    await userEvent.click(within(who).getByRole('checkbox', { name: 'Editor' }));
    expect(stepButton('Step 1: Regional review')).toHaveTextContent('Editor');

    const sla = within(panel).getByRole('spinbutton', { name: 'Deadline (hours)' });
    await userEvent.clear(sla);
    await userEvent.type(sla, '36');
    expect(stepButton('Step 1: Regional review')).toHaveTextContent('36 h');
    await userEvent.clear(sla);
    expect(stepButton('Step 1: Regional review')).toHaveTextContent('No deadline');

    await userEvent.click(within(panel).getByRole('checkbox', { name: 'Escalate when overdue' }));
    expect(stepButton('Step 1: Regional review')).not.toHaveTextContent('Escalates');
    expect(within(panel).getByText('Overdue tasks are also offered to these people.')).toBeInTheDocument();
  });

  it('switches between Any one and Everyone and explains each', async () => {
    savingApi();
    renderAt('/settings/workflows/w1');
    await userEvent.click(await screen.findByRole('button', { name: 'Step 1: Review' }));
    const panel = inspector();
    expect(within(panel).getByText(/first person to decide settles the step/)).toBeInTheDocument();
    await userEvent.click(within(panel).getByRole('button', { name: 'Everyone' }));
    expect(stepButton('Step 1: Review')).toHaveTextContent('Everyone');
    expect(within(panel).getByText(/All of them must approve/)).toBeInTheDocument();
    await userEvent.click(within(panel).getByRole('button', { name: 'Any one' }));
    expect(stepButton('Step 1: Review')).toHaveTextContent('Any one');
  });

  it('routes a rejection to another step and shows it on the card', async () => {
    savingApi();
    renderAt('/settings/workflows/w1');
    await userEvent.click(await screen.findByRole('button', { name: 'Step 2: Final approval' }));
    await userEvent.selectOptions(within(inspector()).getByRole('combobox', { name: 'On reject' }), 'review');
    expect(stepButton('Step 2: Final approval')).toHaveTextContent('Rejected → Review');
  });

  it('adds a person by searching and shows their name', async () => {
    savingApi();
    renderAt('/settings/workflows/w1');
    await userEvent.click(await screen.findByRole('button', { name: 'Step 1: Review' }));
    const who = within(inspector()).getByRole('group', { name: 'Who decides' });
    const box = within(who).getByRole('combobox', { name: 'People' });
    await userEvent.type(box, 'ash');
    const option = await screen.findByRole('option', { name: /Asha Rao/ });
    await userEvent.click(option);
    expect(within(who).getByRole('list', { name: 'Selected people' })).toHaveTextContent('Asha Rao');
    expect(stepButton('Step 1: Review')).toHaveTextContent('Asha Rao');
    await userEvent.click(within(who).getByRole('button', { name: 'Remove Asha Rao' }));
    expect(stepButton('Step 1: Review')).not.toHaveTextContent('Asha Rao');
  });

  it('picks a search result with the keyboard', async () => {
    savingApi();
    renderAt('/settings/workflows/w1');
    await userEvent.click(await screen.findByRole('button', { name: 'Step 1: Review' }));
    const box = within(inspector()).getAllByRole('combobox', { name: 'People' })[0]!;
    await userEvent.type(box, 'asha');
    await screen.findByRole('option', { name: /Asha Rao/ });
    await userEvent.keyboard('{Enter}');
    expect(stepButton('Step 1: Review')).toHaveTextContent('Asha Rao');
  });

  it('falls back to typing role names when roles cannot be listed', async () => {
    savingApi({ rolesStatus: 403 });
    renderAt('/settings/workflows/w1');
    await userEvent.click(await screen.findByRole('button', { name: 'Step 1: Review' }));
    const who = within(inspector()).getByRole('group', { name: 'Who decides' });
    expect(await within(who).findByText(/Roles could not be loaded/)).toBeInTheDocument();
    await userEvent.type(within(who).getByRole('textbox', { name: 'Add a role by name' }), 'Legal{Enter}');
    expect(stepButton('Step 1: Review')).toHaveTextContent('Legal');
  });

  it('renames a step id only once it is valid and keeps the references', async () => {
    savingApi();
    renderAt('/settings/workflows/w1');
    await userEvent.click(await screen.findByRole('button', { name: 'Step 1: Review' }));
    await userEvent.click(within(inspector()).getByText('Advanced'));
    const id = within(inspector()).getByRole('textbox', { name: 'Step id' });
    await userEvent.clear(id);
    await userEvent.type(id, 'Bad Id');
    expect(await within(inspector()).findByText(/Use 1-40 lowercase letters/)).toBeInTheDocument();
    await userEvent.clear(id);
    await userEvent.type(id, 'regional{Enter}');
    expect(screen.queryByText(/Use 1-40 lowercase letters/)).not.toBeInTheDocument();
    expect(stepButton('Step 1: Review')).toBeInTheDocument();
    // Nothing broke: no problems, and the selected step is still selected under its new id.
    expect(screen.getByRole('region', { name: 'Problems' })).toHaveTextContent('No problems found.');
  });

  it('workflow-level settings change the definition', async () => {
    savingApi();
    renderAt('/settings/workflows/w1');
    await screen.findByRole('heading', { name: 'Workflow settings' });
    await userEvent.click(screen.getByRole('checkbox', { name: 'Publish automatically after the final approval' }));
    expect(screen.getByText('Published automatically')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('checkbox', { name: 'The same person cannot approve two steps' }));
    await userEvent.click(screen.getByRole('button', { name: 'Save workflow' }));
    await waitFor(() => expect(callsTo('PUT', '/api/v1/workflows/w1')).toHaveLength(1));
    expect(callsTo('PUT', '/api/v1/workflows/w1')[0]!.body).toMatchObject({
      definition: { autoPublish: true, distinctApprovers: false },
    });
  });
});

describe('designer: removing steps', () => {
  it('removes a step after confirming and reconnects the flow', async () => {
    savingApi();
    renderAt('/settings/workflows/w1');
    await userEvent.click(await screen.findByRole('button', { name: 'Step 1: Review' }));
    await userEvent.click(within(inspector()).getByRole('button', { name: 'Delete step' }));
    const dialog = await screen.findByRole('dialog', { name: 'Delete this step?' });
    await userEvent.click(within(dialog).getByRole('button', { name: 'Delete step' }));
    expect(screen.queryByRole('button', { name: 'Step 2: Final approval' })).not.toBeInTheDocument();
    expect(stepButton('Step 1: Final approval')).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Workflow settings' })).toBeInTheDocument();
  });

  it('cancelling the confirmation keeps the step', async () => {
    savingApi();
    renderAt('/settings/workflows/w1');
    const card = await screen.findByRole('button', { name: 'Step 2: Final approval' });
    card.focus();
    await userEvent.keyboard('{Delete}');
    const dialog = await screen.findByRole('dialog', { name: 'Delete this step?' });
    await userEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    expect(stepButton('Step 2: Final approval')).toBeInTheDocument();
  });

  it('never deletes the only step', async () => {
    savingApi({ workflows: [workflow({ definition: blankDefinition() })] });
    renderAt('/settings/workflows/w1');
    await userEvent.click(await screen.findByRole('button', { name: 'Step 1: Review' }));
    expect(within(inspector()).getByRole('button', { name: 'Delete step' })).toBeDisabled();
    stepButton('Step 1: Review').focus();
    await userEvent.keyboard('{Delete}');
    expect(screen.queryByRole('dialog', { name: 'Delete this step?' })).not.toBeInTheDocument();
  });
});

describe('designer: keyboard', () => {
  it('moves the selection with the arrow keys and selects with Enter', async () => {
    savingApi();
    renderAt('/settings/workflows/w1');
    const first = await screen.findByRole('button', { name: 'Step 1: Review' });
    first.focus();
    await userEvent.keyboard('{Enter}');
    expect(first).toHaveAttribute('aria-pressed', 'true');
    await userEvent.keyboard('{ArrowDown}');
    const second = stepButton('Step 2: Final approval');
    expect(second).toHaveFocus();
    expect(second).toHaveAttribute('aria-pressed', 'true');
    expect(first).toHaveAttribute('aria-pressed', 'false');
    await userEvent.keyboard('{ArrowUp}');
    expect(first).toHaveFocus();
    await userEvent.keyboard('{End}');
    expect(second).toHaveFocus();
    await userEvent.keyboard('{Home}');
    expect(first).toHaveFocus();
  });
});

describe('designer: validation and saving', () => {
  it('blocks Save while a step is invalid and lists the problems, each leading to its step', async () => {
    savingApi();
    renderAt('/settings/workflows/w1');
    await screen.findByRole('heading', { level: 1, name: 'Two-level approval' });
    await userEvent.click(screen.getByRole('button', { name: 'Add a step after Review' }));
    await userEvent.click(screen.getByRole('button', { name: 'Save workflow' }));

    expect(await screen.findByText('Fix 2 problems before saving')).toBeInTheDocument();
    expect(callsTo('POST', '/api/v1/workflows/validate')).toHaveLength(0);
    expect(callsTo('PUT', '/api/v1/workflows/w1')).toHaveLength(0);
    const panel = screen.getByRole('region', { name: /Problems/ });
    expect(within(panel).getByText('Give the step a name.')).toBeInTheDocument();
    expect(within(panel).getByText('Choose at least one role or person to decide this step.')).toBeInTheDocument();

    // Leave this step, then choose a problem: its step is selected again.
    await userEvent.click(screen.getByRole('button', { name: 'Step 1: Review' }));
    await userEvent.click(within(panel).getByRole('button', { name: /Give the step a name/ }));
    expect(stepButton('Step 2: Untitled step')).toHaveAttribute('aria-pressed', 'true');
    expect(within(inspector()).getByText('Give the step a name.')).toBeInTheDocument();
  });

  it('needs a name', async () => {
    savingApi();
    renderAt('/settings/workflows/new');
    await userEvent.click(await screen.findByRole('button', { name: 'Save workflow' }));
    expect(await screen.findByText('Give the workflow a name.', { selector: '.dam-error' })).toBeInTheDocument();
    expect(callsTo('POST', '/api/v1/workflows')).toHaveLength(0);
  });

  it('shows what the server finds on Check, and clears it when the flow changes', async () => {
    standardApi({}, (c) =>
      c.pathname === '/api/v1/workflows/validate'
        ? json({ valid: false, errors: [{ path: 'steps[0].assignees.roles[0]', message: "There is no role named 'Approver'." }] })
        : undefined,
    );
    renderAt('/settings/workflows/w1');
    await userEvent.click(await screen.findByRole('button', { name: 'Check' }));
    const panel = screen.getByRole('region', { name: /Problems/ });
    expect(await within(panel).findByText("There is no role named 'Approver'.")).toBeInTheDocument();
    expect(stepButton('Step 1: Review')).toHaveTextContent('1 problem');
    expect(callsTo('POST', '/api/v1/workflows/validate')[0]!.body).toEqual({ definition: twoLevelApproval() });

    await userEvent.click(within(panel).getByRole('button', { name: /no role named/ }));
    expect(stepButton('Step 1: Review')).toHaveAttribute('aria-pressed', 'true');

    await userEvent.click(within(inspector()).getByRole('button', { name: 'Everyone' }));
    expect(screen.queryByText("There is no role named 'Approver'.")).not.toBeInTheDocument();
  });

  it('confirms a clean check', async () => {
    savingApi();
    renderAt('/settings/workflows/w1');
    await userEvent.click(await screen.findByRole('button', { name: 'Check' }));
    expect(await screen.findByText('Checked by the server: all good.')).toBeInTheDocument();
  });

  it('does not save when the server says the definition is invalid', async () => {
    standardApi({}, (c) =>
      c.pathname === '/api/v1/workflows/validate'
        ? json({
            valid: false,
            errors: [{ path: 'steps[1].assignees.roles[0]', message: "There is no role named 'Librarian'." }],
          })
        : undefined,
    );
    renderAt('/settings/workflows/w1');
    await userEvent.click(await screen.findByRole('button', { name: 'Step 1: Review' }));
    await userEvent.click(within(inspector()).getByRole('button', { name: 'Everyone' }));
    await userEvent.click(screen.getByRole('button', { name: 'Save workflow' }));
    expect(await screen.findByText("There is no role named 'Librarian'.")).toBeInTheDocument();
    expect(callsTo('PUT', '/api/v1/workflows/w1')).toHaveLength(0);
    expect(stepButton('Step 2: Final approval')).toHaveTextContent('1 problem');
  });

  it('maps a 422 from the save onto the steps', async () => {
    standardApi({}, (c) => {
      if (c.pathname === '/api/v1/workflows/validate') return ok();
      if (c.method === 'PUT')
        return json(
          {
            title: 'One or more validation errors occurred.',
            status: 422,
            errors: { 'definition.steps[0].slaHours': ['Use a whole number of hours from 1 to 8760.'] },
          },
          422,
        );
      return undefined;
    });
    renderAt('/settings/workflows/w1');
    await userEvent.click(await screen.findByRole('button', { name: 'Step 1: Review' }));
    await userEvent.click(within(inspector()).getByRole('button', { name: 'Everyone' }));
    await userEvent.click(screen.getByRole('button', { name: 'Save workflow' }));
    expect(await screen.findAllByText('Use a whole number of hours from 1 to 8760.')).not.toHaveLength(0);
    expect(stepButton('Step 1: Review')).toHaveTextContent('1 problem');
  });

  it('shows a name clash from the server next to the name', async () => {
    standardApi({}, (c) => {
      if (c.pathname === '/api/v1/workflows/validate') return ok();
      if (c.method === 'PUT') return json({ title: "A workflow named 'x' already exists.", status: 409 }, 409);
      return undefined;
    });
    renderAt('/settings/workflows/w1');
    const name = await screen.findByRole('textbox', { name: 'Name' });
    await userEvent.type(name, ' 2');
    await userEvent.click(screen.getByRole('button', { name: 'Save workflow' }));
    expect(await screen.findByText('A workflow with this name already exists.')).toBeInTheDocument();
  });

  it('validates on the server, then PUTs the exact payload, and the unsaved marker clears', async () => {
    savingApi();
    renderAt('/settings/workflows/w1');
    await userEvent.click(await screen.findByRole('button', { name: 'Step 1: Review' }));
    await userEvent.click(within(inspector()).getByRole('button', { name: 'Everyone' }));
    const desc = screen.getByRole('textbox', { name: 'Description' });
    await userEvent.clear(desc);
    await userEvent.type(desc, 'Two reviews');
    expect(screen.getByText('Unsaved changes')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Save workflow' }));

    await waitFor(() => expect(callsTo('PUT', '/api/v1/workflows/w1')).toHaveLength(1));
    const expected = twoLevelApproval();
    expected.steps[0]!.mode = 'all';
    expect(callsTo('POST', '/api/v1/workflows/validate')[0]!.body).toEqual({ definition: expected });
    expect(callsTo('PUT', '/api/v1/workflows/w1')[0]!.body).toEqual({
      name: 'Two-level approval',
      description: 'Two reviews',
      definition: expected,
    });
    expect(await screen.findByText('Workflow saved')).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByText('Unsaved changes')).not.toBeInTheDocument());
  });

  it('creates a new workflow from the blank template and opens it', async () => {
    savingApi();
    renderAt('/settings/workflows/new');
    expect(await screen.findByRole('heading', { level: 1, name: 'New workflow' })).toBeInTheDocument();
    expect(stepButton('Step 1: Review')).toHaveTextContent('Approver');
    await userEvent.type(screen.getByRole('textbox', { name: 'Name' }), '  Brand sign-off ');
    await userEvent.click(screen.getByRole('button', { name: 'Add a step at the end' }));
    const panel = inspector();
    await userEvent.type(within(panel).getByRole('textbox', { name: 'Step name' }), 'Final');
    await userEvent.click(
      within(within(panel).getByRole('group', { name: 'Who decides' })).getByRole('checkbox', { name: 'Librarian' }),
    );
    await userEvent.type(within(panel).getByRole('spinbutton', { name: 'Deadline (hours)' }), '24');
    await userEvent.click(within(panel).getByRole('checkbox', { name: 'Escalate when overdue' }));
    await userEvent.click(
      within(within(panel).getByRole('group', { name: 'Escalate to' })).getByRole('checkbox', { name: 'Admin' }),
    );
    await userEvent.click(screen.getByRole('button', { name: 'Save workflow' }));

    await waitFor(() => expect(callsTo('POST', '/api/v1/workflows')).toHaveLength(1));
    expect(callsTo('POST', '/api/v1/workflows')[0]!.body).toEqual({
      name: 'Brand sign-off',
      description: null,
      definition: {
        version: 1,
        start: 'review',
        autoPublish: false,
        distinctApprovers: true,
        steps: [
          {
            id: 'review',
            name: 'Review',
            assignees: { roles: ['Approver'], users: [] },
            mode: 'any',
            slaHours: null,
            escalation: null,
            onApprove: 'step-2',
            onReject: 'reject',
          },
          {
            id: 'step-2',
            name: 'Final',
            assignees: { roles: ['Librarian'], users: [] },
            mode: 'any',
            slaHours: 24,
            escalation: { to: { roles: ['Admin'], users: [] } },
            onApprove: 'complete',
            onReject: 'reject',
          },
        ],
      },
    });
    // Now editing the saved workflow, at its own address.
    expect(await screen.findByRole('heading', { level: 1, name: 'Brand sign-off' })).toBeInTheDocument();
    expect(screen.queryByText('Unsaved changes')).not.toBeInTheDocument();
    expect(stepButton('Step 2: Final')).toBeInTheDocument();
  });

  it('starts from the Two-level approval template, asking before it replaces edits', async () => {
    savingApi();
    renderAt('/settings/workflows/new');
    await screen.findByRole('heading', { level: 1, name: 'New workflow' });
    await userEvent.click(screen.getByRole('button', { name: 'Two-level approval' }));
    expect(stepButton('Step 1: Review')).toHaveTextContent('48 h');
    expect(stepButton('Step 2: Final approval')).toHaveTextContent('Librarian');

    // Edit, then go back to Blank: the edit is protected by a confirmation.
    await userEvent.click(stepButton('Step 1: Review'));
    await userEvent.click(within(inspector()).getByRole('button', { name: 'Everyone' }));
    await userEvent.click(screen.getByRole('button', { name: 'Blank' }));
    const dialog = await screen.findByRole('dialog', { name: 'Replace your steps?' });
    await userEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    expect(stepButton('Step 2: Final approval')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Blank' }));
    await userEvent.click(
      within(await screen.findByRole('dialog', { name: 'Replace your steps?' })).getByRole('button', { name: 'Replace steps' }),
    );
    expect(screen.queryByRole('button', { name: 'Step 2: Final approval' })).not.toBeInTheDocument();
  });

  it('discards edits back to the saved workflow', async () => {
    savingApi();
    renderAt('/settings/workflows/w1');
    await userEvent.click(await screen.findByRole('button', { name: 'Add a step at the end' }));
    expect(screen.getByRole('button', { name: 'Step 3: Untitled step' })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Discard' }));
    expect(screen.queryByRole('button', { name: 'Step 3: Untitled step' })).not.toBeInTheDocument();
    expect(screen.queryByText('Unsaved changes')).not.toBeInTheDocument();
  });
});

describe('designer: JSON view', () => {
  const jsonBox = () => screen.getByRole('textbox', { name: 'Workflow definition (JSON)' }) as HTMLTextAreaElement;

  it('round-trips the definition exactly', async () => {
    savingApi();
    renderAt('/settings/workflows/w1');
    await screen.findByRole('heading', { level: 1, name: 'Two-level approval' });
    await userEvent.click(screen.getByRole('button', { name: 'JSON' }));
    expect(JSON.parse(jsonBox().value)).toEqual(twoLevelApproval());
    await userEvent.click(screen.getByRole('button', { name: 'Visual' }));
    await userEvent.click(screen.getByRole('button', { name: 'JSON' }));
    expect(JSON.parse(jsonBox().value)).toEqual(twoLevelApproval());
    expect(screen.queryByText('Unsaved changes')).not.toBeInTheDocument();
  });

  it('applies an edit to the visual editor', async () => {
    savingApi();
    renderAt('/settings/workflows/w1');
    await screen.findByRole('heading', { level: 1, name: 'Two-level approval' });
    await userEvent.click(screen.getByRole('button', { name: 'JSON' }));
    const edited = twoLevelApproval();
    edited.steps[0]!.name = 'Regional review';
    edited.steps[0]!.slaHours = 12;
    edited.autoPublish = true;
    fireEvent.change(jsonBox(), { target: { value: JSON.stringify(edited, null, 2) } });
    await userEvent.click(screen.getByRole('button', { name: 'Visual' }));
    expect(stepButton('Step 1: Regional review')).toHaveTextContent('12 h');
    expect(screen.getByText('Published automatically')).toBeInTheDocument();
    expect(screen.getByText('Unsaved changes')).toBeInTheDocument();
  });

  it('shows a parse error inline and stays on the JSON view until it is fixed', async () => {
    savingApi();
    renderAt('/settings/workflows/w1');
    await screen.findByRole('heading', { level: 1, name: 'Two-level approval' });
    await userEvent.click(screen.getByRole('button', { name: 'JSON' }));
    fireEvent.change(jsonBox(), { target: { value: '{ "steps": [' } });
    expect(await screen.findByText('That is not valid JSON.', { selector: '.dam-error' })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Visual' }));
    expect(jsonBox()).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Save workflow' }));
    expect(callsTo('PUT', '/api/v1/workflows/w1')).toHaveLength(0);
    fireEvent.change(jsonBox(), { target: { value: JSON.stringify(twoLevelApproval()) } });
    await userEvent.click(screen.getByRole('button', { name: 'Visual' }));
    expect(stepButton('Step 1: Review')).toBeInTheDocument();
  });

  it('keeps properties it does not know when saving', async () => {
    const stored = { ...twoLevelApproval(), layout: { zoom: 2 } };
    savingApi({ workflows: [workflow({ definition: stored })] });
    renderAt('/settings/workflows/w1');
    await userEvent.click(await screen.findByRole('button', { name: 'Step 1: Review' }));
    await userEvent.click(within(inspector()).getByRole('button', { name: 'Everyone' }));
    await userEvent.click(screen.getByRole('button', { name: 'Save workflow' }));
    await waitFor(() => expect(callsTo('PUT', '/api/v1/workflows/w1')).toHaveLength(1));
    expect(callsTo('PUT', '/api/v1/workflows/w1')[0]!.body).toMatchObject({ definition: { layout: { zoom: 2 } } });
  });
});

describe('designer: leaving with unsaved changes', () => {
  it('asks before an in-app link discards edits, and lets you stay or leave', async () => {
    savingApi();
    renderAt('/settings/workflows/w1');
    await userEvent.click(await screen.findByRole('button', { name: 'Add a step at the end' }));
    await userEvent.click(screen.getByRole('link', { name: 'All workflows' }));
    const dialog = await screen.findByRole('dialog', { name: 'Leave without saving?' });
    await userEvent.click(within(dialog).getByRole('button', { name: 'Keep editing' }));
    expect(screen.getByRole('button', { name: 'Step 3: Untitled step' })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('link', { name: 'All workflows' }));
    await userEvent.click(
      within(await screen.findByRole('dialog', { name: 'Leave without saving?' })).getByRole('button', {
        name: 'Leave without saving',
      }),
    );
    expect(await screen.findByRole('heading', { level: 1, name: 'Workflows' })).toBeInTheDocument();
  });

  it('does not interrupt when nothing changed, and warns the browser when something did', async () => {
    savingApi();
    renderAt('/settings/workflows/w1');
    await screen.findByRole('heading', { level: 1, name: 'Two-level approval' });
    const clean = new Event('beforeunload', { cancelable: true });
    window.dispatchEvent(clean);
    expect(clean.defaultPrevented).toBe(false);
    await userEvent.click(screen.getByRole('button', { name: 'Add a step at the end' }));
    const dirty = new Event('beforeunload', { cancelable: true });
    window.dispatchEvent(dirty);
    expect(dirty.defaultPrevented).toBe(true);
  });
});

describe('designer: accessibility', () => {
  it('has no violations with a step selected', async () => {
    savingApi();
    const { container } = renderAt('/settings/workflows/w1');
    await userEvent.click(await screen.findByRole('button', { name: 'Step 1: Review' }));
    await within(inspector()).findAllByRole('checkbox', { name: 'Approver' });
    expect(await axe(container)).toHaveNoViolations();
  });

  it('has no violations in the settings, JSON and problem states', async () => {
    savingApi({ users: [asha] });
    const { container } = renderAt('/settings/workflows/new');
    await screen.findByRole('heading', { level: 1, name: 'New workflow' });
    await userEvent.click(screen.getByRole('button', { name: 'Add a step at the end' }));
    await userEvent.click(screen.getByRole('button', { name: 'Save workflow' }));
    await screen.findByRole('button', { name: /Give the step a name/ });
    expect(await axe(container)).toHaveNoViolations();
    await userEvent.click(screen.getByRole('button', { name: 'JSON' }));
    await screen.findByRole('textbox', { name: 'Workflow definition (JSON)' });
    expect(await axe(container)).toHaveNoViolations();
  });
});
