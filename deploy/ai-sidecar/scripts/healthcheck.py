#!/usr/bin/env python3
"""Container healthcheck: exit 0 only when /healthz reports status == "ok" (models warm)."""

import json
import os
import sys
import urllib.request

port = os.environ.get("AI_PORT", "8080")
try:
    # nosemgrep: python.lang.security.audit.dynamic-urllib-use-detected.dynamic-urllib-use-detected -- fixed loopback URL; only the port number comes from the environment
    with urllib.request.urlopen(f"http://127.0.0.1:{port}/healthz", timeout=4) as r:
        sys.exit(0 if json.load(r).get("status") == "ok" else 1)
except Exception:
    sys.exit(1)
