"""Run actionlint 1.7.12 from a SHA-256 pinned official release archive."""
import hashlib
import importlib.util
import io
import json
import platform
import subprocess
import tarfile
import tempfile
import urllib.request
from pathlib import Path

VERSION = "1.7.12"
ARCHIVES = {
    ("Linux", "x86_64"): ("linux_amd64", "8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8"),
    ("Darwin", "arm64"): ("darwin_arm64", "aba9ced2dee8d27fecca3dc7feb1a7f9a52caefa1eb46f3271ea66b6e0e6953f"),
}
ROOT = Path(__file__).resolve().parents[1]
QUEUE_MESSAGE = 'unexpected key "queue" for "concurrency" section. expected one of "cancel-in-progress", "group"'


def approved_queue_positions(root=ROOT):
    """Bridge only GitHub's queue syntax missing from upstream actionlint 1.7.12."""
    spec = importlib.util.spec_from_file_location("pipeline_policy", root / "Tools/check-pipeline.py")
    policy = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(policy)
    policy.check_all(root)
    locations = {
        "release-vpm.yml": ("concurrency", "queue"),
        "release-candidate.yml": ("concurrency", "queue"),
        "build-listing.yml": ("jobs", "build-listing", "concurrency", "queue"),
    }
    approved = set()
    for name, keys in locations.items():
        path = root / ".github/workflows" / name
        node = policy.yaml.compose(path.read_text(encoding="utf-8"), Loader=policy.WorkflowLoader)
        for key in keys:
            key_node, node = next(pair for pair in node.value if pair[0].value == key)
        approved.add((str(path.relative_to(root)), key_node.start_mark.line + 1, key_node.start_mark.column + 1))
    return approved


def blocking_diagnostics(diagnostics, approved):
    if not isinstance(diagnostics, list) or not all(isinstance(item, dict) for item in diagnostics):
        raise ValueError("Invalid actionlint diagnostics")
    return [item for item in diagnostics if not (
        item.get("kind") == "syntax-check" and item.get("message") == QUEUE_MESSAGE
        and (item.get("filepath"), item.get("line"), item.get("column")) in approved)]


def binary_from_archive(data, expected):
    if hashlib.sha256(data).hexdigest() != expected:
        raise ValueError("actionlint archive SHA-256 mismatch")
    with tarfile.open(fileobj=io.BytesIO(data), mode="r:gz") as archive:
        member = archive.getmember("actionlint")
        if not member.isfile() or member.size > 40 * 1024 * 1024:
            raise ValueError("Invalid actionlint binary")
        return archive.extractfile(member).read()


if __name__ == "__main__":
    approved = approved_queue_positions()
    suffix, expected = ARCHIVES[(platform.system(), platform.machine())]
    url = f"https://github.com/rhysd/actionlint/releases/download/v{VERSION}/actionlint_{VERSION}_{suffix}.tar.gz"
    with urllib.request.urlopen(url, timeout=60) as response:
        data = response.read(20 * 1024 * 1024 + 1)
    if len(data) > 20 * 1024 * 1024:
        raise SystemExit("actionlint archive is too large")
    with tempfile.TemporaryDirectory(prefix="exporter-actionlint-") as directory:
        binary = Path(directory) / "actionlint"
        binary.write_bytes(binary_from_archive(data, expected))
        binary.chmod(0o700)
        files = sorted(str(path.relative_to(ROOT)) for path in (ROOT / ".github/workflows").glob("*.yml"))
        # The independent YAML/expression checker runs without optional local linters.
        result = subprocess.run([str(binary), "-shellcheck=", "-pyflakes=", "-format", "{{json .}}", *files],
                                cwd=ROOT, timeout=60, check=False, capture_output=True, text=True)
        if result.returncode not in (0, 1) or result.stderr:
            raise SystemExit("actionlint failed to produce clean diagnostics")
        diagnostics = json.loads(result.stdout)
        if diagnostics is None and result.returncode == 0:
            diagnostics = []
        errors = blocking_diagnostics(diagnostics, approved)
        if errors or (result.returncode != 0 and not diagnostics):
            print(json.dumps(errors, indent=2))
            raise SystemExit(1)
        print("actionlint passed; queue: max checked by the repository policy (upstream 1.7.12 lacks this syntax).")
