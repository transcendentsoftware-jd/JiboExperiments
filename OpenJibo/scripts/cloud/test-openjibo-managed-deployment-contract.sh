#!/usr/bin/env bash
set -euo pipefail

python_command="${PYTHON_COMMAND:-python3}"

foundation_template_path="infra/azure/foundation/openjibo-managed-foundation.bicep"
managed_template_path="infra/azure/container-apps/openjibo-managed.bicep"
workflow_path="../.github/workflows/openjibo-cloud-managed-deploy.yml"
foundation_script_path="scripts/cloud/Deploy-OpenJiboManagedFoundation.ps1"
managed_script_path="scripts/cloud/Deploy-OpenJiboManaged.ps1"
linux_foundation_script_path="scripts/cloud/deploy-openjibo-managed-foundation.sh"
linux_publish_script_path="scripts/cloud/publish-openjibo-managed.sh"
linux_managed_script_path="scripts/cloud/deploy-openjibo-managed.sh"
linux_prepare_script_path="scripts/cloud/prepare-openjibo-managed-databases.sh"
linux_clone_script_path="scripts/cloud/clone-openjibo-managed-databases.sh"
release_smoke_cleanup_script_path="scripts/cloud/cleanup-release-smoke-authorization.sh"
production_database_binding_script_path="scripts/cloud/verify-openjibo-production-database-binding.py"
smoke_script_path="scripts/cloud/Invoke-CloudSmoke.ps1"
linux_smoke_script_path="scripts/cloud/invoke-cloud-smoke.sh"
dockerfile_path="Dockerfile"

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/../.." && pwd)"

get_repo_file_text() {
  local relative_path="$1"
  local full_path="${repo_root}/${relative_path}"

  if [[ ! -f "$full_path" ]]; then
    echo "Missing required file: $full_path" >&2
    exit 1
  fi

  cat "$full_path"
}

foundation_text="$(get_repo_file_text "$foundation_template_path")"
managed_text="$(get_repo_file_text "$managed_template_path")"
workflow_text="$(get_repo_file_text "$workflow_path")"
workflow_dispatch_input_count="$(
  printf '%s\n' "$workflow_text" |
    sed -n '/^    inputs:$/,/^concurrency:$/p' |
    grep -Ec '^      [A-Za-z0-9_]+:$' || true
)"
if (( workflow_dispatch_input_count > 25 )); then
  echo "GitHub workflow_dispatch supports at most 25 inputs; found ${workflow_dispatch_input_count}." >&2
  exit 1
fi
release_smoke_cleanup_text="$(get_repo_file_text "$release_smoke_cleanup_script_path")"
production_database_binding_script_text="$(get_repo_file_text "$production_database_binding_script_path")"
foundation_script_text="$(get_repo_file_text "$foundation_script_path")"
managed_script_text="$(get_repo_file_text "$managed_script_path")"
linux_foundation_script_text="$(get_repo_file_text "$linux_foundation_script_path")"
linux_publish_script_text="$(get_repo_file_text "$linux_publish_script_path")"
linux_managed_script_text="$(get_repo_file_text "$linux_managed_script_path")"
linux_prepare_script_text="$(get_repo_file_text "$linux_prepare_script_path")"
linux_clone_script_text="$(get_repo_file_text "$linux_clone_script_path")"
smoke_script_text="$(get_repo_file_text "$smoke_script_path")"
linux_smoke_script_text="$(get_repo_file_text "$linux_smoke_script_path")"
dockerfile_text="$(get_repo_file_text "$dockerfile_path")"

