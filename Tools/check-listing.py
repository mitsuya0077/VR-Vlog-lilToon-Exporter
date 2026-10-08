"""Verify the generated VPM listing and its current/stable downloadable packages."""
import argparse
import hashlib
import io
import json
import re
import urllib.request
import zipfile
from pathlib import Path
from urllib.parse import urlsplit

ROOT = Path(__file__).resolve().parents[1]
REPOSITORY = "mitsuya0077/VR-Vlog-lilToon-Exporter"
EXPORTER = "com.vrvlog.liltoon-vrm-exporter"
FIXTURE_BASE = "http://127.0.0.1:8765/listing-fixture/release"
MAX_ZIP_BYTES = 64 * 1024 * 1024


def require(condition, message):
    if not condition:
        raise ValueError(message)


def download(url):
    request = urllib.request.Request(url, headers={"User-Agent": "VRVlog-listing-check"})
    with urllib.request.urlopen(request, timeout=60) as response:
        data = response.read(MAX_ZIP_BYTES + 1)
    require(len(data) <= MAX_ZIP_BYTES, "Package ZIP exceeds the listing-check bound")
    return data


def zip_manifest(data):
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        manifests = [item for item in archive.infolist() if item.filename == "package.json"]
        require(len(manifests) == 1, "Package ZIP must contain one root package.json")
        require(manifests[0].file_size <= 1024 * 1024, "Package manifest is too large")
        return json.loads(archive.read(manifests[0]).decode("utf-8-sig"))


def validate(listing, package, source, compatibility, *, expected_version=None,
             preserve_version="0.11.11", fixture_base=None, read_zip=download):
    expected_version = expected_version or package["version"]
    require(package["name"] == EXPORTER, "Unexpected current package name")
    require(listing.get("id") == source["id"] and listing.get("url") == source["url"],
            "Generated listing identity differs from source.json")
    if fixture_base is not None:
        require(fixture_base == FIXTURE_BASE, "Only the explicit loopback fixture is allowed")
    versions = listing.get("packages", {})
    required = [(EXPORTER, expected_version), (EXPORTER, preserve_version)]
    dependency_version = compatibility["uniVrm"]["releaseVersion"]
    required += [(name, dependency_version) for name in sorted(package["vpmDependencies"])]
    checked = []
    for name, version in dict.fromkeys(required):
        entry = versions.get(name, {}).get("versions", {}).get(version)
        require(isinstance(entry, dict), f"Missing listed package: {name}@{version}")
        require(entry.get("name") == name and entry.get("version") == version,
                f"Listing version key and manifest disagree: {name}@{version}")
        digest = entry.get("zipSHA256", "")
        require(isinstance(digest, str) and re.fullmatch(r"[0-9a-f]{64}", digest),
                f"Missing or invalid ZIP SHA256: {name}@{version}")
        url = entry.get("url", "")
        require(isinstance(url, str), f"Invalid package URL: {name}@{version}")
        parsed = urlsplit(url)
        filename = f"{name}-{version}.zip"
        require(not parsed.query and not parsed.fragment and not parsed.username,
                f"Unexpected package URL components: {name}@{version}")
        if fixture_base is not None:
            require(url == f"{fixture_base}/{filename}", f"Unexpected fixture URL: {name}@{version}")
        else:
            prefix = f"/{REPOSITORY}/releases/download/"
            require(parsed.scheme == "https" and parsed.netloc == "github.com"
                    and parsed.path.startswith(prefix), f"Unexpected release URL: {name}@{version}")
            suffix = parsed.path[len(prefix):].split("/")
            require(len(suffix) == 2 and suffix[1] == filename,
                    f"Unexpected release asset: {name}@{version}")
            if name == EXPORTER:
                require(suffix[0] == f"v{version}", f"Exporter release tag differs: {version}")
        data = read_zip(url)
        require(hashlib.sha256(data).hexdigest() == digest, f"ZIP SHA256 mismatch: {name}@{version}")
        actual = zip_manifest(data)
        require(actual.get("name") == name and actual.get("version") == version,
                f"Downloaded package identity differs: {name}@{version}")
        if name == EXPORTER and version == expected_version:
            require(entry.get("vpmDependencies") == package["vpmDependencies"]
                    and actual.get("vpmDependencies") == package["vpmDependencies"],
                    "Current exporter dependency policy differs in listing or ZIP")
        checked.append({"name": name, "version": version, "url": url, "zipSHA256": digest})
    return {"status": "passed", "currentVersion": expected_version,
            "preservedStableVersion": preserve_version, "verifiedPackages": checked}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--listing", required=True, type=Path)
    parser.add_argument("--package", type=Path, default=ROOT / "package.json")
    parser.add_argument("--source", type=Path, default=ROOT / "source.json")
    parser.add_argument("--compatibility", type=Path, default=ROOT / "Compatibility/dependencies.json")
    parser.add_argument("--expected-version")
    parser.add_argument("--preserve-version", default="0.11.11")
    parser.add_argument("--fixture-base-url")
    args = parser.parse_args()
    read_json = lambda path: json.loads(path.read_text(encoding="utf-8-sig"))
    result = validate(read_json(args.listing), read_json(args.package), read_json(args.source),
                      read_json(args.compatibility), expected_version=args.expected_version,
                      preserve_version=args.preserve_version, fixture_base=args.fixture_base_url)
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
