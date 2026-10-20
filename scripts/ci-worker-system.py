#!/usr/bin/env python3
"""Real processes, real Keycloak commits and two-database recovery. Isolated CI only.

The fault proxy does not fabricate a successful write: it forwards to Keycloak,
waits for its 204, withholds the response, and the fixture kills the responsible
worker before PostgreSQL can record success. No production secrets or servers.
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
# The stress fixture runs only with explicit GITHUB_ACTIONS and isolated guards
# below. Bound input size so a malicious workflow cannot exhaust a public runner.
def isolated_test_size(name, default, low, high):
    value = os.environ.get(name, str(default))
    if not value.isdecimal() or not low <= int(value) <= high:
        raise SystemExit(f'{name} must be an integer between {low} and {high}')
    return int(value)

APPLICATIONS = isolated_test_size('PERMORA_CI_APPLICATIONS', 4, 2, 12)
USERS = isolated_test_size('PERMORA_CI_USERS', 8, 2, 24)
if APPLICATIONS * USERS > 192:
    raise SystemExit('Refusing oversized isolated worker load fixture')


def check(condition, message):
    if not condition:
        raise AssertionError(message)
    print('PASS:', message, flush=True)


def execute(args, *, data=None, env=None, timeout=180):
    result = subprocess.run(args, input=data, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            env=env, timeout=timeout, check=False)
    if result.returncode:
        raise RuntimeError(f'{Path(args[0]).name} failed with exit {result.returncode}')
    return result.stdout


def compact(value):
    return json.dumps(value, separators=(',', ':'), ensure_ascii=True)


def stable_id(realm, kind, remote):
    return str(uuid.UUID(bytes_le=hashlib.sha256(compact([realm, kind, remote]).encode()).digest()[:16]))


def quote(value):
    return "'" + str(value).replace("'", "''") + "'"


def timestamp(minutes=0):
    return (datetime.now(timezone.utc) + timedelta(minutes=minutes)).isoformat()


class Admin:
    def __init__(self, password):
        self.password, self.token, self.until = password, '', 0.0

    def request(self, method, path, body=None, *, token=None, admin=True, form=False, expected=(200, 201, 204)):
        if admin and token is None:
            if time.monotonic() >= self.until:
                doc = self.request('POST', '/realms/master/protocol/openid-connect/token',
                    {'grant_type': 'password', 'client_id': 'admin-cli', 'username': 'ci-admin',
                     'password': self.password}, admin=False, form=True)
                self.token, self.until = doc['access_token'], time.monotonic() + 35
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
        self.offline, self.outages, self.remote_deletes = True, 0, 0
        self.victim = None
        self.committed, self.release = threading.Event(), threading.Event()


class FaultProxy(BaseHTTPRequestHandler):
    faults: Faults
    protocol_version = 'HTTP/1.1'

    def log_message(self, *_):
        pass

    def do_GET(self): self.forward()
    def do_POST(self): self.forward()
    def do_PUT(self): self.forward()
    def do_DELETE(self): self.forward()

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
            req = urllib.request.Request(KEYCLOAK + self.path, data=body, headers=headers, method=self.command)
            try:
                response = urllib.request.urlopen(req, timeout=20)
            except urllib.error.HTTPError as error:
                response = error
            with response:
                status, content = response.status, response.read(4 * 1024 * 1024)
                content_type = response.headers.get('Content-Type', 'application/json')
            lose = False
            if self.command == 'DELETE' and '/role-mappings/clients/' in self.path and status == 204:
                # Only identifies a disposable process to kill. Keycloak itself
                # has already validated token signature and write authorization.
                middle = self.headers.get('Authorization', '').split(' ', 1)[-1].split('.')[1]
                client = json.loads(base64.urlsafe_b64decode(middle + '=' * (-len(middle) % 4))).get('azp')
                with self.faults.lock:
                    self.faults.remote_deletes += 1
                    if self.faults.victim is None and client in ('worker-a-assignments', 'worker-b-assignments'):
                        self.faults.victim = client.split('-assignments')[0]
                        lose = True
                        self.faults.committed.set()
            if lose:
                self.faults.release.wait(timeout=25)
                self.close_connection = True
                try: self.connection.shutdown(socket.SHUT_RDWR)
                except OSError: pass
                return
            self.send_response(status)
            self.send_header('Content-Type', content_type)
            self.send_header('Content-Length', str(len(content)))
            self.end_headers()
            if content: self.wfile.write(content)
        except (OSError, ValueError, IndexError):
            self.close_connection = True


def run():
    if os.environ.get('GITHUB_ACTIONS') != 'true' or os.environ.get('PERMORA_SYSTEM_TESTS') != 'isolated':
        raise SystemExit('Only an explicitly isolated runner may execute this destructive fixture.')
    dll = ROOT / 'src/Permora.Worker/bin/Release/net10.0/Permora.Worker.dll'
    if not dll.is_file(): raise SystemExit('Build the actual Release worker first.')
    suffix = uuid.uuid4().hex[:10]
    pg, kc_name, realm = 'permora-pg-' + suffix, 'permora-kc-' + suffix, 'permora-system-' + suffix
    base, issuer = '/admin/realms/' + realm, KEYCLOAK + '/realms/' + realm
    pg_secret, kc_secret = secrets.token_urlsafe(32), secrets.token_urlsafe(32)
    started_pg = started_kc = False
    processes, logs = {}, []
    faults, proxy = Faults(), None
    FaultProxy.faults = faults
    with tempfile.TemporaryDirectory(prefix='permora-worker-system-') as temp:
        directory = Path(temp)
        directory.chmod(0o700)
        def sql(database, text):
            if database not in ('postgres','permora_test_portal','permora_test_keycloak',
                                'permora_test_portal_restore','permora_test_keycloak_restore'):
                raise ValueError('Unapproved fixture database')
            return execute(['docker','exec','-i',pg,'psql','-U','permora_ci','-d',database,
                            '-X','-qAt','-v','ON_ERROR_STOP=1'], data=text.encode()).decode().strip()
        def count(text): return int(sql('permora_test_portal', text) or '0')
        def start_keycloak(database):
            nonlocal started_kc
            env = dict(os.environ, KC_DB_PASSWORD=pg_secret, KC_BOOTSTRAP_ADMIN_PASSWORD=kc_secret)
            execute(['docker','run','-d','--rm','--name',kc_name,'--network','host',
                '-e','KC_DB=postgres','-e',f'KC_DB_URL=jdbc:postgresql://127.0.0.1:{PGPORT}/{database}',
                '-e','KC_DB_USERNAME=permora_ci','-e','KC_DB_PASSWORD',
                '-e','KC_BOOTSTRAP_ADMIN_USERNAME=ci-admin','-e','KC_BOOTSTRAP_ADMIN_PASSWORD',
                'quay.io/keycloak/keycloak:26.8.1','start-dev','--http-host=127.0.0.1','--http-port=18085'], env=env)
            started_kc = True
            admin = Admin(kc_secret)
            admin.wait()
            return admin
        def stop_workers():
            for process in processes.values():
                if process.poll() is None:
                    process.terminate()
                    try: process.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        process.kill(); process.wait(timeout=5)
        try:
            execute(['docker','run','-d','--rm','--name',pg,'-p',f'127.0.0.1:{PGPORT}:5432',
                     '-e','POSTGRES_USER=permora_ci','-e','POSTGRES_PASSWORD','postgres:18'],
                    env=dict(os.environ, POSTGRES_PASSWORD=pg_secret))
            started_pg = True
            for _ in range(60):
                if subprocess.run(['docker','exec',pg,'pg_isready','-U','permora_ci'],
                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=5).returncode == 0: break
                time.sleep(1)
            else: raise TimeoutError('Own PostgreSQL did not become ready')
            sql('postgres','CREATE DATABASE permora_test_portal; CREATE DATABASE permora_test_keycloak;')
            kc = start_keycloak('permora_test_keycloak')
            kc.request('POST','/admin/realms',{'realm':realm,'enabled':True,'adminPermissionsEnabled':True})
            def client(name, app=None, service=False):
                payload={'clientId':name,'protocol':'openid-connect','enabled':True,'standardFlowEnabled':False,
                    'implicitFlowEnabled':False,'directAccessGrantsEnabled':False,'fullScopeAllowed':False,
                    'serviceAccountsEnabled':service,'publicClient':False}
                if app: payload['attributes']={'permora.application':app}
                kc.request('POST',base+'/clients',payload)
                return next(c['id'] for c in kc.request('GET',base+'/clients?clientId='+name) if c['clientId']==name)
            users=[]
            for i in range(USERS):
                name='worker-fixture-'+str(i)
                kc.request('POST',base+'/users',{'username':name,'enabled':True,'firstName':'Worker',
                    'lastName':'Fixture','email':name+'@permora.invalid','emailVerified':True})
                users.append(next(u for u in kc.request('GET',base+'/users?username='+name) if u['username']==name))
            team_id=str(uuid.uuid4())
            team={'id':team_id,'name':'Isolated system test','revision':1,
                  'members':[{'subjectKey':'A'*64,'name':'Fixture owner','role':'Owner'}]}
            applications, remote_clients=[],[]
            for i in range(APPLICATIONS):
                app_id, role_id=str(uuid.uuid4()),str(uuid.uuid4())
                name='ci-owned-api-'+str(i)
                cid=client(name,app_id)
                kc.request('POST',base+'/clients/'+cid+'/roles',{'name':'role.reader','description':'Fixture reader',
                    'attributes':{'permora.application':[app_id],'permora.entity':[role_id]}})
                role=kc.request('GET',base+'/clients/'+cid+'/roles/role.reader')
                assignments=[]
                for user in users:
                    kc.request('POST',base+'/users/'+user['id']+'/role-mappings/clients/'+cid,[role])
                    assignments.append({'id':str(uuid.uuid4()),'roleId':role_id,
                        'principalId':stable_id(realm,'user',user['id']),'grantedBy':'A'*64,
                        'grantedAt':timestamp(-60),'expiresAt':timestamp(-1)})
                applications.append({'id':app_id,'teamId':team_id,'realmKey':realm,'slug':'app-'+str(i),
                    'name':'Fixture app '+str(i),'description':'Real worker fault test','environment':'Test',
                    'status':'Active','revision':1,'keycloakClientUuid':cid,'keycloakClientId':name,'loginClientUuid':None,
                    'policy':{'requireApproval':True,'maxGrantDays':90,'separationRules':[]},'permissions':[],
                    'roles':[{'id':role_id,'key':'reader','name':'Reader','description':'Fixture reader','permissionIds':[]}],
                    'assignments':assignments,'iamApproval':None})
                remote_clients.append(cid)
            foreign=client('ci-foreign-not-managed')
            authz_client=next(c['id'] for c in kc.request('GET',base+'/clients?clientId=admin-permissions') if c['clientId']=='admin-permissions')
            authz=base+'/clients/'+authz_client+'/authz/resource-server'
            management=next(c['id'] for c in kc.request('GET',base+'/clients?clientId=realm-management') if c['clientId']=='realm-management')
            configurations, accounts={},[]
            for worker in ('worker-a','worker-b'):
                profiles={}
                for profile in ('roles','assignments','directory'):
                    name=worker+'-'+profile
                    cid=client(name,service=True)
                    account=kc.request('GET',base+'/clients/'+cid+'/service-account-user')['id']
                    secret=kc.request('GET',base+'/clients/'+cid+'/client-secret')['value']
                    secret_path=directory/(name+'.secret')
                    secret_path.write_text(secret); secret_path.chmod(0o600)
                    profiles[profile.title()]={'ClientId':name,'SecretFile':str(secret_path)}
                    accounts.append({'name':name,'user':account,'profile':profile,'secret':secret})
                    if profile=='directory':
                        read_role=kc.request('GET',base+'/clients/'+management+'/roles/view-users')
                        kc.request('POST',base+'/users/'+account+'/role-mappings/clients/'+management,[read_role])
                        # FullScopeAllowed=false intentionally: both the service
                        # account and its client role scope must include view-users.
                        kc.request('POST',base+'/clients/'+cid+'/scope-mappings/clients/'+management,[read_role])
                connection=f'Host=127.0.0.1;Port={PGPORT};Database=permora_test_portal;Username=permora_ci;Password={pg_secret}'
                configurations[worker]={'ConnectionStrings':{'Portal':connection},
                    'Portal':{'Authority':issuer,'AllowHttpInDevelopment':True,'AdministratorSubjects':[]},
                    'Keycloak':{'AllowHttpInDevelopment':True,'Realms':{realm:{'BaseUrl':PROXY,'Realm':realm,
                    'Issuer':issuer,**profiles,'ProtectedSubjectIds':[]}}}}
            # One permission per resource type/scope, with both intended process
            # identities in its policy. Multiple overlapping per-account permission
            # objects otherwise conflict under the server's decision strategy.
            viewers=[a['user'] for a in accounts if a['profile'] in ('roles','assignments')]
            mappers=[a['user'] for a in accounts if a['profile']=='assignments']
            for name, ids in (('client-viewers',viewers),('assignment-writers',mappers)):
                kc.request('POST',authz+'/policy/user',{'name':name,'logic':'POSITIVE','users':ids})
            for name,kind,resources,scopes,policy in (
                ('view-owned-clients','Clients',remote_clients,['view'],'client-viewers'),
                ('map-owned-roles','Clients',remote_clients,['map-roles'],'assignment-writers'),
                ('map-approved-users','Users',[u['id'] for u in users],['view','map-roles'],'assignment-writers')):
                kc.request('POST',authz+'/permission/scope',{'name':name,'resourceType':kind,'resources':resources,
                           'scopes':scopes,'policies':[policy]})
            for account in accounts:
                token=kc.request('POST','/realms/'+realm+'/protocol/openid-connect/token',
                    {'grant_type':'client_credentials','client_id':account['name'],'client_secret':account['secret']},
                    admin=False,form=True)['access_token']
                if account['profile']=='roles':
                    kc.request('GET',base+'/clients/'+remote_clients[0],token=token)
                    kc.request('GET',base+'/clients/'+remote_clients[0]+'/roles/role.reader',token=token)
                    kc.request('GET',base+'/clients/'+foreign,token=token,expected=(403,404))
                if account['profile']=='assignments':
                    kc.request('GET',base+'/users/'+users[0]['id']+'/role-mappings/clients/'+remote_clients[0],token=token)
                    kc.request('PUT',base,{'enabled':False},token=token,expected=(401,403))
                check(True,'Real scoped service-account preflight: '+account['name'])
            config=directory/'migrate.json'
            config.write_text(compact(configurations['worker-a'])); config.chmod(0o600)
            execute(['dotnet',str(dll),'--migrate'],env=dict(os.environ,PERMORA_CONFIG=str(config),DOTNET_ENVIRONMENT='Development'),timeout=60)
            sql('permora_test_portal','INSERT INTO teams(id,revision,document) VALUES ('+quote(team_id)+',1,'+quote(compact(team))+'::jsonb);')
            for app in applications:
                sql('permora_test_portal','INSERT INTO applications(id,team_id,realm_key,slug,environment,revision,document) VALUES ('+
                    ','.join(quote(app[k]) for k in ('id','teamId','realmKey','slug','environment'))+',1,'+quote(compact(app))+'::jsonb);')
            for user in users:
                principal={'id':stable_id(realm,'user',user['id']),'realmKey':realm,
                    'subjectKey':hashlib.sha256(compact([issuer,user['id']]).encode()).hexdigest().upper(),
                    'keycloakId':user['id'],'name':user['username'],'kind':'User','enabled':True,'protected':False,
                    'membershipComplete':True,'effectiveMemberKeys':[],'syncedAt':timestamp()}
                sql('permora_test_portal','INSERT INTO directory(id,realm_key,name,document) VALUES ('+
                    ','.join(quote(principal[k]) for k in ('id','realmKey','name'))+','+quote(compact(principal))+'::jsonb);')
            check(count("SELECT sum(jsonb_array_length(document->'assignments')) FROM applications")==APPLICATIONS*USERS,
                  'Real Keycloak and portal start with exactly '+str(APPLICATIONS * USERS)+' expired assignments')
            proxy=ThreadingHTTPServer(('127.0.0.1',19085),FaultProxy)
            threading.Thread(target=proxy.serve_forever,daemon=True).start()
            for worker,configuration in configurations.items():
                path=directory/(worker+'.json'); path.write_text(compact(configuration)); path.chmod(0o600)
                log=(directory/(worker+'.log')).open('wb'); logs.append(log)
                processes[worker]=subprocess.Popen(['dotnet',str(dll)],cwd=ROOT,
                    env=dict(os.environ,PERMORA_CONFIG=str(path),DOTNET_ENVIRONMENT='Development'),stdout=log,stderr=subprocess.STDOUT)
            started=time.monotonic()
            for _ in range(40):
                if any(p.poll() is not None for p in processes.values()): raise RuntimeError('A worker exited before fault injection')
                if count("SELECT count(*) FROM operations WHERE status='Queued'")==APPLICATIONS*USERS: break
                time.sleep(.5)
            check(faults.outages>0,'Real workers traverse an injected Keycloak 503 outage')
            check(count("SELECT count(*) FROM operations WHERE status='Applied'")==0,'No remote success is published during outage')
            with faults.lock: faults.offline=False
            if not faults.committed.wait(timeout=90):
                raise AssertionError('No real DELETE committed; states: '+sql('permora_test_portal',"SELECT status,document->>'errorCode',count(*) FROM operations GROUP BY 1,2"))
            victim=faults.victim
            check(victim in processes,'Committed DELETE identifies the actual responsible worker')
            processes[victim].kill(); processes[victim].wait(timeout=10); faults.release.set()
            survivor=next(name for name in processes if name!=victim)
            # Bound the stress acceptance by work size while retaining the
            # original small-fixture upper bound. This is CI-specific,
            # never a production revocation SLO.
            deadline=time.monotonic()+max(180, APPLICATIONS * USERS * 5)
            while time.monotonic()<deadline:
                if processes[survivor].poll() is not None: raise RuntimeError('Surviving worker exited')
                if count("SELECT count(*) FROM operations WHERE status='Applied'")==APPLICATIONS*USERS: break
                time.sleep(.5)
            elapsed=time.monotonic()-started
            check(count("SELECT count(*) FROM operations WHERE status='Applied'")==APPLICATIONS*USERS,'Survivor completes every revocation after post-commit worker kill')
            check(count("SELECT sum(jsonb_array_length(document->'assignments')) FROM applications")==0,'Portal removes assignments only after verification')
            check(count('SELECT count(*) FROM operations')==APPLICATIONS*USERS,'Exactly one lifecycle operation per expired assignment')
            for cid in remote_clients:
                for user in users:
                    check(not kc.request('GET',base+'/users/'+user['id']+'/role-mappings/clients/'+cid),'Actual Keycloak confirms revocation')
            check(faults.remote_deletes==APPLICATIONS*USERS,'Failover does not duplicate effective remote DELETEs')
            stop_workers()
            keys_before={k['kid'] for k in kc.request('GET','/realms/'+realm+'/protocol/openid-connect/certs',admin=False)['keys']}
            fingerprint=sql('permora_test_portal',"SELECT md5(string_agg(document::text,'' ORDER BY id)) FROM applications")
            execute(['docker','stop','-t','20',kc_name],timeout=40); started_kc=False
            for database in ('permora_test_portal','permora_test_keycloak'):
                backup=execute(['docker','exec',pg,'pg_dump','-U','permora_ci','-Fc',database])
                sql('postgres','CREATE DATABASE '+database+'_restore;')
                execute(['docker','exec','-i',pg,'pg_restore','-U','permora_ci','--no-owner','--no-privileges','-d',database+'_restore'],data=backup)
            check(sql('permora_test_portal_restore',"SELECT md5(string_agg(document::text,'' ORDER BY id)) FROM applications")==fingerprint,'Restored portal retains identical application state')
            check(int(sql('permora_test_portal_restore',"SELECT count(*) FROM operations WHERE status='Applied'"))==APPLICATIONS*USERS,'Restored portal retains completed operation evidence')
            recovered=start_keycloak('permora_test_keycloak_restore')
            keys_after={k['kid'] for k in recovered.request('GET','/realms/'+realm+'/protocol/openid-connect/certs',admin=False)['keys']}
            check(keys_before==keys_after,'Keycloak restore preserves realm signing keys')
            for cid in remote_clients:
                for user in users:
                    check(not recovered.request('GET',base+'/users/'+user['id']+'/role-mappings/clients/'+cid),'Restored Keycloak does not resurrect revoked access')
            print(json.dumps({'scope':'two-real-worker-processes-real-keycloak','applications':APPLICATIONS,
                'revocations':APPLICATIONS*USERS,'users':USERS,'elapsedSeconds':round(elapsed,3),
                'throughputRevocationsPerSecond':round(APPLICATIONS*USERS/max(elapsed,0.001),3),
                'outageResponses':faults.outages,
                'postCommitWorkerKills':1,'databaseRestores':2,'productionSlo':None,'multiHostHaApproval':False}),flush=True)
        finally:
            faults.release.set(); stop_workers()
            if proxy: proxy.shutdown(); proxy.server_close()
            for log in logs: log.close()
            if started_kc: subprocess.run(['docker','rm','-f',kc_name],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,timeout=30)
            if started_pg: subprocess.run(['docker','rm','-f',pg],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,timeout=30)


if __name__=='__main__':
    run()
