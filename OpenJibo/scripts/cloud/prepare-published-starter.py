#!/usr/bin/env python3
"""Prepare an explicitly selected reviewed preview; never start Docker."""
import argparse
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import sys
import subprocess
import tempfile
import urllib.parse
import urllib.request
import zipfile

VERSION = "runtime-preview-36863178102"
SOURCE_COMMIT = "ef8014611bb85139701136b32eb440ab0f253617"
SHA256 = "68f7181b4c50b08c632a482efd9f0a20be3ab3a2ad1de97fe6281a5e320b6a91"
IMAGE = "ghcr.io/transcendent-software-llc/openjibo-runtime@sha256:08b3e27362f47373696158ec619d9ac21e44e7bcf5995bce049b6dbac70d970b"
URL = f"https://github.com/transcendentsoftware-jd/JiboExperiments/releases/download/{VERSION}/starter-portable-preview.zip"
LIMIT = 2 * 1024 * 1024
SIGNED_VERSION = "runtime-preview-37540893708"
SIGNED_SHA256 = "5e38ef4e26920db0909f407ef7b53ac49ecd8d63f33dc9f631ca32f10ac66210"
SIGNED_IMAGE = "ghcr.io/transcendent-software-llc/openjibo-runtime@sha256:6bb69fd68d2c4863feaf17fd90319125ed1534f8cf728a4070ce04ee116c6010"
SIGNED_SOURCE_COMMIT = "fd32678bb29ade975c71a9bcd7dc7a89babaf541"
AUDIO_VERSION = "runtime-preview-38139481435"
AUDIO_SHA256 = "42e967f6863b3f6a6ddd0cf7756f7ac840fb14f7326e928c0e9a73028a1c1b01"
AUDIO_IMAGE = "ghcr.io/transcendent-software-llc/openjibo-runtime@sha256:e1b2aa06fc1f69bd498c5394388636331ba646abf2f39ca66ddcb6c0ee2697a9"
AUDIO_SOURCE_COMMIT = "0b7b56345177156a7da830cfc22c478b5f2b9c80"
AUDIO_AVX2_SHA256 = "4fba38e4528d484238661a5c82150f1dd13594a9d40fa894203727edeb46192d"
AUDIO_AVX2_IMAGE = "ghcr.io/transcendent-software-llc/openjibo-runtime@sha256:5e051559692710205532ec27866d4eb5810e9e69685bfa8c6d3676948f7afb7c"
GUARDED_VERSION = "runtime-preview-38165346370"
GUARDED_SOURCE_COMMIT = "b9dd0de89dfdb7bea558c2ae0923eefd50758b52"
GUARDED_SHA256 = "e8a63cc2ebe27be0499dc6ec5e235382b0063fcd26fc8caeb9e536adc5414cb8"
GUARDED_IMAGE = "ghcr.io/transcendent-software-llc/openjibo-runtime@sha256:f8f475c3ca78fb89e2a78b18e989e39dfb4e51dbe70c323bf3a4f7895eb98468"
GUARDED_AVX2_SHA256 = "660b599a89ac71056699cbf66a1ac76f492c3b3d662702e8f5a3a1ab31daa569"
GUARDED_AVX2_IMAGE = "ghcr.io/transcendent-software-llc/openjibo-runtime@sha256:e40b84cce0428fd75d2c2c938af587286253bee56caecd098b0d3e127c578881"
DURABLE_VERSION = "runtime-preview-41729015780"
DURABLE_SOURCE_COMMIT = "ac0b30b11025944cb0e88d4a3437c39f07307a8d"
DURABLE_SHA256 = "66febd2c39e0e4ec9c923c086eaf6cfee064020f3a662033d27c8119ae972709"
DURABLE_IMAGE = "ghcr.io/transcendent-software-llc/openjibo-runtime@sha256:be8d1d08825de31f910c7dab78d2f3c779a5b2f8735f6bed83298173a8d12169"
DURABLE_AVX2_SHA256 = "b8a0ece11ecdd393af0c7522dcf10e5daf9a4e9a4d4fb9dfd3922ec5bd78be7f"
DURABLE_AVX2_IMAGE = "ghcr.io/transcendent-software-llc/openjibo-runtime@sha256:964fc94249f91f9d78dbf6dba2e8bb8c7740de1019772a70e1f01acdec9cf24f"

