#!/usr/bin/env python3
"""Capture or verify a private, quiesced Compose backup. Never restore or stop services."""

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import stat
import subprocess
import sys


SCHEMA = "openjibo.self-hosted-backup.v1"
FILES = {"configuration.env", "runtime-private.json", "state.dump", "memory.dump", "api-data.tar"}
HEX = re.compile(r"[0-9a-f]{64}\Z")


class BackupError(Exception):
    pass


def digest(path):
    result = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            result.update(block)
    return result.hexdigest()


def regular(path):
    if not stat.S_ISREG(path.lstat().st_mode):
        raise BackupError("Backup inputs and members must be regular files")


def command(args, output=None, timeout=30):
    try:
        result = subprocess.run(args, stdout=output if output is not None else subprocess.PIPE,
                                stderr=subprocess.DEVNULL, timeout=timeout, check=False)
    except (OSError, subprocess.TimeoutExpired):
        raise BackupError("Docker command unavailable or timed out") from None
    if result.returncode != 0:
        raise BackupError("Docker command failed; diagnostics withheld because they may contain secrets")
    return result.stdout


def inspect_container(container_id):
    value = json.loads(command(["docker", "inspect", "--type", "container", container_id]))
    if not isinstance(value, list) or len(value) != 1 or value[0]["Id"] != container_id:
        raise BackupError("Unexpected container inspection")
    return value[0]


def named_volume(container, destination):
    matches = [m for m in container["Mounts"] if m["Destination"] == destination]
    if len(matches) != 1 or matches[0]["Type"] != "volume" or not matches[0].get("Name"):
        raise BackupError("Expected standard named data volumes")
    # Nested mounts would hide data from the /data archive.
    if any(m["Destination"].startswith(destination + "/") for m in container["Mounts"]):
        raise BackupError("Nested data mounts are outside the backup contract")
    return matches[0]["Name"]


def environment(container):
    result = {}
    for entry in container["Config"]["Env"]:
        key, value = entry.split("=", 1)
        if key in result:
            raise BackupError("Duplicate effective environment setting")
        result[key] = value
    return result


def validate_containers(containers, project):
    if set(containers) != {"api", "migrate", "postgres"}:
        raise BackupError("Expected exactly one api, migrate and postgres container")
    for service, container in containers.items():
        labels = container["Config"].get("Labels", {})
        if (labels.get("com.docker.compose.project") != project
                or labels.get("com.docker.compose.service") != service
                or labels.get("com.docker.compose.oneoff", "False").lower() != "false"):
            raise BackupError("Container does not belong to selected Compose service")
        state = container["State"]
        if state.get("Paused") or state.get("Restarting"):
            raise BackupError("Paused or restarting services cannot be backed up")
        if service == "postgres":
            if not state["Running"] or state["Status"] != "running":
                raise BackupError("PostgreSQL must be running")
        elif state["Running"] or state["Status"] not in ("exited", "created"):
            raise BackupError("Stop api and migrate before backup; this tool never stops them")
    api = environment(containers["api"])
    migration = environment(containers["migrate"])
    postgres = environment(containers["postgres"])
    required = {
        "OpenJibo__Deployment__Mode": "self-hosted-isolated",
        "OpenJibo__State__Backend": "PostgreSql",
        "OpenJibo__PersonalMemory__Backend": "PostgreSql",
        "OpenJibo__Media__Backend": "File",
        "OpenJibo__Media__DirectoryPath": "/data/media",
        "OPENJIBO_POSTGRES_HOST": "postgres",
        "OPENJIBO_POSTGRES_PORT": "5432",
        "OPENJIBO_POSTGRES_USER": "openjibo",
    }
    if any(api.get(k) != v for k, v in required.items()):
        raise BackupError("Only the standard isolated PostgreSQL/File-media Compose profile is supported")
    for settings in (api, migration):
        if len({k.lower() for k in settings}) != len(settings):
            raise BackupError("Ambiguous environment setting casing")
        if any(value and ("connectionstring" in key.lower().replace("_", "")
                          or "connection_string" in key.lower()) for key, value in settings.items()):
            raise BackupError("Custom connection strings are outside the backup contract")
    for key in ("OPENJIBO_USER_ENCRYPT", "OPENJIBO_USER_SALT", "OPENJIBO_POSTGRES_PASSWORD"):
        if not api.get(key) or migration.get(key) != api[key]:
            raise BackupError("API and migration secrets must be present and match")
    if postgres.get("POSTGRES_USER") != "openjibo" or postgres.get("POSTGRES_DB") != "openjibo_state":
        raise BackupError("Custom PostgreSQL user or database is outside the backup contract")
    data = named_volume(containers["api"], "/data")
    if len(containers["api"]["Mounts"]) != 1 or len(containers["migrate"]["Mounts"]) != 1:
        raise BackupError("Custom API/migration mounts are outside the backup contract")
    if named_volume(containers["migrate"], "/data") != data:
        raise BackupError("API and migration data volumes differ")
    pgdata = named_volume(containers["postgres"], "/var/lib/postgresql/data")
    if data == pgdata:
        raise BackupError("Database and API data volumes must differ")
    return data, pgdata