required_foundation_markers=(
  "output keyVaultName string"
  "output registryName string"
  "output storageAccountName string"
  "resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01'"
  "param storageAccountName string = ''"
  "var resolvedStorageAccountName"
  "resource speechServicesAccount 'Microsoft.CognitiveServices/accounts@2023-05-01'"
  "param speechServicesAccountName string = ''"
  "output speechServicesAccountName string"
  "resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01'"
  "publicNetworkAccess: 'Enabled'"
  "accessPolicies: []"
  "enableRbacAuthorization: false"
  "param seedPrincipalObjectId string = ''"
  "resource keyVaultSecretSeedAccessPolicy 'Microsoft.KeyVault/vaults/accessPolicies@2023-07-01'"
  "resource postgresServer 'Microsoft.DBforPostgreSQL/flexibleServers@2023-06-01-preview'"
  "resource postgresStateDatabase 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2023-06-01-preview'"
  "resource postgresPersonalMemoryDatabase 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2023-06-01-preview'"
  "resource postgresAllowAzureServicesFirewallRule 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2023-06-01-preview'"
  "resource applicationInsights 'Microsoft.Insights/components@2020-02-02'"
  "output applicationInsightsName string"
  "param postgresDeploymentRunnerFirewallIpAddress string = ''"
  "output postgresFullyQualifiedDomainName string"
  "output postgresStateDatabaseName string"
  "output postgresPersonalMemoryDatabaseName string"
)

required_managed_markers=(
  "param registryLoginServer string"
  "param keyVaultName string"
  "param apiHostname string = 'api.openjibo.com'"
  "param socketHostname string = 'open-jibo-socket.openjibo.com'"
  "param neoHubHostname string = 'neohub.openjibo.com'"
  "param nativeCompatibilityApiHostname string = 'open-jibo.jibo.pro'"
  "param nativeCompatibilitySocketHostname string = 'open-jibo-socket.jibo.pro'"
  "param enableAzureSpeech bool = true"
  "param azureSpeechRegion string = location"
  "resource applicationInsights 'Microsoft.Insights/components@2020-02-02' existing"
  "APPLICATIONINSIGHTS_CONNECTION_STRING"
  "APPLICATIONINSIGHTS_METRIC_NAMESPACE_OPT_IN"
  "param statePostgreSqlMaxPoolSize int = 8"
  "param personalMemoryPostgreSqlMaxPoolSize int = 4"
  "@maxValue(12)"
  "@maxValue(6)"
  "OpenJibo__State__PostgreSql__MaxPoolSize"
  "OpenJibo__PersonalMemory__PostgreSql__MaxPoolSize"
  "OpenJibo__CanonicalApiHostname"
  "OpenJibo__CanonicalApiBaseUrl"
  "OpenJibo__CanonicalSocketHostname"
  "OpenJibo__CanonicalNeoHubHostname"
  "OpenJibo__NativeCompatibilityApiHostname"
  "OpenJibo__NativeCompatibilitySocketHostname"
  "output canonicalApiHostname string"
  "output canonicalSocketHostname string"
  "output canonicalNeoHubHostname string"
  "output nativeCompatibilityApiHostname string"
  "output nativeCompatibilitySocketHostname string"
  "output canonicalSocketBaseUrl string"
  "output canonicalNeoHubBaseUrl string"
  "output containerAppName string"
  "output managedEnvironmentName string"
  "OpenJibo__Stt__EnableAzureSpeech"
  "azure-speech-subscription-key"
  "OpenJibo__Stt__AzureSpeechSubscriptionKey"
  "param stateConnectionString string = ''"
  "param personalMemoryConnectionString string = ''"
  "param mediaConnectionString string = ''"
  "param openWeatherApiKey string = ''"
  "param newsApiKey string = ''"
  "param searchBackend string = ''"
  "param searchFallback string = ''"
  "param portalStatusPassword string = ''"
  "param sigV4ReplayHmacKey string = ''"
  "param sigV4ReplayHmacKeyPrevious string = ''"
  "param sigV4ReplayObservationKeyVersion int = 1"
  "param sigV4ReplayObservationPreviousKeyVersion int = 0"
  "param sigV4ReplayObservationConnectionString string = ''"
  "param sigV4ReplayObservationEnabled bool = false"
  "value: stateConnectionString"
  "value: personalMemoryConnectionString"
  "value: mediaConnectionString"
  "value: openWeatherApiKey"
  "value: newsApiKey"
  "value: searchBackend"
  "value: searchFallback"
  "value: portalStatusPassword"
  "OpenJibo__Portal__StatusPassword"
  "OpenJibo__Security__SigV4ReplayHmacKey"
  "OpenJibo__Security__SigV4ReplayObservation__ConnectionString"
  "OpenJibo__Security__SigV4ReplayObservation__Enabled"
  "OpenJibo__Security__SigV4ReplayObservation__KeyVersion"
  "OpenJibo__Security__SigV4ReplayObservation__PreviousKeyVersion"
  "OpenJibo__Security__SigV4ReplayObservation__PreviousHmacKey"
  "portal-status-password"
  "sigv4-replay-hmac-key"
  "sigv4-replay-hmac-key-previous"
  "sigv4-replay-observer-connection-string"
  "search-backend"
  "search-fallback"
  "var logAnalyticsWorkspaceKey"
  "value: 'PostgreSql'"
  "value: 'AzureBlob'"
  "keyVaultContainerAppSecretAccessPolicy"
  "OpenJibo__Deployment__Mode"
  "OPENJIBO_SEARCH_BACKEND"
  "OPENJIBO_SEARCH_FALLBACK"
  "OpenJibo__Deployment__PostgreSqlServerName"
  "OpenJibo__Deployment__StateDatabaseName"
  "OpenJibo__Deployment__PersonalMemoryDatabaseName"
)

