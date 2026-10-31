# Share an Orbit demo from your MacBook

Colleagues you invite open one `https://<random>.trycloudflare.com` address, sign in with a personal username and password, and use
the full Orbit console on your Mac: browse, upload, preview, download, search. The address is temporary and different every time you
start. It works only while your Mac is awake, online and running this.

## What it does

`make demo-up` starts, from this folder and in the background:

| Piece | What it is |
|---|---|
| **Caddy** (port 8088, this Mac only) | The one front door. Serves the console and passes the rest to the right place. Everything it does not list answers 404. |
| **A second API** (port 6080) | A separate Orbit API for the demo, so your own dev API and tokens are untouched. It uses the dev stack's database, storage, search and queue, and the dev worker and delivery origin do the background work. |
| **A separate sign-in realm** `dam-demo` | Created in your local Keycloak. Your `dam` realm, its users and `admin@dam.local` are not reachable from the demo address, and demo users cannot sign in to your dev console. |
| **A separate tenant** "Acme Demo" | About 30 fictional assets (cars, banners, a manual PDF, a short video and audio clip), folders, tags, collections, a rights record, a review workflow with three tasks waiting, glossary terms, comments and eight weeks of usage so the dashboard has charts. Nothing of your dev tenant is shown. |
| **cloudflared** | The Cloudflare "quick tunnel" that gives Caddy a public https address. |
| **caffeinate** | Keeps the Mac from sleeping while the demo runs. |

## Prerequisites

Already installed: `caddy`, `cloudflared`, `dotnet`, `pnpm`, `python3`. The dev stack must be up as usual:
`make up-search up-scan` (and `make up-mail` if you like), plus the **worker** and the **delivery origin** running (`make run-worker`,
`make run-delivery`; the scheduler is optional). Docker must be running. `make demo-up` checks all of this first and stops with a clear
message, having changed nothing, if something is missing. Run `pnpm install` once in the repo if you have not.

## Commands

```bash
make demo-up USERS="anna.reviewer,ben.reviewer"          # start sharing; prints the address and one password per person
make demo-up USERS="anna.reviewer,ben.viewer:Viewer"     # name:Role, Role is Admin (default), Librarian, Editor, Approver, Contributor or Viewer
make demo-up USERS="anna.reviewer" RESET_PASSWORDS=1     # a new password for people who already exist
make demo-up USERS="..." RESEED=1                        # throw the demo library away and build it again
make demo-up USERS="..." LOCAL=1                         # no tunnel: http://demo.localhost:8088 on this Mac only (for trying it out)
make demo-status                                         # what is running, the address, who may sign in
make demo-down                                           # stop everything and switch demo users off
make demo-down PURGE=1                                   # also delete demo users, the demo tenant's data, its files and search index
```

Without `make`: `deploy/demo/demo.sh up --users "..." [--local] [--reset-passwords] [--reseed] [--skip-build]`, `down [--purge]`, `status`.
Settings (all optional): `DEMO_PORT` (8088), `DEMO_API_PORT` (6080), `DEMO_STATE_DIR` (default `$TMPDIR/orbit-demo`; logs and process ids),
`DEMO_TUNNEL_PROTOCOL` (`auto`; try `http2` on a network that blocks QUIC).

A first start takes about a minute (builds, then the sample library). Later starts reuse the library. `up` returns when it is ready; the
programs keep running in the background until `make demo-down`.

## What colleagues receive

You send each person the address, their username and their password, ideally over different channels. Each person signs in once with
that; there is nothing to install and no forced password change. Passwords are 20 random characters without look-alike letters
(no 0/O, 1/l/I), made fresh with the operating system's secure random generator. They are printed **once**, at the end of `demo-up`, and are not
written to any file or log. If you lose them, run again with `RESET_PASSWORDS=1`. Roles: Admin by default (can do and see everything in
the demo tenant); choose Editor or Viewer for people who should not change much.

The address changes every time the tunnel starts, so send the new address each session. Existing users keep their passwords across
sessions unless you reset them; `demo-down` switches all of them off and `demo-up` only switches on the names you list.

## What is and is not exposed

Reachable from the internet, through the one address:

- `/` the console (static files, built fresh into a temp folder).
- `/api/v1/*` the demo API. Every call needs a valid demo-realm token; without one it is 401.
- Sign-in, only for the demo realm and only the pages the browser needs: `/realms/dam-demo/` discovery, authorize, token, logout, the login form's submit and restart
  actions, and the login theme's static files under `/resources/`.
- `/dam-originals/`, `/dam-renditions/`, `/dam-quarantine/` object storage, but only requests that carry a signature (`X-Amz-Signature`)
  made by the demo API for one file, one action and about 15 minutes. These are the browser's direct uploads and downloads. Without a
  signature: 404.
- `/d/<signed token>/...` previews and downloads from the delivery origin. The token is the permission and expires.

Not reachable (404, checked every start): Keycloak's admin console and admin API (`/admin/...` except the console's own pages such as
`/admin/rights`), the `master` and `dam` realms, the demo realm's account console and client registration, `/healthz`, `/readyz`,
`/metrics`, `/openapi`, `/swagger`, and anything else not listed above. Your dev API (5080), worker, scheduler, RabbitMQ,
Postgres, OpenSearch, mailpit, the S3 console port and the Vite dev server are not routed at all.

## How the pieces fit (and why it is built this way)

- **One address, same origin.** The console, API, sign-in, storage and delivery all share one host, so the browser needs no CORS rules and
  a single secure (https) address. Caddy does path routing, and it passes the `Host` header through untouched because the presigned
  storage URLs are signed for that exact host and path.
