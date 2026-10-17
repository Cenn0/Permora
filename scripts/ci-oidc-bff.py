#!/usr/bin/env python3
"""True BFF/OIDC Authorization Code + PKCE with disposable Keycloak LoA2/TOTP.

Runs actual password-only LoA1 denial and authentic two-factor OTP step-up against
an ephemeral, Admin-REST-configured Keycloak browser flow. This does not certify
any corporate Keycloak/LDAP deployment or product release. Never use production accounts.
"""
import html.parser
import http.cookiejar
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
BASE = os.environ.get("PERMORA_TEST_KC_URL", "").rstrip("/")
USER = os.environ.get("PERMORA_TEST_KC_USERNAME", "")
PASSWORD = os.environ.get("PERMORA_TEST_KC_PASSWORD", "")
DB = os.environ.get("PERMORA_TEST_POSTGRES", "")
APP_URL = "http://127.0.0.1:5082"
REALM = "permora-bff-ci-" + uuid.uuid4().hex[:12]
LOGIN_CLIENT = "permora-browser-ci"
if not BASE.startswith("http://127.0.0.1:") or not USER or not PASSWORD or "Database=permora_test_" not in DB:
    raise SystemExit("OIDC smoke is restricted to disposable loopback Keycloak and permora_test_* PostgreSQL")


def admin_http(method, path, body=None, *, token=None, form=False, allowed=(200, 201, 204)):
    headers = {"Accept": "application/json"}
    if token:
        headers["Authorization"] = "Bearer " + token
    data = None
    if body is not None:
        data = (urllib.parse.urlencode(body) if form else json.dumps(body)).encode()
        headers["Content-Type"] = "application/x-www-form-urlencoded" if form else "application/json"
    req = urllib.request.Request(BASE + path, data=data, headers=headers, method=method)
    try:
        response = urllib.request.urlopen(req, timeout=20)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        status, raw = response.status, response.read(512000)
    if status not in allowed:
        raise AssertionError(f"Keycloak {method} {path.split('?')[0]} returned unexpected HTTP {status}")
    return json.loads(raw) if raw else None


class LoginForm(html.parser.HTMLParser):
    def __init__(self):
        super().__init__()
        self.action = None
        self.fields = {}
        self.inside = False

    def handle_starttag(self, tag, attrs):
        data = dict(attrs)
        if tag == "form" and (data.get("id") == "kc-form-login" or data.get("name") == "login"):
            self.action = data.get("action")
            self.inside = True
        elif tag == "input" and self.inside and data.get("name"):
            self.fields[data["name"]] = data.get("value", "")

    def handle_endtag(self, tag):
        if tag == "form":
            self.inside = False


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, msg, headers, url):
        return None


class ObservedRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, msg, headers, url):
        parsed = urllib.parse.urlsplit(url)
        print("OIDC route:", code, parsed.netloc + parsed.path,
              "parameter names:", sorted(urllib.parse.parse_qs(parsed.query).keys()), flush=True)
        return super().redirect_request(request, fp, code, msg, headers, url)


def browser(opener, method, path, body=None, headers=None):
    url = path if path.startswith("http") else APP_URL + path
    req = urllib.request.Request(url, data=body, headers=headers or {}, method=method)
    try:
        response = opener.open(req, timeout=20)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        raw = response.read(300000)
        return response.status, response.geturl(), dict(response.headers), raw


def wait_for_idp():
    for _ in range(60):
        try:
            config = admin_http("GET", "/realms/master/.well-known/openid-configuration")
            if config["issuer"]:
                return
        except (OSError, ValueError, AssertionError):
            pass
        time.sleep(3)
    raise TimeoutError("Disposable Keycloak failed to start")


def wait_for_bff(proc):
    for _ in range(70):
        if proc.poll() is not None:
            raise RuntimeError("ASP.NET BFF terminated prematurely")
        try:
            with urllib.request.urlopen(APP_URL + "/health/live", timeout=3) as response:
                if response.status == 200:
                    return
        except OSError:
            pass
        time.sleep(0.5)
    raise TimeoutError("ASP.NET BFF did not start")


def assert_status(actual, wanted, label):
    if actual != wanted:
        raise AssertionError(f"{label}: expected HTTP {wanted}; got {actual}")
    print("PASS:", label)


