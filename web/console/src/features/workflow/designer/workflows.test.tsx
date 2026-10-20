import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { setLanguage } from '../../../i18n';
import { twoLevelApproval } from '../definition';
import { copyName } from './WorkflowsPage';
import { callsTo, folder, json, renderAt, standardApi, workflow } from './testApi';

beforeEach(() => {
  setLanguage('en');
  window.localStorage.clear();
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const second = () =>
  workflow({
    id: 'w2',
    name: 'Legal sign-off',
    description: '',
    definition: { ...twoLevelApproval(), autoPublish: true },
    usedByFolders: 0,
    bindings: 0,
    runningInstances: 0,
  });

describe('workflows list', () => {
  it('shows each workflow with its flow, counts and actions', async () => {
    standardApi({ workflows: [workflow(), second()] });
    renderAt('/admin/workflows');
    expect(await screen.findByRole('heading', { level: 1, name: 'Workflows' })).toBeInTheDocument();
    const card = (await screen.findByRole('heading', { name: 'Two-level approval' })).closest('article')!;
    const flow = within(card).getByRole('list', { name: 'Flow of Two-level approval' });
    expect(
      within(flow)
        .getAllByRole('listitem')
        .filter((li) => li.textContent?.includes('Any one')),
    ).toHaveLength(2);
    expect(flow).toHaveTextContent('Review');
    expect(flow).toHaveTextContent('48 h');
    expect(flow).toHaveTextContent('Final approval');
    expect(flow).toHaveTextContent('24 h');
    expect(within(card).getByText('Running now').nextSibling).toHaveTextContent('3');
    expect(within(card).getByText('Folder defaults').nextSibling).toHaveTextContent('1');
    expect(within(card).getByText('Bindings').nextSibling).toHaveTextContent('2');
    expect(within(card).getByRole('link', { name: 'Edit Two-level approval' })).toHaveAttribute('href', '/admin/workflows/w1');
    expect(screen.getByText('Publishes automatically')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'New workflow' })).toHaveAttribute('href', '/admin/workflows/new');
  });

  it('explains how to get started when the tenant has no workflows', async () => {
    standardApi({ workflows: [] });
    renderAt('/admin/workflows');
    expect(await screen.findByText('No workflows yet')).toBeInTheDocument();
    expect(screen.getByText(/Two-level approval/)).toBeInTheDocument();
    expect(screen.getByText(/Create a workflow first/)).toBeInTheDocument();
  });

  it('shows a no-permission state on 403 and an error with retry on failure', async () => {
    standardApi({}, (c) => (c.pathname === '/api/v1/workflows' ? json({ title: 'Forbidden', status: 403 }, 403) : undefined));
    renderAt('/admin/workflows');
    expect(await screen.findByText('You do not have access to workflows')).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'New workflow' })).not.toBeInTheDocument();
    cleanup();

    let fail = true;
    standardApi({}, (c) => (c.pathname === '/api/v1/workflows' && fail ? json({ title: 'Boom', status: 500 }, 500) : undefined));
    renderAt('/admin/workflows');
    expect(await screen.findByText('We could not load the workflows', undefined, { timeout: 4000 })).toBeInTheDocument();
    fail = false;
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByRole('heading', { name: 'Two-level approval' })).toBeInTheDocument();
  });

  it('duplicates a workflow as "<name> copy" with the same definition', async () => {
    standardApi({}, (c) =>
      c.method === 'POST' && c.pathname === '/api/v1/workflows'
        ? json(workflow({ id: 'w9', name: 'Two-level approval copy' }), 201)
        : undefined,
    );
    renderAt('/admin/workflows');
    await userEvent.click(await screen.findByRole('button', { name: 'Duplicate Two-level approval' }));
    await waitFor(() => expect(callsTo('POST', '/api/v1/workflows')).toHaveLength(1));
    expect(callsTo('POST', '/api/v1/workflows')[0]!.body).toEqual({
      name: 'Two-level approval copy',
      description: 'Review, then final approval.',
      definition: twoLevelApproval(),
    });
    expect(await screen.findByText('Created “Two-level approval copy”')).toBeInTheDocument();
  });

  it('picks the first free copy name', () => {
    expect(copyName('Brand', ['Brand', 'brand copy'], 'copy')).toBe('Brand copy 2');
    expect(copyName('Brand', ['Brand'], 'copy')).toBe('Brand copy');
  });

  it('asks before deleting and relays why the server refuses', async () => {
    standardApi({}, (c) =>
      c.method === 'DELETE'
        ? json({ title: '2 folder(s) use this workflow as their default. Change them first.', status: 409 }, 409)
        : undefined,
    );
    renderAt('/admin/workflows');
    await userEvent.click(await screen.findByRole('button', { name: 'Delete Two-level approval' }));
    const dialog = await screen.findByRole('dialog', { name: 'Delete this workflow?' });
    await userEvent.click(within(dialog).getByRole('button', { name: 'Delete' }));
    const refusal = await within(dialog).findByRole('alert');
    expect(refusal).toHaveTextContent('“Two-level approval” cannot be deleted yet.');
    expect(refusal).toHaveTextContent('2 folder(s) use this workflow as their default. Change them first.');
    expect(callsTo('DELETE', '/api/v1/workflows/w1')).toHaveLength(1);
    expect(screen.getByRole('heading', { name: 'Two-level approval' })).toBeInTheDocument();
  });

  it('deletes after confirming', async () => {
    standardApi({}, (c) => (c.method === 'DELETE' ? new Response(null, { status: 204 }) : undefined));
    renderAt('/admin/workflows');
    await userEvent.click(await screen.findByRole('button', { name: 'Delete Two-level approval' }));
    const dialog = await screen.findByRole('dialog', { name: 'Delete this workflow?' });
    await userEvent.click(within(dialog).getByRole('button', { name: 'Delete' }));
    expect(await screen.findByText('Workflow deleted')).toBeInTheDocument();
  });

  it('has no accessibility violations', async () => {
    standardApi({
      workflows: [workflow(), second()],
      bindings: [{ id: 'b1', workflowId: 'w1', workflowName: 'Two-level approval', assetType: 'image', region: 'EMEA' }],
      folders: [folder(), folder({ id: 'f2', parentId: 'f1', path: 'campaigns.summer', label: 'summer', name: 'Summer' })],
    });
    const { container } = renderAt('/admin/workflows');
    await screen.findByRole('heading', { name: 'Two-level approval' });
    await screen.findByRole('group', { name: 'Binding 1' });
    await screen.findByText('Summer');
    expect(await axe(container)).toHaveNoViolations();
  });
});

