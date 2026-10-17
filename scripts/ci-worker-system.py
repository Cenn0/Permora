#!/usr/bin/env python3
"""Real two-process worker/Keycloak/PostgreSQL acceptance on an isolated runner.

Creates its own containers/databases; never connects to a user-supplied database.
Injects a 503 outage and a lost DELETE response after Keycloak's real commit,
kills the responsible worker, then verifies survivor progress and remote state.
Quiesces writers and restores both databases into new, empty test databases.
No production secrets, exported tokens or backups are persisted as CI artifacts.
"""
from __future__ import annotations
import base64
from datetime import datetime, timedelta, timezone
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
KEYCLOAK = 'http://127.0.0.1:18085'
PROXY = 'http://127.0.0.1:19085'
PGPORT = 15439
APPLICATIONS = 4
USERS = 8


def check(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)
    print('PASS:', message, flush=True)


def execute(args: list[str], *, data: bytes | None = None, env=None, timeout=180) -> bytes:
    completed = subprocess.run(args, input=data, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               env=env, timeout=timeout, check=False)
    if completed.returncode:
        # All request bodies, credentials, dumps and SQL remain private to this process.
        raise RuntimeError(f'{Path(args[0]).name} command failed with exit {completed.returncode}')
    return completed.stdout


def compact(value) -> str:
    return json.dumps(value, separators=(',', ':'), ensure_ascii=True)


def stable_id(realm: str, kind: str, remote_id: str) -> str:
    # Matches .NET Guid(byte[]) and Wire.Serialize(string[]) exactly.
    return str(uuid.UUID(bytes_le=hashlib.sha256(compact([realm, kind, remote_id]).encode()).digest()[:16]))


def quote(value) -> str:
    return "'" + str(value).replace("'", "''") + "'"


def timestamp(minutes=0):
    return (datetime.now(timezone.utc) + timedelta(minutes=minutes)).isoformat()


class Admin:
    def __init__(self, password: str):
        self.password = password
        self.token = ''
        self.until = 0.0

    def request(self, method, path, body=None, *, token=None, admin=True, form=False, expected=(200, 201, 204)):
        if admin and token is None:
            if time.monotonic() >= self.until:
                response = self.request('POST', '/realms/master/protocol/openid-connect/token',
                    {'grant_type': 'password', 'client_id': 'admin-cli', 'username': 'ci-admin',
                     'password': self.password}, admin=False, form=True)
                self.token, self.until = response['access_token'], time.monotonic() + 35
            token = self.token
        headers = {'Accept': 'application/json'}
        if token:
            headers['Authorization'] = 'Bearer ' + token
        data = None
        if body is not None:
            data = (urllib.parse.urlencode(body) if form else compact(body)).encode()
            headers['Content-Type'] = 'application/x-www-form-urlencoded' if form else 'application/json'
        req = urllib.request.Request(KEYCLOAK + path, data=data, headers=headers, method=method)
        try:
            response = urllib.request.urlopen(req, timeout=15)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            raw, code = response.read(4 * 1024 * 1024), response.status
        if code not in expected:
            raise AssertionError(f'Keycloak {method} {path.split("?")[0]} returned HTTP {code}')
        return json.loads(raw) if raw else None

    def wait(self):
        for _ in range(100):
            try:
                self.request('GET', '/realms/master/.well-known/openid-configuration', admin=False)
                return
            except (OSError, AssertionError, ValueError):
                time.sleep(2)
        raise TimeoutError('Own Keycloak container failed to start')


class Faults:
    def __init__(self):
        self.lock = threading.Lock()
        self.offline = True
        self.outages = 0
        self.remote_deletes = 0
        self.victim = None
        self.committed = threading.Event()
        self.release = threading.Event()


