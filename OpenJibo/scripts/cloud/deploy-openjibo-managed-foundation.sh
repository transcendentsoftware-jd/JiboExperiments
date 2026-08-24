#!/usr/bin/env bash
set -euo pipefail

resource_group_name=""
template_path="infra/azure/foundation/openjibo-managed-foundation.bicep"
state_connection_string=""
personal_memory_connection_string=""
open_weather_api_key=""
news_api_key=""
search_backend=""
search_fallback=""
postgres_admin_login="openjiboadmin"
postgres_admin_password=""
postgres_server_name=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --resource-group-name)
      resource_group_name="${2:-}"
      shift 2
      ;;
    --template-path)
      template_path="${2:-infra/azure/foundation/openjibo-managed-foundation.bicep}"
      shift 2
      ;;
    --state-connection-string)
      state_connection_string="${2:-}"
      shift 2
      ;;
    --personal-memory-connection-string)
      personal_memory_connection_string="${2:-}"
      shift 2
      ;;
    --open-weather-api-key)
      open_weather_api_key="${2:-}"
      shift 2
      ;;
    --news-api-key)
      news_api_key="${2:-}"
      shift 2
      ;;
    --search-backend)
      search_backend="${2:-}"
      shift 2
      ;;
    --search-fallback)
      search_fallback="${2:-}"
      shift 2
      ;;
    --postgres-admin-login)
      postgres_admin_login="${2:-openjiboadmin}"
      shift 2
      ;;
    --postgres-admin-password)
      postgres_admin_password="${2:-}"
      shift 2
      ;;
    --postgres-server-name)
      postgres_server_name="${2:-}"
      shift 2
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 2
      ;;
  esac
done

if [[ -z "$resource_group_name" ]]; then
  echo "--resource-group-name is required" >&2
  exit 2
fi

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

if [[ ! -f "$resolved_template_path" ]]; then
  echo "Could not find Bicep template at $resolved_template_path" >&2
  exit 1
fi

existing_managed_key_vault_name=""
if command -v az >/dev/null 2>&1; then
  existing_managed_key_vault_name="$(az keyvault list \
    --resource-group "$resource_group_name" \
    --query "[?starts_with(name, 'kv-')].name | [0]" \
    --output tsv 2>/dev/null || true)"
fi

if [[ -z "$postgres_admin_password" ]]; then
  if [[ -n "$existing_managed_key_vault_name" ]]; then
    echo "Found existing managed Key Vault '${existing_managed_key_vault_name}'; checking for a stored PostgreSQL admin password." >&2
    postgres_admin_password="$(az keyvault secret show \
      --vault-name "$existing_managed_key_vault_name" \
      --name openjibo-postgres-admin-password \
      --query value \
      --output tsv 2>/dev/null || true)"
    if [[ -n "$postgres_admin_password" ]]; then
      echo "Reusing existing PostgreSQL admin password from Key Vault." >&2
    fi
  fi
fi

if [[ -z "$postgres_admin_password" ]]; then
  postgres_admin_password="$(python3 - <<'PY'
import secrets
import string

alphabet = string.ascii_letters + string.digits + "!#$%+?@_"
password = [
    secrets.choice(string.ascii_lowercase),
    secrets.choice(string.ascii_uppercase),
    secrets.choice(string.digits),
    secrets.choice("!#$%+?@_"),
]
password.extend(secrets.choice(alphabet) for _ in range(28))
secrets.SystemRandom().shuffle(password)
print("".join(password))
PY
)"
  if [[ -z "$existing_managed_key_vault_name" ]]; then
    echo "No existing managed Key Vault was found; generating the initial PostgreSQL admin password." >&2
    echo "Generated a new PostgreSQL admin password for the initial foundation deployment." >&2
  else
    echo "Existing managed Key Vault '${existing_managed_key_vault_name}' did not return a stored PostgreSQL admin password." >&2
    echo "Managed Key Vault did not return a stored PostgreSQL password; generated a replacement for the foundation deployment." >&2
  fi
fi

deployment_runner_ip=""
if command -v curl >/dev/null 2>&1; then
  deployment_runner_ip="$(curl -fsS --max-time 10 https://api.ipify.org 2>/dev/null || true)"
  if [[ ! "$deployment_runner_ip" =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}$ ]]; then
    deployment_runner_ip=""
  fi
