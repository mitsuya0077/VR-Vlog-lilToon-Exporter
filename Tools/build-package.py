"""Build the Unity package from an explicit allowlist of tracked files."""
import argparse
import json
import subprocess
import zipfile
from pathlib import Path, PurePosixPath

ROOT = Path(__file__).resolve().parents[1]
ROOT_FILES = {"package.json", "LICENSE", "CHANGELOG.md", "Documentation~/README.md"}
EDITOR_SUFFIXES = {".cs", ".asmdef", ".meta"}


def tracked_files(root):
    data = subprocess.check_output(["git", "-C", str(root), "ls-files", "-z"])
    return sorted(set(data.decode("utf-8").split("\0")) - {""})


def included(name):
    path = PurePosixPath(name)
    if path.is_absolute() or ".." in path.parts:
        return False
    return (name in ROOT_FILES
            or (path.parts[0] == "Editor" and path.suffix in EDITOR_SUFFIXES)
            or (path.parts[0] == "ThirdPartyNotices" and path.suffix in {".md", ".txt"}))


def build(root, output):
    root = root.resolve()
    names = [name for name in tracked_files(root) if included(name)]
    missing = ROOT_FILES - set(names)
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
    if not any(name.startswith("Editor/") and name.endswith(".cs") for name in names):
        raise ValueError("Package has no Editor source")
    output.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for name, content in contents.items():
            entry = zipfile.ZipInfo(name, date_time=(2020, 1, 1, 0, 0, 0))
            entry.compress_type = zipfile.ZIP_DEFLATED
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