class FaultProxy(BaseHTTPRequestHandler):
    faults: Faults
    protocol_version = 'HTTP/1.1'

    def log_message(self, *_):
        pass

    def do_GET(self):
        self.forward()

    def do_POST(self):
        self.forward()

    def do_PUT(self):
        self.forward()

    def do_DELETE(self):
        self.forward()

    def forward(self):
        length = int(self.headers.get('Content-Length', '0'))
        if length > 1024 * 1024:
            self.send_error(413)
            return
        body = self.rfile.read(length) if length else None
        with self.faults.lock:
            offline = self.faults.offline
            if offline:
                self.faults.outages += 1
        if offline:
            self.send_response(503)
            self.send_header('Content-Length', '0')
            self.end_headers()
            return
        headers = {key: value for key, value in self.headers.items()
                   if key.lower() in ('accept', 'content-type', 'authorization')}
        try:
            request = urllib.request.Request(KEYCLOAK + self.path, data=body,
                                             headers=headers, method=self.command)
            try:
                response = urllib.request.urlopen(request, timeout=20)
            except urllib.error.HTTPError as error:
                response = error
            with response:
                status, content = response.status, response.read(4 * 1024 * 1024)
                content_type = response.headers.get('Content-Type', 'application/json')
            lose_response = False
            if self.command == 'DELETE' and '/role-mappings/clients/' in self.path and status == 204:
                # JWT decoding is used ONLY to identify a disposable test process to kill.
                # Keycloak, not this proxy, validates the bearer token and authorization.
                raw_token = self.headers.get('Authorization', '').split(' ', 1)[-1]
                middle = raw_token.split('.')[1]
                client = json.loads(base64.urlsafe_b64decode(middle + '=' * (-len(middle) % 4))).get('azp')
                with self.faults.lock:
                    self.faults.remote_deletes += 1
                    if self.faults.victim is None and client in ('worker-a-assignments', 'worker-b-assignments'):
                        self.faults.victim = client.split('-assignments')[0]
                        lose_response = True
                        self.faults.committed.set()
            if lose_response:
                # The REAL Keycloak DELETE has committed. Hold the response while the
                # parent kills that exact worker before it can commit local success.
                self.faults.release.wait(timeout=25)
                self.close_connection = True
                try:
                    self.connection.shutdown(socket.SHUT_RDWR)
                except OSError:
                    pass
                return
            self.send_response(status)
            self.send_header('Content-Type', content_type)
            self.send_header('Content-Length', str(len(content)))
            self.end_headers()
            if content:
                self.wfile.write(content)
        except (OSError, ValueError, IndexError):
            self.close_connection = True


