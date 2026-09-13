import importlib.util
import json
from pathlib import Path
import unittest
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('upstream', Path(__file__).with_name('check-upstream-dependencies.py'))
upstream = importlib.util.module_from_spec(spec)
spec.loader.exec_module(upstream)
spec = importlib.util.spec_from_file_location('unity_runner', Path(__file__).with_name('run-unity-compatibility.py'))
unity_runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(unity_runner)


class UnityResultTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.xml = Path(self.temp.name) / 'result.xml'
        self.root = ET.Element('test-run', result='Passed')
        methods = {
            'DependencyEnvironmentTests': ('ActualInstalledPackagesMatchRequestedTestEnvironment', 1),
            'DependencyStartupTests': ('MenuResolvesBackendWithoutInitializationRegistration', 1),
            'DependencyRoundTripTests': ('SupportedBackendPreservesMeshesMorphsAndMaterialBindingsOnReimport', 2),
            'RendererSelectionTests': ('RendererCase', 4),
            'SkinnedMeshFallbackWeightTests': ('SkinCase', 12),
        }
        for suite, (method, count) in methods.items():
            name = 'VRVlog.LilToonExporter.Tests.' + suite
            for index in range(count):
                ET.SubElement(self.root, 'test-case', classname=name, fullname=name + '.' + method + '(' + str(index) + ')', result='Passed')

    def check(self):
        ET.ElementTree(self.root).write(self.xml)
        return unity_runner.validate_result(self.xml, True)

    def test_all_required_suites_pass(self):
        self.assertEqual(len(self.check()), 20)

    def test_no_suite_can_disappear_from_a_passing_result(self):
        for name in ['DependencyEnvironmentTests', 'DependencyStartupTests', 'DependencyRoundTripTests', 'RendererSelectionTests', 'SkinnedMeshFallbackWeightTests']:
            with self.subTest(suite=name):
                removed = [c for c in self.root if c.get('classname').endswith('.' + name)]
                for case in removed: self.root.remove(case)
                with self.assertRaises(SystemExit): self.check()
                for case in removed: self.root.append(case)

    def test_one_missing_skin_case_is_not_success(self):
        self.root.remove(self.root[-1])
        with self.assertRaises(SystemExit): self.check()

    def test_skipped_or_failed_case_is_not_success(self):
        for result in ['Skipped', 'Failed']:
            self.root[-1].set('result', result)
            with self.assertRaises(SystemExit): self.check()

    def test_missing_result_is_not_success(self):
        with self.assertRaises(FileNotFoundError): unity_runner.validate_result(self.xml, True)


class UpstreamTests(unittest.TestCase):
    def setUp(self):
        self.config = json.loads((ROOT / 'Compatibility/dependencies.json').read_text())
        self.releases = {name: {'tag_name': 'v0.0.0', 'draft': False, 'prerelease': False} for name in upstream.REPOSITORIES}

    def test_new_release_requests_review_without_modifying_policy(self):
        before = json.dumps(self.config)
        self.releases['lilToon']['tag_name'] = '2.3.5'
        result = upstream.compare(self.config, self.releases)
        self.assertTrue(result[0]['needsCompatibilityReview'])
        self.assertEqual(before, json.dumps(self.config))

    def test_numeric_version_order_and_existing_release(self):
        self.assertGreater(upstream.release_version('v0.131.10'), upstream.release_version('0.131.2'))
        self.releases['UniVRM']['tag_name'] = max(self.config['uniVrm']['versions'], key=upstream.release_version)
        self.assertFalse(upstream.compare(self.config, self.releases)[1]['needsCompatibilityReview'])

    def test_unrecognized_and_preview_release_never_reports_safe(self):
        for tag in ['0.131.2-preview.1', '0.131', 'latest', ' 0.131.2']:
            with self.assertRaises(ValueError):
                upstream.release_version(tag)
        self.releases['lilToon']['draft'] = True
        with self.assertRaises(ValueError):
            upstream.compare(self.config, self.releases)


if __name__ == '__main__':
    unittest.main()
