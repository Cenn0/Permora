#!/usr/bin/env python3
"""Install a Grype release with its immutable upstream checksum manifest.

No curl|sh, no generic tar extraction, and no unpinned executable download.
The archive digest is independently listed by the official upstream release.
This is a CI build-time tool; it is never part of Permora's runtime images.
"""
from __future__ import annotations

import hashlib
import io
from pathlib import Path
import sys
import tarfile
import urllib.request

VERSION = "0.120.1"
MANIFEST_SHA256 = "c25b6095265b44fd9bbb9dbe76aca8daa394c5f0490793d2b52b16be4f5b71b1"
ARCHIVE_SHA256 = "0a9ee97ef5ae2ee953b0a80098105052e846cdbe319a57d808b519c33cd1343d"
BASE = "https://github.com/anchore/grype/releases/download/v" + VERSION + "/"


def fetch(filename: str, maximum: int) -> bytes:
    with urllib.request.urlopen(BASE + filename, timeout=120) as response:
        data = response.read(maximum + 1)
    if len(data) > maximum:
        raise ValueError("Scanner asset exceeds the expected size bound")
    return data


def main() -> None:
    if len(sys.argv) != 2:
        raise SystemExit("Usage: install-grype.py ABSOLUTE_DESTINATION_DIRECTORY")
    target_dir = Path(sys.argv[1])
    if not target_dir.is_absolute():
        raise SystemExit("Scanner destination must be an absolute directory")
    target_dir.mkdir(parents=True, exist_ok=True)

    manifest = fetch(f"grype_{VERSION}_checksums.txt", 65536)
    if hashlib.sha256(manifest).hexdigest() != MANIFEST_SHA256:
        raise SystemExit("Pinned Grype checksum manifest did not verify")

    archive_name = f"grype_{VERSION}_linux_amd64.tar.gz"
    matches = [parts[0] for line in manifest.decode("utf-8").splitlines()
               if (parts := line.split()) and len(parts) == 2
               and parts[1].lstrip("*") == archive_name]
    if len(matches) != 1 or matches[0].lower() != ARCHIVE_SHA256:
        raise SystemExit("Pinned Grype binary identity mismatches signed release metadata")
    data = fetch(archive_name, 65 * 1024 * 1024)
    if hashlib.sha256(data).hexdigest() != ARCHIVE_SHA256:
        raise SystemExit("Pinned Grype archive checksum does not match")
    with tarfile.open(fileobj=io.BytesIO(data), mode="r:gz") as archive:
        found = [entry for entry in archive.getmembers()
                 if entry.isfile() and entry.name == "grype"]
        if len(found) != 1 or found[0].size > 200 * 1024 * 1024:
            raise SystemExit("Scanner archive has an unexpected structure")
        stream = archive.extractfile(found[0])
        if stream is None:
            raise SystemExit("Scanner binary missing")
        output = target_dir / "grype"
        with output.open("wb") as file:
            while chunk := stream.read(1024 * 1024):
                file.write(chunk)
        output.chmod(0o755)
    print("Verified upstream Grype " + VERSION + " installed", flush=True)


if __name__ == "__main__":
    main()
