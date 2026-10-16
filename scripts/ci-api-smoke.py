#!/usr/bin/env python3
"""Exercise the started ASP.NET Core HTTP API with real PostgreSQL, no production SSO.

Uses the explicit Development/loopback authentication mode. This does not prove OIDC/MFA.
"""
import http.cookiejar
import json
import os
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DB = os.environ.get("PERMORA_TEST_POSTGRES", "")
if "Database=permora_test_" not in DB:
    raise SystemExit("PERMORA_TEST_POSTGRES must select an isolated permora_test_* database")
TEAM = "10000000-0000-4000-8000-000000000001"


def http(port, method, path, jar, body=None, xsrf=None, extra_headers=None):
    opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))
    headers = {"Accept": "application/json"}
    if body is not None:
        headers["Content-Type"] = "application/json"
    if xsrf is not None:
        headers["X-XSRF-TOKEN"] = xsrf
    if extra_headers:
        headers.update(extra_headers)
    request = urllib.request.Request(
        f"http://127.0.0.1:{port}{path}",
        data=json.dumps(body).encode("utf-8") if body is not None else None,
        headers=headers, method=method)
    try:
        response = opener.open(request, timeout=6)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        raw = response.read(4096)
        try:
            payload = json.loads(raw) if raw else None
        except ValueError:
            payload = None
        return response.status, payload


def assert_status(actual, expected, label):
    if actual != expected:
        raise AssertionError(f"{label}: expected HTTP {expected}, received {actual}")
    print("PASS:", label)


def wait(port, process):
    deadline = time.monotonic() + 35
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError(f"API process exited prematurely with code {process.returncode}")
        try:
            status, _ = http(port, "GET", "/health/live", http.cookiejar.CookieJar())
            if status == 200:
                return
        except OSError:
            pass
        time.sleep(0.6)
    raise TimeoutError("ASP.NET API did not become live within 35 seconds")


def csrf(jar):
    cookie = next((cookie.value for cookie in jar if cookie.name == "XSRF-TOKEN"), None)
    if not cookie:
        raise AssertionError("ASP.NET did not issue the expected XSRF-TOKEN cookie")
    return cookie


def launch(env, port, logfile):
    instance = env.copy()
    instance["ASPNETCORE_URLS"] = f"http://127.0.0.1:{port}"
    p = subprocess.Popen(
        ["dotnet", "run", "--project", "src/Permora.Api/Permora.Api.csproj",
         "--configuration", "Release", "--no-build", "--no-launch-profile"],
        cwd=ROOT, env=instance, stdin=subprocess.DEVNULL,
        stdout=logfile, stderr=subprocess.STDOUT)
    wait(port, p)
    return p


