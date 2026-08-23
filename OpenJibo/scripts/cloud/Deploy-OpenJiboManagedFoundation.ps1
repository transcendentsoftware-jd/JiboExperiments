param(
    [Parameter(Mandatory = $true)]
    [string]$ResourceGroupName,

    [string]$TemplatePath = "infra/azure/foundation/openjibo-managed-foundation.bicep",
    [string]$StateConnectionString = "",
    [string]$PersonalMemoryConnectionString = "",
    [string]$OpenWeatherApiKey = "",
    [string]$NewsApiKey = "",
    [string]$SearchBackend = "",
    [string]$SearchFallback = "",
    [string]$PostgresAdminLogin = "openjiboadmin",
    [string]$PostgresAdminPassword = "",
    [string]$PostgresServerName = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$resolvedTemplatePath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $TemplatePath))

if (-not (Test-Path -LiteralPath $resolvedTemplatePath)) {
    throw "Could not find Bicep template at $resolvedTemplatePath"
}

function New-OpenJiboPostgresPassword {
    $lower = "abcdefghijklmnopqrstuvwxyz"
    $upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ"
    $digits = "0123456789"
    $symbols = "!#$%+?@_"
    $all = $lower + $upper + $digits + $symbols
    $characters = New-Object System.Collections.Generic.List[char]

    foreach ($set in @($lower, $upper, $digits, $symbols)) {
        $characters.Add($set[[System.Security.Cryptography.RandomNumberGenerator]::GetInt32($set.Length)])
    }

    for ($index = $characters.Count; $index -lt 32; $index++) {
        $characters.Add($all[[System.Security.Cryptography.RandomNumberGenerator]::GetInt32($all.Length)])
    }

    for ($index = $characters.Count - 1; $index -gt 0; $index--) {
        $swapIndex = [System.Security.Cryptography.RandomNumberGenerator]::GetInt32($index + 1)
        $temporary = $characters[$index]
        $characters[$index] = $characters[$swapIndex]
        $characters[$swapIndex] = $temporary
    }

    -join $characters
}

function Get-OpenJiboKeyVaultSecretValue {
    param(
        [string]$VaultName,
        [string]$Name
    )

    try {
        $value = Invoke-OpenJiboAzWithRetry `
            -Arguments @("keyvault", "secret", "show", "--vault-name", $VaultName, "--name", $Name, "--query", "value", "--output", "tsv") `
            -Description "Key Vault secret lookup for '$Name'" `
            -Attempts 4
        return $value
    }
    catch {
        return ""
    }
}

function Get-OpenJiboManagedKeyVaultName {
    try {
        return Invoke-OpenJiboAzWithRetry `
            -Arguments @("keyvault", "list", "--resource-group", $ResourceGroupName, "--query", "[?starts_with(name, 'kv-')].name | [0]", "--output", "tsv") `
            -Description "Managed Key Vault lookup" `
            -Attempts 3
    }
    catch {
        return ""
    }
}

function Invoke-OpenJiboAzWithRetry {
    param(
        [string[]]$Arguments,
        [string]$Description,
        [int]$Attempts = 8
    )

    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        $output = & az @Arguments 2>&1
        if ($LASTEXITCODE -eq 0) {
            return (($output | Out-String).Trim())
        }

        if ($attempt -eq $Attempts) {
            throw "$Description failed after $Attempts attempts. $output"
        }

        $waitSeconds = [Math]::Min(90, $attempt * 10)
        $lastLine = (($output | Select-Object -Last 1) | Out-String).Trim()
        if ([string]::IsNullOrWhiteSpace($lastLine)) {
            Write-Warning "$Description failed; retrying in $waitSeconds seconds."
        }
        else {
            Write-Warning "$Description failed; retrying in $waitSeconds seconds. Last Azure CLI message: $lastLine"
        }

        Start-Sleep -Seconds $waitSeconds
    }
}

