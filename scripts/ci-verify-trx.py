#!/usr/bin/env python3
"""Verify the real xUnit TRX output; never silently accept skipped PostgreSQL tests."""
import json
import os
import sys
import xml.etree.ElementTree as et
from pathlib import Path

folder = Path(sys.argv[1] if len(sys.argv) > 1 else "tests/Permora.Tests/TestResults")
files = list(folder.rglob("*.trx"))
if len(files) != 1:
    raise SystemExit(f"Expected exactly one xUnit TRX under {folder}, found {len(files)}")
root = et.parse(files[0]).getroot()
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
cases = root.findall(".//t:Results/t:UnitTestResult", ns)
if not cases:
    raise SystemExit("No xUnit test results were recorded")
failed = [case for case in cases if case.get("outcome") != "Passed"]
postgres = [case for case in cases if "Migration_atomicity_audit_directory_and_dashboard_use_real_postgresql"
            in case.get("testName", "")]
summary = {
    "kind": "dotnet-real-tests",
    "total": len(cases),
    "passed": len(cases) - len(failed),
    "failed_or_skipped": [{"test": c.get("testName"), "outcome": c.get("outcome")} for c in failed],
    "postgres_integration": [{"test": c.get("testName"), "outcome": c.get("outcome")} for c in postgres],
}
(folder / "ci-summary.json").write_text(json.dumps(summary, indent=2) + "\n")
print(json.dumps(summary, indent=2))
if failed or len(postgres) != 1 or postgres[0].get("outcome") != "Passed":
    raise SystemExit("C# tests failed or PostgreSQL integration was not executed and passed")
with open(os.environ.get("GITHUB_STEP_SUMMARY", os.devnull), "a") as output:
    output.write(f"- .NET: {len(cases)} tests passed, including the actual PostgreSQL test.\n")