required_workflow_markers=(
  "shell: bash"
  "working-directory: OpenJibo"
  "deploy-openjibo-managed-foundation.sh"
  "deploy-openjibo-managed.sh"
  "publish-openjibo-managed.sh"
  "steps.foundation.outputs.registryName"
  "steps.foundation.outputs.keyVaultName"
  "inputs.location"
  "existing_log_analytics_workspace_name"
  "existing_container_registry_name"
  "existing_key_vault_name"
  "existing_storage_account_name"
  "existing_postgres_server_name"
  "existing_speech_services_account_name"
  "Specify every existing foundation resource name together"
  "Production promotion requires all six pinned existing foundation resource names"
  "Verify existing production database binding"
  "verify-openjibo-production-database-binding.py"
  "production_database_binding_bootstrap_confirmed"
  "if ! app_names_json="
  "Production Container App must be named openjibo-cloud"
  "az postgres flexible-server show"
  "containerapp revision list"
  "openjiboDatabaseBindingBootstrapCompleted"
  "Record production database binding bootstrap"
  "api_hostname"
  "socket_hostname"
  "neohub_hostname"
  "api.openjibo.com"
  "open-jibo-socket.openjibo.com"
  "neohub.openjibo.com"
  "OPENJIBO_SEARCH_BACKEND"
  "OPENJIBO_SEARCH_FALLBACK"
  "--api-hostname"
  "--socket-hostname"
  "--neohub-hostname"
  "enable_azure_speech"
  "azure_speech_region"
  "--search-backend"
  "--search-fallback"
  "--run-migration"
  "--run-smoke"
)

for marker in "${required_foundation_markers[@]}"; do
  if [[ "$foundation_text" != *"$marker"* ]]; then
    echo "Foundation template is missing expected marker: $marker" >&2
    exit 1
  fi
done

for marker in "${required_managed_markers[@]}"; do
  if [[ "$managed_text" != *"$marker"* ]]; then
    echo "Managed template is missing expected marker: $marker" >&2
    exit 1
  fi
done

for marker in "${required_workflow_markers[@]}"; do
  if [[ "$workflow_text" != *"$marker"* ]]; then
    echo "Workflow is missing expected marker: $marker" >&2
    exit 1
  fi
done

valid_binding_fixture='[{"properties":{"active":true,"trafficWeight":100,"template":{"containers":[{"env":[
  {"name":"OpenJibo__Deployment__PostgreSqlServerName","value":"psql-current"},
  {"name":"OpenJibo__Deployment__StateDatabaseName","value":"openjibo_state"},
  {"name":"OpenJibo__Deployment__PersonalMemoryDatabaseName","value":"openjibo_memory"}
]}]}}}]'
if ! printf '%s' "$valid_binding_fixture" |
    "$python_command" "${repo_root}/${production_database_binding_script_path}" \
      psql-current openjibo_state openjibo_memory false true >/dev/null; then
  echo "Production database binding validator rejected a matching server." >&2
  exit 1
fi

if printf '%s' "$valid_binding_fixture" |
    "$python_command" "${repo_root}/${production_database_binding_script_path}" \
      psql-replacement openjibo_state openjibo_memory false true >/dev/null 2>&1; then
  echo "Production database binding validator accepted a server rebind." >&2
  exit 1
