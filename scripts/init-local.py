#!/usr/bin/env python3
"""Create development-only local configuration; never overwrite existing secrets."""
import json, os, secrets
from pathlib import Path
root = Path(__file__).resolve().parents[1]
config, env = root / 'config/local.json', root / 'deploy/local.env'
if config.exists() or env.exists():
    raise SystemExit('Local configuration already exists; it was not changed.')
local = root / '.local'
local.mkdir(mode=0o700, exist_ok=True)
password, admin = secrets.token_hex(24), secrets.token_hex(24)
settings = {
    'ConnectionStrings': {'Portal': f'Host=127.0.0.1;Database=permora;Username=permora;Password={password}'},
    'Portal': {
        'Authority': 'http://localhost:8080/realms/enterprise', 'ClientId': 'permora-portal',
        'PublicOrigin': 'http://localhost:4200', 'DataProtectionPath': str(local / 'keys'),
        'AdministratorSubjects': ['demo-owner'], 'EnableDevelopmentLogin': True,
        'DevelopmentSubject': 'demo-owner', 'AllowHttpInDevelopment': True,
        'RealmTeams': {'enterprise': ['10000000-0000-4000-8000-000000000001']}
    },
    'Keycloak': {'AllowHttpInDevelopment': True, 'Realms': {}}
}
for path, value in [(config, json.dumps(settings, indent=2) + '\n'),
                    (env, f'POSTGRES_PASSWORD={password}\nKEYCLOAK_ADMIN_PASSWORD={admin}\n')]:
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, 'w') as stream:
        stream.write(value)
print('Created config/local.json and deploy/local.env. Do not commit either file.')
print('The development login is fixed to demo-owner. It is NOT Keycloak SSO.')
