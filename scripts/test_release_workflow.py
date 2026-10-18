"""Release workflow trigger coverage; no third-party dependencies.

Ensures changes to scanner code/security policy cause a new signed-candidate
validation attempt on main, not merely an unrelated green older candidate.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
WORKFLOW = ROOT / ".github/workflows/release-integrity.yml"


class ReleaseWorkflowTriggerTests(unittest.TestCase):
    def test_scanner_and_policy_edits_trigger_both_pr_checks_and_main_candidates(self):
        source = WORKFLOW.read_text(encoding="utf-8")
        self.assertIn("  pull_request:", source)
        self.assertIn("  push:", source)
        pr_block = source.split("  pull_request:", 1)[1].split("  push:", 1)[0]
        push_block = source.split("  push:", 1)[1].split("  workflow_dispatch:", 1)[0]
        for path in (
            "scripts/install-grype.py",
            "scripts/vulnerability_policy.py",
            "scripts/test_vulnerability_policy.py",
            "scripts/scan-release-vulnerabilities.py",
            "scripts/install-syft.py",
            "scripts/build-release-evidence.py",
        ):
            # build-release-evidence.py and scan-release-vulnerabilities.py
            # intentionally use an exact glob-covered source path.
            for block in (pr_block, push_block):
                entries = [line.strip().strip("- ").strip("'\"") for line in block.splitlines() if line.lstrip().startswith("- ")]
                self.assertTrue(
                    path in entries or (path.startswith("scripts/") and "scripts/*release*" in entries and
                        ("release" in Path(path).name)),
                    msg=f"Release integrity trigger is missing {path}"
                )

    def test_main_candidate_requires_immutable_successful_source_gates(self):
        source = WORKFLOW.read_text(encoding="utf-8")
        self.assertIn("github.ref == 'refs/heads/main'", source)
        self.assertIn("Require successful core and system tests on this exact main commit", source)
        self.assertIn("if required <= by_name.keys()", source)
        self.assertIn("gh attestation verify SHA256SUMS", source)
        self.assertIn("--draft --prerelease", source)


if __name__ == "__main__":
    unittest.main()
