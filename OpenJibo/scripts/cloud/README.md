# Cloud Scripts

These scripts help exercise the new .NET hosted cloud locally.

- `Invoke-CloudSmoke.ps1`
  Runs a few quick HTTP checks against a local OpenJibo cloud instance.
- `Invoke-ProtocolFixture.ps1`
  Replays a sanitized HTTP fixture against a running local instance.
- `Get-WebSocketCaptureSummary.ps1`
  Summarizes captured websocket telemetry events and exported live-run fixtures from the .NET cloud.
- `Invoke-LiveJiboPrep.ps1`
  Runs a small readiness checklist before the first physical Jibo test against the .NET cloud.
- `Import-WebSocketCaptureFixture.ps1`
  Sanitizes an exported websocket capture fixture and copies it into the checked-in websocket fixture set.
