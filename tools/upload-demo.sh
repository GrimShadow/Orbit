#!/usr/bin/env bash
# Real resumable upload against a running local stack (compose core + Dam.Api + Dam.Worker), using only curl.
# Usage: tools/upload-demo.sh [SIZE_MB=100] [DUPLICATE=yes|no]
# Bytes go straight to object storage through presigned URLs; the API never sees them.
set -euo pipefail
SIZE_MB="${1:-100}"; DUP="${2:-yes}"
KC="${KC:-http://localhost:18080}"; API="${API:-http://localhost:5080}"
FILE="$(mktemp -t orbit-upload).mp4"; trap 'rm -f "$FILE"' EXIT
step() { printf '\n== %s\n' "$1"; }
jget() { python3 -c "import sys,json;d=json.load(sys.stdin);print($1)"; }

fetch_token() {
  TOKEN=$(curl -sf -X POST "$KC/realms/dam/protocol/openid-connect/token" -d grant_type=password -d client_id=dam-web \
    -d username=admin@dam.local -d password=admin | jget 'd["access_token"]'); TOKEN_AT=$SECONDS
}
fetch_token
# Access tokens live 15 minutes and a large upload outlives one, so refresh before it expires. (Part URLs need no token.)
call() { [ $((SECONDS - TOKEN_AT)) -gt 600 ] && fetch_token; local m=$1 p=$2; shift 2; curl -g -sS -X "$m" -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' "$@" "$API$p"; }

step "make a ${SIZE_MB} MB file (sparse, so it costs no local disk)"
mkfile -n "${SIZE_MB}m" "$FILE" 2>/dev/null || dd if=/dev/zero of="$FILE" bs=1m count=0 seek="$SIZE_MB" 2>/dev/null
BYTES=$(stat -f%z "$FILE")
printf '   %s bytes; hashing locally for the check at the end... ' "$BYTES"; WANT=$(shasum -a 256 "$FILE" | cut -d' ' -f1); echo "$WANT"

new_asset() { call POST /api/v1/assets -d "{\"type\":\"video\",\"title\":\"$1\",\"allowIncomplete\":true}" | jget 'd["id"]'; }

upload() {   # $1 asset id; $2 = number of parts to send before the simulated drop (0 = none)
  local asset=$1 drop=$2
  local start; start=$(call POST "/api/v1/assets/$asset/uploads" -d "{\"fileName\":\"demo.mp4\",\"size\":$BYTES,\"contentType\":\"video/mp4\"}")
  local id psize count
  id=$(echo "$start" | jget 'd["upload"]["id"]'); psize=$(echo "$start" | jget 'd["upload"]["partSize"]'); count=$(echo "$start" | jget 'd["upload"]["partCount"]')
  echo "   upload $id: $count parts of $((psize/1048576)) MB"
  send_parts() {   # part numbers as arguments; asks for fresh URLs in batches of 100
    local batch=() n
    flush() {
      [ ${#batch[@]} -eq 0 ] && return
      local list; list=$(IFS=,; echo "${batch[*]}")
      call POST "/api/v1/uploads/$id/parts" -d "{\"partNumbers\":[$list]}" | python3 -c "
import sys,json
for p in json.load(sys.stdin): print(p['partNumber'], p['url'])" | while read -r num url; do
        dd if="$FILE" bs=$psize skip=$((num-1)) count=1 2>/dev/null | curl -sS -f -X PUT --data-binary @- -o /dev/null "$url"
        printf '.'
      done
      batch=()
    }
    for n in "$@"; do batch+=("$n"); [ ${#batch[@]} -ge 100 ] && flush; done
    flush
  }
  local all; all=($(seq 1 "$count"))
  if [ "$drop" -gt 0 ] && [ "$drop" -lt "$count" ]; then
    printf '   sending the first %s parts, then the connection "drops" ' "$drop"; send_parts "${all[@]:0:$drop}"; echo
    local have; have=$(call GET "/api/v1/uploads/$id" | jget 'len(d["receivedParts"])')
    echo "   after the drop, storage reports $have of $count parts. Resuming with only the missing ones"
    local missing; missing=($(call GET "/api/v1/uploads/$id" | python3 -c "
import sys,json
d=json.load(sys.stdin); got={p['partNumber'] for p in d['receivedParts']}
print(' '.join(str(n) for n in range(1,d['partCount']+1) if n not in got))"))
    printf '   sending %s missing parts ' "${#missing[@]}"; send_parts "${missing[@]}"; echo
  else
    printf '   sending all parts '; send_parts "${all[@]}"; echo
  fi
  call POST "/api/v1/uploads/$id/complete" -d '{}' | jget '"   complete -> "+d["status"]'
  local status="processing" out
  while [ "$status" = "processing" ]; do sleep 1; out=$(call GET "/api/v1/uploads/$id"); status=$(echo "$out" | jget 'd["status"]'); done
  echo "$out"
}

step "upload with a dropped connection and a resume"
A1=$(new_asset "Demo video")
OUT=$(upload "$A1" $(( $(python3 -c "import math;print(math.ceil($BYTES/16777216))") / 2 )) | tee /dev/stderr | tail -1)
GOT=$(echo "$OUT" | jget 'd.get("sha256")')
[ "$(echo "$OUT" | jget 'd["status"]')" = "ready" ] && echo "   status: ready" || { echo "   NOT READY: $OUT"; exit 1; }
[ "$GOT" = "$WANT" ] && echo "   server-side SHA-256 matches the local file: $GOT" || { echo "   HASH MISMATCH: $GOT vs $WANT"; exit 1; }

if [ "$DUP" = "yes" ]; then
  step "send the identical file again to a new asset: it should be recognised as a duplicate"
  A2=$(new_asset "Same file again")
  OUT2=$(upload "$A2" 0 | tee /dev/stderr | tail -1)
  echo "   status: $(echo "$OUT2" | jget 'd["status"]'), duplicate of asset: $(echo "$OUT2" | jget 'd.get("duplicateOfAssetId")')"
  [ "$(echo "$OUT2" | jget 'd.get("duplicateOfAssetId")')" = "$A1" ] && echo "   -> correctly points at the first asset" || { echo "   DUPLICATE NOT DETECTED"; exit 1; }
  echo "   the second asset is now: $(call GET "/api/v1/assets/$A2" | jget 'd["status"]')"
fi
echo; echo "Done."
