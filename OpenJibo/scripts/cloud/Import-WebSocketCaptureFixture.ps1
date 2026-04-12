param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath,
    [Parameter(Mandatory = $true)]
    [string]$FixtureName,
    [string]$DestinationDirectory = "..\..\src\Jibo.Cloud\node\fixtures\websocket",
    [switch]$Overwrite
)

$ErrorActionPreference = "Stop"

function Redact-Object {
    param(
        [Parameter(Mandatory = $true)]
        $Value
    )

    if ($null -eq $Value) {
        return $null
    }

    if ($Value -is [string]) {
        if ($Value -match "token" -or $Value -match "bearer" -or $Value -match "session") {
            return "[redacted]"
        }

        return $Value
    }

    if ($Value -is [System.Collections.IEnumerable] -and -not ($Value -is [string])) {
        $items = @()
        foreach ($item in $Value) {
            $items += ,(Redact-Object -Value $item)
        }

        return $items
    }

    if ($Value.PSObject -and $Value.PSObject.Properties.Count -gt 0) {
        $result = [ordered]@{}
        foreach ($property in $Value.PSObject.Properties) {
            if ($property.Name -match "token" -or $property.Name -match "authorization") {
                $result[$property.Name] = "[redacted]"
                continue
            }

            $result[$property.Name] = Redact-Object -Value $property.Value
        }

        return [pscustomobject]$result
    }

    return $Value
}

$resolvedSourcePath = Resolve-Path -LiteralPath $SourcePath -ErrorAction Stop
$resolvedDestinationDirectory = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $DestinationDirectory))
New-Item -ItemType Directory -Force -Path $resolvedDestinationDirectory | Out-Null

$fixture = Get-Content -LiteralPath $resolvedSourcePath -Raw | ConvertFrom-Json -Depth 50
$sanitized = Redact-Object -Value $fixture
$sanitized.name = $FixtureName
$sanitized.session.token = "[redacted]"

$destinationPath = Join-Path $resolvedDestinationDirectory ($FixtureName + ".flow.json")
if ((Test-Path -LiteralPath $destinationPath) -and -not $Overwrite) {
    throw "Destination fixture already exists: $destinationPath. Use -Overwrite to replace it."
}

$json = $sanitized | ConvertTo-Json -Depth 50
[System.IO.File]::WriteAllText($destinationPath, $json + [Environment]::NewLine)

Write-Host "Imported sanitized websocket fixture:"
Write-Host " - source: $resolvedSourcePath"
Write-Host " - destination: $destinationPath"
