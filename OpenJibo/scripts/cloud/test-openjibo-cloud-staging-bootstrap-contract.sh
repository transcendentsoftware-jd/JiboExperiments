#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/../.." && pwd)"
workflow_path="${repo_root}/../.github/workflows/openjibo-cloud-staging-postgres-bootstrap.yml"
bootstrap_path="${repo_root}/scripts/cloud/bootstrap-openjibo-cloud-staging-postgres.sh"

for required_file in "$workflow_path" "$bootstrap_path"; do
  if [[ ! -f "$required_file" ]]; then
    echo "Missing required staging bootstrap file: $required_file" >&2
    exit 1
  fi
done

workflow_text="$(cat "$workflow_path")"
bootstrap_text="$(cat "$bootstrap_path")"

workflow_markers=(
  "bootstrap_confirmed"
  "environment:"
  "name: openjibo-staging"
  "id-token: write"
  "OPENJIBO_BOOTSTRAP_CONFIRMED: \"true\""
  "bootstrap-openjibo-cloud-staging-postgres.sh"
)
for marker in "${workflow_markers[@]}"; do
  if [[ "$workflow_text" != *"$marker"* ]]; then
    echo "Staging bootstrap workflow is missing required marker: $marker" >&2
    exit 1
  fi
done

bootstrap_markers=(
  'expected_resource_group="rg-openjibo-staging"'
  'expected_database="openjibo_cloud"'
  'admin_secret_name="openjibo-postgres-admin-password"'
  'deployer_secret_name="openjibo-cloud-postgresql-deployer-password"'
  'deployer_role="openjibo_cloud_staging_deployer"'
  'api_secret_name="openjibo-cloud-postgresql-api-password"'
  'api_role="openjibo_cloud_staging_api"'
  'api_capability_role="openjibo_managed_api_runtime"'
  'metering_role="openjibo_usage_metering_runtime"'
  'reconciliation_role="openjibo_usage_reconciliation_runtime"'
  "tags.openjiboEnvironment=='staging'"
  'sslmode=verify-full'
  'sslrootcert=system'
  'flexible-server db create'
  '--name "$expected_database"'
  'NOLOGIN INHERIT NOSUPERUSER NOCREATEDB'
  'NOCREATEROLE NOREPLICATION NOBYPASSRLS'
  'WITH ADMIN OPTION'
  'WITH INHERIT TRUE, SET FALSE, ADMIN FALSE'
  'membership.inherit_option'
  'membership.set_option'
  "member_role.rolname = '\${postgres_admin}'"
  'firewall-rule create'
  'firewall-rule delete'
  '--server-name "$postgres_server"'
  '--name "$firewall_rule"'
  'trap cleanup EXIT'
  'PGPASSFILE'
)
for marker in "${bootstrap_markers[@]}"; do
  if [[ "$bootstrap_text" != *"$marker"* ]]; then
    echo "Staging bootstrap script is missing required marker: $marker" >&2
    exit 1
  fi
done

if [[ "$bootstrap_text" == *'set -x'* || "$bootstrap_text" == *'GITHUB_OUTPUT'* ||
      "$bootstrap_text" == *'PGPASSWORD'* ]]; then
  echo "Staging bootstrap script contains a forbidden credential exposure path." >&2
  exit 1
fi

bash -n "$bootstrap_path"
echo "OpenJiboCloud staging PostgreSQL bootstrap contract passed."
