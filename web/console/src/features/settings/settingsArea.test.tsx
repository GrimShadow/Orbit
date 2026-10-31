import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { setLanguage } from '../../i18n';
import { json, mockApi } from '../workflow/workflowTestKit';
import { ALL_SECTION_PERMISSIONS, currentUrl, renderApp } from './settingsTestKit';

beforeEach(() => {
  setLanguage('en');
  window.localStorage.clear();
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const sectionNav = () => screen.findByRole('navigation', { name: 'Settings sections' });
const linkNames = (nav: HTMLElement) =>
  within(nav)
    .getAllByRole('link')
    .map((l) => l.textContent);

describe('the Settings navigation', () => {
  it('lists the sections in their groups, with icons, and marks the current one', async () => {
    mockApi(() => undefined, { permissions: ALL_SECTION_PERMISSIONS });
    renderApp('/settings/audit');
    const nav = await sectionNav();

    expect(within(nav).getByText('Settings')).toBeInTheDocument();
    const groups = within(nav).getAllByRole('group');
    expect(groups.map((g) => g.getAttribute('aria-labelledby'))).toHaveLength(4);
    expect(within(nav).getByRole('group', { name: 'Connections' })).toBeInTheDocument();
    expect(linkNames(within(nav).getByRole('group', { name: 'Connections' }))).toEqual(['Integrations']);
    expect(linkNames(within(nav).getByRole('group', { name: 'Security and compliance' }))).toEqual(['Audit log']);
    expect(linkNames(within(nav).getByRole('group', { name: 'Automation' }))).toEqual(['Webhooks', 'Workflows', 'AI']);
    expect(linkNames(within(nav).getByRole('group', { name: 'Content rules' }))).toEqual(['Glossary', 'Rights']);

    for (const link of within(nav).getAllByRole('link')) expect(link.querySelector('svg')).not.toBeNull();
    expect(within(nav).getByRole('link', { name: 'Audit log' })).toHaveAttribute('aria-current', 'page');
    expect(within(nav).getByRole('link', { name: 'Audit log' })).toHaveAttribute('href', '/settings/audit');
    expect(within(nav).getByRole('link', { name: 'Webhooks' })).not.toHaveAttribute('aria-current');
  });

  it('puts the section title above the section page: an eyebrow naming Settings and the group', async () => {
    mockApi(() => undefined, { permissions: ALL_SECTION_PERMISSIONS });
    renderApp('/settings/webhooks');
    await sectionNav();
    expect(await screen.findByRole('heading', { level: 1, name: 'Webhooks' })).toBeInTheDocument();
    expect(screen.getByText('Settings · Automation')).toBeInTheDocument();
  });

  it('keeps the current section marked when it is a page beneath a section', async () => {
    mockApi(() => undefined, { permissions: ['workflow.manage', 'rights.manage'] });
    renderApp('/settings/workflows');
    const nav = await sectionNav();
    expect(within(nav).getByRole('link', { name: 'Workflows' })).toHaveAttribute('aria-current', 'page');
  });

  it('shows only the sections whose permission is held, and only their groups', async () => {
    mockApi(() => undefined, { permissions: ['assets.read', 'rights.manage', 'audit.read'] });
    renderApp('/settings/audit');
    const nav = await sectionNav();
    expect(linkNames(nav)).toEqual(['Audit log', 'Rights']);
    expect(within(nav).queryByRole('group', { name: 'Connections' })).not.toBeInTheDocument();
    expect(within(nav).queryByRole('group', { name: 'Automation' })).not.toBeInTheDocument();
    expect(within(nav).getByRole('group', { name: 'Security and compliance' })).toBeInTheDocument();
    expect(within(nav).getByRole('group', { name: 'Content rules' })).toBeInTheDocument();
  });

  it('moves between sections without leaving the area', async () => {
    mockApi(() => undefined, { permissions: ALL_SECTION_PERMISSIONS });
    renderApp('/settings/audit');
    const nav = await sectionNav();
    await userEvent.click(within(nav).getByRole('link', { name: 'Rights' }));
    expect(await screen.findByRole('heading', { level: 1, name: 'Rights' })).toBeInTheDocument();
    expect(currentUrl()).toBe('/settings/rights');
    expect(within(await sectionNav()).getByRole('link', { name: 'Rights' })).toHaveAttribute('aria-current', 'page');
  });

  it('gives the workflow designer the whole width: no section list on /settings/workflows/:id', async () => {
    mockApi(() => undefined, { permissions: ['workflow.manage'] });
    renderApp('/settings/workflows/w1');
    await waitFor(() => expect(document.querySelector('.dam-settings--focus')).not.toBeNull());
    expect(screen.queryByRole('navigation', { name: 'Settings sections' })).not.toBeInTheDocument();
  });

  it('is in Hindi', async () => {
    setLanguage('hi');
    mockApi(() => undefined, { permissions: ALL_SECTION_PERMISSIONS });
    renderApp('/settings/audit');
    const nav = await screen.findByRole('navigation', { name: 'सेटिंग्स के अनुभाग' });
    expect(within(nav).getByRole('group', { name: 'कनेक्शन' })).toBeInTheDocument();
    expect(within(nav).getByRole('group', { name: 'सुरक्षा और अनुपालन' })).toBeInTheDocument();
    expect(within(nav).getByRole('group', { name: 'स्वचालन' })).toBeInTheDocument();
    expect(within(nav).getByRole('group', { name: 'सामग्री के नियम' })).toBeInTheDocument();
    expect(within(nav).getByText('सेटिंग्स')).toBeInTheDocument();
    expect(within(nav).getByRole('link', { name: 'ऑडिट लॉग' })).toHaveAttribute('aria-current', 'page');
  });

  it('has no axe violations', async () => {
    mockApi(() => undefined, { permissions: ALL_SECTION_PERMISSIONS });
    const { container } = renderApp('/settings/audit');
    await sectionNav();
    expect(await axe(container)).toHaveNoViolations();
  });
});

describe('opening /settings', () => {
  it('goes to the first section the person may use', async () => {
    mockApi(() => undefined, { permissions: ['rights.manage', 'ai.manage'] });
    renderApp('/settings');
    await waitFor(() => expect(currentUrl()).toBe('/settings/ai'));
    expect(await screen.findByRole('heading', { level: 1, name: 'AI' })).toBeInTheDocument();
  });

  it('goes to Integrations for an Admin, the first section listed', async () => {
    mockApi(() => undefined, { permissions: ALL_SECTION_PERMISSIONS });
    renderApp('/settings/');
    await waitFor(() => expect(currentUrl()).toBe('/settings/integrations'));
  });

  it('shows a calm "nothing to manage" state to someone with no section permission, with no section list', async () => {
    mockApi(() => undefined, { permissions: ['assets.read', 'analytics.read'] });
    renderApp('/settings');
    expect(await screen.findByText('There is nothing to manage here')).toBeInTheDocument();
    expect(screen.getByRole('heading', { level: 1, name: 'Settings' })).toBeInTheDocument();
    expect(screen.queryByRole('navigation', { name: 'Settings sections' })).not.toBeInTheDocument();
    expect(currentUrl()).toBe('/settings');
  });

  it('shows the same state at a section URL when nothing is permitted', async () => {
    mockApi(() => undefined, { permissions: ['assets.read'] });
    renderApp('/settings/audit');
    expect(await screen.findByText('There is nothing to manage here')).toBeInTheDocument();
    expect(screen.queryByRole('navigation', { name: 'Settings sections' })).not.toBeInTheDocument();
  });

  it('says so, with a retry, when who you are cannot be loaded', async () => {
    let fail = true;
    mockApi((c) => (c.pathname === '/api/v1/me' && fail ? json({ title: 'Boom', status: 500 }, 500) : undefined), {
      permissions: ALL_SECTION_PERMISSIONS,
    });
    renderApp('/settings');
    expect(await screen.findByText('We could not load settings')).toBeInTheDocument();
    fail = false;
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }));
    await waitFor(() => expect(currentUrl()).toBe('/settings/integrations'));
  });

  it('shows a not-found page, inside the area, for an unknown section', async () => {
    mockApi(() => undefined, { permissions: ALL_SECTION_PERMISSIONS });
    renderApp('/settings/nowhere');
    expect(await screen.findByRole('link', { name: 'Back to home' })).toBeInTheDocument();
    expect(await sectionNav()).toBeInTheDocument();
  });
});

