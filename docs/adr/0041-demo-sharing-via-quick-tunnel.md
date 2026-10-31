# ADR-0041: Sharing a demo from a laptop through a Cloudflare quick tunnel

Status: accepted. Changes none of D1-D15. Tooling for demonstrations only; nothing here is a production deployment path.

## Context

Colleagues, invited one by one, should be able to use the full console on a developer's MacBook from their own machines: sign in, browse,
upload, preview, download and search. The console is a Vite app whose dev server proxies `/api`; sign-in is Keycloak OIDC with PKCE
(`dam-web`, redirect addresses fixed to `http://localhost:5173`) and the API requires the token issuer to equal `DAM_OIDC_AUTHORITY`;
browser uploads and downloads go straight to S3 through presigned URLs that are signed for `DAM_S3_PUBLIC_ENDPOINT` (host and path);
previews are signed delivery links built from `DAM_DELIVERY_PUBLIC_BASE_URL`; emails link to `DAM_CONSOLE_PUBLIC_URL`; and browser sign-in
needs a secure context (https, or localhost). A temporary public address that is unknown until the tunnel starts has to satisfy all of this.

## Decision

1. **A Cloudflare quick tunnel (`cloudflared tunnel --url`) in front of a local Caddy.** One public https host, path routing, everything
   same-origin: `/` static console; `/api/v1/*` a separate demo API instance; the demo realm's browser sign-in paths and theme assets
   only; the three bucket paths for requests that carry an `X-Amz-Signature`; `/d/*` the existing delivery origin. Everything else is
   404, and Keycloak admin, the `master` and `dam` realms and all service endpoints are blocked explicitly. Caddy passes the `Host` header
   through and never rewrites paths, so presigned signatures stay valid.
2. **A separate demo API instance** (own port) with `DAM_OIDC_AUTHORITY`, `DAM_S3_PUBLIC_ENDPOINT`, `DAM_DELIVERY_PUBLIC_BASE_URL` and
   `DAM_CONSOLE_PUBLIC_URL` set to the public address, so the developer's own API, tokens and tenant are untouched. The existing worker,
   scheduler and delivery origin serve the demo tenant too (tenancy is by tenant id). Webhook calls to private addresses are switched off for it.
3. **A separate Keycloak realm `dam-demo`**, created through the admin REST API from `realm-dam.json` (same roles and claim mappers; the
   hardcoded `tenant` claim is the demo tenant) without users, with its `frontendUrl` attribute set to the public address. That makes the issuer
   and every advertised endpoint public while Keycloak is still reached locally, so **the running Keycloak container is not changed**.
   Password grants are off (also for the built-in `admin-cli`), registration and reset are off, brute-force protection is on.
   Re-running on a new address updates the realm and client.
4. **`DAM_OIDC_METADATA_ADDRESS` (new, optional, default unset = unchanged).** The API reads the discovery document and signing keys
   from this address (the local Keycloak), through a small handler that redirects back-channel requests for the public origin to it, while
   still validating tokens against the public issuer. HTTPS stays required for the authority unless `DAM_OIDC_REQUIRE_HTTPS=false`;
   plain HTTP is accepted for the metadata address only on loopback. Without this the API would call out through the tunnel for every key fetch.
5. **The console is built once with a relative sign-in address.** `config.ts` resolves an authority that starts with `/` against the page's
   own origin (`VITE_OIDC_AUTHORITY=/realms/dam-demo`), so the unknown public address needs no rebuild.
6. **Per-run credentials, nothing stored.** Demo users are created with passwords from the OS secure random generator (20 characters,
   no look-alikes), printed once and written nowhere. Seeding uses a temporary user and a short window with password grants on the web
   client; both are removed afterwards. The demo tenant ("Acme Demo", fixed id) is built from the `core` and `automotive-sample` packs and
   a deterministic, fictional sample library generated with the Python standard library (ADR-0003: no customer names).
7. **A `--local` mode** runs the identical flow on `http://demo.localhost:8088` (browsers treat `*.localhost` as secure) without cloudflared.
   Everything but the tunnel itself can therefore be tested without exposing anything.

## Alternatives considered

- **Tailscale (tailnet or Funnel).** Strong access control, stable names, but every colleague would need to install and join Tailscale
  (or the demo would be on Funnel, which is public like a quick tunnel but needs account setup and an https certificate flow). Too much
  friction for one-off demos to individually invited people.
- **ngrok.** Equivalent in effect to a quick tunnel; the useful features (stable domain, basic auth/OAuth in front) need an account and,
  for a stable address, a paid plan. No advantage for this use.
- **A named Cloudflare tunnel with Cloudflare Access (one-time PIN or SSO).** The right answer for anything recurring: a stable hostname
  and a second login gate in front of the app. It needs a Cloudflare account, a domain on it and Access policies, so it is not a
  zero-setup option. The design here carries over unchanged: only the `cloudflared` line and the public address differ.
- **Deploying the stack to a cloud host.** Not a laptop demo; needs images, a database and secrets management.
- **Reusing the `dam` realm and the dev API.** Would expose the developer's users (`admin@dam.local` / `admin`) and
  require changing realm redirect addresses and issuer for the running dev stack.

## Consequences

- The address is temporary and different at every start; colleagues must be sent the new one, and sessions end when the tunnel stops.
- The tunnel address is hard to guess but not protected: the application sign-in is the only gate. This is acceptable for a short, invited
  session with strong per-user passwords and brute-force protection; it is why the kit prints a reminder to stop sharing, and why a named
  tunnel with Access is the next step for anything longer.
- The developer's Mac serves the demo and processes what colleagues upload (scan, ffmpeg, document conversion): trusted people only.
- The demo tenant shares the dev stack's database, object store, search and signing key (separate tenant, separate realm, separate API).
  `demo-down --purge` removes its rows, files and index.
- Costs: Cloudflare quick tunnels have no uptime guarantee and a 100 MB request limit (uploads are sent in 16 MB parts, so this does not
  bite) and do not support server-sent events (the console does not use them).
- Two small product changes (a metadata-address setting for the API and a relative-authority rule in the console) are generally useful for any
  deployment that reaches its identity provider by a different address than browsers do.
- Not verified without a real tunnel: Cloudflare's behaviour toward this traffic (headers, DNS lead time, limits). Everything else is
  covered by `--local` runs and the opt-in Playwright check `tests/e2e/demo.spec.ts`.
