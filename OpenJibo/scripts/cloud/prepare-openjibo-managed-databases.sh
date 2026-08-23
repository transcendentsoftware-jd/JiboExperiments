#!/usr/bin/env bash
set -euo pipefail

key_vault_name=""
preview_only=false
verbose=false

while [[ $# -gt 0 ]]; do
  case "$1" in
    --key-vault-name)
      key_vault_name="${2:-}"
      shift 2
      ;;
    --preview)
      preview_only=true
      shift
      ;;
    --verbose)
      verbose=true
      shift
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 2
      ;;
  esac
done

if [[ -z "$key_vault_name" ]]; then
  echo "--key-vault-name is required" >&2
  exit 2
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

read_required_secret() {
  local secret_name="$1"
  local value
  value="$(az keyvault secret show     --vault-name "$key_vault_name"     --name "$secret_name"     --query value     --output tsv)"

  if [[ -z "$value" ]]; then
    echo "Required Key Vault secret '$secret_name' is empty." >&2
    exit 1
  fi

  printf '%s' "$value"
}

export OpenJibo__State__ConnectionString
OpenJibo__State__ConnectionString="$(read_required_secret openjibo-state-connection-string)"
export OpenJibo__PersonalMemory__ConnectionString
OpenJibo__PersonalMemory__ConnectionString="$(read_required_secret openjibo-personal-memory-connection-string)"
export OpenJibo__Media__ConnectionString
OpenJibo__Media__ConnectionString="$(read_required_secret openjibo-media-connection-string)"
export OPENJIBO_USER_ENCRYPT
OPENJIBO_USER_ENCRYPT="$(read_required_secret openjibo-user-encrypt)"
export OPENJIBO_USER_SALT
OPENJIBO_USER_SALT="$(read_required_secret openjibo-user-salt)"

arguments=(
  --target all
  --import-legacy-cloud-state
  --import-legacy-personal-memory
  --verify
)

if [[ "$verbose" == true ]]; then
  arguments+=(--verbose)
fi

if [[ "$preview_only" == true ]]; then
  arguments+=(--preview)
  echo "Previewing managed database preparation for Key Vault '$key_vault_name'."
else
  echo "Applying and verifying managed database preparation for Key Vault '$key_vault_name'."
fi

bash "${script_dir}/invoke-openjibo-migration.sh" "${arguments[@]}"