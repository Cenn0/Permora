#!/usr/bin/env python3
"""Disposable live LDAP -> Keycloak -> PostgreSQL -> Permora.Worker acceptance.

Invoked ONLY from the isolated ci-federation.py runner after its real Keycloak
and certificate-validated OpenLDAP are ready. The application is synthetic.
No corporate identity, durable database, backup, token or secret is uploaded.
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone
import hashlib
import json
import os
from pathlib import Path
import secrets
import subprocess
import time
import uuid

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
                          remote_client, scoped_secret):
    """Use the existing imported-LDAP fixture and verify a real worker revoke."""
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
    postgres_started = False
    process = None
    logs = []

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
        return base + "/" + kind + "/" + keycloak_id + "/role-mappings/clients/" + client_id

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
        kc.request("POST", mapping("users", other_user["id"], remote_client), [reader])
        kc.request("POST", mapping("groups", group["id"], remote_client), [supervisor])
        check(any(r["id"] == reader["id"] for r in
                  kc.request("GET", mapping("users", other_user["id"], remote_client))[1]),
              "Live worker fixture starts with real Keycloak direct reader mapping")
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
                "BaseUrl": KEYCLOAK, "Realm": realm, "Issuer": issuer,
                **profiles, "ProtectedSubjectIds": [], "AllowGroupRoleGrants": False,
                "ApprovedGroupIds": []}}}}
        path = workspace / ("ci-drift-worker-" + suffix + ".json")
        path.write_text(compact(config))
        path.chmod(0o600)
        command(["dotnet", str(worker), "--migrate"],
                env=dict(os.environ, PERMORA_CONFIG=str(path), DOTNET_ENVIRONMENT="Development"))
        team = {"id": team_id, "name": "Synthetic directory drift acceptance",
                "revision": 1, "members": [{"subjectKey": "A" * 64, "name": "Fixture", "role": "Owner"}]}
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
                          "description": "Fixture", "permissionIds": []}],
               "assignments": [
                   {"id": direct_assignment, "roleId": reader_id,
                    "principalId": stable_id(realm, "user", other_user["id"]),
                    "grantedBy": "A" * 64, "grantedAt": timestamp(), "expiresAt": timestamp(7)},
                   {"id": group_assignment, "roleId": supervisor_id,
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
        check(count("SELECT count(*) FROM directory WHERE id=" + quote(group_guid)) == 1,
              "Actual worker persisted the Keycloak/LDAPS group snapshot into PostgreSQL")
        check(count("SELECT count(*) FROM operations") == 0,
              "Initial disjoint LDAP memberships produce no speculative revocation")
        stop_worker(process)
        process = None

        change_group_members("federated0", "federated1")
        kc.request("POST", base + "/clear-user-cache")
        effective = mapping("users", other_user["id"], remote_client) + "/composite"
        check(any(r["id"] == supervisor["id"] for r in kc.request("GET", effective)[1]),
              "External LDAP join creates a real effective Keycloak SoD conflict")
        start = time.monotonic()
        process = start_worker(path, "ldap-worker-drift-" + suffix + ".log")
        deadline = start + 150
        while time.monotonic() < deadline:
            if process.poll() is not None:
                raise RuntimeError("Worker exited while reconciling external LDAPS membership")
            if count("SELECT count(*) FROM operations WHERE status='Applied'") == 1:
                break
            time.sleep(1)
        elapsed = round(time.monotonic() - start, 2)
        check(count("SELECT count(*) FROM operations WHERE status='Applied'") == 1,
              "Real worker queued and applied the targeted group revocation after LDAP drift")
        check(count("SELECT count(*) FROM operations") == 1,
              "External LDAP drift generates exactly one system revocation")
        check(count("SELECT count(*) FROM operations WHERE document->>'systemRevocation'='true'") == 1,
              "Revocation was executed under the restricted internal lifecycle path")
        check(count("SELECT count(*) FROM audit_events WHERE document->>'action'='revocation.queued'") == 1,
              "Append-only audit records the system-selected group revocation")
        check(count("SELECT count(*) FROM audit_events WHERE document->>'action'='change.applied'") == 1,
              "Append-only audit records the verified Keycloak mapping deletion")
        state = sql("SELECT document::text FROM applications WHERE id=" + quote(app_id))
        current = json.loads(state)
        check([a["id"] for a in current["assignments"]] == [direct_assignment],
              "Only conflicted group mapping is removed from the portal, direct user mapping survives")
        check(not any(r["id"] == supervisor["id"] for r in
                      kc.request("GET", mapping("groups", group["id"], remote_client))[1]),
              "Actual Keycloak group role mapping is absent after worker reconciliation")
        check(any(r["id"] == reader["id"] for r in
                  kc.request("GET", mapping("users", other_user["id"], remote_client))[1]),
              "Unrelated direct user role survives a group SoD remediation")
        check(not any(r["id"] == supervisor["id"] for r in kc.request("GET", effective)[1]),
              "Previously conflicting effective Keycloak group right is removed")
        change_group_members("federated0")
        kc.request("POST", base + "/clear-user-cache")
        change_group_members("federated0", "federated1")
        kc.request("POST", base + "/clear-user-cache")
        check(not any(r["id"] == supervisor["id"] for r in kc.request("GET", effective)[1]),
              "External membership restoration cannot replay the revoked group grant")
        print(compact({"scope": "real-LDAPS-Keycloak-Postgres-Permora.Worker",
                       "directoryMode": "imported-read-only", "revocationsApplied": 1,
                       "revokeLatencySeconds": elapsed, "approvedProductionSlo": None,
                       "multiHost": False, "microsoftAD": False,
                       "groupsManagePrivilegeAccepted": False,
                       "productionApproval": False}), flush=True)
    finally:
        stop_worker(process)
        for log in logs:
            log.close()
        if postgres_started:
            subprocess.run(["docker", "rm", "-f", container],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=40)
