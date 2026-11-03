# Cloud Scripts

These scripts help exercise the new .NET hosted cloud locally.

- `backup-self-hosted.py` captures both databases, API data and private recovery
  configuration after writers are stopped. Its offline verifier checks a retained
  manifest checksum without Docker or extraction. See the
  [recovery guide](../../docs/self-hosted-recovery.md); it never restores data.

- `prepare-self-hosted-readiness.py` joins read-only Docker/Compose prerequisites
  and `/health/self-hosted` configuration observations, then exports a selected
  isolated conversion target when those conditions pass. See
  [readiness and target export](../../docs/self-hosted-readiness.md); it does not
  install, certify persistence, enroll robots or apply conversion changes.

- `prepare-openjibo-starter-bundle.py`
  Prepares an offline, allowlisted standalone isolated starter ZIP for a supplied
  digest-pinned runtime image. Does not publish, pull or deploy. See the
  [packaging preview guide](../../docs/standalone-starter-packaging.md) for limits
  and remaining install/recovery verification.
- `verify-openjibo-starter-bundle.py`
  Checks an independently supplied whole-archive SHA-256 and the bounded starter
  ZIP/manifest contract before extraction. Does not extract, run, or authenticate
  a release on its own; the expected checksum must come from a trusted source.

- `Start-OpenJiboDotNet.ps1`
  Starts the current `.NET` cloud with local capture directories configured.
- `Start-OpenJiboNode.ps1`
  Starts the legacy Node protocol oracle from `src/Jibo.Cloud/node`.
- `Start-OpenJiboPlayground.ps1`
  Starts the direct local Jibo ASR/TTS Playground demo.
- `Invoke-CloudSmoke.ps1`
  Runs a few quick HTTP checks against a local OpenJibo cloud instance.
- `Invoke-OpenJiboMigration.ps1`
  Runs the PostgreSQL migration wrapper against the local or managed database targets.
- `Publish-OpenJiboManaged.ps1`
  Builds and pushes the managed Open Jibo image to Azure Container Registry using `az acr build`.
- `Deploy-OpenJiboManagedFoundation.ps1`
  Deploys the managed foundation resources, then seeds Key Vault secrets from the deployment outputs and supplied bootstrap values.
- `Deploy-OpenJiboManaged.ps1`
  Deploys the first Azure Container Apps stack from the Bicep template under `infra/azure/container-apps/`. Use `-RunMigration` to apply schema changes and `-RunSmoke` to verify the deployed endpoint. By default it binds `api.openjibo.com`, `open-jibo-socket.openjibo.com`, and `neohub.openjibo.com` so the robot API, notification socket, and neohub paths stay aligned.
- `deploy-openjibo-managed-foundation.sh`
  Bash deploy wrapper for the managed foundation stack.
- `publish-openjibo-managed.sh`
  Bash build-and-push wrapper for the managed ACR image.
- `deploy-openjibo-managed.sh`
  Bash deploy wrapper for the managed Container Apps stack plus optional migration and smoke. It defaults to the canonical managed host trio: `--api-hostname api.openjibo.com`, `--socket-hostname open-jibo-socket.openjibo.com`, and `--neohub-hostname neohub.openjibo.com`. Fleet peer sync is disabled by default; `--enable-peer-sync` requires `--peer-sync-allowed-hosts` with exact hosts.
- `Test-OpenJiboManagedDeploymentContract.ps1`
  Validates the managed deployment contract by checking the Bicep templates, workflow, and deploy scripts for expected markers before any Azure calls run.
- `test-openjibo-managed-deployment-contract.sh`
  Bash contract checker for the managed deployment path and workflow markers, including the canonical managed host trio and `api.openjibo.com` hostname path.
- `Test-OpenJiboSelfHostedDeploymentContract.ps1`
  Validates the self-hosted contract by checking the Compose file, migration wrapper, and smoke script before local CI brings up the stack.
- GitHub Actions `openjibo-cloud-managed-deploy`
  Manual workflow that deploys the foundation, builds the managed image, deploys the ACA stack, binds the canonical API/socket/neohub hostnames, runs migrations, and smokes the deployed endpoint. The workflow defaults the robot-facing host trio to `api.openjibo.com`, `open-jibo-socket.openjibo.com`, and `neohub.openjibo.com`.
- `OPENJIBO_POSTGRES_PASSWORD`
  Required when running the self-hosted PostgreSQL stack locally or in CI so the database password stays out of source control.
- Managed Azure deploy secrets:
  - `OPENJIBO_SEARCH_BACKEND`
  - `OPENJIBO_SEARCH_FALLBACK`
  Store the backend spec string in the `openjibo-managed` GitHub Actions environment. The deploy workflow seeds those values into Azure Key Vault and then copies them into the Container App as `OPENJIBO_SEARCH_BACKEND` and `OPENJIBO_SEARCH_FALLBACK`.
