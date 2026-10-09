"""Build a non-publishing candidate tied to a complete host-validation receipt."""
import argparse
import hashlib
import importlib.util
import json
import os
import re
import subprocess
import zipfile
from pathlib import Path, PurePosixPath
from urllib.parse import unquote, urlsplit

ROOT = Path(__file__).resolve().parents[1]
REPOSITORY = "mitsuya0077/VR-Vlog-lilToon-Exporter"
MAX_PACKAGE_BYTES = 20 * 1024 * 1024


def load_tool(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / "Tools" / (name + ".py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def validate_report(report, sha):
    runner = load_tool("run-validation")
    expected = [command[1:] for command in runner.plan()]
    if not isinstance(report, dict):
        raise ValueError("Invalid host validation receipt")
    checks = report.get("checks", [])
    if not isinstance(checks, list) or not all(isinstance(row, dict) for row in checks):
        raise ValueError("Invalid host validation checks")
    if (type(report.get("schema")) is not int or report["schema"] != 1 or report.get("commit") != sha
            or report.get("scope") != "host-only" or report.get("passed") is not True
            or not re.fullmatch(r"3\.12\.\d+", report.get("python", ""))
            or report.get("expected_checks") != len(expected)
            or [row.get("command") for row in checks] != expected
            or any(type(row.get("exit_code")) is not int or row["exit_code"] != 0 for row in checks)):
        raise ValueError("Complete successful host validation for this commit is required")


def check_zip_links(path):
    # Inspect the delivered ZIP, not just the source tree.
    with zipfile.ZipFile(path) as archive:
        names = set(archive.namelist())
        for name in names:
            if not name.endswith(".md"):
                continue
            text = archive.read(name).decode("utf-8-sig")
            text = re.sub(r"```.*?```", "", text, flags=re.S)
            for match in re.finditer(r"\]\((<[^>]+>|[^\s)]+)(?:\s+[^)]*)?\)", text):
                target = match[1].strip("<>")
                link = urlsplit(target)
                if link.scheme or link.netloc or not link.path:
                    continue
                parts = list(PurePosixPath(name).parent.parts)
                decoded = unquote(link.path)
                if decoded.startswith("/") or "\\" in decoded:
                    raise ValueError("Invalid relative ZIP link in " + name)
                for part in decoded.split("/"):
                    if part == "..":
                        if not parts:
                            raise ValueError("ZIP link escapes package in " + name)
                        parts.pop()
                    elif part not in {"", "."}:
                        parts.append(part)
                resolved = "/".join(parts)
                if resolved not in names:
                    raise ValueError("Missing ZIP link target in " + name + ": " + resolved)


def build(root, output, report, expected_sha, run_id=None):
    output = output if output.is_absolute() else root / output
    component = output
    while component != root and component != component.parent:
        if component.is_symlink():
            raise ValueError("Candidate output must not use symlinks")
        component = component.parent
    if not output.resolve().is_relative_to((root / "work").resolve()):
        raise ValueError("Candidate output must be under the task's ignored work directory")
    if output.exists() and (output.is_symlink() or any(output.iterdir())):
        raise ValueError("Candidate output must be empty; preserve earlier candidates separately")
    sha = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
    if not re.fullmatch(r"[0-9a-f]{40}", expected_sha) or sha != expected_sha:
        raise ValueError("Expected commit differs from checked-out source")
    if subprocess.check_output(["git", "status", "--porcelain"], cwd=root):
        raise ValueError("Candidate source must have no tracked or untracked changes")
    validate_report(report, sha)
    package = load_tool("build-package")
    version = json.loads((root / "package.json").read_text(encoding="utf-8"))["version"]
    if not package.VERSION.fullmatch(version):
        raise ValueError("Invalid candidate version")
    output.mkdir(parents=True, exist_ok=True)
    filename = f"com.vrvlog.liltoon-vrm-exporter-{version}.zip"
    path = output / filename
    package.build(root, path)
    if path.stat().st_size > MAX_PACKAGE_BYTES:
        raise ValueError("Candidate exceeds the reviewed 20 MiB package budget")
    check_zip_links(path)
    metadata = {"schema": 1, "repository": REPOSITORY, "commit": sha,
                "run_id": run_id, "version": version, "package": filename,
                "package_sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                "validation_scope": "host-only"}
    (output / "candidate.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
    (output / "validation.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    return metadata


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--expected-commit", required=True)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--output", type=Path, default=ROOT / "work/candidate")
    args = parser.parse_args()
    run_id = os.environ.get("GITHUB_RUN_ID")
    metadata = build(ROOT, args.output, json.loads(args.report.read_text(encoding="utf-8")),
                     args.expected_commit, int(run_id) if run_id else None)
    print(json.dumps(metadata, indent=2))
