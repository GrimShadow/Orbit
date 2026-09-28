import { ToastProvider } from '@dam/ui-kit';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { Route, Routes } from 'react-router-dom';
import { ApiError } from './api/client';
import { ProtectedRoute } from './auth/ProtectedRoute';
import { AssetDetailPage } from './features/asset/AssetDetailPage';
import { LibraryPage } from './features/library/LibraryPage';
import { UploadPage } from './features/upload/UploadPage';
import { CallbackPage, HomePage, LoginPage, MePage, NotFoundPage } from './pages/Pages';
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