fi

incomplete_binding_fixture='[{"properties":{"active":true,"trafficWeight":100,"template":{"containers":[{"env":[
  {"name":"OpenJibo__Deployment__PostgreSqlServerName","value":"psql-current"}
]}]}}}]'
if printf '%s' "$incomplete_binding_fixture" |
    "$python_command" "${repo_root}/${production_database_binding_script_path}" \
      psql-current openjibo_state openjibo_memory true false >/dev/null 2>&1; then
  echo "Production database binding validator accepted incomplete identity markers." >&2
  exit 1
fi

bootstrap_binding_fixture='[{"properties":{"active":true,"trafficWeight":100,"template":{"containers":[{"env":[]}]}}}]'
if printf '%s' "$bootstrap_binding_fixture" |
    "$python_command" "${repo_root}/${production_database_binding_script_path}" \
      psql-current openjibo_state openjibo_memory false false >/dev/null 2>&1; then
  echo "Production database binding validator bootstrapped without explicit confirmation." >&2
  exit 1
fi
if ! printf '%s' "$bootstrap_binding_fixture" |
    "$python_command" "${repo_root}/${production_database_binding_script_path}" \
      psql-current openjibo_state openjibo_memory true false >/dev/null; then
  echo "Production database binding validator rejected explicit first-deploy confirmation." >&2
  exit 1
fi
if printf '%s' "$bootstrap_binding_fixture" |
    "$python_command" "${repo_root}/${production_database_binding_script_path}" \
      psql-current openjibo_state openjibo_memory true true >/dev/null 2>&1; then
  echo "Production database binding validator repeated a completed bootstrap." >&2
  exit 1
fi

if [[ "$workflow_text" == *"--show-values"* || "$workflow_text" == *"mapfile -t app_names"* ]]; then
  echo "Production database preflight must not export secret values or fail open through process substitution." >&2
  exit 1
fi

for marker in "openjibo-media-connection-string" "azure-speech-subscription-key" "cognitiveservices account keys list" "speechServicesAccountName" "openjibo-postgres-admin-password" "openjibo-search-backend" "openjibo-search-fallback" "openjibo-portal-status-password" "openjibo-peer-sync-shared-key" "openjibo-sigv4-replay-hmac" "openjibo-sigv4-replay-observer-password" "openjibo-sigv4-replay-observer-connection-string" "postgresFullyQualifiedDomainName" "Invoke-OpenJiboAzWithRetry"; do
  if [[ "$foundation_script_text" != *"$marker"* ]]; then
    echo "Foundation script is missing expected marker: $marker" >&2
    exit 1
  fi
done

if [[ "$foundation_script_text" != *"seedPrincipalObjectId"* ]]; then
  echo "Foundation script does not pass the secret seed access policy principal to the deployment." >&2
  exit 1
fi

for marker in "RegistryName" "ApiHostname" "SocketHostname" "NeoHubHostname" "NativeCompatibilityApiHostname" "NativeCompatibilitySocketHostname" "AdditionalCompatibilityApiHostname" "open-jibo.jibo.pro" "open-jibo-socket.jibo.pro" "api.jibo.pro" "containerapp hostname add" "containerapp hostname bind" "SkipHostnameBinding" "EnableAzureSpeech" "AzureSpeechRegion" "portalStatusPassword" "openjibo-portal-status-password" "searchBackend" "searchFallback" "openjibo-search-backend" "openjibo-search-fallback"; do
  if [[ "$managed_script_text" != *"$marker"* ]]; then
    echo "Managed deploy script is missing expected marker: $marker" >&2
    exit 1
  fi
done

for marker in "managedEnvironmentName" "--environment" "--validation-method CNAME" "search-backend" "search-fallback"; do
  if [[ "$managed_script_text" != *"$marker"* ]]; then
    echo "Managed deploy script is missing hostname binding environment marker: $marker" >&2
    exit 1
  fi
done

