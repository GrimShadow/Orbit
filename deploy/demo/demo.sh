#!/usr/bin/env bash
# Share a demo of Orbit from this Mac. See deploy/demo/README.md.
#
#   deploy/demo/demo.sh up [--local] [--users "anna.reviewer,ben.reviewer:Editor"] [--reset-passwords] [--reseed] [--skip-build]
#   deploy/demo/demo.sh down [--purge]
#   deploy/demo/demo.sh status
#
# Everything it starts (Caddy, the demo API, cloudflared, caffeinate) runs in the background with PID files in $DEMO_STATE_DIR.
# It never touches the dev stack's own processes or the `dam` realm.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
HERE="$ROOT/deploy/demo"
STATE="${DEMO_STATE_DIR:-${TMPDIR:-/tmp}/orbit-demo}"; STATE="${STATE%/}"
PORT="${DEMO_PORT:-8088}"                 # Caddy, the one local port the tunnel (or the browser, in --local mode) talks to
API_PORT="${DEMO_API_PORT:-6080}"         # the demo API instance
REALM="${DEMO_REALM:-dam-demo}"
KC_PORT="${DAM_PORT_KC:-18080}"; S3_PORT="${DAM_PORT_S3:-19000}"; DELIVERY_PORT="${DEMO_DELIVERY_PORT:-5290}"
export PYTHONDONTWRITEBYTECODE=1
export DEMO_TENANT_ID="${DEMO_TENANT_ID:-0197d000-0000-7000-8000-00000000d3a0}"
export DEMO_REALM="$REALM" DEMO_KC_URL="http://localhost:$KC_PORT"
API_DIR="$ROOT/src/Dam.Api/bin/Release/net9.0"

say()  { printf '==> %s\n' "$*"; }
warn() { printf 'warning: %s\n' "$*" >&2; }
die()  { printf 'error: %s\n' "$*" >&2; exit 1; }

# ---- process helpers -------------------------------------------------------------------------------------------------
pidfile() { echo "$STATE/$1.pid"; }
pid_of()  { local f; f="$(pidfile "$1")"; [[ -f "$f" ]] && cat "$f" || true; }
alive()   { local p; p="$(pid_of "$1")"; [[ -n "$p" ]] && kill -0 "$p" 2>/dev/null; }
# Starts a detached background process and remembers its PID. Output goes to $STATE/logs/<name>.log.
start_bg() { local name="$1"; shift; nohup "$@" >"$STATE/logs/$name.log" 2>&1 </dev/null & echo $! >"$(pidfile "$name")"; disown 2>/dev/null || true; }
stop_proc() {
  local name="$1" p; p="$(pid_of "$name")"
  if [[ -n "$p" ]] && kill -0 "$p" 2>/dev/null; then
    kill "$p" 2>/dev/null || true
    for _ in 1 2 3 4 5 6 7 8 9 10; do kill -0 "$p" 2>/dev/null || break; sleep 0.5; done
    kill -0 "$p" 2>/dev/null && kill -9 "$p" 2>/dev/null || true
  fi
  rm -f "$(pidfile "$name")"
}
stop_all() { for n in tunnel caffeinate caddy api; do stop_proc "$n"; done; }
port_busy() { lsof -nP -iTCP:"$1" -sTCP:LISTEN >/dev/null 2>&1; }
wait_http() { # url, seconds, label
  local url="$1" secs="$2" label="$3" i
  for ((i = 0; i < secs; i++)); do curl -fsS -o /dev/null --max-time 3 "$url" 2>/dev/null && return 0; sleep 1; done
  die "$label did not come up at $url within ${secs}s (see $STATE/logs)"
}
# A browser-like User-Agent: some edge networks refuse the default curl one.
UA="Mozilla/5.0 (Macintosh) orbit-demo-check"
code_of() { curl -s -A "$UA" -o /dev/null -w '%{http_code}' --max-time 15 "$@" 2>/dev/null || true; }

