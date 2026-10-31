/** An authority starting with "/" (e.g. "/realms/acme") is resolved against the page's own origin, for deployments that put the
 * console and the identity provider behind one address. Full URLs are used as given. */
export function resolveAuthority(value: string, origin: string): string {
  return value.startsWith('/') ? `${origin}${value}` : value;
}

export const config = {
  oidcAuthority: resolveAuthority(
    import.meta.env.VITE_OIDC_AUTHORITY ?? 'http://localhost:18080/realms/dam',
    window.location.origin,
  ),
  oidcClientId: import.meta.env.VITE_OIDC_CLIENT_ID ?? 'dam-web',
  apiBase: '/api/v1',
};
