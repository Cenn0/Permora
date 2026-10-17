import base64
import json
import tempfile
import unittest
from pathlib import Path
from generate_sbom import generate, hex_digest


class LockfileSbomTests(unittest.TestCase):
    def test_digests_are_normalized_to_cyclonedx_hex(self):
        digest = "sha512-" + base64.b64encode(bytes(range(64))).decode()
        self.assertEqual(hex_digest(digest), {"alg": "SHA-512", "content": bytes(range(64)).hex()})
        with self.assertRaises(ValueError):
            hex_digest("sha512-" + base64.b64encode(b"short").decode())

    def test_repository_inventory_is_deterministic_and_contains_scoped_npm_and_nuget(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "src/Test").mkdir(parents=True)
            (root / "web").mkdir()
            (root / "src/Test/packages.lock.json").write_text(json.dumps({
                "version": 2, "dependencies": {"net10.0": {
                    "NuGet.Example": {"type": "Direct", "resolved": "1.2.3",
                                      "contentHash": base64.b64encode(bytes(64)).decode()}
                }}
            }))
            (root / "web/package-lock.json").write_text(json.dumps({
                "lockfileVersion": 3, "packages": {
                    "": {"name": "test"},
                    "node_modules/@permora/library": {
                        "version": "2.3.4", "dev": False, "license": "MIT"
                    }
                }
            }))
            a = generate(root)
            b = generate(root)
            self.assertEqual(json.dumps(a, sort_keys=True), json.dumps(b, sort_keys=True))
            self.assertEqual(a["bomFormat"], "CycloneDX")
            refs = [c["bom-ref"] for c in a["components"]]
            self.assertIn("pkg:nuget/NuGet.Example@1.2.3", refs)
            self.assertIn("pkg:npm/%40permora/library@2.3.4", refs)

    def test_missing_lockfiles_fail_closed(self):
        with tempfile.TemporaryDirectory() as tmp:
            with self.assertRaises(ValueError):
                generate(Path(tmp))


if __name__ == "__main__":
    unittest.main()