PORTAL_VERSION = "runtime-preview-41757874916"
PORTAL_SOURCE_COMMIT = "2a0cc6fdf718ffcb60399770ce9d4118e54720f8"
PORTAL_SHA256 = "a23c0b1f19619e29dc8e669a0ff3dbd9591fb2e85e466163c64cbac646ca5816"
PORTAL_IMAGE = "ghcr.io/transcendent-software-llc/openjibo-runtime@sha256:7a6b86f60204c18e97fe870c68f26cb94c52a9c143738b840b937d0fc5956758"
PORTAL_AVX2_SHA256 = "9baf78173db0d1c98adc255a932454aedf1eac10bc03a8be9e8bf493c55c3e65"
PORTAL_AVX2_IMAGE = "ghcr.io/transcendent-software-llc/openjibo-runtime@sha256:c33cc7c29ff23366fd1cb75d34f86145783246a4911509207f143225198dfc8a"


class ProvenancePrerequisiteError(ValueError):
    pass


def check_provenance_cli():
    try:
        result = subprocess.run(["gh", "attestation", "verify", "--help"],
                                capture_output=True, text=True, timeout=15, check=False)
    except (OSError, subprocess.TimeoutExpired) as error:
        raise ProvenancePrerequisiteError("GitHub CLI is missing or unavailable") from error
    flags = ("--source-digest", "--signer-workflow", "--deny-self-hosted-runners",
             "--cert-oidc-issuer", "--predicate-type", "--format", "--bundle", "--repo")
    if result.returncode or not all(flag in result.stdout for flag in flags):
        raise ProvenancePrerequisiteError("GitHub CLI lacks required attestation verification support")


def release_identity(version, cpu_profile="portable"):
    if cpu_profile not in ("portable", "avx2"):
        raise ValueError("Unreviewed CPU profile")
    if version == PORTAL_VERSION:
        avx2 = cpu_profile == "avx2"
        return dict(version=version, archive_sha256=PORTAL_AVX2_SHA256 if avx2 else PORTAL_SHA256,
                    runtime_image=PORTAL_AVX2_IMAGE if avx2 else PORTAL_IMAGE,
                    source_commit=PORTAL_SOURCE_COMMIT, release_zip_attested=True,
                    url=f"https://github.com/transcendentsoftware-jd/JiboExperiments/releases/download/{version}/starter-{cpu_profile}-preview.zip")
    if version == DURABLE_VERSION:
        avx2 = cpu_profile == "avx2"
        return dict(version=version, archive_sha256=DURABLE_AVX2_SHA256 if avx2 else DURABLE_SHA256,
                    runtime_image=DURABLE_AVX2_IMAGE if avx2 else DURABLE_IMAGE,
                    source_commit=DURABLE_SOURCE_COMMIT, release_zip_attested=True,
                    url=f"https://github.com/transcendentsoftware-jd/JiboExperiments/releases/download/{version}/starter-{cpu_profile}-preview.zip")
    if version == GUARDED_VERSION:
        avx2 = cpu_profile == "avx2"
        return dict(version=version, archive_sha256=GUARDED_AVX2_SHA256 if avx2 else GUARDED_SHA256,
                    runtime_image=GUARDED_AVX2_IMAGE if avx2 else GUARDED_IMAGE,
                    source_commit=GUARDED_SOURCE_COMMIT, release_zip_attested=True,
                    url=f"https://github.com/transcendentsoftware-jd/JiboExperiments/releases/download/{version}/starter-{cpu_profile}-preview.zip")
    if cpu_profile == "avx2":
        if version != AUDIO_VERSION:
            raise ValueError("AVX2 preparation is pinned only for the audio preview")
        return dict(version=version, archive_sha256=AUDIO_AVX2_SHA256, runtime_image=AUDIO_AVX2_IMAGE,
                    source_commit=AUDIO_SOURCE_COMMIT, release_zip_attested=True,
                    url=f"https://github.com/transcendentsoftware-jd/JiboExperiments/releases/download/{version}/starter-avx2-preview.zip")
    if version == AUDIO_VERSION:
        return dict(version=version, archive_sha256=AUDIO_SHA256, runtime_image=AUDIO_IMAGE,
                    source_commit=AUDIO_SOURCE_COMMIT, release_zip_attested=True,
                    url=f"https://github.com/transcendentsoftware-jd/JiboExperiments/releases/download/{version}/starter-portable-preview.zip")
    if version == VERSION:
        return dict(version=version, archive_sha256=SHA256, runtime_image=IMAGE,
                    source_commit=SOURCE_COMMIT, release_zip_attested=False, url=URL)
    if version == SIGNED_VERSION:
        return dict(version=version, archive_sha256=SIGNED_SHA256, runtime_image=SIGNED_IMAGE,
                    source_commit=SIGNED_SOURCE_COMMIT, release_zip_attested=True,
                    url=f"https://github.com/transcendentsoftware-jd/JiboExperiments/releases/download/{version}/starter-portable-preview.zip")
    raise ValueError("Unreviewed release version")


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