function Sync-OpenJiboPostgresAdminPassword {
    param(
        [string]$ServerName,
        [string]$AdminPassword
    )

    if ([string]::IsNullOrWhiteSpace($ServerName) -or [string]::IsNullOrWhiteSpace($AdminPassword)) {
        return
    }

    Write-Host "Synchronizing PostgreSQL server admin password for '$ServerName' with the selected foundation password."
    Invoke-OpenJiboAzWithRetry `
        -Arguments @(
            "postgres", "flexible-server", "update",
            "--resource-group", $ResourceGroupName,
            "--name", $ServerName,
            "--admin-password", $AdminPassword,
            "--output", "none"
        ) `
        -Description "PostgreSQL admin password synchronization for '$ServerName'" `
        -Attempts 6 | Out-Null
    Write-Host "PostgreSQL server admin password synchronized for '$ServerName'."
}

$existingManagedKeyVaultName = Get-OpenJiboManagedKeyVaultName

if ([string]::IsNullOrWhiteSpace($PostgresAdminPassword) -and -not [string]::IsNullOrWhiteSpace($existingManagedKeyVaultName)) {
    Write-Host "Found existing managed Key Vault '$existingManagedKeyVaultName'; checking for a stored PostgreSQL admin password."
    $existingPostgresAdminPassword = Get-OpenJiboKeyVaultSecretValue -VaultName $existingManagedKeyVaultName -Name openjibo-postgres-admin-password
    if (-not [string]::IsNullOrWhiteSpace($existingPostgresAdminPassword)) {
        $PostgresAdminPassword = $existingPostgresAdminPassword
        Write-Host "Reusing existing PostgreSQL admin password from Key Vault."
    }
}

if ([string]::IsNullOrWhiteSpace($PostgresAdminPassword)) {
    $PostgresAdminPassword = New-OpenJiboPostgresPassword
    if ([string]::IsNullOrWhiteSpace($existingManagedKeyVaultName)) {
        Write-Host "No existing managed Key Vault was found; generating the initial PostgreSQL admin password."
        Write-Host "Generated a new PostgreSQL admin password for the initial foundation deployment."
    }
    else {
        Write-Host "Existing managed Key Vault '$existingManagedKeyVaultName' did not return a stored PostgreSQL admin password."
        Write-Host "Managed Key Vault did not return a stored PostgreSQL password; generated a replacement for the foundation deployment."
    }
}

$deploymentRunnerIp = ""
try {
    $candidateIp = (Invoke-RestMethod -Uri "https://api.ipify.org" -TimeoutSec 10).ToString()
    if ($candidateIp -match '^([0-9]{1,3}\.){3}[0-9]{1,3}$') {
        $deploymentRunnerIp = $candidateIp
    }
}
catch {
    Write-Warning "Could not resolve deployment runner public IP for the PostgreSQL migration firewall rule: $_"
}

$deploymentName = "openjibo-foundation-{0}" -f ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())

$currentPrincipalId = ""
try {
    $accessToken = az account get-access-token --query accessToken --output tsv
    $payload = $accessToken.Split(".")[1]
    $payload = $payload.PadRight($payload.Length + ((4 - ($payload.Length % 4)) % 4), "=")
    $claimsJson = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload.Replace("-", "+").Replace("_", "/")))
    $currentPrincipalId = (ConvertFrom-Json $claimsJson).oid
}
catch {
    Write-Warning "Could not resolve current Azure principal object id: $_"
}

$arguments = @(
    "deployment", "group", "create",
    "--resource-group", $ResourceGroupName,
    "--name", $deploymentName,
    "--template-file", $resolvedTemplatePath,
    "--parameters", "postgresAdministratorLogin=$PostgresAdminLogin",
    "--parameters", "postgresAdministratorPassword=$PostgresAdminPassword"
)

if (-not [string]::IsNullOrWhiteSpace($PostgresServerName)) {
    $arguments += @("--parameters", "postgresServerName=$PostgresServerName")
}

if (-not [string]::IsNullOrWhiteSpace($deploymentRunnerIp)) {
    $arguments += @("--parameters", "postgresDeploymentRunnerFirewallIpAddress=$deploymentRunnerIp")
}

if (-not [string]::IsNullOrWhiteSpace($currentPrincipalId)) {
    $arguments += @(
        "--parameters",
        "seedPrincipalObjectId=$currentPrincipalId"
    )
}

$arguments += @(
    "--output", "json"
)

Write-Host "Deploying Open Jibo managed foundation to resource group '$ResourceGroupName'"
$deploymentJson = az @arguments | ConvertFrom-Json
$outputs = $deploymentJson.properties.outputs

Sync-OpenJiboPostgresAdminPassword -ServerName $outputs.postgresServerName.value -AdminPassword $PostgresAdminPassword

$storageConnectionString = Invoke-OpenJiboAzWithRetry `
    -Arguments @("storage", "account", "show-connection-string", "--resource-group", $ResourceGroupName, "--name", $outputs.storageAccountName.value, "--query", "connectionString", "--output", "tsv") `
    -Description "Storage connection string lookup"

 # az cognitiveservices account keys list
$speechSubscriptionKey = Invoke-OpenJiboAzWithRetry `
    -Arguments @("cognitiveservices", "account", "keys", "list", "--resource-group", $ResourceGroupName, "--name", $outputs.speechServicesAccountName.value, "--query", "key1", "--output", "tsv") `
    -Description "Azure Speech subscription key lookup"

function New-OpenJiboPostgresConnectionString {
    param(
        [string]$DatabaseName
    )

    "Host=$($outputs.postgresFullyQualifiedDomainName.value);Port=5432;Database=$DatabaseName;Username=$($outputs.postgresAdministratorLogin.value);Password=$PostgresAdminPassword;SSL Mode=Require;Trust Server Certificate=true"
}

$resolvedStateConnectionString = if ([string]::IsNullOrWhiteSpace($StateConnectionString)) { New-OpenJiboPostgresConnectionString -DatabaseName $outputs.postgresStateDatabaseName.value } else { $StateConnectionString }
$resolvedPersonalMemoryConnectionString = if ([string]::IsNullOrWhiteSpace($PersonalMemoryConnectionString)) { New-OpenJiboPostgresConnectionString -DatabaseName $outputs.postgresPersonalMemoryDatabaseName.value } else { $PersonalMemoryConnectionString }

function Set-OpenJiboKeyVaultSecretWithRetry {
    param(
        [string]$VaultName,
        [string]$Name,
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return
    }

    Invoke-OpenJiboAzWithRetry `
        -Arguments @("keyvault", "secret", "set", "--vault-name", $VaultName, "--name", $Name, "--value", $Value) `
        -Description "Key Vault secret set for '$Name'" `
        -Attempts 6 | Out-Null
}

