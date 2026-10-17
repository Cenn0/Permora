#!/usr/bin/env python3
"""Create traceable candidate artifacts; never grant a production approval.

Scans actual locally built images (OS + runtime + packages), plus the exact
resolved frontend production dependency closure. Exports framework-dependent
applications, not OS image tarballs. Records license evidence and review gaps.
No secret files or untracked source files are copied into candidate artifacts.
"""
from __future__ import annotations
import argparse
import gzip
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import tarfile
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
PROJECTS = ('Permora.Api', 'Permora.Worker', 'Permora.Provisioner')
PERMISSIVE = {'MIT', 'Apache-2.0', 'BSD-2-Clause', 'BSD-3-Clause', 'ISC', '0BSD',
              'CC0-1.0', 'Unlicense', 'Zlib', 'PostgreSQL', 'Python-2.0'}


def run(args, *, cwd=ROOT, output=None):
    if output:
        with Path(output).open('wb') as stream:
            subprocess.run(args, cwd=cwd, stdout=stream, check=True, timeout=1200)
        return ''
    return subprocess.check_output(args, cwd=cwd, text=True, timeout=1200).strip()


def digest(path):
    with Path(path).open('rb') as file:
        return hashlib.file_digest(file, 'sha256').hexdigest()


def json_write(path, value):
    Path(path).write_text(json.dumps(value, indent=2, sort_keys=True) + '\n')


def safe_archive(source, target):
    # Stable metadata; binary contents remain bound to this exact build.
    with Path(target).open('wb') as raw, gzip.GzipFile(fileobj=raw, mode='wb', mtime=0) as gz:
        with tarfile.open(fileobj=gz, mode='w|') as archive:
            for path in sorted(Path(source).rglob('*')):
                if not path.is_file() or path.is_symlink():
                    continue
                entry = archive.gettarinfo(str(path), arcname=path.relative_to(source).as_posix())
                entry.uid = entry.gid = entry.mtime = 0
                entry.uname = entry.gname = ''
                with path.open('rb') as stream:
                    archive.addfile(entry, stream)


def license_texts(package, target):
    files = []
    for source in package.iterdir():
        if source.is_file() and re.match(r'(?i)^(licen[cs]e|copying|notice)([._-].*)?$', source.name):
            target.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, target / source.name)
            files.append(source.name)
    return files


