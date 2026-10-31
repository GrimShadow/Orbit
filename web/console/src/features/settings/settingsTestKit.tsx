// Test-only scaffolding for the Settings area: render one section's page on its own (outside the Settings layout, which
// would not open it without the permission), so a page's own no-access states stay covered. Nothing in the app imports this.
import { ToastProvider } from '@dam/ui-kit';
import { QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import type { ReactElement } from 'react';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { App, queryClient } from '../../App';
import { AuthContext } from '../../auth/AuthProvider';
import { signedIn } from '../workflow/workflowTestKit';

export function renderPage(page: ReactElement, route: string) {
  queryClient.clear();
  return render(
    <AuthContext.Provider value={signedIn}>
      <QueryClientProvider client={queryClient}>
        <ToastProvider>
          <MemoryRouter initialEntries={[route]}>{page}</MemoryRouter>
        </ToastProvider>
      </QueryClientProvider>
    </AuthContext.Provider>,
  );
}

/** Renders the whole console at a route, with a probe that exposes the current URL (path, query and hash). */
export function renderApp(route: string) {
  queryClient.clear();
  return render(
    <AuthContext.Provider value={signedIn}>
      <MemoryRouter initialEntries={[route]}>
        <App />
        <UrlProbe />
      </MemoryRouter>
    </AuthContext.Provider>,
  );
}

function UrlProbe() {
  const l = useLocation();
  return <output data-testid="url">{`${l.pathname}${l.search}${l.hash}`}</output>;
}

export const currentUrl = () => screen.getByTestId('url').textContent;

/** Every permission that opens a Settings section, as an Admin holds them. */
export const ALL_SECTION_PERMISSIONS = [
  'integrations.manage',
  'audit.read',
  'webhooks.manage',
  'workflow.manage',
  'ai.manage',
  'glossary.manage',
  'rights.manage',
];
