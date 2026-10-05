"""Validate the reviewed commit, package version and explicit release channel."""
import json
import os
import re
from pathlib import Path


def validate(package, compatibility, *, version, univrm_version, channel, ref, sha, expected_sha):
    if ref != "refs/heads/main":
        raise ValueError("Publish only the reviewed main branch")
    if not re.fullmatch(r"[0-9a-f]{40}", expected_sha) or sha != expected_sha:
        raise ValueError("The main commit must equal the reviewed expected commit")
    if version != package["version"]:
        raise ValueError("Requested version must equal package.json")
    number = r"(?:0|[1-9][0-9]*)"
    stable = rf"{number}\.{number}\.{number}"
    patterns = {"stable": stable, "beta": stable + rf"-beta\.{number}"}
    if channel not in patterns or not re.fullmatch(patterns[channel], version):
        raise ValueError("Release channel and version must agree (stable or X.Y.Z-beta.N)")
    if univrm_version != compatibility["uniVrm"]["releaseVersion"]:
        raise ValueError("UniVRM version must match the pinned release dependency")
    return ["--prerelease", "--latest=false"] if channel == "beta" else ["--latest"]


if __name__ == "__main__":
    root = Path(__file__).resolve().parents[1]
    flags = validate(
        json.loads((root / "package.json").read_text(encoding="utf-8")),
        json.loads((root / "Compatibility/dependencies.json").read_text(encoding="utf-8")),
        version=os.environ["EXPORTER_VERSION"], univrm_version=os.environ["UNIVRM_VERSION"],
        channel=os.environ["RELEASE_CHANNEL"], ref=os.environ["GITHUB_REF"],
        sha=os.environ["GITHUB_SHA"], expected_sha=os.environ["EXPECTED_COMMIT"],
    )
    # Only hard-coded flags reach the shell; no workflow input becomes an option.
    print("\n".join(flags))
