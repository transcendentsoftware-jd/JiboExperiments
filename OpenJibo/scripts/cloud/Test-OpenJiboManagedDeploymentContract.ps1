param(
    [string]$FoundationTemplatePath = "infra/azure/foundation/openjibo-managed-foundation.bicep",
    [string]$ManagedTemplatePath = "infra/azure/container-apps/openjibo-managed.bicep",
    [string]$WorkflowPath = "../.github/workflows/openjibo-cloud-managed-deploy.yml",
    [string]$FoundationScriptPath = "scripts/cloud/Deploy-OpenJiboManagedFoundation.ps1",
    [string]$ManagedScriptPath = "scripts/cloud/Deploy-OpenJiboManaged.ps1",
    [string]$LinuxFoundationScriptPath = "scripts/cloud/deploy-openjibo-managed-foundation.sh",
    [string]$LinuxPublishScriptPath = "scripts/cloud/publish-openjibo-managed.sh",
    [string]$LinuxManagedScriptPath = "scripts/cloud/deploy-openjibo-managed.sh",
    [string]$LinuxPrepareScriptPath = "scripts/cloud/prepare-openjibo-managed-databases.sh",
    [string]$LinuxCloneScriptPath = "scripts/cloud/clone-openjibo-managed-databases.sh",
    [string]$ReleaseSmokeCleanupScriptPath = "scripts/cloud/cleanup-release-smoke-authorization.sh",
    [string]$SmokeScriptPath = "scripts/cloud/Invoke-CloudSmoke.ps1",
    [string]$LinuxSmokeScriptPath = "scripts/cloud/invoke-cloud-smoke.sh",
    [string]$DockerfilePath = "Dockerfile"
)

$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))

function Get-RepoFileText {
    param([string]$RelativePath)

    $fullPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $RelativePath))
    if (-not (Test-Path -LiteralPath $fullPath)) {
        throw "Missing required file: $fullPath"
    }

    return Get-Content -LiteralPath $fullPath -Raw
}

function Assert-ContainsMarker {
    param(
        [string]$Text,
        [string]$Marker,
        [string]$FailurePrefix
    )

    if ($Text -notmatch [regex]::Escape($Marker)) {
        throw "$FailurePrefix`: $Marker"
    }
}

$foundationText = Get-RepoFileText -RelativePath $FoundationTemplatePath
$managedText = Get-RepoFileText -RelativePath $ManagedTemplatePath
$releaseSmokeCleanupText = Get-RepoFileText -RelativePath $ReleaseSmokeCleanupScriptPath
$workflowText = Get-RepoFileText -RelativePath $WorkflowPath
$foundationScriptText = Get-RepoFileText -RelativePath $FoundationScriptPath
$managedScriptText = Get-RepoFileText -RelativePath $ManagedScriptPath
$linuxFoundationScriptText = Get-RepoFileText -RelativePath $LinuxFoundationScriptPath
$linuxPublishScriptText = Get-RepoFileText -RelativePath $LinuxPublishScriptPath
$linuxManagedScriptText = Get-RepoFileText -RelativePath $LinuxManagedScriptPath
$linuxPrepareScriptText = Get-RepoFileText -RelativePath $LinuxPrepareScriptPath
$linuxCloneScriptText = Get-RepoFileText -RelativePath $LinuxCloneScriptPath
$smokeScriptText = Get-RepoFileText -RelativePath $SmokeScriptPath
$linuxSmokeScriptText = Get-RepoFileText -RelativePath $LinuxSmokeScriptPath
$dockerfileText = Get-RepoFileText -RelativePath $DockerfilePath

