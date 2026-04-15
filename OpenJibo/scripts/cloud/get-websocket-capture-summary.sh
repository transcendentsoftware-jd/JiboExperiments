#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
CAPTURE_DIRECTORY="${1:-${REPO_ROOT}/captures/websocket}"

if [[ ! -d "${CAPTURE_DIRECTORY}" ]]; then
  echo "No websocket capture directory found at ${CAPTURE_DIRECTORY}"
  exit 0
fi

shopt -s nullglob
event_files=( "${CAPTURE_DIRECTORY}"/*.events.ndjson )
if [[ ${#event_files[@]} -eq 0 ]]; then
  echo "No websocket telemetry event files found in ${CAPTURE_DIRECTORY}"
  exit 0
fi

python3 - "$CAPTURE_DIRECTORY" "${event_files[@]}" <<'PY'
import collections
import json
import os
import sys

capture_dir = sys.argv[1]
event_files = sys.argv[2:]

counter = collections.Counter()
for path in event_files:
    with open(path, "r", encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            payload = json.loads(line)
            counter[payload.get("EventType", "unknown")] += 1

for key in sorted(counter):
    print(f"{key}: {counter[key]}")

fixture_dir = os.path.join(capture_dir, "fixtures")
if os.path.isdir(fixture_dir):
    print("")
    print("Exported websocket fixtures:")
    for name in sorted(os.listdir(fixture_dir)):
        if name.endswith(".flow.json"):
            print(f" - {name}")
PY
