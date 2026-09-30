#!/usr/bin/env python3
"""Verify a starter ZIP against a SHA-256 obtained independently of the ZIP."""

import argparse
import hashlib
import io
import json
from pathlib import Path
import re
import stat
import sys
import zipfile


MAX_ARCHIVE_BYTES = 2 * 1024 * 1024
MAX_MEMBER_BYTES = 512 * 1024
MAX_MEMBERS = 32
MEMBERS = frozenset({
    "docker-compose.yml", ".env.example", "README.md", "LICENSE",
    "start.sh", "Start.ps1", "MANIFEST.json",
    "scripts/cloud/Initialize-OpenJiboComposeEnv.ps1",
    "scripts/cloud/initialize-openjibo-compose-env.sh",
    "scripts/cloud/Invoke-OpenJiboSelfHostedStack.ps1",
    "scripts/cloud/invoke-openjibo-self-hosted-stack.sh",
    "scripts/cloud/postgres-init/01-create-databases.sh",
})
PROFILE_MEMBERS = MEMBERS | {"CPU-PROFILE.json"}
HEX_SHA256 = re.compile(r"[0-9a-f]{64}\Z")
IMAGE = re.compile(
    r"(?:localhost(?::[0-9]+)?|[a-z0-9]+(?:[.-][a-z0-9]+)*(?::[0-9]+)?)"
    r"(?:/[a-z0-9]+(?:[._-][a-z0-9]+)*)+@sha256:[0-9a-f]{64}\Z"
)


def _unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"Duplicate manifest key: {key}")
        result[key] = value
    return result


def _reject_constant(value):
    raise ValueError(f"Invalid JSON constant: {value}")


def _read_bounded_member(archive, info):
    if info.file_size > MAX_MEMBER_BYTES or info.compress_size > MAX_MEMBER_BYTES:
        raise ValueError(f"Oversized member: {info.filename}")
    with archive.open(info, "r") as stream:
        data = stream.read(MAX_MEMBER_BYTES + 1)
        if len(data) > MAX_MEMBER_BYTES or stream.read(1):
            raise ValueError(f"Oversized member: {info.filename}")
    if len(data) != info.file_size:
        raise ValueError(f"Member size mismatch: {info.filename}")
    return data