$requiredFoundationMarkers = @(
    "output keyVaultName string",
    "output registryName string",
    "output storageAccountName string",
    "resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01'",
    "param storageAccountName string = ''",
    "var resolvedStorageAccountName",
    "resource speechServicesAccount 'Microsoft.CognitiveServices/accounts@2023-05-01'",
    "param speechServicesAccountName string = ''",
    "output speechServicesAccountName string",
    "resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01'",
    "publicNetworkAccess: 'Enabled'",
    "accessPolicies: []",
    "enableRbacAuthorization: false",
    "param seedPrincipalObjectId string = ''",
    "resource keyVaultSecretSeedAccessPolicy 'Microsoft.KeyVault/vaults/accessPolicies@2023-07-01'",
    "resource postgresServer 'Microsoft.DBforPostgreSQL/flexibleServers@2023-06-01-preview'",
    "resource postgresStateDatabase 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2023-06-01-preview'",
    "resource postgresPersonalMemoryDatabase 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2023-06-01-preview'",
    "resource postgresAllowAzureServicesFirewallRule 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2023-06-01-preview'",
    "resource applicationInsights 'Microsoft.Insights/components@2020-02-02'",
    "output applicationInsightsName string",
    "param postgresDeploymentRunnerFirewallIpAddress string = ''",
    "output postgresFullyQualifiedDomainName string",
    "output postgresStateDatabaseName string",
    "output postgresPersonalMemoryDatabaseName string"
)

$requiredManagedMarkers = @(
    "param registryLoginServer string",
    "param keyVaultName string",
    "param apiHostname string = 'api.openjibo.com'",
    "param socketHostname string = 'open-jibo-socket.openjibo.com'",
    "param neoHubHostname string = 'neohub.openjibo.com'",
    "param enableAzureSpeech bool = true",
    "param azureSpeechRegion string = location",
    "resource applicationInsights 'Microsoft.Insights/components@2020-02-02' existing",
    "APPLICATIONINSIGHTS_CONNECTION_STRING",
    "APPLICATIONINSIGHTS_METRIC_NAMESPACE_OPT_IN",
    "param statePostgreSqlMaxPoolSize int = 8",
    "param personalMemoryPostgreSqlMaxPoolSize int = 4",
    "@maxValue(12)",
    "@maxValue(6)",
    "OpenJibo__State__PostgreSql__MaxPoolSize",
    "OpenJibo__PersonalMemory__PostgreSql__MaxPoolSize",
    "OpenJibo__CanonicalApiHostname",
    "OpenJibo__CanonicalApiBaseUrl",
    "OpenJibo__CanonicalSocketHostname",
    "OpenJibo__CanonicalNeoHubHostname",
    "output canonicalApiHostname string",
    "output canonicalSocketHostname string",
    "output canonicalNeoHubHostname string",
    "output canonicalSocketBaseUrl string",
    "output canonicalNeoHubBaseUrl string",
    "output containerAppName string",
    "output managedEnvironmentName string",
    "OpenJibo__Stt__EnableAzureSpeech",
    "azure-speech-subscription-key",
    "OpenJibo__Stt__AzureSpeechSubscriptionKey",
    "param stateConnectionString string = ''",
    "param personalMemoryConnectionString string = ''",
    "param mediaConnectionString string = ''",
    "param openWeatherApiKey string = ''",
    "param newsApiKey string = ''",
    "param searchBackend string = ''",
    "param searchFallback string = ''",
    "param portalStatusPassword string = ''",
    "param sigV4ReplayHmacKey string = ''",
    "param sigV4ReplayHmacKeyPrevious string = ''",
    "param sigV4ReplayObservationKeyVersion int = 1",
    "param sigV4ReplayObservationPreviousKeyVersion int = 0",
    "param sigV4ReplayObservationConnectionString string = ''",
    "param sigV4ReplayObservationEnabled bool = false",
    "value: stateConnectionString",
    "value: personalMemoryConnectionString",
    "value: mediaConnectionString",
    "value: openWeatherApiKey",
    "value: newsApiKey",
    "value: searchBackend",
    "value: searchFallback",
    "value: portalStatusPassword",
    "OpenJibo__Portal__StatusPassword",
    "OpenJibo__Security__SigV4ReplayHmacKey",
    "OpenJibo__Security__SigV4ReplayObservation__ConnectionString",
    "OpenJibo__Security__SigV4ReplayObservation__Enabled",
    "OpenJibo__Security__SigV4ReplayObservation__KeyVersion",
    "OpenJibo__Security__SigV4ReplayObservation__PreviousKeyVersion",
    "OpenJibo__Security__SigV4ReplayObservation__PreviousHmacKey",
    "portal-status-password",
    "sigv4-replay-hmac-key",
    "sigv4-replay-hmac-key-previous",
    "sigv4-replay-observer-connection-string",
    "search-backend",
    "search-fallback",
    "var logAnalyticsWorkspaceKey",
    "value: 'PostgreSql'",
    "value: 'AzureBlob'",
    "keyVaultContainerAppSecretAccessPolicy",
    "OPENJIBO_SEARCH_BACKEND",
    "OPENJIBO_SEARCH_FALLBACK"
)