def download(version=VERSION, cpu_profile="portable"):
    opener = urllib.request.build_opener(ReleaseRedirect())
    request = urllib.request.Request(trusted_url(release_identity(version, cpu_profile)["url"]), headers={"User-Agent": "OpenJibo-preview-preparer"})
    with opener.open(request, timeout=20) as response:
        trusted_url(response.geturl())
        return read_bounded(response)


def verify_provenance(data, attestation=None, version=VERSION, cpu_profile="portable"):
    identity = release_identity(version, cpu_profile)
    spec = importlib.util.spec_from_file_location("published_provenance_verifier", Path(__file__).with_name("verify-starter-provenance.py"))
    verifier = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(verifier)
    with tempfile.TemporaryDirectory() as temp:
        snapshot = Path(temp) / "starter.zip"
        snapshot.write_bytes(data)
        report = verifier.verify(snapshot, identity["archive_sha256"], identity["source_commit"], attestation)
    if report.get("provenance_verified") is not True:
        raise ValueError("Signed release verification did not succeed")


def prepare(data, destination, require_provenance=False, attestation=None, version=VERSION, cpu_profile="portable"):
    identity = release_identity(version, cpu_profile)
    # A signed release can never opt down to the legacy checksum-only policy.
    require_provenance = require_provenance or identity["release_zip_attested"]
    if attestation is not None and not require_provenance:
        raise ValueError("Attestation input requires signed verification")
    # Hash immutable in-memory bytes before parsing or creating the destination.
    if hashlib.sha256(data).hexdigest() != identity["archive_sha256"]:
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
        report = verifier.verify_bundle(snapshot, identity["archive_sha256"])
    if report["runtime_image"] != identity["runtime_image"] or report["declared_cpu_profile"] != cpu_profile:
        raise ValueError("Verified package differs from the reviewed release identity")
    if require_provenance:
        if cpu_profile == "portable":
            verify_provenance(data, attestation, version)
        else:
            verify_provenance(data, attestation, version, cpu_profile)
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
    return {"prepared": True, "version": version, "cpu_profile": cpu_profile,
            "archive_sha256": identity["archive_sha256"], "runtime_image": identity["runtime_image"],
            "destination": str(destination), "docker_started": False,
            "provenance_verified": require_provenance,
            "verification_policy": "signed-provenance" if require_provenance else "reviewed-preview-checksum-only",
            "scope": "Fresh-directory preparation only; inspect README and invoke scripts with Bash/PowerShell. No secrets initialized or data migrated."}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--channel", required=True, choices=["preview"])
    parser.add_argument("--version", required=True, choices=[VERSION, SIGNED_VERSION, AUDIO_VERSION, GUARDED_VERSION, DURABLE_VERSION, PORTAL_VERSION])
    parser.add_argument("--cpu-profile", choices=["portable", "avx2"], default="portable",
                        help="Explicit profile; AVX2 daemon-host compatibility is checked separately at launch")
    parser.add_argument("--destination", required=True, type=Path)
    parser.add_argument("--bundle", type=Path, help="Optional local copy of this exact release ZIP")
    parser.add_argument("--plan", action="store_true", help="Print pinned identity without downloading or writing")
    parser.add_argument("--require-provenance", action="store_true", help="Require provenance for legacy preview too; the new signed preview always requires it")
    parser.add_argument("--attestation", type=Path, help="Optional local signature bundle; signed verification must be selected")
    args = parser.parse_args()
    try:
        identity = release_identity(args.version, args.cpu_profile)
    except ValueError as error:
        parser.error(str(error))
    required = args.require_provenance or identity["release_zip_attested"]
    if args.attestation is not None and not required:
        parser.error("--attestation requires --require-provenance")
    if args.plan:
        print(json.dumps(dict(identity, cpu_profile=args.cpu_profile, provenance_required=required), sort_keys=True))
        return 0
    try:
        if args.destination.exists() or args.destination.is_symlink():
            raise ValueError("Destination already exists; upgrades are not supported")
        if required:
            check_provenance_cli()
        if args.bundle:
            with args.bundle.open("rb") as stream:
                data = read_bounded(stream)
        else:
            data = download(args.version, args.cpu_profile)
        print(json.dumps(prepare(data, args.destination, required, args.attestation, args.version, args.cpu_profile), sort_keys=True))
        return 0
    except ProvenancePrerequisiteError:
        print("Preview preparation stopped: GitHub CLI is missing, outdated or unavailable. Upgrade gh from its official repository and confirm gh attestation verify --help succeeds. No download or extraction was attempted.", file=sys.stderr)
        return 1
    except (OSError, ValueError, RuntimeError, zipfile.BadZipFile, subprocess.TimeoutExpired) as error:
        # Do not print redirect URLs, query tokens, secrets or partial archive data.
        print(f"Preview preparation failed ({type(error).__name__}); verify the release, network and unused destination. Any partial directory is retained for inspection.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
