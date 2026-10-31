#!/usr/bin/env python3
"""Keycloak side of the demo kit (Python standard library only).

Library for seed.py and a small CLI for demo.sh:

  kc.py realm  --public URL            create/update the demo realm and its web client for this public address
  kc.py users  --specs a,b:Editor      create/enable demo users; prints "username<TAB>password<TAB>role" lines to STDOUT only
  kc.py lockdown                       password grants off, seeder user removed (also done by seed.py itself)
  kc.py disable                        disable every demo user and end their sessions (demo-down)
  kc.py purge                          delete every demo user (demo-down --purge)

Nothing here writes a password to disk or to a log. The admin login for the local Keycloak is read from the compose file
(or DEMO_KC_ADMIN_USER / DEMO_KC_ADMIN_PASSWORD) and is never printed.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import secrets
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
REALM = os.environ.get("DEMO_REALM", "dam-demo")
KC_URL = os.environ.get("DEMO_KC_URL", "http://localhost:18080").rstrip("/")
TENANT_ID = os.environ.get("DEMO_TENANT_ID", "0197d000-0000-7000-8000-00000000d3a0")
CLIENT_ID = "dam-web"
# Roles offered to demo users. Dealer and ApiClient are left out: they need attributes a demo user does not have.
DEMO_ROLES = ["Admin", "Librarian", "Editor", "Approver", "Contributor", "Viewer"]
SEED_PREFIX = "orbit-seeder-"
# No 0/O/1/l/I: passwords get read out loud and typed by hand.
ALPHABET_UPPER = "ABCDEFGHJKLMNPQRSTUVWXYZ"
ALPHABET_LOWER = "abcdefghijkmnpqrstuvwxyz"
ALPHABET_DIGIT = "23456789"


class KcError(RuntimeError):
    pass


def generate_password(length: int = 20) -> str:
    """CSPRNG password with at least one upper, lower and digit; ambiguous characters excluded."""
    pools = [ALPHABET_UPPER, ALPHABET_LOWER, ALPHABET_DIGIT]
    allchars = "".join(pools)
    chars = [secrets.choice(p) for p in pools] + [secrets.choice(allchars) for _ in range(length - len(pools))]
    secrets.SystemRandom().shuffle(chars)
    return "".join(chars)


def _admin_credentials() -> tuple[str, str]:
    user, pw = os.environ.get("DEMO_KC_ADMIN_USER"), os.environ.get("DEMO_KC_ADMIN_PASSWORD")
    if user and pw:
        return user, pw
    text = (REPO / "deploy" / "docker-compose.yml").read_text()
    u = re.search(r"^\s*KEYCLOAK_ADMIN:\s*(\S+)\s*$", text, re.M)
    p = re.search(r"^\s*KEYCLOAK_ADMIN_PASSWORD:\s*(\S+)\s*$", text, re.M)
    if not (u and p):
        raise KcError("Cannot find the Keycloak admin login: set DEMO_KC_ADMIN_USER and DEMO_KC_ADMIN_PASSWORD.")
    return u.group(1).strip("\"'"), p.group(1).strip("\"'")


class Keycloak:
    def __init__(self, base: str = KC_URL):
        self.base = base
        self._token: str | None = None

    # -- plumbing ------------------------------------------------------------------------------------------------
    def _call(self, method: str, url: str, *, body=None, form=None, token: str | None = None, ok=(200, 201, 204)):
        data, headers = None, {"Accept": "application/json"}
        if form is not None:
            data, headers["Content-Type"] = urllib.parse.urlencode(form).encode(), "application/x-www-form-urlencoded"
        elif body is not None:
            data, headers["Content-Type"] = json.dumps(body).encode(), "application/json"
        if token:
            headers["Authorization"] = f"Bearer {token}"
        req = urllib.request.Request(url, data=data, headers=headers, method=method)
        try:
            # nosemgrep: python.lang.security.audit.dynamic-urllib-use-detected.dynamic-urllib-use-detected -- the URL is Keycloak's own admin address plus a fixed path (http/https only), never user input
            with urllib.request.urlopen(req, timeout=30) as r:
                raw = r.read()
                code, location = r.status, r.headers.get("Location")
        except urllib.error.HTTPError as e:
            if e.code in ok:
                return e.code, None, None
            # The body can echo what we sent, so only the status and Keycloak's own error text are reported.
            msg = ""
            try:
                err = json.loads(e.read() or b"{}")
                msg = err.get("errorMessage") or err.get("error_description") or err.get("error") or ""
            except ValueError:
                pass
            raise KcError(f"{method} {urllib.parse.urlsplit(url).path} -> HTTP {e.code} {msg}".strip()) from None
        except urllib.error.URLError as e:
            raise KcError(f"Cannot reach Keycloak at {self.base}: {e.reason}") from None
        return code, (json.loads(raw) if raw else None), location

    def admin_token(self) -> str:
        if not self._token:
            user, pw = _admin_credentials()
            _, tok, _ = self._call("POST", f"{self.base}/realms/master/protocol/openid-connect/token",
                                   form={"grant_type": "password", "client_id": "admin-cli", "username": user, "password": pw})
            self._token = tok["access_token"]
        return self._token

    def admin(self, method: str, path: str, body=None, ok=(200, 201, 204)):
        code, data, loc = self._call(method, f"{self.base}/admin/realms{path}", body=body, token=self.admin_token(), ok=ok)
        return (code, data, loc)

    def get(self, path: str):
        return self.admin("GET", path)[1]

    # -- realm ---------------------------------------------------------------------------------------------------
    def realm_exists(self) -> bool:
        code, _, _ = self.admin("GET", f"/{REALM}", ok=(200, 404))
        return code == 200

    def realm_representation(self, public: str) -> dict:
        """The demo realm, derived from deploy/keycloak/realm-dam.json: same roles, claim mappers and user profile, no users or groups."""
        rep = json.loads((REPO / "deploy" / "keycloak" / "realm-dam.json").read_text())
        rep["realm"], rep["id"] = REALM, REALM
        rep["displayName"], rep["displayNameHtml"] = "Orbit demo", "<strong>Orbit</strong> demo"
        rep.pop("users", None)
        rep.pop("groups", None)
        rep["roles"] = {"realm": [r for r in rep["roles"]["realm"] if r["name"] in DEMO_ROLES]}
        rep["clients"] = [c for c in rep["clients"] if c["clientId"] in (CLIENT_ID, "dam-api")]   # no pads4-plugin (it has a known secret)
        for scope in rep["clientScopes"]:
            for m in scope["protocolMappers"]:
                if m["name"] == "tenant":
                    m["config"]["claim.value"] = TENANT_ID
        rep.update(
            enabled=True, sslRequired="external", registrationAllowed=False, registrationEmailAsUsername=False, rememberMe=False,
            verifyEmail=False, resetPasswordAllowed=False, loginWithEmailAllowed=True, duplicateEmailsAllowed=False, editUsernameAllowed=False,
            bruteForceProtected=True, permanentLockout=False, failureFactor=5, waitIncrementSeconds=60, maxFailureWaitSeconds=900,
            minimumQuickLoginWaitSeconds=60, maxDeltaTimeSeconds=43200,
            accessTokenLifespan=600, ssoSessionIdleTimeout=1800, ssoSessionMaxLifespan=28800, accessCodeLifespan=60,
            offlineSessionIdleTimeout=1800, passwordPolicy="length(12)",
            eventsEnabled=True, eventsExpiration=604800, adminEventsEnabled=False,
            attributes={"frontendUrl": public},
        )
        for c in rep["clients"]:
            if c["clientId"] == CLIENT_ID:
                c.update(self.web_client_settings(public))
        return rep

    @staticmethod
    def web_client_settings(public: str) -> dict:
        return {
            "publicClient": True, "standardFlowEnabled": True, "directAccessGrantsEnabled": False, "implicitFlowEnabled": False,
            "serviceAccountsEnabled": False, "consentRequired": False, "fullScopeAllowed": True,
            "rootUrl": public, "baseUrl": "/",
            "redirectUris": [f"{public}/auth/callback"], "webOrigins": [public],
            "attributes": {"pkce.code.challenge.method": "S256", "post.logout.redirect.uris": f"{public}/login"},
            "defaultClientScopes": ["dam-claims"],
        }

    def ensure_realm(self, public: str) -> str:
        """Create the realm, or point an existing one at a new public address. Returns 'created' or 'updated'."""
        rep = self.realm_representation(public)
        if not self.realm_exists():
            self.admin("POST", "", rep)
            self.switch_off_builtin_clients()
            return "created"
        cur = self.get(f"/{REALM}")
        # Only what depends on the address (and the settings the demo insists on) is touched; users, sessions and keys stay.
        for key in ("sslRequired", "registrationAllowed", "bruteForceProtected", "failureFactor", "accessTokenLifespan", "ssoSessionIdleTimeout",
                    "ssoSessionMaxLifespan", "verifyEmail", "resetPasswordAllowed", "passwordPolicy", "eventsEnabled", "eventsExpiration"):
            cur[key] = rep[key]
        cur["attributes"] = {**(cur.get("attributes") or {}), "frontendUrl": public}
        self.admin("PUT", f"/{REALM}", cur)
        self.ensure_web_client(public)
        self.ensure_tenant_claim()
        self.switch_off_builtin_clients()
        return "updated"

    def client_by_id(self, client_id: str) -> dict | None:
        found = self.get(f"/{REALM}/clients?clientId={urllib.parse.quote(client_id)}&search=false") or []
        return found[0] if found else None

    def switch_off_builtin_clients(self) -> None:
        """Every realm ships with clients the demo does not use. admin-cli in particular allows password grants, so they are all disabled."""
        for cid in ("admin-cli", "account", "account-console", "security-admin-console"):
            c = self.client_by_id(cid)
            if c is not None and (c.get("enabled", True) or c.get("directAccessGrantsEnabled")):
                c.update(enabled=False, directAccessGrantsEnabled=False)
                self.admin("PUT", f"/{REALM}/clients/{c['id']}", c)

    def ensure_web_client(self, public: str) -> None:
        c = self.client_by_id(CLIENT_ID)
        if c is None:
            raise KcError(f"Client {CLIENT_ID} is missing from realm {REALM}.")
        s = self.web_client_settings(public)
        c.update({k: v for k, v in s.items() if k not in ("attributes", "defaultClientScopes")})
        c["attributes"] = {**(c.get("attributes") or {}), **s["attributes"]}
        self.admin("PUT", f"/{REALM}/clients/{c['id']}", c)

    def ensure_tenant_claim(self) -> None:
        scopes = self.get(f"/{REALM}/client-scopes") or []
        for sc in (s for s in scopes if s["name"] == "dam-claims"):
            for m in self.get(f"/{REALM}/client-scopes/{sc['id']}/protocol-mappers/models") or []:
                if m["name"] == "tenant" and m["config"].get("claim.value") != TENANT_ID:
                    m["config"]["claim.value"] = TENANT_ID
                    self.admin("PUT", f"/{REALM}/client-scopes/{sc['id']}/protocol-mappers/models/{m['id']}", m)

    def set_direct_grants(self, client_uuid: str, on: bool) -> None:
        c = self.get(f"/{REALM}/clients/{client_uuid}")
        c["directAccessGrantsEnabled"] = on
        self.admin("PUT", f"/{REALM}/clients/{client_uuid}", c)

    # -- users ---------------------------------------------------------------------------------------------------
    def user_by_name(self, username: str) -> dict | None:
        found = self.get(f"/{REALM}/users?username={urllib.parse.quote(username)}&exact=true") or []
        return found[0] if found else None

    def all_users(self) -> list[dict]:
        return self.get(f"/{REALM}/users?max=1000") or []

    def set_password(self, user_id: str, password: str) -> None:
        self.admin("PUT", f"/{REALM}/users/{user_id}/reset-password", {"type": "password", "value": password, "temporary": False})

    def set_roles(self, user_id: str, role: str) -> None:
        wanted = self.get(f"/{REALM}/roles/{urllib.parse.quote(role)}")
        have = self.get(f"/{REALM}/users/{user_id}/role-mappings/realm") or []
        stale = [r for r in have if r["name"] in DEMO_ROLES and r["name"] != role]
        if stale:
            self.admin("DELETE", f"/{REALM}/users/{user_id}/role-mappings/realm", stale)
        if not any(r["name"] == role for r in have):
            self.admin("POST", f"/{REALM}/users/{user_id}/role-mappings/realm", [wanted])

    def ensure_user(self, username: str, role: str, *, new_password: bool, enabled: bool = True,
                    first: str | None = None, last: str | None = None, email: str | None = None, fixed_id: str | None = None) -> str | None:
        """Create or update one user. Returns the new password if one was set, else None."""
        parts = username.replace("_", ".").split(".")
        rep = {"username": username, "enabled": enabled, "emailVerified": True, "email": email or f"{username}@acme-demo.example",
               "firstName": first or parts[0].capitalize(), "lastName": last or (" ".join(p.capitalize() for p in parts[1:]) or "Demo"),
               "requiredActions": []}
        existing = self.user_by_name(username)
        if existing is None:
            self.admin("POST", f"/{REALM}/users", {**rep, "id": fixed_id} if fixed_id else rep)
            existing = self.user_by_name(username)
            new_password = True
        else:
            self.admin("PUT", f"/{REALM}/users/{existing['id']}", {**existing, **rep})
        assert existing is not None
        self.set_roles(existing["id"], role)
        if not new_password:
            return None
        pw = generate_password()
        self.set_password(existing["id"], pw)
        return pw

    def delete_user(self, user_id: str) -> None:
        self.admin("DELETE", f"/{REALM}/users/{user_id}")

    def logout_user(self, user_id: str) -> None:
        self.admin("POST", f"/{REALM}/users/{user_id}/logout")

    # -- temporary seeder ----------------------------------------------------------------------------------------
    def lock_down(self) -> None:
        """Back to the steady state: no password grants on the web client, no seeder user left behind."""
        c = self.client_by_id(CLIENT_ID)
        if c is not None and c.get("directAccessGrantsEnabled"):
            self.set_direct_grants(c["id"], False)
        for usr in self.all_users():
            if usr["username"].startswith(SEED_PREFIX):
                self.delete_user(usr["id"])

    def password_grant(self, client_id: str, username: str, password: str) -> str:
        """A token for the demo realm, fetched from the local Keycloak (its issuer is still the public one, thanks to frontendUrl)."""
        _, tok, _ = self._call("POST", f"{self.base}/realms/{REALM}/protocol/openid-connect/token",
                               form={"grant_type": "password", "client_id": client_id, "username": username, "password": password})
        return tok["access_token"]


def parse_specs(text: str) -> list[tuple[str, str]]:
    out: list[tuple[str, str]] = []
    for raw in re.split(r"[,\s]+", text.strip()):
        if not raw:
            continue
        name, _, role = raw.partition(":")
        name, role = name.strip().lower(), (role.strip() or "Admin")
        role = next((r for r in DEMO_ROLES if r.lower() == role.lower()), role)
        if not re.fullmatch(r"[a-z0-9][a-z0-9._-]{2,40}", name):
            raise KcError(f"Bad user name '{name}': use 3-41 lowercase letters, digits, dots, dashes or underscores.")
        if role not in DEMO_ROLES:
            raise KcError(f"Bad role '{role}' for {name}: choose one of {', '.join(DEMO_ROLES)}.")
        if any(n == name for n, _ in out):
            raise KcError(f"User '{name}' is listed twice.")
        out.append((name, role))
    return out


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    r = sub.add_parser("realm")
    r.add_argument("--public", required=True)
    u = sub.add_parser("users")
    u.add_argument("--specs", default="")
    u.add_argument("--reset-passwords", action="store_true")
    sub.add_parser("lockdown")
    sub.add_parser("disable")
    sub.add_parser("purge")
    a = ap.parse_args(argv)
    kc = Keycloak()
    try:
        if a.cmd == "realm":
            print(f"realm {REALM}: {kc.ensure_realm(a.public.rstrip('/'))} (public address {a.public.rstrip('/')})")
        elif a.cmd == "users":
            specs = parse_specs(a.specs)
            listed = {n for n, _ in specs}
            for existing in kc.all_users():                       # anyone not invited this run stays switched off
                if existing["username"] not in listed and existing.get("enabled") and not existing["username"].startswith(SEED_PREFIX):
                    kc.admin("PUT", f"/{REALM}/users/{existing['id']}", {**existing, "enabled": False})
                    kc.logout_user(existing["id"])
            for name, role in specs:
                existed = kc.user_by_name(name) is not None
                pw = kc.ensure_user(name, role, new_password=a.reset_passwords)
                if pw:
                    print(f"{name}\t{pw}\t{role}")                  # STDOUT only: demo.sh keeps it in memory and prints it once at the end
                elif existed:
                    print(f"{name}\t-\t{role}")
        elif a.cmd == "lockdown":
            kc.lock_down()
        elif a.cmd == "disable":
            for usr in kc.all_users():
                if usr["username"].startswith(SEED_PREFIX):
                    kc.delete_user(usr["id"])
                    continue
                if usr.get("enabled"):
                    kc.admin("PUT", f"/{REALM}/users/{usr['id']}", {**usr, "enabled": False})
                kc.logout_user(usr["id"])
            print("demo users disabled and signed out")
        elif a.cmd == "purge":
            users = kc.all_users()
            for usr in users:
                kc.delete_user(usr["id"])
            print(f"deleted {len(users)} demo user(s)")
    except KcError as e:
        print(f"error: {e}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
