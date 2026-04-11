param(
    [string]$LogDirectory = ".",
    [string[]]$KnownHosts = @(
        "api.jibo.com",
        "api-socket.jibo.com",
        "neo-hub.jibo.com"
    )
)

$resolved = foreach ($host in $KnownHosts) {
    try {
        $dns = Resolve-DnsName -Name $host -ErrorAction Stop
        [pscustomobject]@{
            Host = $host
            Addresses = ($dns | Where-Object { $_.IPAddress } | Select-Object -ExpandProperty IPAddress) -join ", "
            ObservedUtc = [DateTime]::UtcNow.ToString("o")
        }
    }
    catch {
        [pscustomobject]@{
            Host = $host
            Addresses = "<unresolved>"
            ObservedUtc = [DateTime]::UtcNow.ToString("o")
        }
    }
}

$resolved | Tee-Object -Variable rows | Format-Table -AutoSize

$outputPath = Join-Path $LogDirectory "jibo-host-discovery.json"
$rows | ConvertTo-Json -Depth 4 | Set-Content -Path $outputPath

Write-Host "Saved discovery report to $outputPath"
