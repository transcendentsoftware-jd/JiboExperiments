#!/usr/bin/env bash
set -euo pipefail

resource_group_name=""
key_vault_name=""
registry_name=""
image_tag="managed"
location=""
api_hostname="api.openjibo.com"
socket_hostname="open-jibo-socket.openjibo.com"
neohub_hostname="neohub.openjibo.com"
native_compatibility_api_hostname="open-jibo.jibo.pro"
native_compatibility_socket_hostname="open-jibo-socket.jibo.pro"
enable_azure_speech=true
azure_speech_region=""
template_path="infra/azure/container-apps/openjibo-managed.bicep"
parameters_path="infra/azure/container-apps/openjibo-managed.parameters.json"
run_migration=false
run_smoke=false
smoke_generated_fqdn=false
skip_hostname_binding=false
peer_sync_enabled=false
allowed_peer_hosts=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --resource-group-name)
      resource_group_name="${2:-}"
      shift 2
      ;;
    --key-vault-name)
      key_vault_name="${2:-}"
      shift 2
      ;;
    --registry-name)
      registry_name="${2:-}"
      shift 2
      ;;
    --image-tag)
      image_tag="${2:-managed}"
      shift 2
      ;;
    --location)
      location="${2:-}"
      shift 2
      ;;
    --api-hostname)
      api_hostname="${2:-api.openjibo.com}"
      shift 2
      ;;
    --socket-hostname)
      socket_hostname="${2:-open-jibo-socket.openjibo.com}"
      shift 2
      ;;
    --neohub-hostname)
      neohub_hostname="${2:-neohub.openjibo.com}"
      shift 2
      ;;
    --native-compatibility-api-hostname)
      native_compatibility_api_hostname="${2:-open-jibo.jibo.pro}"
      shift 2
      ;;
    --native-compatibility-socket-hostname)
      native_compatibility_socket_hostname="${2:-open-jibo-socket.jibo.pro}"
      shift 2
      ;;
    --enable-azure-speech)
      enable_azure_speech=true
      shift
      ;;
    --disable-azure-speech)
      enable_azure_speech=false
      shift
      ;;
    --azure-speech-region)
      azure_speech_region="${2:-eastus}"
      shift 2
      ;;
    --template-path)
      template_path="${2:-infra/azure/container-apps/openjibo-managed.bicep}"
      shift 2
      ;;
    --parameters-path)
      parameters_path="${2:-infra/azure/container-apps/openjibo-managed.parameters.json}"
      shift 2
      ;;
    --run-migration)
      run_migration=true
      shift
      ;;
    --run-smoke)
      run_smoke=true
      shift
      ;;
    --smoke-generated-fqdn)
      smoke_generated_fqdn=true
      shift
      ;;
    --skip-hostname-binding)
      skip_hostname_binding=true
      shift
      ;;
    --enable-peer-sync)
      peer_sync_enabled=true
      shift
      ;;
    --disable-peer-sync)
      peer_sync_enabled=false
      shift
      ;;
    --peer-sync-allowed-hosts)
      allowed_peer_hosts="${2:-}"
      shift 2
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 2
      ;;
  esac
done

if [[ "$peer_sync_enabled" == true && -z "$allowed_peer_hosts" ]]; then
  echo "--peer-sync-allowed-hosts is required when --enable-peer-sync is used" >&2
  exit 2
fi

for required_name in resource_group_name key_vault_name registry_name; do
  if [[ -z "${!required_name}" ]]; then
    echo "--${required_name//_/-} is required" >&2
    exit 2
  fi
done

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/../.." && pwd)"

