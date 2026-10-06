#!/usr/bin/env python3
"""Prepare one reviewed portable preview in a new directory; never start Docker."""
import argparse
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import sys
import tempfile
import urllib.parse
import urllib.request
import zipfile

VERSION = "runtime-preview-36863178102"
SHA256 = "68f7181b4c50b08c632a482efd9f0a20be3ab3a2ad1de97fe6281a5e320b6a91"
IMAGE = "ghcr.io/transcendent-software-llc/openjibo-runtime@sha256:08b3e27362f47373696158ec619d9ac21e44e7bcf5995bce049b6dbac70d970b"
URL = f"https://github.com/transcendentsoftware-jd/JiboExperiments/releases/download/{VERSION}/starter-portable-preview.zip"
LIMIT = 2 * 1024 * 1024


def trusted_url(url):
    parsed = urllib.parse.urlsplit(url)
    if (parsed.scheme != "https" or parsed.hostname not in
            {"github.com", "release-assets.githubusercontent.com"}
            or parsed.username or parsed.password or parsed.port not in (None, 443)):
        raise ValueError("Unapproved download URL")
    return url


class ReleaseRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        trusted_url(newurl)
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def read_bounded(stream):
    data = stream.read(LIMIT + 1)
    if not data or len(data) > LIMIT:
        raise ValueError("Empty or oversized bundle")
    return data


def download():
    opener = urllib.request.build_opener(ReleaseRedirect())
    request = urllib.request.Request(trusted_url(URL), headers={"User-Agent": "OpenJibo-preview-preparer"})
    with opener.open(request, timeout=20) as response:
        trusted_url(response.geturl())
        return read_bounded(response)


def prepare(data, destination):
    # Hash immutable in-memory bytes before parsing or creating the destination.
    if hashlib.sha256(data).hexdigest() != SHA256:
        raise ValueError("Published bundle checksum mismatch")
    destination = Path(destination).absolute()
    if destination.exists() or destination.is_symlink():
        raise ValueError("Destination already exists; upgrades are not supported")
    if not destination.parent.is_dir() or destination.parent.is_symlink():
        raise ValueError("Destination needs an existing, non-symlink parent")
    for parent in destination.parents:
        if parent.is_symlink():
            raise ValueError("Symlink destination ancestor is not permitted")
    verifier_path = Path(__file__).with_name("verify-openjibo-starter-bundle.py")
    spec = importlib.util.spec_from_file_location("published_starter_verifier", verifier_path)
    verifier = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(verifier)
    with tempfile.TemporaryDirectory() as temp:
        snapshot = Path(temp) / "starter.zip"
        snapshot.write_bytes(data)
        report = verifier.verify_bundle(snapshot, SHA256)
    if report["runtime_image"] != IMAGE or report["declared_cpu_profile"] != "portable":
        raise ValueError("Verified package differs from the reviewed release identity")
    # The existing verifier rejects traversal, duplicate names, links and extra files.
    # Extract those same verified bytes; refuse overwrite even after validation.
    destination.mkdir(mode=0o700)
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        for info in archive.infolist():
            target = destination / info.filename
            # PostgreSQL's unprivileged user needs the bind-mounted init subtree.
            target.parent.mkdir(parents=True, exist_ok=True, mode=0o755)
            for parent in target.parents:
                if parent == destination:
                    break
                parent.chmod(0o755)
            with target.open("xb") as output:
                output.write(archive.read(info))
            target.chmod(0o755 if info.filename.endswith(".sh") else 0o600)
    return {"prepared": True, "version": VERSION, "cpu_profile": "portable",
            "archive_sha256": SHA256, "runtime_image": IMAGE,
            "destination": str(destination), "docker_started": False,
            "scope": "Fresh-directory preparation only; inspect README and invoke scripts with Bash/PowerShell. No secrets initialized or data migrated."}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--channel", required=True, choices=["preview"])
    parser.add_argument("--version", required=True, choices=[VERSION])
    parser.add_argument("--destination", required=True, type=Path)
    parser.add_argument("--bundle", type=Path, help="Optional local copy of this exact release ZIP")
    parser.add_argument("--plan", action="store_true", help="Print pinned identity without downloading or writing")
    args = parser.parse_args()
    if args.plan:
        print(json.dumps({"version": VERSION, "url": URL, "archive_sha256": SHA256,
                          "runtime_image": IMAGE, "cpu_profile": "portable"}, sort_keys=True))
        return 0
    try:
        if args.destination.exists() or args.destination.is_symlink():
            raise ValueError("Destination already exists; upgrades are not supported")
        if args.bundle:
            with args.bundle.open("rb") as stream:
                data = read_bounded(stream)
        else:
            data = download()
        print(json.dumps(prepare(data, args.destination), sort_keys=True))
        return 0
    except (OSError, ValueError, zipfile.BadZipFile) as error:
        # Do not print redirect URLs, query tokens, secrets or partial archive data.
        print(f"Preview preparation failed ({type(error).__name__}); verify the release, network and unused destination. Any partial directory is retained for inspection.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
