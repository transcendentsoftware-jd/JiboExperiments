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
    "value: stateConnectionString",
    "value: personalMemoryConnectionString",
    "value: mediaConnectionString",
    "value: openWeatherApiKey",
    "value: newsApiKey",
    "value: searchBackend",
    "value: searchFallback",
    "value: portalStatusPassword",
    "OpenJibo__Portal__StatusPassword",
    "portal-status-password",
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

foreach ($marker in @("openjibo-media-connection-string", "azure-speech-subscription-key", "cognitiveservices account keys list", "speechServicesAccountName", "openjibo-postgres-admin-password", "openjibo-search-backend", "openjibo-search-fallback", "postgresFullyQualifiedDomainName", "Invoke-OpenJiboAzWithRetry", "seedPrincipalObjectId")) {
    Assert-ContainsMarker -Text $foundationScriptText -Marker $marker -FailurePrefix "Foundation script is missing expected marker"
}

foreach ($marker in @("RegistryName", "ApiHostname", "SocketHostname", "NeoHubHostname", "containerapp hostname add", "containerapp hostname bind", "SkipHostnameBinding", "EnableAzureSpeech", "AzureSpeechRegion", "portalStatusPassword", "openjibo-portal-status-password", "searchBackend", "searchFallback", "openjibo-search-backend", "openjibo-search-fallback")) {
    Assert-ContainsMarker -Text $managedScriptText -Marker $marker -FailurePrefix "Managed deploy script is missing expected marker"
}

foreach ($marker in @("managedEnvironmentName", "--environment", "--validation-method CNAME", "search-backend", "search-fallback")) {
    Assert-ContainsMarker -Text $managedScriptText -Marker $marker -FailurePrefix "Managed deploy script is missing hostname binding environment marker"
}

foreach ($marker in @("containerapp env show", "firewall-rule create", "firewall-rule update")) {
    Assert-ContainsMarker -Text $managedScriptText -Marker $marker -FailurePrefix "Managed deploy script is missing firewall marker"
}

foreach ($marker in @("seedPrincipalObjectId", "openjibo-media-connection-string", "openjibo-postgres-admin-password", "postgresFullyQualifiedDomainName", "run_command_with_retry")) {
    Assert-ContainsMarker -Text $linuxFoundationScriptText -Marker $marker -FailurePrefix "Linux foundation script is missing expected marker"
}

Assert-ContainsMarker -Text $linuxFoundationScriptText -Marker '"az", "storage", "account", "show-connection-string"' -FailurePrefix "Linux foundation script does not resolve the storage connection string outside Bicep outputs"
Assert-ContainsMarker -Text $linuxPublishScriptText -Marker "az acr build" -FailurePrefix "Linux publish script is missing the ACR build path"

foreach ($marker in @("--run-smoke", "--run-migration", "--api-hostname", "--socket-hostname", "--neohub-hostname", "az containerapp hostname add", "az containerapp hostname bind", "--skip-hostname-binding", "portal-status-password", "openjibo-portal-status-password", "searchBackend", "searchFallback", "openjibo-search-backend", "openjibo-search-fallback")) {
    Assert-ContainsMarker -Text $linuxManagedScriptText -Marker $marker -FailurePrefix "Linux managed deploy script is missing expected marker"
}

foreach ($marker in @("managedEnvironmentName", "--environment", "--validation-method CNAME", "search-backend", "search-fallback")) {
    Assert-ContainsMarker -Text $linuxManagedScriptText -Marker $marker -FailurePrefix "Linux managed deploy script is missing hostname binding environment marker"
}

foreach ($marker in @("deployment_target", "openjibo-staging-gate", "clone-openjibo-managed-databases.sh", "production_backup_confirmed", "backup.backupRetentionDays", "revision deactivate", "Restore previous image after failure")) {
    Assert-ContainsMarker -Text $workflowText -Marker $marker -FailurePrefix "Workflow is missing staging or promotion safeguard"
}

foreach ($marker in @("OPENJIBO_USER_ENCRYPT", "OPENJIBO_USER_SALT", "user-encryption-passphrase", "user-encryption-salt")) {
    Assert-ContainsMarker -Text $managedText -Marker $marker -FailurePrefix "Managed template is missing encryption marker"
}

foreach ($marker in @("openjibo-user-encrypt", "openjibo-user-salt")) {
    Assert-ContainsMarker -Text $linuxFoundationScriptText -Marker $marker -FailurePrefix "Linux foundation script is missing encryption secret provisioning"
}

foreach ($marker in @("prepare-openjibo-managed-databases.sh", "--smoke-generated-fqdn", "user-encryption-passphrase", "user-encryption-salt")) {
    Assert-ContainsMarker -Text $linuxManagedScriptText -Marker $marker -FailurePrefix "Linux managed deploy script is missing pre-deploy safeguard"
}

foreach ($marker in @("--import-legacy-cloud-state", "--import-legacy-personal-memory", "--verify", "openjibo-user-encrypt", "openjibo-user-salt")) {
    Assert-ContainsMarker -Text $linuxPrepareScriptText -Marker $marker -FailurePrefix "Managed database preparation script is missing expected marker"
}

foreach ($marker in @("pg_dump", "pg_restore", "Source and target resource groups must be different", "source and target PostgreSQL hosts are identical")) {
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
