param(
    [Parameter(Mandatory = $true)]
    [string]$ResourceGroupName,

    [Parameter(Mandatory = $true)]
    [string]$KeyVaultName,

    [string]$ImageTag = "managed",
    [string]$Location = "",
    [string]$ApiHostname = "api.openjibo.com",
    [string]$SocketHostname = "open-jibo-socket.openjibo.com",
    [string]$NeoHubHostname = "neohub.openjibo.com",
    [string]$NativeCompatibilityApiHostname = "open-jibo.jibo.pro",
    [string]$NativeCompatibilitySocketHostname = "open-jibo-socket.jibo.pro",
    [Parameter(Mandatory = $true)]
    [string]$RegistryName,
    [bool]$EnableAzureSpeech = $true,
    [switch]$DisableAzureSpeech,
    [string]$AzureSpeechRegion = "",
    [string]$TemplatePath = "infra/azure/container-apps/openjibo-managed.bicep",
    [string]$ParametersPath = "infra/azure/container-apps/openjibo-managed.parameters.json",
    [switch]$RunMigration,
    [switch]$RunSmoke,
    [switch]$SkipHostnameBinding
)

$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$resolvedTemplatePath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $TemplatePath))
$resolvedParametersPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $ParametersPath))

if (-not (Test-Path -LiteralPath $resolvedTemplatePath)) {
    throw "Could not find Bicep template at $resolvedTemplatePath"
}

if (-not (Test-Path -LiteralPath $resolvedParametersPath)) {
    throw "Could not find parameter file at $resolvedParametersPath"
}

if ([string]::IsNullOrWhiteSpace($KeyVaultName)) {
    throw "KeyVaultName is required."
}

if ([string]::IsNullOrWhiteSpace($RegistryName)) {
    throw "RegistryName is required."
}

$RegistryLoginServer = "$RegistryName.azurecr.io"
$deploymentName = "openjibo-managed-{0}" -f ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())

$stateConnectionString = az keyvault secret show --vault-name $KeyVaultName --name openjibo-state-connection-string --query value -o tsv
$personalMemoryConnectionString = az keyvault secret show --vault-name $KeyVaultName --name openjibo-personal-memory-connection-string --query value -o tsv
$mediaConnectionString = az keyvault secret show --vault-name $KeyVaultName --name openjibo-media-connection-string --query value -o tsv
$openWeatherApiKey = az keyvault secret show --vault-name $KeyVaultName --name openjibo-openweather-api-key --query value -o tsv
$newsApiKey = az keyvault secret show --vault-name $KeyVaultName --name openjibo-newsapi-key --query value -o tsv
$searchBackend = ""
$searchFallback = ""
$portalStatusPassword = az keyvault secret show --vault-name $KeyVaultName --name openjibo-portal-status-password --query value -o tsv
$peerSyncSharedKey = az keyvault secret show --vault-name $KeyVaultName --name openjibo-peer-sync-shared-key --query value -o tsv
$userEncryptionPassphrase = az keyvault secret show --vault-name $KeyVaultName --name openjibo-user-encrypt --query value -o tsv
$userEncryptionSalt = az keyvault secret show --vault-name $KeyVaultName --name openjibo-user-salt --query value -o tsv

if ([string]::IsNullOrWhiteSpace($userEncryptionPassphrase) -or [string]::IsNullOrWhiteSpace($userEncryptionSalt)) {
    throw "Managed user encryption secrets are missing from Key Vault '$KeyVaultName'."
}