def configure_isolated_step_up(realm_admin, token):
    """Configure LoA1 password + LoA2 TOTP through Keycloak's documented Admin REST API."""
    auth = realm_admin + "/authentication"
    root = "permora-ci-step-up"
    admin_http("POST", auth + "/flows", {
        "alias": root, "description": "Disposable CI LoA1/LoA2 browser flow",
        "providerId": "basic-flow", "topLevel": True, "builtIn": False
    }, token=token)

    def executions(alias):
        return admin_http("GET", auth + "/flows/" + urllib.parse.quote(alias, safe="") + "/executions", token=token)

    def execution(alias, provider=None, child=None):
        rows = executions(alias)
        matches = [row for row in rows if
                   (provider is not None and row.get("providerId") == provider)
                   or (child is not None and row.get("displayName") == child)]
        if len(matches) != 1:
            raise AssertionError("Unexpected auth-flow topology for " + alias +
                                 "; selected providers: " + repr([(r.get("displayName"), r.get("providerId")) for r in rows]))
        return matches[0]

    def requirement(parent, item, value):
        updated = dict(item)
        updated["requirement"] = value
        admin_http("PUT", auth + "/flows/" + urllib.parse.quote(parent, safe="") + "/executions",
                   updated, token=token, allowed=(204,))

    def create_step(parent, provider, required):
        path = auth + "/flows/" + urllib.parse.quote(parent, safe="") + "/executions/execution"
        admin_http("POST", path, {"provider": provider}, token=token, allowed=(201,))
        item = execution(parent, provider=provider)
        requirement(parent, item, required)
        return item

    def create_flow(parent, name, requirement_name):
        path = auth + "/flows/" + urllib.parse.quote(parent, safe="") + "/executions/flow"
        admin_http("POST", path, {
            "alias": name, "type": "basic-flow", "provider": "basic-flow",
            "description": "Disposable Permora LoA test subflow"
        }, token=token, allowed=(201,))
        requirement(parent, execution(parent, child=name), requirement_name)

    create_step(root, "auth-cookie", "ALTERNATIVE")
    auth_flow = "permora-ci-auth"
    create_flow(root, auth_flow, "ALTERNATIVE")

    for level, provider, max_age in (
            ("1", "auth-username-password-form", "36000"),
            ("2", "auth-otp-form", "0")):
        condition_alias = "permora-ci-level-" + level
        create_flow(auth_flow, condition_alias, "CONDITIONAL")
        item = create_step(condition_alias, "conditional-level-of-authentication", "REQUIRED")
        admin_http("POST", auth + "/executions/" + item["id"] + "/config", {
            "alias": "permora-ci-loa-" + level,
            "config": {"loa-condition-level": level, "loa-max-age": max_age}
        }, token=token, allowed=(201,))
        create_step(condition_alias, provider, "REQUIRED")

    admin_http("PUT", realm_admin, {"browserFlow": root}, token=token, allowed=(204,))
    current = admin_http("GET", realm_admin, token=token)
    if current.get("browserFlow") != root:
        raise AssertionError("Keycloak did not activate the disposable LoA browser flow")
    print("PASS: configured disposable Keycloak LoA1 password + LoA2 OTP browser flow", flush=True)

