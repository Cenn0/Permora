#!/usr/bin/env python3
"""Build traceable application candidates, actual-image SBOMs and license evidence.

No production approval is implied. Framework-dependent archives do not include
OS image layers. The frontend inventory is the resolved production dependency
closure and may conservatively include code removed by tree shaking.
"""
from __future__ import annotations
import argparse
import gzip
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tarfile
import tempfile
import xml.etree.ElementTree as ET
from release_sbom import merge, validate_graph

ROOT = Path(__file__).resolve().parents[1]
PROJECTS = ('Permora.Api', 'Permora.Worker', 'Permora.Provisioner')
PERMISSIVE = {'MIT', 'Apache-2.0', 'BSD-2-Clause', 'BSD-3-Clause', 'ISC', '0BSD',
              'CC0-1.0', 'Unlicense', 'Zlib', 'PostgreSQL', 'Python-2.0'}


def run(args, *, cwd=ROOT):
    return subprocess.check_output(args, cwd=cwd, text=True, timeout=1200).strip()


def digest(path):
    with Path(path).open('rb') as file:
        return hashlib.file_digest(file, 'sha256').hexdigest()


def json_write(path, value):
    Path(path).write_text(json.dumps(value, indent=2, sort_keys=True) + '\n')


def safe_name(value):
    if not re.fullmatch(r'[A-Za-z0-9_+@.\-/]+', value) or '..' in value or value.startswith('/'):
        raise ValueError('Unexpected package identifier in license metadata')
    return value.replace('/', '_')


def safe_archive(source, target):
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
        if source.is_file() and not source.is_symlink() and re.match(r'(?i)^(licen[cs]e|copying|notice)([._-].*)?$', source.name):
            target.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, target / source.name)
            files.append(source.name)
    return files