for marker in "--log-analytics-workspace-name" "--container-registry-name" "--key-vault-name" "--storage-account-name" "--postgres-server-name" "--speech-services-account-name" 'f"--value={value}"' "seedPrincipalObjectId" "openjibo-media-connection-string" "openjibo-postgres-admin-password" "openjibo-search-backend" "openjibo-search-fallback" "postgresFullyQualifiedDomainName" "run_command_with_retry"; do
  if [[ "$linux_foundation_script_text" != *"$marker"* ]]; then
    echo "Linux foundation script is missing expected marker: $marker" >&2
    exit 1
  fi
done

storage_connection_marker='"az", "storage", "account", "show-connection-string"'
if [[ "$linux_foundation_script_text" != *"$storage_connection_marker"* ]]; then
  echo "Linux foundation script does not resolve the storage connection string outside Bicep outputs." >&2
  exit 1
fi

if [[ "$linux_publish_script_text" != *"az acr build"* ]]; then
  echo "Linux publish script is missing the ACR build path." >&2
  exit 1
fi

for marker in "--run-smoke" "--run-migration" "--api-hostname" "--socket-hostname" "--neohub-hostname" "--native-compatibility-api-hostname" "--native-compatibility-socket-hostname" "--additional-compatibility-api-hostname" "open-jibo.jibo.pro" "open-jibo-socket.jibo.pro" "api.jibo.pro" "az containerapp hostname add" "az containerapp hostname bind" 'prepare-openjibo-managed-databases.sh' "--skip-hostname-binding" "--enable-peer-sync" "--disable-peer-sync" "--peer-sync-allowed-hosts" "--enable-sigv4-replay-observation" "sigV4ReplayObservationEnabled" "sigV4ReplayObservationKeyVersion" "sigV4ReplayObservationPreviousKeyVersion" "openjibo-sigv4-replay-observer-connection-string" "openjibo-sigv4-replay-hmac-previous" "openjibo-sigv4-replay-hmac-key-version" "openjibo-sigv4-replay-hmac-previous-key-version" "sigv4-replay-hmac-key-previous" "peerSyncEnabled" "allowedPeerHosts" "portal-status-password" "openjibo-portal-status-password" "sigv4_replay_hmac_key" "sigv4_replay_hmac_key_previous" "sigV4ReplayHmacKey" "sigV4ReplayHmacKeyPrevious" "sigv4-replay-hmac-key" "validate_base64url_secret" "base64url_secrets_equal" "searchBackend" "searchFallback" "openjibo-search-backend" "openjibo-search-fallback" "run_command_with_retry" "parse_postgres_database_name" "stateDatabaseName" "personalMemoryDatabaseName"; do
  if [[ "$linux_managed_script_text" != *"$marker"* ]]; then
    echo "Linux managed deploy script is missing expected marker: $marker" >&2
    exit 1
  fi
done

for marker in "managedEnvironmentName" "--environment" "--validation-method CNAME" "search-backend" "search-fallback"; do
  if [[ "$linux_managed_script_text" != *"$marker"* ]]; then
    echo "Linux managed deploy script is missing hostname binding environment marker: $marker" >&2
    exit 1
  fi
done

for marker in "containerapp env show" "firewall-rule create" "firewall-rule update"; do
  if [[ "$linux_managed_script_text" != *"$marker"* ]]; then
    echo "Linux managed deploy script is missing firewall marker: $marker" >&2
    exit 1
  fi
done

