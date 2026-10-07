#!/usr/bin/env python3
"""Check, then explicitly launch a fresh portable preview on native Linux only."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import platform
import re
import secrets
import shutil
import socket
import subprocess
import sys
import time
import urllib.request

MANIFEST_SHA256 = "d68cf6afee940d77e59b862da3566f94fbc163c6619f4c1f62f9b4870da3a02e"
IMAGE = "ghcr.io/transcendent-software-llc/openjibo-runtime@sha256:08b3e27362f47373696158ec619d9ac21e44e7bcf5995bce049b6dbac70d970b"
SIGNED_MANIFEST_SHA256 = "c9ceaedd1339721a7fa1a32b64990623633a2abc2617ed6242be973079f38b86"
SIGNED_IMAGE = "ghcr.io/transcendent-software-llc/openjibo-runtime@sha256:6bb69fd68d2c4863feaf17fd90319125ed1534f8cf728a4070ce04ee116c6010"


def manifest_identity(raw):
    approved = {
        MANIFEST_SHA256: ("runtime-preview-36863178102", IMAGE),
        SIGNED_MANIFEST_SHA256: ("runtime-preview-37540893708", SIGNED_IMAGE),
    }
    identity = approved.get(hashlib.sha256(raw).hexdigest())
    if identity is None:
        raise ValueError("Manifest does not match a reviewed published preview")
    return identity


def clean_environment():
    # Do not let caller variables select a different daemon, Compose file,
    # image, project, password, profile or secret value.
    return {key: value for key, value in os.environ.items()
            if not key.startswith(("COMPOSE_", "OPENJIBO_", "OpenJibo__", "DOCKER_"))}


def run(command, env, timeout=15):
    result = subprocess.run(command, env=env, capture_output=True, text=True,
                            timeout=timeout, check=False)
    if result.returncode:
        raise ValueError("Command failed; inspect Docker access or startup separately")
    return result.stdout.strip()


def checked_files(directory):
    directory = Path(directory).absolute()
    if not directory.is_dir() or any(p.is_symlink() for p in (directory, *directory.parents)):
        raise ValueError("Prepared directory must not contain symlink ancestors")
    manifest = directory / "MANIFEST.json"
    if manifest.is_symlink() or not manifest.is_file() or manifest.stat().st_size > 512 * 1024:
        raise ValueError("Invalid manifest file")
    raw = manifest.read_bytes()
    _, image = manifest_identity(raw)
    record = json.loads(raw)
    if record["runtime_image"] != image:
        raise ValueError("Unexpected image identity")
    expected = set(record["sha256"]) | {"MANIFEST.json"}
    found = set()
    for entry in directory.rglob("*"):
        if entry.is_symlink():
            raise ValueError("Symlink in prepared directory")
        if not entry.is_dir():
            if not entry.is_file():
                raise ValueError("Non-regular prepared file")
            found.add(entry.relative_to(directory).as_posix())
    if found != expected:
        raise ValueError("Fresh prepared directory required; existing environment or extra files refused")
    for name, digest in record["sha256"].items():
        path = directory / name
        if path.stat().st_size > 512 * 1024 or hashlib.sha256(path.read_bytes()).hexdigest() != digest:
            raise ValueError("Prepared file no longer matches the verified bundle")
    return directory


def parameters(project, port):
    if not re.fullmatch(r"openjibo-[a-z0-9][a-z0-9-]{0,47}", project):
        raise ValueError("Project must be a bounded lowercase openjibo-* name")
    if not 1024 <= port <= 65535:
        raise ValueError("Choose an unprivileged TCP port")


def collect_preflight(docker, env):
    spec = importlib.util.spec_from_file_location("launcher_preflight", Path(__file__).with_name("preflight-native-linux-starter.py"))
    preflight = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(preflight)

    def preflight_command(args):
        try:
            return run(docker + args[1:], env, timeout=8), None
        except (OSError, ValueError, subprocess.TimeoutExpired):
            return None, "command_failed"

    preflight._command = preflight_command
    return preflight.collect_report()


def check(directory, project, port, docker, env):
    parameters(project, port)
    directory = checked_files(directory)
    version, image = manifest_identity((directory / "MANIFEST.json").read_bytes())
    if platform.system() != "Linux" or platform.machine().lower() not in ("x86_64", "amd64"):
        raise ValueError("This launcher supports native Linux x86_64 only")
    for binary in ("docker", "bash", "openssl"):
        if not shutil.which(binary):
            raise ValueError("Required executable missing")
    report = collect_preflight(docker, env)
    if not report["preflight_passed"]:
        raise ValueError("Host preflight failed: " + ",".join(report["issues"]))
    server = report["docker_server"]
    if server["architecture"].lower() not in ("x86_64", "amd64"):
        raise ValueError("Docker daemon architecture is not x86_64")
    compose_version = re.fullmatch(r"v?(\d+)\.(\d+)\.(\d+)(?:[-+].*)?", report["compose_version"])
    if not compose_version or tuple(map(int, compose_version.groups())) < (2, 24, 4):
        raise ValueError("Docker Compose 2.24.4 or newer is required")
    if server["memory_total_bytes"] < 4 * 1024**3 or report["host"]["memory_available_bytes"] < 1024**3:
        raise ValueError("Need at least 4 GiB daemon RAM and 1 GiB available host RAM")
    if shutil.disk_usage(directory).free < 3 * 1024**3:
        raise ValueError("Need at least 3 GiB free in the prepared directory filesystem")
    for resource, args in (("containers", ["ps", "-aq"]), ("volumes", ["volume", "ls", "-q"]), ("networks", ["network", "ls", "-q"])):
        if run(docker + args + ["--filter", f"label=com.docker.compose.project={project}"], env):
            raise ValueError("Project already has " + resource)
    volumes = run(docker + ["volume", "ls", "--format", "{{.Name}}"], env).splitlines()
    networks = run(docker + ["network", "ls", "--format", "{{.Name}}"], env).splitlines()
    if any(name in volumes for name in (project + "_api-data", project + "_postgres-data")) or project + "_default" in networks:
        raise ValueError("Project resource names already exist, even without labels")
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", port))
    return directory, {"checked": True, "project": project, "port": port,
                       "version": version, "runtime_image": image, "docker_started": False,
                       "scope": "Prerequisites and current conflicts only; no capacity or physical-robot certification."}


class NoHealthRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise ValueError("Local health redirects are not permitted")


def wait_health(port):
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoHealthRedirect())
    for _ in range(30):
        try:
            with opener.open(f"http://127.0.0.1:{port}/health", timeout=2) as response:
                health = json.loads(response.read(4096))
            if isinstance(health, dict) and health.get("ok") is True:
                return
        except (OSError, ValueError):
            pass
        time.sleep(2)
    raise ValueError("Health deadline exceeded; retain files and volumes")


def start(directory, project, port, docker, env):
    # Recheck immediately before any mutation. Failed attempts are retained and
    # cannot be silently reinitialized or upgraded by this fresh-only launcher.
    directory, report = check(directory, project, port, docker, env)
    lock = directory / ".preview-launch.lock"
    with lock.open("x") as stream:
        stream.write("Fresh launch attempted; preserve environment and volumes.\n")
    init_env = dict(env, OPENJIBO_POSTGRES_PASSWORD=secrets.token_hex(32))
    run(["bash", str(directory / "scripts/cloud/initialize-openjibo-compose-env.sh")], init_env)
    (directory / ".env").chmod(0o600)
    override = directory / "preview-launch.compose.yaml"
    with override.open("x") as stream:
        stream.write(f'services:\n  api:\n    ports: !override\n      - "127.0.0.1:{port}:8080"\n    environment:\n      OpenJibo__Stt__EnableAzureSpeech: "false"\n      OpenJibo__Stt__WhisperThreads: "4"\n')
    compose = docker + ["compose", "--project-directory", str(directory),
                        "--env-file", str(directory / ".env"), "-p", project,
                        "-f", str(directory / "docker-compose.yml"), "-f", str(override)]
    run(compose + ["config", "--quiet"], env)
    run(compose + ["up", "-d", "--no-build", "postgres", "migrate", "api"], env, timeout=900)
    wait_health(port)
    return dict(report, docker_started=True, health_passed=True,
                scope="Health passed; socket/audio acceptance still required. Preserve the private .env and all volumes.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", required=True, type=Path)
    parser.add_argument("--project", required=True)
    parser.add_argument("--port", required=True, type=int)
    parser.add_argument("--sudo-docker", action="store_true", help="Use preauthorized sudo -n for Docker only; do not run Python as root")
    parser.add_argument("--start", action="store_true", help="Explicitly initialize secrets and launch; default is read-only check")
    args = parser.parse_args()
    docker = ["sudo", "-n", "docker"] if args.sudo_docker else ["docker"]
    try:
        if hasattr(os, "geteuid") and os.geteuid() == 0:
            raise ValueError("Run Python as your normal user; use --sudo-docker for Docker access")
        operation = start if args.start else check
        result = operation(args.directory, args.project, args.port, docker, clean_environment())
        if not args.start:
            _, result = result
        print(json.dumps(result, sort_keys=True))
        return 0
    except (OSError, ValueError, subprocess.TimeoutExpired) as error:
        print(f"Launch failed ({type(error).__name__}). Check prerequisites, unchanged prepared files, unused project/port and Docker access. Any created files/volumes are retained; never delete keys to retry.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
