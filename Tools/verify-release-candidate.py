"""Verify an authentic completed candidate run before publication.

Only JSON and package bytes are inspected; downloaded code is never executed.
The owner must separately approve Unity/device acceptance and publication.
"""
import argparse
import hashlib
import io
import json
import re
import subprocess
import sys
import zipfile

from pathlib import Path
import importlib.util

REPOSITORY = "mitsuya0077/VR-Vlog-lilToon-Exporter"
WORKFLOW = ".github/workflows/release-candidate.yml"
MAX_ARCHIVE_BYTES = 24 * 1024 * 1024


def candidate_tool():
    spec = importlib.util.spec_from_file_location("candidate", Path(__file__).with_name("build-release-candidate.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def verify_run(run, workflow_id, sha):
    if (run.get("repository", {}).get("full_name") != REPOSITORY
            or run.get("workflow_id") != workflow_id or run.get("path") != WORKFLOW
            or run.get("head_sha") != sha or run.get("head_branch") != "main"
            or run.get("event") != "workflow_dispatch" or run.get("status") != "completed"
            or run.get("conclusion") != "success"):
        raise ValueError("Candidate must be a successful manual main run of the approved workflow for this SHA")


def select_artifact(artifacts, sha):
    matches = [row for row in artifacts if row.get("name") == "release-candidate-" + sha]
    if len(matches) != 1:
        raise ValueError("Exactly one candidate artifact is required")
    artifact = matches[0]
    size = artifact.get("size_in_bytes")
    if artifact.get("expired") is not False or type(size) is not int or not 0 < size <= MAX_ARCHIVE_BYTES:
        raise ValueError("Candidate artifact is expired or exceeds its size budget")
    return artifact


def verify_archive(data, sha, version, expected_hash, run_id):
    if len(data) > MAX_ARCHIVE_BYTES:
        raise ValueError("Candidate archive exceeds its size budget")
    if not re.fullmatch(r"[0-9a-f]{64}", expected_hash):
        raise ValueError("A complete expected package SHA-256 is required")
    package = f"com.vrvlog.liltoon-vrm-exporter-{version}.zip"
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        entries = archive.infolist()
        if len(entries) != 3 or {row.filename for row in entries} != {"candidate.json", "validation.json", package}:
            raise ValueError("Candidate archive inventory differs from the approved three files")
        for row in entries:
            limit = 20 * 1024 * 1024 if row.filename == package else 128 * 1024
            if row.file_size > limit or row.flag_bits & 1:
                raise ValueError("Invalid candidate entry size or encryption")
        metadata = json.loads(archive.read("candidate.json"))
        report = json.loads(archive.read("validation.json"))
        actual = hashlib.sha256(archive.read(package)).hexdigest()
    expected = {"schema": 1, "repository": REPOSITORY, "commit": sha,
                "run_id": run_id, "version": version, "package": package,
                "package_sha256": expected_hash, "validation_scope": "host-only"}
    if type(metadata.get("schema")) is not int or metadata != expected or actual != expected_hash:
        raise ValueError("Candidate package/receipt differs from the approved commit, version, run or hash")
    candidate_tool().validate_report(report, sha)


def gh_api(path, binary=False):
    # Native gh handles authentication and redirect credential rules. Never print stderr
    # or signed download URLs, and never execute data from the artifact.
    completed = subprocess.run(["gh", "api", path], capture_output=True, timeout=90, check=False)
    if completed.returncode:
        raise ValueError("GitHub candidate read failed")
    if len(completed.stdout) > MAX_ARCHIVE_BYTES:
        raise ValueError("GitHub candidate response is too large")
    return completed.stdout if binary else json.loads(completed.stdout)


def verify(run_id, sha, version, expected_hash):
    if not re.fullmatch(r"[1-9][0-9]*", run_id) or not re.fullmatch(r"[0-9a-f]{40}", sha):
        raise ValueError("Invalid candidate run ID or commit")
    prefix = "repos/" + REPOSITORY
    workflow = gh_api(prefix + "/actions/workflows/release-candidate.yml")
    run = gh_api(prefix + "/actions/runs/" + run_id)
    verify_run(run, workflow["id"], sha)
    listing = gh_api(prefix + "/actions/runs/" + run_id + "/artifacts?per_page=100")
    if listing.get("total_count") != len(listing.get("artifacts", [])):
        raise ValueError("Candidate artifact list is incomplete")
    artifact = select_artifact(listing["artifacts"], sha)
    data = gh_api(prefix + "/actions/artifacts/" + str(artifact["id"]) + "/zip", binary=True)
    verify_archive(data, sha, version, expected_hash, int(run_id))
    # Reruns invalidate a receipt being verified while the run is restarted.
    after = gh_api(prefix + "/actions/runs/" + run_id)
    verify_run(after, workflow["id"], sha)
    if after.get("run_attempt") != run.get("run_attempt"):
        raise ValueError("Candidate run changed during verification")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--package-sha256", required=True)
    args = parser.parse_args()
    try:
        verify(args.run_id, args.commit, args.version, args.package_sha256)
    except Exception:
        # Exception messages can include redirected signed URLs or archive inputs.
        print("Release blocked: candidate run, receipt or package verification failed.", file=sys.stderr)
        raise SystemExit(1)
    print("Authentic candidate run and approved package SHA-256 verified (host checks only).")