# ---- up --------------------------------------------------------------------------------------------------------------
preflight() {
  say "Checking prerequisites"
  local missing=() t
  for t in caddy dotnet pnpm python3 curl lsof caffeinate; do command -v "$t" >/dev/null || missing+=("$t"); done
  [[ "$LOCAL" == 1 ]] || command -v cloudflared >/dev/null || missing+=(cloudflared)
  ((${#missing[@]} == 0)) || die "missing tools: ${missing[*]}"

  local ok=1 svc
  check() { # label url
    local c; c="$(code_of "$2")"
    [[ "$c" =~ ^[2-4][0-9][0-9]$ ]] || { warn "$1 is not reachable at $2"; ok=0; }
  }
  check "Keycloak" "http://localhost:$KC_PORT/realms/master"
  check "Object storage (S3)" "http://localhost:$S3_PORT/"
  check "Delivery origin" "http://localhost:$DELIVERY_PORT/healthz"
  check "Worker (ingest pipeline)" "http://localhost:5081/healthz"
  for svc in "Postgres:15432" "RabbitMQ:15673" "OpenSearch:19200"; do
    nc -z localhost "${svc#*:}" 2>/dev/null || { warn "${svc%%:*} is not reachable on port ${svc#*:}"; ok=0; }
  done
  [[ "$(code_of "http://localhost:5082/healthz")" == 200 ]] || warn "the scheduler is not running: usage rollups and scheduled jobs will not run (the dashboard still works)"
  [[ "$ok" == 1 ]] || die "the dev stack is not fully up (make up-search up-scan; and run the worker and the delivery origin: make run-worker, make run-delivery). Nothing was changed."

  for p in "$PORT" "$API_PORT"; do port_busy "$p" && die "port $p is already in use (set DEMO_PORT / DEMO_API_PORT, or run: deploy/demo/demo.sh down)"; done
  return 0
}

build_console() {
  say "Building the console (static files, same-origin sign-in at /realms/$REALM)"
  [[ -d "$ROOT/node_modules" ]] || (cd "$ROOT" && pnpm install --frozen-lockfile >"$STATE/logs/pnpm-install.log" 2>&1) || die "pnpm install failed (see $STATE/logs/pnpm-install.log)"
  (cd "$ROOT" && VITE_OIDC_AUTHORITY="/realms/$REALM" pnpm --filter @dam/console exec vite build --outDir "$STATE/console" --emptyOutDir) \
    >"$STATE/logs/console-build.log" 2>&1 || { tail -20 "$STATE/logs/console-build.log" >&2; die "console build failed"; }
}

build_api() {
  say "Building the demo API (Release)"
  (cd "$ROOT" && dotnet build src/Dam.Api/Dam.Api.csproj -c Release --nologo -v quiet) >"$STATE/logs/api-build.log" 2>&1 \
    || { tail -30 "$STATE/logs/api-build.log" >&2; die "API build failed"; }
}

write_caddyfile() {
  sed -e "s#{{PORT}}#$PORT#g" -e "s#{{LOG_FILE}}#$STATE/logs/access.log#g" -e "s#{{CONSOLE_DIR}}#$STATE/console#g" \
      -e "s#{{API_PORT}}#$API_PORT#g" -e "s#{{REALM}}#$REALM#g" -e "s#{{KC_PORT}}#$KC_PORT#g" \
      -e "s#{{S3_PORT}}#$S3_PORT#g" -e "s#{{DELIVERY_PORT}}#$DELIVERY_PORT#g" "$HERE/Caddyfile.tmpl" >"$STATE/Caddyfile"
  caddy validate --config "$STATE/Caddyfile" --adapter caddyfile >/dev/null 2>&1 || die "generated Caddyfile is invalid ($STATE/Caddyfile)"
}

start_tunnel() {
  : >"$STATE/cloudflared.yml"     # an empty config: a leftover ~/.cloudflared/config.yml would stop a quick tunnel from starting
  say "Starting the Cloudflare quick tunnel"
  start_bg tunnel cloudflared tunnel --config "$STATE/cloudflared.yml" --no-autoupdate --url "http://127.0.0.1:$PORT" \
    --protocol "${DEMO_TUNNEL_PROTOCOL:-auto}" --loglevel info
  local i url
  for ((i = 0; i < 60; i++)); do
    alive tunnel || { tail -15 "$STATE/logs/tunnel.log" >&2; die "cloudflared stopped before it got an address"; }
    url="$(grep -Eo 'https://[a-z0-9-]+\.trycloudflare\.com' "$STATE/logs/tunnel.log" 2>/dev/null | grep -v '^https://api\.' | head -1 || true)"
    [[ -n "$url" ]] && { PUBLIC="$url"; return 0; }
    sleep 1
  done
  die "cloudflared did not report a trycloudflare.com address within 60s (see $STATE/logs/tunnel.log)"
}

start_api() {
  say "Starting the demo API on port $API_PORT"
  # The dev stack's appsettings.Development.json (next to the binary) supplies database, storage, search and signing-key settings, so the
  # same worker, scheduler and delivery origin serve this tenant. Everything that makes this instance "the demo" is set here.
  (
    cd "$API_DIR"
    export ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="http://127.0.0.1:$API_PORT"
    export DAM_OIDC_AUTHORITY="$PUBLIC/realms/$REALM" DAM_OIDC_AUDIENCE="${DEMO_OIDC_AUDIENCE:-dam-api}"
    export DAM_OIDC_METADATA_ADDRESS="http://localhost:$KC_PORT/realms/$REALM/.well-known/openid-configuration"
    export DAM_OIDC_REQUIRE_HTTPS="$([[ "$LOCAL" == 1 ]] && echo false || echo true)"
    export DAM_S3_PUBLIC_ENDPOINT="$PUBLIC" DAM_DELIVERY_PUBLIC_BASE_URL="$PUBLIC" DAM_CONSOLE_PUBLIC_URL="$PUBLIC"
    export DAM_BOOTSTRAP_TENANT_ID="$DEMO_TENANT_ID" DAM_BOOTSTRAP_TENANT_NAME="Acme Demo" DAM_BOOTSTRAP_TENANT_SLUG="acme-demo"
    export DAM_BOOTSTRAP_TEMPLATES="core,automotive-sample"
    export Logging__LogLevel__Default=Warning
    export DAM_WEBHOOK_ALLOW_PRIVATE=false      # the dev default lets a webhook call any address on this Mac's network; a shared demo must not
    start_bg api dotnet Dam.Api.dll
  )
  wait_http "http://127.0.0.1:$API_PORT/healthz" 90 "The demo API"
}

verify_public() {
  say "Checking $PUBLIC from the outside in"
  local fail=0 c
  expect() { # label, expected code, curl args...
    local label="$1" want="$2"; shift 2
    c="$(code_of "$@")"
    if [[ "$c" == "$want" ]]; then printf '    ok    %-52s %s\n' "$label" "$c"; else printf '    FAIL  %-52s %s (wanted %s)\n' "$label" "$c" "$want"; fail=1; fi
  }
  local i
  for ((i = 0; i < 90; i++)); do [[ "$(code_of "$PUBLIC/")" == 200 ]] && break; sleep 1; done   # a new tunnel address takes a few seconds to resolve
  expect "console loads" 200 "$PUBLIC/"
  expect "console route (client-side)" 200 "$PUBLIC/library"
  local issuer
  issuer="$(curl -fsS -A "$UA" --max-time 15 "$PUBLIC/realms/$REALM/.well-known/openid-configuration" | python3 -c 'import sys,json;print(json.load(sys.stdin)["issuer"])' 2>/dev/null || true)"
  if [[ "$issuer" == "$PUBLIC/realms/$REALM" ]]; then printf '    ok    %-52s %s\n' "discovery issuer is the public address" "$issuer"; else printf '    FAIL  %-52s %s\n' "discovery issuer" "${issuer:-none}"; fail=1; fi
  expect "API refuses a request without a token" 401 "$PUBLIC/api/v1/me"
  expect "keycloak admin console" 404 "$PUBLIC/admin/master/console/"
  expect "keycloak admin REST" 404 "$PUBLIC/admin/realms/master"
  expect "keycloak master realm" 404 "$PUBLIC/realms/master/.well-known/openid-configuration"
  expect "developer realm (dam)" 404 "$PUBLIC/realms/dam/.well-known/openid-configuration"
  expect "demo realm account console" 404 "$PUBLIC/realms/$REALM/account/"
  expect "demo realm client registration" 404 "$PUBLIC/realms/$REALM/clients-registrations/openid-connect"
  expect "health, metrics, openapi, swagger" 404 "$PUBLIC/healthz"
  expect "  /readyz" 404 "$PUBLIC/readyz"
  expect "  /metrics" 404 "$PUBLIC/metrics"
  expect "  /openapi/v1.json" 404 "$PUBLIC/openapi/v1.json"
  expect "  /swagger" 404 "$PUBLIC/swagger"
  expect "storage without a signature" 404 "$PUBLIC/dam-originals/"
  expect "storage bucket listing" 404 "$PUBLIC/dam-quarantine"
  expect "a file that does not exist" 404 "$PUBLIC/wp-login.php"
  expect "unknown API path" 404 "$PUBLIC/api/healthz"
  expect "delivery with a bad token" 403 "$PUBLIC/d/bad/00000000-0000-0000-0000-000000000000/thumb"
  [[ "$fail" == 0 ]] || return 1
}

cmd_up() {
  LOCAL=0; USERS="${DEMO_USERS:-}"; RESET=0; RESEED=0; SKIP_BUILD=0
  while (($#)); do
    case "$1" in
      --local) LOCAL=1 ;;
      --users) USERS="${2:?--users needs a value}"; shift ;;
      --users=*) USERS="${1#*=}" ;;
      --reset-passwords) RESET=1 ;;
      --reseed) RESEED=1 ;;
      --skip-build) SKIP_BUILD=1 ;;
      *) die "unknown option for up: $1" ;;
    esac
    shift
  done
  [[ -n "$USERS" ]] || die 'no users: pass --users "anna.reviewer,ben.reviewer" (name or name:Role, Role = Admin|Librarian|Editor|Approver|Contributor|Viewer; default Admin)'

  mkdir -p "$STATE/logs"
  if alive api || alive caddy || alive tunnel; then die "the demo is already running (deploy/demo/demo.sh status). Stop it first: deploy/demo/demo.sh down"; fi
  preflight

  DONE_OK=0
  trap '[[ "$DONE_OK" == 1 ]] || { echo; warn "start-up did not finish: stopping what was started (the dev stack is untouched)"; stop_all; python3 "$HERE/kc.py" lockdown >/dev/null 2>&1 || true; }' EXIT
  trap 'exit 130' INT TERM

  if [[ "$SKIP_BUILD" == 1 && -d "$STATE/console" && -d "$API_DIR" ]]; then say "Skipping builds (--skip-build)"; else build_console; build_api; fi
  [[ -d "$API_DIR" ]] || die "no API build at $API_DIR"
  write_caddyfile

  say "Starting Caddy on 127.0.0.1:$PORT"
  start_bg caddy caddy run --config "$STATE/Caddyfile" --adapter caddyfile
  wait_http "http://127.0.0.1:$PORT/" 20 "Caddy"

  if [[ "$LOCAL" == 1 ]]; then
    PUBLIC="http://demo.localhost:$PORT"
    say "Local mode: no tunnel. Open $PUBLIC in Chrome"
  else
    start_tunnel
  fi
  echo "$PUBLIC" >"$STATE/public_url"
  echo "$([[ "$LOCAL" == 1 ]] && echo local || echo tunnel)" >"$STATE/mode"

  say "Creating or updating the '$REALM' sign-in realm for $PUBLIC"
  python3 "$HERE/kc.py" realm --public "$PUBLIC"
  say "Preparing demo users"
  local creds user_args=(--specs "$USERS")
  if [[ "$RESET" == 1 ]]; then user_args+=(--reset-passwords); fi
  creds="$(python3 "$HERE/kc.py" users "${user_args[@]}")" || die "could not create the demo users"

  if [[ "$RESEED" == 1 ]]; then
    say "Reseed: removing the demo tenant's content first"
    python3 "$HERE/purge.py" --keep-tenant || die "could not purge the demo tenant (is Docker up?)"
  fi
  start_api

  say "Setting up the demo tenant and its sample library (this takes a minute or two the first time)"
  local seed_args=(--api-local "http://127.0.0.1:$PORT" --public "$PUBLIC")
  if [[ "$LOCAL" != 1 ]]; then seed_args+=(--check-public); fi
  python3 "$HERE/seed.py" "${seed_args[@]}" \
    || die "seeding failed (see the messages above; $STATE/logs/api.log has the API's side)"

  verify_public || die "some checks failed (see above)"

  # Keep the Mac awake while the API runs.
  start_bg caffeinate caffeinate -i -w "$(pid_of api)"
  DONE_OK=1

  echo
  echo "================================================================================"
  echo " Orbit demo is up"
  echo "   Address : $PUBLIC"
  echo
  printf '   %-24s %-22s %s\n' "Username" "Password" "Role"
  local u p r kept=0
  while IFS=$'\t' read -r u p r; do
    [[ -n "$u" ]] || continue
    if [[ "$p" == "-" ]]; then p="(kept)"; kept=1; fi
    printf '   %-24s %-22s %s\n' "$u" "$p" "$r"
  done <<<"$creds"
  [[ "$kept" == 1 ]] && echo "   (kept) = this person already had an account; their password is not shown. Run again with RESET_PASSWORDS=1 for a new one."
  echo
  echo " These passwords are shown only now and are stored nowhere."
  echo " Stop sharing:  make demo-down      Status:  make demo-status"
  echo "================================================================================"
}