fi

deployment_name="openjibo-foundation-$(date -u +%s)"

current_principal_id="$(python3 -c 'import base64, json, subprocess, sys
try:
    token = subprocess.check_output(["az", "account", "get-access-token", "--query", "accessToken", "--output", "tsv"], text=True).strip()
    payload = token.split(".")[1]
    payload += "=" * (-len(payload) % 4)
    print(json.loads(base64.urlsafe_b64decode(payload.encode("utf-8"))).get("oid", ""))
except Exception as exc:
    print(f"Warning: could not resolve current Azure principal object id: {exc}", file=sys.stderr)')"

deployment_args=(
  az deployment group create
  --resource-group "$resource_group_name"
  --name "$deployment_name"
  --template-file "$resolved_template_path"
  --parameters "postgresAdministratorLogin=${postgres_admin_login}"
  --parameters "postgresAdministratorPassword=${postgres_admin_password}"
)

if [[ -n "$postgres_server_name" ]]; then
  deployment_args+=(--parameters "postgresServerName=${postgres_server_name}")
fi

if [[ -n "$deployment_runner_ip" ]]; then
  deployment_args+=(--parameters "postgresDeploymentRunnerFirewallIpAddress=${deployment_runner_ip}")
fi

if [[ -n "$current_principal_id" ]]; then
  deployment_args+=(--parameters "seedPrincipalObjectId=${current_principal_id}")
fi

deployment_args+=(--output json)

echo "Deploying Open Jibo managed foundation to resource group '${resource_group_name}'" >&2
deployment_json="$("${deployment_args[@]}")"

python3 - "$deployment_json" "$resource_group_name" "$state_connection_string" "$personal_memory_connection_string" "$open_weather_api_key" "$news_api_key" "$search_backend" "$search_fallback" "$current_principal_id" "$postgres_admin_password" <<'PY'
import json
import secrets
import subprocess
import sys
import time

deployment_json = json.loads(sys.argv[1])
resource_group_name = sys.argv[2]
state_connection_string = sys.argv[3]
personal_memory_connection_string = sys.argv[4]
open_weather_api_key = sys.argv[5]
news_api_key = sys.argv[6]
search_backend = sys.argv[7]
search_fallback = sys.argv[8]
current_principal_id = sys.argv[9]
postgres_admin_password = sys.argv[10]
outputs = deployment_json["properties"]["outputs"]
key_vault_name = outputs["keyVaultName"]["value"]
storage_account_name = outputs["storageAccountName"]["value"]
speech_services_account_name = outputs["speechServicesAccountName"]["value"]
postgres_host = outputs["postgresFullyQualifiedDomainName"]["value"]
postgres_server_name = outputs["postgresServerName"]["value"]
postgres_login = outputs["postgresAdministratorLogin"]["value"]
state_database_name = outputs["postgresStateDatabaseName"]["value"]
personal_memory_database_name = outputs["postgresPersonalMemoryDatabaseName"]["value"]


def run_command_with_retry(command: list[str], description: str, attempts: int = 8) -> str:
    for attempt in range(1, attempts + 1):
        result = subprocess.run(command, capture_output=True, text=True)
        if result.returncode == 0:
            return result.stdout.strip()

        if attempt == attempts:
            if result.stdout.strip():
                print(result.stdout.strip(), file=sys.stderr)
            if result.stderr.strip():
                print(result.stderr.strip(), file=sys.stderr)
            raise subprocess.CalledProcessError(result.returncode, command, result.stdout, result.stderr)

        wait_seconds = min(90, attempt * 10)
        detail = (result.stderr or result.stdout or "").strip().splitlines()
        suffix = f" Last Azure CLI message: {detail[-1]}" if detail else ""
        print(f"{description} failed; retrying in {wait_seconds} seconds.{suffix}", file=sys.stderr)
        time.sleep(wait_seconds)

    raise RuntimeError(f"Unexpected retry loop exit for {description}.")


def set_secret(name: str, value: str) -> None:
    if not value.strip():
        return

    run_command_with_retry(
        [
            "az", "keyvault", "secret", "set",
            "--vault-name", key_vault_name,
            "--name", name,
            "--value", value,
        ],
        f"Key Vault secret set for '{name}'",
        attempts=6,
    )


