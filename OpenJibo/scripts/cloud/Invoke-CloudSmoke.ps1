param(
    [string]$BaseUrl = "http://localhost:5000"
)

$checks = @(
    @{ Name = "Health"; Method = "GET"; Url = "$BaseUrl/health"; Headers = @{}; Body = $null },
    @{ Name = "CreateHubToken"; Method = "POST"; Url = "$BaseUrl/"; Headers = @{ "X-Amz-Target" = "Account_20160715.CreateHubToken"; Host = "api.jibo.com" }; Body = "{}" },
    @{ Name = "NewRobotToken"; Method = "POST"; Url = "$BaseUrl/"; Headers = @{ "X-Amz-Target" = "Notification_20160715.NewRobotToken"; Host = "api.jibo.com" }; Body = '{"deviceId":"my-robot-serial-number"}' }
)

foreach ($check in $checks) {
    try {
        $response = Invoke-WebRequest -Uri $check.Url -Method $check.Method -Headers $check.Headers -Body $check.Body -ContentType "application/json"
        [pscustomobject]@{
            Name = $check.Name
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
            Name = $check.Name
            StatusCode = $statusCode
            Success = $false
        }
    }
}
