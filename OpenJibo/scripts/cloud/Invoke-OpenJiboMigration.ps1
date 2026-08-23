param(
    [string]$ProjectPath = "src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Migrations/Jibo.Cloud.Migrations.csproj",
    [string]$ScriptsDirectory = "src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Migrations/Migrations/PostgreSql",
    [ValidateSet("state", "personal-memory", "all")]
    [string]$Target = "all",
    [string]$StateConnectionString,
    [string]$PersonalMemoryConnectionString,
    [string]$MediaConnectionString,
    [string]$MediaContainer = "openjibo-media",
    [switch]$ImportLegacyCloudState,
    [switch]$ImportLegacyPersonalMemory,
    [switch]$Verify,
    [switch]$Preview,
    [switch]$VerboseOutput
)

$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$resolvedProjectPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $ProjectPath))

if (-not (Test-Path -LiteralPath $resolvedProjectPath)) {
    throw "Could not find migration project at $resolvedProjectPath"
}

$arguments = @(
    "run",
    "--project", $resolvedProjectPath,
    "--",
    "--target", $Target
)

$resolvedScriptsDirectory = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $ScriptsDirectory))
if (-not (Test-Path -LiteralPath $resolvedScriptsDirectory)) {
    throw "Could not find migration scripts at $resolvedScriptsDirectory"
}

$arguments += @("--scripts", $resolvedScriptsDirectory)

if (-not [string]::IsNullOrWhiteSpace($StateConnectionString)) {
    $arguments += @("--state-connection", $StateConnectionString)
}

if (-not [string]::IsNullOrWhiteSpace($PersonalMemoryConnectionString)) {
    $arguments += @("--memory-connection", $PersonalMemoryConnectionString)
}

if (-not [string]::IsNullOrWhiteSpace($MediaConnectionString)) {
    $arguments += @("--media-connection", $MediaConnectionString, "--media-container", $MediaContainer)
}

if ($ImportLegacyCloudState) {
    $arguments += "--import-legacy-cloud-state"
}

if ($ImportLegacyPersonalMemory) {
    $arguments += "--import-legacy-personal-memory"
}

if ($Verify) {
    $arguments += "--verify"
}

if ($Preview) {
    $arguments += "--preview"
} else {
    $arguments += "--apply"
}

if ($VerboseOutput) {
    $arguments += "--verbose"
}

Write-Host "Running Open Jibo migrations for target '$Target'"
dotnet @arguments
