import { createApiClient } from '@dam/sdk-js';
import { useMemo } from 'react';
import { useAuth } from '../auth/AuthProvider';

/** The typed API client (generated from `docs/api/openapi.json`), carrying the current access token. Vite's dev-server
 * proxy (and, in production, the same-origin ingress) makes the API reachable at the same origin the console itself is
 * served from, so no base URL is configured here. */
export function useApiClient() {
  const { accessToken } = useAuth();
  return useMemo(() => createApiClient({ getAccessToken: () => accessToken }), [accessToken]);
}
