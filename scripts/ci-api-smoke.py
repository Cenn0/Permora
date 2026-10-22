#!/usr/bin/env python3
"""Exercise the started ASP.NET Core HTTP API with real PostgreSQL, no production SSO.

Uses the explicit Development/loopback authentication mode. This does not prove OIDC/MFA.
"""
import http.cookiejar
from datetime import datetime, timedelta, timezone
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


def seed_isolated_group_gate_fixture():
    """Insert an Active test app and group into the isolated PostgreSQL test DB.

    This deliberately bypasses real IAM onboarding *only in an isolated test
    database* so HTTP admission can be tested without Keycloak Admin secrets.
    It does not assert that these grants exist in Keycloak.
    """
    connection = dict(part.split("=", 1) for part in DB.split(";") if "=" in part)
    host = connection.get("Host", "")
    database = connection.get("Database", "")
    if host not in ("127.0.0.1", "localhost") or not database.startswith("permora_test_"):
        raise RuntimeError("Refusing to prepare group gate fixture outside a loopback test database")

    app_id, assigned_role, new_role, group_id, assignment_id = (str(uuid.uuid4()) for _ in range(5))
    now = datetime.now(timezone.utc)
    document = {
        "id": app_id, "teamId": TEAM, "realmKey": "enterprise",
        "slug": "ci-group-admission", "name": "CI Group Admission",
        "description": "No actual Keycloak credentials or remote group grants",
        "environment": "Test", "status": "Active", "revision": 1,
        "keycloakClientUuid": str(uuid.uuid4()), "keycloakClientId": "ci-only-app",
        "loginClientUuid": None,
        "policy": {"requireApproval": True, "maxGrantDays": 90, "separationRules": []},
        "permissions": [],
        "roles": [
            {"id": assigned_role, "key": "reader", "name": "Reader",
             "description": "Previously assigned synthetic role", "permissionIds": []},
            {"id": new_role, "key": "writer", "name": "Writer",
             "description": "Prohibited new group assignment", "permissionIds": []}
        ],
        "assignments": [{"id": assignment_id, "roleId": assigned_role,
                         "principalId": group_id, "grantedBy": "isolated-fixture",
                         "grantedAt": now.isoformat(),
                         "expiresAt": (now + timedelta(days=7)).isoformat()}]
    }
    group = {
        "id": group_id, "realmKey": "enterprise", "subjectKey": "ci-synthetic-group",
        "keycloakId": "ci-synthetic-group", "name": "CI Test Group",
        "kind": "Group", "enabled": True, "protected": False,
        "membershipComplete": True, "effectiveMemberKeys": [],
        "syncedAt": now.isoformat()
    }

    def quoted_json(value):
        return "'" + json.dumps(value, separators=(",", ":")).replace("'", "''") + "'::jsonb"

    sql = f"""
      INSERT INTO applications(id, team_id, realm_key, slug, environment, revision, document)
      VALUES ('{app_id}'::uuid, '{TEAM}'::uuid, 'enterprise', 'ci-group-admission',
              'Test', 1, {quoted_json(document)});
      INSERT INTO directory(id, realm_key, name, document)
      VALUES ('{group_id}'::uuid, 'enterprise', 'CI Test Group', {quoted_json(group)});
    """
    postgres_env = os.environ.copy()
    postgres_env.update({
        "PGHOST": host, "PGPORT": connection.get("Port", "5432"),
        "PGUSER": connection["Username"], "PGPASSWORD": connection.get("Password", ""),
        "PGDATABASE": database
    })
    # An explicit native psql path permits the identical isolated fixture on
    # developer machines without Docker. CI retains its PostgreSQL 18 client;
    # this changes only SQL transport, never the assertions or fixture data.
    native_psql = os.environ.get("PERMORA_TEST_PSQL_BIN")
    if native_psql:
        binary = Path(native_psql)
        if not binary.is_absolute() or not binary.is_file() or not os.access(binary, os.X_OK):
            raise ValueError("PERMORA_TEST_PSQL_BIN must be an absolute executable psql path")
        command = [str(binary), "-X", "-v", "ON_ERROR_STOP=1"]
    else:
        command = ["docker", "run", "--rm", "-i", "--network", "host",
                   "-e", "PGHOST", "-e", "PGPORT", "-e", "PGUSER", "-e", "PGPASSWORD",
                   "-e", "PGDATABASE", "postgres:18", "psql", "-X", "-v", "ON_ERROR_STOP=1"]
    subprocess.run(
        command, input=sql, text=True, capture_output=True, check=True,
        timeout=60, env=postgres_env)

    return app_id, new_role, group_id, assignment_id