# ---- down / status ---------------------------------------------------------------------------------------------------
cmd_down() {
  local purge=0
  while (($#)); do case "$1" in --purge) purge=1 ;; *) die "unknown option for down: $1" ;; esac; shift; done
  mkdir -p "$STATE/logs"
  say "Stopping the tunnel, Caddy, the demo API and caffeinate"
  stop_all
  if curl -fsS -o /dev/null --max-time 3 "http://localhost:$KC_PORT/realms/master" 2>/dev/null; then
    if python3 "$HERE/kc.py" disable 2>/dev/null; then :; else warn "could not reach the demo realm (nothing to disable?)"; fi
    if [[ "$purge" == 1 ]]; then
      say "Purging: demo users, the demo tenant's data, its files and search entries"
      python3 "$HERE/kc.py" purge || warn "could not delete the demo users"
      python3 "$HERE/purge.py" || warn "could not purge the tenant's data (is Docker up?)"
    fi
  else
    warn "Keycloak is not reachable: demo users were not disabled"
  fi
  rm -rf "$STATE/console" "$STATE/public_url" "$STATE/mode" "$STATE/Caddyfile" "$STATE/cloudflared.yml"
  say "Done. The dev stack was not touched."
}

cmd_status() {
  local n any=0
  for n in caddy api tunnel caffeinate; do
    if alive "$n"; then printf '  %-11s running (pid %s)\n' "$n" "$(pid_of "$n")"; any=1; else printf '  %-11s stopped\n' "$n"; fi
  done
  if [[ "$any" == 0 ]]; then echo "The demo is not running."; return 0; fi
  local url mode; url="$(cat "$STATE/public_url" 2>/dev/null || true)"; mode="$(cat "$STATE/mode" 2>/dev/null || true)"
  echo "  mode        ${mode:-?}"; echo "  address     ${url:-?}"
  [[ -n "$url" ]] && printf '  reachable   %s\n' "$([[ "$(code_of "$url/")" == 200 ]] && echo yes || echo NO)"
  printf '  sign-in     %s\n' "$([[ "$(code_of "$url/realms/$REALM/.well-known/openid-configuration")" == 200 ]] && echo ok || echo NOT OK)"
  local enabled
  enabled="$(DEMO_DIR="$HERE" python3 -c 'import sys,os;sys.path.insert(0,os.environ["DEMO_DIR"]);import kc;k=kc.Keycloak();print(", ".join(u["username"] for u in k.all_users() if u.get("enabled")) or "(none)")' 2>/dev/null || echo '?')"
  echo "  users on    $enabled"
  echo "  logs        $STATE/logs"
}

case "${1:-}" in
  up) shift; cmd_up "$@" ;;
  down) shift; cmd_down "$@" ;;
  status) shift; cmd_status "$@" ;;
  *) sed -n '2,10p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 2 ;;
esac
