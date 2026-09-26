#!/usr/bin/env bash
# Walks through identity & access on a running local stack (compose core + Dam.Api) with real Keycloak tokens.
# Usage: tools/demo-identity.sh   (env: KC=http://localhost:18080 API=http://localhost:5080)
set -euo pipefail
KC="${KC:-http://localhost:18080}"; API="${API:-http://localhost:5080}"
tok() { curl -sf -X POST "$KC/realms/dam/protocol/openid-connect/token" -d grant_type=password -d client_id=dam-web \
  -d "username=$1" -d "password=$2" | python3 -c 'import sys,json;print(json.load(sys.stdin)["access_token"])'; }
api() { local m=$1 t=$2 p=$3; shift 3; curl -g -s -X "$m" -H "Authorization: Bearer $t" -H 'Content-Type: application/json' "$@" -w '\n%{http_code}' "$API$p"; }
SHOW_PY=$(cat <<'PY'
import sys, json
raw = sys.stdin.read().rsplit("\n", 1)
code = raw[1]
body = json.loads(raw[0]) if raw[0].strip() else {}
if code != "200":
    print("   -> HTTP", code, body.get("title", "") if isinstance(body, dict) else "")
    sys.exit()
print("   %-16s roles=%s groups=%s attrs=%s" % (body["displayName"], body["roles"], body["groups"], body["attributes"]))
print("   %-16s permissions=%d rules=%d inactiveRoles=%s status=%s" % ("", len(body["permissions"]), body["accessRuleCount"], body["inactiveRoles"], body["status"]))
PY
)
show() { python3 -c "$SHOW_PY"; }
first() { python3 -c 'import sys,json;print(json.loads(sys.stdin.read().rsplit("\n",1)[0])["items"][0]["id"])'; }

ADMIN=$(tok admin@dam.local admin)
echo "== everyone signs in (first request provisions them just-in-time)"
for u in admin@dam.local:admin editor@dam.local:dam approver@dam.local:dam viewer@dam.local:dam \
         dealer.north@dam.local:dam dealer.south@dam.local:dam dealer.noregion@dam.local:dam; do
  echo " ${u%%:*}"; api GET "$(tok "${u%%:*}" "${u##*:}")" /api/v1/me | show
done

echo; echo "== admin: users and IdP groups created from tokens"
api GET "$ADMIN" /api/v1/users | python3 -c 'import sys,json
b=json.loads(sys.stdin.read().rsplit("\n",1)[0])
for u in b["items"]: print("  ", u["email"].ljust(28), u["status"].ljust(8), u["roles"], u["groups"])'
api GET "$ADMIN" /api/v1/groups | python3 -c 'import sys,json
for g in json.loads(sys.stdin.read().rsplit("\n",1)[0]): print("  group", g["name"], g["source"], "members:", g["memberCount"])'

echo; echo "== non-admins cannot use the admin API"
for u in editor@dam.local approver@dam.local dealer.north@dam.local; do
  printf "  %-26s GET /users -> " "$u"; api GET "$(tok $u dam)" /api/v1/users | tail -1
done

echo; echo "== admin assigns the approver the folder brand.roadster"
APPROVER_ID=$(api GET "$ADMIN" "/api/v1/users?filter[q]=approver" | first)
# re-runnable: clear any rules a previous run left for this approver
for rid in $(api GET "$ADMIN" "/api/v1/access-rules?filter[principalId]=$APPROVER_ID" | python3 -c 'import sys,json
[print(r["id"]) for r in json.loads(sys.stdin.read().rsplit("\n",1)[0])]'); do api DELETE "$ADMIN" "/api/v1/access-rules/$rid" >/dev/null; done
api POST "$ADMIN" /api/v1/access-rules -d "{\"principalType\":\"user\",\"principalId\":\"$APPROVER_ID\",\"scopeType\":\"folder\",\"scopeValue\":\"brand.roadster\",\"permissions\":[\"assets.approve\",\"workflow.tasks.decide\"]}" | tail -1
api GET "$(tok approver@dam.local dam)" /api/v1/me | show

echo; echo "== bad rule is refused with field errors"
api POST "$ADMIN" /api/v1/access-rules -d "{\"principalType\":\"user\",\"principalId\":\"$APPROVER_ID\",\"scopeType\":\"folder\",\"scopeValue\":\"brand/roadster\",\"permissions\":[\"assets.fly\"]}" \
  | python3 -c 'import sys,json
r=sys.stdin.read().rsplit("\n",1)
print("  HTTP",r[1],json.dumps(json.loads(r[0]).get("errors")))'

echo; echo "== disable a dealer: the SAME still-valid token stops working"
SOUTH_ID=$(api GET "$ADMIN" "/api/v1/users?filter[q]=dealer.south" | first)
api PATCH "$ADMIN" "/api/v1/users/$SOUTH_ID" -d '{"status":"disabled"}' | tail -1
api GET "$(tok dealer.south@dam.local dam)" /api/v1/me | show
api PATCH "$ADMIN" "/api/v1/users/$SOUTH_ID" -d '{"status":"active"}' | tail -1