def verify_bundle(path: Path, expected_sha256: str) -> dict:
    """Return a report only after all checks pass; never extract or execute."""
    if not HEX_SHA256.fullmatch(expected_sha256):
        raise ValueError("--expected-sha256 must be 64 lowercase hex characters")
    with path.open("rb") as stream:
        snapshot = stream.read(MAX_ARCHIVE_BYTES + 1)
        if len(snapshot) > MAX_ARCHIVE_BYTES:
            raise ValueError("Archive exceeds 2 MiB limit")
    actual_sha256 = hashlib.sha256(snapshot).hexdigest()
    if actual_sha256 != expected_sha256:
        raise ValueError("Archive SHA-256 does not match independently supplied value")

    with zipfile.ZipFile(io.BytesIO(snapshot), "r") as archive:
        infos = archive.infolist()
        if len(infos) > MAX_MEMBERS:
            raise ValueError("Too many archive members")
        names = [info.filename for info in infos]
        if len(names) != len(set(names)):
            raise ValueError("Duplicate archive member")
        if set(names) not in (MEMBERS, PROFILE_MEMBERS):
            raise ValueError("Archive member set differs from the starter allowlist")
        if archive.comment:
            raise ValueError("Archive comment is not permitted")
        payload = {}
        for info in infos:
            name = info.filename
            # The exact allowlist above also excludes absolute paths and traversal.
            if (name.startswith(("/", "\\")) or "\\" in name
                    or any(part in ("", ".", "..") for part in name.split("/"))):
                raise ValueError(f"Unsafe archive path: {name}")
            if info.flag_bits & 1:
                raise ValueError(f"Encrypted member: {name}")
            if info.compress_type != zipfile.ZIP_STORED:
                raise ValueError(f"Compressed member: {name}")
            if info.orig_filename != name:
                raise ValueError(f"Truncated archive filename: {name}")
            mode = info.external_attr >> 16
            expected_mode = stat.S_IFREG | (0o755 if name.endswith(".sh") else 0o644)
            if info.create_system != 3 or mode != expected_mode:
                raise ValueError(f"Unexpected member type or permissions: {name}")
            if info.extra or info.comment:
                raise ValueError(f"Member metadata is not permitted: {name}")
            payload[name] = _read_bounded_member(archive, info)

    manifest_bytes = payload.pop("MANIFEST.json")
    try:
        manifest = json.loads(manifest_bytes.decode("utf-8"),
                              object_pairs_hook=_unique_object,
                              parse_constant=_reject_constant)
    except (UnicodeError, json.JSONDecodeError) as error:
        raise ValueError(f"Invalid MANIFEST.json: {error}") from error
    if not isinstance(manifest, dict) or set(manifest) != {"schema", "runtime_image", "sha256"}:
        raise ValueError("Invalid manifest schema keys")
    if type(manifest["schema"]) is not int or manifest["schema"] != 1:
        raise ValueError("Unsupported manifest schema")
    image = manifest["runtime_image"]
    if not isinstance(image, str) or not IMAGE.fullmatch(image):
        raise ValueError("Invalid manifest runtime image")
    checksums = manifest["sha256"]
    if not isinstance(checksums, dict) or set(checksums) != set(payload):
        raise ValueError("Manifest checksums do not cover exactly the payload")
    for name, data in payload.items():
        digest = checksums[name]
        if not isinstance(digest, str) or not HEX_SHA256.fullmatch(digest):
            raise ValueError(f"Invalid manifest checksum: {name}")
        if hashlib.sha256(data).hexdigest() != digest:
            raise ValueError(f"Checksum mismatch: {name}")

    cpu_profile = None
    if "CPU-PROFILE.json" in payload:
        profile = json.loads(payload["CPU-PROFILE.json"].decode("utf-8"), object_pairs_hook=_unique_object, parse_constant=_reject_constant)
        if not isinstance(profile, dict) or set(profile) != {"schema", "profile", "required_x86_flags", "scope"}:
            raise ValueError("Invalid CPU profile metadata")
        cpu_profile = profile["profile"]
        required = ["avx", "avx2", "bmi2", "f16c", "fma", "sse4_2"] if cpu_profile == "avx2" else []
        if type(profile["schema"]) is not int or profile["schema"] != 1 or cpu_profile not in ("portable", "avx2") or profile["required_x86_flags"] != required or not isinstance(profile["scope"], str):
            raise ValueError("Invalid CPU profile metadata")

    try:
        compose = payload["docker-compose.yml"].decode("utf-8")
        bash = payload["start.sh"].decode("utf-8")
        powershell = payload["Start.ps1"].decode("utf-8")
    except UnicodeError as error:
        raise ValueError(f"Invalid starter text encoding: {error}") from error
    compose_images = re.findall(r"^    image: (\S+)\s*$", compose, re.MULTILINE)
    if (compose_images != [image, image, "postgres:16-alpine", "curlimages/curl:8.10.1"]
            or re.search(r"^\s+build:\s*$", compose, re.MULTILINE)
            or '"127.0.0.1:8080:8080"' not in compose
            or '"5432:5432"' in compose):
        raise ValueError("Compose runtime image or starter configuration differs from manifest")
    if (bash.count(f"--image '{image}'") != 1
            or powershell.count(f"-Image '{image}'") != 1):
        raise ValueError("Entrypoint runtime image differs from manifest")
    return {
        "archive_sha256": actual_sha256,
        "archive_bytes": len(snapshot),
        "members": len(infos),
        "runtime_image": image,
        "declared_cpu_profile": cpu_profile,
        "verified_against_external_sha256": True,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--expected-sha256", required=True)
    args = parser.parse_args()
    try:
        report = verify_bundle(args.bundle, args.expected_sha256)
    except (OSError, ValueError, zipfile.BadZipFile, RuntimeError) as error:
        print(f"starter bundle verification failed: {error}", file=sys.stderr)
        return 1
    print(json.dumps(report, sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main())