def nuget_licenses(application, notices):
    packages = {}
    for deps in application.glob('*.deps.json'):
        for name, value in json.loads(deps.read_text()).get('libraries', {}).items():
            if value.get('type') == 'package':
                packages[name] = value
    result = []
    cache = Path(os.environ.get('NUGET_PACKAGES', str(Path.home() / '.nuget/packages')))
    for identity in sorted(packages):
        name, version = identity.rsplit('/', 1)
        package = cache / name.lower() / version.lower()
        specs = list(package.glob('*.nuspec'))
        expression = None
        declared_file = None
        if len(specs) == 1:
            tree = ET.parse(specs[0])
            for node in tree.iter():
                if node.tag.rsplit('}', 1)[-1] == 'license':
                    if node.attrib.get('type') == 'expression':
                        expression = (node.text or '').strip()
                    elif node.attrib.get('type') == 'file':
                        declared_file = (node.text or '').strip()
        destination = notices / 'nuget' / name / version
        texts = license_texts(package, destination) if package.exists() else []
        if declared_file:
            source = (package / declared_file).resolve()
            if source.is_relative_to(package.resolve()) and source.is_file():
                destination.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(source, destination / 'declared-license.txt')
                texts.append('declared-license.txt')
        if specs:
            destination.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(specs[0], destination / 'package.nuspec')
        result.append({'ecosystem': 'nuget', 'name': name, 'version': version,
            'licenseExpression': expression, 'licenseFiles': sorted(set(texts)),
            'reviewRequired': expression not in PERMISSIVE,
            'reason': None if expression in PERMISSIVE else 'unclassified-expression-or-file-license'})
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', required=True)
    parser.add_argument('--syft', required=True)
    args = parser.parse_args()
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    if any(output.iterdir()):
        raise SystemExit('Candidate output directory must be empty; never overwrite evidence')
    source_sha = run(['git', 'rev-parse', 'HEAD'])
    if not re.fullmatch('[0-9a-f]{40}', source_sha):
        raise SystemExit('An exact Git source commit is required')
    # Resolve NuGet metadata from the exact committed locks for license evidence.
    run(['dotnet', 'restore', 'Permora.slnx', '--locked-mode'])
    image_evidence = []
    all_components = []
    all_dependencies = []
    license_report = []
    with tempfile.TemporaryDirectory(prefix='permora-release-') as temp:
        temporary = Path(temp)
        frontend = temporary / 'frontend-production'
        frontend.mkdir()
        for name in ('package.json', 'package-lock.json'):
            shutil.copyfile(ROOT / 'web' / name, frontend / name)
        run(['npm', 'ci', '--omit=dev', '--ignore-scripts', '--no-audit'], cwd=frontend)
        run([args.syft, 'scan', 'dir:' + str(frontend), '-o', 'cyclonedx-json=' + str(output / 'frontend.cdx.json')])
        notices = temporary / 'notices'
        notices.mkdir()
        shutil.copyfile(ROOT / 'LICENSE', notices / 'Permora-LICENSE')
        lock = json.loads((frontend / 'package-lock.json').read_text())
        for path, item in sorted(lock.get('packages', {}).items()):
            if not path or not (frontend / path / 'package.json').is_file():
                continue
            manifest = json.loads((frontend / path / 'package.json').read_text())
            name = manifest.get('name', path.rsplit('node_modules/', 1)[-1])
            declared = manifest.get('license')
            if isinstance(declared, dict):
                declared = declared.get('type')
            files = license_texts(frontend / path, notices / 'npm' / name.replace('/', '_') / manifest['version'])
            license_report.append({'ecosystem': 'npm', 'name': name, 'version': manifest['version'],
                'licenseExpression': declared, 'licenseFiles': files,
                'reviewRequired': declared not in PERMISSIVE,
                'reason': None if declared in PERMISSIVE else 'unclassified-expression-or-file-license'})
        for project in PROJECTS:
            label = project.rsplit('.', 1)[1].lower()
            image = 'permora-candidate-' + label + ':' + source_sha
            run(['docker', 'build', '-f', 'deploy/Dockerfile', '--build-arg', 'PROJECT=' + project, '-t', image, '.'])
            inspect = json.loads(run(['docker', 'image', 'inspect', image]))[0]
            if inspect.get('Config', {}).get('User') in (None, '', 'root', '0'):
                raise SystemExit('Candidate image may not run as root')
            run([args.syft, 'scan', 'docker:' + image,
                 '-o', 'cyclonedx-json=' + str(output / (label + '.image.cdx.json')),
                 '-o', 'spdx-json=' + str(output / (label + '.image.spdx.json'))])
            container = run(['docker', 'create', image])
            application = temporary / label
            application.mkdir()
            try:
                run(['docker', 'cp', container + ':/app/.', str(application)])
                # Copyright evidence for the ACTUAL scanned OS, not a claimed review.
                os_notices = notices / 'os' / label
                os_notices.mkdir(parents=True)
                run(['docker', 'cp', container + ':/usr/share/doc/.', str(os_notices)])
            finally:
                run(['docker', 'rm', container])
            dll = application / (project + '.dll')
            if not dll.is_file():
                raise SystemExit('Actual published application DLL is missing')
            if project == 'Permora.Api' and not (application / 'wwwroot/index.html').is_file():
                raise SystemExit('Actual Angular production bundle is missing')
            license_report.extend(nuget_licenses(application, notices))
            files = [{'path': p.relative_to(application).as_posix(), 'sha256': digest(p)}
                     for p in sorted(application.rglob('*')) if p.is_file() and not p.is_symlink()]
            json_write(output / (label + '.files.json'), files)
            shutil.copyfile(ROOT / 'LICENSE', application / 'LICENSE-Permora')
            safe_archive(application, output / (project + '-' + source_sha[:12] + '.tar.gz'))
            image_evidence.append({'project': project, 'imageId': inspect['Id'],
                'rootFsLayers': inspect.get('RootFS', {}).get('Layers', []),
                'architecture': inspect.get('Architecture'), 'os': inspect.get('Os'),
                'entryAssemblySha256': digest(dll), 'runtimeUser': inspect['Config']['User']})
        for bom in sorted(output.glob('*.cdx.json')):
            doc = json.loads(bom.read_text())
            prefix = bom.name + ':'
            components = doc.get('components', [])
            if not components:
                raise SystemExit('Scanner returned an empty component inventory: ' + bom.name)
            for component in components:
                original = component.get('bom-ref', component.get('purl', component['name']))
                component['bom-ref'] = prefix + original
                component.setdefault('properties', []).append({'name': 'permora:evidence-source', 'value': bom.name})
                all_components.append(component)
            for dependency in doc.get('dependencies', []):
                all_dependencies.append({'ref': prefix + dependency['ref'],
                    'dependsOn': [prefix + ref for ref in dependency.get('dependsOn', [])]})
        unique = {(r['ecosystem'], r['name'], r['version']): r for r in license_report}
        license_report = [unique[key] for key in sorted(unique)]
        json_write(output / 'application-licenses.json', {'packages': license_report,
            'manualReviewRequired': [r for r in license_report if r['reviewRequired']],
            'osCopyrightEvidence': 'third-party-notices.tar.gz',
            'legalApproval': False})
        safe_archive(notices, output / 'third-party-notices.tar.gz')
    # This conservative frontend closure may include tree-shaken packages. It is
    # explicitly not a precise source-map-to-minified-byte ownership assertion.
    json_write(output / 'permora-artifacts.cdx.json', {'bomFormat': 'CycloneDX', 'specVersion': '1.6', 'version': 1,
        'metadata': {'component': {'type': 'application', 'name': 'Permora', 'version': source_sha},
            'properties': [{'name': 'permora:scope', 'value': 'actual-api-worker-provisioner-images-plus-resolved-production-frontend-closure'},
                           {'name': 'permora:production-approval', 'value': 'false'}]},
        'components': all_components, 'dependencies': all_dependencies})
    json_write(output / 'build-manifest.json', {'sourceCommit': source_sha, 'scanner': run([args.syft, 'version']),
        'images': image_evidence, 'artifactType': 'framework-dependent-application-candidates',
        'runtimeIncluded': False, 'osImagesPublished': False, 'productionApproved': False,
        'licenseReviewOutstanding': sum(r['reviewRequired'] for r in license_report)})
    (output / 'README-CANDIDATE.txt').write_text(
        'Permora build candidates. NOT a production-approved release.\n'
        'Archives require a compatible .NET/ASP.NET runtime; operating system images are scanned but not distributed.\n'
        'Verify the signed provenance, source commit and SHA256SUMS before using any file.\n'
        'See application-licenses.json for declaration evidence and unresolved review requirements.\n'
        'A signature proves builder identity and bytes, not security or legal approval.\n')
    checksums = [digest(path) + '  ' + path.name for path in sorted(output.iterdir()) if path.is_file()]
    (output / 'SHA256SUMS').write_text('\n'.join(checksums) + '\n')
    print(json.dumps({'sourceCommit': source_sha, 'imagesScanned': len(image_evidence),
        'components': len(all_components), 'applicationPackages': len(license_report),
        'licenseReviewOutstanding': sum(r['reviewRequired'] for r in license_report),
        'productionApproved': False}), flush=True)


if __name__ == '__main__':
    main()
