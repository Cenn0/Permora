#!/usr/bin/env python3
"""Generate a deterministic CycloneDX 1.6 component INVENTORY from committed lockfiles.

Offline, no secrets, no package downloads, and no third-party Python dependencies.
Coverage is limited to NuGet + npm lockfiles; runtime image/OS libraries, licenses
of NuGet packages, deployed components, vulnerabilities and dependency relations
are NOT claimed to be complete or verified.
"""
import argparse
import base64
import hashlib
import json
from pathlib import Path
from urllib.parse import quote


def hex_digest(integrity):
    if not integrity:
        return None
    token = integrity.split()[0]
    if "-" not in token:
        return None
    algorithm, value = token.split("-", 1)
    algorithms = {"sha1": ("SHA-1", 20), "sha256": ("SHA-256", 32),
                  "sha384": ("SHA-384", 48), "sha512": ("SHA-512", 64)}
    if algorithm.lower() not in algorithms:
        return None
    canonical, size = algorithms[algorithm.lower()]
    raw = base64.b64decode(value, validate=True)
    if len(raw) != size:
        raise ValueError("Unexpected integrity digest length")
    return {"alg": canonical, "content": raw.hex()}


def generate(root):
    root = Path(root)
    components = {}
    sources = {}
    nuget_lockfiles = sorted((root / "src").glob("*/packages.lock.json"))
    nuget_lockfiles += sorted((root / "tests").glob("*/packages.lock.json"))
    npm_lockfile = root / "web/package-lock.json"
    if not nuget_lockfiles or not npm_lockfile.is_file():
        raise ValueError("NuGet or npm lockfiles are missing")

    def put(purl, name, version, origin, digest=None, license_name=None, development=False):
        if not name or not version:
            raise ValueError("Dependency is missing an exact name/version")
        component = {"type": "library", "bom-ref": purl, "name": name,
                     "version": version, "purl": purl}
        if digest:
            component["hashes"] = [digest]
        if license_name and isinstance(license_name, str):
            # npm's lockfile license strings are not guaranteed to be SPDX IDs.
            component["licenses"] = [{"license": {"name": license_name}}]
        if development:
            component["properties"] = [{"name": "permora:development", "value": "true"}]
        if purl in components and components[purl].get("hashes") != component.get("hashes"):
            raise ValueError("Conflicting resolved dependency hash: " + purl)
        if purl not in components:
            components[purl] = component
        sources.setdefault(purl, set()).add(origin)

    for lock in nuget_lockfiles:
        doc = json.loads(lock.read_text("utf-8"))
        if doc.get("version") != 2:
            raise ValueError("Unsupported NuGet lockfile format")
        for framework, entries in doc.get("dependencies", {}).items():
            for name, dependency in entries.items():
                if dependency.get("type") == "Project":
                    continue  # represented by its own transitive lockfile
                version = dependency.get("resolved")
                hash_value = dependency.get("contentHash")
                digest = hex_digest("sha512-" + hash_value) if hash_value else None
                purl = "pkg:nuget/" + quote(name, safe="") + "@" + quote(version or "", safe="")
                put(purl, name, version, lock.relative_to(root).as_posix() + ":" + framework,
                    digest, development=lock.parts[-3] == "tests")

    npm = json.loads(npm_lockfile.read_text("utf-8"))
    if npm.get("lockfileVersion") != 3:
        raise ValueError("Unsupported npm lockfile format")
    for key, package in npm.get("packages", {}).items():
        if not key or package.get("link"):
            continue
        if "node_modules/" not in key:
            continue
        name = key.rsplit("node_modules/", 1)[1]
        version = package.get("version")
        purl = "pkg:npm/" + quote(name, safe="/") + "@" + quote(version or "", safe="")
        put(purl, name, version, "web/package-lock.json:" + key,
            hex_digest(package.get("integrity")), package.get("license"),
            bool(package.get("dev")))

    if not components or not any(p.startswith("pkg:nuget/") for p in components) \
            or not any(p.startswith("pkg:npm/") for p in components):
        raise ValueError("No NuGet/npm components were discovered")
    for purl, c in components.items():
        c["properties"] = sorted(c.get("properties", []) +
            [{"name": "permora:lockfileSources", "value": ", ".join(sorted(sources[purl]))}],
            key=lambda p: p["name"])
    return {
        "bomFormat": "CycloneDX", "specVersion": "1.6", "version": 1,
        "metadata": {
            "component": {"type": "application", "name": "Permora", "version": "0.1.0"},
            "tools": {"components": [{"type": "application", "name": "permora-lockfile-inventory",
                                     "version": "1.0.0"}]}
        },
        "components": [components[p] for p in sorted(components)]
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    doc = generate(args.root)
    encoded = (json.dumps(doc, indent=2, ensure_ascii=False, sort_keys=True) + "\n").encode("utf-8")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(encoded)
    actual = json.loads(args.output.read_text("utf-8"))
    assert actual["bomFormat"] == "CycloneDX" and len(actual["components"]) > 0
    count_nuget = sum(c["purl"].startswith("pkg:nuget/") for c in actual["components"])
    count_npm = sum(c["purl"].startswith("pkg:npm/") for c in actual["components"])
    print(f"Lockfile component inventory: NuGet={count_nuget}, npm={count_npm}; "
          f"SHA-256={hashlib.sha256(encoded).hexdigest()}")
    print("Excluded: container/OS components, advisory and transitive license verification, "
          "provenance and production/runtime dependency graph.")


if __name__ == "__main__":
    main()