$requiredWorkflowMarkers = @(
    "shell: bash",
    "working-directory: OpenJibo",
    "deploy-openjibo-managed-foundation.sh",
    "deploy-openjibo-managed.sh",
    "publish-openjibo-managed.sh",
    "steps.foundation.outputs.registryName",
    "steps.foundation.outputs.keyVaultName",
    "inputs.location",
    "existing_log_analytics_workspace_name",
    "existing_container_registry_name",
    "existing_key_vault_name",
    "existing_storage_account_name",
    "existing_postgres_server_name",
    "existing_speech_services_account_name",
    "Specify every existing foundation resource name together",
    "api_hostname",
    "socket_hostname",
    "neohub_hostname",
    "api.openjibo.com",
    "open-jibo-socket.openjibo.com",
    "neohub.openjibo.com",
    "OPENJIBO_SEARCH_BACKEND",
    "OPENJIBO_SEARCH_FALLBACK",
    "--api-hostname",
    "--socket-hostname",
    "--neohub-hostname",
    "enable_azure_speech",
    "azure_speech_region",
    "--search-backend",
    "--search-fallback",
    "--run-migration",
    "--run-smoke"
)

foreach ($marker in $requiredFoundationMarkers) {
    Assert-ContainsMarker -Text $foundationText -Marker $marker -FailurePrefix "Foundation template is missing expected marker"
}

foreach ($marker in $requiredManagedMarkers) {
    Assert-ContainsMarker -Text $managedText -Marker $marker -FailurePrefix "Managed template is missing expected marker"
}

foreach ($marker in $requiredWorkflowMarkers) {
    Assert-ContainsMarker -Text $workflowText -Marker $marker -FailurePrefix "Workflow is missing expected marker"
}

foreach ($marker in @("openjibo-media-connection-string", "azure-speech-subscription-key", "cognitiveservices account keys list", "speechServicesAccountName", "openjibo-postgres-admin-password", "openjibo-search-backend", "openjibo-search-fallback", "openjibo-portal-status-password", "openjibo-peer-sync-shared-key", "openjibo-sigv4-replay-hmac", "openjibo-sigv4-replay-hmac-key-version", "openjibo-sigv4-replay-hmac-previous-key-version", "openjibo-sigv4-replay-observer-password", "openjibo-sigv4-replay-observer-connection-string", "postgresFullyQualifiedDomainName", "Invoke-OpenJiboAzWithRetry", "seedPrincipalObjectId")) {
    Assert-ContainsMarker -Text $foundationScriptText -Marker $marker -FailurePrefix "Foundation script is missing expected marker"
}

foreach ($marker in @("RegistryName", "ApiHostname", "SocketHostname", "NeoHubHostname", "NativeCompatibilityApiHostname", "NativeCompatibilitySocketHostname", "AdditionalCompatibilityApiHostname", "open-jibo.jibo.pro", "open-jibo-socket.jibo.pro", "api.jibo.pro", "containerapp hostname add", "containerapp hostname bind", "SkipHostnameBinding", "EnableAzureSpeech", "AzureSpeechRegion", "portalStatusPassword", "openjibo-portal-status-password", "sigV4ReplayHmacKey", "sigV4ReplayHmacKeyPrevious", "sigv4-replay-hmac-key", "sigv4-replay-hmac-key-previous", "EnableSigV4ReplayObservation", "sigv4-replay-hmac-key-version", "sigv4-replay-hmac-previous-key-version", "sigV4ReplayObservationEnabled", "sigV4ReplayObservationKeyVersion", "sigV4ReplayObservationPreviousKeyVersion", "openjibo-sigv4-replay-observer-connection-string", "ProvisionSigV4ReplayObserver", "ReplayObserverConnectionString", "Test-OpenJiboBase64UrlSecret", "searchBackend", "searchFallback", "openjibo-search-backend", "openjibo-search-fallback")) {
    Assert-ContainsMarker -Text $managedScriptText -Marker $marker -FailurePrefix "Managed deploy script is missing expected marker"
}

