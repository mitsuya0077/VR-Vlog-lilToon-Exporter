"""Exercise package isolation and diagnostic redaction with synthetic inputs."""
import importlib.util
import ast
import subprocess
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest import mock


def load(name):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(name + ".py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


package = load("build-package")
public = load("check-public-content")
unity_meta = load("unity_meta")


class UnityMetaTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.meta = self.root / "Example.cs.meta"

    def test_valid_guid_and_bom_are_accepted(self):
        self.meta.write_text("fileFormatVersion: 2\nguid: 0123456789ABCDEF0123456789abcdef\n", encoding="utf-8-sig")
        unity_meta.validate_meta_guids(self.root)

    def test_malformed_guid_reports_ignored_asset(self):
        # The first fixture reproduced Unity omitting NeutralShapeSampler.cs.
        for value in ("720af334c68d4a34b80e68321e35ff148", "a" * 31, "g" * 32, "", "a" * 32 + "\nguid: " + "b" * 32):
            with self.subTest(value=value):
                self.meta.write_text("fileFormatVersion: 2\nguid: " + value + "\n", encoding="utf-8")
                with self.assertRaisesRegex(ValueError, "Invalid Unity GUID in Example.cs.meta"):
                    unity_meta.validate_meta_guids(self.root)
        self.meta.write_text("fileFormatVersion: 2\n", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "Invalid Unity GUID in Example.cs.meta"):
            unity_meta.validate_meta_guids(self.root)

    def test_duplicate_guid_reports_both_assets(self):
        self.meta.write_text("guid: " + "a" * 32 + "\n", encoding="utf-8")
        (self.root / "Other.cs.meta").write_text("guid: " + "A" * 32 + "\n", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "Duplicate Unity GUID: Example.cs.meta and Other.cs.meta"):
            unity_meta.validate_meta_guids(self.root)


class PackageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.git("init", "--quiet")
        for name in package.ROOT_FILES | package.LOCALE_FILES | {"Editor/Example.cs", "Editor/Example.cs.meta", "Editor/Test.asmdef", "Editor/Example.shader", "Runtime.meta", "Runtime/Tracking.cs", "Runtime/Tracking.cs.meta", "Runtime/Tracking.asmdef", "ThirdPartyNotices/Example.md"}:
            self.write(name, name)
        repository = Path(__file__).resolve().parents[1]
        for name in package.DEPENDENCY_PATCH_FILES:
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes((repository / name).read_bytes())
        self.git("add", ".")

    def git(self, *args):
        subprocess.run(["git", "-C", str(self.root), *args], check=True, capture_output=True)

    def write(self, name, text):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")

    def test_excludes_tracked_development_and_untracked_inputs(self):
        excluded = ["work/report.md", ".env", ".github/workflows/example.yml", "Tools/debug.py", "Tools/test-aao-vertex-buffer-patch.py", "Tools/test-aao-vertex-buffer-patch.py.meta", "Documentation~/DependencyPatches/debug.patch", "Tests/Editor/Test.cs", "Docs/ReleaseVerification.md", "Editor/private.vrm", "Editor/error.log", "Editor/private.cs.disabled", "Editor/Locales/private.json"]
        for name in excluded:
            self.write(name, "private example")
        self.git("add", ".")
        self.write("Editor/Untracked.cs", "not reviewed")
        archive = self.root / "package.zip"
        names = package.build(self.root, archive)
        self.assertTrue(package.ROOT_FILES <= set(names))
        self.assertTrue(package.LOCALE_FILES <= set(names))
        self.assertTrue(package.DEPENDENCY_PATCH_FILES <= set(names))
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

    def test_archive_bytes_are_independent_of_host_os_and_zlib(self):
        # Include a UTF-8 filename and exact mixed-newline/binary payloads.
        # No checkout conversion or compressor may rewrite these bytes.
        inputs = {
            "Editor/Example.cs": b"first\r\nsecond\nthird\r\n",
            "Editor/Unicode_\u00e9.cs": "exact UTF-8 payload: \u8868\u60c5\n".encode("utf-8"),
            "Editor/Binary.shader": bytes(range(256)) * 32,
        }
        for name, content in inputs.items():
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(content)
        self.git("add", ".")
        native_zip_info = zipfile.ZipInfo
        native_zlib = zipfile.zlib
        reference = None
        for creator_system in (0, 3):
            for has_zlib in (True, False):
                with self.subTest(creator_system=creator_system, has_zlib=has_zlib):
                    class HostZipInfo(native_zip_info):
                        def __init__(self, *args, **kwargs):
                            super().__init__(*args, **kwargs)
                            self.create_system = creator_system

                    output = self.root / f"portable-{creator_system}-{has_zlib}.zip"
                    with mock.patch.object(zipfile, "ZipInfo", HostZipInfo), \
                            mock.patch.object(zipfile, "zlib", native_zlib if has_zlib else None):
                        names = package.build(self.root, output)
                    data = output.read_bytes()
                    if reference is None:
                        reference = data
                    self.assertEqual(data, reference)
                    with zipfile.ZipFile(output) as archive:
                        self.assertEqual(archive.namelist(), sorted(names))
                        self.assertEqual(archive.comment, b"")
                        for entry in archive.infolist():
                            self.assertEqual(entry.create_system, 3)
                            self.assertEqual(entry.compress_type, zipfile.ZIP_STORED)
                            self.assertEqual(entry.compress_size, entry.file_size)
                            self.assertEqual(entry.date_time, (2020, 1, 1, 0, 0, 0))
                            self.assertEqual(entry.external_attr, 0o100644 << 16)
                            self.assertEqual(entry.extra, b"")
                            self.assertEqual(entry.comment, b"")
                            self.assertEqual(archive.read(entry), (self.root / entry.filename).read_bytes())
                        for name, expected in inputs.items():
                            self.assertEqual(archive.read(name), expected)

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

    def test_shipped_patch_tool_provenance_and_exact_allowlist(self):
        archive = self.root / "package.zip"
        names = package.build(self.root, archive)
        self.assertEqual({name for name in names if name.startswith("Tools/")},
                         {"Tools/patch-aao-vertex-buffer.py", "Tools/patch-aao-vertex-buffer.py.meta"})
        with zipfile.ZipFile(archive) as built:
            for name in package.DEPENDENCY_PATCH_FILES:
                self.assertEqual(built.read(name), (self.root / name).read_bytes())
            tree = ast.parse(built.read("Tools/patch-aao-vertex-buffer.py").decode("utf-8"))
            expected = {
                "PACKAGE_NAME": "com.anatawa12.avatar-optimizer", "PACKAGE_VERSION": "1.9.20",
                "PATCH_ID": "vrvlog.aao-1.9.20.vertex-buffer-dispose.1",
                "UPSTREAM_COMMIT": "439a56aae2cc744c2a1590e79067353879496b8e",
                "ORIGINAL_SHA256": "fc6d349153753d83b9e588727ce3328ef3db6c4f185668d8a6a73218a4a3a4af",
                "PATCHED_SHA256": "7c002a439e4a19f83f47f8db1f43db16ec1016cbad933f691004f114671516d2",
            }
            actual = {node.targets[0].id: ast.literal_eval(node.value) for node in tree.body
                      if isinstance(node, ast.Assign) and isinstance(node.targets[0], ast.Name)
                      and node.targets[0].id in expected}
            self.assertEqual(actual, expected)
            readme = built.read("Documentation~/DependencyPatches/README.md").decode("utf-8")
            for key in ["PACKAGE_VERSION", "PATCH_ID", "UPSTREAM_COMMIT", "ORIGINAL_SHA256", "PATCHED_SHA256"]:
                self.assertIn(expected[key], readme)
            diff = built.read("Documentation~/DependencyPatches/aao-1.9.20-dispose-vertex-buffer.patch").decode("utf-8")
            self.assertIn("-                var vertexBuffer = mesh.GetVertexBuffer(i);", diff)
            self.assertIn("+                using var vertexBuffer = mesh.GetVertexBuffer(i);", diff)
            self.assertIn("Copyright (c) 2022 anatawa12", built.read("Documentation~/DependencyPatches/AAO-LICENSE.txt").decode("utf-8"))

    def test_missing_required_dependency_patch_file_fails(self):
        for name in ["Tools/patch-aao-vertex-buffer.py", "Documentation~/DependencyPatches/AAO-LICENSE.txt"]:
            with self.subTest(name=name):
                self.git("rm", "--cached", name)
                with self.assertRaisesRegex(ValueError, "Required package files"):
                    package.build(self.root, self.root / "invalid.zip")
                self.git("add", name)

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
