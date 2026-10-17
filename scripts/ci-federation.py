#!/usr/bin/env python3
"""Real LDAPS/Keycloak acceptance. All resources are disposable and local.

READ_ONLY LDAP without import uses Keycloak's ReadOnlyUserModelDelegate:
individual role writes are unsupported. Test their rejection and separately
prove delegated GROUP mappings and inherited effective access instead.
OpenLDAP results are never advertised as Microsoft AD or Kerberos acceptance.
"""
from __future__ import annotations
import base64
import hashlib
import json
import os
from pathlib import Path
import secrets
import socket
import ssl
import subprocess
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

BASE = 'http://127.0.0.1:18084'
DN = 'dc=permora,dc=test'
LDAP = 'ldaps://127.0.0.1:1636'
CHECKS: list[str] = []


def check(condition, label):
    if not condition:
        raise AssertionError(label)
    CHECKS.append(label)
    print('PASS:', label, flush=True)


def command(args, *, data=None, env=None):
    result = subprocess.run(args, input=data, text=True, capture_output=True,
                            timeout=120, check=False, env=env)
    if result.returncode:
        raise RuntimeError(f'{Path(args[0]).name} failed with exit {result.returncode}')
    return result.stdout


def ssha(password):
    salt = secrets.token_bytes(16)
    return '{SSHA}' + base64.b64encode(hashlib.sha1(password.encode() + salt).digest() + salt).decode()


class Keycloak:
    def __init__(self, password):
        self.password, self.token, self.until = password, '', 0.0

    def request(self, method, path, data=None, *, token=None,
                admin=True, form=False, expected=(200, 201, 204)):
        if admin and token is None:
            if time.monotonic() >= self.until:
                doc = self.request('POST', '/realms/master/protocol/openid-connect/token',
                    {'client_id': 'admin-cli', 'grant_type': 'password',
                     'username': 'ci-admin', 'password': self.password}, admin=False, form=True)[1]
                self.token, self.until = doc['access_token'], time.monotonic() + 35
            token = self.token
        headers = {'Accept': 'application/json'}
        if token:
            headers['Authorization'] = 'Bearer ' + token
        body = None
        if data is not None:
            body = (urllib.parse.urlencode(data) if form else json.dumps(data)).encode()
            headers['Content-Type'] = 'application/x-www-form-urlencoded' if form else 'application/json'
        req = urllib.request.Request(BASE + path, data=body, headers=headers, method=method)
        try:
            response = urllib.request.urlopen(req, timeout=20)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            raw, status = response.read(4 * 1024 * 1024), response.status
            location = response.headers.get('Location', '')
        if status not in expected:
            raise AssertionError(f'Keycloak {method} {path.split("?")[0]}: HTTP {status}')
        try:
            parsed = json.loads(raw) if raw else None
        except ValueError:
            parsed = None
        return status, parsed, location