def observe(compose, project):
    containers = {}
    for service in ("api", "migrate", "postgres"):
        ids = command(compose + ["ps", "--all", "--quiet", service]).decode("ascii").split()
        if len(ids) != 1 or not HEX.fullmatch(ids[0]):
            raise BackupError("Expected one existing container per service")
        containers[service] = inspect_container(ids[0])
    volumes = validate_containers(containers, project)
    for volume in volumes:
        active = command(["docker", "ps", "--quiet", "--no-trunc", "--filter", "volume=" + volume]).decode("ascii").split()
        allowed = {containers["postgres"]["Id"]} if volume == volumes[1] else set()
        if set(active) - allowed:
            raise BackupError("Another running container uses a backed-up volume")
    return containers


def identity(containers):
    result = {}
    for name, item in containers.items():
        result[name] = {key: item[key] for key in ("Id", "Image", "Mounts", "Config", "RestartCount")}
        result[name]["State"] = {key: item["State"].get(key) for key in
                                 ("Running", "Status", "StartedAt", "FinishedAt", "Paused", "Restarting")}
    return result


def capture(args):
    if not args.writers_stopped:
        raise BackupError("--writers-stopped is required after stopping application and external writers")
    directory = Path(args.directory).resolve(strict=True)
    env = Path(args.env_file).resolve(strict=True)
    regular(env)
    files = [Path(p).resolve(strict=True) for p in args.compose_file]
    for file in files:
        regular(file)
    if not re.fullmatch(r"[a-z0-9][a-z0-9_-]*", args.project_name):
        raise BackupError("Invalid Compose project name")
    compose = ["docker", "compose", "--project-directory", str(directory), "--project-name", args.project_name,
               "--env-file", str(env)]
    for file in files:
        compose += ["--file", str(file)]
    compose += ["--profile", "self-hosted"]
    before = observe(compose, args.project_name)
    configuration = env.read_bytes()
    output = Path(args.output).absolute()
    if not output.parent.is_dir():
        raise BackupError("Backup parent must already exist")
    output.mkdir(mode=0o700)  # exclusive; partial output is retained without a completion manifest
    (output / "configuration.env").write_bytes(configuration)
    runtime = {
        "schema": SCHEMA,
        "containers": {service: {"imageId": value["Image"], "imageReference": value["Config"]["Image"],
                                 "environment": value["Config"]["Env"]} for service, value in before.items()},
        "composeInputs": [{"filename": p.name, "sha256": digest(p)} for p in files],
    }
    (output / "runtime-private.json").write_text(json.dumps(runtime, indent=2) + "\n", encoding="utf-8")
    postgres = before["postgres"]["Id"]
    for database, filename in (("openjibo_state", "state.dump"), ("openjibo_memory", "memory.dump")):
        with (output / filename).open("xb") as stream:
            command(["docker", "exec", postgres, "pg_dump", "--username=openjibo", "--dbname=" + database,
                     "--format=custom", "--no-owner", "--no-acl"], output=stream, timeout=args.timeout)
    with (output / "api-data.tar").open("xb") as stream:
        command(["docker", "cp", before["api"]["Id"] + ":/data/.", "-"], output=stream, timeout=args.timeout)
    after = observe(compose, args.project_name)
    if identity(before) != identity(after) or env.read_bytes() != configuration:
        raise BackupError("Container/configuration changed during backup; incomplete output retained")
    for file in FILES:
        if not (output / file).stat().st_size:
            raise BackupError("Empty backup member; incomplete output retained")
    manifest = {
        "schema": SCHEMA, "createdUtc": datetime.now(timezone.utc).isoformat(),
        "applicationWritersObservedStopped": True, "externalWritersStoppedAcknowledged": True,
        "crossDatabaseAtomicSnapshot": False, "restoreVerified": False,
        "files": {name: {"sha256": digest(output / name), "bytes": (output / name).stat().st_size}
                  for name in sorted(FILES)},
    }
    with (output / "manifest.json").open("x", encoding="utf-8") as stream:
        stream.write(json.dumps(manifest, indent=2) + "\n")
    return {"backupCompleted": True, "manifestSha256": digest(output / "manifest.json"),
            "containsSecrets": True, "restoreVerified": False}


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise BackupError("Duplicate backup manifest key")
        result[key] = value
    return result


