import { LoadingRegion, Skeleton } from '@dam/ui-kit';
import { Navigate, Outlet, useLocation } from 'react-router-dom';
import { useAuth } from './AuthProvider';

export function ProtectedRoute() {
  const auth = useAuth();
  const location = useLocation();
  if (auth.status === 'loading')
    return (
      <LoadingRegion>
        <Skeleton height={24} width={240} />
      </LoadingRegion>
    );
  if (auth.status === 'unauthenticated')
    return <Navigate to="/login" replace state={{ from: location.pathname + location.search }} />;
  return <Outlet />;
}
