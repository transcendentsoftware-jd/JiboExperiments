#!/usr/bin/env bash
set -euo pipefail

HOSTS=(
  "https://api.jibo.com/health"
  "https://api-socket.jibo.com/"
  "https://neo-hub.jibo.com/v1/proactive"
)

for url in "${HOSTS[@]}"; do
  if status_code="$(curl --silent --output /dev/null --write-out "%{http_code}" --insecure "${url}")"; then
    echo "${url} status=${status_code} success=true"
  else
    echo "${url} status=000 success=false"
  fi
done