def nuget_licenses(application, notices):
    packages = {}
    for deps in application.glob('*.deps.json'):
        for name, value in json.loads(deps.read_text()).get('libraries', {}).items():
            if value.get('type') == 'package': packages[name] = value
    result = []
    cache = Path(os.environ.get('NUGET_PACKAGES', str(Path.home() / '.nuget/packages')))
    for identity in sorted(packages):
        name, version = identity.rsplit('/', 1)
        safe_name(name); safe_name(version)
        package = cache / name.lower() / version.lower()
        specs = list(package.glob('*.nuspec'))
        expression = declared_file = None
        if len(specs) == 1:
            for node in ET.parse(specs[0]).iter():
                if node.tag.rsplit('}', 1)[-1] == 'license':
                    if node.attrib.get('type') == 'expression': expression = (node.text or '').strip()
                    elif node.attrib.get('type') == 'file': declared_file = (node.text or '').strip()
        destination = notices / 'nuget' / safe_name(name) / safe_name(version)
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
    if any(output.iterdir()): raise SystemExit('Output must be empty; existing evidence is never overwritten')
    sha = run(['git', 'rev-parse', 'HEAD'])
    if not re.fullmatch('[0-9a-f]{40}', sha): raise SystemExit('Exact source commit required')
    run(['dotnet', 'restore', 'Permora.slnx', '--locked-mode'])
    images, licenses = [], []
    with tempfile.TemporaryDirectory(prefix='permora-release-') as temp:
        temporary = Path(temp)
        frontend = temporary / 'frontend-production'
        frontend.mkdir()
        for name in ('package.json', 'package-lock.json'): shutil.copyfile(ROOT / 'web' / name, frontend / name)
        run(['npm', 'ci', '--omit=dev', '--ignore-scripts', '--no-audit'], cwd=frontend)
        run([args.syft, 'scan', 'dir:' + str(frontend), '-o', 'cyclonedx-json@1.6=' + str(output / 'frontend.cdx.json')])
        notices = temporary / 'notices'
        notices.mkdir()
        shutil.copyfile(ROOT / 'LICENSE', notices / 'Permora-LICENSE')
        lock = json.loads((frontend / 'package-lock.json').read_text())
        for path, item in sorted(lock.get('packages', {}).items()):
            if not path: continue
            package = (frontend / path).resolve()
            if not package.is_relative_to(frontend.resolve()): raise ValueError('Escaping package path')
            if not (package / 'package.json').is_file(): continue
            manifest = json.loads((package / 'package.json').read_text())
            name = manifest.get('name', path.rsplit('node_modules/', 1)[-1])
            declared = manifest.get('license')
            if isinstance(declared, dict): declared = declared.get('type')
            files = license_texts(package, notices / 'npm' / safe_name(name) / safe_name(manifest['version']))
            licenses.append({'ecosystem': 'npm', 'name': name, 'version': manifest['version'],
                'licenseExpression': declared, 'licenseFiles': files,
                'reviewRequired': declared not in PERMISSIVE,
                'reason': None if declared in PERMISSIVE else 'unclassified-expression-or-file-license'})
        for project in PROJECTS:
            label = project.rsplit('.', 1)[1].lower()
            image = 'permora-candidate-' + label + ':' + sha
            run(['docker', 'build', '-f', 'deploy/Dockerfile', '--build-arg', 'PROJECT=' + project, '-t', image, '.'])
            inspect = json.loads(run(['docker', 'image', 'inspect', image]))[0]
            if inspect.get('Config', {}).get('User') in (None, '', 'root', '0'): raise SystemExit('Root runtime forbidden')
            run([args.syft, 'scan', 'docker:' + image,
                 '-o', 'cyclonedx-json@1.6=' + str(output / (label + '.image.cdx.json')),
                 '-o', 'spdx-json=' + str(output / (label + '.image.spdx.json'))])
            container = run(['docker', 'create', image])
            application = temporary / label
            application.mkdir()
            try:
                run(['docker', 'cp', container + ':/app/.', str(application)])
                os_notices = notices / 'os' / label
                os_notices.mkdir(parents=True)
                run(['docker', 'cp', container + ':/usr/share/doc/.', str(os_notices)])
            finally: run(['docker', 'rm', container])
            dll = application / (project + '.dll')
            if not dll.is_file(): raise SystemExit('Actual published DLL missing')
            if project == 'Permora.Api' and not (application / 'wwwroot/index.html').is_file():
                raise SystemExit('Actual Angular production bundle missing')
            licenses.extend(nuget_licenses(application, notices))
            shutil.copyfile(ROOT / 'LICENSE', application / 'LICENSE-Permora')
            json_write(output / (label + '.files.json'), [{'path': p.relative_to(application).as_posix(), 'sha256': digest(p)}
                for p in sorted(application.rglob('*')) if p.is_file() and not p.is_symlink()])
            safe_archive(application, output / (project + '-' + sha[:12] + '.tar.gz'))
            images.append({'project': project, 'imageId': inspect['Id'],
                'rootFsLayers': inspect.get('RootFS', {}).get('Layers', []), 'architecture': inspect.get('Architecture'),
                'os': inspect.get('Os'), 'entryAssemblySha256': digest(dll), 'runtimeUser': inspect['Config']['User']})
        unique = {(r['ecosystem'], r['name'], r['version']): r for r in licenses}
        licenses = [unique[key] for key in sorted(unique)]
        json_write(output / 'application-licenses.json', {'packages': licenses,
            'manualReviewRequired': [r for r in licenses if r['reviewRequired']],
            'osCopyrightEvidence': 'third-party-notices.tar.gz', 'legalApproval': False})
        safe_archive(notices, output / 'third-party-notices.tar.gz')
    sources = {file.name: json.loads(file.read_text()) for file in sorted(output.glob('*.cdx.json'))}
    combined = merge(sha, sources)
    component_count = validate_graph(combined)
    json_write(output / 'permora-artifacts.cdx.json', combined)
    json_write(output / 'build-manifest.json', {'sourceCommit': sha, 'scanner': run([args.syft, 'version']),
        'images': images, 'artifactType': 'framework-dependent-application-candidates', 'runtimeIncluded': False,
        'osImagesPublished': False, 'productionApproved': False,
        'licenseReviewOutstanding': sum(r['reviewRequired'] for r in licenses)})
    (output / 'README-CANDIDATE.txt').write_text(
        'Permora build candidates. NOT a production-approved release.\n'
        'Archives require a compatible .NET/ASP.NET runtime; OS images are scanned but not distributed.\n'
        'Verify signed provenance, source commit and SHA256SUMS before using files.\n'
        'application-licenses.json records declarations and outstanding review requirements.\n'
        'A signature proves builder identity and bytes, not security or legal approval.\n')
    (output / 'SHA256SUMS').write_text('\n'.join(digest(p) + '  ' + p.name for p in sorted(output.iterdir()) if p.is_file()) + '\n')
    print(json.dumps({'sourceCommit': sha, 'imagesScanned': len(images), 'components': component_count,
        'applicationPackages': len(licenses), 'licenseReviewOutstanding': sum(r['reviewRequired'] for r in licenses),
        'productionApproved': False}), flush=True)


if __name__ == '__main__': main()
