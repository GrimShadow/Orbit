export const config = {
  oidcAuthority: import.meta.env.VITE_OIDC_AUTHORITY ?? 'http://localhost:18080/realms/dam',
  oidcClientId: import.meta.env.VITE_OIDC_CLIENT_ID ?? 'dam-web',
  apiBase: '/api/v1',
};
