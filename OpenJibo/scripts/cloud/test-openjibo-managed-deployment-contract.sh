#!/usr/bin/env bash
set -euo pipefail

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
  "value: stateConnectionString"
  "value: personalMemoryConnectionString"
  "value: mediaConnectionString"
  "value: openWeatherApiKey"
  "value: newsApiKey"
  "value: searchBackend"
  "value: searchFallback"
  "value: portalStatusPassword"
  "OpenJibo__Portal__StatusPassword"
  "portal-status-password"
  "search-backend"
  "search-fallback"
  "var logAnalyticsWorkspaceKey"
  "value: 'PostgreSql'"
  "value: 'AzureBlob'"
  "keyVaultContainerAppSecretAccessPolicy"
  "OPENJIBO_SEARCH_BACKEND"
  "OPENJIBO_SEARCH_FALLBACK"
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

for marker in "openjibo-media-connection-string" "azure-speech-subscription-key" "cognitiveservices account keys list" "speechServicesAccountName" "openjibo-postgres-admin-password" "openjibo-search-backend" "openjibo-search-fallback" "openjibo-portal-status-password" "openjibo-peer-sync-shared-key" "postgresFullyQualifiedDomainName" "Invoke-OpenJiboAzWithRetry"; do
  if [[ "$foundation_script_text" != *"$marker"* ]]; then
    echo "Foundation script is missing expected marker: $marker" >&2
    exit 1
  fi
done

if [[ "$foundation_script_text" != *"seedPrincipalObjectId"* ]]; then
  echo "Foundation script does not pass the secret seed access policy principal to the deployment." >&2
  exit 1
fi

for marker in "RegistryName" "ApiHostname" "SocketHostname" "NeoHubHostname" "NativeCompatibilityApiHostname" "NativeCompatibilitySocketHostname" "open-jibo.jibo.pro" "open-jibo-socket.jibo.pro" "containerapp hostname add" "containerapp hostname bind" "SkipHostnameBinding" "EnableAzureSpeech" "AzureSpeechRegion" "portalStatusPassword" "openjibo-portal-status-password" "searchBackend" "searchFallback" "openjibo-search-backend" "openjibo-search-fallback"; do
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

for marker in "--run-smoke" "--run-migration" "--api-hostname" "--socket-hostname" "--neohub-hostname" "--native-compatibility-api-hostname" "--native-compatibility-socket-hostname" "open-jibo.jibo.pro" "open-jibo-socket.jibo.pro" "az containerapp hostname add" "az containerapp hostname bind" 'prepare-openjibo-managed-databases.sh' "--skip-hostname-binding" "--enable-peer-sync" "--disable-peer-sync" "--peer-sync-allowed-hosts" "peerSyncEnabled" "allowedPeerHosts" "portal-status-password" "openjibo-portal-status-password" "searchBackend" "searchFallback" "openjibo-search-backend" "openjibo-search-fallback" "run_command_with_retry"; do
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

for marker in "deployment_target" "openjibo-staging-gate" "clone-openjibo-managed-databases.sh" "keyVaultUrl" "keyVaultUri" "urlsplit" "openjibo-managed-" "containerAppName" "properties.outputs" "user-encryption-passphrase" "user-encryption-salt" "production_backup_confirmed" "enable_fleet_peer_sync" "fleet_peer_allowed_hosts" "Fleet peer sync cannot be enabled by the staging workflow" "backup.backupRetentionDays" "Verify production hostname DNS prerequisites" "customDomainVerificationId" "dig +short CNAME" "dig +short TXT" "open-jibo.jibo.pro" "open-jibo-socket.jibo.pro" "properties.active" '[[ "$revision_active" == "true" ]]' '[[ "$previous_revision_active" != "true" ]]' "already active" "revision deactivate" "revision activate" "PREVIOUS_REVISION" "Restore previous image after failure" "Run deployed WebSocket release smoke" "invoke-release-smoke.mjs" "webSocketReleaseSmoke"; do
  if [[ "$workflow_text" != *"$marker"* ]]; then
    echo "Workflow is missing staging or promotion safeguard: $marker" >&2
    exit 1
  fi
done

for marker in "OPENJIBO_USER_ENCRYPT" "OPENJIBO_USER_SALT" "user-encryption-passphrase" "user-encryption-salt" "OpenJibo__FleetNetwork__PeerSyncEnabled" "OpenJibo__FleetNetwork__AllowedPeerHosts"; do
  if [[ "$managed_text" != *"$marker"* ]]; then
    echo "Managed template is missing encryption marker: $marker" >&2
    exit 1
  fi
done

for marker in "openjibo-user-encrypt" "openjibo-user-salt" "openjibo-portal-status-password" "openjibo-peer-sync-shared-key"; do
  if [[ "$linux_foundation_script_text" != *"$marker"* ]]; then
    echo "Linux foundation script is missing managed secret provisioning: $marker" >&2
    exit 1
  fi
done

for marker in "prepare-openjibo-managed-databases.sh" "--smoke-generated-fqdn" "user-encryption-passphrase" "user-encryption-salt"; do
  if [[ "$linux_managed_script_text" != *"$marker"* ]]; then
    echo "Linux managed deploy script is missing pre-deploy safeguard: $marker" >&2
    exit 1
  fi
done

for marker in "--import-legacy-cloud-state" "--import-legacy-personal-memory" "--verify" "openjibo-user-encrypt" "openjibo-user-salt"; do
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
