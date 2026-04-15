# Cloud Scripts

These scripts help exercise the new .NET hosted cloud locally.

- `Invoke-CloudSmoke.ps1`
  Runs a few quick HTTP checks against a local OpenJibo cloud instance.
- `Invoke-ProtocolFixture.ps1`
  Replays a sanitized HTTP fixture against a running local instance.
- `Get-WebSocketCaptureSummary.ps1`
  Summarizes captured websocket telemetry events and exported live-run fixtures from the .NET cloud.
- repo-root `captures/http/`
  Structured HTTP request/response telemetry for live robot startup comparison.
- `Invoke-LiveJiboPrep.ps1`
  Runs a small readiness checklist before the first physical Jibo test against the .NET cloud.
- `Import-WebSocketCaptureFixture.ps1`
  Sanitizes an exported websocket capture fixture and copies it into the checked-in websocket fixture set.
- `start-dotnet-with-node-cert.sh`
  Starts the .NET API on Linux using the same PEM certificate material already used by the Node server.
- `invoke-live-jibo-prep.sh`
  Bash equivalent of the live-run prep checklist for Ubuntu.
- `get-websocket-capture-summary.sh`
  Bash summary helper for captured websocket telemetry and exported fixtures.
- `import-websocket-capture-fixture.py`
  Cross-platform import/sanitization helper for exported websocket fixtures.
