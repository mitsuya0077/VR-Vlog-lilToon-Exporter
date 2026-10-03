#!/usr/bin/env python3
"""Explicit, hash-pinned AAO 1.9.20 source correction; never called by export."""
import argparse
import codecs
import hashlib
import json
import os
from pathlib import Path
import stat
import sys
import tempfile

PACKAGE_NAME = "com.anatawa12.avatar-optimizer"
PACKAGE_VERSION = "1.9.20"
PATCH_ID = "vrvlog.aao-1.9.20.vertex-buffer-dispose.1"
UPSTREAM_COMMIT = "439a56aae2cc744c2a1590e79067353879496b8e"
ORIGINAL_SHA256 = "fc6d349153753d83b9e588727ce3328ef3db6c4f185668d8a6a73218a4a3a4af"
PATCHED_SHA256 = "7c002a439e4a19f83f47f8db1f43db16ec1016cbad933f691004f114671516d2"
SOURCE_RELATIVE = Path("Internal/MeshInfo2/MeshInfo2.cs")
METHOD = b"        private static (byte[] buffer, int stride)[] GetVertexBuffers(Mesh mesh)"
ORIGINAL_LINE = b"                var vertexBuffer = mesh.GetVertexBuffer(i);"
PATCHED_LINE = b"                using var vertexBuffer = mesh.GetVertexBuffer(i);"


class PatchError(ValueError):
    """Safe-to-print error without user paths or source contents."""


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def normalized(data):
    payload = data[len(codecs.BOM_UTF8):] if data.startswith(codecs.BOM_UTF8) else data
    payload.decode("utf-8")
    payload = payload.replace(b"\r\n", b"\n")
    if b"\r" in payload:
        raise PatchError("Bare CR line endings are unsupported; source was not changed.")
    return payload


def require_no_links(path):
    # Inspect every supplied component before resolve(), including Windows
    # junctions/reparse points. Resolving first could hide a linked ancestor.
    absolute = path.absolute()
    cursor = Path(absolute.anchor)
    for part in absolute.parts[1:]:
        cursor /= part
        info = cursor.lstat()
        reparse = getattr(info, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
        if stat.S_ISLNK(info.st_mode) or reparse:
            raise PatchError("Symlinks, junctions and reparse points are unsupported; source was not changed.")


def require_regular(path):
    require_no_links(path)
    info = path.lstat()
    if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1:
        raise PatchError("Package inputs must be regular files without hard links; source was not changed.")
    return info


def validate_package(package_directory):
    if not os.fspath(package_directory).strip():
        raise PatchError("An explicit package directory path is required; no discovery or source changes were performed.")
    package = Path(package_directory).expanduser().absolute()
    if any(part.casefold() in {"packagecache", "vpmcache", ".cache"} for part in package.parts):
        raise PatchError("Package cache directories are unsupported; use a separately owned package copy.")
    require_no_links(package)
    if not package.is_dir():
        raise PatchError("An explicit existing package directory is required; source was not changed.")
    package = package.resolve(strict=True)
    manifest_path = package / "package.json"
    source_path = package / SOURCE_RELATIVE
    require_regular(manifest_path)
    source_stat = require_regular(source_path)
    manifest_bytes = manifest_path.read_bytes()
    manifest = json.loads(manifest_bytes.decode("utf-8-sig"))
    if not isinstance(manifest, dict) or manifest.get("name") != PACKAGE_NAME or manifest.get("version") != PACKAGE_VERSION:
        raise PatchError("Only com.anatawa12.avatar-optimizer version 1.9.20 is supported; source was not changed.")
    return manifest_path, manifest_bytes, source_path, source_stat


def prepare_patch(original):
    content = normalized(original)
    digest = sha256(content)
    if digest == PATCHED_SHA256:
        return original, "already_patched"
    if digest != ORIGINAL_SHA256:
        raise PatchError("MeshInfo2.cs does not match the exact official or patched source hash; source was not changed.")
    method_start = content.find(METHOD)
    method_end = content.find(b"        delegate T DataParser", method_start)
    if (method_start < 0 or method_end < 0 or content.count(ORIGINAL_LINE) != 1
            or content[method_start:method_end].count(ORIGINAL_LINE) != 1):
        raise PatchError("The expected GetVertexBuffers declaration is missing or ambiguous; source was not changed.")
    patched = original.replace(ORIGINAL_LINE, PATCHED_LINE, 1)
    if sha256(normalized(patched)) != PATCHED_SHA256:
        raise PatchError("The candidate does not match the exact patched source hash; source was not changed.")
    return patched, "needs_patch"


def atomic_replace(source, original, patched, source_stat, manifest_path, manifest_bytes):
    staged = None
    try:
        with tempfile.NamedTemporaryFile(prefix=".vrvlog-aao-patch-", dir=source.parent, delete=False) as stream:
            staged = Path(stream.name)
            stream.write(patched)
            stream.flush()
            os.fsync(stream.fileno())
        os.chmod(staged, stat.S_IMODE(source_stat.st_mode))
        current = require_regular(source)
        require_regular(manifest_path)
        if ((current.st_dev, current.st_ino) != (source_stat.st_dev, source_stat.st_ino)
                or source.read_bytes() != original or manifest_path.read_bytes() != manifest_bytes):
            raise PatchError("Package inputs changed during validation; the staged patch was not applied.")
        if staged.read_bytes() != patched:
            raise PatchError("The staged patch differs from the validated candidate; source was not changed.")
        os.replace(staged, source)
        staged = None
    finally:
        if staged is not None:
            staged.unlink(missing_ok=True)


def patch_package(package_directory, check_only=False):
    manifest_path, manifest_bytes, source, source_stat = validate_package(package_directory)
    original = source.read_bytes()
    patched, status = prepare_patch(original)
    if status == "needs_patch" and not check_only:
        if not source_stat.st_mode & stat.S_IWUSR:
            raise PatchError("The source copy is read-only; make the owned copy writable before applying.")
        atomic_replace(source, original, patched, source_stat, manifest_path, manifest_bytes)
        status = "applied"
    elif status == "needs_patch":
        status = "checked_original"
    applied = status in {"applied", "already_patched"}
    return {
        "package": PACKAGE_NAME, "original_version": PACKAGE_VERSION,
        "effective_version": PACKAGE_VERSION + "+" + PATCH_ID if applied else PACKAGE_VERSION,
        "patch": PATCH_ID, "applied_patch": PATCH_ID if applied else None,
        "status": status, "check_only": check_only, "upstream_commit": UPSTREAM_COMMIT,
        "source_sha256_before_normalized": sha256(normalized(original)),
        "source_sha256_after_normalized": sha256(normalized(patched if applied else original)),
        "source_sha256_before_raw": sha256(original),
        "source_sha256_after_raw": sha256(patched if applied else original),
    }


def main(argv=None):
    parser = argparse.ArgumentParser(prog="patch-aao-vertex-buffer.py", description=__doc__)
    parser.add_argument("package_directory", help="Explicit separately owned AAO 1.9.20 package copy; no discovery.")
    parser.add_argument("--check-only", action="store_true", help="Validate and report status without writing any file.")
    args = parser.parse_args(argv)
    try:
        result = patch_package(args.package_directory, args.check_only)
    except PatchError as error:
        print(str(error), file=sys.stderr)
        return 1
    except (OSError, UnicodeError, json.JSONDecodeError):
        print("Cannot read or replace validated package inputs; no successful patch was recorded.", file=sys.stderr)
        return 1
    print(json.dumps(result, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
