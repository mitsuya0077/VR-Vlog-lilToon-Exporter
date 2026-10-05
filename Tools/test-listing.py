"""Exercise stable/beta listing identity, package hashes and publication guards."""
import copy
import hashlib
import importlib.util
import io
import json
import unittest
import zipfile
from pathlib import Path

spec = importlib.util.spec_from_file_location("listing_check", Path(__file__).with_name("check-listing.py"))
check = importlib.util.module_from_spec(spec)
spec.loader.exec_module(check)


class ListingTests(unittest.TestCase):
    def setUp(self):
        self.package = {"name": check.EXPORTER, "version": "0.11.11-beta.1",
                        "vpmDependencies": {"com.vrmc.gltf": "0.131.x", "com.vrmc.vrm": "0.131.x"}}
        self.source = {"id": "com.vrvlog.packages", "url": "https://example.invalid/index.json"}
        self.compatibility = {"uniVrm": {"releaseVersion": "0.131.0"}}
        self.listing = dict(self.source, packages={})
        self.zips = {}
        for name, version in [(check.EXPORTER, "0.11.10"), (check.EXPORTER, "0.11.11-beta.1"),
                              ("com.vrmc.gltf", "0.131.0"), ("com.vrmc.vrm", "0.131.0")]:
            manifest = dict(self.package, version=version) if name == check.EXPORTER else {"name": name, "version": version}
            url = f"https://github.com/{check.REPOSITORY}/releases/download/v0.11.11-beta.1/{name}-{version}.zip"
            if name == check.EXPORTER:
                url = f"https://github.com/{check.REPOSITORY}/releases/download/v{version}/{name}-{version}.zip"
            data = self.archive(manifest)
            self.zips[url] = data
            entry = dict(manifest, url=url, zipSHA256=hashlib.sha256(data).hexdigest())
            self.listing["packages"].setdefault(name, {"versions": {}})["versions"][version] = entry

    @staticmethod
    def archive(manifest, root="package.json"):
        stream = io.BytesIO()
        with zipfile.ZipFile(stream, "w") as archive:
            archive.writestr(root, json.dumps(manifest))
        return stream.getvalue()

    def run_check(self, **kwargs):
        return check.validate(self.listing, self.package, self.source, self.compatibility,
                              read_zip=self.zips.__getitem__, **kwargs)

    def current(self):
        return self.listing["packages"][check.EXPORTER]["versions"][self.package["version"]]

    def test_stable_and_beta_and_pinned_dependencies_are_verified(self):
        result = self.run_check()
        self.assertEqual("passed", result["status"])
        self.assertEqual(4, len(result["verifiedPackages"]))
        self.assertEqual("0.11.10", result["preservedStableVersion"])

    def test_loopback_fixture_uses_the_same_hash_and_manifest_checks(self):
        for group in self.listing["packages"].values():
            for entry in group["versions"].values():
                old = entry["url"]
                entry["url"] = f"{check.FIXTURE_BASE}/{entry['name']}-{entry['version']}.zip"
                self.zips[entry["url"]] = self.zips[old]
        self.assertEqual(4, len(self.run_check(fixture_base=check.FIXTURE_BASE)["verifiedPackages"]))

    def test_missing_current_or_stable_is_rejected(self):
        for version in ("0.11.10", self.package["version"]):
            with self.subTest(version=version):
                saved = self.listing["packages"][check.EXPORTER]["versions"].pop(version)
                with self.assertRaisesRegex(ValueError, "Missing listed package"):
                    self.run_check()
                self.listing["packages"][check.EXPORTER]["versions"][version] = saved

    def test_missing_pinned_dependency_is_rejected(self):
        del self.listing["packages"]["com.vrmc.gltf"]
        with self.assertRaisesRegex(ValueError, "Missing listed package"):
            self.run_check()

    def test_version_key_cannot_hide_another_manifest(self):
        self.current()["version"] = "0.11.10"
        with self.assertRaisesRegex(ValueError, "version key and manifest disagree"):
            self.run_check()

    def test_missing_or_changed_zip_hash_is_rejected(self):
        for digest in (None, "", "a" * 63, "A" * 64, "a" * 64):
            with self.subTest(digest=digest):
                self.current()["zipSHA256"] = digest
                with self.assertRaises(ValueError):
                    self.run_check()

    def test_valid_hash_cannot_hide_a_different_downloaded_version(self):
        entry = self.current()
        data = self.archive(dict(self.package, version="0.11.10"))
        self.zips[entry["url"]] = data
        entry["zipSHA256"] = hashlib.sha256(data).hexdigest()
        with self.assertRaisesRegex(ValueError, "Downloaded package identity differs"):
            self.run_check()

    def test_nested_manifest_is_not_an_installable_vpm_zip(self):
        entry = self.current()
        data = self.archive(self.package, root="package/package.json")
        self.zips[entry["url"]] = data
        entry["zipSHA256"] = hashlib.sha256(data).hexdigest()
        with self.assertRaisesRegex(ValueError, "one root package.json"):
            self.run_check()

    def test_current_dependency_policy_must_match_zip_and_listing(self):
        for change_zip in (False, True):
            with self.subTest(change_zip=change_zip):
                entry = self.current()
                saved = copy.deepcopy(entry)
                if change_zip:
                    data = self.archive(dict(self.package, vpmDependencies={}))
                    self.zips[entry["url"]] = data
                    entry["zipSHA256"] = hashlib.sha256(data).hexdigest()
                else:
                    entry["vpmDependencies"] = {}
                with self.assertRaisesRegex(ValueError, "dependency policy differs"):
                    self.run_check()
                entry.update(saved)

    def test_foreign_tag_asset_host_or_query_is_rejected_before_download(self):
        entry = self.current()
        original = entry["url"]
        for url in (original.replace("github.com", "example.invalid"), original.replace("https:", "http:"),
                    original.replace("/v0.11.11-beta.1/", "/v0.11.10/"), original + "?token=private",
                    original.replace(".zip", ".unitypackage")):
            with self.subTest(url=url):
                entry["url"] = url
                with self.assertRaises(ValueError):
                    self.run_check()
        entry["url"] = original

    def test_fixture_mode_cannot_enable_arbitrary_http_downloads(self):
        with self.assertRaisesRegex(ValueError, "explicit loopback fixture"):
            self.run_check(fixture_base="http://example.invalid/release")

    def test_generated_listing_identity_is_preserved(self):
        self.listing["id"] = "another.repository"
        with self.assertRaisesRegex(ValueError, "listing identity differs"):
            self.run_check()


if __name__ == "__main__":
    unittest.main()
