#!/usr/bin/env python3
"""Removes the demo tenant's data from the shared dev stack (Python standard library; needs Docker for the database).

  purge.py                 delete everything of the demo tenant, including the tenant row (demo.sh down --purge)
  purge.py --keep-tenant   delete its content but keep the tenant row, so the API can set it up afresh (demo.sh up --reseed)

Only rows whose tenant_id is the DEMO tenant are touched; the script refuses to run for any other id, and in particular for the dev tenant.
Also removes the tenant's files in object storage (every version) and its search index. Users in Keycloak are handled by kc.py.
"""
from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import hmac
import json
import os
import re
import subprocess
import sys
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
TENANT_ID = os.environ.get("DEMO_TENANT_ID", "0197d000-0000-7000-8000-00000000d3a0")
DEV_TENANT_ID = "0197a000-0000-7000-8000-000000000001"
SLUG = "acme-demo"
S3_URL = f"http://localhost:{os.environ.get('DAM_PORT_S3', '19000')}"
OS_URL = f"http://localhost:{os.environ.get('DAM_PORT_OS', '19200')}"
BUCKETS = ["dam-originals", "dam-renditions", "dam-quarantine"]


def guard() -> None:
    if not re.fullmatch(r"[0-9a-f-]{36}", TENANT_ID) or TENANT_ID == DEV_TENANT_ID:
        sys.exit(f"refusing to purge tenant {TENANT_ID}")


def psql(sql: str) -> str:
    cmd = ["docker", "compose", "-f", str(REPO / "deploy" / "docker-compose.yml"), "exec", "-T", "postgres", "psql", "-U", "dam", "-d", "dam",
           "-v", "ON_ERROR_STOP=1", "-At", "-q"]
    r = subprocess.run(cmd, input=sql, capture_output=True, text=True, timeout=120)
    if r.returncode != 0:
        raise RuntimeError("database: " + (r.stderr.strip().splitlines() or ["failed"])[-1])
    return r.stdout


def purge_database(keep_tenant: bool) -> int:
    tables = psql("""
        SELECT c.table_name FROM information_schema.columns c
        JOIN pg_class k ON k.relname = c.table_name AND k.relnamespace = 'public'::regnamespace
        WHERE c.table_schema = 'public' AND c.column_name = 'tenant_id' AND k.relkind IN ('r', 'p')
          AND NOT EXISTS (SELECT 1 FROM pg_inherits i WHERE i.inhrelid = k.oid)
        ORDER BY 1;""").split()
    stmts = ["BEGIN;", "SET LOCAL session_replication_role = replica;"]         # superuser in the dev container: skips FK and audit triggers
    stmts += [f'DELETE FROM public."{t}" WHERE tenant_id = \'{TENANT_ID}\';' for t in tables]
    if not keep_tenant:
        stmts.append(f"DELETE FROM public.tenants WHERE id = '{TENANT_ID}';")
    stmts.append("COMMIT;")
    psql("\n".join(stmts))
    return len(tables)


# ---- object storage (SigV4, path style) -----------------------------------------------------------------------------------
def s3_credentials() -> tuple[str, str, str]:
    key, secret, region = os.environ.get("DAM_S3_ACCESS_KEY"), os.environ.get("DAM_S3_SECRET_KEY"), os.environ.get("DAM_S3_REGION", "us-east-1")
    if not (key and secret):
        cfg = json.loads((REPO / "src" / "Dam.Api" / "appsettings.Development.json").read_text())
        key, secret, region = cfg["DAM_S3_ACCESS_KEY"], cfg["DAM_S3_SECRET_KEY"], cfg.get("DAM_S3_REGION", region)
    return key, secret, region


