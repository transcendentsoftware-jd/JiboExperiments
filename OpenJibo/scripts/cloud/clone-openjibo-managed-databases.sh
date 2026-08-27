#!/usr/bin/env bash
set -euo pipefail
umask 077

source_resource_group=""
target_resource_group=""
source_key_vault_name=""
target_key_vault_name=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --source-resource-group)
      source_resource_group="${2:-}"
      shift 2
      ;;
    --target-resource-group)
      target_resource_group="${2:-}"
      shift 2
      ;;
    --source-key-vault-name)
      source_key_vault_name="${2:-}"
      shift 2
      ;;
    --target-key-vault-name)
      target_key_vault_name="${2:-}"
      shift 2
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 2
      ;;
  esac
done

for required_name in source_resource_group target_resource_group target_key_vault_name; do
  if [[ -z "${!required_name}" ]]; then
    echo "--${required_name//_/-} is required" >&2
    exit 2
  fi
done

if [[ -z "$source_key_vault_name" ]]; then
  echo "--source-key-vault-name is required to avoid cloning from an ambiguous vault." >&2
  exit 2
fi

if [[ "$source_resource_group" == "$target_resource_group" ]]; then
  echo "Source and target resource groups must be different." >&2
  exit 1
fi

for command_name in az curl pg_dump pg_restore; do
  if ! command -v "$command_name" >/dev/null 2>&1; then
    echo "Required command '$command_name' is not installed." >&2
    exit 1
  fi
done

read_required_secret() {
  local vault_name="$1"
  local secret_name="$2"
  local value
  value="$(az keyvault secret show     --vault-name "$vault_name"     --name "$secret_name"     --query value     --output tsv)"
  if [[ -z "$value" ]]; then
    echo "Required secret '$secret_name' is empty in Key Vault '$vault_name'." >&2
    exit 1
  fi
  printf '%s' "$value"
}

connection_value() {
  local connection_string="$1"
  local wanted_key="${2,,}"
  local segment
  local key
  local value

  IFS=';' read -ra segments <<<"$connection_string"
  for segment in "${segments[@]}"; do
    key="${segment%%=*}"
    value="${segment#*=}"
    key="${key//[[:space:]]/}"
    if [[ "${key,,}" == "$wanted_key" ]]; then
      printf '%s' "$value"
      return 0
    fi
  done

  echo "Connection string does not contain '$2'." >&2
  return 1
}

source_state="$(read_required_secret "$source_key_vault_name" openjibo-state-connection-string)"
source_memory="$(read_required_secret "$source_key_vault_name" openjibo-personal-memory-connection-string)"
source_user_encryption_passphrase="$(read_required_secret "$source_key_vault_name" openjibo-user-encrypt)"
source_user_encryption_salt="$(read_required_secret "$source_key_vault_name" openjibo-user-salt)"
target_state="$(read_required_secret "$target_key_vault_name" openjibo-state-connection-string)"
target_memory="$(read_required_secret "$target_key_vault_name" openjibo-personal-memory-connection-string)"

source_host="$(connection_value "$source_state" Host)"
target_host="$(connection_value "$target_state" Host)"
if [[ "${source_host,,}" == "${target_host,,}" ]]; then
  echo "Refusing to clone because source and target PostgreSQL hosts are identical." >&2
  exit 1
fi

runner_ip="$(curl -fsS --max-time 10 https://api.ipify.org)"
if [[ ! "$runner_ip" =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}$ ]]; then
  echo "Could not resolve a valid deployment runner IPv4 address." >&2
  exit 1
fi

source_server="${source_host%%.*}"
target_server="${target_host%%.*}"
rule_suffix="${GITHUB_RUN_ID:-manual}"
rule_name="AllowStagingClone-${rule_suffix:0:40}"
temporary_directory="$(mktemp -d)"
cleanup_complete=false

remove_rule() {
  local resource_group="$1"
  local server_name="$2"
  az postgres flexible-server firewall-rule delete     --resource-group "$resource_group"     --server-name "$server_name"     --name "$rule_name"     --yes     --output none >/dev/null 2>&1 || true
}

cleanup() {
  if [[ "$cleanup_complete" == true ]]; then
    return
  fi
  cleanup_complete=true
  remove_rule "$source_resource_group" "$source_server"
  remove_rule "$target_resource_group" "$target_server"
  rm -rf -- "$temporary_directory"
}
trap cleanup EXIT

create_rule() {
  local resource_group="$1"
  local server_name="$2"
  az postgres flexible-server firewall-rule create     --resource-group "$resource_group"     --server-name "$server_name"     --name "$rule_name"     --start-ip-address "$runner_ip"     --end-ip-address "$runner_ip"     --output none >/dev/null
}

create_rule "$source_resource_group" "$source_server"
create_rule "$target_resource_group" "$target_server"

dump_database() {
  local connection_string="$1"
  local output_path="$2"
  (
    export PGHOST="$(connection_value "$connection_string" Host)"
    export PGPORT="$(connection_value "$connection_string" Port)"
    export PGDATABASE="$(connection_value "$connection_string" Database)"
    export PGUSER="$(connection_value "$connection_string" Username)"
    export PGPASSWORD="$(connection_value "$connection_string" Password)"
    export PGSSLMODE=require
    pg_dump --format=custom --no-owner --no-acl --file "$output_path"
  )
}

restore_database() {
  local connection_string="$1"
  local input_path="$2"
  (
    export PGHOST="$(connection_value "$connection_string" Host)"
    export PGPORT="$(connection_value "$connection_string" Port)"
    export PGDATABASE="$(connection_value "$connection_string" Database)"
    export PGUSER="$(connection_value "$connection_string" Username)"
    export PGPASSWORD="$(connection_value "$connection_string" Password)"
    export PGSSLMODE=require
    pg_restore       --clean       --if-exists       --no-owner       --no-acl       --exit-on-error       --dbname "$PGDATABASE"       "$input_path"
  )
}

echo "Cloning production state into isolated staging PostgreSQL."
dump_database "$source_state" "$temporary_directory/state.dump"
restore_database "$target_state" "$temporary_directory/state.dump"

echo "Cloning production personal memory into isolated staging PostgreSQL."
dump_database "$source_memory" "$temporary_directory/personal-memory.dump"
restore_database "$target_memory" "$temporary_directory/personal-memory.dump"

echo "Aligning staging encryption secrets with the cloned production records."
printf '%s' "$source_user_encryption_passphrase" > "$temporary_directory/openjibo-user-encrypt"
printf '%s' "$source_user_encryption_salt" > "$temporary_directory/openjibo-user-salt"
az keyvault secret set \
  --vault-name "$target_key_vault_name" \
  --name openjibo-user-encrypt \
  --file "$temporary_directory/openjibo-user-encrypt" \
  --output none
az keyvault secret set \
  --vault-name "$target_key_vault_name" \
  --name openjibo-user-salt \
  --file "$temporary_directory/openjibo-user-salt" \
  --output none

echo "Production database clone completed without changing the source databases."