function Set-OpenJiboKeyVaultSecretIfChanged {
    param(
        [string]$VaultName,
        [string]$Name,
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return
    }

    $existingValue = ""
    try {
        $existingValue = Invoke-OpenJiboAzWithRetry `
            -Arguments @("keyvault", "secret", "show", "--vault-name", $VaultName, "--name", $Name, "--query", "value", "--output", "tsv") `
            -Description "Key Vault secret lookup for '$Name'" `
            -Attempts 4
    }
    catch {
        $existingValue = ""
    }

    if (-not [string]::IsNullOrWhiteSpace($existingValue) -and $existingValue -ceq $Value) {
        Write-Host "Key Vault secret '$Name' already matches the desired value; skipping write."
        return
    }

    if ([string]::IsNullOrWhiteSpace($existingValue)) {
        Write-Host "Key Vault secret '$Name' is missing or unreadable; creating or refreshing it."
    }
    else {
        Write-Host "Key Vault secret '$Name' changed; refreshing it."
    }

    Set-OpenJiboKeyVaultSecretWithRetry -VaultName $VaultName -Name $Name -Value $Value
}

function Get-OrCreateOpenJiboRandomSecret {
    param(
        [string]$VaultName,
        [string]$Name,
        [int]$ByteCount
    )

    try {
        $existing = Invoke-OpenJiboAzWithRetry `
            -Arguments @("keyvault", "secret", "show", "--vault-name", $VaultName, "--name", $Name, "--query", "value", "--output", "tsv") `
            -Description "Key Vault secret lookup for '$Name'" `
            -Attempts 4
        if (-not [string]::IsNullOrWhiteSpace($existing)) {
            return $existing
        }
    }
    catch {
    }

    $value = ([Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes($ByteCount))).TrimEnd("=").Replace("+", "-").Replace("/", "_")
    Set-OpenJiboKeyVaultSecretWithRetry -VaultName $VaultName -Name $Name -Value $value
    Write-Host "Created managed secret '$Name'."
    return $value
}
Set-OpenJiboKeyVaultSecretIfChanged -VaultName $outputs.keyVaultName.value -Name openjibo-state-connection-string -Value $resolvedStateConnectionString

Set-OpenJiboKeyVaultSecretIfChanged -VaultName $outputs.keyVaultName.value -Name openjibo-personal-memory-connection-string -Value $resolvedPersonalMemoryConnectionString

Set-OpenJiboKeyVaultSecretIfChanged -VaultName $outputs.keyVaultName.value -Name openjibo-media-connection-string -Value $storageConnectionString

Set-OpenJiboKeyVaultSecretIfChanged -VaultName $outputs.keyVaultName.value -Name azure-speech-subscription-key -Value $speechSubscriptionKey

Set-OpenJiboKeyVaultSecretIfChanged -VaultName $outputs.keyVaultName.value -Name openjibo-postgres-admin-password -Value $PostgresAdminPassword

Set-OpenJiboKeyVaultSecretIfChanged -VaultName $outputs.keyVaultName.value -Name openjibo-openweather-api-key -Value $OpenWeatherApiKey

Set-OpenJiboKeyVaultSecretIfChanged -VaultName $outputs.keyVaultName.value -Name openjibo-newsapi-key -Value $NewsApiKey

Set-OpenJiboKeyVaultSecretIfChanged -VaultName $outputs.keyVaultName.value -Name openjibo-search-backend -Value $SearchBackend

Set-OpenJiboKeyVaultSecretIfChanged -VaultName $outputs.keyVaultName.value -Name openjibo-search-fallback -Value $SearchFallback

$null = Get-OrCreateOpenJiboRandomSecret -VaultName $outputs.keyVaultName.value -Name openjibo-user-encrypt -ByteCount 48
$null = Get-OrCreateOpenJiboRandomSecret -VaultName $outputs.keyVaultName.value -Name openjibo-user-salt -ByteCount 24

$deploymentJson