foreach ($marker in @("managedEnvironmentName", "--environment", "--validation-method CNAME", "search-backend", "search-fallback")) {
    Assert-ContainsMarker -Text $managedScriptText -Marker $marker -FailurePrefix "Managed deploy script is missing hostname binding environment marker"
}

foreach ($marker in @("containerapp env show", "firewall-rule create", "firewall-rule update")) {
    Assert-ContainsMarker -Text $managedScriptText -Marker $marker -FailurePrefix "Managed deploy script is missing firewall marker"
}

foreach ($marker in @("--log-analytics-workspace-name", "--container-registry-name", "--key-vault-name", "--storage-account-name", "--postgres-server-name", "--speech-services-account-name", 'f"--value={value}"', "seedPrincipalObjectId", "openjibo-media-connection-string", "openjibo-postgres-admin-password", "openjibo-sigv4-replay-hmac", "postgresFullyQualifiedDomainName", "run_command_with_retry")) {
    Assert-ContainsMarker -Text $linuxFoundationScriptText -Marker $marker -FailurePrefix "Linux foundation script is missing expected marker"
}

Assert-ContainsMarker -Text $linuxFoundationScriptText -Marker '"az", "storage", "account", "show-connection-string"' -FailurePrefix "Linux foundation script does not resolve the storage connection string outside Bicep outputs"
Assert-ContainsMarker -Text $linuxPublishScriptText -Marker "az acr build" -FailurePrefix "Linux publish script is missing the ACR build path"

foreach ($marker in @("--run-smoke", "--run-migration", "--api-hostname", "--socket-hostname", "--neohub-hostname", "--native-compatibility-api-hostname", "--native-compatibility-socket-hostname", "--additional-compatibility-api-hostname", "open-jibo.jibo.pro", "open-jibo-socket.jibo.pro", "api.jibo.pro", "az containerapp hostname add", "az containerapp hostname bind", "--skip-hostname-binding", "--enable-peer-sync", "--disable-peer-sync", "--peer-sync-allowed-hosts", "peerSyncEnabled", "allowedPeerHosts", "portal-status-password", "openjibo-portal-status-password", "sigv4_replay_hmac_key", "sigV4ReplayHmacKey", "sigv4-replay-hmac-key", "validate_base64url_secret", "searchBackend", "searchFallback", "openjibo-search-backend", "openjibo-search-fallback")) {
    Assert-ContainsMarker -Text $linuxManagedScriptText -Marker $marker -FailurePrefix "Linux managed deploy script is missing expected marker"
}

foreach ($marker in @("managedEnvironmentName", "--environment", "--validation-method CNAME", "search-backend", "search-fallback")) {
    Assert-ContainsMarker -Text $linuxManagedScriptText -Marker $marker -FailurePrefix "Linux managed deploy script is missing hostname binding environment marker"
}

