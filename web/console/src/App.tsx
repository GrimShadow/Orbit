import { ToastProvider } from '@dam/ui-kit';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { Navigate, Route, Routes } from 'react-router-dom';
import { ApiError } from './api/client';
import { TasksPage } from './features/workflow/tasks/TasksPage';
import { WorkflowDesignerPage } from './features/workflow/designer/WorkflowDesignerPage';
import { WorkflowsPage } from './features/workflow/designer/WorkflowsPage';
import { AuditPage } from './features/audit/AuditPage';
import { NotificationsPage } from './features/notifications/NotificationsPage';
import { WebhooksPage } from './features/webhooks/WebhooksPage';
import { IntegrationsPage } from './features/integrations/IntegrationsPage';
import { RightsPage } from './features/rights/admin/RightsPage';
import { AiSettingsPage } from './features/ai/admin/AiSettingsPage';
import { GlossaryPage } from './features/glossary/GlossaryPage';
import { UnusedAssetsPage } from './features/analytics/UnusedAssetsPage';
import { KeyResultsPage } from './features/insights/KeyResultsPage';
import { ReportLinkRedirect } from './features/insights/ReportLinkRedirect';
import { ReportsPage } from './features/insights/ReportsPage';
import { LegacyRedirect } from './features/settings/LegacyRedirect';
import { SettingsLayout } from './features/settings/SettingsLayout';
import { LEGACY_SETTINGS_PATHS } from './features/settings/settingsModel';
import { ProtectedRoute } from './auth/ProtectedRoute';
import { AssetDetailPage } from './features/asset/AssetDetailPage';
import { CollectionDetailPage } from './features/collections/CollectionDetailPage';
import { CollectionsPage } from './features/collections/CollectionsPage';
import { LibraryPage } from './features/library/LibraryPage';
import { SearchPage } from './features/search/SearchPage';
import { UploadPage } from './features/upload/UploadPage';
import { CallbackPage } from './pages/CallbackPage';
import { HomePage } from './pages/HomePage';
import { LoginPage } from './pages/LoginPage';
import { MePage } from './pages/MePage';
import { NotFoundPage } from './pages/NotFoundPage';
import { ErrorBoundary } from './shell/ErrorBoundary';
import { Shell } from './shell/Shell';

export const queryClient = new QueryClient({
  defaultOptions: { queries: { retry: (n, e) => !(e instanceof ApiError && e.status < 500) && n < 2, staleTime: 30_000 } },
});

export function App() {
  return (
    <ErrorBoundary>
      <QueryClientProvider client={queryClient}>
        <ToastProvider>
          <Routes>
            <Route element={<Shell />}>
              <Route path="/login" element={<LoginPage />} />
              <Route path="/auth/callback" element={<CallbackPage />} />
              <Route element={<ProtectedRoute />}>
                <Route index element={<HomePage />} />
                <Route path="/library" element={<LibraryPage />} />
                <Route path="/library/assets/:id" element={<AssetDetailPage />} />
                <Route path="/search" element={<SearchPage />} />
                <Route path="/collections" element={<CollectionsPage />} />
                <Route path="/collections/:id" element={<CollectionDetailPage />} />
                <Route path="/insights" element={<Navigate to="/insights/kpis" replace />} />
                <Route path="/insights/kpis" element={<KeyResultsPage />} />
                <Route path="/insights/unused" element={<UnusedAssetsPage />} />
                <Route path="/insights/reports" element={<ReportsPage />} />
                <Route path="/insights/reports/:name" element={<ReportLinkRedirect />} />
                <Route path="/tasks" element={<TasksPage />} />
                <Route path="/notifications" element={<NotificationsPage />} />
                <Route path="/settings" element={<SettingsLayout />}>
                  <Route index element={null} />
                  <Route path="integrations" element={<IntegrationsPage />} />
                  <Route path="audit" element={<AuditPage />} />
                  <Route path="webhooks" element={<WebhooksPage />} />
                  <Route path="workflows" element={<WorkflowsPage />} />
                  <Route path="workflows/:id" element={<WorkflowDesignerPage />} />
                  <Route path="ai" element={<AiSettingsPage />} />
                  <Route path="glossary" element={<GlossaryPage />} />
                  <Route path="rights" element={<RightsPage />} />
                  <Route path="*" element={<NotFoundPage />} />
                </Route>
                {LEGACY_SETTINGS_PATHS.map(({ from, to }) => (
                  <Route key={from} path={from} element={<LegacyRedirect to={to} />} />
                ))}
                <Route path="/upload" element={<UploadPage />} />
                <Route path="/me" element={<MePage />} />
              </Route>
              <Route path="*" element={<NotFoundPage />} />
            </Route>
          </Routes>
        </ToastProvider>
      </QueryClientProvider>
    </ErrorBoundary>
  );
}
