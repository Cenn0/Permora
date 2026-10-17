#!/usr/bin/env python3
"""Install a fixed scanner from its verified upstream release, without curl|sh.

The checksum-manifest digest was read from GitHub's immutable v1.54.1 release
asset metadata. Both the manifest and selected Linux binary are authenticated
against pinned digests before execution. No archive paths are extracted blindly.
"""
from __future__ import annotations
import hashlib
import io
import os
from pathlib import Path
import sys
import tarfile
import urllib.request

VERSION = '1.54.1'
MANIFEST_SHA256 = '7577f0d0b3fb16f65a2e56b413862f69aa5eaa2379d5760cb73d691ca7986059'
BASE = 'https://github.com/anchore/syft/releases/download/v' + VERSION + '/'


def fetch(name: str, limit: int) -> bytes:
    with urllib.request.urlopen(BASE + name, timeout=120) as response:
        data = response.read(limit + 1)
    if len(data) > limit:
        raise ValueError('Scanner download exceeded the expected size bound')
    return data


def main() -> None:
    if len(sys.argv) != 2:
        raise SystemExit('Usage: install-syft.py ABSOLUTE_DESTINATION_DIRECTORY')
    destination = Path(sys.argv[1])
    if not destination.is_absolute():
        raise SystemExit('Destination must be absolute')
    destination.mkdir(parents=True, exist_ok=True)
    manifest = fetch('syft_' + VERSION + '_checksums.txt', 65536)
    if hashlib.sha256(manifest).hexdigest() != MANIFEST_SHA256:
        raise SystemExit('Pinned scanner checksum manifest verification failed')
    name = 'syft_' + VERSION + '_linux_amd64.tar.gz'
    matches = [line.split()[0] for line in manifest.decode().splitlines()
               if len(line.split()) == 2 and line.split()[1].lstrip('*') == name]
    if len(matches) != 1:
        raise SystemExit('The verified checksum manifest did not uniquely identify the scanner')
    data = fetch(name, 120 * 1024 * 1024)
    if hashlib.sha256(data).hexdigest() != matches[0]:
        raise SystemExit('Pinned scanner binary verification failed')
    with tarfile.open(fileobj=io.BytesIO(data), mode='r:gz') as archive:
        members = [item for item in archive.getmembers() if item.name == 'syft' and item.isfile()]
        if len(members) != 1 or members[0].size > 250 * 1024 * 1024:
            raise SystemExit('Unexpected scanner archive structure')
        stream = archive.extractfile(members[0])
        if stream is None:
            raise SystemExit('Verified scanner payload missing')
        target = destination / 'syft'
        target.write_bytes(stream.read())
        target.chmod(0o755)
    print('Verified Syft ' + VERSION + ' installed', flush=True)


if __name__ == '__main__':
    main()
