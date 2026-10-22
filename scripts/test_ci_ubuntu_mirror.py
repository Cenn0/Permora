import os
import unittest
from unittest.mock import patch

from ci_ubuntu_mirror import main, primary_archive


class UbuntuMirrorTests(unittest.TestCase):
    def test_github_priority_mirror_list_keeps_priorities_and_security_fallback(self):
        source = ("http://azure.archive.ubuntu.com/ubuntu/\tpriority:1\n"
                  "https://archive.ubuntu.com/ubuntu/\tpriority:2\n"
                  "https://security.ubuntu.com/ubuntu/\tpriority:3\n")
        self.assertEqual(source.replace("http://azure.archive.ubuntu.com/ubuntu/",
                                        "https://archive.ubuntu.com/ubuntu/"), primary_archive(source))

    def test_plain_mirror_list_keeps_other_mirrors_and_comments(self):
        source = "# Ubuntu mirror list\nhttp://azure.archive.ubuntu.com/ubuntu/\nhttps://archive.ubuntu.com/ubuntu/\n"
        self.assertEqual("# Ubuntu mirror list\nhttps://archive.ubuntu.com/ubuntu/\nhttps://archive.ubuntu.com/ubuntu/\n",
                         primary_archive(source))

    def test_deb822_retains_signature_keys_suites_components_and_security_archive(self):
        source = ("Types: deb\nURIs: http://azure.archive.ubuntu.com/ubuntu/\n"
                  "Suites: noble noble-updates noble-backports\nComponents: main universe\n"
                  "Signed-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg\n\n"
                  "URIs: http://security.ubuntu.com/ubuntu/\nSuites: noble-security\n"
                  "Signed-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg\n")
        result = primary_archive(source)
        self.assertEqual(source.replace("http://azure.archive.ubuntu.com/ubuntu/",
                                        "https://archive.ubuntu.com/ubuntu/"), result)
        self.assertEqual(result, primary_archive(result))

    def test_legacy_list_preserves_signed_by_and_does_not_rewrite_lookalike_or_third_party_urls(self):
        source = ("deb [signed-by=/usr/share/keyrings/ubuntu-archive-keyring.gpg] "
                  "https://azure.archive.ubuntu.com/ubuntu noble main\n"
                  "deb https://azure.archive.ubuntu.com/ubuntu-lookalike noble main\n"
                  "deb https://azure.archive.ubuntu.com.evil.test/ubuntu noble main\n"
                  "deb https://packages.example.test/ubuntu noble main\n")
        result = primary_archive(source)
        self.assertEqual(source.replace("https://azure.archive.ubuntu.com/ubuntu noble",
                                        "https://archive.ubuntu.com/ubuntu noble"), result)

    def test_non_github_execution_fails_before_reading_or_writing_system_sources(self):
        with patch.dict(os.environ, {"GITHUB_ACTIONS": "false"}), patch("ci_ubuntu_mirror.Path.read_text") as read:
            with self.assertRaises(SystemExit):
                main()
            read.assert_not_called()


if __name__ == "__main__":
    unittest.main()
