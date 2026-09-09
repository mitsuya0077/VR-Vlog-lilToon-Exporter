"""Catch common accidental disclosures in tracked public source (not a full secret audit)."""
import re
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PRIVATE_SUFFIXES = {".vrm", ".fbx", ".blend", ".p12", ".pfx", ".pem", ".key", ".mobileprovision", ".log"}
PRIVATE_DIRS = {"work", "outputs", "artifacts", "Library", "Temp", "Logs", "UserSettings", ".codex", ".agents"}
PATTERNS = {
    "private key": re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----"),
    "access token": re.compile(r"\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,}|AKIA[A-Z0-9]{16}|sk-(?:proj-)?[A-Za-z0-9_-]{30,})\b"),
    "personal absolute path": re.compile(r"(?:[A-Za-z]:[\\/]+Users[\\/]+[^\s'\"<>]+|/(?:Users|home)/[^\s'\"<>]+)"),
}


def text_views(data):
    views = [data.decode("utf-8-sig", errors="replace")]
    # A UTF-16LE BOM followed by a NUL is also a UTF-32LE BOM.
    # Keep every plausible interpretation instead of returning on one match.
    for bom, encoding in [(b"\xff\xfe\x00\x00", "utf-32"), (b"\x00\x00\xfe\xff", "utf-32"),
                          (b"\xff\xfe", "utf-16"), (b"\xfe\xff", "utf-16")]:
        if data.startswith(bom):
            views.append(data.decode(encoding, errors="replace"))
    if b"\0" in data:
        # Also inspect embedded ASCII strings and BOM-less UTF-16/32 text.
        # A binary-looking file must not bypass checks entirely.
        views.append(data.replace(b"\0", b"").decode("utf-8", errors="replace"))
    return list(dict.fromkeys(views))


def inspect(name, data):
    path = Path(name)
    findings = []
    if (path.suffix.lower() in PRIVATE_SUFFIXES or set(path.parts) & PRIVATE_DIRS
            or path.name == ".env" or path.name.startswith(".env.")):
        findings.append((0, "private/local input type"))
    for view in text_views(data):
        for line, text in enumerate(view.splitlines(), 1):
            for label, pattern in PATTERNS.items():
                if pattern.search(text):
                    findings.append((line, label))
    return sorted(set(findings))


if __name__ == "__main__":
    names = subprocess.check_output(["git", "-C", str(ROOT), "ls-files", "-z"]).decode("utf-8").split("\0")
    findings = []
    for name in filter(None, names):
        path = ROOT / name
        if path.is_symlink() or not path.resolve().is_relative_to(ROOT.resolve()):
            findings.append((name, 0, "symlink or external input"))
            continue
        findings.extend((name, line, label) for line, label in inspect(name, path.read_bytes()))
    for name, line, label in findings:
        # Never print the matched value, including when CI fails.
        print(f"{name}:{line}: {label}")
    if findings:
        raise SystemExit(1)
    print("Public content check passed (tracked files; common disclosure patterns).")
