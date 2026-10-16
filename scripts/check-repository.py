#!/usr/bin/env python3
"""Structural checks, NOT a compiler, security audit, or integration test."""
import hashlib, json, re, sys
from pathlib import Path
from xml.etree import ElementTree as ET
root = Path(__file__).resolve().parents[1]
failures = []
checks = 0
def check(condition, description):
    global checks
    checks += 1
    if not condition: failures.append(description)
files = [p for p in root.rglob('*') if p.is_file() and not any(part in {'.git','node_modules','bin','obj','.angular','.local','test-results','playwright-report'} for part in p.relative_to(root).parts) and p.name not in {'local.json','local.env'}]
for f in files:
    rel = f.relative_to(root).as_posix()
    check(f.name not in {'.gitlab-ci.yml','azure-pipelines.yml','Jenkinsfile'}, 'Unexpected secondary CI system: '+rel)
    if f.suffix == '.json':
        try: json.loads(f.read_text())
        except (ValueError, UnicodeError) as e: failures.append(rel+': invalid JSON: '+str(e))
        checks += 1
    if f.suffix in {'.csproj','.props','.slnx','.svg'}:
        try: ET.parse(f)
        except ET.ParseError as e: failures.append(rel+': invalid XML: '+str(e))
        checks += 1
    if f.suffix == '.csproj':
        for node in ET.parse(f).iter('ProjectReference'):
            check((f.parent / node.attrib['Include']).exists(), rel+': project reference missing')
    if f.suffix == '.md':
        text=f.read_text()
        for link in re.findall(r'\]\(([^)]+)\)', text):
            if not link.startswith(('https://','http://','#','mailto:')):
                check((f.parent/link.split('#')[0]).exists(), rel+': broken relative link '+link)
    if f.suffix in {'.cs','.ts','.json','.yml','.md','.sh','.py'}:
        text=f.read_text()
        check('BEGIN ' + 'PRIVATE KEY' not in text and not re.search(r'gh[pousr]_[A-Za-z0-9]{25,}',text),rel+': possible credential')
        if rel.startswith('web/src/'):
            check(not re.search(r'localStorage\s*\.',text),rel+': browser token persistence forbidden')
# Exec-form Docker instructions contain JSON; shell quoting alone is not valid JSON.
for line in (root/'deploy/Dockerfile').read_text().splitlines():
    directive, _, payload = line.partition(' ')
    if directive in {'ENTRYPOINT', 'CMD'} and payload.lstrip().startswith('['):
        try:
            command = json.loads(payload)
            check(isinstance(command, list) and bool(command) and all(isinstance(arg, str) for arg in command), 'Docker exec form requires a nonempty string array')
            if directive == 'ENTRYPOINT':
                check(command == ['/bin/sh', '-c', 'exec dotnet "$PERMORA_ENTRYPOINT"'], 'Docker entrypoint must preserve the quoted runtime assembly path')
        except ValueError as error:
            check(False, 'Invalid Docker exec-form JSON: '+str(error))
for node in ET.parse(root/'Permora.slnx').iter('Project'):
    check((root/node.attrib['Path']).exists(),'Missing solution project '+node.attrib['Path'])
api=(root/'src/Permora.Api/Program.cs').read_text()
check('SaveTokens = false' in api,'OIDC tokens must not be persisted in browser tickets')
check('ValidateRequestAsync' in api,'CSRF validation missing')
check('EnableDevelopmentLogin' in (root/'src/Permora.Infrastructure/Configuration.cs').read_text(),'Explicit development gate missing')
check('permora_test_' in (root/'tests/Permora.Tests/PostgresTests.cs').read_text(),'Dedicated test database gate missing')
check('applications = visible.Count' in (root/'src/Permora.Api/ApiEndpoints.cs').read_text(),'Dashboard scoped count contract changed')
check("LIMIT 20" in (root/'src/Permora.Infrastructure/PostgresStore.cs').read_text(),'Pending feed limit contract changed')
license_bytes=(root/'LICENSE').read_bytes()
check(hashlib.sha1(b'blob '+str(len(license_bytes)).encode()+b'\0'+license_bytes).hexdigest()=='261eeb9e9f8b2b4b0d119366dda99c6fd7d35c64','Original Apache license must be preserved')
check('IamApproval? IamApproval = null' in (root/'src/Permora.Core/Models.cs').read_text(), 'IAM approval model missing')
check('iam_approval_required' in (root/'src/Permora.Core/OperationProcessor.cs').read_text(), 'IAM approval activation gate missing')
check('/iam-approval' in (root/'src/Permora.Api/ApiEndpoints.cs').read_text(), 'Central IAM approval endpoint missing')
check('role_still_assigned' in (root/'src/Permora.Infrastructure/KeycloakConnector.cs').read_text(), 'Keycloak deletion recipient check missing')
check('prior.created_at' in (root/'src/Permora.Infrastructure/PostgresStore.cs').read_text(), 'Ordered mutation queue gate missing')
check('/signin-oidc' in (root/'web/proxy.conf.json').read_text(), 'Development OIDC callback proxy missing')
check('--locked-mode' in (root/'scripts/check.sh').read_text(), 'Locked manual restore missing')
workflow = root/'.github/workflows/ci.yml'
check(workflow.exists(), 'CI workflow must exist')
if workflow.exists():
    ci = workflow.read_text(encoding='utf-8')
    for marker in ('pull_request:', 'push:', 'workflow_dispatch:', 'contents: read',
                   'structure:', 'dotnet:', 'angular:', 'keycloak:',
                   'dependency-locks:', 'scope-summary:', 'PERMORA_TEST_POSTGRES:'):
        check(marker in ci, 'Missing CI verification contract: ' + marker)
    check('pull_request_target:' not in ci and 'permissions: write' not in ci,
          'Unsafe privileged CI trigger or permissions')
check((root/'scripts/ci-keycloak-contract.py').is_file(), 'Missing live Keycloak contract')
check((root/'scripts/ci-verify-trx.py').is_file(), 'Missing TRX evidence checker')
check((root/'scripts/ci-require-locks.py').is_file(), 'Missing dependency lock gate')
print(json.dumps({'kind':'structural-only','checks':checks,'failed':len(failures),'failures':failures},indent=2))
sys.exit(1 if failures else 0)