function Get-OptionalKeyVaultSecretValue {
    param(
        [string]$VaultName,
        [string]$Name
    )

    try {
        return Invoke-OpenJiboAzWithRetry `
            -Arguments @("keyvault", "secret", "show", "--vault-name", $VaultName, "--name", $Name, "--query", "value", "--output", "tsv") `
            -Description "Optional Key Vault secret lookup for '$Name'" `
            -Attempts 4
    }
    catch {
        return ""
    }
}

$searchBackend = Get-OptionalKeyVaultSecretValue -VaultName $KeyVaultName -Name openjibo-search-backend
$searchFallback = Get-OptionalKeyVaultSecretValue -VaultName $KeyVaultName -Name openjibo-search-fallback

function Get-PostgresServerNameFromConnectionString {
    param([string]$ConnectionString)

    foreach ($segment in $ConnectionString.Split(";")) {
        $parts = $segment.Split("=", 2)
        if ($parts.Length -eq 2 -and $parts[0].Trim().Equals("Host", [System.StringComparison]::OrdinalIgnoreCase)) {
            return ($parts[1].Trim().Split(".", 2))[0]
        }
    }

    throw "Could not determine the PostgreSQL server name from the connection string."
}

function Ensure-PostgresFirewallRule {
    param(
        [string]$ServerName,
        [string]$RuleName,
        [string]$IpAddress
    )

    $helpText = & az postgres flexible-server firewall-rule create -h 2>&1
    $serverFlag = if ($helpText -match '--server-name') { '--server-name' } else { '--name' }
    $ruleFlag = if ($helpText -match '--rule-name') { '--rule-name' } else { '--name' }

    $showArgs = @(
        "postgres", "flexible-server", "firewall-rule", "show",
        "--resource-group", $ResourceGroupName,
        $serverFlag, $ServerName,
        $ruleFlag, $RuleName,
        "--output", "none"
    )

    & az @showArgs *> $null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Refreshing PostgreSQL firewall rule '$RuleName' for server '$ServerName'."
        & az postgres flexible-server firewall-rule update `
            --resource-group $ResourceGroupName `
            $serverFlag $ServerName `
            $ruleFlag $RuleName `
            --start-ip-address $IpAddress `
            --end-ip-address $IpAddress `
            --output none | Out-Null
        return
    }

    Write-Host "Creating PostgreSQL firewall rule '$RuleName' for server '$ServerName'."
    & az postgres flexible-server firewall-rule create `
        --resource-group $ResourceGroupName `
        $serverFlag $ServerName `
        $ruleFlag $RuleName `
        --start-ip-address $IpAddress `
        --end-ip-address $IpAddress `
        --output none | Out-Null
}

function Remove-PostgresFirewallRule {
    param(
        [string]$ServerName,
        [string]$RuleName
    )

    & az postgres flexible-server firewall-rule delete `
        --resource-group $ResourceGroupName `
        --name $ServerName `
        --rule-name $RuleName `
        --yes `
        --output none *> $null

    if ($LASTEXITCODE -eq 0) {
        Write-Host "Removed PostgreSQL firewall rule '$RuleName' from server '$ServerName'."
    }
}

function Set-ContainerAppSecretsFromKeyVault {
    param(
        [string]$ContainerAppName,
        [string]$StateConnectionString,
        [string]$PersonalMemoryConnectionString,
        [string]$SearchBackend,
        [string]$SearchFallback,
        [string]$PortalStatusPassword,
        [string]$PeerSyncSharedKey,
        [string]$UserEncryptionPassphrase,
        [string]$UserEncryptionSalt
    )

    if ([string]::IsNullOrWhiteSpace($ContainerAppName)) {
        return
    }

    if ([string]::IsNullOrWhiteSpace($StateConnectionString) -or [string]::IsNullOrWhiteSpace($PersonalMemoryConnectionString)) {
        throw "Managed Container App secret refresh requires both PostgreSQL connection strings."
    }

    Write-Host "Refreshing Container App '$ContainerAppName' secrets from Key Vault to force the latest managed values into the revision."
    $secretArgs = @(
            "state-connection-string=$StateConnectionString",
            "personal-memory-connection-string=$PersonalMemoryConnectionString",
            "portal-status-password=$PortalStatusPassword",
            "peer-sync-shared-key=$PeerSyncSharedKey",
            "user-encryption-passphrase=$UserEncryptionPassphrase",
            "user-encryption-salt=$UserEncryptionSalt"
        )

    if (-not [string]::IsNullOrWhiteSpace($SearchBackend)) {
        $secretArgs += "search-backend=$SearchBackend"
    }

    if (-not [string]::IsNullOrWhiteSpace($SearchFallback)) {
        $secretArgs += "search-fallback=$SearchFallback"
    }

    $containerAppSecretArguments = @(
        "containerapp", "secret", "set",
        "--resource-group", $ResourceGroupName,
        "--name", $ContainerAppName,
        "--secrets"
    )
    $containerAppSecretArguments += $secretArgs
    $containerAppSecretArguments += "--output"
    $containerAppSecretArguments += "none"

    Invoke-OpenJiboAzWithRetry `
        -Arguments $containerAppSecretArguments `
        -Description "Container App secret refresh for '$ContainerAppName'" `
        -Attempts 6 | Out-Null
    Write-Host "Container App managed secrets refreshed for '$ContainerAppName'."
}

function Restart-ContainerAppRevision {
    param(
        [string]$ContainerAppName
    )

    if ([string]::IsNullOrWhiteSpace($ContainerAppName)) {
        return
    }

    $latestRevisionName = ""
    try {
        $latestRevisionName = Invoke-OpenJiboAzWithRetry `
            -Arguments @(
                "containerapp", "show",
                "--resource-group", $ResourceGroupName,
                "--name", $ContainerAppName,
                "--query", "properties.latestRevisionName",
                "--output", "tsv"
            ) `
            -Description "Container App latest revision lookup for '$ContainerAppName'" `
            -Attempts 4
    }
    catch {
        throw "Could not resolve the latest revision name for Container App '$ContainerAppName' to restart it after secret changes. $_"
    }

    if ([string]::IsNullOrWhiteSpace($latestRevisionName)) {
        throw "Container App '$ContainerAppName' did not report a latest revision name."
    }

    Write-Host "Restarting Container App revision '$latestRevisionName' so the refreshed secrets take effect."
    Invoke-OpenJiboAzWithRetry `
        -Arguments @(
            "containerapp", "revision", "restart",
            "--resource-group", $ResourceGroupName,
            "--name", $ContainerAppName,
            "--revision", $latestRevisionName,
            "--output", "none"
        ) `
        -Description "Container App revision restart for '$ContainerAppName'" `
        -Attempts 4 | Out-Null
    Write-Host "Container App revision '$latestRevisionName' restarted."
}

function Bind-ContainerAppHostname {
    param(
        [string]$ContainerAppName,
        [string]$ManagedEnvironmentName,
        [string]$Hostname
    )

    if ([string]::IsNullOrWhiteSpace($Hostname)) {
        return
    }

    Write-Host "Adding hostname '$Hostname' to Container App '$ContainerAppName'. DNS must point directly at the generated Container App hostname before Azure can issue the managed certificate."
    & az containerapp hostname add `
        --resource-group $ResourceGroupName `
        --name $ContainerAppName `
        --hostname $Hostname `
        --output none
    Write-Host "Hostname '$Hostname' added. Binding the managed certificate for Container App '$ContainerAppName'."
    & az containerapp hostname bind `
        --resource-group $ResourceGroupName `
        --name $ContainerAppName `
        --hostname $Hostname `
        --environment $ManagedEnvironmentName `
        --validation-method CNAME `
        --output none
    Write-Host "Managed certificate binding completed for hostname '$Hostname'."
}

$arguments = @(
    "deployment", "group", "create",
    "--resource-group", $ResourceGroupName,
    "--name", $deploymentName,
    "--template-file", $resolvedTemplatePath,
    "--parameters", "@$resolvedParametersPath",
    "--parameters", "registryLoginServer=$RegistryLoginServer",
    "--parameters", "keyVaultName=$KeyVaultName",
    "--parameters", "imageTag=$ImageTag",
    "--parameters", "apiHostname=$ApiHostname",
    "--parameters", "socketHostname=$SocketHostname",
    "--parameters", "neoHubHostname=$NeoHubHostname",
    "--parameters", "nativeCompatibilityApiHostname=$NativeCompatibilityApiHostname",
    "--parameters", "nativeCompatibilitySocketHostname=$NativeCompatibilitySocketHostname",
    "--parameters", "stateConnectionString=$stateConnectionString",
    "--parameters", "personalMemoryConnectionString=$personalMemoryConnectionString",
    "--parameters", "mediaConnectionString=$mediaConnectionString",
    "--parameters", "openWeatherApiKey=$openWeatherApiKey",
    "--parameters", "newsApiKey=$newsApiKey",
    "--parameters", "portalStatusPassword=$portalStatusPassword",
    "--parameters", "peerSyncSharedKey=$peerSyncSharedKey",
    "--parameters", "userEncryptionPassphrase=$userEncryptionPassphrase",
    "--parameters", "userEncryptionSalt=$userEncryptionSalt"
)

if (-not [string]::IsNullOrWhiteSpace($Location)) {
    $arguments += @("--parameters", "location=$Location")
}

if (-not [string]::IsNullOrWhiteSpace($searchBackend)) {
    $arguments += @("--parameters", "searchBackend=$searchBackend")
}

if (-not [string]::IsNullOrWhiteSpace($searchFallback)) {
    $arguments += @("--parameters", "searchFallback=$searchFallback")
}

if ($DisableAzureSpeech) {
    $EnableAzureSpeech = $false
}

if ($EnableAzureSpeech) {
    $arguments += @("--parameters", "enableAzureSpeech=true")
    $azureSpeechSubscriptionKey = az keyvault secret show --vault-name $KeyVaultName --name azure-speech-subscription-key --query value -o tsv
    if ([string]::IsNullOrWhiteSpace($azureSpeechSubscriptionKey)) {
        throw "Could not read azure-speech-subscription-key from Key Vault '$KeyVaultName'."
    }
    $arguments += @("--parameters", "azureSpeechSubscriptionKey=$azureSpeechSubscriptionKey")
    if (-not [string]::IsNullOrWhiteSpace($AzureSpeechRegion)) {
        $arguments += @("--parameters", "azureSpeechRegion=$AzureSpeechRegion")
    }
}

$arguments += @("--output", "json")

if ($RunMigration) {
    $migrationScript = Join-Path $repoRoot "scripts/cloud/Invoke-OpenJiboMigration.ps1"
    $previousEncrypt = [Environment]::GetEnvironmentVariable("OPENJIBO_USER_ENCRYPT", "Process")
    $previousSalt = [Environment]::GetEnvironmentVariable("OPENJIBO_USER_SALT", "Process")
    try {
        [Environment]::SetEnvironmentVariable("OPENJIBO_USER_ENCRYPT", $userEncryptionPassphrase, "Process")
        [Environment]::SetEnvironmentVariable("OPENJIBO_USER_SALT", $userEncryptionSalt, "Process")
        & $migrationScript `
            -Target all `
            -StateConnectionString $stateConnectionString `
            -PersonalMemoryConnectionString $personalMemoryConnectionString `
            -MediaConnectionString $mediaConnectionString `
            -ImportLegacyCloudState `
            -ImportLegacyPersonalMemory `
            -Verify
    }
    finally {
        [Environment]::SetEnvironmentVariable("OPENJIBO_USER_ENCRYPT", $previousEncrypt, "Process")
        [Environment]::SetEnvironmentVariable("OPENJIBO_USER_SALT", $previousSalt, "Process")
    }
}

