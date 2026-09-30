#!/usr/bin/env python3
"""Build a checkout-free, preview self-hosted starter archive (stdlib only)."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import sys
import tempfile
import zipfile


IMAGE_PATTERN = re.compile(
    r"(?:localhost(?::[0-9]+)?|[a-z0-9]+(?:[.-][a-z0-9]+)*(?::[0-9]+)?)"
    r"(?:/[a-z0-9]+(?:[._-][a-z0-9]+)*)+@sha256:[0-9a-f]{64}\Z"
)
SOURCES = (
    ("OpenJibo/docker-compose.yml", "docker-compose.yml"),
    ("OpenJibo/.env.example", ".env.example"),
    ("OpenJibo/scripts/cloud/Initialize-OpenJiboComposeEnv.ps1", "scripts/cloud/Initialize-OpenJiboComposeEnv.ps1"),
    ("OpenJibo/scripts/cloud/initialize-openjibo-compose-env.sh", "scripts/cloud/initialize-openjibo-compose-env.sh"),
    ("OpenJibo/scripts/cloud/Invoke-OpenJiboSelfHostedStack.ps1", "scripts/cloud/Invoke-OpenJiboSelfHostedStack.ps1"),
    ("OpenJibo/scripts/cloud/invoke-openjibo-self-hosted-stack.sh", "scripts/cloud/invoke-openjibo-self-hosted-stack.sh"),
    ("OpenJibo/scripts/cloud/postgres-init/01-create-databases.sh", "scripts/cloud/postgres-init/01-create-databases.sh"),
    ("LICENSE", "LICENSE"),
)
BUILD_BLOCK = (
    "    build:\n"
    "      context: .\n"
    "      dockerfile: Dockerfile\n"
    "      args:\n"
    "        ENABLE_LOCAL_WHISPER: ${OPENJIBO_ENABLE_LOCAL_WHISPER:-true}\n"
    "        WHISPER_MODEL: ${OPENJIBO_WHISPER_MODEL:-base.en}\n"
)
IMAGE_LINE = "    image: ${OPENJIBO_RUNTIME_IMAGE:-openjibo-cloud:self-hosted}\n"
POSTGRES_PORTS = '    ports:\n      - "5432:5432"\n'
API_PORTS = '    ports:\n      - "8080:8080"\n'
# Reviewed source Compose contract. Any change needs a packaging review.
COMPOSE_SHA256 = "b5e4e4767549ddc025db4ebbd2b348011271f06c505f1913f75e1665ac525445"


def _source_bytes(root: Path, relative: str) -> bytes:
    """Read only an explicitly listed regular file, rejecting symlink components."""
    current = root
    for component in Path(relative).parts:
        current = current / component
        mode = current.lstat().st_mode
        if stat.S_ISLNK(mode):
            raise ValueError(f"Symlink source is not permitted: {relative}")
    if not stat.S_ISREG(current.stat().st_mode):
        raise ValueError(f"Source is not a regular file: {relative}")
    return current.read_bytes()


def _text(data: bytes) -> str:
    return data.decode("utf-8").replace("\r\n", "\n").replace("\r", "\n")


def transform_compose(compose: str, image: str) -> str:
    if not IMAGE_PATTERN.fullmatch(image):
        raise ValueError("--image must be a lowercase digest-pinned registry/repository reference")
    if hashlib.sha256(compose.encode("utf-8")).hexdigest() != COMPOSE_SHA256:
        raise ValueError("Source Compose changed; review and update starter bundle contract")
    if not compose.endswith("\n"):
        raise ValueError("Unexpected Compose structure: missing final newline")
    # The transformation is intentionally tied to the reviewed Compose contract.
    if not compose.startswith("services:\n") or compose.count("\nvolumes:\n") != 1:
        raise ValueError("Unexpected Compose top-level structure")
    headings = re.findall(r"^  ([a-z][a-z0-9-]*):$", compose, flags=re.MULTILINE)
    if headings != ["migrate", "api", "postgres", "smoke", "api-data", "postgres-data"]:
        raise ValueError("Unexpected Compose service/volume structure")
    if compose.count(BUILD_BLOCK) != 2 or compose.count(IMAGE_LINE) != 2:
        raise ValueError("Unexpected Compose build/image structure")
    if compose.count(POSTGRES_PORTS) != 1 or compose.count(API_PORTS) != 1:
        raise ValueError("Unexpected Compose ports structure")
    # Confirm the blocks are in their expected services before modifying them.
    service = dict(re.findall(
        r"^  (migrate|api|postgres|smoke):\n(.*?)(?=^  [a-z][a-z0-9-]*:\n|^volumes:\n)",
        compose, flags=re.MULTILINE | re.DOTALL,
    ))
    if len(service) != 4 or any(BUILD_BLOCK[4:] not in service[name] for name in ("migrate", "api")):
        raise ValueError("Unexpected Compose service build placement")
    if POSTGRES_PORTS[4:] not in service["postgres"]:
        raise ValueError("Unexpected PostgreSQL port placement")
    if API_PORTS[4:] not in service["api"]:
        raise ValueError("Unexpected API port placement")
    if compose.count("${OPENJIBO_POSTGRES_PASSWORD}") != 3:
        raise ValueError("Unexpected PostgreSQL password structure")
    rendered = compose.replace(BUILD_BLOCK, "").replace(IMAGE_LINE, f"    image: {image}\n")
    rendered = rendered.replace(POSTGRES_PORTS, "")
    rendered = rendered.replace(API_PORTS, '    ports:\n      - "127.0.0.1:8080:8080"\n')
    rendered = rendered.replace("${OPENJIBO_POSTGRES_PASSWORD}", "${OPENJIBO_POSTGRES_PASSWORD:?Set OPENJIBO_POSTGRES_PASSWORD in .env}")
    if re.search(r"^\s+build:\s*$", rendered, flags=re.MULTILINE):
        raise ValueError("Unexpected remaining Compose build directive")
    return rendered


def _readme(image: str) -> str:
    return f"""# OpenJibo self-hosted starter preview

