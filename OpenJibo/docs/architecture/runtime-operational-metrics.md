# Runtime Operational Metrics

Status date: `2026-09-21`

OpenJibo emits privacy-safe aggregate measurements through the .NET meter `OpenJibo.Transport`. These
measurements are intended to establish a concurrency and cost envelope; they are not a customer activity log.
Robot IDs, session IDs, transcripts, audio, credentials, connection strings, and free-form error text must never
be added as metric attributes.

These metrics are not a durable per-robot source and must not be converted into billing facts. The separate,
dormant source-local design is documented in [runtime-usage-outbox.md](runtime-usage-outbox.md).

## Application Instruments

| Instrument | Type | Unit | Attributes |
| --- | --- | --- | --- |
| `openjibo.turn.active` | up/down counter | turns | none |
| `openjibo.turn.phase.duration` | histogram | ms | `phase`, `outcome` |
| `openjibo.turn.phase.operations` | counter | operations | `phase`, `outcome` |
| `openjibo.turn.finalization_suppressions` | counter | suppressions | `reason` |
| `openjibo.turn.reply_batches` | counter | batches | `has_eos` |
| `openjibo.turn.reply_count` | histogram | replies | `has_eos` |
| `openjibo.audio.current_buffered_bytes` | observable gauge | bytes | none |
| `openjibo.audio.buffered_high_water_bytes` | observable gauge | bytes | none |
| `openjibo.audio.accepted_bytes` | counter | bytes | none |
| `openjibo.audio.buffer_limit_rejections` | counter | rejections | none |
| `openjibo.audio.rejected_bytes` | counter | bytes | none |
| `openjibo.persistence.cache.accesses` | counter | accesses | `store`, `result` |
| `openjibo.persistence.postgresql.configured_max_connections` | observable gauge | connections | `store` |

The transport HTTP, WebSocket, connection, and active-session instruments remain in the same meter. Turn phases
are limited to `stt`, `plan`, `finalize`, and `other`. Outcomes are limited to `success`, `bypassed`,
`unavailable`, `failure`, `canceled`, and `other`. Persistence store, cache result, suppression reason, socket,
payload, message, endpoint, method, and status attributes have fixed allowlists in `TransportMetrics`; unknown
values collapse to `other`.

`finalize` is the current end-to-end server-side finalization interval. `plan` covers conversation routing and
plan creation. `stt` covers selection and transcription, or records a bypass when the robot supplied a usable
transcript or no audio required transcription. Robot acknowledgement latency is not observable in the current
wire protocol.

The audio high-water value is monotonic for the life of one process and resets when that replica restarts. The
configured PostgreSQL gauge is a ceiling, not live pool use.

## SigV4 Replay Observer Health

The observe-only legacy SigV4 replay path uses the separate
`Jibo.Cloud.SigV4ReplayObservation` meter. Its bounded instruments are:

| Instrument | Type | Attributes |
| --- | --- | --- |
| `openjibo.sigv4_replay_observation.outcomes` | counter | `operation`, `key_slot`, `outcome` |
| `openjibo.sigv4_replay_observation.degraded_publishers` | observable gauge | none |
| `openjibo.sigv4_replay_observation.health_transitions` | counter | `state` |

Operations, key slots, outcomes, and transition states are fixed application labels. The path never emits a
digest, access key, device, robot, request, or exception value as a metric attribute. A persistence outage logs
one warning when the publisher enters the degraded state, continues counting every failed observation, and
coalesces later warnings until a fully successful work item records recovery and the suppressed-failure count.
Request authorization and token issuance do not depend on replay persistence.

## Collection And Provider Metrics

Managed Azure deployments provision a workspace-backed Application Insights resource and register the Azure
Monitor OpenTelemetry metrics exporter when `APPLICATIONINSIGHTS_CONNECTION_STRING` is present. The exporter
subscribes to:

- `OpenJibo.Transport` for the application instruments above;
- `Jibo.Cloud.SigV4ReplayObservation` when the shadow replay observer is explicitly enabled;
- the .NET runtime metrics for working set, managed heap, allocation rate, GC collections, and pause duration;
- Npgsql's native metrics for pool connections, pending requests/waits, command duration, and failures;
- Azure Container Apps and Azure Database for PostgreSQL platform metrics for replica, CPU, memory, restart,
  database CPU/storage/connection, and network evidence.