Write-Host "Deploying Open Jibo managed Container Apps stack to resource group '$ResourceGroupName'"
$deploymentJson = az @arguments | ConvertFrom-Json

if (-not $SkipHostnameBinding -and -not [string]::IsNullOrWhiteSpace($ApiHostname)) {
    $containerAppName = $deploymentJson.properties.outputs.containerAppName.value
    $managedEnvironmentName = $deploymentJson.properties.outputs.managedEnvironmentName.value
    if ([string]::IsNullOrWhiteSpace($containerAppName)) {
        throw "Container app name was not returned from the deployment."
    }
    if ([string]::IsNullOrWhiteSpace($managedEnvironmentName)) {
        throw "Managed environment name was not returned from the deployment."
    }

    Bind-ContainerAppHostname -ContainerAppName $containerAppName -ManagedEnvironmentName $managedEnvironmentName -Hostname $ApiHostname
    Bind-ContainerAppHostname -ContainerAppName $containerAppName -ManagedEnvironmentName $managedEnvironmentName -Hostname $SocketHostname
    Bind-ContainerAppHostname -ContainerAppName $containerAppName -ManagedEnvironmentName $managedEnvironmentName -Hostname $NeoHubHostname
    Bind-ContainerAppHostname -ContainerAppName $containerAppName -ManagedEnvironmentName $managedEnvironmentName -Hostname $NativeCompatibilityApiHostname
    Bind-ContainerAppHostname -ContainerAppName $containerAppName -ManagedEnvironmentName $managedEnvironmentName -Hostname $NativeCompatibilitySocketHostname

    $stateConnectionString = az keyvault secret show --vault-name $KeyVaultName --name openjibo-state-connection-string --query value -o tsv
    $postgresServerName = Get-PostgresServerNameFromConnectionString -ConnectionString $stateConnectionString
    $environmentJsonText = az containerapp env show `
        --resource-group $ResourceGroupName `
        --name $managedEnvironmentName `
        --output json `
        --only-show-errors 2>$null
    $outboundIpAddresses = @()

    if (-not [string]::IsNullOrWhiteSpace($environmentJsonText)) {
        try {
            $environmentJson = $environmentJsonText | ConvertFrom-Json
        }
        catch {
            Write-Warning "Container Apps environment lookup did not return valid JSON for PostgreSQL firewall rules."
            $environmentJson = $null
        }
    }

    function Get-CandidateIpAddresses {
        param([object]$Value)

        if ($null -eq $Value) {
            return
        }

        if ($Value -is [string]) {
            if ($Value -match '^(?:\d{1,3}\.){3}\d{1,3}$') {
                $script:outboundIpAddresses += $Value
            }
            return
        }

        if ($Value -is [System.Collections.IDictionary]) {
            foreach ($entry in $Value.GetEnumerator()) {
                if ($entry.Key -in @("outboundIpAddresses", "staticIp", "staticIpAddress")) {
                    Get-CandidateIpAddresses -Value $entry.Value
                }
                else {
                    Get-CandidateIpAddresses -Value $entry.Value
                }
            }
            return
        }

        if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
            foreach ($item in $Value) {
                Get-CandidateIpAddresses -Value $item
            }
        }
    }

    Get-CandidateIpAddresses -Value $environmentJson

    if ($outboundIpAddresses.Count -eq 0) {
        Write-Warning "Container Apps environment did not report any outbound IP addresses for PostgreSQL firewall rules."
    }

    foreach ($outboundIpAddress in ($outboundIpAddresses | Select-Object -Unique)) {
        $ruleName = "AllowContainerApps-{0}" -f $outboundIpAddress.Replace(".", "-")
        Write-Host "Ensuring PostgreSQL firewall rule '$ruleName' for Container Apps outbound IP '$outboundIpAddress'."
        Ensure-PostgresFirewallRule -ServerName $postgresServerName -RuleName $ruleName -IpAddress $outboundIpAddress
    }

    Start-Sleep -Seconds 30
}