def test_realm(kc, bind_secret, imported, group_members):
    group_members('federated0')
    realm = 'permora-federation-' + uuid.uuid4().hex[:10]
    base, mode = '/admin/realms/' + realm, 'imported' if imported else 'non-imported'
    kc.request('POST', '/admin/realms', {'realm': realm, 'enabled': True, 'adminPermissionsEnabled': True})
    try:
        realm_id = kc.request('GET', base)[1]['id']
        cfg = {'enabled': 'true', 'vendor': 'other', 'editMode': 'READ_ONLY',
            'connectionUrl': LDAP, 'usersDn': 'ou=people,' + DN,
            'bindDn': 'cn=admin,' + DN, 'bindCredential': bind_secret,
            'authType': 'simple', 'usernameLDAPAttribute': 'uid', 'rdnLDAPAttribute': 'uid',
            'uuidLDAPAttribute': 'entryUUID', 'userObjectClasses': 'inetOrgPerson',
            'searchScope': '2', 'pagination': 'true', 'batchSizeForSync': '2',
            'importEnabled': str(imported).lower(), 'syncRegistrations': 'false',
            'cachePolicy': 'NO_CACHE', 'useTruststoreSpi': 'always',
            'connectionTimeout': '5000', 'readTimeout': '5000',
            'fullSyncPeriod': '-1', 'changedSyncPeriod': '-1'}
        _, _, location = kc.request('POST', base + '/components', {'name': 'ldaps-fixture',
            'parentId': realm_id, 'providerId': 'ldap',
            'providerType': 'org.keycloak.storage.UserStorageProvider',
            'config': {k: [v] for k, v in cfg.items()}})
        provider = location.rsplit('/', 1)[-1]
        mapper_cfg = {'groups.dn': 'ou=groups,' + DN, 'group.name.ldap.attribute': 'cn',
            'group.object.classes': 'groupOfNames', 'membership.ldap.attribute': 'member',
            'membership.attribute.type': 'DN', 'membership.user.ldap.attribute': 'uid',
            'mode': 'READ_ONLY', 'user.roles.retrieve.strategy': 'LOAD_GROUPS_BY_MEMBER_ATTRIBUTE',
            'preserve.group.inheritance': 'true', 'ignore.missing.groups': 'false'}
        _, _, loc = kc.request('POST', base + '/components', {'name': 'nested-groups',
            'parentId': provider, 'providerId': 'group-ldap-mapper',
            'providerType': 'org.keycloak.storage.ldap.mappers.LDAPStorageMapper',
            'config': {k: [v] for k, v in mapper_cfg.items()}})
        mapper = loc.rsplit('/', 1)[-1]
        kc.request('POST', base + '/user-storage/' + provider + '/mappers/' + mapper + '/sync?direction=fedToKeycloak')
        if imported:
            sync = kc.request('POST', base + '/user-storage/' + provider + '/sync?action=triggerFullSync')[1]
            check(sync.get('failed', 0) == 0, mode + ': full LDAPS synchronization')
        users = []
        for first in range(0, 8, 2):
            page = kc.request('GET', base + '/users?search=federated&first=' + str(first) + '&max=2')[1]
            users.extend(page)
            if len(page) < 2:
                break
        check(len(users) == 6 and {u['username'] for u in users} == {'federated' + str(i) for i in range(6)},
              mode + ': complete unique paginated directory')
        subject = next(u for u in users if u['username'] == 'federated0')
        other = next(u for u in users if u['username'] == 'federated1')
        check(bool(subject.get('federationLink')) if imported else subject['id'].startswith('f:'),
              mode + ': LDAP linkage/storage ID retained')
        def user_path(user):
            return base + '/users/' + urllib.parse.quote(user['id'], safe='')
        memberships = kc.request('GET', user_path(subject) + '/groups')[1]
        group = next((g for g in memberships if g['name'] == 'team-a'), None)
        check(group is not None and group['path'].endswith('/parent-a/team-a'), mode + ': nested LDAP hierarchy')
        check(not any(g['name'] == 'team-a' for g in kc.request('GET', user_path(other) + '/groups')[1]),
              mode + ': unrelated user outside group')
        group_members('federated0', 'federated1')
        kc.request('POST', base + '/clear-user-cache')
        check(any(g['name'] == 'team-a' for g in kc.request('GET', user_path(other) + '/groups')[1]),
              mode + ': external membership addition visible')
        group_members('federated0')
        kc.request('POST', base + '/clear-user-cache')
        check(not any(g['name'] == 'team-a' for g in kc.request('GET', user_path(other) + '/groups')[1]),
              mode + ': external membership removal visible')
        def client(client_id, **extra):
            kc.request('POST', base + '/clients', {'clientId': client_id, 'protocol': 'openid-connect',
                'enabled': True, 'standardFlowEnabled': False, 'fullScopeAllowed': False, **extra})
            return next(c for c in kc.request('GET', base + '/clients?clientId=' + client_id)[1]
                        if c['clientId'] == client_id)['id']
        ca, cb = client('own-api'), client('foreign-api')
        for cid in (ca, cb):
            kc.request('POST', base + '/clients/' + cid + '/roles', {'name': 'role.reader'})
        own = kc.request('GET', base + '/clients/' + ca + '/roles/role.reader')[1]
        foreign = kc.request('GET', base + '/clients/' + cb + '/roles/role.reader')[1]
        sa = client('limited-mapper', serviceAccountsEnabled=True, publicClient=False)
        sa_user = kc.request('GET', base + '/clients/' + sa + '/service-account-user')[1]['id']
        authz_client = next(c for c in kc.request('GET', base + '/clients?clientId=admin-permissions')[1]
                            if c['clientId'] == 'admin-permissions')['id']
        authz = base + '/clients/' + authz_client + '/authz/resource-server'
        kc.request('POST', authz + '/policy/user', {'name': 'only-mapper', 'logic': 'POSITIVE', 'users': [sa_user]})
        for name, kind, resources, scopes in (
            ('own-roles', 'Clients', [ca], ['view', 'map-roles']),
            ('allowed-user', 'Users', [subject['id']], ['view', 'map-roles']),
            # Keycloak group manage is broader than mapping; this is disclosed,
            # never called a fine-grained group-mapping-only privilege.
            ('allowed-group', 'Groups', [group['id']], ['view', 'manage'])):
            kc.request('POST', authz + '/permission/scope', {'name': name, 'resourceType': kind,
                'resources': resources, 'scopes': scopes, 'policies': ['only-mapper']})
        secret = kc.request('GET', base + '/clients/' + sa + '/client-secret')[1]['value']
        delegated = kc.request('POST', '/realms/' + realm + '/protocol/openid-connect/token',
            {'grant_type': 'client_credentials', 'client_id': 'limited-mapper', 'client_secret': secret},
            admin=False, form=True)[1]['access_token']
        mapping = user_path(subject) + '/role-mappings/clients/' + ca
        if imported:
            kc.request('POST', mapping, [own], token=delegated)
            check(any(r['id'] == own['id'] for r in kc.request('GET', mapping)[1]), mode + ': direct delegated grant applied')
            kc.request('DELETE', mapping, [own], token=delegated)
            check(not any(r['id'] == own['id'] for r in kc.request('GET', mapping)[1]), mode + ': direct delegated revoke applied')
        else:
            # Upstream LDAPStorageProvider.proxy() selects ReadOnlyUserModelDelegate
            # when importEnabled=false and editMode=READ_ONLY. No local role mapping
            # may be advertised as persisted. Verify the capability boundary, too.
            status, _, _ = kc.request('POST', mapping, [own], token=delegated, expected=(400,))
            check(status == 400 and not any(r['id'] == own['id'] for r in kc.request('GET', mapping)[1]),
                  mode + ': unsupported individual write is rejected without a mapping')
        group_path = base + '/groups/' + group['id'] + '/role-mappings/clients/' + ca
        effective = user_path(subject) + '/role-mappings/clients/' + ca + '/composite'
        kc.request('POST', group_path, [own], token=delegated)
        check(any(r['id'] == own['id'] for r in kc.request('GET', group_path)[1]), mode + ': delegated group mapping applied')
        check(any(r['id'] == own['id'] for r in kc.request('GET', effective)[1]), mode + ': LDAP member inherits mapped group access')
        for label, method, path, body in (
            ('foreign user', 'POST', user_path(other) + '/role-mappings/clients/' + ca, [own]),
            ('foreign client', 'POST', user_path(subject) + '/role-mappings/clients/' + cb, [foreign]),
            ('foreign group role', 'POST', group_path, [foreign]),
            ('impersonation', 'POST', user_path(subject) + '/impersonation', None),
            ('password reset', 'PUT', user_path(subject) + '/reset-password', {'type': 'password', 'value': 'NeverApplied-Test-Only'}),
            ('user changes', 'PUT', user_path(subject), {'enabled': False}),
            ('realm changes', 'PUT', base, {'enabled': False})):
            status, _, _ = kc.request(method, path, body, token=delegated, expected=(401, 403, 404))
            check(status in (401, 403, 404), mode + ': denied ' + label)
        kc.request('DELETE', group_path, [own], token=delegated)
        check(not any(r['id'] == own['id'] for r in kc.request('GET', effective)[1]), mode + ': inherited access revoked and verified')
        check(kc.request('GET', user_path(subject))[1]['enabled'], mode + ': denied account changes had no side effect')
    finally:
        kc.request('DELETE', base, expected=(204, 404))


