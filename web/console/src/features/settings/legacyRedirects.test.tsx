import { cleanup, screen, waitFor } from '@testing-library/react';
import { setLanguage } from '../../i18n';
import { mockApi } from '../workflow/workflowTestKit';
import { ALL_SECTION_PERMISSIONS, currentUrl, renderApp } from './settingsTestKit';

beforeEach(() => {
  setLanguage('en');
  window.localStorage.clear();
  mockApi(() => undefined, { permissions: ALL_SECTION_PERMISSIONS });
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('the pre-Settings URLs', () => {
  it.each([
    ['/admin/ai', '/settings/ai'],
    ['/admin/glossary', '/settings/glossary'],
    ['/admin/rights', '/settings/rights'],
    ['/admin/webhooks', '/settings/webhooks'],
    ['/admin/audit', '/settings/audit'],
    ['/admin/workflows', '/settings/workflows'],
    ['/integrations', '/settings/integrations'],
  ])('%s opens %s', async (from, to) => {
    renderApp(from);
    await waitFor(() => expect(currentUrl()).toBe(to));
    expect(screen.getByRole('main')).toBeInTheDocument();
  });

  it('keeps the query string and the hash', async () => {
    renderApp('/admin/webhooks?tab=deliveries&status=failed#d1');
    await waitFor(() => expect(currentUrl()).toBe('/settings/webhooks?tab=deliveries&status=failed#d1'));
  });

  it('keeps the filters of a saved audit-log link', async () => {
    renderApp('/admin/audit?action=asset.&q=hero');
    await waitFor(() => expect(currentUrl()).toBe('/settings/audit?action=asset.&q=hero'));
  });

  it('keeps the query string on /integrations', async () => {
    renderApp('/integrations?connect=pads4');
    await waitFor(() => expect(currentUrl()).toBe('/settings/integrations?connect=pads4'));
  });

  it('opens a workflow in the designer by its id', async () => {
    renderApp('/admin/workflows/w1');
    await waitFor(() => expect(currentUrl()).toBe('/settings/workflows/w1'));
  });

  it('keeps the id, query string and hash of a workflow link, and a new workflow', async () => {
    renderApp('/admin/workflows/0197a000-0000-7000-8000-00000000abcd?view=json#steps');
    await waitFor(() => expect(currentUrl()).toBe('/settings/workflows/0197a000-0000-7000-8000-00000000abcd?view=json#steps'));
    cleanup();
    renderApp('/admin/workflows/new');
    await waitFor(() => expect(currentUrl()).toBe('/settings/workflows/new'));
  });

  it('is still subject to the section permission once redirected', async () => {
    vi.unstubAllGlobals();
    mockApi(() => undefined, { permissions: ['rights.manage'] });
    renderApp('/admin/audit');
    await waitFor(() => expect(currentUrl()).toBe('/settings/audit'));
    expect(await screen.findByText('You do not have access to this section')).toBeInTheDocument();
  });
});