- **The console is built once, with a relative sign-in address** (`VITE_OIDC_AUTHORITY=/realms/dam-demo`, which the console resolves
  against the page's own address; a four-line change in `web/console/src/config.ts`). So the unknown-in-advance tunnel address
  never needs a rebuild.
- **Sign-in realm.** Built from `deploy/keycloak/realm-dam.json` (same roles and claim mappers, the `tenant` claim set to the demo
  tenant), without its users, groups or the `pads4-plugin` client (it has a known secret). Its **frontend URL** is set to the public address,
  so the issuer and every endpoint Keycloak advertises are the public ones even though Keycloak itself is reached on localhost. No change to the running Keycloak container is needed.
  Password grants are off for every client (including Keycloak's own `admin-cli`), self-registration and password reset are off, brute-force
  protection is on (5 failures lock the account for a minute, growing), sessions last 30 idle minutes up to 8 hours.
  Re-running on a new address updates the realm and the web client's redirect addresses; it does not duplicate anything.
- **The demo API validates tokens against the public address** (`DAM_OIDC_AUTHORITY`) but reads Keycloak's discovery document and keys from
  your local Keycloak, not through the tunnel (new setting `DAM_OIDC_METADATA_ADDRESS`, unset by default so nothing changes elsewhere;
  plain HTTP is only accepted for localhost, and an https authority is required whenever `DAM_OIDC_REQUIRE_HTTPS` is not `false`).
- **Seeding** goes through the real API with a temporary user. Password grants on the web client are opened only for the moment it takes to fetch
  a token, then closed; the temporary user is deleted when seeding ends, even if it fails.
  Everything is looked up before it is created, so seeding is repeatable. Content is fictional (Acme Motors, Roadster, Trail, Sedan, Lumen)
  and generated by `deploy/demo/media.py` with only the Python standard library (plus `ffmpeg` for the short video and audio clip).
- **Template pack.** The demo tenant uses the shipped `core` and `automotive-sample` packs, so the library shows real folders and vocabularies.
  The repo has no knowledge-document module yet, so there is no VIN knowledge document; the owner manual PDF carries a VIN chapter instead.

Files: `demo.sh` (start, stop, status), `kc.py` (realm and users), `seed.py` (library), `media.py` (sample files), `purge.py` (removal),
`Caddyfile.tmpl` (the front door; the generated copy lives in the temp folder, not the repo).

## Stopping

`make demo-down` stops the tunnel, Caddy, the demo API and caffeinate, signs everybody out and switches all demo users off. Do this when
the session is over. Closing the terminal does not stop it. If the Mac restarts, run `make demo-up` again. Your dev stack is never stopped,
restarted or changed by these commands.

## Troubleshooting

- **"the dev stack is not fully up"**: it lists what it could not reach. Start it as usual, including the worker and delivery origin.
- **"port 8088 / 6080 is already in use"**: `make demo-down`, or set `DEMO_PORT` / `DEMO_API_PORT`.
- **Colleague sees an error page right after you start**: a brand-new address takes up to a minute to resolve; the script waits for it
  before printing, but a colleague's network (or your Mac, if it cached a failed lookup) can take longer. Wait a minute and reload.
- **Uploads stay at "processing"**: the dev worker is not running or has stopped (`make run-worker`).
- **No thumbnails or previews**: the delivery origin (port 5290) is not running (`make run-delivery`).
- **The tunnel dropped** (`make demo-status` shows `tunnel stopped`, or the address stops answering): `make demo-down`, then `make demo-up` again. You get a new address.
- **Logs**: `$TMPDIR/orbit-demo/logs/` (`api.log`, `caddy`'s `access.log` with query strings and delivery tokens removed, `tunnel.log`).
- **A failed start leaves things running**: `make demo-down` always cleans up; `python3 deploy/demo/kc.py lockdown` closes the temporary sign-in window if a seeding run was killed.

## Security notes

- The tunnel address is hard to guess but **not secret-protected**: anyone who has it can see the sign-in page. The app sign-in is the gate.
  Use the strong per-run passwords, send them privately, and **stop the demo (`make demo-down`) when you are done**.
- Only the demo realm and tenant are exposed. Keycloak's admin console, the master realm and your dev realm are blocked at the front door,
  and the demo API only trusts tokens from the demo realm.
- The demo API runs with webhook calls to private network addresses switched **off** (the dev default allows them), so an Admin cannot aim a webhook at
  your Mac's other services.
- What colleagues upload is processed on your Mac by the dev worker (virus scan, ffmpeg, document conversion) and stored in your dev object store. Invite
  people you trust, and use `PURGE=1` afterwards if you want the demo files gone.
- The demo shares the dev stack's delivery signing key and AI sidecar. Admin users of the demo tenant can see the AI settings page.
- Cloudflare quick tunnels carry no uptime promise and are meant for testing. For anything longer-lived, see ADR-0041 (named tunnel with Cloudflare Access).
- Passwords are never stored. `demo-down` disables the accounts; `demo-down PURGE=1` deletes them.

## Checking it yourself

`--local` mode runs the same flow without the tunnel. With a demo running in that mode (`make demo-up USERS=... LOCAL=1`), an optional
Playwright check signs in, looks at the dashboard and library, uploads a file, searches, downloads and signs out:

```bash
DEMO_URL=http://demo.localhost:8088 DEMO_USER=anna.reviewer DEMO_PASSWORD='...' pnpm exec playwright test --config tests/e2e/playwright.config.ts --project demo
```
(add `DEV_USER=admin@dam.local DEV_PASSWORD=admin` to also prove a dev-realm user is refused). Without those variables the check skips itself.
