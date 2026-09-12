import importlib.util
import json
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('upstream', Path(__file__).with_name('check-upstream-dependencies.py'))
upstream = importlib.util.module_from_spec(spec)
spec.loader.exec_module(upstream)


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
