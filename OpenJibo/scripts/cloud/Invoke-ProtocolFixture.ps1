param(
    [string]$BaseUrl = "http://localhost:5000",
    [string]$FixturePath
)

if ([string]::IsNullOrWhiteSpace($FixturePath)) {
    throw "FixturePath is required."
}

$fixture = Get-Content $FixturePath | ConvertFrom-Json
$headers = @{}

foreach ($property in $fixture.headers.PSObject.Properties) {
    $headers[$property.Name] = [string]$property.Value
}

if (-not $headers.ContainsKey("Host") -and $fixture.host) {
    $headers["Host"] = [string]$fixture.host
}

$body = ""
if ($null -ne $fixture.body) {
    $body = $fixture.body | ConvertTo-Json -Depth 10
}

$response = Invoke-WebRequest `
    -Uri ($BaseUrl + [string]$fixture.path) `
    -Method ([string]$fixture.method) `
    -Headers $headers `
    -Body $body `
    -ContentType "application/json"

[pscustomobject]@{
    Fixture = $FixturePath
    StatusCode = $response.StatusCode
    Body = $response.Content
}