def run():
    wait_for_idp()
    token = admin_http("POST", "/realms/master/protocol/openid-connect/token", {
        "grant_type": "password", "client_id": "admin-cli", "username": USER, "password": PASSWORD
    }, form=True)["access_token"]
    admin_http("POST", "/admin/realms", {"realm": REALM, "enabled": True}, token=token)
    try:
        admin = "/admin/realms/" + REALM
        configure_isolated_step_up(admin, token)
        admin_http("POST", admin + "/clients", {
            "clientId": LOGIN_CLIENT, "enabled": True, "protocol": "openid-connect",
            "standardFlowEnabled": True, "implicitFlowEnabled": False,
            "directAccessGrantsEnabled": False, "serviceAccountsEnabled": False,
            "publicClient": False, "fullScopeAllowed": False,
            "redirectUris": [APP_URL + "/signin-oidc", APP_URL + "/signout-callback-oidc"]
        }, token=token)
        clients = admin_http("GET", admin + "/clients?clientId=" + LOGIN_CLIENT, token=token)
        matching = [c for c in clients if c.get("clientId") == LOGIN_CLIENT]
        if len(matching) != 1:
            raise AssertionError("OIDC client creation failed")
        client_uuid = matching[0]["id"]
        secret = admin_http("GET", admin + "/clients/" + client_uuid + "/client-secret", token=token).get("value")
        if not secret:
            raise AssertionError("OIDC confidential client has no secret")
        test_password = "PermoraOnly-CI-Password-1!"
        admin_http("POST", admin + "/users", {
            "username": "bff-ci-user", "enabled": True, "emailVerified": True,
            "firstName": "Permora", "lastName": "CI",
            "email": "bff-ci-user@permora.invalid",
            "requiredActions": ["CONFIGURE_TOTP"], "credentials": [
                {"type": "password", "value": test_password, "temporary": False}
            ]
        }, token=token)

        with tempfile.TemporaryDirectory(prefix="permora-real-oidc-") as directory:
            tmp = Path(directory)
            (tmp / "keys").mkdir()
            (tmp / "oidc-secret").write_text(secret)
            config = {
                "ConnectionStrings": {"Portal": DB},
                "Portal": {
                    "Authority": BASE + "/realms/" + REALM,
                    "ClientId": LOGIN_CLIENT,
                    "ClientSecretFile": str(tmp / "oidc-secret"),
                    "PublicOrigin": APP_URL, "DataProtectionPath": str(tmp / "keys"),
                    "AdministratorSubjects": [], "RealmTeams": {}, "AllowHttpInDevelopment": True,
                    "EnableDevelopmentLogin": False,
                    "PrivilegedAcr": "2", "PrivilegedAuthenticationMaxAgeMinutes": 10
                },
                "Keycloak": {"AllowHttpInDevelopment": True, "Realms": {}}
            }
            (tmp / "portal.json").write_text(json.dumps(config))
            env = os.environ.copy()
            env.update({"PERMORA_CONFIG": str(tmp / "portal.json"),
                        "DOTNET_ENVIRONMENT": "Development", "ASPNETCORE_ENVIRONMENT": "Development",
                        "ASPNETCORE_URLS": APP_URL,
                        "PERMORA_CI_DIAG_ASSURANCE": "1"})
            subprocess.run(["dotnet", "run", "--project", "src/Permora.Worker",
                            "-c", "Release", "--no-build", "--", "--migrate"],
                           cwd=ROOT, env=env, check=True, timeout=60,
                           stdout=subprocess.DEVNULL)
            with (tmp / "api.log").open("w") as logfile:
                proc = subprocess.Popen(["dotnet", "run", "--project", "src/Permora.Api",
                                         "-c", "Release", "--no-build", "--no-launch-profile"],
                                        cwd=ROOT, env=env, stdin=subprocess.DEVNULL,
                                        stdout=logfile, stderr=subprocess.STDOUT)
                try:
                    wait_for_bff(proc)
                    browser_env = env.copy()
                    browser_env["PERMORA_TEST_BROWSER_USERNAME"] = "bff-ci-user"
                    browser_env["PERMORA_TEST_BROWSER_PASSWORD"] = test_password
                    subprocess.run(["node", "web/e2e/oidc-bff.smoke.mjs"],
                                   cwd=ROOT, env=browser_env, check=True, timeout=220)
                except BaseException:
                    # Do not echo request credentials, OIDC codes, or ID tokens into CI logs.
                    print("BFF test failed; ASP.NET process status:", proc.poll())
                    # Only booleans and counts, never token content, cookies or account IDs.
                    for line in (tmp / "api.log").read_text(errors="replace").splitlines():
                        if "PERMORA-CI-ASSURANCE:" in line:
                            print(line.split("PERMORA-CI-ASSURANCE:", 1)[1].strip(), flush=True)
                    raise
                finally:
                    proc.terminate()
                    try:
                        proc.wait(timeout=6)
                    except subprocess.TimeoutExpired:
                        proc.kill()
                        proc.wait(timeout=5)
    finally:
        admin_http("DELETE", "/admin/realms/" + REALM, token=token, allowed=(204, 404))


if __name__ == "__main__":
    run()
