import { cleanup, screen, within } from '@testing-library/react';
import { axe } from 'vitest-axe';
import { queryClient } from '../App';
import { setLanguage } from '../i18n';
import { json, mockApi, renderAt } from '../features/workflow/workflowTestKit';

beforeEach(() => {
  queryClient.clear();
  setLanguage('en');
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const sidebarLink = (name: string | RegExp) =>
  within(screen.getByRole('navigation', { name: 'Primary' })).findByRole('link', { name });
const tabLink = (name: string | RegExp) =>
  within(screen.getByRole('navigation', { name: 'Sections' })).findByRole('link', { name });

describe('Tasks nav badge', () => {
  it('shows nothing when no task is waiting', async () => {
    mockApi(() => undefined, { summary: { pendingForMe: 0, overdueForMe: 0 } });
    const { container } = renderAt('/me');
    expect(await sidebarLink('Tasks')).toHaveAttribute('href', '/tasks');
    expect(container.querySelector('.dam-navbadge')).toBeNull();
  });

  it('shows the pending count with an accessible name, in the sidebar and the phone tab bar', async () => {
    mockApi(() => undefined, { summary: { pendingForMe: 3, overdueForMe: 0 } });
    const { container } = renderAt('/me');
    const side = await sidebarLink('Tasks, 3 pending');
    expect(side).toHaveTextContent('3');
    expect(await tabLink('Tasks, 3 pending')).toHaveTextContent('3');
    expect(container.querySelectorAll('.dam-navbadge')).toHaveLength(2);
    expect(container.querySelector('.dam-navbadge--danger')).toBeNull();
  });

  it('turns danger-toned and names the overdue count when anything is overdue', async () => {
    mockApi(() => undefined, { summary: { pendingForMe: 3, overdueForMe: 1 } });
    const { container } = renderAt('/me');
    expect(await sidebarLink('Tasks, 3 pending, 1 overdue')).toBeInTheDocument();
    expect(await tabLink('Tasks, 3 pending, 1 overdue')).toBeInTheDocument();
    expect(container.querySelectorAll('.dam-navbadge--danger')).toHaveLength(2);
  });

  it('caps the pill at 99+ but keeps the real number in the name', async () => {
    mockApi(() => undefined, { summary: { pendingForMe: 150, overdueForMe: 0 } });
    renderAt('/me');
    expect(await sidebarLink('Tasks, 150 pending')).toHaveTextContent('99+');
  });

  it('shows nothing, and no error, when the summary cannot be loaded', async () => {
    mockApi((c) => (c.pathname === '/api/v1/tasks/summary' ? json({ title: 'Forbidden', status: 403 }, 403) : undefined));
    const { container } = renderAt('/me');
    expect(await sidebarLink('Tasks')).toBeInTheDocument();
    expect(container.querySelector('.dam-navbadge')).toBeNull();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('adds the translations waiting for my review to the count, and names them', async () => {
    mockApi((c) => (c.pathname === '/api/v1/ai/reviews/summary' ? json({ translation: 2 }) : undefined), {
      summary: { pendingForMe: 3, overdueForMe: 0 },
    });
    const { container } = renderAt('/me');
    const side = await sidebarLink('Tasks, 3 pending, 2 translations to review');
    expect(side).toHaveTextContent('5');
    expect(await tabLink('Tasks, 3 pending, 2 translations to review')).toHaveTextContent('5');
    expect(container.querySelector('.dam-navbadge--danger')).toBeNull();
  });

  it('shows translations alone, and keeps the overdue tone for approvals only', async () => {
    mockApi((c) => (c.pathname === '/api/v1/ai/reviews/summary' ? json({ translation: 1 }) : undefined), {
      summary: { pendingForMe: 0, overdueForMe: 0 },
    });
    renderAt('/me');
    expect(await sidebarLink('Tasks, 1 translation to review')).toHaveTextContent('1');
  });

  it('counts approvals and translations together towards 99+', async () => {
    mockApi((c) => (c.pathname === '/api/v1/ai/reviews/summary' ? json({ translation: 40 }) : undefined), {
      summary: { pendingForMe: 70, overdueForMe: 2 },
    });
    renderAt('/me');
    const link = await sidebarLink('Tasks, 70 pending, 2 overdue, 40 translations to review');
    expect(link).toHaveTextContent('99+');
  });

  it('ignores a translation count that cannot be loaded', async () => {
    mockApi((c) => (c.pathname === '/api/v1/ai/reviews/summary' ? json({ title: 'Nope', status: 500 }, 500) : undefined), {
      summary: { pendingForMe: 3, overdueForMe: 0 },
    });
    renderAt('/me');
    expect(await sidebarLink('Tasks, 3 pending')).toHaveTextContent('3');
  });

  it('has no axe violations with the badge showing', async () => {
    mockApi(() => undefined, { summary: { pendingForMe: 3, overdueForMe: 1 } });
    const { container } = renderAt('/me');
    await sidebarLink('Tasks, 3 pending, 1 overdue');
    expect(await axe(container)).toHaveNoViolations();
  });
});
