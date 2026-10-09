"""Run the same host checks for PRs, main, candidates and publication.

This receipt covers host checks, not Unity Editor or device acceptance.
"""
import argparse
import json
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PYTHON_CHECKS = (
    "check-pipeline.py", "test-pipeline.py", "validate.py",
    "test-release-policy.py", "test-listing.py", "check-public-content.py",
    "test-package.py", "test-aao-vertex-buffer-patch.py",
    "generate-compatibility.py", "test-compatibility-tooling.py",
)
HOST_CHECKS = (
    "run-compatibility-tests.ps1", "run-behavior-tests.ps1",
    "run-pose-contract-tests.ps1", "run-bake-tests.ps1",
    "run-ndmf-preparation-tests.ps1",
)
TRANSFER_MODES = ("Default", "EditorOnly", "DevelopmentOnly", "DevelopmentEditor")


def plan(python=sys.executable, pwsh="pwsh"):
    commands = [[python, "Tools/" + name] for name in PYTHON_CHECKS]
    commands += [[pwsh, "-NoLogo", "-NoProfile", "-File", "Tools/" + name]
                 for name in HOST_CHECKS]
    commands += [[pwsh, "-NoLogo", "-NoProfile", "-File",
                  "Tools/run-lan-transfer-tests.ps1", "-CloudAvailabilityMode", mode]
                 for mode in TRANSFER_MODES]
    return commands


def run_checks(commands, root, execute=subprocess.run):
    results = []
    for command in commands:
        # No shell or PR/workflow input is interpolated into these commands.
        print("Running " + " ".join(command[1:]), flush=True)
        started = time.monotonic()
        try:
            completed = execute(command, cwd=root, timeout=600, check=False)
            code = completed.returncode
        except (OSError, subprocess.TimeoutExpired):
            code = 1
        results.append({"command": command[1:], "exit_code": code,
                        "seconds": round(time.monotonic() - started, 3)})
        if code:
            break
    return results


def source_state(root):
    sha = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
    dirty = subprocess.check_output(["git", "status", "--porcelain", "--untracked-files=all"], cwd=root)
    return sha, not dirty


def validation_receipt(commands, root, execute=subprocess.run, snapshot=source_state):
    before_sha, before_clean = snapshot(root)
    results = run_checks(commands, root, execute)
    after_sha, after_clean = snapshot(root)
    passed = (before_sha == after_sha and len(results) == len(commands)
              and all(r["exit_code"] == 0 for r in results))
    return {"schema": 1, "commit": before_sha, "scope": "host-only",
            "python": sys.version.split()[0], "source_clean": before_clean and after_clean,
            "passed": passed, "checks": results, "expected_checks": len(commands)}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pwsh", default="pwsh")
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    commands = plan(pwsh=args.pwsh)
    report = validation_receipt(commands, ROOT)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    raise SystemExit(0 if report["passed"] else 1)
