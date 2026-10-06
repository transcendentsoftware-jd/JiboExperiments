#!/usr/bin/env python3
"""Verify a starter ZIP and its publisher identity; no extraction or installation."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import zipfile

REPOSITORY = "transcendentsoftware-jd/JiboExperiments"
WORKFLOW = "github.com/" + REPOSITORY + "/.github/workflows/openjibo-runtime-preview-publish.yml"


def bounded_bytes(path, limit):
    with Path(path).open("rb") as stream:
        data = stream.read(limit + 1)
    if not data or len(data) > limit:
        raise ValueError("Empty or oversized verification input")
    return data


def verify(bundle, expected_sha256, source_commit, attestation=None):
    if not re.fullmatch(r"[0-9a-f]{40}", source_commit):
        raise ValueError("A trusted full lowercase source commit is required")
    if not re.fullmatch(r"[0-9a-f]{64}", expected_sha256):
        raise ValueError("An independently trusted archive checksum is required")
    spec = importlib.util.spec_from_file_location("provenance_bundle_verifier", Path(__file__).with_name("verify-openjibo-starter-bundle.py"))
    verifier = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(verifier)
    # Verify and attest the same immutable byte snapshot, not a user file that
    # could change between hash checking and the external cryptographic check.
    data = bounded_bytes(bundle, 2 * 1024 * 1024)
    proof = bounded_bytes(attestation, 4 * 1024 * 1024) if attestation else None
    with tempfile.TemporaryDirectory() as temp:
        snapshot = Path(temp) / "starter.zip"
        snapshot.write_bytes(data)
        report = verifier.verify_bundle(snapshot, expected_sha256)
        command = ["gh", "attestation", "verify", str(snapshot),
                   "--repo", REPOSITORY, "--signer-workflow", WORKFLOW,
                   "--source-digest", source_commit,
                   "--deny-self-hosted-runners",
                   "--cert-oidc-issuer", "https://token.actions.githubusercontent.com",
                   "--predicate-type", "https://slsa.dev/provenance/v1", "--format", "json"]
        if proof is not None:
            proof_path = Path(temp) / "attestation.json"
            proof_path.write_bytes(proof)
            command.extend(["--bundle", str(proof_path)])
        result = subprocess.run(command, capture_output=True, text=True, timeout=60, check=False,
                                env=dict(os.environ, GH_HOST="github.com"))
        if result.returncode or len(result.stdout) > 16 * 1024 * 1024:
            raise ValueError("Signed provenance verification failed")
        verified = json.loads(result.stdout)
        if not isinstance(verified, list) or not verified or not all(isinstance(item, dict) and item.get("verificationResult") for item in verified):
            raise ValueError("Missing successful signed verification results")
    return dict(report, provenance_verified=True, source_commit=source_commit,
                trusted_repository=REPOSITORY, trusted_workflow=WORKFLOW,
                scope="ZIP bytes and signed builder identity only; not stable-channel freshness, install certification, SBOM or license approval.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle", required=True, type=Path)
    parser.add_argument("--expected-sha256", required=True)
    parser.add_argument("--source-commit", required=True)
    parser.add_argument("--attestation", type=Path, help="Optional downloaded signature bundle; otherwise query GitHub")
    args = parser.parse_args()
    try:
        print(json.dumps(verify(args.bundle, args.expected_sha256, args.source_commit, args.attestation), sort_keys=True))
        return 0
    except (OSError, ValueError, RuntimeError, zipfile.BadZipFile, subprocess.TimeoutExpired):
        print("Starter provenance verification failed. Check the trusted checksum/source, signature availability, publisher identity and GitHub CLI access. No checksum-only fallback is permitted.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
