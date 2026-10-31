import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { setLanguage } from '../i18n';
import { mockApi } from '../features/workflow/workflowTestKit';
import { ALL_SECTION_PERMISSIONS, currentUrl, renderApp } from '../features/settings/settingsTestKit';
import { ADMIN_ITEMS, NAV_ITEMS, SETTINGS_ITEM } from './Sidebar';

beforeEach(() => {
  setLanguage('en');
  window.localStorage.clear();
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const sidebar = () => screen.findByRole('navigation', { name: 'Primary' });
const MOVED = ['Workflows', 'AI', 'Glossary', 'Rights', 'Integrations', 'Webhooks', 'Audit log'];

describe('the sidebar', () => {
  it('has one Settings entry (a gear, to /settings) in place of the seven administrative items', async () => {
    mockApi(() => undefined, { permissions: ['analytics.read', ...ALL_SECTION_PERMISSIONS] });
    renderApp('/me');
    const nav = await sidebar();
    const settings = await within(nav).findByRole('link', { name: 'Settings' });
    expect(settings).toHaveAttribute('href', '/settings');
    expect(settings.querySelector('svg')).not.toBeNull();
    for (const name of MOVED) expect(within(nav).queryByRole('link', { name })).not.toBeInTheDocument();
    expect(
      within(nav)
        .getAllByRole('link')
        .map((l) => l.textContent),
    ).toEqual(['Home', 'Library', 'Search', 'Collections', 'Tasks', 'Upload', 'Insights', 'Settings']);
  });

  it('keeps Insights where it was, behind analytics.read, and not under Settings', async () => {
    mockApi(() => undefined, { permissions: ['analytics.read'] });
    renderApp('/me');
    const nav = await sidebar();
    expect(await within(nav).findByRole('link', { name: 'Insights' })).toHaveAttribute('href', '/insights');
    // Without a section permission there is no Settings entry.
    expect(within(nav).queryByRole('link', { name: 'Settings' })).not.toBeInTheDocument();
    expect(ADMIN_ITEMS.map((i) => i.to)).toEqual(['/insights']);
    expect(NAV_ITEMS.map((i) => i.to)).not.toContain('/settings');
  });

  it.each(ALL_SECTION_PERMISSIONS)('shows Settings to someone holding only %s', async (permission) => {
    mockApi(() => undefined, { permissions: ['assets.read', permission] });
    renderApp('/me');
    expect(await within(await sidebar()).findByRole('link', { name: 'Settings' })).toBeInTheDocument();
  });

  it('shows no Settings entry without any section permission, once permissions have loaded', async () => {
    mockApi(() => undefined, { permissions: ['assets.read', 'analytics.read', 'notifications.manage'] });
    renderApp('/me');
    const nav = await sidebar();
    await within(nav).findByRole('link', { name: 'Insights' });
    expect(within(nav).queryByRole('link', { name: 'Settings' })).not.toBeInTheDocument();
  });

  it('keeps the Settings entry current on every /settings URL', async () => {
    mockApi(() => undefined, { permissions: ALL_SECTION_PERMISSIONS });
    renderApp('/settings/glossary');
    expect(await within(await sidebar()).findByRole('link', { name: 'Settings' })).toHaveAttribute('aria-current', 'page');
    cleanup();
    renderApp('/settings/workflows/w1');
    expect(await within(await sidebar()).findByRole('link', { name: 'Settings' })).toHaveAttribute('aria-current', 'page');
    cleanup();
    renderApp('/me');
    expect(await within(await sidebar()).findByRole('link', { name: 'Settings' })).not.toHaveAttribute('aria-current');
  });

  it('takes you to the first section you may use', async () => {
    mockApi(() => undefined, { permissions: ['rights.manage', 'glossary.manage'] });
    renderApp('/me');
    await userEvent.click(await within(await sidebar()).findByRole('link', { name: 'Settings' }));
    await waitFor(() => expect(currentUrl()).toBe('/settings/glossary'));
  });

  it('keeps the icon and names the entry when collapsed', async () => {
    window.localStorage.setItem('dam.sidebar', 'collapsed');
    mockApi(() => undefined, { permissions: ALL_SECTION_PERMISSIONS });
    renderApp('/me');
    const link = await within(await sidebar()).findByRole('link', { name: 'Settings' });
    expect(link).toHaveAttribute('title', 'Settings');
    expect(link).toHaveAttribute('aria-label', 'Settings');
    expect(link.querySelector('svg')).not.toBeNull();
  });

  it('names the entry in Hindi', async () => {
    setLanguage('hi');
    mockApi(() => undefined, { permissions: ALL_SECTION_PERMISSIONS });
    renderApp('/me');
    expect(
      await within(await screen.findByRole('navigation', { name: 'मुख्य' })).findByRole('link', { name: 'सेटिंग्स' }),
    ).toHaveAttribute('href', '/settings');
  });

  it('exports the item that the table of sections feeds', () => {
    expect(SETTINGS_ITEM).toMatchObject({ to: '/settings', labelKey: 'nav.settings' });
  });
});

describe('the account menu', () => {
  const open = async () => {
    await userEvent.click(await screen.findByRole('button', { name: /^Account menu/ }));
    return screen.findByRole('link', { name: 'My account' }).then((l) => l.closest('.dam-usermenu__panel') as HTMLElement);
  };

  it('has a Settings link, so the area is reachable where the sidebar is hidden, for someone holding a section permission', async () => {
    mockApi(() => undefined, { permissions: ['audit.read'] });
    renderApp('/me');
    // The menu reads permissions from the same query as the sidebar: wait for them.
    await within(await sidebar()).findByRole('link', { name: 'Settings' });
    const panel = await open();
    const link = within(panel).getByRole('link', { name: 'Settings' });
    expect(link).toHaveAttribute('href', '/settings');
    expect(link.querySelector('svg')).not.toBeNull();
    await userEvent.click(link);
    await waitFor(() => expect(currentUrl()).toBe('/settings/audit'));
    expect(document.querySelector('.dam-usermenu__panel')).toBeNull();
  });

  it('has no Settings link without a section permission', async () => {
    mockApi(() => undefined, { permissions: ['analytics.read'] });
    renderApp('/me');
    await within(await sidebar()).findByRole('link', { name: 'Insights' });
    const panel = await open();
    expect(within(panel).queryByRole('link', { name: 'Settings' })).not.toBeInTheDocument();
  });

  it('has no axe violations with the menu open', async () => {
    mockApi(() => undefined, { permissions: ALL_SECTION_PERMISSIONS });
    const { container } = renderApp('/me');
    await within(await sidebar()).findByRole('link', { name: 'Settings' });
    await open();
    expect(await axe(container)).toHaveNoViolations();
  });
});
