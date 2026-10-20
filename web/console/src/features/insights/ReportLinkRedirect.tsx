import { Navigate, useParams, useSearchParams } from 'react-router-dom';

/** The link in a scheduled report email: /insights/reports/<name>?days=N. It lands on the Reports page with that report chosen. */
export function ReportLinkRedirect() {
  const { name } = useParams();
  const [params] = useSearchParams();
  const next = new URLSearchParams();
  if (name) next.set('report', name);
  const days = params.get('days');
  if (days) next.set('days', days);
  return <Navigate to={`/insights/reports?${next.toString()}`} replace />;
}
