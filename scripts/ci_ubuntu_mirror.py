#!/usr/bin/env python3
"""Select Ubuntu's signed primary archive on disposable GitHub Linux runners.

Only the known Azure Ubuntu mirror URL changes. Keys, suites, components,
security.ubuntu.com and third-party repositories are left intact.
"""
import os
from pathlib import Path
import platform
import re

AZURE_MIRROR = re.compile(r"(?<!\S)https?://azure\.archive\.ubuntu\.com/ubuntu(?=/|\s|$)")


def primary_archive(source):
    return AZURE_MIRROR.sub("https://archive.ubuntu.com/ubuntu", source)


def main():
    if (os.environ.get("GITHUB_ACTIONS") != "true"
            or os.environ.get("RUNNER_OS") != "Linux"
            or platform.freedesktop_os_release().get("ID") != "ubuntu"
            or platform.machine() != "x86_64" or os.geteuid() != 0):
        raise SystemExit("Only an authorized disposable Ubuntu x64 GitHub runner may change its mirror")
    changed = 0
    for path in (Path("/etc/apt/sources.list"), Path("/etc/apt/sources.list.d/ubuntu.sources"),
                 Path("/etc/apt/mirrors/ubuntu.list")):
        if not path.exists():
            continue
        if path.is_symlink():
            raise SystemExit("Refusing a symlinked Ubuntu sources file")
        original = path.read_text()
        updated = primary_archive(original)
        if updated != original:
            path.write_text(updated)
            changed += 1
    print(f"CI Ubuntu archive selection: {changed} source files updated; existing signature policy preserved")


if __name__ == "__main__":
    main()