def set_secret_if_changed(name: str, value: str) -> None:
    if not value.strip():
        return

    result = subprocess.run(
        [
            "az", "keyvault", "secret", "show",
            "--vault-name", key_vault_name,
            "--name", name,
            "--query", "value",
            "--output", "tsv",
        ],
        capture_output=True,
        text=True,
    )
    existing_value = result.stdout.strip() if result.returncode == 0 else ""
    if existing_value and existing_value == value:
        print(f"Key Vault secret '{name}' already matches the desired value; skipping write.", file=sys.stderr)
        return

    if not existing_value:
        print(f"Key Vault secret '{name}' is missing or unreadable; creating or refreshing it.", file=sys.stderr)
    else:
        print(f"Key Vault secret '{name}' changed; refreshing it.", file=sys.stderr)

    set_secret(name, value)


def get_or_create_random_secret(name: str, byte_count: int = 32) -> str:
    result = subprocess.run(
        [
            "az", "keyvault", "secret", "show",
            "--vault-name", key_vault_name,
            "--name", name,
            "--query", "value",
            "--output", "tsv",
        ],
        capture_output=True,
        text=True,
    )
    existing_value = result.stdout.strip() if result.returncode == 0 else ""
    if existing_value:
        return existing_value

    value = secrets.token_urlsafe(byte_count)
    set_secret(name, value)
    print(f"Created managed secret '{name}'.", file=sys.stderr)
    return value


def postgres_connection_string(database_name: str) -> str:
    return (
        f"Host={postgres_host};Port=5432;Database={database_name};"
        f"Username={postgres_login};Password={postgres_admin_password};"
        "SSL Mode=Require;Trust Server Certificate=true"
    )


def sync_postgres_admin_password() -> None:
    if not postgres_server_name.strip() or not postgres_admin_password.strip():
        return

    print(
        f"Synchronizing PostgreSQL server admin password for '{postgres_server_name}' with the selected foundation password.",
        file=sys.stderr,
    )
    run_command_with_retry(
        [
            "az", "postgres", "flexible-server", "update",
            "--resource-group", resource_group_name,
            "--name", postgres_server_name,
            "--admin-password", postgres_admin_password,
            "--output", "none",
        ],
        f"PostgreSQL admin password synchronization for '{postgres_server_name}'",
        attempts=6,
    )
    print(f"PostgreSQL server admin password synchronized for '{postgres_server_name}'.", file=sys.stderr)


storage_connection_string = run_command_with_retry(
    [
        "az", "storage", "account", "show-connection-string",
        "--resource-group", resource_group_name,
        "--name", storage_account_name,
        "--query", "connectionString",
        "--output", "tsv",
    ],
    "Storage connection string lookup",
)

speech_subscription_key = run_command_with_retry(
    [
        # az cognitiveservices account keys list
        "az", "cognitiveservices", "account", "keys", "list",
        "--resource-group", resource_group_name,
        "--name", speech_services_account_name,
        "--query", "key1",
        "--output", "tsv",
    ],
    "Azure Speech subscription key lookup",
)

sync_postgres_admin_password()

set_secret_if_changed("openjibo-state-connection-string", state_connection_string or postgres_connection_string(state_database_name))
set_secret_if_changed("openjibo-personal-memory-connection-string", personal_memory_connection_string or postgres_connection_string(personal_memory_database_name))
set_secret_if_changed("openjibo-media-connection-string", storage_connection_string)
set_secret_if_changed("azure-speech-subscription-key", speech_subscription_key)
set_secret_if_changed("openjibo-postgres-admin-password", postgres_admin_password)
set_secret_if_changed("openjibo-openweather-api-key", open_weather_api_key)
set_secret_if_changed("openjibo-newsapi-key", news_api_key)
set_secret_if_changed("openjibo-search-backend", search_backend)
set_secret_if_changed("openjibo-search-fallback", search_fallback)
get_or_create_random_secret("openjibo-user-encrypt", 48)
get_or_create_random_secret("openjibo-user-salt", 24)
get_or_create_random_secret("openjibo-portal-status-password", 32)
get_or_create_random_secret("openjibo-peer-sync-shared-key", 48)

print(json.dumps(deployment_json, indent=2))
PY
