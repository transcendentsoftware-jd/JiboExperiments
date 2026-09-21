# Managed Persistence Deployment Runbook

This is the operator procedure for moving OpenJibo from legacy PostgreSQL snapshots to normalized PostgreSQL tables. For architectural background, see [persistence-architecture.md](persistence-architecture.md).

## Current Decision

Do not deploy this persistence release directly to production.

The required order is:

1. Create isolated staging infrastructure.
2. Clone production PostgreSQL into staging.
3. Run and verify the migration in staging.
4. Deploy the exact commit to staging and complete smoke and robot checks.
5. Promote that exact commit to production using the staging workflow run ID.

The workflow refuses production promotion without a successful staging artifact for the same Git commit.

## What the Workflow Protects

The workflow now:

- defaults to `staging` and uses a separate GitHub Environment and resource group;
- assigns an immutable `sha-<commit>` image tag by default;
- optionally clones both production PostgreSQL databases into staging;
- refuses a clone when source and target resource groups or PostgreSQL hosts match;
- applies schemas and imports both legacy snapshots before starting the new revision;
- exports embedded legacy backups to the target Azure Blob store;
- verifies import ledgers before application startup;
- smoke-tests staging and creates an exact-commit promotion artifact;
- requires that artifact and explicit backup confirmation for production;
- refuses to rebind an existing production Container App to a different PostgreSQL server;
- verifies Azure PostgreSQL point-in-time recovery retention is at least seven days;
- quiesces the old revision during the final import; and
- restores the previous image automatically if deployment fails.

The migration is additive. It preserves `PersistenceSnapshots/cloud-state` and
`PersistenceSnapshots/personal-memory`.

## SigV4 Replay HMAC Rotation

SigV4 replay observation remains disabled unless explicitly enabled. The current
key is stored in Key Vault as `openjibo-sigv4-replay-hmac`. The deployment
metadata is persisted in `openjibo-sigv4-replay-hmac-key-version` and
`openjibo-sigv4-replay-hmac-previous-key-version`. During rotation, an operator
may stage the old key as `openjibo-sigv4-replay-hmac-previous`; the foundation
scripts initialize the version metadata only when absent and never generate or
refresh the previous-key secret. After initialization, they preserve both
version values exactly as the operator set them.

1. As one operator change, stage the replacement as the current key, retain the
   old value as the previous key, and update both version metadata secrets: the
   current version increments and the previous version becomes the old current
   version. Do not put either secret value in shell history. Do not deploy until
   all four Key Vault values are present and mutually consistent.
2. Deploy with the replay observer enabled and with migration enabled. The
   Linux and PowerShell deploy helpers read the optional previous secret and
   fail closed if its version is missing, malformed, or equal to the current
   version.
3. Keep both keys available for at least the 15-minute replay-observation TTL,
   plus the bounded publisher queue/drain and revision-rollout margin. Review
   observation metrics before retiring the old key.
4. Remove the previous Key Vault secret, set its version to `0`, and redeploy.
   Never remove the current key until the replacement revision is healthy.

If replay observation is not needed in a self-hosted or managed environment,
leave it disabled and do not create a previous-key secret.

## One-Time Staging Setup

### 1. Create an Azure resource group

Use a resource group that is not production. Prefer the production Azure region.

```powershell
az group create --name <staging-resource-group> --location <production-region>
```

Staging receives separate PostgreSQL, storage, Key Vault, registry, Log Analytics, Speech, and Container Apps resources. This adds Azure cost while staging exists.

### 2. Create the GitHub Environment

Create a GitHub Environment named `openjibo-staging` with the same secret names used by `openjibo-managed`:

- `AZURE_CLIENT_ID`
- `AZURE_TENANT_ID`
- `AZURE_SUBSCRIPTION_ID`
- `OPENJIBO_RESOURCE_GROUP`
- `OPENWEATHER_API_KEY`
- `NEWSAPI_KEY`
- `OPENJIBO_SEARCH_BACKEND`
- `OPENJIBO_SEARCH_FALLBACK`