def run():
    if os.environ.get('GITHUB_ACTIONS') != 'true' or os.environ.get('PERMORA_SYSTEM_TESTS') != 'isolated':
        raise SystemExit('Only an explicitly isolated runner may execute this destructive fixture.')
    worker_dll = ROOT / 'src/Permora.Worker/bin/Release/net10.0/Permora.Worker.dll'
    if not worker_dll.is_file():
        raise SystemExit('Build the actual Release worker first.')
    suffix = uuid.uuid4().hex[:10]
    pg = 'permora-system-pg-' + suffix
    kc_name = 'permora-system-kc-' + suffix
    realm = 'permora-system-' + suffix
    base = '/admin/realms/' + realm
    issuer = KEYCLOAK + '/realms/' + realm
    pg_secret, kc_secret = secrets.token_urlsafe(32), secrets.token_urlsafe(32)
    pg_env = dict(os.environ, POSTGRES_PASSWORD=pg_secret)
    started_pg = False
    started_kc = False
    processes: dict[str, subprocess.Popen] = {}
    logs = []
    faults = Faults()
    FaultProxy.faults = faults
    proxy = None
    with tempfile.TemporaryDirectory(prefix='permora-worker-system-') as temp:
        directory = Path(temp)
        directory.chmod(0o700)

        def sql(database: str, text: str) -> str:
            if database not in ('postgres', 'permora_test_portal', 'permora_test_keycloak',
                                'permora_test_portal_restore', 'permora_test_keycloak_restore'):
                raise ValueError('Unapproved fixture database')
            return execute(['docker', 'exec', '-i', pg, 'psql', '-U', 'permora_ci', '-d', database,
                            '-X', '-qAt', '-v', 'ON_ERROR_STOP=1'], data=text.encode()).decode().strip()

        def count(text):
            return int(sql('permora_test_portal', text) or '0')

        def start_keycloak(database):
            nonlocal started_kc
            env = dict(os.environ, KC_DB_PASSWORD=pg_secret, KC_BOOTSTRAP_ADMIN_PASSWORD=kc_secret)
            execute(['docker', 'run', '-d', '--rm', '--name', kc_name, '--network', 'host',
                '-e', 'KC_DB=postgres', '-e', 'KC_DB_URL=jdbc:postgresql://127.0.0.1:' + str(PGPORT) + '/' + database,
                '-e', 'KC_DB_USERNAME=permora_ci', '-e', 'KC_DB_PASSWORD',
                '-e', 'KC_BOOTSTRAP_ADMIN_USERNAME=ci-admin', '-e', 'KC_BOOTSTRAP_ADMIN_PASSWORD',
                'quay.io/keycloak/keycloak:26.8.1', 'start-dev', '--http-host=127.0.0.1', '--http-port=18085'], env=env)
            started_kc = True
            admin = Admin(kc_secret)
            admin.wait()
            return admin

        def stop_workers():
            for process in processes.values():
                if process.poll() is None:
                    process.terminate()
                    try:
                        process.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=5)

        try:
            execute(['docker', 'run', '-d', '--rm', '--name', pg, '-p', f'127.0.0.1:{PGPORT}:5432',
                     '-e', 'POSTGRES_USER=permora_ci', '-e', 'POSTGRES_PASSWORD', 'postgres:18'], env=pg_env)
            started_pg = True
            for _ in range(60):
                ready = subprocess.run(['docker', 'exec', pg, 'pg_isready', '-U', 'permora_ci'],
                                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=5)
                if ready.returncode == 0:
                    break
                time.sleep(1)
            else:
                raise TimeoutError('Own PostgreSQL container did not become ready')
            sql('postgres', 'CREATE DATABASE permora_test_portal; CREATE DATABASE permora_test_keycloak;')
            kc = start_keycloak('permora_test_keycloak')
            kc.request('POST', '/admin/realms', {'realm': realm, 'enabled': True, 'adminPermissionsEnabled': True})

            def client(name, app=None, service=False):
                payload = {'clientId': name, 'protocol': 'openid-connect', 'enabled': True,
                    'standardFlowEnabled': False, 'implicitFlowEnabled': False,
                    'directAccessGrantsEnabled': False, 'fullScopeAllowed': False,
                    'serviceAccountsEnabled': service, 'publicClient': False}
                if app:
                    payload['attributes'] = {'permora.application': app}
                kc.request('POST', base + '/clients', payload)
                return next(c['id'] for c in kc.request('GET', base + '/clients?clientId=' + name) if c['clientId'] == name)

            users = []
            for i in range(USERS):
                username = 'worker-fixture-' + str(i)
                kc.request('POST', base + '/users', {'username': username, 'enabled': True,
                    'firstName': 'Worker', 'lastName': 'Fixture', 'email': username + '@permora.invalid',
                    'emailVerified': True})
                users.append(next(u for u in kc.request('GET', base + '/users?username=' + username)
                                  if u['username'] == username))
            team_id = str(uuid.uuid4())
            team = {'id': team_id, 'name': 'Isolated system test team', 'revision': 1,
                    'members': [{'subjectKey': 'A' * 64, 'name': 'Fixture owner', 'role': 'Owner'}]}
            applications = []
            remote_clients = []
            for i in range(APPLICATIONS):
                app_id, role_id = str(uuid.uuid4()), str(uuid.uuid4())
                name = 'ci-owned-api-' + str(i)
                cid = client(name, app_id)
                kc.request('POST', base + '/clients/' + cid + '/roles', {'name': 'role.reader',
                    'description': 'Fixture reader', 'attributes':
                    {'permora.application': [app_id], 'permora.entity': [role_id]}})
                role = kc.request('GET', base + '/clients/' + cid + '/roles/role.reader')
                assignments = []
                for user in users:
                    kc.request('POST', base + '/users/' + user['id'] + '/role-mappings/clients/' + cid, [role])
                    assignments.append({'id': str(uuid.uuid4()), 'roleId': role_id,
                        'principalId': stable_id(realm, 'user', user['id']), 'grantedBy': 'A' * 64,
                        'grantedAt': timestamp(-60), 'expiresAt': timestamp(-1)})
                applications.append({'id': app_id, 'teamId': team_id, 'realmKey': realm, 'slug': 'app-' + str(i),
                    'name': 'Fixture app ' + str(i), 'description': 'Real worker fault test', 'environment': 'Test',
                    'status': 'Active', 'revision': 1, 'keycloakClientUuid': cid, 'keycloakClientId': name,
                    'loginClientUuid': None, 'policy': {'requireApproval': True, 'maxGrantDays': 90, 'separationRules': []},
                    'permissions': [], 'roles': [{'id': role_id, 'key': 'reader', 'name': 'Reader',
                    'description': 'Fixture reader', 'permissionIds': []}], 'assignments': assignments, 'iamApproval': None})
                remote_clients.append(cid)

            authz_client = next(c['id'] for c in kc.request('GET', base + '/clients?clientId=admin-permissions')
                                if c['clientId'] == 'admin-permissions')
            authz = base + '/clients/' + authz_client + '/authz/resource-server'
            management = next(c['id'] for c in kc.request('GET', base + '/clients?clientId=realm-management')
                              if c['clientId'] == 'realm-management')
            configurations = {}
            for worker in ('worker-a', 'worker-b'):
                profiles = {}
                for profile in ('roles', 'assignments', 'directory'):
                    account_name = worker + '-' + profile
                    cid = client(account_name, service=True)
                    account = kc.request('GET', base + '/clients/' + cid + '/service-account-user')['id']
                    secret = kc.request('GET', base + '/clients/' + cid + '/client-secret')['value']
                    secret_path = directory / (account_name + '.secret')
                    secret_path.write_text(secret)
                    secret_path.chmod(0o600)
                    profiles[profile.title()] = {'ClientId': account_name, 'SecretFile': str(secret_path)}
                    if profile == 'directory':
                        read_role = kc.request('GET', base + '/clients/' + management + '/roles/view-users')
                        kc.request('POST', base + '/users/' + account + '/role-mappings/clients/' + management, [read_role])
                    else:
                        policy = account_name + '-policy'
                        kc.request('POST', authz + '/policy/user', {'name': policy, 'logic': 'POSITIVE', 'users': [account]})
                        kc.request('POST', authz + '/permission/scope', {'name': policy + '-clients',
                            'resourceType': 'Clients', 'resources': remote_clients,
                            'scopes': ['view'] if profile == 'roles' else ['view', 'map-roles'], 'policies': [policy]})
                        if profile == 'assignments':
                            kc.request('POST', authz + '/permission/scope', {'name': policy + '-users',
                                'resourceType': 'Users', 'resources': [u['id'] for u in users],
                                'scopes': ['view', 'map-roles'], 'policies': [policy]})
                connection = f'Host=127.0.0.1;Port={PGPORT};Database=permora_test_portal;Username=permora_ci;Password={pg_secret}'
                configurations[worker] = {'ConnectionStrings': {'Portal': connection},
                    'Portal': {'Authority': issuer, 'AllowHttpInDevelopment': True, 'AdministratorSubjects': []},
                    'Keycloak': {'AllowHttpInDevelopment': True, 'Realms': {realm: {
                        'BaseUrl': PROXY, 'Realm': realm, 'Issuer': issuer, **profiles, 'ProtectedSubjectIds': []}}}}
            config = directory / 'migrate.json'
            config.write_text(compact(configurations['worker-a']))
            config.chmod(0o600)
            env = dict(os.environ, PERMORA_CONFIG=str(config), DOTNET_ENVIRONMENT='Development')
            execute(['dotnet', str(worker_dll), '--migrate'], env=env, timeout=60)
            sql('permora_test_portal', 'INSERT INTO teams(id,revision,document) VALUES (' +
                quote(team_id) + ',1,' + quote(compact(team)) + '::jsonb);')
            for app in applications:
                sql('permora_test_portal', 'INSERT INTO applications(id,team_id,realm_key,slug,environment,revision,document) VALUES (' +
                    ','.join(quote(app[k]) for k in ('id', 'teamId', 'realmKey', 'slug', 'environment')) + ',1,' +
                    quote(compact(app)) + '::jsonb);')
            for user in users:
                principal = {'id': stable_id(realm, 'user', user['id']), 'realmKey': realm,
                    'subjectKey': hashlib.sha256(compact([issuer, user['id']]).encode()).hexdigest().upper(),
                    'keycloakId': user['id'], 'name': user['username'], 'kind': 'User', 'enabled': True,
                    'protected': False, 'membershipComplete': True, 'effectiveMemberKeys': [], 'syncedAt': timestamp()}
                sql('permora_test_portal', 'INSERT INTO directory(id,realm_key,name,document) VALUES (' +
                    ','.join(quote(principal[k]) for k in ('id', 'realmKey', 'name')) + ',' + quote(compact(principal)) + '::jsonb);')
            check(count('SELECT sum(jsonb_array_length(document->\'assignments\')) FROM applications') == APPLICATIONS * USERS,
                  'Real Keycloak and portal start with the same 32 expired assignments')
            proxy = ThreadingHTTPServer(('127.0.0.1', 19085), FaultProxy)
            threading.Thread(target=proxy.serve_forever, daemon=True).start()
            for worker, configuration in configurations.items():
                path = directory / (worker + '.json')
                path.write_text(compact(configuration))
                path.chmod(0o600)
                logfile = (directory / (worker + '.log')).open('wb')
                logs.append(logfile)
                processes[worker] = subprocess.Popen(['dotnet', str(worker_dll)], cwd=ROOT,
                    env=dict(os.environ, PERMORA_CONFIG=str(path), DOTNET_ENVIRONMENT='Development'),
                    stdout=logfile, stderr=subprocess.STDOUT)
            started = time.monotonic()
            for _ in range(40):
                if any(p.poll() is not None for p in processes.values()):
                    raise RuntimeError('A real worker exited before fault injection')
                if count('SELECT count(*) FROM operations WHERE status=\'Queued\'') == APPLICATIONS * USERS:
                    break
                time.sleep(.5)
            check(faults.outages > 0, 'Both real workers traverse an injected Keycloak 503 outage')
            check(count('SELECT count(*) FROM operations WHERE status=\'Applied\'') == 0,
                  'No remote success is published during the outage')
            with faults.lock:
                faults.offline = False
            if not faults.committed.wait(timeout=90):
                statuses = sql('permora_test_portal', 'SELECT status,document->>\'errorCode\',count(*) FROM operations GROUP BY 1,2')
                raise AssertionError('No real remote DELETE committed; operation states: ' + statuses)
            victim = faults.victim
            check(victim in processes, 'Lost-response fault identifies the actual worker performing a committed DELETE')
            processes[victim].kill()
            processes[victim].wait(timeout=10)
            faults.release.set()
            survivor = next(name for name in processes if name != victim)
            deadline = time.monotonic() + 180
            while time.monotonic() < deadline:
                if processes[survivor].poll() is not None:
                    raise RuntimeError('Surviving worker exited unexpectedly')
                if count('SELECT count(*) FROM operations WHERE status=\'Applied\'') == APPLICATIONS * USERS:
                    break
                time.sleep(.5)
            elapsed = time.monotonic() - started
            check(count('SELECT count(*) FROM operations WHERE status=\'Applied\'') == APPLICATIONS * USERS,
                  'Surviving process completes every revocation after a real post-commit worker kill')
            check(count('SELECT sum(jsonb_array_length(document->\'assignments\')) FROM applications') == 0,
                  'Portal removes all expired assignments only after remote verification')
            check(count('SELECT count(*) FROM operations') == APPLICATIONS * USERS,
                  'Concurrent lifecycle sweeps create exactly one idempotent operation per assignment')
            for cid in remote_clients:
                for user in users:
                    mapping = kc.request('GET', base + '/users/' + user['id'] + '/role-mappings/clients/' + cid)
                    check(not mapping, 'Real Keycloak confirms revoked mapping')
            check(faults.remote_deletes == APPLICATIONS * USERS,
                  'Lost reply and process failover do not cause duplicate effective remote DELETEs')
            stop_workers()
            jwks = kc.request('GET', '/realms/' + realm + '/protocol/openid-connect/certs', admin=False)
            keys_before = {k['kid'] for k in jwks['keys']}
            fingerprint = sql('permora_test_portal', "SELECT md5(string_agg(document::text,'' ORDER BY id)) FROM applications")
            execute(['docker', 'stop', '-t', '20', kc_name], timeout=40)
            started_kc = False
            # Writers are now quiesced. Dumps go only to restrictive ephemeral memory/files.
            dumps = {}
            for database in ('permora_test_portal', 'permora_test_keycloak'):
                dumps[database] = execute(['docker', 'exec', pg, 'pg_dump', '-U', 'permora_ci', '-Fc', database])
                restored = database + '_restore'
                sql('postgres', 'CREATE DATABASE ' + restored + ';')
                execute(['docker', 'exec', '-i', pg, 'pg_restore', '-U', 'permora_ci', '--no-owner',
                         '--no-privileges', '-d', restored], data=dumps[database])
            check(sql('permora_test_portal_restore', "SELECT md5(string_agg(document::text,'' ORDER BY id)) FROM applications") == fingerprint,
                  'Restored portal database has an identical approved application state')
            check(int(sql('permora_test_portal_restore', "SELECT count(*) FROM operations WHERE status='Applied'")) == APPLICATIONS * USERS,
                  'Restored portal database retains completed operation evidence')
            recovered = start_keycloak('permora_test_keycloak_restore')
            keys_after = {k['kid'] for k in recovered.request('GET', '/realms/' + realm + '/protocol/openid-connect/certs', admin=False)['keys']}
            check(keys_before == keys_after, 'Keycloak database restore preserves realm signing-key identity')
            for cid in remote_clients:
                for user in users:
                    check(not recovered.request('GET', base + '/users/' + user['id'] + '/role-mappings/clients/' + cid),
                          'Restored Keycloak does not resurrect a revoked assignment')
            print(json.dumps({'scope': 'two-real-worker-processes-and-real-keycloak',
                'applications': APPLICATIONS, 'revocations': APPLICATIONS * USERS,
                'elapsedSeconds': round(elapsed, 3), 'outageResponses': faults.outages,
                'postCommitWorkerKills': 1, 'databaseRestores': 2,
                'productionSlo': None, 'multiHostHaApproval': False}), flush=True)
        finally:
            faults.release.set()
            stop_workers()
            if proxy:
                proxy.shutdown()
                proxy.server_close()
            for logfile in logs:
                logfile.close()
            if started_kc:
                subprocess.run(['docker', 'rm', '-f', kc_name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=30)
            if started_pg:
                subprocess.run(['docker', 'rm', '-f', pg], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=30)


if __name__ == '__main__':
    run()
