param(
    [string]$CaptureDirectory = "..\..\src\Jibo.Cloud\dotnet\src\Jibo.Cloud.Api\bin\Debug\net10.0\captures\websocket"
)

$resolvedDirectory = Resolve-Path -LiteralPath $CaptureDirectory -ErrorAction Stop
$eventFiles = Get-ChildItem -LiteralPath $resolvedDirectory -Filter *.events.ndjson -File | Sort-Object LastWriteTimeUtc

if (-not $eventFiles) {
    Write-Host "No websocket telemetry event files found in $resolvedDirectory"
    exit 0
}

$records = foreach ($file in $eventFiles) {
    Get-Content -LiteralPath $file.FullName | Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object {
        $_ | ConvertFrom-Json
    }
}

$records |
    Group-Object EventType |
    Sort-Object Name |
    Select-Object Name, Count |
    Format-Table -AutoSize

$fixtureDirectory = Join-Path $resolvedDirectory "fixtures"
if (Test-Path -LiteralPath $fixtureDirectory) {
    Write-Host ""
    Write-Host "Exported websocket fixtures:"
    Get-ChildItem -LiteralPath $fixtureDirectory -Filter *.flow.json -File |
        Sort-Object LastWriteTimeUtc |
        Select-Object LastWriteTimeUtc, Name |
        Format-Table -AutoSize
}