foreach ($marker in @("deployment_target", "openjibo-staging-gate", "clone-openjibo-managed-databases.sh", "keyVaultUrl", "keyVaultUri", "urlsplit", "openjibo-managed-", "containerAppName", "properties.outputs", "user-encryption-passphrase", "user-encryption-salt", "production_backup_confirmed", "fleet_peer_allowed_hosts", "Fleet peer sync cannot be enabled by the staging workflow", "--enable-peer-sync --peer-sync-allowed-hosts", "enable_sigv4_replay_observation", "SigV4 replay observation is staging-only", "--enable-sigv4-replay-observation", "--disable-sigv4-replay-observation", "Verify staging SigV4 replay observer configuration", "OpenJibo__Security__SigV4ReplayObservation__Enabled", "sigv4-replay-observer-connection-string", "sigv4-replay-hmac-key", "sigV4ReplayObservation", "enabled-shadow", "PREVIOUS_REPLAY_OBSERVATION_ENABLED", "backup.backupRetentionDays", "Verify production hostname DNS prerequisites", "customDomainVerificationId", "dig +short CNAME", "dig +short TXT", "open-jibo.jibo.pro", "open-jibo-socket.jibo.pro", "api.jibo.pro", "staging-api.jibo.pro", "properties.active", '[[ "$revision_active" == "true" ]]', '[[ "$previous_revision_active" != "true" ]]', "already active", "revision deactivate", "revision activate", "revision restart", "latestReadyRevisionName", "properties.runningState", '[[ "$latest_ready_revision" == "$configured_revision"', "PREVIOUS_REVISION", "Restore previous image after failure", "Re-disable release smoke authorization after rollback", "Run deployed WebSocket release smoke", "invoke-release-smoke.mjs", "cleanup-release-smoke-authorization.sh", "TEST_ROBOT_ID: open-jibo-smoke-staging", "openssl rand -hex 32", "release-smoke-authorization", "OpenJibo__ReleaseSmoke__Enabled=true", "OPENJIBO_RELEASE_SMOKE_ALLOWED_HOST", "cancel-in-progress: false", "webSocketReleaseSmoke")) {
    Assert-ContainsMarker -Text $workflowText -Marker $marker -FailurePrefix "Workflow is missing staging or promotion safeguard"
}
$productionRejectIndex = $workflowText.IndexOf("SigV4 replay observation is staging-only", [StringComparison]::Ordinal)
$deployInvocationIndex = $workflowText.IndexOf('bash ./scripts/cloud/deploy-openjibo-managed.sh "${deploy_args[@]}"', [StringComparison]::Ordinal)
$verifyReplayIndex = $workflowText.IndexOf("- name: Verify staging SigV4 replay observer configuration", [StringComparison]::Ordinal)
$promotionGateIndex = $workflowText.IndexOf("- name: Create staging promotion gate", [StringComparison]::Ordinal)
if ($productionRejectIndex -lt 0 -or $deployInvocationIndex -lt 0 -or $verifyReplayIndex -lt 0 -or $promotionGateIndex -lt 0 -or
    -not ($productionRejectIndex -lt $deployInvocationIndex -and $deployInvocationIndex -lt $verifyReplayIndex -and $verifyReplayIndex -lt $promotionGateIndex)) {
    throw "Replay observation must reject production before deployment and verify staging before promotion evidence is created."
}
$rollbackIndex = $workflowText.IndexOf("- name: Restore previous image after failure", [StringComparison]::Ordinal)
$rollbackReplayIndex = $workflowText.IndexOf('--set-env-vars "OpenJibo__Security__SigV4ReplayObservation__Enabled=${PREVIOUS_REPLAY_OBSERVATION_ENABLED:-false}"', [StringComparison]::Ordinal)
if ($rollbackIndex -lt 0 -or $rollbackReplayIndex -lt $rollbackIndex) {
    throw "Rollback must restore the previous replay-observation enabled state."
}
$configureSmokeIndex = $workflowText.IndexOf("OpenJibo__ReleaseSmoke__Enabled=true", [StringComparison]::Ordinal)
$twoReplicaMarkers = @(
    "Capture staging scale before two-replica proof",
    "--min-replicas 2",
    '"$revision_running_state" == "RunningAtMaxScale"',
    "RELEASE_SMOKE_MIN_REPLICAS",
    "RELEASE_SMOKE_EXPECTED_REVISION",
    "minimumReplicasObserved",
    "crossReplicaCommittedRead",
    "Restore staging scale after two-replica proof"
)
foreach ($marker in $twoReplicaMarkers) {
    Assert-ContainsMarker -Text $workflowText -Marker $marker -FailurePrefix "Workflow is missing two-replica staging safeguard"
}
$restartSmokeIndex = $workflowText.IndexOf("az containerapp revision restart", [StringComparison]::Ordinal)
$runSmokeIndex = $workflowText.IndexOf("- name: Run deployed WebSocket release smoke", [StringComparison]::Ordinal)
if ($configureSmokeIndex -lt 0 -or $restartSmokeIndex -lt 0 -or $runSmokeIndex -lt 0 -or
    -not ($configureSmokeIndex -lt $restartSmokeIndex -and $restartSmokeIndex -lt $runSmokeIndex)) {
    throw "The configured release-smoke revision must be restarted and verified before deployed smoke runs."
}