Only the metrics signal is exported by the application; request traces and Serilog events continue through the
existing Container Apps Log Analytics path so enabling operational measurements does not duplicate those data.
Npgsql data sources use the bounded names `cloud_state` and `personal_memory`; never allow a connection string
to become the pool-name attribute.

Do not duplicate Npgsql internals with a second application-side pool tracker. Reconcile the provider's live pool
measurements against `openjibo.persistence.postgresql.configured_max_connections` instead.

Managed deployments explicitly set the cloud-state pool ceiling to `8` and personal-memory to `4` connections
per replica. At the current maximum of two replicas, the static worst-case application pool budget is therefore
`24` of PostgreSQL's `50` connections. The configured ceiling is not an assertion that all 24 connections are
open; live Npgsql usage remains the primary observation. Deployment parameters are capped at `12` and `6`
respectively so one accidental override cannot consume the current server budget.

Azure Monitor does not preserve arbitrary percentiles for OpenTelemetry histogram exports. The capacity report
therefore labels runtime GC-pause and Npgsql command-duration values as weighted average and observed maximum;
it does not present a percentile computed from already-aggregated histogram rows. Application gauges and
counters retain their valid P50/P95 summaries.

## Seven-Day Evidence Command

Install the official Azure CLI Application Insights extension once, then run the report from the repository root:

```powershell
az extension add --name application-insights --yes
node scripts/cloud/openjibo-capacity-report.mjs `
  --resource-group rg-openjibo-staging `
  --container-app openjibo-cloud `
  --application-insights appi-openjibo-managed `
  --days 7 `
  --average-robots 2.5 `
  --format markdown `
  --output artifact-output/openjibo-staging-capacity.md
```

Use staging for active fake-robot load tiers. A passive baseline for the real average of 2.5 connected robots
must instead read production aggregate telemetry without sending probe traffic:

```powershell
node scripts/cloud/openjibo-capacity-report.mjs `
  --resource-group rg-openjibo-prod `
  --container-app openjibo-cloud `
  --application-insights appi-openjibo-managed `
  --days 7 `
  --average-robots 2.5 `
  --format markdown `
  --output artifact-output/openjibo-production-capacity.md
```

Do not run the fake-robot load driver against production. Keep the production image and ready revision unchanged
during the passive observation window: even a configuration-only Container App update creates a new revision and
restarts the exact-revision evidence clock. An idle staging window does not become representative merely by
reaching seven days; it still needs application payload traffic and database command samples.

The report resolves the latest ready revision first and filters both Application Insights and Container Apps
metrics to that exact revision. This prevents an old rollback, failed deployment, or overlapping rollout replica
from being attributed to the build under observation. It reads only aggregate metric values; it does not query or
emit robot, device, account, session, transcript, credential, connection-string, or pool-name values.

An observation is `insufficient-evidence` until its exact revision covers at least 80% of the requested window
(134.4 hours for a seven-day run). A restart, database command failure, or audio-limit rejection also prevents a
representative classification. The window must also contain application payload traffic and database command
samples; seven idle days are not representative robot evidence. A passing classification remains a bounded operating hypothesis, not a linear
robot-count extrapolation. Preserve the generated report with the exact-commit staging gate artifact and note any
deployment, load-smoke, or unusual robot-use periods that overlap the window.

Application payload bytes are expected to be below Container Apps `RxBytes + TxBytes`: platform traffic also
contains TLS/WebSocket framing, database and provider calls, health traffic, image/startup activity, and other
protocol overhead. Investigate a rising gap across comparable quiet windows; do not expect equality.

In the September 3 production sample, outbound application payload was about 0.51 MiB versus roughly 1.75 GiB
inbound. Even eliminating all measured outbound application bytes would change total application payload by only
about 0.03%. Static HTTP text compression is therefore not a meaningful capacity-runway lever for this workload;
keep it as a separate portal/latency optimization. The inbound side remains dominated by already-compressed
Ogg Opus audio and must be evaluated through packet/envelope changes and physical-client compatibility evidence.

