#!/usr/bin/env python3
"""Verify non-secret database identity markers on an existing production Container App."""

from __future__ import annotations

import json
import re
import sys
from typing import NoReturn


MARKERS = {
    "OpenJibo__Deployment__PostgreSqlServerName": "server",
    "OpenJibo__Deployment__StateDatabaseName": "state",
    "OpenJibo__Deployment__PersonalMemoryDatabaseName": "memory",
}


def fail(message: str) -> NoReturn:
    raise SystemExit(message)


def main() -> None:
    if len(sys.argv) != 6:
        fail(
            "Usage: verify-openjibo-production-database-binding.py "
            "<expected-server> <expected-state-database> <expected-memory-database> "
            "<bootstrap-confirmed> <bootstrap-already-completed>"
        )

    expected = {
        "server": sys.argv[1].strip().lower(),
        "state": sys.argv[2].strip().lower(),
        "memory": sys.argv[3].strip().lower(),
    }
    bootstrap_confirmed = sys.argv[4].strip().lower() == "true"
    bootstrap_already_completed = sys.argv[5].strip().lower() == "true"
    if not re.fullmatch(r"[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?", expected["server"]):
        fail("Expected PostgreSQL server name is invalid.")
    for key in ("state", "memory"):
        if not re.fullmatch(r"[a-z0-9_][a-z0-9_-]{0,62}", expected[key]):
            fail(f"Expected {key} PostgreSQL database name is invalid.")

    try:
        revisions = json.load(sys.stdin)
    except (json.JSONDecodeError, UnicodeDecodeError) as exc:
        fail(f"Container App metadata is not valid JSON: {exc}")
    if not isinstance(revisions, list):
        fail("Container App revision metadata must be a JSON array.")

    active_revisions = []
    for revision in revisions:
        if not isinstance(revision, dict):
            continue
        properties = revision.get("properties", {})
        if properties.get("active") is True and properties.get("trafficWeight", 0) > 0:
            active_revisions.append(revision)
    if len(active_revisions) != 1:
        fail(
            "Expected exactly one active production revision with traffic; found "
            f"{len(active_revisions)}."
        )
    app = active_revisions[0]
    containers = (
        app.get("properties", {})
        .get("template", {})
        .get("containers", [])
    )
    if not isinstance(containers, list) or len(containers) != 1:
        fail("Expected exactly one container in the production Container App.")
    environment = containers[0].get("env", [])
    if not isinstance(environment, list):
        fail("Production Container App environment metadata is invalid.")

    actual: dict[str, str] = {}
    present_markers: set[str] = set()
    for item in environment:
        if not isinstance(item, dict) or item.get("name") not in MARKERS:
            continue
        marker_name = item["name"]
        present_markers.add(marker_name)
        marker_key = MARKERS[marker_name]
        if marker_key in actual:
            fail(f"Database identity marker '{marker_name}' appears more than once.")
        value = item.get("value")
        if not isinstance(value, str) or not value.strip() or item.get("secretRef"):
            fail(f"Database identity marker '{marker_name}' has no valid literal value.")
        actual[marker_key] = value.strip().lower()

    if not present_markers:
        if bootstrap_already_completed:
            fail(
                "Production database identity markers are missing after bootstrap was "
                "previously completed."
            )
        if not bootstrap_confirmed:
            fail(
                "Production Container App has no database identity markers. Confirm the "
                "one-time binding bootstrap only after independently verifying the pinned "
                "resource names."
            )
        print(
            "Production database identity marker bootstrap confirmed for "
            f"'{expected['server']}/{expected['state']}/{expected['memory']}'."
        )
        return

    missing = sorted(set(expected) - set(actual))
    if missing:
        fail(
            "Production Container App has an incomplete database identity marker set: "
            + ", ".join(missing)
        )
    mismatches = [
        key for key in expected if actual[key] != expected[key]
    ]
    if mismatches:
        details = ", ".join(
            f"{key}='{actual[key]}' expected '{expected[key]}'" for key in mismatches
        )
        fail(f"Production database rebind refused: {details}.")

    print(
        "Production database binding verified from non-secret Container App markers for "
        f"'{expected['server']}/{expected['state']}/{expected['memory']}'."
    )


if __name__ == "__main__":
    main()
