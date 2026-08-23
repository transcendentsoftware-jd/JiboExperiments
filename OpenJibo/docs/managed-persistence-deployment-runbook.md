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
- verifies Azure PostgreSQL point-in-time recovery retention is at least seven days;
- quiesces the old revision during the final import; and
- restores the previous image automatically if deployment fails.

The migration is additive. It preserves `PersistenceSnapshots/cloud-state` and
`PersistenceSnapshots/personal-memory`.

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

Set staging's `OPENJIBO_RESOURCE_GROUP` to the new staging resource group. Never reuse production's value.

### 3. Add Azure OIDC federation

The Azure application used by Actions must trust:

```text
repo:transcendentsoftware-jd/JiboExperiments:environment:openjibo-staging
```

Keep the existing `openjibo-managed` production subject. Using the same deployment principal for the first rehearsal allows read access to the production Key Vault; the clone does not write production databases.

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
| `staging_run_id`                  | blank                                            |
| `image_tag`                       | blank                                            |
| `location`                        | blank unless Container Apps needs another region |

Expected sequence:

1. Validate the deployment contract.
2. Create or update isolated staging foundation resources.
3. Build the exact commit.
4. Quiesce an older staging revision.
5. Clone production state and personal-memory databases.
6. Apply normalized schemas.
7. Import and verify both legacy snapshots.
8. Store embedded backups in staging Azure Blob Storage.
9. Deploy and smoke-test staging.
10. Upload `openjibo-staging-gate-<commit>`.

A failed run produces no promotion gate.

## Staging Verification

Before production, confirm:

- the workflow and smoke checks pass;
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
| `image_tag`                       | blank                                          |
| Production hostnames              | retain current defaults                        |

Promotion is refused if the staging gate is missing, belongs to another commit, lacks a passing smoke test, backup confirmation is absent, or PITR retention is below seven days.

Downtime begins when the old revision is quiesced and ends after the new revision passes smoke checks.

## Failure and Rollback

On failure, the workflow restores the previous container image and replica limits.

Database rollback normally is unnecessary because migrations are additive, imports are transactional and idempotent, and the legacy snapshots remain unchanged. For database recovery, use the PITR reference time written to the production workflow summary.

Do not delete legacy snapshots or imported backup payloads during the verification window.

## After Production

Monitor Container App restarts and memory, PostgreSQL connections and latency, WebSocket connections and bytes, and robot reconnect behavior. Remove legacy recovery artifacts only in a later, separately reviewed release.