def run():
    if os.environ.get('GITHUB_ACTIONS') != 'true' or os.environ.get('PERMORA_SYSTEM_TESTS') != 'isolated':
        raise SystemExit('Only an explicitly isolated GitHub runner may run these destructive fixtures.')
    with tempfile.TemporaryDirectory(prefix='permora-ldaps-') as name:
        root = Path(name)
        root.chmod(0o700)
        (root / 'db').mkdir()
        bind_secret, user_secret, admin_secret = (secrets.token_urlsafe(32) for _ in range(3))
        (root / 'bind.secret').write_text(bind_secret)
        (root / 'bind.secret').chmod(0o600)
        command(['openssl', 'req', '-x509', '-newkey', 'rsa:3072', '-nodes', '-days', '2',
                 '-keyout', str(root / 'ldap.key'), '-out', str(root / 'ldap.crt'),
                 '-subj', '/CN=localhost', '-addext', 'subjectAltName=IP:127.0.0.1,DNS:localhost'])
        (root / 'ldap.key').chmod(0o600)
        (root / 'slapd.conf').write_text(f'''include /etc/ldap/schema/core.schema
include /etc/ldap/schema/cosine.schema
include /etc/ldap/schema/inetorgperson.schema
pidfile {root}/slapd.pid
argsfile {root}/slapd.args
modulepath /usr/lib/ldap
moduleload back_mdb
TLSCertificateFile {root}/ldap.crt
TLSCertificateKeyFile {root}/ldap.key
TLSCACertificateFile {root}/ldap.crt
TLSProtocolMin 3.3
database mdb
maxsize 1073741824
suffix "{DN}"
rootdn "cn=admin,{DN}"
rootpw {ssha(bind_secret)}
directory {root}/db
index objectClass eq
index uid,cn eq
access to attrs=userPassword by self write by anonymous auth by * none
access to * by * read
''')
        (root / 'slapd.conf').chmod(0o600)
        logfile = (root / 'ldap-startup.log').open('w')
        server = subprocess.Popen(['/usr/sbin/slapd', '-f', str(root / 'slapd.conf'), '-h', LDAP, '-d', '256'],
                                  stdout=logfile, stderr=subprocess.STDOUT)
        container = 'permora-federation-' + uuid.uuid4().hex[:12]
        started = False
        ldap_env = dict(os.environ, LDAPTLS_CACERT=str(root / 'ldap.crt'), LDAPTLS_REQCERT='demand')
        def ldap_write(program, ldif):
            command([program, '-x', '-H', LDAP, '-D', 'cn=admin,' + DN, '-y', str(root / 'bind.secret')], data=ldif, env=ldap_env)
        def group_members(*users):
            ldap_write('ldapmodify', f'dn: cn=team-a,ou=groups,{DN}\nchangetype: modify\nreplace: member\n' +
                       ''.join(f'member: uid={user},ou=people,{DN}\n' for user in users) + '\n')
        try:
            context = ssl.create_default_context(cafile=str(root / 'ldap.crt'))
            for _ in range(40):
                if server.poll() is not None:
                    raise RuntimeError('Isolated slapd failed before TLS readiness')
                try:
                    with socket.create_connection(('127.0.0.1', 1636), timeout=1) as sock:
                        with context.wrap_socket(sock, server_hostname='127.0.0.1'):
                            break
                except OSError:
                    time.sleep(.25)
            else:
                raise TimeoutError('LDAPS fixture did not start')
            check(True, 'LDAPS certificate and hostname validated without a bypass')
            ldif = f'dn: {DN}\nobjectClass: domain\ndc: permora\n\n'
            for ou in ('people', 'groups'):
                ldif += f'dn: ou={ou},{DN}\nobjectClass: organizationalUnit\nou: {ou}\n\n'
            for i in range(6):
                ldif += (f'dn: uid=federated{i},ou=people,{DN}\nobjectClass: inetOrgPerson\nuid: federated{i}\n'
                         f'cn: Federated {i}\nsn: Fixture\ngivenName: User{i}\nmail: federated{i}@permora.invalid\n'
                         f'userPassword: {ssha(user_secret)}\n\n')
            ldif += f'dn: cn=team-a,ou=groups,{DN}\nobjectClass: groupOfNames\ncn: team-a\nmember: uid=federated0,ou=people,{DN}\n\n'
            ldif += f'dn: cn=parent-a,ou=groups,{DN}\nobjectClass: groupOfNames\ncn: parent-a\nmember: cn=team-a,ou=groups,{DN}\n\n'
            ldap_write('ldapadd', ldif)
            command(['docker', 'run', '-d', '--rm', '--name', container, '--network', 'host',
                '-e', 'KC_BOOTSTRAP_ADMIN_USERNAME=ci-admin', '-e', 'KC_BOOTSTRAP_ADMIN_PASSWORD',
                '-v', str(root / 'ldap.crt') + ':/opt/keycloak/conf/ldap.crt:ro', 'quay.io/keycloak/keycloak:26.8.1',
                'start-dev', '--http-host=127.0.0.1', '--http-port=18084', '--truststore-paths=/opt/keycloak/conf/ldap.crt'],
                env=dict(os.environ, KC_BOOTSTRAP_ADMIN_PASSWORD=admin_secret))
            started = True
            kc = Keycloak(admin_secret)
            for _ in range(80):
                try:
                    kc.request('GET', '/realms/master/.well-known/openid-configuration', admin=False)
                    break
                except (OSError, AssertionError):
                    time.sleep(2)
            else:
                raise TimeoutError('Disposable Keycloak did not become ready')
            for imported in (True, False):
                test_realm(kc, bind_secret, imported, group_members)
            print(json.dumps({'scope': 'real-openldap-ldaps-and-keycloak', 'checks': len(CHECKS),
                'nonImportedReadOnlyDirectWrites': 'unsupported-and-rejection-verified',
                'microsoftAD': 'not-executed', 'productionApproval': False}), flush=True)
        finally:
            if started:
                subprocess.run(['docker', 'rm', '-f', container], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=30)
            server.terminate()
            try:
                server.wait(timeout=10)
            except subprocess.TimeoutExpired:
                server.kill()
                server.wait(timeout=5)
            logfile.close()


if __name__ == '__main__':
    run()