def main():
    with tempfile.TemporaryDirectory(prefix="permora-api-ci-") as directory:
        tmp = Path(directory)
        keypath = tmp / "keys"
        keypath.mkdir()
        config = {
            "ConnectionStrings": {"Portal": DB},
            "Portal": {
                "Authority": "http://127.0.0.1:8080/realms/enterprise",
                "ClientId": "permora-portal",
                "PublicOrigin": "http://127.0.0.1:5080",
                "DataProtectionPath": str(keypath),
                "AdministratorSubjects": ["demo-owner"],
                "EnableDevelopmentLogin": True,
                "DevelopmentSubject": "demo-owner",
                "AllowHttpInDevelopment": True,
                "RealmTeams": {"enterprise": [TEAM]}
            },
            "Keycloak": {"AllowHttpInDevelopment": True, "Realms": {}}
        }
        file = tmp / "app.json"
        file.write_text(json.dumps(config))
        env = os.environ.copy()
        env["PERMORA_CONFIG"] = str(file)
        env["DOTNET_ENVIRONMENT"] = "Development"
        env["ASPNETCORE_ENVIRONMENT"] = "Development"
        for flag in ("--migrate", "--seed-demo"):
            subprocess.run(["dotnet", "run", "--project", "src/Permora.Worker/Permora.Worker.csproj",
                            "--configuration", "Release", "--no-build", "--", flag],
                           cwd=ROOT, env=env, check=True, timeout=30)

        processes = []
        with (tmp / "api.log").open("w") as owner_log, (tmp / "foreign.log").open("w") as foreign_log:
            try:
                owner = launch(env, 5080, owner_log)
                processes.append(owner)
                jar = http.cookiejar.CookieJar()
                status, session = http(5080, "GET", "/bff/session", jar)
                assert_status(status, 200, "BFF session endpoint starts with a real HTTP response")
                if not session.get("authenticated") or not session.get("development"):
                    raise AssertionError("Expected explicit Development identity, not anonymous mode")
                token = csrf(jar)

                status, apps = http(5080, "GET", "/api/v1/applications", jar)
                assert_status(status, 200, "Owned applications are visible to the seeded owner")
                if len(apps) < 4:
                    raise AssertionError("Demo data did not initialize applications")
                status, _ = http(5080, "GET", "/api/v1/applications/" + str(uuid.uuid4()), jar)
                assert_status(status, 404, "Unknown application ID is not exposed")

                entity = {"teamId": TEAM, "realmKey": "enterprise", "slug": "ci-smoke",
                          "name": "CI Smoke Application", "description": "Isolated HTTP contract",
                          "environment": "Test"}
                status, _ = http(5080, "POST", "/api/v1/applications", jar, body=entity)
                assert_status(status, 400, "Mutating API rejects missing CSRF token")
                status, created = http(5080, "POST", "/api/v1/applications", jar, body=entity, xsrf=token)
                assert_status(status, 201, "Valid owner can register an application with CSRF")
                app_id = created.get("id")
                if not app_id:
                    raise AssertionError("Registered application has no stable identifier")
                status, details = http(5080, "GET", "/api/v1/applications/" + app_id, jar)
                assert_status(status, 200, "Registered application can be read by owner")
                if details["application"]["status"] != "AwaitingIam":
                    raise AssertionError("New applications must be disabled pending central IAM")

                request = {"expectedRevision": 1,
                           "change": {"kind": "CreatePermission", "key": "orders.approve",
                                      "name": "Approve orders", "reason": "Test only"}}
                status, _ = http(5080, "POST", "/api/v1/applications/" + app_id + "/changes",
                                 jar, body=request, xsrf=token)
                assert_status(status, 400, "Change request requires an idempotency key")
                status, _ = http(5080, "POST", "/api/v1/applications/" + app_id + "/changes",
                                 jar, body=request, xsrf=token,
                                 extra_headers={"Idempotency-Key": "ci-smoke-request-0001"})
                assert_status(status, 409, "Pending IAM application refuses a new permission")

                # Start a second real API process under a different Development subject.
                foreign_env = env.copy()
                foreign_env["Portal__DevelopmentSubject"] = "ci-foreign"
                outsider = launch(foreign_env, 5081, foreign_log)
                processes.append(outsider)
                foreign_jar = http.cookiejar.CookieJar()
                status, _ = http(5081, "GET", "/bff/session", foreign_jar)
                assert_status(status, 200, "Foreign actor session initialized")
                foreign_csrf = csrf(foreign_jar)
                status, foreign_apps = http(5081, "GET", "/api/v1/applications", foreign_jar)
                assert_status(status, 200, "Application list remains scoped for foreign actor")
                if foreign_apps:
                    raise AssertionError("A foreign principal unexpectedly sees team applications")
                status, _ = http(5081, "GET", "/api/v1/applications/" + app_id, foreign_jar)
                assert_status(status, 404, "Foreign actor cannot discover protected app by direct ID")
                status, _ = http(5081, "POST", "/api/v1/admin/teams", foreign_jar,
                                 body={"name": "Forbidden", "members": [], "expectedRevision": 0},
                                 xsrf=foreign_csrf)
                assert_status(status, 403, "Foreign actor cannot enter central administrative endpoint")
            except BaseException:
                for path in (tmp / "api.log", tmp / "foreign.log"):
                    if path.exists():
                        print(f"--- {path.name}: last 30 lines (isolated test only) ---")
                        print("\n".join(path.read_text(errors="replace").splitlines()[-30:]))
                raise
            finally:
                for process in reversed(processes):
                    process.terminate()
                    try:
                        process.wait(timeout=4)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=4)
    print("ASP.NET real HTTP smoke completed; OIDC/MFA is intentionally not covered")


if __name__ == "__main__":
    main()
