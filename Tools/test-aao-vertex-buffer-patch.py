"""Exercise the explicit patcher on isolated synthetic package copies only."""
import codecs
from contextlib import redirect_stderr, redirect_stdout
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import stat
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock

sys.dont_write_bytecode = True
TOOLS = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("aao_vertex_buffer_patch", TOOLS / "patch-aao-vertex-buffer.py")
patcher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(patcher)

# A small, public fixture exercises file handling without bundling AAO source,
# reading installed packages or fetching anything. Its two hashes replace the
# real hashes only within each test; production constants are checked below.
FIXTURE = (b"// Synthetic UTF-8 fixture: " + "髪".encode("utf-8") + b"\n"
           + patcher.METHOD + b"\n        {\n" + patcher.ORIGINAL_LINE
           + b"\n                vertexBuffer.GetData(data);\n        }\n"
           + b"        delegate T DataParser<T>(byte[] data, int offset);\n")
FIXTURE_PATCHED = FIXTURE.replace(patcher.ORIGINAL_LINE, patcher.PATCHED_LINE)


class PinnedProductionContractTests(unittest.TestCase):
    def test_known_official_and_patched_hashes_and_review_diff(self):
        self.assertEqual(patcher.ORIGINAL_SHA256, "fc6d349153753d83b9e588727ce3328ef3db6c4f185668d8a6a73218a4a3a4af")
        self.assertEqual(patcher.PATCHED_SHA256, "7c002a439e4a19f83f47f8db1f43db16ec1016cbad933f691004f114671516d2")
        self.assertEqual(patcher.UPSTREAM_COMMIT, "439a56aae2cc744c2a1590e79067353879496b8e")
        self.assertEqual(patcher.PACKAGE_NAME, "com.anatawa12.avatar-optimizer")
        self.assertEqual(patcher.PACKAGE_VERSION, "1.9.20")
        docs = TOOLS.parent / "Documentation~/DependencyPatches"
        diff = (docs / "aao-1.9.20-dispose-vertex-buffer.patch").read_text(encoding="utf-8")
        additions = [line[1:] for line in diff.splitlines() if line.startswith("+") and not line.startswith("+++")]
        removals = [line[1:] for line in diff.splitlines() if line.startswith("-") and not line.startswith("---")]
        self.assertEqual(additions, [patcher.PATCHED_LINE.decode()])
        self.assertEqual(removals, [patcher.ORIGINAL_LINE.decode()])
        self.assertIn("Copyright (c) 2022 anatawa12", (docs / "AAO-LICENSE.txt").read_text(encoding="utf-8"))

    def test_production_hash_guard_rejects_synthetic_source(self):
        with self.assertRaisesRegex(patcher.PatchError, "source hash"):
            patcher.prepare_patch(FIXTURE)


class IsolatedPackagePatchTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="vrvlog-aao-patch-test-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name).resolve()
        self.package = self.root / "owned-package"
        self.source = self.package / patcher.SOURCE_RELATIVE
        self.source.parent.mkdir(parents=True)
        self.manifest = self.package / "package.json"
        self.write_manifest()
        self.source.write_bytes(FIXTURE)
        hashes = mock.patch.multiple(patcher,
            ORIGINAL_SHA256=hashlib.sha256(FIXTURE).hexdigest(),
            PATCHED_SHA256=hashlib.sha256(FIXTURE_PATCHED).hexdigest())
        hashes.start()
        self.addCleanup(hashes.stop)

    def write_manifest(self, name=None, version=None):
        self.manifest.write_text(json.dumps({"name": name or patcher.PACKAGE_NAME,
                                             "version": version or patcher.PACKAGE_VERSION}), encoding="utf-8")

    def assert_no_staging_files(self):
        self.assertEqual(list(self.source.parent.glob(".vrvlog-aao-patch-*")), [])

    def test_bom_and_each_line_ending_are_preserved(self):
        for bom in [b"", codecs.BOM_UTF8]:
            for ending in ["LF", "CRLF", "mixed"]:
                with self.subTest(bom=bool(bom), ending=ending):
                    if ending == "mixed":
                        source = b"".join(line + (b"\r\n" if index % 2 else b"\n")
                                          for index, line in enumerate(FIXTURE.splitlines()))
                    else:
                        source = FIXTURE.replace(b"\n", b"\r\n") if ending == "CRLF" else FIXTURE
                    source = bom + source
                    self.source.write_bytes(source)
                    manifest = self.manifest.read_bytes()
                    mode = stat.S_IMODE(self.source.stat().st_mode)
                    result = patcher.patch_package(self.package)
                    self.assertEqual(result["status"], "applied")
                    self.assertEqual(self.source.read_bytes(), source.replace(patcher.ORIGINAL_LINE, patcher.PATCHED_LINE))
                    self.assertEqual(self.manifest.read_bytes(), manifest)
                    self.assertEqual(stat.S_IMODE(self.source.stat().st_mode), mode)
                    self.assertEqual(result["applied_patch"], patcher.PATCH_ID)
                    self.assert_no_staging_files()

    def test_check_only_writes_nothing_for_original_and_patched(self):
        for content, status in [(FIXTURE, "checked_original"), (FIXTURE_PATCHED, "already_patched")]:
            self.source.write_bytes(content)
            before = {path: (path.read_bytes(), path.stat().st_mtime_ns) for path in [self.manifest, self.source]}
            with mock.patch.object(patcher, "atomic_replace") as replacement:
                result = patcher.patch_package(self.package, check_only=True)
            replacement.assert_not_called()
            self.assertEqual(result["status"], status)
            self.assertEqual(before, {path: (path.read_bytes(), path.stat().st_mtime_ns) for path in before})
            self.assert_no_staging_files()

    def test_second_application_is_idempotent_without_replacement(self):
        patcher.patch_package(self.package)
        before = self.source.stat()
        with mock.patch.object(patcher.os, "replace") as replacement:
            result = patcher.patch_package(self.package)
        replacement.assert_not_called()
        self.assertEqual(result["status"], "already_patched")
        self.assertEqual(self.source.read_bytes(), FIXTURE_PATCHED)
        self.assertEqual(self.source.stat().st_mtime_ns, before.st_mtime_ns)

    def test_name_version_and_source_changes_fail_closed(self):
        for name, version in [("different-package", "1.9.20"), (patcher.PACKAGE_NAME, "1.9.21"),
                              (patcher.PACKAGE_NAME, "1.9.20+unreviewed")]:
            with self.subTest(name=name, version=version):
                self.write_manifest(name, version)
                with self.assertRaises(patcher.PatchError):
                    patcher.patch_package(self.package)
                self.assertEqual(self.source.read_bytes(), FIXTURE)
        self.write_manifest()
        changed = FIXTURE + b"// another change\n"
        self.source.write_bytes(changed)
        with self.assertRaisesRegex(patcher.PatchError, "source hash"):
            patcher.patch_package(self.package)
        self.assertEqual(self.source.read_bytes(), changed)
        self.assert_no_staging_files()

    def test_missing_and_non_regular_inputs_are_rejected_without_writes(self):
        with self.assertRaises(OSError):
            patcher.patch_package(self.root / "missing")
        original = self.source.read_bytes()
        self.manifest.unlink()
        self.manifest.mkdir()
        with self.assertRaisesRegex(patcher.PatchError, "regular files"):
            patcher.patch_package(self.package)
        self.assertEqual(self.source.read_bytes(), original)
        self.assert_no_staging_files()

    def test_invalid_encoding_and_bare_cr_are_rejected(self):
        for content in [FIXTURE + b"\xff", FIXTURE.replace(b"\n", b"\r")]:
            with self.subTest(content=content[-4:]):
                self.source.write_bytes(content)
                with self.assertRaises((UnicodeError, patcher.PatchError)):
                    patcher.patch_package(self.package)
                self.assertEqual(self.source.read_bytes(), content)
                self.assert_no_staging_files()

    def test_verified_hash_still_requires_exact_method_and_candidate(self):
        misplaced = FIXTURE.replace(patcher.METHOD, b"// outside expected method")
        self.source.write_bytes(misplaced)
        with mock.patch.object(patcher, "ORIGINAL_SHA256", hashlib.sha256(misplaced).hexdigest()):
            with self.assertRaisesRegex(patcher.PatchError, "GetVertexBuffers"):
                patcher.patch_package(self.package)
        self.assertEqual(self.source.read_bytes(), misplaced)
        self.source.write_bytes(FIXTURE)
        with mock.patch.object(patcher, "PATCHED_SHA256", "0" * 64):
            with self.assertRaisesRegex(patcher.PatchError, "candidate"):
                patcher.patch_package(self.package)
        self.assertEqual(self.source.read_bytes(), FIXTURE)

    def test_recognized_package_cache_path_is_rejected_even_for_check_only(self):
        cache = self.root / "Library/PackageCache" / patcher.PACKAGE_NAME
        cache.mkdir(parents=True)
        with self.assertRaisesRegex(patcher.PatchError, "cache"):
            patcher.patch_package(cache, check_only=True)
        self.assertEqual(list(cache.iterdir()), [])

    def test_package_source_and_manifest_symlinks_are_rejected(self):
        target = self.root / "external-source"
        target.write_bytes(FIXTURE)
        alias = self.root / "linked-package"
        try:
            alias.symlink_to(self.package, target_is_directory=True)
        except (OSError, NotImplementedError):
            self.skipTest("This platform does not permit creating a test symlink.")
        with self.assertRaisesRegex(patcher.PatchError, "Symlinks"):
            patcher.patch_package(alias)
        self.source.unlink()
        self.source.symlink_to(target)
        with self.assertRaisesRegex(patcher.PatchError, "Symlinks"):
            patcher.patch_package(self.package)
        self.assertEqual(target.read_bytes(), FIXTURE)
        self.source.unlink()
        self.source.write_bytes(FIXTURE)
        manifest_target = self.root / "external-package.json"
        manifest_target.write_bytes(self.manifest.read_bytes())
        self.manifest.unlink()
        self.manifest.symlink_to(manifest_target)
        with self.assertRaisesRegex(patcher.PatchError, "Symlinks"):
            patcher.patch_package(self.package)
        self.assert_no_staging_files()

    def test_link_and_windows_reparse_attributes_are_rejected(self):
        for info in [SimpleNamespace(st_mode=stat.S_IFDIR, st_file_attributes=0x400),
                     SimpleNamespace(st_mode=stat.S_IFLNK, st_file_attributes=0)]:
            with self.subTest(mode=info.st_mode):
                with mock.patch.object(Path, "lstat", return_value=info):
                    with self.assertRaisesRegex(patcher.PatchError, "reparse"):
                        patcher.require_no_links(self.package)

    def test_hard_linked_source_is_rejected_without_affecting_original(self):
        alias = self.root / "hard-linked-original"
        try:
            os.link(self.source, alias)
        except (OSError, NotImplementedError):
            self.skipTest("This platform does not permit creating a test hard link.")
        with self.assertRaisesRegex(patcher.PatchError, "hard links"):
            patcher.patch_package(self.package)
        self.assertEqual(alias.read_bytes(), FIXTURE)
        self.assert_no_staging_files()

    def test_failed_atomic_replace_preserves_original_and_cleans_stage(self):
        with mock.patch.object(patcher.os, "replace", side_effect=OSError("simulated replacement failure")):
            with self.assertRaises(OSError):
                patcher.patch_package(self.package)
        self.assertEqual(self.source.read_bytes(), FIXTURE)
        self.assert_no_staging_files()

    def test_read_only_original_can_be_checked_but_is_not_written(self):
        original_mode = stat.S_IMODE(self.source.stat().st_mode)
        try:
            self.source.chmod(stat.S_IRUSR)
            self.assertEqual(patcher.patch_package(self.package, check_only=True)["status"], "checked_original")
            with self.assertRaisesRegex(patcher.PatchError, "read-only"):
                patcher.patch_package(self.package)
            self.assertEqual(self.source.read_bytes(), FIXTURE)
            self.assert_no_staging_files()
        finally:
            self.source.chmod(original_mode)

    def test_concurrent_source_or_manifest_change_aborts_before_replace(self):
        real_fsync = os.fsync
        for target in [self.source, self.manifest]:
            with self.subTest(target=target.name):
                self.source.write_bytes(FIXTURE)
                self.write_manifest()
                before = target.read_bytes()
                changed = before + b"\n"
                def concurrent_write(descriptor):
                    real_fsync(descriptor)
                    target.write_bytes(changed)
                with mock.patch.object(patcher.os, "fsync", side_effect=concurrent_write):
                    with self.assertRaisesRegex(patcher.PatchError, "changed during validation"):
                        patcher.patch_package(self.package)
                self.assertEqual(target.read_bytes(), changed)
                self.assert_no_staging_files()

    def test_cli_requires_explicit_input_and_does_not_print_machine_paths(self):
        stdout, stderr = io.StringIO(), io.StringIO()
        with redirect_stdout(stdout), redirect_stderr(stderr):
            self.assertEqual(patcher.main([str(self.package), "--check-only"]), 0)
        result = json.loads(stdout.getvalue())
        self.assertEqual(result["status"], "checked_original")
        self.assertEqual(result["original_version"], "1.9.20")
        self.assertNotIn(str(self.root), stdout.getvalue() + stderr.getvalue())
        self.write_manifest(version="1.9.21")
        with redirect_stdout(stdout), redirect_stderr(stderr):
            self.assertEqual(patcher.main([str(self.package)]), 1)
            self.assertEqual(patcher.main([""]), 1)
            with self.assertRaises(SystemExit) as missing:
                patcher.main([])
        self.assertEqual(missing.exception.code, 2)
        self.assertNotIn(str(self.root), stdout.getvalue() + stderr.getvalue())


if __name__ == "__main__":
    unittest.main()
