#!/usr/bin/env python3
"""Fail the release reproducibility gate until real, reviewed lockfiles are committed."""
import json
import os
from pathlib import Path

root = Path(__file__).resolve().parents[1]
needed = [root / "web" / "package-lock.json"]
needed += [path.parent / "packages.lock.json" for path in [*root.glob("src/**/*.csproj"), *root.glob("tests/**/*.csproj")]]
missing = [p for p in needed if not p.is_file()]
invalid = []
for path in needed:
    if not path.is_file():
        continue
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
        if path.name == "package-lock.json" and (document.get("lockfileVersion", 0) < 3
            or not isinstance(document.get("packages"), dict)):
            invalid.append(str(path.relative_to(root)))
        if path.name == "packages.lock.json" and not document.get("dependencies"):
            invalid.append(str(path.relative_to(root)))
    except (ValueError, UnicodeError):
        invalid.append(str(path.relative_to(root)))
for path in missing:
    print(f"::error file={path.relative_to(root)}::Missing committed dependency lockfile")
for path in invalid:
    print(f"::error file={path}::Invalid dependency lockfile")
with open(os.environ.get("GITHUB_STEP_SUMMARY", os.devnull), "a") as output:
    output.write("## Dependency reproducibility\n\n")
    output.write(f"Committed files required: {len(needed)}; missing: {len(missing)}; invalid: {len(invalid)}.\n\n")
    if missing:
        output.write("Download the generated dependency artifacts from the .NET and Angular jobs, review their exact package resolution, and commit the lockfiles.\n")
if missing or invalid:
    raise SystemExit("Dependency reproducibility gate failed; diagnostic builds may still run in other jobs")
print(f"{len(needed)} committed lockfiles structurally verified")