describe('a section without its permission', () => {
  it('shows a forbidden state inside the area (it does not redirect) and never loads the section', async () => {
    const calls = mockApi(() => undefined, { permissions: ['rights.manage'] });
    renderApp('/settings/audit');
    expect(await screen.findByText('You do not have access to this section')).toBeInTheDocument();
    expect(screen.getByRole('heading', { level: 1, name: 'Audit log' })).toBeInTheDocument();
    expect(screen.getByText('Ask an administrator for the permission it needs.')).toBeInTheDocument();
    // The person keeps their own sections, and the URL stays as typed.
    expect(linkNames(await sectionNav())).toEqual(['Rights']);
    expect(currentUrl()).toBe('/settings/audit');
    expect(calls.some((c) => c.pathname.startsWith('/api/v1/audit'))).toBe(false);
  });

  it('covers the workflow designer too', async () => {
    const calls = mockApi(() => undefined, { permissions: ['audit.read'] });
    renderApp('/settings/workflows/w1');
    expect(await screen.findByText('You do not have access to this section')).toBeInTheDocument();
    expect(calls.some((c) => c.pathname.startsWith('/api/v1/workflows'))).toBe(false);
  });

  it('has no axe violations', async () => {
    mockApi(() => undefined, { permissions: ['rights.manage'] });
    const { container } = renderApp('/settings/audit');
    await screen.findByText('You do not have access to this section');
    expect(await axe(container)).toHaveNoViolations();
  });
});