foreach ($marker in @("OpenJibo__ReleaseSmoke__Enabled=false", "--remove-env-vars", "OpenJibo__ReleaseSmoke__Secret", "/health", "containerapp secret remove", "release-smoke-authorization", "failures=")) {
    Assert-ContainsMarker -Text $releaseSmokeCleanupText -Marker $marker -FailurePrefix "Release-smoke cleanup script is missing lifecycle safeguard"
}
$disabledConfigIndex = $releaseSmokeCleanupText.IndexOf("OpenJibo__ReleaseSmoke__Enabled=false", [StringComparison]::Ordinal)
$healthCheckIndex = $releaseSmokeCleanupText.IndexOf('"https://${app_fqdn}/health"', [StringComparison]::Ordinal)
$secretDeleteIndex = $releaseSmokeCleanupText.IndexOf("containerapp secret remove", [StringComparison]::Ordinal)
if (-not ($disabledConfigIndex -lt $healthCheckIndex -and $healthCheckIndex -lt $secretDeleteIndex)) {
    throw "Release-smoke cleanup must deploy disabled/no-reference configuration, wait healthy, then delete the secret."
}

$disableSmokeIndex = $workflowText.IndexOf("- name: Disable staging release smoke authorization", [StringComparison]::Ordinal)
$restoreIndex = $workflowText.IndexOf("- name: Restore previous image after failure", [StringComparison]::Ordinal)
$redisableIndex = $workflowText.IndexOf("- name: Re-disable release smoke authorization after rollback", [StringComparison]::Ordinal)
$promotionGateIndex = $workflowText.IndexOf("- name: Create staging promotion gate", [StringComparison]::Ordinal)
if ($disableSmokeIndex -lt 0 -or $restoreIndex -lt 0 -or $redisableIndex -lt 0 -or $promotionGateIndex -lt 0 -or
    -not ($disableSmokeIndex -lt $restoreIndex -and $restoreIndex -lt $redisableIndex -and $redisableIndex -lt $promotionGateIndex)) {
    throw "Release-smoke cleanup and rollback safeguards must complete before the staging promotion gate."
}

foreach ($marker in @("OPENJIBO_USER_ENCRYPT", "OPENJIBO_USER_SALT", "user-encryption-passphrase", "user-encryption-salt", "OpenJibo__FleetNetwork__PeerSyncEnabled", "OpenJibo__FleetNetwork__AllowedPeerHosts")) {
    Assert-ContainsMarker -Text $managedText -Marker $marker -FailurePrefix "Managed template is missing encryption marker"
}

foreach ($marker in @("openjibo-user-encrypt", "openjibo-user-salt", "openjibo-portal-status-password", "openjibo-peer-sync-shared-key", "openjibo-sigv4-replay-hmac")) {
    Assert-ContainsMarker -Text $linuxFoundationScriptText -Marker $marker -FailurePrefix "Linux foundation script is missing managed secret provisioning"
}
if ($linuxFoundationScriptText.Contains('get_or_create_random_secret("openjibo-sigv4-replay-hmac-previous"')) {
    throw "Linux foundation script must not generate or rotate the optional previous SigV4 replay HMAC secret."
}
if ($foundationScriptText.Contains("-Name openjibo-sigv4-replay-hmac-previous -ByteCount")) {
    throw "PowerShell foundation script must not generate or rotate the optional previous SigV4 replay HMAC secret."
}

