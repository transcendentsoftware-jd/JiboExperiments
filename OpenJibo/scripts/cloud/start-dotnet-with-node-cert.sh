#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
API_PROJECT="${REPO_ROOT}/src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Api/Jibo.Cloud.Api.csproj"

CERT_PEM="${CERT_PEM:-${REPO_ROOT}/src/Jibo.Cloud/node/cert.pem}"
KEY_PEM="${KEY_PEM:-${REPO_ROOT}/src/Jibo.Cloud/node/key.pem}"
CHAIN_PEM="${CHAIN_PEM:-}"
PFX_OUT="${PFX_OUT:-${REPO_ROOT}/.tmp/openjibo-dev-cert.pfx}"
PFX_PASSWORD="${PFX_PASSWORD:-openjibo-dev-password}"
ASPNETCORE_URLS="${ASPNETCORE_URLS:-https://0.0.0.0:443;http://0.0.0.0:24605}"
DOTNET_ENVIRONMENT="${DOTNET_ENVIRONMENT:-Development}"
CAPTURE_DIRECTORY="${CAPTURE_DIRECTORY:-${REPO_ROOT}/captures/websocket}"
PROTOCOL_CAPTURE_DIRECTORY="${PROTOCOL_CAPTURE_DIRECTORY:-${REPO_ROOT}/captures/http}"

mkdir -p "$(dirname "${PFX_OUT}")"
mkdir -p "${CAPTURE_DIRECTORY}"
mkdir -p "${PROTOCOL_CAPTURE_DIRECTORY}"

if [[ ! -f "${CERT_PEM}" ]]; then
  echo "Missing CERT_PEM: ${CERT_PEM}" >&2
  exit 1
fi

if [[ ! -f "${KEY_PEM}" ]]; then
  echo "Missing KEY_PEM: ${KEY_PEM}" >&2
  exit 1
fi

OPENSSL_ARGS=(
  pkcs12
  -export
  -out "${PFX_OUT}"
  -inkey "${KEY_PEM}"
  -in "${CERT_PEM}"
  -passout "pass:${PFX_PASSWORD}"
)

if [[ -n "${CHAIN_PEM}" ]]; then
  if [[ ! -f "${CHAIN_PEM}" ]]; then
    echo "Missing CHAIN_PEM: ${CHAIN_PEM}" >&2
    exit 1
  fi

  OPENSSL_ARGS+=( -certfile "${CHAIN_PEM}" )
fi

echo "Creating PFX for Kestrel"
echo " - cert: ${CERT_PEM}"
echo " - key: ${KEY_PEM}"
if [[ -n "${CHAIN_PEM}" ]]; then
  echo " - chain: ${CHAIN_PEM}"
fi
echo " - pfx: ${PFX_OUT}"
openssl "${OPENSSL_ARGS[@]}"

export ASPNETCORE_URLS
export DOTNET_ENVIRONMENT
export ASPNETCORE_Kestrel__Certificates__Default__Path="${PFX_OUT}"
export ASPNETCORE_Kestrel__Certificates__Default__Password="${PFX_PASSWORD}"
export OpenJibo__Telemetry__DirectoryPath="${CAPTURE_DIRECTORY}"
export OpenJibo__ProtocolTelemetry__DirectoryPath="${PROTOCOL_CAPTURE_DIRECTORY}"

echo ""
echo "Starting OpenJibo .NET cloud"
echo " - project: ${API_PROJECT}"
echo " - urls: ${ASPNETCORE_URLS}"
echo " - environment: ${DOTNET_ENVIRONMENT}"
echo " - websocket captures: ${CAPTURE_DIRECTORY}"
echo " - http captures: ${PROTOCOL_CAPTURE_DIRECTORY}"

cd "${REPO_ROOT}"
exec dotnet run --project "${API_PROJECT}" --no-launch-profile
