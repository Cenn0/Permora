"""Regression tests for Permora's public documentation privacy guard."""
import contextlib
import io
from pathlib import Path
import tempfile
import unittest

import check_public_docs as policy


class PublicDocsPolicyTests(unittest.TestCase):
    def test_sensitive_markers_are_detected_without_storing_real_secrets(self):
        # Build inert test markers at runtime so they are not committed as tokens.
        markers = (
            "gh" + "p_" + "A" * 36,
            "github_" + "pat_" + "B" * 42,
            "AK" + "IA" + "C" * 16,
            "-----BEGIN " + "OPENSSH PRIVATE KEY-----",
        )
        for marker in markers:
            with self.subTest(marker_type=marker[:2]):
                self.assertTrue(policy.has_sensitive_marker(marker))
                self.assertFalse(policy.has_sensitive_marker("prefix" + marker[:8]))

    def test_normal_public_documents_are_not_rejected(self):
        for source in ("ghp_example", "github_pat_short", "-----BEGIN PUBLIC KEY-----",
                       "git clone https://example.invalid/project", "noreply contact details"):
            self.assertFalse(policy.has_sensitive_marker(source))

    def test_critical_public_guidance_is_in_the_scan_scope(self):
        for document in ("AGENTS.md", "CONTRIBUTING.md", "SECURITY.md"):
            self.assertIn(document, policy.FILES)

    def test_main_reports_category_not_matching_value(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for path in policy.FILES:
                file = root / path
                file.parent.mkdir(parents=True, exist_ok=True)
                file.write_text("safe\n", encoding="utf-8")
            marker = "gh" + "p_" + "Z" * 36
            (root / "AGENTS.md").write_text(marker + "\n", encoding="utf-8")
            (root / "SECURITY.md").write_text("[bad](synthetic-private-path)\n", encoding="utf-8")
            previous = policy.ROOT
            try:
                policy.ROOT = root
                output = io.StringIO()
                with contextlib.redirect_stdout(output):
                    status = policy.main()
            finally:
                policy.ROOT = previous
            self.assertEqual(status, 1)
            self.assertIn("AGENTS.md: possible credential", output.getvalue())
            self.assertNotIn(marker, output.getvalue())
            self.assertNotIn(marker[:20], output.getvalue())
            self.assertNotIn("synthetic-private-path", output.getvalue())


if __name__ == "__main__":
    unittest.main()
