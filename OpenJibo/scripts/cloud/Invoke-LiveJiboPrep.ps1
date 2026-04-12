param(
    [string]$BaseUrl = "https://localhost:5001",
    [string[]]$ExpectedHosts = @(
        "api.jibo.com",
        "api-socket.jibo.com",
        "neo-hub.jibo.com"
    ),
    [string]$CaptureDirectory = "..\..\src\Jibo.Cloud\dotnet\src\Jibo.Cloud.Api\bin\Debug\net10.0\captures\websocket"
)

$ErrorActionPreference = "Stop"

Write-Host "OpenJibo live Jibo prep"
Write-Host ""

Write-Host "1. HTTP health check"
try {
    $health = Invoke-RestMethod -Uri ($BaseUrl.TrimEnd("/") + "/health") -Method Get
    $health | ConvertTo-Json -Depth 5
} catch {
    Write-Error "Health check failed against $BaseUrl. Start the .NET cloud and verify TLS/routing before continuing. $_"
}

Write-Host ""
Write-Host "2. Expected robot-facing hosts"
$ExpectedHosts | ForEach-Object { Write-Host " - $_" }

Write-Host ""
Write-Host "3. Capture directory"
$resolvedCaptureDirectory = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $CaptureDirectory))
Write-Host " - $resolvedCaptureDirectory"
if (-not (Test-Path -LiteralPath $resolvedCaptureDirectory)) {
    New-Item -ItemType Directory -Force -Path $resolvedCaptureDirectory | Out-Null
    Write-Host " - created"
}

Write-Host ""
Write-Host "4. Live-run checklist"
Write-Host " - keep the Ubuntu/Jibo routing setup in place"
Write-Host " - keep the Node server available as a fallback"
Write-Host " - point Jibo at the .NET server using the same controlled network settings"
Write-Host " - perform one startup check, one chat turn, and one joke turn"
Write-Host " - after the run, inspect capture output with scripts/cloud/Get-WebSocketCaptureSummary.ps1"
Write-Host " - import the best exported fixture with scripts/cloud/Import-WebSocketCaptureFixture.ps1"
