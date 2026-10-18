import copy
from pathlib import Path
import tarfile
import tempfile
import unittest
from importlib.util import module_from_spec, spec_from_file_location
from release_sbom import merge, validate_graph

# The release builder is a CLI with a hyphenated filename, not a Python module.
# Load that exact checked-in file to test its production code without renaming it.
_builder_spec = spec_from_file_location(
    "build_release_evidence", Path(__file__).with_name("build-release-evidence.py")
)
if _builder_spec is None or _builder_spec.loader is None:
    raise RuntimeError("Cannot locate the release evidence builder")
_builder = module_from_spec(_builder_spec)
_builder_spec.loader.exec_module(_builder)
safe_archive = _builder.safe_archive
safe_name = _builder.safe_name


def bom():
    return {'bomFormat': 'CycloneDX', 'specVersion': '1.6', 'version': 1,
        'metadata': {'component': {'type': 'container', 'name': 'image', 'bom-ref': 'image-root'}},
        'components': [{'type': 'library', 'name': 'package', 'bom-ref': 'package'}],
        'dependencies': [{'ref': 'image-root', 'dependsOn': ['package']}, {'ref': 'package', 'dependsOn': []}]}


class ReleaseEvidenceTests(unittest.TestCase):
    def test_scanned_root_and_all_dependency_edges_are_retained(self):
        result = merge('a' * 40, {'api.cdx.json': bom()})
        self.assertEqual(3, validate_graph(result))
        refs = {c['bom-ref'] for c in result['components']}
        self.assertIn('api.cdx.json:image-root', refs)
        self.assertIn('api.cdx.json:package', refs)
        self.assertEqual(['api.cdx.json:image-root'], result['dependencies'][0]['dependsOn'])

    def test_separate_images_do_not_overwrite_identical_package_refs(self):
        result = merge('b' * 40, {'api': bom(), 'worker': bom()})
        self.assertEqual(5, validate_graph(result))
        self.assertEqual(4, len(result['components']))

    def test_missing_root_and_dangling_dependency_fail_closed(self):
        value = bom()
        value['dependencies'][0]['dependsOn'] = ['missing']
        with self.assertRaises(ValueError): merge('a' * 40, {'broken': value})
        del value['metadata']['component']
        with self.assertRaises(ValueError): merge('a' * 40, {'broken': value})

    def test_duplicate_reference_or_unknown_schema_cannot_be_signed_as_valid(self):
        value = bom()
        value['components'].append(copy.deepcopy(value['components'][0]))
        with self.assertRaises(ValueError): merge('a' * 40, {'duplicate': value})
        value = bom()
        value['specVersion'] = 'unknown'
        with self.assertRaises(ValueError): merge('a' * 40, {'unknown': value})

    def test_nested_components_have_consistent_references(self):
        value = bom()
        value['components'][0]['components'] = [{'type': 'library', 'name': 'child', 'bom-ref': 'child'}]
        value['dependencies'][1]['dependsOn'] = ['child']
        result = merge('c' * 40, {'source': value})
        self.assertEqual(4, validate_graph(result))
        self.assertEqual('source:child', result['components'][1]['components'][0]['bom-ref'])

    def test_package_metadata_cannot_escape_notice_directory(self):
        self.assertEqual('@scope_package', safe_name('@scope/package'))
        for value in ('../private', '/etc/passwd', 'a/../../secret', 'bad:name'):
            with self.assertRaises(ValueError): safe_name(value)

    def test_archive_is_deterministic_and_never_follows_symlinks(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            app = root / 'app'
            app.mkdir()
            (app / 'binary.dll').write_bytes(b'actual-shipped-bytes')
            (root / 'secret').write_text('must-never-ship')
            (app / 'link').symlink_to(root / 'secret')
            safe_archive(app, root / 'a.tar.gz')
            safe_archive(app, root / 'b.tar.gz')
            # gzip stores an output filename by default; compare the normalized
            # inner tar payload, whose metadata is deterministic.
            import gzip
            self.assertEqual(gzip.decompress((root / 'a.tar.gz').read_bytes()), gzip.decompress((root / 'b.tar.gz').read_bytes()))
            with tarfile.open(root / 'a.tar.gz') as archive:
                self.assertEqual(['binary.dll'], archive.getnames())
                self.assertEqual(b'actual-shipped-bytes', archive.extractfile('binary.dll').read())



class ReleaseWorkflowGatesTests(unittest.TestCase):
    def test_each_source_change_on_main_triggers_a_new_signed_draft(self):
        workflow = (Path(__file__).resolve().parents[1] /
                    '.github/workflows/release-integrity.yml').read_text()
        self.assertIn("on:", workflow)
        self.assertIn("  push:", workflow)
        self.assertIn("  pull_request:", workflow)
        push = workflow.split('  push:', 1)[1].split('  workflow_dispatch:', 1)[0]
        for path in (
            "'src/**'", "'web/**'", "'deploy/Dockerfile'",
            "'Directory.*.props'", "'global.json'",
            "'.github/workflows/release-integrity.yml'",
        ):
            self.assertIn(path, push, 'Missing main-branch candidate trigger: ' + path)
        self.assertIn("github.ref == 'refs/heads/main'", workflow)
        self.assertIn("github.event_name != 'pull_request'", workflow)
        self.assertIn("'Permora accessibility acceptance'", workflow)
        self.assertIn("'Permora system acceptance'", workflow)
        self.assertIn("'Permora worker and recovery acceptance'", workflow)
        self.assertIn("'Permora CI'", workflow)
        self.assertIn('--draft --prerelease', workflow)
        self.assertIn('--source-digest "$GITHUB_SHA"', workflow)



if __name__ == '__main__': unittest.main()