def verify(directory, expected):
    if not HEX.fullmatch(expected):
        raise BackupError("Expected manifest SHA-256 must be 64 lowercase hex characters")
    directory = Path(directory)
    if directory.is_symlink() or not directory.is_dir():
        raise BackupError("Expected a regular backup directory")
    if {p.name for p in directory.iterdir()} != FILES | {"manifest.json"}:
        raise BackupError("Backup inventory is incomplete or contains extra files")
    for name in FILES | {"manifest.json"}:
        regular(directory / name)
    manifest_path = directory / "manifest.json"
    with manifest_path.open("rb") as stream:
        snapshot = stream.read(16385)
    if len(snapshot) > 16384 or hashlib.sha256(snapshot).hexdigest() != expected:
        raise BackupError("Manifest does not match the independently retained checksum")
    manifest = json.loads(snapshot, object_pairs_hook=unique_object)
    if (not isinstance(manifest, dict) or manifest.get("schema") != SCHEMA
            or not isinstance(manifest.get("files"), dict) or set(manifest["files"]) != FILES):
        raise BackupError("Unexpected backup manifest contract")
    for name in FILES:
        record = manifest["files"][name]
        if (not isinstance(record, dict) or type(record.get("bytes")) is not int or record["bytes"] <= 0
                or (directory / name).stat().st_size != record["bytes"]
                or digest(directory / name) != record.get("sha256")):
            raise BackupError("Backup member checksum or size mismatch")
    return {"backupBytesVerified": True, "containsSecrets": True, "restoreVerified": False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="action", required=True)
    create = sub.add_parser("capture")
    create.add_argument("--directory", required=True)
    create.add_argument("--project-name", required=True)
    create.add_argument("--env-file", required=True)
    create.add_argument("--compose-file", required=True, action="append")
    create.add_argument("--output", required=True)
    create.add_argument("--writers-stopped", action="store_true")
    create.add_argument("--timeout", type=int, default=300)
    check = sub.add_parser("verify")
    check.add_argument("--backup", required=True)
    check.add_argument("--expected-manifest-sha256", required=True)
    args = parser.parse_args()
    try:
        if args.action == "capture":
            if not 1 <= args.timeout <= 3600:
                raise BackupError("--timeout must be 1 through 3600 seconds per transfer")
            result = capture(args)
        else:
            result = verify(args.backup, args.expected_manifest_sha256)
    except BackupError as error:
        print(json.dumps({"ok": False, "error": str(error)}))
        return 2
    except (OSError, ValueError, KeyError, TypeError):
        print(json.dumps({"ok": False, "error": "Backup operation refused or incomplete; private diagnostics withheld"}))
        return 2
    print(json.dumps({"ok": True, **result}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