- `initialize-openjibo-compose-env.sh`
  Initializes a missing `.env` from the example with cryptographically random per-install user encryption values (requires OpenSSL). Preserves existing encryption values and keeps `OPENJIBO_POSTGRES_PASSWORD` in sync when supplied. Back up `.env` with the database; never regenerate keys for existing encrypted data.
- `Initialize-OpenJiboComposeEnv.ps1`
  PowerShell equivalent of the compose env bootstrap helper, using .NET cryptographic randomness for new environments and preserving existing encryption values, including password propagation into an existing `.env`.
- `invoke-openjibo-self-hosted-stack.sh`
  Starts the local self-hosted stack, bootstraps `.env`, and can include the migration service when requested.
- `Invoke-OpenJiboSelfHostedStack.ps1`
  PowerShell equivalent of the self-hosted stack launcher.
- `scripts/cloud/postgres-init/01-create-databases.sh`
  PostgreSQL init hook that creates the additional `openjibo_memory` database the migrator expects in self-hosted mode.
- `Invoke-ProtocolFixture.ps1`
  Replays a sanitized HTTP fixture against a running local instance.
- `Get-WebSocketCaptureSummary.ps1`
  Summarizes captured websocket telemetry events and exported live-run fixtures from the .NET cloud, and highlights the buffered-audio replay fixtures that are most useful when debugging STT regressions.
- repo-root `captures/http/`
  Structured HTTP request/response telemetry for live robot startup comparison.
- repo-root `captures/websocket/`
  Structured websocket telemetry plus exported replay fixtures for live robot sessions.
- `Invoke-LiveJiboPrep.ps1`
  Runs a small readiness checklist before the first physical Jibo test against the .NET cloud.
- `Import-WebSocketCaptureFixture.ps1`
  Sanitizes an exported websocket capture fixture and copies it into the checked-in websocket fixture set.
- `New-CaptureBundle.ps1`
  Packages the capture root, capture index, and exported fixtures into a single zip bundle for group testing handoff, including the fixture name list in the manifest for quicker STT replay triage.
- `start-dotnet-with-node-cert.sh`
  Starts the .NET API on Linux using the same PEM certificate material already used by the Node server.
- `invoke-live-jibo-prep.sh`
  Bash equivalent of the live-run prep checklist for Ubuntu.
- `invoke-openjibo-migration.sh`
  Bash wrapper for the PostgreSQL migration runner so Linux container and self-hosted flows do not depend on PowerShell.
- `invoke-cloud-smoke.sh`
  Bash onboarding replay and health smoke for Linux CI and containerized self-hosted runs.
- `invoke-release-smoke.mjs`
  Runs the browser-shared HTTP/WebSocket release gate and configurable fake-robot load scenario. Defaults to six
  connected notification sockets with one round of 25% simultaneous `CLIENT_ASR` turns. Configure it with
  `RELEASE_SMOKE_CONCURRENCY`, `RELEASE_SMOKE_TURN_PERCENT`, `RELEASE_SMOKE_TURN_ROUNDS`,
  `RELEASE_SMOKE_HOLD_MS`, `RELEASE_SMOKE_ROUND_INTERVAL_MS`, and `RELEASE_SMOKE_TIMEOUT_MS`. The JSON result
  includes completed-turn count and min/P50/P95/max client-observed latency.
- `openjibo-capacity-report.mjs`
  Reads aggregate-only Application Insights, Npgsql, .NET runtime, Container Apps, and PostgreSQL limit data for
  the exact ready revision. It reconciles application payload bytes with platform wire bytes, calculates memory
  and connection headroom, and refuses to classify a short observation window as representative evidence. The
  Azure CLI `application-insights` extension is required.
- `test-openjibo-self-hosted-deployment-contract.sh`
  Bash contract checker for the self-hosted compose/migration/smoke trio.
- `get-websocket-capture-summary.sh`
  Bash summary helper for captured websocket telemetry and exported live-run fixtures.
- `inspect-websocket-recognition-candidates.py` / `inspect-websocket-recognition-candidates.sh`
  Scans websocket telemetry and fixtures for face, voice, speaker, person, enrollment, confidence, and score-like fields so live robot captures can be assessed before wiring them into persisted recognition observations.
- `import-websocket-capture-fixture.py`
  Cross-platform import/sanitization helper for exported websocket fixtures.

## Managed Azure storage

The managed Azure path separates persistent storage from the application container:

- State snapshots use Azure Database for PostgreSQL Flexible Server and the `openjibo_state` database.
- Personal memory snapshots use the same PostgreSQL server and the `openjibo_memory` database.
- Media stays on Azure Blob Storage through the `OpenJibo__Media__Backend=AzureBlob` runtime setting. Robot log,
  ASR, and binary upload artifacts use that same managed container under the `logs/` prefix, with an adjacent JSON
  manifest that records the originating robot, request IDs, content type, checksum, and storage timestamp.

`deploy-openjibo-managed-foundation.sh` and `Deploy-OpenJiboManagedFoundation.ps1` create the PostgreSQL server and databases, generate a PostgreSQL administrator password when one is not supplied, and seed the state, personal-memory, media, and API-provider secrets into Key Vault. The scripts still accept explicit state and personal-memory connection string overrides for emergency or external-database cases, but the default managed deployment is PostgreSQL-backed.

The foundation adds a PostgreSQL firewall rule for Azure services and, when the deployment runner public IP can be detected, a narrow firewall rule for that runner so deploy-time migrations can connect. The managed deploy path removes that runner rule again after a successful deploy so it does not linger longer than needed. If migrations fail with a PostgreSQL network error, confirm the runner IP was detected and that the database firewall allows the current deploy runner.

`deploy-openjibo-managed.sh` and `Deploy-OpenJiboManaged.ps1` also query the Container Apps environment outbound IPs and create matching PostgreSQL firewall rules before migrations and smoke checks run. That keeps the managed runtime on PostgreSQL while giving the deployed container its own firewall path.

The managed Container App now expects a Key Vault secret named `openjibo-portal-status-password` before deploy. Seed it the same way you seed the other managed secrets, then the deploy script will pass it through as the password gate for `/portal/status`.

For trusted-server fleet presence synchronization, every participating deployment must also have the same Key Vault secret named `openjibo-peer-sync-shared-key`. The runtime uses it to HMAC-sign short-lived presence reports; each server still accepts reports only from active trusted-server entries with cloud sync enabled.

## Managed knowledge-search runbook

Hosted deployments can enable Wolfram Alpha, ChatGPT, or another supported knowledge backend by storing the backend spec string in the `openjibo-managed` GitHub Actions environment.

Use these two secrets:

- `OPENJIBO_SEARCH_BACKEND`
- `OPENJIBO_SEARCH_FALLBACK`

The workflow seeds those values into Key Vault as `openjibo-search-backend` and `openjibo-search-fallback`, then copies them into the Container App as `OPENJIBO_SEARCH_BACKEND` and `OPENJIBO_SEARCH_FALLBACK`.

Use the same spec format the app already understands: `backend!credential!model`.

Examples:

- Wolfram only: `Wolfram!<wolfram-app-id>`
- ChatGPT only: `ChatGPT!<openai-api-key>`
- ChatGPT with explicit model: `ChatGPT!<openai-api-key>!gpt-5.4-nano`
- Primary plus fallback: set `OPENJIBO_SEARCH_BACKEND` and `OPENJIBO_SEARCH_FALLBACK` independently, for example `Wolfram!<wolfram-app-id>` as primary and `ChatGPT!<openai-api-key>!gpt-5.4-nano` as fallback.

If you do not want hosted AI search enabled, leave both secrets empty. The deploy path will still work, and the runtime will keep using its other configured providers.

## Managed hostname binding

`deploy-openjibo-managed.sh` and `Deploy-OpenJiboManaged.ps1` bind the requested host trio after the Container App deployment. Azure Container Apps managed certificates require DNS to point directly at the generated Container App hostname before certificate issuance can succeed. For subdomains such as `api.openjibo.com`, `open-jibo-socket.openjibo.com`, and `neohub.openjibo.com`, create a CNAME from each custom hostname to the generated Container App FQDN returned by the deployment output.

Use `--skip-hostname-binding` or `-SkipHostnameBinding` only for temporary diagnostics where the generated Container App FQDN is enough.

## Managed migrations

The managed workflow runs PostgreSQL migrations on every deploy before smoke tests. Treat checked-in migration scripts as forward-only operational changes: prefer additive schema changes, avoid destructive backwards edits, and do not remove or rewrite already-applied migrations unless the production impact is understood and intentionally coordinated. Any destructive data change should be paired with an explicit backup or recovery plan before it lands in the deploy path.

The smoke helper treats `Loop_20160324.SetEnrollment` as best-effort during managed deploys. If the hosted app returns a transient `500` there, the script still continues to the recognition-observation proof so a single enrollment edge case does not block the deploy gate.

See [docs/local-cloud-quickstart.md](../../docs/local-cloud-quickstart.md) for the full local (non-Docker) setup guide, and [docs/self-hosted-runbook.md](../../docs/self-hosted-runbook.md) for the single-source-of-truth Docker Compose self-hosting runbook covering configuration, building the container (including local Whisper vs. Azure Speech), and starting the stack.
