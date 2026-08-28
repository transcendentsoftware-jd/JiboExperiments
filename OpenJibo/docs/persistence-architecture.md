# Persistence Architecture

## Operator Note

This document explains the design. It is not the deployment checklist. For the staging build,
legacy import, production promotion, and rollback sequence, follow
[managed-persistence-deployment-runbook.md](managed-persistence-deployment-runbook.md).

## Goal

Keep OpenJibo's stateful behavior portable while making durable production state database-backed and bounded.

In-memory stores are appropriate for tests, local development, and explicitly bounded active-session state. They are not an acceptable production source of truth for durable cloud or personal-memory data. The `2026-08-16/17` production OOM incident demonstrated that placing a PostgreSQL snapshot adapter behind `InMemoryCloudStateStore` still hydrates and rewrites the entire cloud state in process memory.

The tracked replacement work and incident evidence live in [feature-backlog.md](feature-backlog.md#13-replace-snapshot-backed-in-memory-cloud-state).

## Design Principles

- Application code talks to small, intent-specific interfaces.
- Persistence keys are always scoped by tenant and person where relevant.
- PostgreSQL is the managed and self-hosted source of truth for durable structured state.
- Blob/file storage holds binary media and backup payloads; PostgreSQL holds their manifests and integrity metadata.
- In-memory adapters are limited to tests/local development and bounded ephemeral connection/turn state.
- Long-lived data should be versioned so we can add optimistic concurrency later.
- Ephemeral turn/session state should stay separate from durable user and device state.
- A scoped mutation must not serialize or rewrite unrelated records or tenants.

## Current Seams

These are the contracts we should preserve:

- `IPersonalMemoryStore`
  - personal facts: names, birthdays, preferences, affinities, important dates, household lists
  - scope: account + loop + device + optional person
- `ICloudStateStore`
  - account, robot, loops, people, sessions, updates, media, backups, holidays, keys
  - scope: system-level state with loop/device/person records inside it
- `IJiboExperienceContentRepository`
  - catalog/content layer only

## Recommended Storage Split

### 1. Identity and topology store

Responsible for:

- account profile
- robot/device registration
- loop membership
- person records
- greeting/proactive presence metadata when it becomes durable

This belongs in normalized PostgreSQL tables with transactional writes and revision checks.

### 2. Personal memory store

Responsible for:

- names
- birthdays
- preferences
- affinities
- important dates
- household lists

This belongs in PostgreSQL keyed by account/loop/device/person. An in-memory implementation remains useful for tests only.

### 3. Session and short-lived orchestration state

Responsible for:

- websocket/session tokens
- temporary skill state
- active report/list/greeting interaction state

Active connection and turn state can stay in process only with TTL cleanup, per-session audio limits, total memory bounds, and disconnect cleanup. Durable issued-token material must be separated from live connection state and persisted safely when restart survival is required.

### 4. Media and backup store

Responsible for:

- uploaded media metadata
- backup manifests
- binary references

Payload bytes belong in Azure Blob Storage (or the self-hosted blob/file adapter) and manifests belong in PostgreSQL.

## Record Shape Guidance

For durable records, prefer a small shared envelope:

- `AccountId`
- `LoopId`
- `DeviceId`
- `PersonId` when relevant
- `RecordType`
- `RecordKey`
- `Value`
- `CreatedUtc`
- `UpdatedUtc`
- `Revision` or `ETag`

That gives us:

- easy partitioning later
- clear tenant boundaries
- room for concurrency checks
- a path to Azure Table, Cosmos, or SQL without changing behavior code

## Adapter Plan

### Phase 1: Incident Mitigation (Complete)

- prevent backups from recursively embedding the backup catalog
- compact legacy recursive backup payloads on load
- retain behavior tests around backup creation, persistence, and restore

### Phase 2: Production Store Replacement (In Progress)

Implemented:

- PostgreSQL personal memory now uses related scope, profile, preference, important-date, affinity, and list-item tables instead of `PersistenceSnapshots`.
- Reads hydrate one account/loop/device/person scope and use a bounded cache (256 scopes and a 30-second TTL by default); writes commit only the affected rows and invalidate that scope. The short TTL is also the missed-invalidation recovery bound between replicas.
- A one-time, transactionally locked importer preserves and imports the legacy `personal-memory` snapshot.
- Personal memory is explicitly capped at four PostgreSQL connections per replica by default. The cloud-state pool defaults to eight, keeping two replicas at 24 pooled connections and leaving headroom under the current 35-connection database limit.
- durable issued-token records and bounded active dialog sessions now use separate registries; only token records are serialized, active sessions are removed on disconnect, and explicit robot links persist outside session metadata.
- audio buffering is independently bounded at 4 MiB per session and 64 MiB across the process, with buffer release tied to WebSocket teardown.
- the normalized cloud-state migration now defines target-specific relational tables for all durable state families, excluding live WebSocket/turn state.
- aggregate WebSocket message/byte/connection metrics cover primary, notification, and Home Assistant send paths without recording payloads or identifiers.
- bounded turn phase/outcome, active-turn, reply-batch, audio high-water, cache hit/miss, and configured PostgreSQL pool metrics are emitted without customer identifiers; collection and provider metric requirements are documented in [runtime operational metrics](architecture/runtime-operational-metrics.md).

Configuration overrides are `OpenJibo:PersonalMemory:PostgreSql:MaxPoolSize`,
`OpenJibo:PersonalMemory:Cache:MaxEntries`, and `OpenJibo:PersonalMemory:Cache:TtlSeconds`.
Cloud-state equivalents are `OpenJibo:State:PostgreSql:MaxPoolSize`,
`OpenJibo:State:Cache:MaxEntries`, `OpenJibo:State:Cache:TtlSeconds`, and
`OpenJibo:State:Sessions:MaximumActive`.
Apply migrations before selecting the PostgreSQL personal-memory backend.

The legacy cloud-state import is deliberately separate from normal schema application. Run it once with
`--apply --target state --import-legacy-cloud-state` after taking a database backup. The importer locks and
hashes the source `cloud-state` snapshot, writes normalized rows transactionally, records an idempotency ledger,
and leaves the source snapshot intact for rollback. Legacy backup JSON is exported to the configured
`OPENJIBO_LEGACY_BACKUP_EXPORT_DIRECTORY`; the self-hosted Compose migration service mounts that directory on
the persistent `api-data` volume. Do not remove the source snapshot or export until record counts and robot
identity mappings have been verified against the running API.
If the API finds a legacy `cloud-state` snapshot but no normalized default account, it fails startup with the
import command instead of silently bootstrapping a second source of truth. A genuinely empty database still
receives the default self-hosted account, hidden bootstrap robot, loop, profile, and household records.

### Phase 3: Scale And Remove Snapshot Fallback

- validate two-replica cache-expiry and committed-write behavior in PostgreSQL CI and staging
- add replication/invalidation primitives only if the measured 30-second cache convergence bound is insufficient
- alert if startup encounters an unimported legacy snapshot or normalized persistence fails
- export the application meter alongside .NET runtime, Npgsql, Container Apps, and PostgreSQL platform metrics
- retain export snapshots only as operational recovery artifacts, never runtime truth

## Non-Goals For Now

- no Azure SDK types in application logic
- no event-sourcing rewrite
- no giant generic repository
- no distributed transaction work before single-node semantics are stable
- no attempt to make active audio buffers durable

## Immediate Next Step

Keep the production PostgreSQL binding pinned through the
[managed persistence deployment runbook](managed-persistence-deployment-runbook.md), complete the
environment-gated PostgreSQL integration suite and two-replica staging proof, then collect seven representative
days of application, .NET, Npgsql, Container Apps, and PostgreSQL metrics. Preserve the legacy snapshots,
pre-cutover database, and exported v1 backup payloads until that observation window and a separately reviewed
recovery drill pass.
