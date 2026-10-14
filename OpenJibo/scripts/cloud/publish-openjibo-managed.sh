#!/usr/bin/env bash
set -euo pipefail

registry_name=""
image_name="openjibo-cloud"
tag="managed"
dockerfile_path="Dockerfile"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --registry-name)
      registry_name="${2:-}"
      shift 2
      ;;
    --image-name)
      image_name="${2:-openjibo-cloud}"
      shift 2
      ;;
    --tag)
      tag="${2:-managed}"
      shift 2
      ;;
    --dockerfile-path)
      dockerfile_path="${2:-Dockerfile}"
      shift 2
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 2
      ;;
  esac
done

if [[ -z "$registry_name" ]]; then
  echo "--registry-name is required" >&2
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

resolved_dockerfile_path="$(resolve_path "$dockerfile_path")"

if [[ ! -f "$resolved_dockerfile_path" ]]; then
  echo "Could not find Dockerfile at $resolved_dockerfile_path" >&2
  exit 1
fi

if ! role_assignment_mode="$(az acr show --name "$registry_name" --query roleAssignmentMode --output tsv --only-show-errors)"; then
  echo "Could not determine ACR role assignment mode for $registry_name" >&2
  exit 1
fi

acr_auth_args=()
case "$role_assignment_mode" in
  LegacyRegistryPermissions)
    ;;
  AbacRepositoryPermissions)
    acr_auth_args=(--source-acr-auth-id '[caller]')
    ;;
  *)
    echo "Unsupported ACR role assignment mode for $registry_name: $role_assignment_mode" >&2
    exit 1
    ;;
esac

echo "Building managed Open Jibo image in ACR: ${registry_name}.azurecr.io/${image_name}:${tag}"
cd "$repo_root"
# Managed deployments rely on Azure Speech, so skip baking in whisper.cpp and its model.
exec az acr build --registry "$registry_name" --image "${image_name}:${tag}" --file "$resolved_dockerfile_path" --build-arg ENABLE_LOCAL_WHISPER=false "${acr_auth_args[@]}" "$repo_root"