When refreshing staging from production, the workflow reads the exact production
Key Vault URL metadata attached to the deployed Container App secrets
`user-encryption-passphrase` and `user-encryption-salt`. It requires both
references, verifies they point to the same vault, and fails closed if either is
missing, ambiguous, or malformed. It never reads secret values to discover the
vault and never guesses among `kv-*` resources.

Set staging's `OPENJIBO_RESOURCE_GROUP` to the new staging resource group. Never reuse production's value.

### 3. Add Azure OIDC federation

The Azure application used by Actions must trust:

```text
repo:transcendentsoftware-jd/JiboExperiments:environment:openjibo-staging
```

Keep the existing `openjibo-managed` production subject. Using the same deployment principal for the first rehearsal allows read access to the production Key Vault; the clone does not write production databases. The principal also needs Key Vault data-plane `get` permission on the production vault and `set` permission (typically the Key Vault Secrets Officer role) on the staging vault. Azure Contributor alone does not grant these secret data-plane permissions.

The current workflow logs into one Azure subscription, so production and staging resource groups must be in that same subscription. If they diverge, add a separately reviewed multi-subscription login/context before running a clone. The production Container App must retain Key Vault-backed secret references (with `keyVaultUrl` metadata); copied literal Container App secret values are intentionally rejected because they cannot identify the source vault safely.

### 4. Optional staging DNS

The first staging run needs no DNS and smoke-tests Azure's generated hostname.

For robot testing, later create CNAMEs for:

- `staging-api.openjibo.com`
- `staging-open-jibo-socket.openjibo.com`
- `staging-neohub.openjibo.com`

Then rerun staging with `bind_staging_hostnames` enabled. Never point production hostnames at staging.

## First Staging Run

Run `openjibo-cloud-managed-deploy` with:

| Input                             | Value                                            |
| --------------------------------- | ------------------------------------------------ |
| `deployment_target`               | `staging`                                        |
| `refresh_staging_from_production` | `true`                                           |
| `production_resource_group`       | existing production resource group               |
| `bind_staging_hostnames`          | `false` until DNS exists                         |
| `production_backup_confirmed`     | `false`                                          |
| `enable_sigv4_replay_observation` | `false` for the first deployment; enable only for the reviewed shadow trial |
| `staging_run_id`                  | blank                                            |
| `image_tag`                       | blank                                            |
| `location`                        | blank unless Container Apps needs another region |

Expected sequence:

1. Validate the deployment contract.
2. Create or update isolated staging foundation resources.
3. Build the exact commit.
4. Quiesce an older staging revision.
5. Clone production state and personal-memory databases, then copy the matching encryption passphrase and salt into staging so the cloned encrypted rows remain readable. This makes staging production-sensitive: anyone with staging Key Vault access can decrypt the copied personal data during the verification window.
6. Apply normalized schemas.
7. Import and verify both legacy snapshots.
8. Store embedded backups in staging Azure Blob Storage.
9. Deploy and run the HTTP smoke test against staging.
10. Capture the deployed scale, temporarily pin staging to two replicas, and wait until both replicas report running.
11. Run the deployed WebSocket release smoke through Azure ingress: require the protected replica probe to observe two distinct serving instance ids, record the instance that commits a bounded smoke-device registration, require another instance on the same revision to read that device and issue its Hub token, then exercise notification reconnect, `CLIENT_ASR`, `CLIENT_NLU`, malformed-frame recovery, missing-token rejection, persistence, and the selected connected-robot/simultaneous-turn tier. Replica headers are returned only when the temporary deployment smoke secret is valid. The quick gate defaults to six connected robots and 25% simultaneous turns for one round.
12. Disable the deployment-scoped smoke authorization and restore the captured staging scale so the proof does not silently increase ongoing cost.
13. Upload `openjibo-staging-gate-<commit>` with HTTP smoke, WebSocket smoke, two-replica ingress evidence, and cross-replica committed-read evidence. Production promotion rejects an older gate that lacks any of these fields.