This is a fresh-install preview bundle. It is not an official signed release,
verified image, updater, or upgrade/restore procedure. The OpenJibo runtime
image is pinned to `{image}`. A digest identifies bytes but does not prove
provenance. Verify the image source, architecture, and included speech model
before using it. PostgreSQL (`postgres:16-alpine`) and the optional smoke image
(`curlimages/curl:8.10.1`) remain tag-based dependencies. The archive itself
contains no container images and Docker will need network access to pull them.
The included MIT license covers this project's files; third-party image and
dependency license notices and an SBOM have not been certified for this preview.

Prerequisites: Docker with Compose v2; Bash plus OpenSSL for the Bash path, or
PowerShell for the Windows path. Extract the archive to a new directory.

1. Run `./scripts/cloud/initialize-openjibo-compose-env.sh` (Bash) or
   `.\\scripts\\cloud\\Initialize-OpenJiboComposeEnv.ps1` (PowerShell) from the
   extracted directory. This creates `.env` with fresh encryption keys. Do not
   hand-copy `.env.example` to `.env`: its encryption values are samples, and
   the initializer preserves an existing `.env` rather than replacing them.
2. Edit `.env` and set `OPENJIBO_POSTGRES_PASSWORD` to a strong, unique password.
   Preserve `.env` securely: changing its encryption keys later can make stored
   data unreadable. Match the prebuilt image's Whisper/model variant to the
   runtime settings you select.
3. Start with the reviewed image explicitly using the bundled entrypoint:

   Bash: `./start.sh`

   PowerShell: `.\\Start.ps1`

The entrypoints call the included launchers with the reviewed image digest.
The API binds only to 127.0.0.1:8080 on the host, and PostgreSQL has no host
port mapping. For a physical robot on your LAN, deliberately change the API
port binding and firewall it for trusted clients; never expose this HTTP
preview publicly. The launcher checks Compose configuration, pulls a missing
pinned image, and applies migrations before the API starts. Do not use this
preview bundle on an existing installation: existing volumes and `.env` require
a release-specific backup, migration, and restore plan. PostgreSQL initialization
only acts on a fresh database volume.
Never use `docker compose down -v` on data you wish to retain.