$stateConnectionString = az keyvault secret show --vault-name $KeyVaultName --name openjibo-state-connection-string --query value -o tsv
$personalMemoryConnectionString = az keyvault secret show --vault-name $KeyVaultName --name openjibo-personal-memory-connection-string --query value -o tsv
Set-ContainerAppSecretsFromKeyVault -ContainerAppName $deploymentJson.properties.outputs.containerAppName.value -StateConnectionString $stateConnectionString -PersonalMemoryConnectionString $personalMemoryConnectionString -SearchBackend $searchBackend -SearchFallback $searchFallback -PortalStatusPassword $portalStatusPassword -PeerSyncSharedKey $peerSyncSharedKey -UserEncryptionPassphrase $userEncryptionPassphrase -UserEncryptionSalt $userEncryptionSalt
Restart-ContainerAppRevision -ContainerAppName $deploymentJson.properties.outputs.containerAppName.value
Start-Sleep -Seconds 20


if ($RunSmoke) {
    $smokeBaseUrl = if (-not [string]::IsNullOrWhiteSpace($ApiHostname)) {
        "https://$ApiHostname"
    } else {
        $containerAppFqdn = $deploymentJson.properties.outputs.containerAppFqdn.value
        if ([string]::IsNullOrWhiteSpace($containerAppFqdn)) {
            throw "Container app FQDN was not returned from the deployment."
        }

        "https://$containerAppFqdn"
    }

    $smokeScript = Join-Path $repoRoot "scripts/cloud/Invoke-CloudSmoke.ps1"
    & $smokeScript -BaseUrl $smokeBaseUrl
}

$stateConnectionStringForCleanup = az keyvault secret show --vault-name $KeyVaultName --name openjibo-state-connection-string --query value -o tsv
if (-not [string]::IsNullOrWhiteSpace($stateConnectionStringForCleanup)) {
    $postgresServerName = Get-PostgresServerNameFromConnectionString -ConnectionString $stateConnectionStringForCleanup
    Write-Host "Removing temporary deployment runner firewall rule 'AllowDeploymentRunner' from server '$postgresServerName'."
    Remove-PostgresFirewallRule -ServerName $postgresServerName -RuleName "AllowDeploymentRunner"
}

$deploymentJson