def s3(method: str, path: str, query: dict[str, str]) -> bytes:
    key, secret, region = s3_credentials()
    now = dt.datetime.now(dt.timezone.utc)
    amz, day = now.strftime("%Y%m%dT%H%M%SZ"), now.strftime("%Y%m%d")
    host = urllib.parse.urlsplit(S3_URL).netloc
    canon_q = "&".join(f"{urllib.parse.quote(k, safe='')}={urllib.parse.quote(v, safe='')}" for k, v in sorted(query.items()))
    payload = hashlib.sha256(b"").hexdigest()
    canon = f"{method}\n{urllib.parse.quote(path)}\n{canon_q}\nhost:{host}\nx-amz-content-sha256:{payload}\nx-amz-date:{amz}\n\nhost;x-amz-content-sha256;x-amz-date\n{payload}"
    scope = f"{day}/{region}/s3/aws4_request"
    to_sign = f"AWS4-HMAC-SHA256\n{amz}\n{scope}\n{hashlib.sha256(canon.encode()).hexdigest()}"

    def h(k: bytes, m: str) -> bytes:
        return hmac.new(k, m.encode(), hashlib.sha256).digest()

    sk = h(h(h(h(("AWS4" + secret).encode(), day), region), "s3"), "aws4_request")
    sig = hmac.new(sk, to_sign.encode(), hashlib.sha256).hexdigest()
    auth = f"AWS4-HMAC-SHA256 Credential={key}/{scope}, SignedHeaders=host;x-amz-content-sha256;x-amz-date, Signature={sig}"
    req = urllib.request.Request(f"{S3_URL}{urllib.parse.quote(path)}" + (f"?{canon_q}" if canon_q else ""), method=method,
                                 headers={"x-amz-date": amz, "x-amz-content-sha256": payload, "Authorization": auth})
    # nosemgrep: python.lang.security.audit.dynamic-urllib-use-detected.dynamic-urllib-use-detected -- the URL is the local S3 or OpenSearch address from the environment plus a fixed path (http/https only), never user input
    with urllib.request.urlopen(req, timeout=60) as r:
        return r.read()


def purge_files() -> int:
    hexid = TENANT_ID.replace("-", "")
    removed = 0
    for bucket in BUCKETS:
        for prefix in (f"{hexid}/", f"staging/{hexid}/"):
            while True:
                root = ET.fromstring(s3("GET", f"/{bucket}", {"versions": "", "prefix": prefix, "max-keys": "500"}))
                ns = {"s": root.tag.split("}")[0].strip("{")}
                items = [(e.findtext("s:Key", namespaces=ns), e.findtext("s:VersionId", namespaces=ns))
                         for tag in ("Version", "DeleteMarker") for e in root.findall(f"s:{tag}", ns)]
                if not items:
                    break
                for k, v in items:
                    q = {"versionId": v} if v and v != "null" else {}
                    s3("DELETE", f"/{bucket}/{k}", q)
                    removed += 1
    return removed


def purge_search() -> int:
    req = urllib.request.Request(f"{OS_URL}/dam-{SLUG}-assets-*", method="DELETE")
    try:
        # nosemgrep: python.lang.security.audit.dynamic-urllib-use-detected.dynamic-urllib-use-detected -- the URL is the local S3 or OpenSearch address from the environment plus a fixed path (http/https only), never user input
        with urllib.request.urlopen(req, timeout=30) as r:
            return 1 if r.status == 200 else 0
    except urllib.error.HTTPError as e:
        if e.code == 404:
            return 0                      # no index (yet): nothing to drop
        raise RuntimeError(f"search: HTTP {e.code}") from None


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--keep-tenant", action="store_true")
    a = ap.parse_args(argv)
    guard()
    errors = 0
    for label, fn in (("database tables cleared", lambda: purge_database(a.keep_tenant)), ("files removed", purge_files), ("search index dropped", purge_search)):
        try:
            print(f"   {label}: {fn()}")
        except Exception as e:  # noqa: BLE001
            print(f"   {label}: FAILED ({e})", file=sys.stderr)
            errors += 1
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