for marker in "deployment_target" "openjibo-staging-gate" "clone-openjibo-managed-databases.sh" "keyVaultUrl" "keyVaultUri" "urlsplit" "openjibo-managed-" "containerAppName" "properties.outputs" "user-encryption-passphrase" "user-encryption-salt" "production_backup_confirmed" "fleet_peer_allowed_hosts" "Fleet peer sync cannot be enabled by the staging workflow" "--enable-peer-sync --peer-sync-allowed-hosts" "enable_sigv4_replay_observation" "SigV4 replay observation is staging-only" "--enable-sigv4-replay-observation" "--disable-sigv4-replay-observation" "Verify staging SigV4 replay observer configuration" "OpenJibo__Security__SigV4ReplayObservation__Enabled" "sigv4-replay-observer-connection-string" "sigv4-replay-hmac-key" "sigV4ReplayObservation" "enabled-shadow" "PREVIOUS_REPLAY_OBSERVATION_ENABLED" "backup.backupRetentionDays" "Verify production hostname DNS prerequisites" "customDomainVerificationId" "dig +short CNAME" "dig +short TXT" "open-jibo.jibo.pro" "open-jibo-socket.jibo.pro" "api.jibo.pro" "staging-api.jibo.pro" "properties.active" '[[ "$revision_active" == "true" ]]' '[[ "$previous_revision_active" != "true" ]]' "already active" "revision deactivate" "revision activate" "revision restart" "latestReadyRevisionName" "properties.runningState" '[[ "$latest_ready_revision" == "$configured_revision"' "PREVIOUS_REVISION" "Restore previous image after failure" "Re-disable release smoke authorization after rollback" "Run deployed WebSocket release smoke" "invoke-release-smoke.mjs" "cleanup-release-smoke-authorization.sh" "TEST_ROBOT_ID: open-jibo-smoke-staging" "openssl rand -hex 32" "release-smoke-authorization" "OpenJibo__ReleaseSmoke__Enabled=true" "OPENJIBO_RELEASE_SMOKE_ALLOWED_HOST" "cancel-in-progress: false" "webSocketReleaseSmoke"; do
  if [[ "$workflow_text" != *"$marker"* ]]; then
    echo "Workflow is missing staging or promotion safeguard: $marker" >&2
    exit 1
  fi
done
production_reject_line="$(grep -n -- "SigV4 replay observation is staging-only" <<<"$workflow_text" | head -1 | cut -d: -f1)"
deploy_invocation_line="$(grep -nF -- 'bash ./scripts/cloud/deploy-openjibo-managed.sh "${deploy_args[@]}"' <<<"$workflow_text" | head -1 | cut -d: -f1)"
verify_replay_line="$(grep -n -- "- name: Verify staging SigV4 replay observer configuration" <<<"$workflow_text" | head -1 | cut -d: -f1)"
promotion_gate_line="$(grep -n -- "- name: Create staging promotion gate" <<<"$workflow_text" | head -1 | cut -d: -f1)"
if [[ -z "$production_reject_line" || -z "$deploy_invocation_line" || -z "$verify_replay_line" || -z "$promotion_gate_line" ||
      ! ( "$production_reject_line" -lt "$deploy_invocation_line" && "$deploy_invocation_line" -lt "$verify_replay_line" && "$verify_replay_line" -lt "$promotion_gate_line" ) ]]; then
  echo "Replay observation must reject production before deployment and verify staging before promotion evidence is created." >&2
  exit 1
fi
rollback_line="$(grep -n -- "- name: Restore previous image after failure" <<<"$workflow_text" | head -1 | cut -d: -f1)"
rollback_replay_line="$(grep -nF -- '--set-env-vars "OpenJibo__Security__SigV4ReplayObservation__Enabled=${PREVIOUS_REPLAY_OBSERVATION_ENABLED:-false}"' <<<"$workflow_text" | head -1 | cut -d: -f1)"
if [[ -z "$rollback_line" || -z "$rollback_replay_line" || ! "$rollback_line" -lt "$rollback_replay_line" ]]; then
  echo "Rollback must restore the previous replay-observation enabled state." >&2
  exit 1
fi
configure_smoke_line="$(grep -n -- "OpenJibo__ReleaseSmoke__Enabled=true" <<<"$workflow_text" | head -1 | cut -d: -f1)"
for marker in "Capture staging scale before two-replica proof" "--min-replicas 2" '"$revision_running_state" == "RunningAtMaxScale"' "RELEASE_SMOKE_MIN_REPLICAS" "RELEASE_SMOKE_EXPECTED_REVISION" "minimumReplicasObserved" "crossReplicaCommittedRead" "Restore staging scale after two-replica proof"; do
  if [[ "$workflow_text" != *"$marker"* ]]; then
    echo "Workflow is missing two-replica staging safeguard: $marker" >&2
    exit 1
  fi