describe('bindings editor', () => {
  const existing = { id: 'b1', workflowId: 'w1', workflowName: 'Two-level approval', assetType: 'image', region: 'EMEA' };

  it('saves the whole list in one replace-all call', async () => {
    standardApi({ workflows: [workflow(), second()], bindings: [existing] }, (c) =>
      c.method === 'PUT' && c.pathname === '/api/v1/workflows/bindings' ? json([]) : undefined,
    );
    renderAt('/admin/workflows');
    const first = await screen.findByRole('group', { name: 'Binding 1' });
    expect(within(first).getByRole('combobox', { name: 'Asset type' })).toHaveValue('image');
    expect(within(first).getByRole('textbox', { name: 'Region (optional)' })).toHaveValue('EMEA');
    expect(screen.queryByRole('button', { name: 'Save bindings' })).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Add binding' }));
    const added = await screen.findByRole('group', { name: 'Binding 2' });
    await userEvent.selectOptions(within(added).getByRole('combobox', { name: 'Asset type' }), 'video');
    await userEvent.selectOptions(within(added).getByRole('combobox', { name: 'Workflow' }), 'w2');
    await userEvent.click(screen.getByRole('button', { name: 'Save bindings' }));

    await waitFor(() => expect(callsTo('PUT', '/api/v1/workflows/bindings')).toHaveLength(1));
    expect(callsTo('PUT', '/api/v1/workflows/bindings')[0]!.body).toEqual({
      bindings: [
        { workflowId: 'w1', assetType: 'image', region: 'EMEA' },
        { workflowId: 'w2', assetType: 'video', region: null },
      ],
    });
    expect(await screen.findByText('Bindings saved')).toBeInTheDocument();
  });

  it('can remove the last binding (an empty list replaces everything)', async () => {
    standardApi({ bindings: [existing] }, (c) => (c.method === 'PUT' ? json([]) : undefined));
    renderAt('/admin/workflows');
    await userEvent.click(await screen.findByRole('button', { name: 'Remove binding 1' }));
    await userEvent.click(screen.getByRole('button', { name: 'Save bindings' }));
    await waitFor(() => expect(callsTo('PUT', '/api/v1/workflows/bindings')).toHaveLength(1));
    expect(callsTo('PUT', '/api/v1/workflows/bindings')[0]!.body).toEqual({ bindings: [] });
  });

  it('refuses a duplicate type and region, and a binding with neither', async () => {
    standardApi({ bindings: [existing] });
    renderAt('/admin/workflows');
    await screen.findByRole('group', { name: 'Binding 1' });
    await userEvent.click(screen.getByRole('button', { name: 'Add binding' }));
    const added = await screen.findByRole('group', { name: 'Binding 2' });
    await userEvent.type(within(added).getByRole('textbox', { name: 'Region (optional)' }), 'emea');
    expect(await within(added).findByText('Another binding already uses this type and region.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save bindings' })).toBeDisabled();
    await userEvent.selectOptions(within(added).getByRole('combobox', { name: 'Asset type' }), '');
    await userEvent.clear(within(added).getByRole('textbox', { name: 'Region (optional)' }));
    expect(await within(added).findByText('Choose an asset type, a region, or both.')).toBeInTheDocument();
    expect(callsTo('PUT', '/api/v1/workflows/bindings')).toHaveLength(0);
  });

  it('explains the resolution order', async () => {
    standardApi();
    renderAt('/admin/workflows');
    expect(await screen.findByText(/A folder default wins first/)).toBeInTheDocument();
  });
});

