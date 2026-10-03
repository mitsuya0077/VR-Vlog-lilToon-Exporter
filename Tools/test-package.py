"""Exercise package isolation and diagnostic redaction with synthetic inputs."""
import importlib.util
import subprocess
import tempfile
import unittest
import zipfile
from pathlib import Path


def load(name):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(name + ".py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


package = load("build-package")
public = load("check-public-content")


class PackageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.git("init", "--quiet")
        for name in package.ROOT_FILES | package.LOCALE_FILES | {"Editor/Example.cs", "Editor/Example.cs.meta", "Editor/Test.asmdef", "Editor/Example.shader", "Runtime.meta", "Runtime/Tracking.cs", "Runtime/Tracking.cs.meta", "Runtime/Tracking.asmdef", "ThirdPartyNotices/Example.md"}:
            self.write(name, name)
        self.git("add", ".")

    def git(self, *args):
        subprocess.run(["git", "-C", str(self.root), *args], check=True, capture_output=True)

    def write(self, name, text):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")

    def test_excludes_tracked_development_and_untracked_inputs(self):
        excluded = ["work/report.md", ".env", ".github/workflows/example.yml", "Tools/debug.py", "Tests/Editor/Test.cs", "Docs/ReleaseVerification.md", "Editor/private.vrm", "Editor/error.log", "Editor/private.cs.disabled", "Editor/Locales/private.json"]
        for name in excluded:
            self.write(name, "private example")
        self.git("add", ".")
        self.write("Editor/Untracked.cs", "not reviewed")
        archive = self.root / "package.zip"
        names = package.build(self.root, archive)
        self.assertTrue(package.ROOT_FILES <= set(names))
        self.assertTrue(package.LOCALE_FILES <= set(names))
        self.assertIn("Editor/Example.cs.meta", names)
        self.assertIn("Editor/Example.shader", names)
        self.assertIn("Runtime.meta", names)
        self.assertIn("Runtime/Tracking.cs", names)
        self.assertIn("Runtime/Tracking.cs.meta", names)
        self.assertIn("Runtime/Tracking.asmdef", names)
        self.assertIn("ThirdPartyNotices/Example.md", names)
        self.assertFalse(set(excluded) & set(names))
        self.assertNotIn("Editor/Untracked.cs", names)
        with zipfile.ZipFile(archive) as built:
            self.assertEqual(names, built.namelist())
            self.assertEqual(built.read("Editor/Example.cs"), b"Editor/Example.cs")
            for locale in package.LOCALE_ASSETS:
                self.assertEqual(built.read(locale), locale.encode("utf-8"))
        second = self.root / "second.zip"
        package.build(self.root, second)
        self.assertEqual(archive.read_bytes(), second.read_bytes())

    def test_missing_required_file_fails(self):
        self.git("rm", "--cached", "LICENSE")
        with self.assertRaisesRegex(ValueError, "Required package files"):
            package.build(self.root, self.root / "invalid.zip")

    def test_lan_transfer_guide_is_packaged_and_required(self):
        guide = "Documentation~/LanTransfer.md"
        content = "Public LAN installation, permissions and acceptance instructions\n"
        self.write(guide, content)
        expected = (self.root / guide).read_bytes()
        self.git("add", guide)
        archive = self.root / "with-lan-guide.zip"
        names = package.build(self.root, archive)
        self.assertIn(guide, names)
        with zipfile.ZipFile(archive) as built:
            self.assertEqual(built.read(guide), expected)
        self.assertFalse(package.included("Documentation~/private-notes.md"))
        self.git("rm", "--cached", guide)
        with self.assertRaisesRegex(ValueError, "Required package files.*LanTransfer"):
            package.build(self.root, self.root / "missing-lan-guide.zip")

    def test_only_pinned_dlls_are_allowed(self):
        for name in package.PINNED_DLLS:
            self.assertTrue(package.included(name))
        self.assertFalse(package.included("Editor/arbitrary.dll"))
        self.assertFalse(package.included("Runtime/BouncyCastle.Cryptography.dll"))

    def test_missing_or_modified_transfer_dependency_fails(self):
        contents = {"Editor/LanTransfer/VRVlog.LanTransfer.Editor.asmdef": b"{}"}
        with self.assertRaisesRegex(ValueError, "Pinned LAN transfer dependency"):
            package.verify_lan_dependencies(contents)
        for name in package.PINNED_DLLS:
            contents[name] = b"modified DLL"
            contents[name + ".meta"] = b"importer"
        with self.assertRaisesRegex(ValueError, "different SHA-256"):
            package.verify_lan_dependencies(contents)

    def test_referenced_untracked_transfer_assembly_fails(self):
        contents = {"Editor/VRVlog.LilToonExporter.Editor.asmdef": b'{"references":["VRVlog.LanTransfer.Editor"]}'}
        with self.assertRaisesRegex(ValueError, "Required LAN transfer assembly"):
            package.verify_lan_dependencies(contents)

    def test_missing_locale_asset_fails(self):
        self.git("rm", "--cached", "Editor/Locales/ExporterLocale_ko.json")
        with self.assertRaisesRegex(ValueError, "Required package files"):
            package.build(self.root, self.root / "invalid.zip")

    def test_content_check_labels_without_values(self):
        token = "ghp_" + "a" * 36
        findings = public.inspect("Editor/Example.cs", ("credential=" + token).encode())
        self.assertEqual(findings, [(1, "access token")])
        self.assertNotIn(token, repr(findings))
        self.assertEqual(public.inspect("work/sample.vrm", b"\0data"), [(0, "private/local input type")])
        self.assertEqual(public.inspect("README.md", b"https://example.com/guide"), [])

    def test_symlink_is_not_packaged(self):
        self.write("private-input.txt", "private fixture")
        link = self.root / "Editor/Linked.cs"
        try:
            link.symlink_to(self.root / "private-input.txt")
        except OSError:
            self.skipTest("Host does not permit creating symlinks")
        self.git("add", "Editor/Linked.cs")
        with self.assertRaisesRegex(ValueError, "Symlinks"):
            package.build(self.root, self.root / "invalid.zip")

    def test_nul_and_unicode_text_cannot_bypass_content_checks(self):
        token = "ghp_" + "a" * 36
        content = "example\ncredential=" + token
        for encoding in ["utf-8-sig", "utf-16", "utf-16-le", "utf-16-be", "utf-32", "utf-32-le", "utf-32-be"]:
            with self.subTest(encoding=encoding):
                findings = public.inspect("example.txt", content.encode(encoding))
                self.assertIn((2, "access token"), findings)
                self.assertNotIn(token, repr(findings))
        self.assertIn((1, "access token"), public.inspect("example.bin", b"\0" + token.encode()))
        for encoding in ["utf-16", "utf-32"]:
            with self.subTest(nul_prefix=encoding):
                self.assertIn((1, "access token"), public.inspect("example.txt", ("\0credential=" + token).encode(encoding)))


if __name__ == "__main__":
    unittest.main()