done
restart_smoke_line="$(grep -n -- "az containerapp revision restart" <<<"$workflow_text" | head -1 | cut -d: -f1)"
run_smoke_line="$(grep -n -- "- name: Run deployed WebSocket release smoke" <<<"$workflow_text" | head -1 | cut -d: -f1)"
if [[ -z "$configure_smoke_line" || -z "$restart_smoke_line" || -z "$run_smoke_line" ||
      ! ( "$configure_smoke_line" -lt "$restart_smoke_line" && "$restart_smoke_line" -lt "$run_smoke_line" ) ]]; then
  echo "The configured release-smoke revision must be restarted and verified before deployed smoke runs." >&2
  exit 1
fi

for marker in "OpenJibo__ReleaseSmoke__Enabled=false" "--remove-env-vars" "OpenJibo__ReleaseSmoke__Secret" "/health" "containerapp secret remove" "release-smoke-authorization" "failures="; do
  if [[ "$release_smoke_cleanup_text" != *"$marker"* ]]; then
    echo "Release-smoke cleanup script is missing lifecycle safeguard: $marker" >&2
    exit 1
  fi
done
disabled_config_line="$(grep -n -- "OpenJibo__ReleaseSmoke__Enabled=false" <<<"$release_smoke_cleanup_text" | head -1 | cut -d: -f1)"
health_check_line="$(grep -n -- '/health' <<<"$release_smoke_cleanup_text" | head -1 | cut -d: -f1)"
secret_delete_line="$(grep -n -- "containerapp secret remove" <<<"$release_smoke_cleanup_text" | head -1 | cut -d: -f1)"
if [[ ! ( "$disabled_config_line" -lt "$health_check_line" && "$health_check_line" -lt "$secret_delete_line" ) ]]; then
  echo "Release-smoke cleanup must deploy disabled/no-reference configuration, wait healthy, then delete the secret." >&2
  exit 1
fi

disable_smoke_line="$(grep -n -- "- name: Disable staging release smoke authorization" <<<"$workflow_text" | cut -d: -f1)"
restore_line="$(grep -n -- "- name: Restore previous image after failure" <<<"$workflow_text" | cut -d: -f1)"
redisable_line="$(grep -n -- "- name: Re-disable release smoke authorization after rollback" <<<"$workflow_text" | cut -d: -f1)"
promotion_gate_line="$(grep -n -- "- name: Create staging promotion gate" <<<"$workflow_text" | cut -d: -f1)"
if [[ -z "$disable_smoke_line" || -z "$restore_line" || -z "$redisable_line" || -z "$promotion_gate_line" ||
      ! ( "$disable_smoke_line" -lt "$restore_line" && "$restore_line" -lt "$redisable_line" && "$redisable_line" -lt "$promotion_gate_line" ) ]]; then
  echo "Release-smoke cleanup and rollback safeguards must complete before the staging promotion gate." >&2
  exit 1
fi

for marker in "OPENJIBO_USER_ENCRYPT" "OPENJIBO_USER_SALT" "user-encryption-passphrase" "user-encryption-salt" "OpenJibo__FleetNetwork__PeerSyncEnabled" "OpenJibo__FleetNetwork__AllowedPeerHosts"; do
  if [[ "$managed_text" != *"$marker"* ]]; then
    echo "Managed template is missing encryption marker: $marker" >&2
    exit 1
  fi
done

for marker in "openjibo-user-encrypt" "openjibo-user-salt" "openjibo-portal-status-password" "openjibo-peer-sync-shared-key" "openjibo-sigv4-replay-hmac" "openjibo-sigv4-replay-hmac-key-version" "openjibo-sigv4-replay-hmac-previous-key-version"; do
  if [[ "$linux_foundation_script_text" != *"$marker"* ]]; then
    echo "Linux foundation script is missing managed secret provisioning: $marker" >&2
    exit 1
  fi
done
if [[ "$linux_foundation_script_text" == *'get_or_create_random_secret("openjibo-sigv4-replay-hmac-previous"'* ]]; then
  echo "Linux foundation script must not generate or rotate the optional previous SigV4 replay HMAC secret." >&2
  exit 1
fi

for marker in "prepare-openjibo-managed-databases.sh" "--smoke-generated-fqdn" "user-encryption-passphrase" "user-encryption-salt"; do
  if [[ "$linux_managed_script_text" != *"$marker"* ]]; then
    echo "Linux managed deploy script is missing pre-deploy safeguard: $marker" >&2
    exit 1
  fi
