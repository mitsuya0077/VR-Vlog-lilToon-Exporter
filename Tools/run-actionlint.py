"""Run actionlint 1.7.11 from a SHA-256 pinned official release archive."""
import hashlib
import io
import platform
import subprocess
import tarfile
import tempfile
import urllib.request
from pathlib import Path

VERSION = "1.7.11"
ARCHIVES = {
    ("Linux", "x86_64"): ("linux_amd64", "900919a84f2229bac68ca9cd4103ea297abc35e9689ebb842c6e34a3d1b01b0a"),
    ("Darwin", "arm64"): ("darwin_arm64", "a21ba7366d8329e7223faee0ed69eb13da27fe8acabb356bb7eb0b7f1e1cb6d8"),
}
ROOT = Path(__file__).resolve().parents[1]


def binary_from_archive(data, expected):
    if hashlib.sha256(data).hexdigest() != expected:
        raise ValueError("actionlint archive SHA-256 mismatch")
    with tarfile.open(fileobj=io.BytesIO(data), mode="r:gz") as archive:
        member = archive.getmember("actionlint")
        if not member.isfile() or member.size > 40 * 1024 * 1024:
            raise ValueError("Invalid actionlint binary")
        return archive.extractfile(member).read()


if __name__ == "__main__":
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
        raise SystemExit(subprocess.run([str(binary), "-shellcheck=", "-pyflakes=", *files],
                                        cwd=ROOT, timeout=60, check=False).returncode)
