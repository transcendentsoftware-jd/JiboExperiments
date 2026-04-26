# Cloud Deploy And Jibo RCM Path Prompt

Prepare OpenJibo for a lightweight v1 cloud deployment and the cleanest practical Jibo configuration path for group testing.

Current repo context:

- workspace root: `.\OpenJibo`
- the current `.NET` cloud is the target runtime
- the Node server remains a discovery oracle and fallback
- latest live-test guidance is in:
  - `docs/live-jibo-test-runbook.md`
  - `docs/live-jibo-capture.md`
  - `docs/device-bootstrap.md`
  - `docs/development-plan.md`
  - `src/Jibo.Cloud/dotnet/README.md`

What we need from this workstream:

1. define the smallest, cleanest, easiest-to-repeat deployment path for a v1 hosted OpenJibo cloud
2. define the lightest reliable way to configure Jibo devices to use that cloud, with as few manual error-prone steps as possible
3. produce scripts and docs that make it realistic for additional revival-group testers to get connected quickly

Important goals:

- prefer a path that is easy for non-experts in the revival group to follow
- minimize hand-edited device changes and confusing setup steps
- preserve a clear fallback path when a deployment or routing change fails
- keep the deployment practical for a small testing cohort first; enterprise polish can come later

Areas to review:

- current API host and routing logic in `src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Api/Program.cs`
- existing scripts under:
  - `scripts/cloud/`
  - `scripts/bootstrap/`
- docs around routing and bootstrap in:
  - `docs/device-bootstrap.md`
  - `docs/live-jibo-test-runbook.md`
  - `docs/live-jibo-capture.md`

Deliverables:

- a concrete v1 deployment recommendation
- any needed deployment scripts or setup helpers
- a clean Jibo configuration / routing / RCM procedure with the fewest practical steps
- validation steps that clearly distinguish cloud issues from robot/network issues
- doc updates aimed at making group adoption fast and low-risk

Constraints:

- do not over-design for full production scale yet
- avoid adding multiple competing deployment paths unless there is a strong reason
- optimize for reliability, repeatability, and low support burden for the next round of testers
- keep the Node oracle available as a troubleshooting fallback until `.NET` parity is clearly strong enough