done

for marker in "--import-legacy-cloud-state" "--import-legacy-personal-memory" "--verify" "--provision-sigv4-replay-observer" "--replay-observer-connection" "openjibo-user-encrypt" "openjibo-user-salt"; do
  if [[ "$linux_prepare_script_text" != *"$marker"* ]]; then
    echo "Managed database preparation script is missing expected marker: $marker" >&2
    exit 1
  fi
done

for marker in "pg_dump" "pg_restore" "firewall-rule create" "firewall-rule delete" '--server-name "$server_name"' '--name "$rule_name"' "Source and target resource groups must be different" "source and target PostgreSQL hosts are identical" "source-key-vault-name" "openjibo-user-encrypt" "openjibo-user-salt" 'keyvault secret set' '--file'; do
  if [[ "$linux_clone_script_text" != *"$marker"* ]]; then
    echo "Staging clone script is missing expected safety marker: $marker" >&2
    exit 1
  fi
done
if [[ "$smoke_script_text" == *'Host = "api.jibo.com"'* ]]; then
  echo "Managed smoke script still hardcodes the api.jibo.com host header." >&2
  exit 1
fi

for marker in "request_json_with_retry" "rollbackSnapshotId"; do
  if [[ "$linux_smoke_script_text" != *"$marker"* ]]; then
    echo "Linux smoke script is missing retry marker: $marker" >&2
    exit 1
  fi
done

if [[ "$linux_smoke_script_text" == *'"Host": "api.jibo.com"'* ]]; then
  echo "Linux smoke script still hardcodes the api.jibo.com host header." >&2
  exit 1
fi

if [[ "$linux_managed_script_text" != *"--location"* ]]; then
  echo "Linux managed deploy script is missing the regional override path." >&2
  exit 1
fi

if [[ "$managed_script_text" != *"Location"* ]]; then
  echo "Managed deploy script is missing the regional override path." >&2
  exit 1
fi

for marker in "apt-get install -y --no-install-recommends ffmpeg" "/usr/bin/ffmpeg"; do
  if [[ "$dockerfile_text$managed_text" != *"$marker"* ]]; then
    echo "Managed deployment is missing the Azure STT ffmpeg contract marker: $marker" >&2
    exit 1
  fi
done

if [[ "$linux_publish_script_text" != *"--build-arg ENABLE_LOCAL_WHISPER=false"* ]]; then
  echo "Linux publish script must build managed images with ENABLE_LOCAL_WHISPER=false to stay on Azure Speech and avoid baking in whisper.cpp." >&2
  exit 1
fi
if ! printf '%s\n' "$managed_text" | awk '
  /name: '\''OpenJibo__Stt__EnableLocalWhisperCpp'\''/ { found_name = 1; next }
  found_name && /value: '\''false'\''/ { found_value = 1; exit }
  found_name && /name:/ { exit }
  END { exit(found_value ? 0 : 1) }
'; then
  echo "Managed Container App must explicitly disable local Whisper at runtime because the managed image omits whisper.cpp." >&2
  exit 1
fi

for forbidden_marker in "OPENJIBO_MEDIA_CONNECTION_STRING" "OPENJIBO_STATE_CONNECTION_STRING" "OPENJIBO_PERSONAL_MEMORY_CONNECTION_STRING" "openjiboacr" "openjibokv" "-MediaConnectionString" "output storageConnectionString" "listKeys(storageAccount" "keyvault set-policy" "AZURE_SPEECH_SUBSCRIPTION_KEY" "--azure-speech-subscription-key"; do
  if [[ "$workflow_text" == *"$forbidden_marker"* ]]; then
    echo "Workflow still references forbidden marker: $forbidden_marker" >&2
    exit 1
  fi
  if [[ "$foundation_script_text" == *"$forbidden_marker"* ]]; then
    echo "Foundation script still references forbidden marker: $forbidden_marker" >&2
    exit 1
  fi
done

if command -v az >/dev/null 2>&1; then
  if az bicep version >/dev/null 2>&1; then
    echo "Azure CLI Bicep support is available."
  fi
fi

echo "Managed deployment contract checks passed."