describe('folder defaults', () => {
  it('sets a folder default with PATCH and clears it with clearDefaultWorkflow', async () => {
    standardApi(
      {
        workflows: [workflow(), second()],
        folders: [
          folder(),
          folder({
            id: 'f2',
            parentId: 'f1',
            path: 'campaigns.summer',
            label: 'summer',
            name: 'Summer',
            defaultWorkflowId: 'w1',
          }),
        ],
      },
      (c) => (c.method === 'PATCH' ? json(folder()) : undefined),
    );
    renderAt('/admin/workflows');
    const campaigns = await screen.findByRole('combobox', { name: 'Default workflow for Campaigns' });
    expect(campaigns).toHaveValue('');
    expect(screen.getByRole('combobox', { name: 'Default workflow for Summer' })).toHaveValue('w1');

    await userEvent.selectOptions(campaigns, 'w2');
    await waitFor(() => expect(callsTo('PATCH', '/api/v1/folders/f1')).toHaveLength(1));
    expect(callsTo('PATCH', '/api/v1/folders/f1')[0]!.body).toEqual({
      name: null,
      inheritedMetadata: null,
      defaultWorkflowId: 'w2',
      clearDefaultWorkflow: false,
    });

    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Default workflow for Summer' }), '');
    await waitFor(() => expect(callsTo('PATCH', '/api/v1/folders/f2')).toHaveLength(1));
    expect(callsTo('PATCH', '/api/v1/folders/f2')[0]!.body).toEqual({
      name: null,
      inheritedMetadata: null,
      defaultWorkflowId: null,
      clearDefaultWorkflow: true,
    });
  });

  it('shows which workflow a sub-folder inherits', async () => {
    standardApi({
      folders: [
        folder({ defaultWorkflowId: 'w1' }),
        folder({ id: 'f2', parentId: 'f1', path: 'campaigns.summer', label: 'summer', name: 'Summer' }),
      ],
    });
    renderAt('/admin/workflows');
    expect(await screen.findByText('Uses “Two-level approval” from Campaigns')).toBeInTheDocument();
  });

  it('reports a refused change', async () => {
    standardApi({ folders: [folder()] }, (c) =>
      c.method === 'PATCH' ? json({ title: 'Workflow not found.', status: 422 }, 422) : undefined,
    );
    renderAt('/admin/workflows');
    await userEvent.selectOptions(await screen.findByRole('combobox', { name: 'Default workflow for Campaigns' }), 'w1');
    expect(await screen.findByText('Could not update the folder')).toBeInTheDocument();
  });
});

describe('Hindi', () => {
  it('renders the workflows list and the designer in Hindi', async () => {
    setLanguage('hi');
    standardApi({ workflows: [workflow(), second()] });
    renderAt('/admin/workflows');
    expect(await screen.findByRole('heading', { level: 1, name: 'वर्कफ़्लो' })).toBeInTheDocument();
    const card = (await screen.findByRole('heading', { name: 'Two-level approval' })).closest('article')!;
    expect(within(card).getByRole('list', { name: 'Two-level approval का फ़्लो' })).toHaveTextContent('48 घं');
    expect(within(card).getByRole('link', { name: 'Two-level approval संपादित करें' })).toHaveAttribute(
      'href',
      '/admin/workflows/w1',
    );
    expect(screen.getByText('अपने आप प्रकाशित होता है')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'नया वर्कफ़्लो' })).toHaveAttribute('href', '/admin/workflows/new');
    cleanup();

    renderAt('/admin/workflows/w1');
    expect(await screen.findByRole('button', { name: 'वर्कफ़्लो सहेजें' })).toBeInTheDocument();
    expect(await screen.findByText('जमा किया गया')).toBeInTheDocument();
  });
});
