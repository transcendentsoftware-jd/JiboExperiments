#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
BASE_URL="${BASE_URL:-https://localhost:5001}"
CAPTURE_DIRECTORY="${CAPTURE_DIRECTORY:-${REPO_ROOT}/captures/websocket}"
EXPECTED_HOSTS=(
  "api.jibo.com"
  "api-socket.jibo.com"
  "neo-hub.jibo.com"
)

echo "OpenJibo live Jibo prep"
echo ""

echo "1. HTTP health check"
curl --silent --show-error --fail "${BASE_URL%/}/health" | python3 -m json.tool

echo ""
echo "2. Expected robot-facing hosts"
for host in "${EXPECTED_HOSTS[@]}"; do
  echo " - ${host}"
done

echo ""
echo "3. Capture directory"
mkdir -p "${CAPTURE_DIRECTORY}"
echo " - ${CAPTURE_DIRECTORY}"

echo ""
echo "4. Live-run checklist"
echo " - keep the Ubuntu/Jibo routing setup in place"
echo " - keep the Node server available as a fallback"
echo " - point Jibo at the .NET server using the same controlled network settings"
echo " - perform one startup check, one chat turn, and one joke turn"
echo " - after the run, inspect capture output with scripts/cloud/get-websocket-capture-summary.sh"
echo " - import the best exported fixture with scripts/cloud/import-websocket-capture-fixture.py"