def request_http(port, method, path, jar, body=None, xsrf=None, extra_headers=None):
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
        raw = response.read(1024 * 1024)
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
            status, _ = request_http(port, "GET", "/health/live", http.cookiejar.CookieJar())
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


def review_schedule_contract(jar, token, application_path, assignment_id):
    """Verify the real HTTP schedule contract without simulating a worker run."""
    status, before = request_http(5080, "GET", application_path, jar)
    assert_status(status, 200, "Owner can read the initial review schedule")
    if "reviewSchedule" not in before or before["reviewSchedule"] is not None:
        raise AssertionError("An application without a review schedule must return reviewSchedule=null")
    revision = before["application"]["revision"]
    schedule_path = application_path + "/review-schedule"
    now = datetime.now(timezone.utc)
    command = {"name": "Monthly access review", "intervalDays": 30,
               "reviewWindowDays": 7, "nextStartAt": (now + timedelta(days=1)).isoformat(),
               "enabled": True, "expectedRevision": 0}
    status, _ = request_http(5080, "PUT", schedule_path, jar, body=command)
    assert_status(status, 400, "Schedule configuration requires a CSRF token")
    for label, invalid in (
        ("zero interval", {"intervalDays": 0}),
        ("interval longer than a year", {"intervalDays": 366}),
        ("zero review window", {"reviewWindowDays": 0}),
        ("review window beyond 90 days", {"intervalDays": 365, "reviewWindowDays": 91}),
        ("review window exceeds interval", {"reviewWindowDays": 31}),
        ("enabled schedule in the past", {"nextStartAt": (now - timedelta(days=1)).isoformat()}),
        ("first review beyond a year", {"nextStartAt": (now + timedelta(days=366)).isoformat()}),
    ):
        status, _ = request_http(5080, "PUT", schedule_path, jar,
            body={**command, **invalid}, xsrf=token)
        assert_status(status, 400, "Schedule validation rejects " + label)
    status, unchanged = request_http(5080, "GET", application_path, jar)
    assert_status(status, 200, "Rejected schedule writes leave the application readable")
    if unchanged["reviewSchedule"] is not None or unchanged["application"]["revision"] != revision:
        raise AssertionError("Rejected schedule writes must not persist configuration or change application revision")

    status, configured = request_http(5080, "PUT", schedule_path, jar, body=command, xsrf=token)
    assert_status(status, 200, "Owner creates a recurring review schedule")
    if (configured.get("revision") != 1 or configured.get("intervalDays") != 30
            or configured.get("reviewWindowDays") != 7 or configured.get("enabled") is not True):
        raise AssertionError("Saved schedule must expose its independent revision and exact configuration")
    status, details = request_http(5080, "GET", application_path, jar)
    assert_status(status, 200, "Application detail returns the persisted schedule")
    if details["reviewSchedule"] != configured or details["application"]["revision"] != revision:
        raise AssertionError("Schedule writes must not invalidate pending application changes")

    # Disabling future scheduling must not close an already open campaign or
    # erase its pending assignment decisions. No worker is running in this test.
    status, review = request_http(5080, "POST", application_path + "/reviews", jar,
        body={"name": "Campaign preserved on pause", "dueAt": (now + timedelta(days=2)).isoformat()},
        xsrf=token)
    assert_status(status, 200, "Owner starts an independent campaign before pausing the schedule")
    if review.get("closed") or not any(item["assignmentId"] == assignment_id
            and item["decision"] == "Pending" for item in review["items"]):
        raise AssertionError("The open campaign must contain the existing assignment")
    disabled = {**command, "enabled": False, "expectedRevision": 1,
                "nextStartAt": (now - timedelta(days=1)).isoformat()}
    status, paused = request_http(5080, "PUT", schedule_path, jar, body=disabled, xsrf=token)
    assert_status(status, 200, "Owner can pause a schedule whose stored date is in the past")
    if paused.get("revision") != 2 or paused.get("enabled") is not False:
        raise AssertionError("Pausing must advance the schedule revision exactly once")
    status, _ = request_http(5080, "PUT", schedule_path, jar, body=command, xsrf=token)
    assert_status(status, 412, "An outdated schedule revision cannot overwrite a later change")
    status, reviews = request_http(5080, "GET", application_path + "/reviews", jar)
    assert_status(status, 200, "Existing reviews remain accessible after pausing")
    if next((item for item in reviews if item["id"] == review["id"]), None) != review:
        raise AssertionError("Pausing the schedule must preserve the existing campaign unchanged")
    status, final = request_http(5080, "GET", application_path, jar)
    assert_status(status, 200, "Schedule state is readable after a stale write is rejected")
    if final["reviewSchedule"] != paused or final["application"]["revision"] != revision:
        raise AssertionError("The stale request must not alter either saved schedule or application revision")
    return paused, review


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

        group_app, group_role, group_id, existing_assignment = seed_isolated_group_gate_fixture()
        processes = []
        with (tmp / "api.log").open("w") as owner_log, (tmp / "foreign.log").open("w") as foreign_log, (tmp / "approved.log").open("w") as approved_log, (tmp / "allowlisted.log").open("w") as allowlisted_log, (tmp / "approver.log").open("w") as approver_log:
            try:
                owner = launch(env, 5080, owner_log)
                processes.append(owner)
                jar = http.cookiejar.CookieJar()
                status, session = request_http(5080, "GET", "/bff/session", jar)
                assert_status(status, 200, "BFF session endpoint starts with a real HTTP response")
                if not session.get("authenticated") or not session.get("development"):
                    raise AssertionError("Expected explicit Development identity, not anonymous mode")
                token = csrf(jar)

                status, apps = request_http(5080, "GET", "/api/v1/applications", jar)
                assert_status(status, 200, "Owned applications are visible to the seeded owner")
                status, monitoring = request_http(5080, "GET", "/api/v1/admin/operations-health", jar)
                assert_status(status, 200, "Central IAM can query aggregate queue and revocation diagnostics")
                if not isinstance(monitoring, dict) or not all(field in monitoring for field in (
                    "queued", "awaitingApproval", "failed", "conflict", "dueRevocations", "oldestDueRevocationSeconds",
                    "failedRevocations", "conflictedRevocations", "oldestOutstandingRevocationSeconds"
                )):
                    raise AssertionError("Operational health is missing expected aggregate fields")
                if len(apps) < 4:
                    raise AssertionError("Demo data did not initialize applications")
                status, _ = request_http(5080, "GET", "/api/v1/applications/" + str(uuid.uuid4()), jar)
                assert_status(status, 404, "Unknown application ID is not exposed")

                entity = {"teamId": TEAM, "realmKey": "enterprise", "slug": "ci-smoke",
                          "name": "CI Smoke Application", "description": "Isolated HTTP contract",
                          "environment": "Test"}
                status, _ = request_http(5080, "POST", "/api/v1/applications", jar, body=entity)
                assert_status(status, 400, "Mutating API rejects missing CSRF token")
                status, created = request_http(5080, "POST", "/api/v1/applications", jar, body=entity, xsrf=token)
                assert_status(status, 201, "Valid owner can register an application with CSRF")
                app_id = created.get("id")
                if not app_id:
                    raise AssertionError("Registered application has no stable identifier")
                status, details = request_http(5080, "GET", "/api/v1/applications/" + app_id, jar)
                assert_status(status, 200, "Registered application can be read by owner")
                if details["application"]["status"] != "AwaitingIam":
                    raise AssertionError("New applications must be disabled pending central IAM")

                request = {"expectedRevision": 1,
                           "change": {"kind": "CreatePermission", "key": "orders.approve",
                                      "name": "Approve orders", "reason": "Test only"}}
                status, _ = request_http(5080, "POST", "/api/v1/applications/" + app_id + "/changes",
                                 jar, body=request, xsrf=token)
                assert_status(status, 400, "Change request requires an idempotency key")
                status, _ = request_http(5080, "POST", "/api/v1/applications/" + app_id + "/changes",
                                 jar, body=request, xsrf=token,
                                 extra_headers={"Idempotency-Key": "ci-smoke-request-0001"})
                assert_status(status, 409, "Pending IAM application refuses a new permission")

                # A real ASP.NET HTTP request must receive 403 *before* an operation
                # is saved. No real Keycloak grant is made by this isolated fixture.
                group_url = "/api/v1/applications/" + group_app
                status, details = request_http(5080, "GET", group_url, jar)
                assert_status(status, 200, "Isolated active group fixture is visible to the owner")
                if details["application"]["status"] != "Active":
                    raise AssertionError("Group grant HTTP fixture must be Active")
                paused_schedule, open_review = review_schedule_contract(
                    jar, token, group_url, existing_assignment)
                group_grant = {"expectedRevision": 1, "change": {
                    "kind": "GrantAccess", "roleId": group_role, "principalId": group_id,
                    "expiresAt": (datetime.now(timezone.utc) + timedelta(days=7)).isoformat(),
                    "reason": "Isolated negative group grant test"}}
                status, _ = request_http(5080, "POST", group_url + "/changes", jar,
                    body=group_grant, xsrf=token,
                    extra_headers={"Idempotency-Key": "ci-deny-group-grant"})
                assert_status(status, 403, "Default-deny rejects group grant synchronously over HTTP")
                status, operations = request_http(5080, "GET", group_url + "/operations", jar)
                assert_status(status, 200, "Group grant operations are queryable by the owner")
                if operations:
                    raise AssertionError("Denied group grant must not be queued or audited as accepted")

                group_revoke = {"expectedRevision": 1, "change": {
                    "kind": "RevokeAccess", "targetId": existing_assignment,
                    "reason": "Emergency revoke must remain available"}}
                status, revoke_operation = request_http(5080, "POST", group_url + "/changes", jar,
                    body=group_revoke, xsrf=token,
                    extra_headers={"Idempotency-Key": "ci-allow-group-revoke"})
                assert_status(status, 202, "Emergency group revocation can still be queued")
                if revoke_operation.get("status") != "Queued":
                    raise AssertionError("Emergency group revocation must enter the normal worker queue")

                # Start a second real API process under a different Development subject.
                foreign_env = env.copy()
                foreign_env["Portal__DevelopmentSubject"] = "ci-foreign"
                outsider = launch(foreign_env, 5081, foreign_log)
                processes.append(outsider)
                foreign_jar = http.cookiejar.CookieJar()
                status, _ = request_http(5081, "GET", "/bff/session", foreign_jar)
                assert_status(status, 200, "Foreign actor session initialized")
                foreign_csrf = csrf(foreign_jar)
                status, foreign_apps = request_http(5081, "GET", "/api/v1/applications", foreign_jar)
                assert_status(status, 200, "Application list remains scoped for foreign actor")
                if foreign_apps:
                    raise AssertionError("A foreign principal unexpectedly sees team applications")
                status, _ = request_http(5081, "GET", "/api/v1/applications/" + app_id, foreign_jar)
                assert_status(status, 404, "Foreign actor cannot discover protected app by direct ID")
                status, _ = request_http(5081, "GET", "/api/v1/admin/operations-health", foreign_jar)
                assert_status(status, 403, "Foreign team cannot query central operational telemetry")
                # Cross-team IDOR regression: every direct-resource API must resolve
                # application visibility before reading records or mutating state.
                foreign_resource = "/api/v1/applications/" + app_id
                scoped_routes = [
                    foreign_resource,
                    foreign_resource + "/operations",
                    foreign_resource + "/operations/" + str(uuid.uuid4()),
                    foreign_resource + "/identities?q=owner",
                    foreign_resource + "/audit",
                    foreign_resource + "/reviews",
                ]
                for path in scoped_routes:
                    status, _ = request_http(5081, "GET", path, foreign_jar)
                    assert_status(status, 404, "Cross-team read blocked: " + path.split("?")[0])

                foreign_commands = [
                    (foreign_resource + "/changes",
                     {"expectedRevision": 1, "change": {"kind": "Reconcile", "reason": "Denied cross-team write"}}),
                    (foreign_resource + "/reviews",
                     {"name": "Forbidden outside team", "dueAt": "2099-01-01T00:00:00Z"}),
                    (foreign_resource + "/operations/" + str(uuid.uuid4()) + "/decision",
                     {"approve": True, "reason": "Forbidden approval"}),
                    (foreign_resource + "/operations/" + str(uuid.uuid4()) + "/retry", {}),
                    (foreign_resource + "/reviews/" + str(uuid.uuid4()) + "/items/" + str(uuid.uuid4()),
                     {"retain": True}),
                ]
                for number, (path, payload) in enumerate(foreign_commands):
                    status, _ = request_http(5081, "POST", path, foreign_jar, body=payload,
                        xsrf=foreign_csrf,
                        extra_headers={"Idempotency-Key": "foreign-denied-" + str(number).zfill(8)})
                    # Deliberately unauthorized resources must not disclose whether
                    # an operation/review UUID exists. The same result is expected
                    # regardless of payload validity.
                    assert_status(status, 404, "Cross-team mutation blocked: " + path)

                for path in (group_url, "/api/v1/applications/" + str(uuid.uuid4())):
                    status, _ = request_http(5081, "PUT", path + "/review-schedule", foreign_jar,
                        body={"name": "x", "intervalDays": 0, "reviewWindowDays": 0,
                              "nextStartAt": "2000-01-01T00:00:00Z", "enabled": True,
                              "expectedRevision": 0}, xsrf=foreign_csrf)
                    assert_status(status, 404,
                        "Schedule scope is checked before disclosing configuration validation")

                status, _ = request_http(5081, "PUT", "/api/v1/admin/applications/" + app_id + "/binding",
                    foreign_jar, body={"clientUuid": str(uuid.uuid4()), "clientId": "not-owned",
                    "expectedRevision": 1}, xsrf=foreign_csrf)
                assert_status(status, 403, "Cross-team actor cannot rebind a client via central IAM")

                status, _ = request_http(5081, "POST", "/api/v1/admin/teams", foreign_jar,
                                 body={"name": "Forbidden", "members": [], "expectedRevision": 0},
                                 xsrf=foreign_csrf)
                assert_status(status, 403, "Foreign actor cannot enter central administrative endpoint")

                status, _ = request_http(5081, "POST", group_url + "/changes", foreign_jar,
                    body=group_grant, xsrf=foreign_csrf,
                    extra_headers={"Idempotency-Key": "ci-foreign-group-probe"})
                assert_status(status, 404, "Foreign actor cannot probe group policy or application existence")

                # Explicit opt-in at API admission does not enable the worker, grant
                # Keycloak credentials, or pretend that a queued grant was applied.
                approved_env = env.copy()
                approved_env["Keycloak__Realms__enterprise__AllowGroupRoleGrants"] = "true"
                approved = launch(approved_env, 5082, approved_log)
                processes.append(approved)
                approved_jar = http.cookiejar.CookieJar()
                status, _ = request_http(5082, "GET", "/bff/session", approved_jar)
                assert_status(status, 200, "Approved-realm API session starts")
                approved_csrf = csrf(approved_jar)
                status, _ = request_http(5082, "POST", group_url + "/changes", approved_jar,
                    body=group_grant, xsrf=approved_csrf,
                    extra_headers={"Idempotency-Key": "ci-realm-only-denied"})
                assert_status(status, 403, "Realm-only opt-in without group ID is denied over real HTTP")

                # The group ID is exact and non-secret. This does not grant
                # Keycloak privileges; the worker must validate it again.
                allowlisted_env = approved_env.copy()
                allowlisted_env["Keycloak__Realms__enterprise__ApprovedGroupIds__0"] = "ci-synthetic-group"
                allowlisted = launch(allowlisted_env, 5083, allowlisted_log)
                processes.append(allowlisted)
                allowlisted_jar = http.cookiejar.CookieJar()
                status, _ = request_http(5083, "GET", "/bff/session", allowlisted_jar)
                assert_status(status, 200, "Allowlisted-group API session starts")
                allowlisted_csrf = csrf(allowlisted_jar)
                status, accepted = request_http(5083, "POST", group_url + "/changes", allowlisted_jar,
                    body=group_grant, xsrf=allowlisted_csrf,
                    extra_headers={"Idempotency-Key": "ci-approved-group-grant"})
                assert_status(status, 202, "Exact realm and Keycloak-group opt-in admits an approval request")
                if accepted.get("status") != "AwaitingApproval":
                    raise AssertionError("Group allowlist must not claim a grant was remotely applied")

                approver_env = env.copy()
                approver_env["Portal__DevelopmentSubject"] = "demo-approver"
                approver = launch(approver_env, 5084, approver_log)
                processes.append(approver)
                approver_jar = http.cookiejar.CookieJar()
                status, _ = request_http(5084, "GET", "/bff/session", approver_jar)
                assert_status(status, 200, "Approver session initialized")
                approver_csrf = csrf(approver_jar)
                status, details = request_http(5084, "GET", group_url, approver_jar)
                assert_status(status, 200, "Approver can read application and schedule")
                if details.get("managementRole") != "Approver" or details["reviewSchedule"] != paused_schedule:
                    raise AssertionError("Approver must receive the saved schedule with read-only management role")
                status, reviews = request_http(5084, "GET", group_url + "/reviews", approver_jar)
                assert_status(status, 200, "Approver can read the outstanding campaign")
                if not any(review["id"] == open_review["id"] for review in reviews):
                    raise AssertionError("Approver must see campaigns belonging to the same team")
                status, _ = request_http(5084, "PUT", group_url + "/review-schedule", approver_jar,
                    body={"name": "Unauthorized schedule change", "intervalDays": 30,
                          "reviewWindowDays": 7,
                          "nextStartAt": (datetime.now(timezone.utc) + timedelta(days=1)).isoformat(),
                          "enabled": True, "expectedRevision": paused_schedule["revision"]}, xsrf=approver_csrf)
                assert_status(status, 403, "Approver cannot configure an owner's review schedule")
                status, final = request_http(5080, "GET", group_url, jar)
                assert_status(status, 200, "Owner reads state after an unauthorized schedule mutation")
                if final["reviewSchedule"] != paused_schedule:
                    raise AssertionError("Unauthorized actors must not mutate the schedule")
            except BaseException:
                for path in (tmp / "api.log", tmp / "foreign.log", tmp / "approved.log", tmp / "allowlisted.log", tmp / "approver.log"):
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
