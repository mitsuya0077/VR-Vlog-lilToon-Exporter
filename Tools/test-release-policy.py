"""Exercise release gates before any tag, asset or stable pointer is changed."""
import unittest
from release_policy import validate


class ReleasePolicyTests(unittest.TestCase):
    def request(self, version="0.11.11-beta.1", **overrides):
        args = dict(version=version, univrm_version="0.131.0", channel="beta",
                    ref="refs/heads/main", sha="a" * 40, expected_sha="a" * 40)
        args.update(overrides)
        return validate({"version": version}, {"uniVrm": {"releaseVersion": "0.131.0"}}, **args)

    def test_beta_cannot_move_stable_latest(self):
        self.assertEqual(["--prerelease", "--latest=false"], self.request())

    def test_explicit_stable_release(self):
        self.assertEqual(["--latest"], self.request("0.11.11", channel="stable"))

    def test_channel_version_mismatch_or_noncanonical_version(self):
        for version, channel in (("0.11.11", "beta"), ("0.11.11-beta.1", "stable"),
                                 ("0.11.11-dev.3", "beta"), ("0.11.11-beta.01", "beta"),
                                 ("v0.11.11-beta.1", "beta"), ("0.11.11-beta.1", "auto"),
                                 ("0.11.11-beta.1\n", "beta"), ("01.11.11", "stable"),
                                 ("0.11.11+metadata", "stable")):
            with self.subTest(version=version, channel=channel), self.assertRaises(ValueError):
                self.request(version, channel=channel)

    def test_reviewed_main_commit_required(self):
        for override in ({"ref": "refs/heads/feature"}, {"ref": "refs/tags/v0.11.11-beta.1"},
                         {"sha": "b" * 40}, {"expected_sha": "a" * 7}, {"expected_sha": ""}):
            with self.subTest(override=override), self.assertRaises(ValueError):
                self.request(**override)

    def test_package_version_must_match_requested_version(self):
        with self.assertRaisesRegex(ValueError, "package.json"):
            validate({"version": "0.11.11-beta.2"}, {"uniVrm": {"releaseVersion": "0.131.0"}},
                     version="0.11.11-beta.1", univrm_version="0.131.0", channel="beta",
                     ref="refs/heads/main", sha="a" * 40, expected_sha="a" * 40)

    def test_dependency_pin_cannot_be_overridden(self):
        with self.assertRaisesRegex(ValueError, "pinned"):
            self.request(univrm_version="0.131.1")


if __name__ == "__main__":
    unittest.main()