The capacity report may infer a zero for missing database pending-request samples only when database
command-duration and connection-usage samples are present, and for a missing database-failure counter only when
database command-duration samples populate distinct hourly buckets across the representative coverage threshold
and prove that the provider meter
was active. It may infer a zero for missing
audio-limit-rejection counter samples only when accepted-audio or buffered-audio gauge samples prove that the
OpenJibo transport meter was active across that same hourly-bucket threshold. It may infer a zero for missing
restart samples only when distinct populated working-set and replica hours meet the threshold. These inferences
are recorded in
`evidence.inferredZeroSignals`; without the corroborating signals, the missing metric remains a blocker and is not
treated as zero.

## Representative Production Baseline

The read-only report generated on `2026-09-07` covers 143.38 of 168 hours (85.35%) for production revision
`openjibo-cloud--0000049` and image `sha-3773955b69c6`. It is the first sample to pass the representative-evidence
gate at the planning assumption of 2.5 average continuously connected robots.

| Signal | Observed baseline |
| --- | ---: |
| Application working-set maximum | 261 MiB of 2 GiB |
| Container Apps working-set maximum | 304 MiB of 2 GiB |
| Highest hourly-average CPU | 2.1% of one core |
| PostgreSQL connections | P95 2, maximum 3 of 50 |
| PostgreSQL command duration | 1.70 ms weighted average, 1.83 s observed maximum |
| Persistence cache hit ratio | 99.64% |
| Buffered audio | P95 0 B, 153 KiB high-water maximum |
| Application payload per observed robot-day | 307 MiB |
| Estimated platform wire traffic per observed robot-day | 395 MiB |

No restart, database command failure, pending-request queue, or audio-limit rejection was observed. These values
describe this revision and workload only. The per-robot traffic values are useful for a cost worksheet but must
not be extrapolated as a fleet-capacity curve.

## Provisional Alert Review Table

These are candidate sustained-window thresholds derived from the representative baseline and current hard limits.
They are not deployed alerts. Review the operator action group, ownership, and escalation procedure before enabling
paging, and prefer per-replica/finer-grained queries where an hourly aggregate could hide a hot instance.

| Signal | Review warning | Review critical |
| --- | --- | --- |
| Per-replica working set | above 1.0 GiB for 15 minutes | above 1.5 GiB for 5 minutes |
| Per-replica CPU | above 50% for 15 minutes | above 70% for 10 minutes |
| PostgreSQL live connections | P95 at least 12 for 15 minutes | at least 20, or server total above 30 |
| Npgsql pending requests | any nonzero value sustained for 5 minutes | continued queueing with turn degradation |
| Database command failure | any occurrence | repeated occurrence; invalidate capacity evidence |
| Database weighted-average command duration | above 100 ms sustained | above 250 ms sustained |
| Audio buffering | P95 above 16 MiB or high-water above 32 MiB | P95 above 32 MiB or high-water above 48 MiB |
| Audio-limit rejection | any occurrence | immediate investigation |
| Container restart | any unexpected occurrence | repeated or correlated with memory growth |
| Persistence cache hit ratio | below 95% sustained | below 90% sustained |
| Platform wire traffic per robot-day | above 500 MiB over 24 hours | above 700 MiB after workload-mix review |

The audio high-water gauge resets when a replica restarts, so evaluate it with current buffered bytes and restart
evidence. Do not turn the table into deployment defaults until the short staging matrix, longer soak, and operator
review establish that these thresholds are actionable rather than noisy.

## Capacity Worksheet

For each load-test tier (`6`, `10`, `15`, and `20` connected fake robots), retain the same time window and record:

1. connected sockets, active turns, finalize throughput, and success/failure/cancellation counts;
2. STT, plan, and finalize P50/P95/P99 durations;
3. current and high-water audio bytes plus rejection count;
4. cache hit ratio by bounded store;
5. configured and observed PostgreSQL connections, pool waits, command duration, and errors;
6. per-replica CPU, working set, managed heap, GC pauses, restarts, and revision identity.

Use `hits / (hits + misses)` for cache ratio. Treat any pool wait, audio rejection, restart, OOM, rising overnight
memory slope, or cross-replica inconsistency as a failed tier even when average latency looks acceptable. A tier
is not the enrollment cap until its highest simultaneous-turn case retains at least 25% measured headroom.
