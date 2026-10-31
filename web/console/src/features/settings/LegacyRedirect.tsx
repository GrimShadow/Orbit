import { Navigate, generatePath, useLocation, useParams } from 'react-router-dom';

/** An old admin URL sent to its place in Settings. Replaces the history entry and keeps the query string and hash, so a
 * bookmark, an emailed link or a filtered view still lands where it meant to. `to` may use the route's params (`:id`). */
export function LegacyRedirect({ to }: { to: string }) {
  const { search, hash } = useLocation();
  const params = useParams();
  return <Navigate to={{ pathname: generatePath(to, params), search, hash }} replace />;
}
