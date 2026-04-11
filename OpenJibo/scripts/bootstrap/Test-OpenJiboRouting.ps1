param(
    [string[]]$Hosts = @(
        "https://api.jibo.com/health",
        "https://api-socket.jibo.com/",
        "https://neo-hub.jibo.com/v1/proactive"
    )
)

foreach ($url in $Hosts) {
    try {
        $response = Invoke-WebRequest -Uri $url -Method Get -SkipCertificateCheck -ErrorAction Stop
        [pscustomobject]@{
            Url = $url
            StatusCode = $response.StatusCode
            Success = $true
        }
    }
    catch {
        $statusCode = $null
        if ($_.Exception.Response -and $_.Exception.Response.StatusCode) {
            $statusCode = [int]$_.Exception.Response.StatusCode
        }

        [pscustomobject]@{
            Url = $url
            StatusCode = $statusCode
            Success = $false
        }
    }
}