resolve_path() {
  local candidate="$1"
  if [[ "$candidate" = /* ]]; then
    printf '%s\n' "$candidate"
  else
    printf '%s\n' "${repo_root}/${candidate}"
  fi
}

resolved_template_path="$(resolve_path "$template_path")"
resolved_parameters_path="$(resolve_path "$parameters_path")"

parse_postgres_server_name() {
  python3 - "$1" <<'PY'
import sys

connection_string = sys.argv[1]
for segment in connection_string.split(";"):
    key, _, value = segment.partition("=")
    if key.strip().lower() == "host" and value.strip():
        print(value.strip().split(".", 1)[0])
        raise SystemExit(0)

raise SystemExit("Could not determine the PostgreSQL server name from the connection string.")
PY
}

parse_postgres_database_name() {
  python3 - "$1" <<'PY'
import sys

connection_string = sys.argv[1]
for segment in connection_string.split(";"):
    key, _, value = segment.partition("=")
    if key.strip().lower() in {"database", "initial catalog"} and value.strip():
        print(value.strip())
        raise SystemExit(0)

raise SystemExit("Could not determine the PostgreSQL database name from the connection string.")
PY
}

ensure_postgres_firewall_rule() {
  local postgres_server_name="$1"
  local rule_name="$2"
  local ip_address="$3"
  local help_text=""
  local server_flag=""
  local rule_flag=""

  help_text="$(az postgres flexible-server firewall-rule create -h 2>&1 || true)"
  if grep -q -- '--server-name' <<<"$help_text"; then
    server_flag="--server-name"
  else
    server_flag="--name"
  fi

  if grep -q -- '--rule-name' <<<"$help_text"; then
    rule_flag="--rule-name"
  else
    rule_flag="--name"
  fi

  if az postgres flexible-server firewall-rule show \
    --resource-group "$resource_group_name" \
    "$server_flag" "$postgres_server_name" \
    "$rule_flag" "$rule_name" \
    --output none >/dev/null 2>&1; then
    echo "Refreshing PostgreSQL firewall rule '${rule_name}' for server '${postgres_server_name}'." >&2
    az postgres flexible-server firewall-rule update \
      --resource-group "$resource_group_name" \
      "$server_flag" "$postgres_server_name" \
      "$rule_flag" "$rule_name" \
      --start-ip-address "$ip_address" \
      --end-ip-address "$ip_address" \
      --output none
  else
    echo "Creating PostgreSQL firewall rule '${rule_name}' for server '${postgres_server_name}'." >&2
    az postgres flexible-server firewall-rule create \
      --resource-group "$resource_group_name" \
      "$server_flag" "$postgres_server_name" \
      "$rule_flag" "$rule_name" \
      --start-ip-address "$ip_address" \
      --end-ip-address "$ip_address" \
      --output none
  fi
}

remove_postgres_firewall_rule() {
  local postgres_server_name="$1"
  local rule_name="$2"

  echo "Removing temporary deployment runner firewall rule '${rule_name}' from server '${postgres_server_name}'." >&2
  az postgres flexible-server firewall-rule delete \
    --resource-group "$resource_group_name" \
    --name "$postgres_server_name" \
    --rule-name "$rule_name" \
    --yes \
    --output none >/dev/null 2>&1 || true
}

run_command_with_retry() {
  local description="$1"
  local attempts="${2:-6}"
  shift 2
  local attempt
  local exit_code
  local output
  for attempt in $(seq 1 "$attempts"); do
    output="$("$@" 2>&1)"
    exit_code=$?
    if [[ $exit_code -eq 0 ]]; then
      printf '%s\n' "$output"
      return 0
    fi
    if [[ $attempt -eq $attempts ]]; then
      printf '%s\n' "$output" >&2
      return $exit_code
    fi
    local wait_seconds=$(( attempt < 9 ? attempt * 10 : 90 ))
    local last_line
    last_line="$(tail -1 <<<"$output" | tr -d '\r')"
    local suffix=""
    if [[ -n "$last_line" ]]; then
      suffix=" Last Azure CLI message: ${last_line}"
    fi
    echo "${description} failed; retrying in ${wait_seconds} seconds.${suffix}" >&2
    sleep "$wait_seconds"
  done
}

if [[ ! -f "$resolved_template_path" ]]; then
  echo "Could not find Bicep template at $resolved_template_path" >&2
  exit 1
fi

if [[ ! -f "$resolved_parameters_path" ]]; then
  echo "Could not find parameter file at $resolved_parameters_path" >&2
  exit 1
fi

registry_login_server="${registry_name}.azurecr.io"
deployment_name="openjibo-managed-$(date -u +%s)"

state_connection_string="$(az keyvault secret show --vault-name "$key_vault_name" --name openjibo-state-connection-string --query value -o tsv)"
personal_memory_connection_string="$(az keyvault secret show --vault-name "$key_vault_name" --name openjibo-personal-memory-connection-string --query value -o tsv)"
postgres_server_name="$(parse_postgres_server_name "$state_connection_string")"
personal_memory_postgres_server_name="$(parse_postgres_server_name "$personal_memory_connection_string")"
state_database_name="$(parse_postgres_database_name "$state_connection_string")"
personal_memory_database_name="$(parse_postgres_database_name "$personal_memory_connection_string")"
if [[ "$postgres_server_name" != "$personal_memory_postgres_server_name" ]]; then
  echo "Managed state and personal-memory connections target different PostgreSQL servers." >&2
  exit 1
fi
media_connection_string="$(az keyvault secret show --vault-name "$key_vault_name" --name openjibo-media-connection-string --query value -o tsv)"
open_weather_api_key="$(az keyvault secret show --vault-name "$key_vault_name" --name openjibo-openweather-api-key --query value -o tsv)"
news_api_key="$(az keyvault secret show --vault-name "$key_vault_name" --name openjibo-newsapi-key --query value -o tsv)"
search_backend="$(az keyvault secret show --vault-name "$key_vault_name" --name openjibo-search-backend --query value -o tsv 2>/dev/null || true)"
search_fallback="$(az keyvault secret show --vault-name "$key_vault_name" --name openjibo-search-fallback --query value -o tsv 2>/dev/null || true)"
portal_status_password="$(az keyvault secret show --vault-name "$key_vault_name" --name openjibo-portal-status-password --query value -o tsv)"
peer_sync_shared_key="$(az keyvault secret show --vault-name "$key_vault_name" --name openjibo-peer-sync-shared-key --query value -o tsv)"
user_encryption_passphrase="$(az keyvault secret show --vault-name "$key_vault_name" --name openjibo-user-encrypt --query value -o tsv)"
user_encryption_salt="$(az keyvault secret show --vault-name "$key_vault_name" --name openjibo-user-salt --query value -o tsv)"

if [[ -z "$user_encryption_passphrase" || -z "$user_encryption_salt" ]]; then
  echo "Managed user encryption secrets are missing from Key Vault '$key_vault_name'." >&2
  exit 1
fi

if [[ "$run_migration" == true ]]; then
  bash "${script_dir}/prepare-openjibo-managed-databases.sh" \
    --key-vault-name "$key_vault_name"
fi

echo "Deploying Open Jibo managed Container Apps stack to resource group '${resource_group_name}'"
deployment_args=(
  az deployment group create
  --resource-group "$resource_group_name"
  --name "$deployment_name"
  --template-file "$resolved_template_path"
  --parameters "@${resolved_parameters_path}"
  --parameters "registryLoginServer=${registry_login_server}"
  --parameters "keyVaultName=${key_vault_name}"
  --parameters "imageTag=${image_tag}"
  --parameters "apiHostname=${api_hostname}"
  --parameters "socketHostname=${socket_hostname}"
  --parameters "neoHubHostname=${neohub_hostname}"
  --parameters "nativeCompatibilityApiHostname=${native_compatibility_api_hostname}"
  --parameters "nativeCompatibilitySocketHostname=${native_compatibility_socket_hostname}"
  --parameters "postgresServerName=${postgres_server_name}"
  --parameters "stateDatabaseName=${state_database_name}"
  --parameters "personalMemoryDatabaseName=${personal_memory_database_name}"
  --parameters "stateConnectionString=${state_connection_string}"
  --parameters "personalMemoryConnectionString=${personal_memory_connection_string}"
  --parameters "mediaConnectionString=${media_connection_string}"
  --parameters "openWeatherApiKey=${open_weather_api_key}"
  --parameters "newsApiKey=${news_api_key}"
  --parameters "portalStatusPassword=${portal_status_password}"
  --parameters "peerSyncSharedKey=${peer_sync_shared_key}"
  --parameters "peerSyncEnabled=${peer_sync_enabled}"
  --parameters "allowedPeerHosts=${allowed_peer_hosts}"
  --parameters "userEncryptionPassphrase=${user_encryption_passphrase}"
  --parameters "userEncryptionSalt=${user_encryption_salt}"
)

if [[ "$enable_azure_speech" == true ]]; then
  deployment_args+=(--parameters "enableAzureSpeech=true")
  azure_speech_subscription_key="$(az keyvault secret show --vault-name "$key_vault_name" --name azure-speech-subscription-key --query value -o tsv)"
  if [[ -z "$azure_speech_subscription_key" ]]; then
    echo "Could not read azure-speech-subscription-key from Key Vault '$key_vault_name'." >&2
    exit 1
  fi
  deployment_args+=(--parameters "azureSpeechSubscriptionKey=${azure_speech_subscription_key}")
  if [[ -n "$azure_speech_region" ]]; then
    deployment_args+=(--parameters "azureSpeechRegion=${azure_speech_region}")
  fi
fi

if [[ -n "$location" ]]; then
  deployment_args+=(--parameters "location=${location}")
fi

if [[ -n "$search_backend" ]]; then
  deployment_args+=(--parameters "searchBackend=${search_backend}")
fi

if [[ -n "$search_fallback" ]]; then
  deployment_args+=(--parameters "searchFallback=${search_fallback}")
fi

deployment_args+=(--output json)

deployment_json="$("${deployment_args[@]}")"

if [[ "$skip_hostname_binding" != true && -n "$api_hostname" ]]; then
  container_app_name="$(python3 - "$deployment_json" <<'PY'
import json
import sys

deployment_json = json.loads(sys.argv[1])
print(deployment_json["properties"]["outputs"]["containerAppName"]["value"])
PY
)"
  managed_environment_name="$(python3 - "$deployment_json" <<'PY'
import json
import sys

deployment_json = json.loads(sys.argv[1])
print(deployment_json["properties"]["outputs"]["managedEnvironmentName"]["value"])
PY
)"

  bind_containerapp_hostname() {
    local hostname="$1"
    if [[ -z "$hostname" ]]; then
      return
    fi

    echo "Adding hostname '${hostname}' to Container App '${container_app_name}'. DNS must point directly at the generated Container App hostname before Azure can issue the managed certificate." >&2
    az containerapp hostname add \
      --resource-group "$resource_group_name" \
      --name "$container_app_name" \
      --hostname "$hostname" \
      --output none
    echo "Hostname '${hostname}' added. Binding the managed certificate for Container App '${container_app_name}'." >&2
    az containerapp hostname bind \
      --resource-group "$resource_group_name" \
      --name "$container_app_name" \
      --hostname "$hostname" \
      --environment "$managed_environment_name" \
      --validation-method CNAME \
      --output none
    echo "Managed certificate binding completed for hostname '${hostname}'." >&2
  }

  bind_containerapp_hostname "$api_hostname"
  bind_containerapp_hostname "$socket_hostname"
  bind_containerapp_hostname "$neohub_hostname"
  bind_containerapp_hostname "$native_compatibility_api_hostname"
  bind_containerapp_hostname "$native_compatibility_socket_hostname"

  state_connection_string="$(az keyvault secret show --vault-name "$key_vault_name" --name openjibo-state-connection-string --query value -o tsv)"
  postgres_server_name="$(parse_postgres_server_name "$state_connection_string")"
  environment_json="$(
    az containerapp env show \
    --resource-group "$resource_group_name" \
    --name "$managed_environment_name" \
    --output json \
    --only-show-errors 2>/dev/null || true
  )"

  while IFS= read -r outbound_ip; do
    if [[ -z "$outbound_ip" ]]; then
      continue
    fi

    rule_name="AllowContainerApps-${outbound_ip//./-}"
    echo "Ensuring PostgreSQL firewall rule '${rule_name}' for Container Apps outbound IP '${outbound_ip}'." >&2
    ensure_postgres_firewall_rule "$postgres_server_name" "$rule_name" "$outbound_ip"
  done < <(
    python3 - "$environment_json" <<'PY'
import json
import sys
import re

text = sys.argv[1].strip()
if not text:
    raise SystemExit(0)

document = json.loads(text)
ips = []
ip_pattern = re.compile(r"\b(?:\d{1,3}\.){3}\d{1,3}\b")

def collect(value):
    if isinstance(value, str):
        if ip_pattern.fullmatch(value.strip()):
            ips.append(value)
        return
    if isinstance(value, list):
        for item in value:
            collect(item)
        return
    if isinstance(value, dict):
        for key, child in value.items():
            if key in {"outboundIpAddresses", "staticIp", "staticIpAddress"}:
                collect(child)
            else:
                collect(child)

collect(document)

for outbound_ip in dict.fromkeys(ips):
    print(outbound_ip)
PY
  )

  sleep 30
fi

state_connection_string="$(az keyvault secret show --vault-name "$key_vault_name" --name openjibo-state-connection-string --query value -o tsv)"
personal_memory_connection_string="$(az keyvault secret show --vault-name "$key_vault_name" --name openjibo-personal-memory-connection-string --query value -o tsv)"
if [[ -n "${container_app_name:-}" ]]; then
  echo "Refreshing Container App '${container_app_name}' PostgreSQL secrets from Key Vault to force the latest database credentials into the revision." >&2
  secret_args=(
    "state-connection-string=${state_connection_string}"
    "personal-memory-connection-string=${personal_memory_connection_string}"
    "portal-status-password=${portal_status_password}"
    "peer-sync-shared-key=${peer_sync_shared_key}"
    "user-encryption-passphrase=${user_encryption_passphrase}"
    "user-encryption-salt=${user_encryption_salt}"
  )

  if [[ -n "$search_backend" ]]; then
    secret_args+=("search-backend=${search_backend}")
  fi

  if [[ -n "$search_fallback" ]]; then
    secret_args+=("search-fallback=${search_fallback}")
  fi

  run_command_with_retry \
    "Container App secret refresh for '${container_app_name}'" 6 \
    az containerapp secret set \
    --resource-group "$resource_group_name" \
    --name "$container_app_name" \
    --secrets "${secret_args[@]}" \
    --output none >/dev/null
  echo "Container App PostgreSQL secrets refreshed for '${container_app_name}'." >&2

  latest_revision_name="$(run_command_with_retry \
    "Container App latest revision lookup for '${container_app_name}'" 4 \
    az containerapp show \
    --resource-group "$resource_group_name" \
    --name "$container_app_name" \
    --query properties.latestRevisionName \
    --output tsv)"
  if [[ -z "$latest_revision_name" ]]; then
    echo "Container App '${container_app_name}' did not report a latest revision name." >&2
    exit 1
  fi

  echo "Restarting Container App revision '${latest_revision_name}' so the refreshed secrets take effect." >&2
  run_command_with_retry \
    "Container App revision restart for '${container_app_name}'" 4 \
    az containerapp revision restart \
    --resource-group "$resource_group_name" \
    --name "$container_app_name" \
    --revision "$latest_revision_name" \
    --output none >/dev/null
  echo "Container App revision '${latest_revision_name}' restarted." >&2
fi

sleep 20


if [[ "$run_smoke" == true ]]; then
  smoke_base_url="$(python3 - "$deployment_json" "$api_hostname" "$smoke_generated_fqdn" <<'PY'
import json
import sys

api_hostname = sys.argv[2].strip()
use_generated_fqdn = sys.argv[3].strip().lower() == "true"
if api_hostname and not use_generated_fqdn:
    print(f"https://{api_hostname}")
    raise SystemExit(0)

deployment_json = json.loads(sys.argv[1])
container_app_fqdn = deployment_json["properties"]["outputs"]["containerAppFqdn"]["value"]
print(f"https://{container_app_fqdn}")
PY
)"

  if [[ -z "$smoke_base_url" ]]; then
    echo "Smoke base URL could not be resolved from the canonical API hostname or deployment output." >&2
    exit 1
  fi

  BASE_URL="$smoke_base_url" bash "${script_dir}/invoke-cloud-smoke.sh"
fi

if [[ -n "${postgres_server_name:-}" ]]; then
  remove_postgres_firewall_rule "$postgres_server_name" "AllowDeploymentRunner"
fi

printf '%s\n' "$deployment_json"