The same WebSocket release smoke is available from the admin harness and runs against the current deployment. The protected `/health/replica` endpoint is hidden while deployment smoke is disabled and requires the short-lived smoke secret while enabled. A failed HTTP, WebSocket, or replica-evidence check restores the previous revision and produces no promotion gate.

Fleet peer synchronization is disabled by default and the managed workflow forbids enabling it in staging, even
when staging was cloned from production and has the same trusted-server rows or shared-key secret. Production
must deliberately provide exact comma-separated `fleet_peer_allowed_hosts`; a nonempty production value enables
peer sync, and the application applies that allowlist to both outbound and inbound presence reports.
Do not enable it until the remote peer has the matching key and reciprocal trust configuration.

Legacy SigV4 replay observation is also disabled by default and is deliberately staging-only. A reviewed shadow
trial may set `enable_sigv4_replay_observation=true`; the workflow then requires migration/role provisioning,
passes the explicit enable flag to the deployment script, and verifies that the resulting Container App uses the
dedicated observer connection and HMAC secret references. The observer remains asynchronous and non-authorizing.
Production rejects the enable input until the staging trial has bounded persistence-failure, queue-drop,
cross-replica, and key-rotation evidence.

Immediate staging containment was applied on `2026-08-26`: the
`OpenJibo__FleetNetwork__PeerSyncSharedKey` environment reference was removed from `rg-openjibo-staging`, creating
healthy revision `openjibo-cloud--0000009`. The six-robot/25%-turn smoke passed on that revision at 209-212 ms,
and its console tail contained no FleetPeerSync or smoke error markers. The secret remains stored for controlled
future use but is not exposed to the staging container. Deploy this source patch before running the managed
workflow again; an older workflow definition would restore the environment reference.

## Staging Verification

Before production, confirm:

- the workflow and smoke checks pass;
- the workflow summary links to the staging status dashboard;
- the authenticated dashboard's Deployment card shows the expected exact revision, `managed` mode,
  staging canonical hostname, serving replica, and `config compatible`;
- the dashboard persistence schema and revision match the migrated staging database;
- normalized accounts, robots, devices, loops, people, tokens, and personal memory exist;
- robot identity mappings match production;
- at least one legacy backup can be read or restored when backups exist;
- legacy snapshots remain present; and
- a robot can authenticate, connect its WebSockets, and complete representative interactions after staging DNS is enabled.

Record the successful staging workflow run ID.

## Production Promotion

Schedule a short maintenance window. The old revision is quiesced so it cannot update the legacy snapshot during final import.

Run the workflow with:

| Input                             | Value                                          |
| --------------------------------- | ---------------------------------------------- |
| `deployment_target`               | `production`                                   |
| `refresh_staging_from_production` | `false`                                        |
| `staging_run_id`                  | successful run ID for this exact commit        |
| `production_backup_confirmed`     | `true` after checking PostgreSQL backup health |
| `enable_sigv4_replay_observation` | `false`                                        |
| `image_tag`                       | blank                                          |
| Production hostnames              | retain current defaults                        |

If the production resource group moved to another Azure subscription, `uniqueString(resourceGroup().id)` changes even though the resources moved intact. Supply all six `existing_*_name` inputs together so the workflow reuses the original Log Analytics workspace, Container Registry, Key Vault, Storage Account, PostgreSQL server, and Speech Services account. Recover these exact names from the active Container App configuration or the most recent successful production deployment; never mix old and newly generated foundation names.

A subscription move also changes the Container App `customDomainVerificationId`. Before promotion, keep every production hostname CNAME pointed directly at the generated Container App FQDN and replace each `asuid.<hostname>` TXT value with the current verification ID reported by the production Container App. This applies to the three `openjibo.com` hosts and both native `jibo.pro` compatibility hosts. The production DNS preflight verifies all five CNAME and TXT pairs before the image build or maintenance window begins.

Promotion is refused if the staging gate is missing, belongs to another commit, lacks a passing smoke test, backup confirmation is absent, PITR retention is below seven days, or production hostname DNS is not ready.

