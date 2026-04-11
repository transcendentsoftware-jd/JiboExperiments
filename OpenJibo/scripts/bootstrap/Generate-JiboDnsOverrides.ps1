param(
    [string]$TargetIp,
    [string[]]$HostNames = @(
        "api.jibo.com",
        "api-socket.jibo.com",
        "neo-hub.jibo.com",
        "neohub.jibo.com"
    )
)

if ([string]::IsNullOrWhiteSpace($TargetIp)) {
    throw "TargetIp is required."
}

$entries = foreach ($host in $HostNames) {
    [pscustomobject]@{
        Host = $host
        TargetIp = $TargetIp
        HostsFileLine = "$TargetIp`t$host"
    }
}

$entries | Format-Table -AutoSize
