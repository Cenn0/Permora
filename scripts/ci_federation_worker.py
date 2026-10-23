#!/usr/bin/env python3
"""Disposable live LDAP -> Keycloak -> PostgreSQL -> Permora.Worker acceptance.

Invoked ONLY from the isolated ci-federation.py runner after its real Keycloak
and certificate-validated OpenLDAP are ready. The application is synthetic.
No corporate identity, durable database, backup, token or secret is uploaded.
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone
import hashlib
import http.cookiejar
import json
import os
from pathlib import Path
import secrets
import subprocess
import time
import uuid
import urllib.error
import urllib.request

from ci_federation_faults import start_proxy

PGPORT = 15440
KEYCLOAK = "http://127.0.0.1:18084"


def compact(value):
    return json.dumps(value, separators=(",", ":"), ensure_ascii=True)


def stable_id(realm, kind, remote):
    # .NET Guid(ReadOnlySpan<byte>) uses the little-endian representation.
    payload = compact([realm, kind, remote]).encode()
    return str(uuid.UUID(bytes_le=hashlib.sha256(payload).digest()[:16]))


def subject(issuer, remote):
    return hashlib.sha256(compact([issuer, remote]).encode()).hexdigest().upper()


def quote(value):
    return "'" + str(value).replace("'", "''") + "'"


def timestamp(days=0):
    return (datetime.now(timezone.utc) + timedelta(days=days)).isoformat()


def command(args, *, data=None, env=None, timeout=120):
    result = subprocess.run(args, input=data, env=env, stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE, timeout=timeout, check=False)
    if result.returncode:
        # Do NOT echo stderr: it may contain fixture-only credentials or tokens.
        raise RuntimeError(f"{Path(args[0]).name} returned {result.returncode}")
    return result.stdout.decode()


def run_worker_acceptance(kc, workspace, realm, base, subject_user,
                          other_user, group, change_group_members, check,
                          remote_client, scoped_secret, *, imported,
                          reader_group, ldap_available):
    """Actual worker/API, both LDAP storage modes, failures and remote readback."""
    if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("PERMORA_SYSTEM_TESTS") != "isolated":
        raise RuntimeError("Only disposable, explicitly authorized CI may execute this test")
    worker = Path(__file__).resolve().parents[1] / "src/Permora.Worker/bin/Release/net10.0/Permora.Worker.dll"
    if not worker.is_file():
        raise RuntimeError("The actual built Release worker is missing")
    suffix = uuid.uuid4().hex[:12]
    container = "permora-ldaps-worker-pg-" + suffix
    issuer = KEYCLOAK + "/realms/" + realm
    password = secrets.token_urlsafe(32)
    app_id, team_id = str(uuid.uuid4()), str(uuid.uuid4())
    reader_id, supervisor_id = str(uuid.uuid4()), str(uuid.uuid4())
    direct_assignment, group_assignment = str(uuid.uuid4()), str(uuid.uuid4())
    emergency_id, emergency_assignment = str(uuid.uuid4()), str(uuid.uuid4())
    observer_id, observer_assignment = str(uuid.uuid4()), str(uuid.uuid4())
    postgres_started = False
    process = None
    api_process = None
    proxy, proxy_thread = start_proxy(realm)
    faults = proxy.faults
    proxy_url = "http://127.0.0.1:" + str(proxy.server_port)
    logs = []

    def wait_for(predicate, label, seconds=100):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            if process is not None and process.poll() is not None:
                raise RuntimeError("Worker exited: " + label)
            if predicate():
                return
            time.sleep(.5)
        raise TimeoutError(label)

    def worker_reported_sync_failure():
        return "Directory synchronization failed" in Path(logs[-1].name).read_text(errors="replace")

    def sql(query):
        return command(["docker", "exec", "-i", container, "psql", "-X", "-qAt",
                        "-v", "ON_ERROR_STOP=1", "-U", "permora_ci", "-d", "permora_ci"],
                       data=query.encode()).strip()

    def count(query):
        return int(sql(query) or "0")

    def create_client(name, *, app=None, service=False):
        payload = {"clientId": name, "protocol": "openid-connect",
                   "enabled": True, "standardFlowEnabled": False,
                   "implicitFlowEnabled": False, "directAccessGrantsEnabled": False,
                   "fullScopeAllowed": False, "serviceAccountsEnabled": service,
                   "publicClient": False}
        if app:
            payload["attributes"] = {"permora.application": app}
        kc.request("POST", base + "/clients", payload)
        return next(c["id"] for c in kc.request("GET", base + "/clients?clientId=" + name)[1]
                    if c["clientId"] == name)

    def mapped_role(client_id, name, entity_id):
        kc.request("POST", base + "/clients/" + client_id + "/roles", {
            "name": name, "description": "Disposable integration-only fixture",
            "attributes": {"permora.application": [app_id], "permora.entity": [entity_id]}})
        return kc.request("GET", base + "/clients/" + client_id + "/roles/" + name)[1]

    def mapping(kind, keycloak_id, client_id):
        from urllib.parse import quote as url_quote
        return base + "/" + kind + "/" + url_quote(keycloak_id, safe="") + "/role-mappings/clients/" + client_id

    def start_worker(config_path, log_name):
        file = (workspace / log_name).open("wb")
        logs.append(file)
        return subprocess.Popen(["dotnet", str(worker)], cwd=worker.parents[3],
            env=dict(os.environ, PERMORA_CONFIG=str(config_path), DOTNET_ENVIRONMENT="Development"),
            stdout=file, stderr=subprocess.STDOUT)

    def stop_worker(proc):
        if proc and proc.poll() is None:
            proc.terminate()
            try:
                proc.wait(timeout=12)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait(timeout=8)

    try:
        command(["docker", "run", "-d", "--rm", "--name", container,
                 "-p", f"127.0.0.1:{PGPORT}:5432",
                 "-e", "POSTGRES_USER=permora_ci", "-e", "POSTGRES_PASSWORD",
                 "postgres:18"], env=dict(os.environ, POSTGRES_PASSWORD=password), timeout=120)
        postgres_started = True
        for _ in range(60):
            if subprocess.run(["docker", "exec", container, "pg_isready", "-U", "permora_ci"],
                              stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=5).returncode == 0:
                break
            time.sleep(1)
        else:
            raise TimeoutError("Disposable PostgreSQL was not ready")

        # Reuse the FGAP-scoped "limited-mapper" principal already verified by
        # ci-federation.py. A second overlapping Groups.manage permission would
        # produce a 403 under Keycloak FGAP policy resolution. The service
        # identity is intentionally shared by roles/assignments in THIS fixture;
        # separate profiles are covered by the real multiworker test.
        kc.request("PUT", base + "/clients/" + remote_client, {
            "id": remote_client, "clientId": "own-api", "protocol": "openid-connect",
            "enabled": True, "standardFlowEnabled": False,
            "implicitFlowEnabled": False, "directAccessGrantsEnabled": False,
            "fullScopeAllowed": False, "serviceAccountsEnabled": False,
            "attributes": {"permora.application": app_id}})
        kc.request("PUT", base + "/clients/" + remote_client + "/roles/role.reader", {
            "name": "role.reader", "clientRole": True,
            "attributes": {"permora.application": [app_id],
                           "permora.entity": [reader_id]}})
        reader = kc.request("GET", base + "/clients/" + remote_client + "/roles/role.reader")[1]
        supervisor = mapped_role(remote_client, "role.supervisor", supervisor_id)
        emergency = mapped_role(remote_client, "role.emergency", emergency_id)
        observer = mapped_role(remote_client, "role.observer", observer_id)
        reader_kind = "users" if imported else "groups"
        reader_remote = other_user["id"] if imported else reader_group["id"]
        reader_path = mapping(reader_kind, reader_remote, remote_client)
        group_path = mapping("groups", group["id"], remote_client)
        kc.request("POST", reader_path, [reader])
        kc.request("POST", group_path, [emergency, observer])
        kc.request("POST", mapping("groups", group["id"], remote_client), [supervisor])
        check(any(r["id"] == reader["id"] for r in
                  kc.request("GET", reader_path)[1]),
              "Live worker fixture starts with the actual direct/group reader mapping for its LDAP mode")
        check(any(r["id"] == supervisor["id"] for r in
                  kc.request("GET", mapping("groups", group["id"], remote_client))[1]),
              "Live worker fixture starts with real Keycloak group supervisor mapping")

        scoped_file = workspace / ("ci-existing-scoped-" + suffix + ".secret")
        scoped_file.write_text(scoped_secret)
        scoped_file.chmod(0o600)
        profiles = {
            "Roles": {"ClientId": "limited-mapper", "SecretFile": str(scoped_file)},
            "Assignments": {"ClientId": "limited-mapper", "SecretFile": str(scoped_file)}}
        directory_name = "ci-ldap-worker-directory"
        cid = create_client(directory_name, service=True)
        account = kc.request("GET", base + "/clients/" + cid + "/service-account-user")[1]["id"]
        directory_secret = kc.request("GET", base + "/clients/" + cid + "/client-secret")[1]["value"]
        directory_file = workspace / ("ci-worker-directory-" + suffix + ".secret")
        directory_file.write_text(directory_secret)
        directory_file.chmod(0o600)
        profiles["Directory"] = {"ClientId": directory_name, "SecretFile": str(directory_file)}
        management = next(c["id"] for c in kc.request("GET",
            base + "/clients?clientId=realm-management")[1] if c["clientId"] == "realm-management")
        view = kc.request("GET", base + "/clients/" + management + "/roles/view-users")[1]
        kc.request("POST", base + "/users/" + account + "/role-mappings/clients/" + management, [view])
        # With Full Scope Allowed disabled, a service account role must ALSO
        # be in its client's explicit role scope. Otherwise the resulting
        # token lacks view-users and Keycloak GET /users returns HTTP 403.
        kc.request("POST", base + "/clients/" + cid + "/scope-mappings/clients/" + management, [view])
        # Probe the *exact directory credential* used by the real worker.
        # The service may read user lists yet have no access to nested group
        # membership; that must fail the acceptance instead of claiming sync.
        directory_token = kc.request("POST",
            "/realms/" + realm + "/protocol/openid-connect/token",
            {"grant_type": "client_credentials", "client_id": directory_name,
             "client_secret": directory_secret}, admin=False, form=True)[1]["access_token"]
        directory_users = kc.request("GET",
            base + "/users?first=0&max=100&briefRepresentation=false",
            token=directory_token)[1]
        kc.request("GET", base + "/groups?first=0&max=100&briefRepresentation=false",
                   token=directory_token)
        kc.request("GET", base + "/groups/" + group["id"] + "/members?first=0&max=100",
                   token=directory_token)
        kc.request("GET", base + "/groups/" + group["id"] + "/children?first=0&max=100",
                   token=directory_token)
        check(any(u["id"] == other_user["id"] for u in directory_users),
              "Worker directory token sees the actual federated target user in this LDAP mode")
        assignments_token = kc.request("POST",
            "/realms/" + realm + "/protocol/openid-connect/token",
            {"grant_type": "client_credentials", "client_id": "limited-mapper",
             "client_secret": scoped_secret}, admin=False, form=True)[1]["access_token"]
        kc.request("GET", base + "/clients/" + remote_client, token=assignments_token)
        kc.request("GET", mapping("groups", group["id"], remote_client), token=assignments_token)
        status, _, _ = kc.request("PUT", base,
            {"enabled": False}, token=assignments_token, expected=(401, 403))
        check(status in (401, 403), "Same worker mapping credential cannot alter its Keycloak realm")

        config = {
            "ConnectionStrings": {"Portal":
                f"Host=127.0.0.1;Port={PGPORT};Database=permora_ci;Username=permora_ci;Password={password}"},
            "Portal": {"Authority": issuer, "AllowHttpInDevelopment": True, "AdministratorSubjects": []},
            "Keycloak": {"AllowHttpInDevelopment": True, "Realms": {realm: {
                "BaseUrl": proxy_url, "Realm": realm, "Issuer": issuer,
                **profiles, "ProtectedSubjectIds": [], "AllowGroupRoleGrants": False,
                "ApprovedGroupIds": [], "DirectoryProbeUserIds": [subject_user["id"]]}}}}
        path = workspace / ("ci-drift-worker-" + suffix + ".json")
        path.write_text(compact(config))
        path.chmod(0o600)
        command(["dotnet", str(worker), "--migrate"],
                env=dict(os.environ, PERMORA_CONFIG=str(path), DOTNET_ENVIRONMENT="Development"))
        owner_key = subject(issuer, "fixture-owner")
        team = {"id": team_id, "name": "Synthetic directory drift acceptance",
                "revision": 1, "members": [{"subjectKey": owner_key, "name": "Fixture", "role": "Owner"}]}
        app = {"id": app_id, "teamId": team_id, "realmKey": realm,
               "slug": "ldap-worker-app", "name": "LDAPS worker fixture",
               "description": "Disposable integration acceptance only",
               "environment": "Test", "status": "Active", "revision": 1,
               "keycloakClientUuid": remote_client, "keycloakClientId": "own-api",
               "loginClientUuid": None,
               "policy": {"requireApproval": True, "maxGrantDays": 90,
                          "separationRules": [{"firstRoleId": reader_id,
                          "secondRoleId": supervisor_id, "reason": "External membership conflict"}]},
               "permissions": [],
               "roles": [{"id": reader_id, "key": "reader", "name": "Reader",
                          "description": "Fixture", "permissionIds": []},
                         {"id": supervisor_id, "key": "supervisor", "name": "Supervisor",
                          "description": "Fixture", "permissionIds": []},
                         {"id": emergency_id, "key": "emergency", "name": "Emergency",
                          "description": "Fixture", "permissionIds": []},
                         {"id": observer_id, "key": "observer", "name": "Observer",
                          "description": "Fixture", "permissionIds": []}],
               "assignments": [
                   {"id": direct_assignment, "roleId": reader_id,
                    "principalId": stable_id(realm, "user" if imported else "group", reader_remote),
                    "grantedBy": "A" * 64, "grantedAt": timestamp(), "expiresAt": timestamp(7)},
                   {"id": group_assignment, "roleId": supervisor_id,
                    "principalId": stable_id(realm, "group", group["id"]),
                    "grantedBy": "A" * 64, "grantedAt": timestamp(), "expiresAt": timestamp(7)},
                   {"id": emergency_assignment, "roleId": emergency_id,
                    "principalId": stable_id(realm, "group", group["id"]),
                    "grantedBy": "A" * 64, "grantedAt": timestamp(), "expiresAt": timestamp(7)},
                   {"id": observer_assignment, "roleId": observer_id,
                    "principalId": stable_id(realm, "group", group["id"]),
                    "grantedBy": "A" * 64, "grantedAt": timestamp(), "expiresAt": timestamp(7)}],
               "iamApproval": None}
        sql("INSERT INTO teams(id,revision,document) VALUES (" +
            quote(team_id) + ",1," + quote(compact(team)) + "::jsonb);")
        sql("INSERT INTO applications(id,team_id,realm_key,slug,environment,revision,document) VALUES (" +
            ",".join(quote(app[k]) for k in ("id", "teamId", "realmKey", "slug", "environment")) +
            ",1," + quote(compact(app)) + "::jsonb);")

        # First worker start: establish a genuine persisted baseline snapshot
        # with no conflict. The second worker start must then detect *changed*
        # external LDAP membership, not merely an injected portal fixture.
        process = start_worker(path, "ldap-worker-first-" + suffix + ".log")
        group_guid = stable_id(realm, "group", group["id"])
        deadline = time.monotonic() + 100
        while time.monotonic() < deadline:
            if process.poll() is not None:
                raise RuntimeError("Worker exited before the baseline directory sync")
            if count("SELECT count(*) FROM directory WHERE id=" + quote(group_guid)) == 1:
                break
            time.sleep(1)
        if count("SELECT count(*) FROM directory WHERE id=" + quote(group_guid)) != 1:
            print("CI-DIAG: persisted directory rows=" +
                  str(count("SELECT count(*) FROM directory")) +
                  " groupRows=" + str(count("SELECT count(*) FROM directory WHERE document->>'kind'='Group'")),
                  flush=True)
            # Diagnostics are bounded and scrubbed; never expose client secrets.
            import re
            for log in logs:
                log.flush()
                diagnostic = Path(log.name).read_text(errors="replace")
                for sensitive in (password, scoped_secret, directory_secret):
                    diagnostic = diagnostic.replace(sensitive, "[REDACTED]")
                diagnostic = re.sub(r"[A-Za-z0-9_-]{15,}\.[A-Za-z0-9_-]{15,}\.[A-Za-z0-9_-]{15,}",
                                    "[REDACTED-TOKEN]", diagnostic)
                for line in diagnostic.splitlines()[-35:]:
                    print("CI-WORKER-DIAG:", line[:350], flush=True)
        check(count("SELECT count(*) FROM directory WHERE id=" + quote(group_guid)) == 1,
              "Actual worker persisted the Keycloak/LDAPS group snapshot into PostgreSQL")
        check(count("SELECT count(*) FROM operations") == 0,
              "Initial disjoint LDAP memberships produce no speculative revocation")
        stop_worker(process)
        process = None

        # The Portal API shares the real DB but has no Admin REST credentials.
        # Explicit loopback Development authentication is deliberate: genuine
        # OIDC/MFA has its own suite and is NOT claimed by this fixture.
        api = worker.parents[4] / "Permora.Api/bin/Release/net10.0/Permora.Api.dll"
        if not api.is_file():
            raise RuntimeError("Build the actual Release API before the joined fixture")
        api_config = {
            "ConnectionStrings": config["ConnectionStrings"],
            "Portal": {"Authority": issuer, "PublicOrigin": "http://127.0.0.1:15086",
                       "AllowHttpInDevelopment": True, "EnableDevelopmentLogin": True,
                       "DevelopmentSubject": "fixture-owner", "AdministratorSubjects": [],
                       "DataProtectionPath": str(workspace / ("ci-api-keys-" + suffix))},
            "Keycloak": {"Realms": {realm: {"AllowGroupRoleGrants": False, "ApprovedGroupIds": []}}}}
        api_path = workspace / ("ci-federation-api-" + suffix + ".json")
        api_path.write_text(compact(api_config))
        api_path.chmod(0o600)
        api_log = (workspace / ("ci-federation-api-" + suffix + ".log")).open("wb")
        logs.append(api_log)
        api_process = subprocess.Popen(["dotnet", str(api)], cwd=api.parents[4],
            env=dict(os.environ, PERMORA_CONFIG=str(api_path), ASPNETCORE_ENVIRONMENT="Development",
                     ASPNETCORE_URLS="http://127.0.0.1:15086"), stdout=api_log, stderr=subprocess.STDOUT)
        jar = http.cookiejar.CookieJar()
        opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))

        def portal_request(method, route, body=None):
            headers = {"Accept": "application/json", "Content-Type": "application/json",
                       "Idempotency-Key": "ci-denied-group-" + suffix}
            token = next((c.value for c in jar if c.name == "XSRF-TOKEN"), None)
            if token: headers["X-XSRF-TOKEN"] = token
            request = urllib.request.Request("http://127.0.0.1:15086" + route, method=method,
                headers=headers, data=compact(body).encode() if body is not None else None)
            try:
                response = opener.open(request, timeout=6)
            except urllib.error.HTTPError as error:
                response = error
            with response:
                raw, status = response.read(1024 * 1024), response.status
            return status, json.loads(raw) if raw else None

        def api_ready():
            if api_process.poll() is not None:
                raise RuntimeError("Joined fixture API exited before readiness")
            try:
                return portal_request("GET", "/health/live")[0] == 200
            except OSError:
                return False
        wait_for(api_ready, "Actual Portal API did not become live", 40)
        status, session = portal_request("GET", "/bff/session")
        check(status == 200 and session["subjectKey"] == owner_key and session["development"],
              "Actual Portal API authenticates the explicit loopback-only fixture owner")
        app_route = "/api/v1/applications/" + app_id
        status, denied = portal_request("POST", app_route + "/changes", {
            "expectedRevision": 1, "change": {"kind": "GrantAccess", "roleId": observer_id,
                "principalId": stable_id(realm, "group", reader_group["id"]),
                "expiresAt": timestamp(1), "reason": "Must be denied without a queue entry"}})
        check(status == 403 and denied["title"] == "group_mapping_disabled"
              and count("SELECT count(*) FROM operations") == 0 and faults.grants == 0,
              "Disabled group grants return real Portal HTTP 403 without queue or remote write")

        baseline = sql("SELECT jsonb_agg(document ORDER BY id)::text FROM directory")
        kc.request("POST", base + "/clear-user-cache")
        ldap_available(False)
        process = start_worker(path, "ldap-worker-visibility-" + suffix + ".log")
        wait_for(worker_reported_sync_failure, "Actual LDAP visibility loss was not observed", 70)
        stop_worker(process)
        process = None
        ldap_available(True)
        check(sql("SELECT jsonb_agg(document ORDER BY id)::text FROM directory") == baseline
              and count("SELECT count(*) FROM operations") == 0 and faults.deletes == 0,
              "Real LDAPS timeout preserves the last complete snapshot and never invents a revoke")

        # Corrupt one persisted identity with two candidate remote targets. The
        # simultaneous duplicated HTTP page prevents refresh from hiding this
        # injected DB-corruption scenario. Neither candidate may be guessed.
        duplicate_row = str(uuid.uuid4())
        sql("INSERT INTO directory(id,realm_key,name,document) SELECT " + quote(duplicate_row) +
            ",realm_key,name,jsonb_set(document,'{keycloakId}'," +
            quote(compact(reader_group["id"])) + "::jsonb) FROM directory WHERE id=" + quote(group_guid))
        app["assignments"][2]["expiresAt"] = timestamp(-1)
        app["revision"] = 2
        sql("UPDATE applications SET revision=2,document=" + quote(compact(app)) +
            "::jsonb WHERE id=" + quote(app_id))
        with faults.lock: faults.duplicate_users = True
        process = start_worker(path, "ldap-worker-ambiguous-" + suffix + ".log")
        wait_for(lambda: count("SELECT count(*) FROM operations WHERE status='Conflict' "
            "AND document->>'errorCode'='ambiguous_directory_identity'") == 1,
            "Ambiguous identity did not block the actual emergency revoke")
        stop_worker(process)
        process = None
        check(faults.duplicates > 0 and worker_reported_sync_failure() and faults.deletes == 0,
              "Duplicate directory page is rejected; ambiguous persisted ID causes no remote deletion")
        check(count("SELECT count(*) FROM audit_events WHERE document->>'action'='change.blocked'") == 1,
              "Actual ambiguous revoke records Conflict and an append-only blocked audit event")
        check(any(r["id"] == emergency["id"] for r in kc.request("GET", group_path)[1]),
              "Independent Keycloak read confirms ambiguous targets were not mutated")

        sql("DELETE FROM directory WHERE id=" + quote(duplicate_row))
        with faults.lock:
            faults.duplicate_users = False
            faults.offline = True
            faults.drop_path = group_path
        operation_id = sql("SELECT id FROM operations")
        status, retried = portal_request("POST", app_route + "/operations/" + operation_id + "/retry")
        check(status == 200 and retried["status"] == "Queued",
              "Real Portal owner explicitly retries after resolving the ambiguous identity")
        process = start_worker(path, "ldap-worker-outage-" + suffix + ".log")
        wait_for(lambda: count("SELECT count(*) FROM operations WHERE status='Queued' "
            "AND document->>'errorCode'='connector_unavailable'") == 1,
            "Keycloak outage did not leave a retryable emergency revoke")
        check(faults.outages > 0 and faults.deletes == 0,
              "Actual worker retains the emergency revoke across injected Keycloak HTTP 503")
        with faults.lock: faults.offline = False
        wait_for(faults.committed.is_set, "Keycloak did not commit the emergency deletion", 80)
        # Lost response + abrupt process termination before local commit.
        process.kill()
        process.wait(timeout=10)
        process = None
        faults.release.set()
        check(not any(r["id"] == emergency["id"] for r in kc.request("GET", group_path)[1])
              and count("SELECT count(*) FROM operations WHERE status='Applied'") == 0,
              "Remote DELETE committed while the killed worker could not record local success")
        process = start_worker(path, "ldap-worker-recover-" + suffix + ".log")
        wait_for(lambda: count("SELECT count(*) FROM operations WHERE status='Applied'") == 1,
                 "Restart did not verify and finish the committed emergency revoke")
        check(faults.deletes == 1 and faults.grants == 0,
              "Restart reads the real postcondition without a duplicate DELETE or any grant")
        surviving_roles = {r["id"] for r in kc.request("GET", group_path)[1]}
        check({supervisor["id"], observer["id"]}.issubset(surviving_roles),
              "Emergency deletion preserves the actual unrelated supervisor and observer mappings")
        check(count("SELECT count(*) FROM audit_events WHERE document->>'action'='change.retry_requested'") == 1
              and count("SELECT count(*) FROM audit_events WHERE document->>'action'='change.retry'") >= 1,
              "Owner retry and network retry are represented in the actual audit chain")
        stop_worker(process)
        process = None

        change_group_members("federated0", "federated1")
        kc.request("POST", base + "/clear-user-cache")
        effective = mapping("users", other_user["id"], remote_client) + "/composite"
        effective_roles = {r["id"] for r in kc.request("GET", effective)[1]}
        check({reader["id"], supervisor["id"]}.issubset(effective_roles),
              "External LDAP join creates a real effective Keycloak SoD conflict")
        start = time.monotonic()
        sod_revocations = 1 if imported else 2
        total_revocations = 1 + sod_revocations
        process = start_worker(path, "ldap-worker-drift-" + suffix + ".log")
        deadline = start + 150
        while time.monotonic() < deadline:
            if process.poll() is not None:
                raise RuntimeError("Worker exited while reconciling external LDAPS membership")
            if count("SELECT count(*) FROM operations WHERE status='Applied'") == total_revocations:
                break
            time.sleep(1)
        elapsed = round(time.monotonic() - start, 2)
        check(count("SELECT count(*) FROM operations WHERE status='Applied'") == total_revocations,
              "Real worker applied the expected one/two targeted group SoD revocations after LDAP drift")
        check(count("SELECT count(*) FROM operations") == total_revocations,
              "External LDAP drift generates exactly the deterministic group revocations plus the emergency revoke")
        check(count("SELECT count(*) FROM operations WHERE document->>'systemRevocation'='true'") == total_revocations,
              "Revocation was executed under the restricted internal lifecycle path")
        check(count("SELECT count(*) FROM audit_events WHERE document->>'action'='revocation.queued'") == total_revocations,
              "Append-only audit records the system-selected group revocation")
        check(count("SELECT count(*) FROM audit_events WHERE document->>'action'='change.applied'") == total_revocations,
              "Append-only audit records the verified Keycloak mapping deletion")
        state = sql("SELECT document::text FROM applications WHERE id=" + quote(app_id))
        current = json.loads(state)
        expected_survivors = {observer_assignment, direct_assignment} if imported else {observer_assignment}
        check({a["id"] for a in current["assignments"]} == expected_survivors,
              "Only conflicted/expired assignments are removed; unrelated assignments survive")
        check(not any(r["id"] == supervisor["id"] for r in
                      kc.request("GET", mapping("groups", group["id"], remote_client))[1]),
              "Actual Keycloak group role mapping is absent after worker reconciliation")
        check(any(r["id"] == reader["id"] for r in kc.request("GET", reader_path)[1]) == imported,
              "Direct reader survives in imported mode; both conflicting groups are revoked in non-imported mode")
        check(any(r["id"] == observer["id"] for r in kc.request("GET", group_path)[1]),
              "Unrelated observer group mapping survives targeted SoD remediation")
        check(not any(r["id"] == supervisor["id"] for r in kc.request("GET", effective)[1]),
              "Previously conflicting effective Keycloak group right is removed")
        change_group_members("federated0")
        kc.request("POST", base + "/clear-user-cache")
        change_group_members("federated0", "federated1")
        kc.request("POST", base + "/clear-user-cache")
        check(not any(r["id"] == supervisor["id"] for r in kc.request("GET", effective)[1]),
              "External membership restoration cannot replay the revoked group grant")
        stop_worker(process)
        restart_at = timestamp()
        process = start_worker(path, "ldap-worker-final-restart-" + suffix + ".log")
        wait_for(lambda: count("SELECT count(*) FROM directory WHERE id=" + quote(group_guid) +
            " AND (document->>'syncedAt')::timestamptz > " + quote(restart_at) + "::timestamptz") == 1,
            "Final restart did not refresh the real directory")
        check(count("SELECT count(*) FROM operations") == total_revocations
              and faults.deletes == total_revocations and faults.grants == 0,
              "Membership loss/rejoin and another restart never regrant or duplicate a remote revoke")
        final_effective = {r["id"] for r in kc.request("GET", effective)[1]}
        check(supervisor["id"] not in final_effective and observer["id"] in final_effective
              and (reader["id"] in final_effective) == imported,
              "Independent final effective-role readback preserves unrelated rights after rejoin/restart")
        print(compact({"scope": "real-LDAPS-Keycloak-Postgres-Permora.Worker",
                       "observedAt": timestamp(),
                       "sourceCommit": command(["git", "-C", str(worker.parents[5]), "rev-parse", "HEAD"]).strip(),
                       "keycloakVersion": "26.8.1", "postgresVersion": "18",
                       "dotnetSdk": command(["dotnet", "--version"]).strip(),
                       "directoryMode": "imported-read-only" if imported else "non-imported-read-only",
                       "sodRevocationsApplied": sod_revocations, "emergencyRevocationsApplied": 1,
                       "revocationsApplied": total_revocations,
                       "ldapVisibilityLoss": True, "ambiguousIdentityBlocked": True,
                       "keycloak503": True, "postCommitReplyLossAndRestart": True,
                       "portalAuthentication": "explicit-loopback-development-not-OIDC",
                       "groupGrantsEnabled": False, "remoteGrantCalls": faults.grants,
                       "revokeLatencySeconds": elapsed, "approvedProductionSlo": None,
                       "multiHost": False, "microsoftAD": False,
                       "groupsManagePrivilegeAccepted": False,
                       "productionApproval": False}), flush=True)
    finally:
        ldap_available(True)
        faults.release.set()
        stop_worker(process)
        stop_worker(api_process)
        proxy.shutdown()
        proxy.server_close()
        proxy_thread.join(timeout=5)
        for log in logs:
            log.close()
        if postgres_started:
            subprocess.run(["docker", "rm", "-f", container],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=40)