Production promotion requires all six pinned `existing_*_name` inputs. Before foundation provisioning, it requires exactly one existing Container App named `openjibo-cloud` and one revision owning traffic, verifies that the pinned PostgreSQL resource exists in the production resource group, and compares that live revision's non-secret server/state-database/personal-memory-database identity markers with the pinned production contract. A mismatch fails before resource mutation, image build, migration, or the maintenance window. The first marker-enabled promotion additionally requires the one-time `production_database_binding_bootstrap_confirmed` check after independent verification of those pinned names. After the marker-enabled revision passes deployment smoke, the workflow re-resolves `openjibo-cloud`, validates its live marker set, and persists `openjiboDatabaseBindingBootstrapCompleted=true` on the resource group; a later marker-less revision cannot reuse the checkbox. An intentional PostgreSQL move must use a separately reviewed clone/recovery cutover.

Downtime begins when the old revision is quiesced and ends after the new revision passes smoke checks.

## Failure and Rollback

On failure, the workflow restores the previous container image and replica limits.

Database rollback normally is unnecessary because migrations are additive, imports are transactional and idempotent, and the legacy snapshots remain unchanged. For database recovery, use the PITR reference time written to the production workflow summary.

Do not delete legacy snapshots or imported backup payloads during the verification window.

## After Production

Monitor Container App restarts and memory, PostgreSQL connections and latency, WebSocket connections and bytes, and robot reconnect behavior. Remove legacy recovery artifacts only in a later, separately reviewed release.

## Inventory Audit and Device Recovery

Run the aggregate audit before recovery:

```powershell
dotnet Jibo.Cloud.Migrations.dll --audit-cloud-state --state-connection "<target-state-connection>"
```

The report is aggregate-only. Its `integrity` section includes account/device linkage,
robot-profile and identity-link anomalies, default-record cardinality, and backup counts by
schema version. A nonzero `backupSchemaV1` count identifies a candidate for the required
legacy restore drill; `unsupportedBackupSchemas` must be zero. No identifiers, token material,
connection strings, or backup payloads are emitted.

When comparing the preserved normalized database with the current target, recovery is dry-run by default and emits aggregate-only counts:

```powershell
dotnet Jibo.Cloud.Migrations.dll --recover-missing-devices `
  --source-state-connection "<preserved-state-connection>" `
  --target-state-connection "<target-state-connection>"
```

Review the dry-run counts, then require both explicit apply flags to mutate the target:

```powershell
dotnet Jibo.Cloud.Migrations.dll --recover-missing-devices --apply `
  --confirm-recover-missing-devices `
  --source-state-connection "<preserved-state-connection>" `
  --target-state-connection "<target-state-connection>"
```

Recovery is limited to missing non-synthetic devices, existing-account links, and host mappings for newly inserted devices. It does not copy accounts, tokens, sessions, credentials, profiles, identity links, or other dependent families. Keep the source database preserved and read-only throughout the process.

## 2026-08-27 Production Inventory Incident

After the production resource group moved subscriptions, Azure's resource-group identity changed. The foundation's default names are derived from that identity, so a deployment without the six pinned `existing_*_name` inputs selected a newly created PostgreSQL server. That server was structurally valid but did not contain the older normalized fleet inventory. With no legacy snapshot requiring import, empty-database bootstrap also looked valid to the application. The admin portal correctly displayed the new database's incomplete inventory.

The preserved pre-move PostgreSQL server still contained the missing records. A guarded recovery restored 14 non-synthetic devices, 14 links to accounts already present in the target, and three host mappings. It deliberately excluded 61 historical smoke/bootstrap devices and did not restore tokens, sessions, credentials, profiles, identity links, or loop membership. A second dry-run reported zero remaining changes.

The prevention boundary is resource identity, not a fixed robot-count threshold: legitimate inventory can grow, shrink, or be archived. Production promotion now refuses any unreviewed PostgreSQL server-name change, while exact-commit staging, PITR verification, dry-run recovery, and the stable smoke namespace provide the remaining layers.
