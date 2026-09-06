# Runtime Operational Metrics

Status date: `2026-08-31`

OpenJibo emits privacy-safe aggregate measurements through the .NET meter `OpenJibo.Transport`. These
measurements are intended to establish a concurrency and cost envelope; they are not a customer activity log.
Robot IDs, session IDs, transcripts, audio, credentials, connection strings, and free-form error text must never
be added as metric attributes.

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

## Collection And Provider Metrics

Managed Azure deployments provision a workspace-backed Application Insights resource and register the Azure
Monitor OpenTelemetry metrics exporter when `APPLICATIONINSIGHTS_CONNECTION_STRING` is present. The exporter
subscribes to:

- `OpenJibo.Transport` for the application instruments above;
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
