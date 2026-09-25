#!/usr/bin/env bash
# End-to-end smoke test against a running local stack (compose core + Dam.Api + Dam.Worker).
# Usage: tools/smoke.sh   (env: KC=http://localhost:18080 API=http://localhost:5080)
set -euo pipefail
KC="${KC:-http://localhost:18080}"; API="${API:-http://localhost:5080}"
step() { printf '\n== %s\n' "$1"; }

step "health"
curl -sf "$API/healthz" >/dev/null && curl -sf "$API/readyz" >/dev/null && echo ok

step "unauthenticated /me is 401 problem+json"
code=$(curl -s -o /tmp/smoke-401.json -w '%{http_code} %{content_type}' "$API/api/v1/me")
[ "$code" = "401 application/problem+json" ] && echo "$code" || { echo "unexpected: $code"; exit 1; }

step "keycloak token"
TOKEN=$(curl -sf -X POST "$KC/realms/dam/protocol/openid-connect/token" -d grant_type=password -d client_id=dam-web \
  -d username=admin@dam.local -d password=admin | python3 -c 'import sys,json;print(json.load(sys.stdin)["access_token"])')

step "/me"
curl -sf -H "Authorization: Bearer $TOKEN" "$API/api/v1/me" | tee /dev/stderr | grep -q '"Admin"'

step "ping -> outbox -> RabbitMQ -> worker"
marker="smoke-$(date +%s)"
curl -sf -X POST -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d "{\"message\":\"$marker\"}" "$API/api/v1/system/ping" >/dev/null
for _ in $(seq 1 20); do
  n=$(docker compose -f deploy/docker-compose.yml exec -T postgres psql -U dam -d dam -tA \
      -c "SELECT count(*) FROM audit_log WHERE action='system.ping.handled' AND after->>'Message'='$marker'")
  [ "$n" = "1" ] && { echo "handled exactly once"; exit 0; }
  sleep 1
done
echo "worker did not handle the ping"; exit 1
