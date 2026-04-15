# Live Jibo Capture

## Recommendation

For the first real `.NET cloud -> physical Jibo` runs, use the existing controlled network and routing setup that already works with the Node server.

Recommended order:

1. Keep the robot on the known-good Ubuntu laptop based environment.
2. Swap the `.NET` cloud into that same controlled path.
3. Leave the Node oracle available as a fallback on separate ports or on a second machine.
4. Capture real `.NET` websocket traffic and turn it into sanitized fixtures.
5. Only after that, decide what belongs in permanent Azure hosting and IaC.

This is the lowest-risk path because it changes only one major variable at a time: the cloud implementation. It avoids mixing protocol-parity questions with new infrastructure variables.

## Why Not Azure First

Azure remains the target hosting direction, but it is not the best first environment for live robot discovery.

Reasons:

- the main unknowns are still protocol and turn-behavior details, not Azure primitives
- keeping Node and `.NET` both available locally makes fallback and side-by-side comparison much easier
- live robot capture is more valuable right now than early CI/CD polish
- region injection and device routing work are easier to debug in a tightly controlled local network

## When To Move Beyond The Ubuntu Setup

Move to a second local/staging server or Azure after:

- startup flows are stable against the physical robot
- websocket turn telemetry is being captured reliably
- several real captured sessions have been sanitized into replay fixtures
- the fallback path to Node is no longer needed for normal testing

## Telemetry Before Live Runs

The `.NET` cloud now supports structured websocket capture intended for first live runs:

- event stream written as NDJSON
- per-session fixture export for replay
- turn metadata including `transID`, buffered audio counts, finalize attempts, and reply types

Default capture location:

- `captures/websocket/`

Artifacts:

- `*.events.ndjson`
- `fixtures/*.flow.json`

## Suggested First Hookup Plan

1. Start the `.NET` API on the Ubuntu-backed controlled network using the same robot routing settings currently used for Node.
2. Confirm HTTP bootstrap and websocket acceptance with the existing smoke/routing helpers.
3. Run one or two controlled listen turns with Jibo.
4. Inspect the captured websocket events and exported fixtures.
5. Convert the best captures into sanitized checked-in fixtures and tests.
6. Keep Node available to compare any surprising turn behavior before changing infrastructure.

Useful helper scripts:

- [scripts/cloud/Invoke-LiveJiboPrep.ps1](/OpenJibo/scripts/cloud/Invoke-LiveJiboPrep.ps1)
- [scripts/cloud/Get-WebSocketCaptureSummary.ps1](/OpenJibo/scripts/cloud/Get-WebSocketCaptureSummary.ps1)
- [scripts/cloud/Import-WebSocketCaptureFixture.ps1](/OpenJibo/scripts/cloud/Import-WebSocketCaptureFixture.ps1)
