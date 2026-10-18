#!/usr/bin/env python3
"""Scan only current candidate SBOMs with a freshly updated Grype database.

A verified source build and SBOM is required first. A fixable High/Critical
finding blocks even a signed development candidate. High/Critical findings
without identified fixes are preserved for manual review, never called safe.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
from vulnerability_policy import SBOM_NAMES, summarize, verify_manifest, refresh_manifest


def run(args: list[str], timeout: int, env: dict) -> None:
    process = subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                             text=True, timeout=timeout, env=env, check=False)
    if process.returncode:
        # Upstream output may contain package names; never emit complete scanner
        # logs or local paths on an untrusted PR failure.
        raise RuntimeError(f"Grype command failed (exit={process.returncode}; action={args[1] if len(args)>1 else 'scan'})")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--grype", required=True)
    parser.add_argument("--candidate", required=True)
    args = parser.parse_args()
    candidate = Path(args.candidate).resolve()
    scanner = Path(args.grype).resolve()
    if not candidate.is_dir() or not scanner.is_file() or not scanner.is_absolute():
        raise SystemExit("A local candidate and pinned scanner executable are required")
    verify_manifest(candidate)

    environment = os.environ.copy()
    # Prevent accidental reuse of a stale DB between unrelated builds. The
    # explicit update command must succeed or no candidate is signed.
    environment["GRYPE_DB_VALIDATE_AGE"] = "true"
    environment["GRYPE_DB_AUTO_UPDATE"] = "false"
    try:
        run([str(scanner), "db", "update"], 600, environment)
    except (OSError, subprocess.TimeoutExpired, RuntimeError) as error:
        raise SystemExit("Fresh Grype vulnerability database unavailable; signing blocked") from error

    results = []
    for sbom_name in SBOM_NAMES:
        source = candidate / sbom_name
        if not source.is_file() or source.is_symlink():
            raise SystemExit("Expected actual candidate SBOM missing: " + sbom_name)
        output = candidate / sbom_name.replace(".cdx.json", ".vulnerabilities.json")
        if output.exists():
            raise SystemExit("Never overwrite existing candidate vulnerability evidence")
        with output.open("wb") as report:
            try:
                process = subprocess.run([str(scanner), "sbom:" + str(source), "-o", "json"],
                    stdout=report, stderr=subprocess.PIPE, timeout=600,
                    env=environment, check=False)
            except (OSError, subprocess.TimeoutExpired) as error:
                raise SystemExit("Vulnerability matching failed; signing blocked") from error
        if process.returncode:
            raise SystemExit("Vulnerability scanner returned an error; signing blocked")
        try:
            summary = summarize(json.loads(output.read_text(encoding="utf-8")), sbom_name)
        except (OSError, UnicodeError, ValueError, TypeError) as error:
            raise SystemExit("Vulnerability report was incomplete or invalid") from error
        results.append(summary)
        print("GRYPE:", sbom_name,
              "matched=", summary["matchedVulnerabilityCount"],
              "fixable-high-or-critical=", len(summary["fixableHighCritical"]),
              "high-or-critical-manual-review=", len(summary["highCriticalManualReview"]),
              flush=True)

    evidence = {
        "scope": "current built API/Worker/Provisioner images plus frontend SBOM",
        "scanner": "Grype 0.120.1, checksum-pinned, updated vulnerability database",
        "productionApproved": False,
        "fixableHighCriticalBlocking": sum(len(x["fixableHighCritical"]) for x in results),
        "highCriticalManualReview": sum(len(x["highCriticalManualReview"]) for x in results),
        "sources": results
    }
    (candidate / "vulnerability-review.json").write_text(json.dumps(evidence, indent=2, sort_keys=True) + "\n")
    refresh_manifest(candidate)
    verify_manifest(candidate)
    if evidence["fixableHighCriticalBlocking"]:
        raise SystemExit(
            "Fixable High/Critical vulnerabilities found in current images; "
            "release signing blocked until remediated"
        )
    print("No known fixable High/Critical vulnerabilities in the scoped current candidate. "
          "Unknown/unfixed vulnerabilities still require human review. "
          "No production release approval.", flush=True)


if __name__ == "__main__":
    main()
