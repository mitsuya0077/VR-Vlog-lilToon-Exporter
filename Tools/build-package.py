"""Build the Unity package from an explicit allowlist of tracked files."""
import argparse
import json
import re
import subprocess
import zipfile
from pathlib import Path, PurePosixPath

ROOT = Path(__file__).resolve().parents[1]
ROOT_FILES = {"package.json", "LICENSE", "CHANGELOG.md", "Documentation~/README.md",
              "Documentation~/HumanoidPoses.md", "Documentation~/HumanoidAnimations.md"}
DEPENDENCY_PATCH_FILES = {
    "Tools/patch-aao-vertex-buffer.py", "Tools/patch-aao-vertex-buffer.py.meta",
    "Documentation~/DependencyPatches/README.md",
    "Documentation~/DependencyPatches/aao-1.9.20-dispose-vertex-buffer.patch",
    "Documentation~/DependencyPatches/AAO-LICENSE.txt",
}
PACKAGE_SUFFIXES = {".cs", ".asmdef", ".meta", ".shader"}
LOCALE_ASSETS = {
    f"Editor/Locales/ExporterLocale_{locale}.json"
    for locale in ("en", "ko", "zh-Hans", "zh-Hant")
}
LOCALE_FILES = LOCALE_ASSETS | {name + ".meta" for name in LOCALE_ASSETS}
TRANSFER_FILES = {
    "Editor/LanTransfer.meta",
    "Documentation~/LanTransfer.md", "Documentation~/CloudTransfer.md",
    "ThirdPartyNotices/LanTransfer.md", "ThirdPartyNotices/BouncyCastle-LICENSE.txt", "ThirdPartyNotices/ZXing-LICENSE.txt",
}
TRANSFER_DLLS = {"bouncycastle.cryptography.dll", "zxing.dll"}
# Source and host tests retain the unreleased implementation. Distribution must
# also fail if a caller or copied implementation is added outside that subtree.
TRANSFER_REFERENCE = re.compile(
    r"\b(?:LanTransfer\w*|CloudTransfer\w*|CloudDevelopment\w*|(?:Cloud|Lan)VrmTransfer\w*|CloudEncryptedSnapshot|ZXing|BouncyCastle)\b"
    r"|\b(?:GUID:)?b15e228f27f843bdbdd4c2335be4354b\b", re.IGNORECASE)


def tracked_files(root):
    data = subprocess.check_output(["git", "-C", str(root), "ls-files", "-z"])
    return sorted(set(data.decode("utf-8").split("\0")) - {""})


def included(name):
    path = PurePosixPath(name)
    if path.is_absolute() or ".." in path.parts:
        return False
    if (name.startswith("Editor/LanTransfer/") or name in TRANSFER_FILES
            or name.removesuffix(".meta") in TRANSFER_FILES
            or path.name.lower().removesuffix(".meta") in TRANSFER_DLLS):
        return False
    return (name in ROOT_FILES or name in LOCALE_FILES or name in DEPENDENCY_PATCH_FILES
            or name == "Runtime.meta"
            or (path.parts[0] in {"Editor", "Runtime"} and path.suffix in PACKAGE_SUFFIXES)
            or (path.parts[0] == "ThirdPartyNotices" and path.suffix in {".md", ".txt"}))


def verify_no_transfer_dependencies(contents):
    for name, content in contents.items():
        if (name.startswith("Editor/LanTransfer/") or name in TRANSFER_FILES
                or name.removesuffix(".meta") in TRANSFER_FILES
                or PurePosixPath(name).name.lower().removesuffix(".meta") in TRANSFER_DLLS):
            raise ValueError("QR transfer input cannot be distributed: " + name)
        if PurePosixPath(name).suffix not in {".cs", ".asmdef"}:
            continue
        try:
            text = content.decode("utf-8-sig")
        except UnicodeDecodeError as error:
            raise ValueError("Package source must be UTF-8: " + name) from error
        # C# identifiers and JSON strings can spell a reference with Unicode
        # escapes. Normalize those too; report the path, never source contents.
        text = re.sub(r"\\u([0-9a-fA-F]{4})|\\U([0-9a-fA-F]{8})",
                      lambda match: chr(int(match[1] or match[2], 16)), text)
        if TRANSFER_REFERENCE.search(text):
            raise ValueError("QR transfer dependency cannot be distributed: " + name)


def build(root, output):
    root = root.resolve()
    names = [name for name in tracked_files(root) if included(name)]
    missing = (ROOT_FILES | LOCALE_FILES | DEPENDENCY_PATCH_FILES) - set(names)
    if missing:
        raise ValueError("Required package files are not tracked: " + ", ".join(sorted(missing)))
    contents = {}
    for name in names:
        path = root / name
        # Check every path component: a symlinked directory is also an escape.
        component = root
        for part in PurePosixPath(name).parts:
            component = component / part
            if component.is_symlink():
                raise ValueError("Symlinks are not package inputs: " + name)
        if not path.resolve().is_relative_to(root.resolve()):
            raise ValueError("Package input escapes repository: " + name)
        contents[name] = path.read_bytes()
    verify_no_transfer_dependencies(contents)
    if not any(name.startswith("Editor/") and name.endswith(".cs") for name in names):
        raise ValueError("Package has no Editor source")
    output.parent.mkdir(parents=True, exist_ok=True)
    # Stored entries avoid platform/zlib-dependent DEFLATE bytes. Pin the
    # creator OS too: Python otherwise emits different Windows/Unix headers.
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_STORED) as archive:
        for name, content in contents.items():
            entry = zipfile.ZipInfo(name, date_time=(2020, 1, 1, 0, 0, 0))
            entry.create_system = 3
            entry.compress_type = zipfile.ZIP_STORED
            entry.external_attr = 0o100644 << 16
            archive.writestr(entry, content)
    return names


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    version = json.loads((ROOT / "package.json").read_text(encoding="utf-8"))["version"]
    output = args.output or ROOT / "artifacts" / f"com.vrvlog.liltoon-vrm-exporter-{version}.zip"
    names = build(ROOT, output)
    print(f"Built {output.name}: {len(names)} package files")