foreach ($marker in @("prepare-openjibo-managed-databases.sh", "--smoke-generated-fqdn", "user-encryption-passphrase", "user-encryption-salt")) {
    Assert-ContainsMarker -Text $linuxManagedScriptText -Marker $marker -FailurePrefix "Linux managed deploy script is missing pre-deploy safeguard"
}

foreach ($marker in @("--import-legacy-cloud-state", "--import-legacy-personal-memory", "--verify", "--provision-sigv4-replay-observer", "--replay-observer-connection", "openjibo-user-encrypt", "openjibo-user-salt")) {
    Assert-ContainsMarker -Text $linuxPrepareScriptText -Marker $marker -FailurePrefix "Managed database preparation script is missing expected marker"
}

foreach ($marker in @("pg_dump", "pg_restore", "firewall-rule create", "firewall-rule delete", '--server-name "$server_name"', '--name "$rule_name"', "Source and target resource groups must be different", "source and target PostgreSQL hosts are identical", "source-key-vault-name", "openjibo-user-encrypt", "openjibo-user-salt", "keyvault secret set", "--file")) {
    Assert-ContainsMarker -Text $linuxCloneScriptText -Marker $marker -FailurePrefix "Staging clone script is missing expected safety marker"
}
if ($smokeScriptText -match [regex]::Escape('Host = "api.jibo.com"')) {
    throw "Managed smoke script still hardcodes the api.jibo.com host header."
}

foreach ($marker in @("Invoke-JsonRequestWithRetry", "rollbackSnapshotId")) {
    Assert-ContainsMarker -Text $smokeScriptText -Marker $marker -FailurePrefix "Managed smoke script is missing retry marker"
}

if ($linuxSmokeScriptText -match [regex]::Escape('"Host": "api.jibo.com"')) {
    throw "Linux smoke script still hardcodes the api.jibo.com host header."
}

Assert-ContainsMarker -Text $linuxManagedScriptText -Marker "--location" -FailurePrefix "Linux managed deploy script is missing the regional override path"
Assert-ContainsMarker -Text $managedScriptText -Marker "Location" -FailurePrefix "Managed deploy script is missing the regional override path"
Assert-ContainsMarker -Text $dockerfileText -Marker "apt-get install -y --no-install-recommends ffmpeg" -FailurePrefix "Managed image is missing ffmpeg"
Assert-ContainsMarker -Text ($dockerfileText + $managedText) -Marker "/usr/bin/ffmpeg" -FailurePrefix "Managed deployment is missing the ffmpeg path contract"
Assert-ContainsMarker -Text $linuxPublishScriptText -Marker "--build-arg ENABLE_LOCAL_WHISPER=false" -FailurePrefix "Linux publish script must build managed images with ENABLE_LOCAL_WHISPER=false to stay on Azure Speech"
if ($managedText -notmatch "name: 'OpenJibo__Stt__EnableLocalWhisperCpp'\s+value: 'false'") {
    throw "Managed Container App must explicitly disable local Whisper because the managed image omits whisper.cpp"
}
$forbiddenMarkers = @(
    "OPENJIBO_MEDIA_CONNECTION_STRING",
    "OPENJIBO_STATE_CONNECTION_STRING",
    "OPENJIBO_PERSONAL_MEMORY_CONNECTION_STRING",
    "openjiboacr",
    "openjibokv",
    "-MediaConnectionString",
    "output storageConnectionString",
    "listKeys(storageAccount",
    "keyvault set-policy",
    "AZURE_SPEECH_SUBSCRIPTION_KEY",
    "--azure-speech-subscription-key"
)

foreach ($marker in $forbiddenMarkers) {
    if ($workflowText -match [regex]::Escape($marker)) {
        throw "Workflow still references forbidden marker: $marker"
    }
    if ($foundationScriptText -match [regex]::Escape($marker)) {
        throw "Foundation script still references forbidden marker: $marker"
    }
}

if (Get-Command az -ErrorAction SilentlyContinue) {
    try {
        $null = & az bicep version 2>$null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "Azure CLI Bicep support is available."
        }
    } catch {
        Write-Host "Azure CLI is available, but Bicep support is not installed."
    }
}

Write-Host "Managed deployment contract checks passed."