`MANIFEST.json` records SHA-256 values for every other archive member. It helps
check archive integrity; it is not a signature or proof of image authenticity.
"""


def build_payload(root: Path, image: str, cpu_profile: str = "portable") -> dict[str, bytes]:
    if cpu_profile not in ("portable", "avx2"):
        raise ValueError("CPU profile must be portable or avx2")
    if not IMAGE_PATTERN.fullmatch(image):
        raise ValueError("--image must be a lowercase digest-pinned registry/repository reference")
    if root.is_symlink():
        raise ValueError("Source root may not be a symlink")
    entries = {target: _source_bytes(root, source) for source, target in SOURCES}
    entries = {name: _text(data).encode("utf-8") for name, data in entries.items()}
    entries["docker-compose.yml"] = transform_compose(_text(entries["docker-compose.yml"]), image).encode("utf-8")
    entries["README.md"] = _readme(image).encode("utf-8")
    entries["CPU-PROFILE.json"] = (json.dumps({
        "schema": 1, "profile": cpu_profile,
        "required_x86_flags": ["avx", "avx2", "bmi2", "f16c", "fma", "sse4_2"] if cpu_profile == "avx2" else [],
        "scope": "Publisher-declared CPU profile; verify against image build evidence and the actual Docker runtime host.",
    }, sort_keys=True, indent=2) + "\n").encode("utf-8")
    entries["README.md"] += (f"\nCPU profile: `{cpu_profile}`. See `CPU-PROFILE.json`.\n"
        "An AVX2 image requires x86_64 with AVX, AVX2, BMI2, F16C, FMA and SSE4.2\n"
        "on the Docker runtime host. Do not infer capabilities from a remote client.\n"
        "Profile metadata is a publisher declaration, not image verification.\n").encode("utf-8")
    entries["start.sh"] = (
        '#!/usr/bin/env bash\nset -euo pipefail\n'
        'script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"\n'
        f'exec bash "$script_dir/scripts/cloud/invoke-openjibo-self-hosted-stack.sh" --run-migration --image \'{image}\'\n'
    ).encode("utf-8")
    entries["Start.ps1"] = (
        '$ErrorActionPreference = "Stop"\n'
        f'& (Join-Path $PSScriptRoot "scripts/cloud/Invoke-OpenJiboSelfHostedStack.ps1") -RunMigration -Image \'{image}\'\n'
        'if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }\n'
    ).encode("utf-8")
    checksums = {name: hashlib.sha256(data).hexdigest() for name, data in sorted(entries.items())}
    manifest = {"schema": 1, "runtime_image": image, "sha256": checksums}
    entries["MANIFEST.json"] = (json.dumps(manifest, sort_keys=True, indent=2) + "\n").encode("utf-8")
    return entries


def write_bundle(root: Path, image: str, output: Path, cpu_profile: str = "portable") -> None:
    if output.suffix.lower() != ".zip" or output.name.lower() in {".env", ".env.example"}:
        raise ValueError("--output must name a .zip file")
    if output.exists() or output.is_symlink():
        raise FileExistsError(f"Output already exists: {output}")
    entries = build_payload(root, image, cpu_profile)
    output.parent.mkdir(parents=True, exist_ok=True)
    descriptor, staged = tempfile.mkstemp(prefix=".openjibo-starter-", suffix=".zip", dir=output.parent)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            with zipfile.ZipFile(stream, "w", compression=zipfile.ZIP_STORED) as archive:
                for name, data in sorted(entries.items()):
                    info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
                    info.compress_type = zipfile.ZIP_STORED
                    info.create_system = 3
                    permission = 0o755 if name.endswith(".sh") else 0o644
                    info.external_attr = (stat.S_IFREG | permission) << 16
                    archive.writestr(info, data)
            stream.flush()
            os.fsync(stream.fileno())
        # Hard-link installation is atomic and fails if another process created output.
        os.link(staged, output)
    finally:
        Path(staged).unlink(missing_ok=True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--image", required=True)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--cpu-profile", choices=("portable", "avx2"), default="portable")
    args = parser.parse_args()
    try:
        write_bundle(Path(__file__).resolve().parents[3], args.image, args.output, args.cpu_profile)
    except (OSError, UnicodeError, ValueError) as error:
        print(f"starter bundle: {error}", file=sys.stderr)
        return 1
    print(args.output)
    return 0


if __name__ == "__main__":
    sys.exit(main())
