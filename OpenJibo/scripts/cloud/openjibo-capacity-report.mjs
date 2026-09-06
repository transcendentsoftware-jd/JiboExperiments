#!/usr/bin/env node

import { existsSync, writeFileSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";

const APP_METRICS = [
  "dotnet.process.memory.working_set",
  "dotnet.gc.pause.time",
  "db.client.connections.usage",
  "db.client.connections.pending_requests",
  "db.client.commands.duration",
  "db.client.commands.failed",
  "openjibo.persistence.postgresql.configured_max_connections",
  "openjibo.persistence.cache.accesses",
  "openjibo.audio.accepted_bytes",
  "openjibo.audio.current_buffered_bytes",
  "openjibo.audio.buffered_high_water_bytes",
  "openjibo.audio.buffer_limit_rejections",
  "openjibo.transport.websocket.payload_bytes",
  "openjibo.transport.http.payload_bytes",
];

const PLATFORM_METRICS = [
  ["WorkingSetBytes", "Maximum"], ["UsageNanoCores", "Average"], ["RxBytes", "Total"],
  ["TxBytes", "Total"], ["Requests", "Total"], ["Replicas", "Maximum"], ["RestartCount", "Maximum"],
];

export function parseArgs(argv) {
  const values = { resourceGroup: "rg-openjibo-staging", containerApp: "openjibo-cloud",
    applicationInsights: "appi-openjibo-managed", days: 7, averageRobots: 2.5, format: "markdown",
    output: null, postgresServer: null };
  const options = new Map([["--resource-group", "resourceGroup"], ["--container-app", "containerApp"],
    ["--application-insights", "applicationInsights"], ["--days", "days"],
    ["--average-robots", "averageRobots"], ["--format", "format"], ["--output", "output"],
    ["--postgres-server", "postgresServer"]]);
  for (let index = 0; index < argv.length; index += 1) {
    const argument = argv[index];
    if (argument === "--help") values.help = true;
    else if (options.has(argument)) {
      const value = argv[++index];
      if (!value || value.startsWith("--")) throw new Error(`${argument} requires a value.`);
      values[options.get(argument)] = value;
    } else throw new Error(`Unknown option: ${argument}`);
  }
  values.days = Number(values.days);
  values.averageRobots = Number(values.averageRobots);
  if (!Number.isInteger(values.days) || values.days < 1 || values.days > 30)
    throw new Error("--days must be an integer from 1 through 30.");
  if (!Number.isFinite(values.averageRobots) || values.averageRobots <= 0 || values.averageRobots > 10000)
    throw new Error("--average-robots must be greater than zero and no more than 10000.");
  if (!new Set(["json", "markdown"]).has(values.format)) throw new Error("--format must be json or markdown.");
  return values;
}

export function buildApplicationQuery(days, revision = null) {
  const names = APP_METRICS.map((name) => `'${name}'`).join(",");
  if (revision && !/^[A-Za-z0-9-]+$/.test(revision)) throw new Error("Revision name was not Azure-safe.");
  const revisionFilter = revision ? `\n| where cloud_RoleInstance startswith '${revision}/'` : "";
  return `let source = customMetrics
| where timestamp > ago(${days}d)
${revisionFilter}
| where name in (${names});
let scalar = source
| where name in ('dotnet.process.memory.working_set','db.client.connections.pending_requests','db.client.commands.failed','openjibo.audio.accepted_bytes','openjibo.audio.current_buffered_bytes','openjibo.audio.buffered_high_water_bytes','openjibo.audio.buffer_limit_rejections')
| summarize Samples=count(), Hours=dcount(bin(timestamp, 1h)), P50=percentile(value, 50), P95=percentile(value, 95), Average=avg(value), Max=max(value), Total=sum(value), First=min(timestamp), Last=max(timestamp) by Metric=name;
let histograms = source
| where name in ('dotnet.gc.pause.time','db.client.commands.duration')
| summarize Samples=tolong(sum(valueCount)), Hours=dcount(bin(timestamp, 1h)), Average=sum(valueSum) / sum(valueCount), Max=max(valueMax), Total=sum(valueSum), First=min(timestamp), Last=max(timestamp) by Metric=name
| extend P50=real(null), P95=real(null);
let connectionUsage = source
| where name == 'db.client.connections.usage'
| extend instance=tostring(cloud_RoleInstance), state=tostring(customDimensions['state'])
| summarize value=sum(value) by timestamp, instance, state
| summarize value=sum(value) by timestamp
| summarize Samples=count(), Hours=dcount(bin(timestamp, 1h)), P50=percentile(value, 50), P95=percentile(value, 95), Average=avg(value), Max=max(value), Total=sum(value), First=min(timestamp), Last=max(timestamp)
| extend Metric='db.client.connections.usage.total';
let configuredPools = source
| where name == 'openjibo.persistence.postgresql.configured_max_connections'
| extend instance=tostring(cloud_RoleInstance), store=tostring(customDimensions['store'])
| summarize poolMax=max(value), First=min(timestamp), Last=max(timestamp) by instance, store
| summarize value=sum(poolMax), First=min(First), Last=max(Last) by instance
| summarize Samples=count(), P50=percentile(value, 50), P95=percentile(value, 95), Average=avg(value), Max=max(value), Total=sum(value), First=min(First), Last=max(Last)
| extend Metric='openjibo.persistence.postgresql.configured_max_connections.per_replica';
let traffic = source
| where name in ('openjibo.transport.websocket.payload_bytes','openjibo.transport.http.payload_bytes')
| extend direction=tostring(customDimensions['direction'])
| summarize Samples=count(), Hours=dcount(bin(timestamp, 1h)), P50=percentile(value, 50), P95=percentile(value, 95), Average=avg(value), Max=max(value), Total=sum(value), First=min(timestamp), Last=max(timestamp) by direction
| extend Metric=strcat('openjibo.transport.payload_bytes.', iff(direction == 'out', 'out', 'in'));
let cache = source
| where name == 'openjibo.persistence.cache.accesses'
| extend result=tostring(customDimensions['result'])
| summarize Samples=count(), Hours=dcount(bin(timestamp, 1h)), P50=percentile(value, 50), P95=percentile(value, 95), Average=avg(value), Max=max(value), Total=sum(value), First=min(timestamp), Last=max(timestamp) by result
| extend Metric=strcat('openjibo.persistence.cache.accesses.', iff(result == 'hit', 'hit', 'miss'));
union scalar, histograms, connectionUsage, configuredPools, traffic, cache
| project Metric, Samples, Hours, P50, P95, Average, Max, Total, First, Last
| order by Metric asc`;
}

function runAz(args) {
  let executable = "az";
  let executableArgs = args;
  if (process.platform === "win32") {
    const lookup = spawnSync("where.exe", ["az.cmd"], { encoding: "utf8", windowsHide: true });
    const launcher = lookup.status === 0 ? lookup.stdout.split(/\r?\n/).find(Boolean)?.trim() : null;
    const python = launcher ? resolve(dirname(launcher), "..", "python.exe") : null;
    if (!python || !existsSync(python))
      throw new Error("Azure CLI's bundled Python executable could not be resolved from az.cmd.");
    executable = python;
    executableArgs = ["-IBm", "azure.cli", ...args];
  }
  const result = spawnSync(executable, executableArgs, { encoding: "utf8", windowsHide: true });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error((result.stderr || `Azure CLI exited ${result.status}.`).trim());
  try { return JSON.parse(result.stdout || "null"); }
  catch (error) { throw new Error(`Azure CLI returned invalid JSON: ${error.message}`); }
}

export function tableRows(payload) {
  const table = payload?.tables?.[0];
  if (!table) return [];
  const names = table.columns.map((column) => column.name);
  return table.rows.map((row) => Object.fromEntries(names.map((name, index) => [name, row[index]])));
}

export function parseMemoryBytes(value) {
  const match = /^\s*([0-9]+(?:\.[0-9]+)?)\s*(Ki|Mi|Gi|Ti)?\s*$/i.exec(String(value ?? ""));
  if (!match) return null;
  const units = { "": 1, ki: 1024, mi: 1024 ** 2, gi: 1024 ** 3, ti: 1024 ** 4 };
  return Number(match[1]) * units[(match[2] ?? "").toLowerCase()];
}

export function summarizePlatformMetric(payload, aggregation) {
  const metric = payload?.value?.[0];
  const key = aggregation.toLowerCase();
  const populatedPoints = (metric?.timeseries ?? []).flatMap((series) => series.data ?? [])
    .filter((point) => Number.isFinite(point[key]));
  const points = populatedPoints.map((point) => point[key]);
  const hours = new Set(populatedPoints.map((point) => Date.parse(point.timeStamp))
    .filter(Number.isFinite).map((timestamp) => Math.floor(timestamp / 3_600_000))).size;
  return { name: metric?.name?.value ?? null, unit: metric?.unit ?? null, samples: points.length, hours,
    total: points.reduce((sum, value) => sum + value, 0), max: points.length ? Math.max(...points) : null,
    average: points.length ? points.reduce((sum, value) => sum + value, 0) / points.length : null };
}

function metricByName(rows, name) { return rows.find((row) => row.Metric === name) ?? null; }
function finite(value) {
  if (value === null || value === undefined || value === "") return null;
  const number = Number(value);
  return Number.isFinite(number) ? number : null;
}
function ratio(numerator, denominator) {
  return finite(numerator) !== null && finite(denominator) > 0 ? numerator / denominator : null;
}

export function buildCapacityReport({ options, container, applicationRows, platform, postgresMaxConnections,
  configuredPoolCapacityPerReplica = null }) {
  const memoryLimitBytes = parseMemoryBytes(container?.properties?.template?.containers?.[0]?.resources?.memory);
  const cpuLimitCores = finite(container?.properties?.template?.containers?.[0]?.resources?.cpu);
  const appMemory = metricByName(applicationRows, "dotnet.process.memory.working_set");
  const connectionUsage = metricByName(applicationRows, "db.client.connections.usage.total");
  const inbound = metricByName(applicationRows, "openjibo.transport.payload_bytes.in");
  const outbound = metricByName(applicationRows, "openjibo.transport.payload_bytes.out");
  const cacheHits = metricByName(applicationRows, "openjibo.persistence.cache.accesses.hit");
  const cacheMisses = metricByName(applicationRows, "openjibo.persistence.cache.accesses.miss");
  const observedFirst = applicationRows.map((row) => Date.parse(row.First)).filter(Number.isFinite);
  const observedLast = applicationRows.map((row) => Date.parse(row.Last)).filter(Number.isFinite);
  const observedHours = observedFirst.length && observedLast.length
    ? Math.max(0, (Math.max(...observedLast) - Math.min(...observedFirst)) / 3_600_000) : 0;
  const requestedHours = options.days * 24;
  const coverageRatio = Math.min(1, observedHours / requestedHours);
  const requiredCoverageHours = requestedHours * 0.8;
  const metricHasRepresentativeCoverage = (row) =>
    (finite(row?.Hours) ?? 0) >= Math.ceil(requiredCoverageHours);
  const platformMetricHasRepresentativeCoverage = (metric) =>
    (finite(metric?.hours) ?? 0) >= Math.ceil(requiredCoverageHours);
  const appTrafficBytes = (finite(inbound?.Total) ?? 0) + (finite(outbound?.Total) ?? 0);
  const platformTrafficBytes = (finite(platform.RxBytes?.total) ?? 0) + (finite(platform.TxBytes?.total) ?? 0);
  const observedRobotDays = options.averageRobots * Math.max(observedHours / 24, 0);
  const cacheAccesses = (finite(cacheHits?.Total) ?? 0) + (finite(cacheMisses?.Total) ?? 0);
  const configuredMaxReplicas = finite(container?.properties?.template?.scale?.maxReplicas);
  const configuredPoolCapacityAtMaxScale = finite(configuredPoolCapacityPerReplica) !== null &&
    configuredMaxReplicas !== null ? configuredPoolCapacityPerReplica * configuredMaxReplicas : null;
  const dbFailures = metricByName(applicationRows, "db.client.commands.failed");
  const dbDuration = metricByName(applicationRows, "db.client.commands.duration");
  const pendingRequests = metricByName(applicationRows, "db.client.connections.pending_requests");
  const acceptedAudioBytes = metricByName(applicationRows, "openjibo.audio.accepted_bytes");
  const bufferedAudioCurrent = metricByName(applicationRows, "openjibo.audio.current_buffered_bytes");
  const bufferedAudioHighWater = metricByName(applicationRows, "openjibo.audio.buffered_high_water_bytes");
  const audioRejections = metricByName(applicationRows, "openjibo.audio.buffer_limit_rejections");
  const restarts = platform.RestartCount?.max ?? null;
  const inferredZeroSignals = [];
  const inferPendingRequestsZero = (finite(pendingRequests?.Samples) ?? 0) === 0 &&
    metricHasRepresentativeCoverage(dbDuration) && metricHasRepresentativeCoverage(connectionUsage);
  const inferRestartsZero = (finite(platform.RestartCount?.samples) ?? 0) === 0 &&
    platformMetricHasRepresentativeCoverage(platform.WorkingSetBytes) &&
    platformMetricHasRepresentativeCoverage(platform.Replicas);
  const inferDatabaseFailuresZero = (finite(dbFailures?.Samples) ?? 0) === 0 &&
    metricHasRepresentativeCoverage(dbDuration);
  const inferAudioRejectionsZero = (finite(audioRejections?.Samples) ?? 0) === 0 &&
    (metricHasRepresentativeCoverage(acceptedAudioBytes) ||
      metricHasRepresentativeCoverage(bufferedAudioCurrent) ||
      metricHasRepresentativeCoverage(bufferedAudioHighWater));
  if (inferPendingRequestsZero) inferredZeroSignals.push("databasePendingRequests");
  if (inferRestartsZero) inferredZeroSignals.push("platformRestarts");
  if (inferDatabaseFailuresZero) inferredZeroSignals.push("databaseFailures");
  if (inferAudioRejectionsZero) inferredZeroSignals.push("audioLimitRejections");
  const pendingRequestP95 = finite(pendingRequests?.P95) ?? (inferPendingRequestsZero ? 0 : null);
  const pendingRequestMax = finite(pendingRequests?.Max) ?? (inferPendingRequestsZero ? 0 : null);
  const restartMax = finite(restarts) ?? (inferRestartsZero ? 0 : null);
  const databaseFailureTotal = finite(dbFailures?.Total) ?? (inferDatabaseFailuresZero ? 0 : null);
  const audioRejectionTotal = finite(audioRejections?.Total) ?? (inferAudioRejectionsZero ? 0 : null);
  const sufficientCoverage = observedHours >= requiredCoverageHours;
  const hasRepresentativeActivity = appTrafficBytes > 0 &&
    (finite(metricByName(applicationRows, "db.client.commands.duration")?.Samples) ?? 0) > 0;
  const requiredSignals = {
    applicationMemory: (finite(appMemory?.Samples) ?? 0) > 0,
    databaseDuration: (finite(dbDuration?.Samples) ?? 0) > 0,
    databaseConnections: (finite(connectionUsage?.Samples) ?? 0) > 0,
    databasePendingRequests: (finite(pendingRequests?.Samples) ?? 0) > 0 || inferPendingRequestsZero,
    databaseFailures: (finite(dbFailures?.Samples) ?? 0) > 0 || inferDatabaseFailuresZero,
    audioLimitRejections: (finite(audioRejections?.Samples) ?? 0) > 0 || inferAudioRejectionsZero,
    platformMemory: (finite(platform.WorkingSetBytes?.samples) ?? 0) > 0,
    platformRestarts: (finite(platform.RestartCount?.samples) ?? 0) > 0 || inferRestartsZero,
  };
  const missingSignals = Object.entries(requiredSignals).filter(([, present]) => !present).map(([name]) => name);
  const reliabilitySignalDetected = [databaseFailureTotal, audioRejectionTotal, restartMax, pendingRequestMax]
    .some((value) => finite(value) !== null && finite(value) > 0);
  const blockers = [];
  if (!sufficientCoverage) blockers.push("observation-window-incomplete");
  if (!hasRepresentativeActivity) blockers.push("representative-robot-activity-absent");
  if (missingSignals.length) blockers.push("required-telemetry-missing");
  if (reliabilitySignalDetected) blockers.push("reliability-signal-detected");
  return {
    generatedUtc: new Date().toISOString(),
    scope: { resourceGroup: options.resourceGroup, containerApp: options.containerApp,
      applicationInsights: options.applicationInsights, requestedDays: options.days,
      averageRobotsAssumption: options.averageRobots,
      image: container?.properties?.template?.containers?.[0]?.image ?? null,
      revision: container?.properties?.latestReadyRevisionName ?? null,
      replicaScale: container?.properties?.template?.scale ?? null },
    evidence: { observedHours, requestedHours, coverageRatio,
      representativeActivityObserved: hasRepresentativeActivity, missingSignals, inferredZeroSignals, blockers,
      classification: blockers.length === 0 ? "representative-evidence" : "insufficient-evidence",
      caveat: blockers.length === 0
        ? "Coverage, activity, and zero-failure checks passed; this remains a bounded hypothesis, not a linear fleet extrapolation."
        : `No capacity claim: ${blockers.join(", ")}. Deployment, smoke, or idle time may dominate the sample.` },
    memory: { containerLimitBytes: memoryLimitBytes, applicationP95Bytes: finite(appMemory?.P95),
      applicationMaxBytes: finite(appMemory?.Max), platformMaxBytes: finite(platform.WorkingSetBytes?.max),
      applicationMaxLimitRatio: ratio(appMemory?.Max, memoryLimitBytes),
      platformMaxLimitRatio: ratio(platform.WorkingSetBytes?.max, memoryLimitBytes),
      gcPauseAverageSeconds: finite(metricByName(applicationRows, "dotnet.gc.pause.time")?.Average),
      gcPauseMaxSeconds: finite(metricByName(applicationRows, "dotnet.gc.pause.time")?.Max) },
    cpu: { containerLimitCores: cpuLimitCores,
      platformMaxAverageCores: finite(platform.UsageNanoCores?.max) === null
        ? null : platform.UsageNanoCores.max / 1_000_000_000,
      platformMaxAverageLimitRatio: ratio(platform.UsageNanoCores?.max,
        cpuLimitCores === null ? null : cpuLimitCores * 1_000_000_000) },
    database: { postgresMaxConnections: finite(postgresMaxConnections),
      observedConnectionP95: finite(connectionUsage?.P95), observedConnectionMax: finite(connectionUsage?.Max),
      configuredPoolCapacityPerReplica: finite(configuredPoolCapacityPerReplica),
      configuredPoolCapacityAtMaxScale,
      observedServerLimitRatio: ratio(connectionUsage?.Max, postgresMaxConnections),
      configuredServerLimitRatio: ratio(configuredPoolCapacityAtMaxScale, postgresMaxConnections),
      commandDurationAverageSeconds: finite(metricByName(applicationRows, "db.client.commands.duration")?.Average),
      commandDurationMaxSeconds: finite(metricByName(applicationRows, "db.client.commands.duration")?.Max),
      commandFailures: databaseFailureTotal, pendingRequestP95,
      pendingRequestMax, cacheHitRatio: ratio(cacheHits?.Total, cacheAccesses) },
    traffic: { applicationInboundPayloadBytes: finite(inbound?.Total) ?? 0,
      applicationOutboundPayloadBytes: finite(outbound?.Total) ?? 0,
      platformRxBytes: finite(platform.RxBytes?.total), platformTxBytes: finite(platform.TxBytes?.total),
      applicationToPlatformRatio: ratio(appTrafficBytes, platformTrafficBytes),
      applicationBytesPerObservedRobotDay: ratio(appTrafficBytes, observedRobotDays) },
    audio: { acceptedBytes: finite(acceptedAudioBytes?.Total),
      bufferedCurrentP95Bytes: finite(bufferedAudioCurrent?.P95),
      bufferedHighWaterMaxBytes: finite(bufferedAudioHighWater?.Max) },
    reliability: { containerRestarts: restartMax, audioLimitRejections: audioRejectionTotal,
      requestCount: finite(platform.Requests?.total), maximumReplicasObserved: finite(platform.Replicas?.max) },
  };
}

function formatBytes(value) {
  if (!Number.isFinite(value)) return "n/a";
  const units = ["B", "KiB", "MiB", "GiB", "TiB"];
  let scaled = value; let unit = 0;
  while (Math.abs(scaled) >= 1024 && unit < units.length - 1) { scaled /= 1024; unit += 1; }
  return `${scaled.toFixed(unit === 0 ? 0 : 2)} ${units[unit]}`;
}
function formatPercent(value) { return Number.isFinite(value) ? `${(value * 100).toFixed(1)}%` : "n/a"; }
function formatNumber(value, digits = 2) { return Number.isFinite(value) ? value.toFixed(digits) : "n/a"; }

export function renderMarkdown(report) {
  const inferredZeroSignals = report.evidence.inferredZeroSignals ?? [];
  return `# OpenJibo capacity evidence\n\nGenerated: ${report.generatedUtc}\n\n` +
    `Evidence classification: **${report.evidence.classification}**. ` +
    `${formatNumber(report.evidence.observedHours, 1)} of ${report.evidence.requestedHours} requested hours observed ` +
    `(${formatPercent(report.evidence.coverageRatio)}).\n\n${report.evidence.caveat}\n\n` +
    `Inferred zero signals: ${inferredZeroSignals.length ? inferredZeroSignals.join(", ") : "none"}.\n\n` +
    `| Boundary | Observation | Headroom indicator |\n|---|---:|---:|\n` +
    `| Application working set (P95 / max) | ${formatBytes(report.memory.applicationP95Bytes)} / ${formatBytes(report.memory.applicationMaxBytes)} | ${formatPercent(report.memory.applicationMaxLimitRatio)} of container limit |\n` +
    `| Container Apps working set (max) | ${formatBytes(report.memory.platformMaxBytes)} | ${formatPercent(report.memory.platformMaxLimitRatio)} of container limit |\n` +
    `| CPU (highest hourly average) | ${formatNumber(report.cpu.platformMaxAverageCores, 3)} cores | ${formatPercent(report.cpu.platformMaxAverageLimitRatio)} of container limit |\n` +
    `| GC pause (average / max) | ${formatNumber(report.memory.gcPauseAverageSeconds, 4)} / ${formatNumber(report.memory.gcPauseMaxSeconds, 4)} s | lower is better |\n` +
    `| PostgreSQL connections (P95 / max) | ${formatNumber(report.database.observedConnectionP95)} / ${formatNumber(report.database.observedConnectionMax)} | ${formatPercent(report.database.observedServerLimitRatio)} of server limit |\n` +
    `| Configured pools (per replica / max scale) | ${formatNumber(report.database.configuredPoolCapacityPerReplica)} / ${formatNumber(report.database.configuredPoolCapacityAtMaxScale)} | ${formatPercent(report.database.configuredServerLimitRatio)} of server limit |\n` +
    `| DB command duration (average / max) | ${formatNumber(report.database.commandDurationAverageSeconds, 3)} / ${formatNumber(report.database.commandDurationMaxSeconds, 3)} s | failures: ${formatNumber(report.database.commandFailures, 0)} |\n` +
    `| DB pending requests (P95 / max) | ${formatNumber(report.database.pendingRequestP95)} / ${formatNumber(report.database.pendingRequestMax)} | both should remain zero |\n` +
    `| Persistence cache hit ratio | ${formatPercent(report.database.cacheHitRatio)} | higher is better |\n` +
    `| App payload traffic | ${formatBytes(report.traffic.applicationInboundPayloadBytes + report.traffic.applicationOutboundPayloadBytes)} | ${formatPercent(report.traffic.applicationToPlatformRatio)} of platform wire bytes |\n` +
    `| App bytes per observed robot-day | ${formatBytes(report.traffic.applicationBytesPerObservedRobotDay)} | based on ${report.scope.averageRobotsAssumption} average robots |\n` +
    `| Buffered audio (current P95 / high-water max) | ${formatBytes(report.audio?.bufferedCurrentP95Bytes)} / ${formatBytes(report.audio?.bufferedHighWaterMaxBytes)} | both should remain bounded |\n` +
    `| Restarts / audio-limit rejections | ${formatNumber(report.reliability.containerRestarts, 0)} / ${formatNumber(report.reliability.audioLimitRejections, 0)} | both should remain zero |\n\n` +
    `Revision: \`${report.scope.revision ?? "unknown"}\`  \nImage: \`${report.scope.image ?? "unknown"}\`\n`;
}

export async function collectCapacityReport(options, azure = runAz) {
  const container = azure(["containerapp", "show", "--resource-group", options.resourceGroup,
    "--name", options.containerApp, "--output", "json"]);
  const resourceId = container?.id;
  if (!resourceId) throw new Error("Container App response did not contain a resource id.");
  const revision = container?.properties?.latestReadyRevisionName;
  if (!revision) throw new Error("Container App response did not contain a ready revision name.");
  const appPayload = azure(["monitor", "app-insights", "query", "--app", options.applicationInsights,
    "--resource-group", options.resourceGroup, "--analytics-query", buildApplicationQuery(options.days, revision),
    "--offset", `${options.days}d`,
    "--output", "json"]);
  const platform = {};
  for (const [name, aggregation] of PLATFORM_METRICS) {
    const payload = azure(["monitor", "metrics", "list", "--resource", resourceId, "--metric", name,
      "--interval", "PT1H", "--aggregation", aggregation, "--offset", `${options.days}d`,
      "--filter", `RevisionName eq '${revision}'`, "--output", "json"]);
    platform[name] = summarizePlatformMetric(payload, aggregation);
  }
  const env = container?.properties?.template?.containers?.[0]?.env ?? [];
  const configuredNumber = (name, fallback) => {
    const raw = env.find((item) => item.name === name)?.value;
    const parsed = Number(raw);
    return Number.isFinite(parsed) && parsed >= 1 ? parsed : fallback;
  };
  const configuredPoolCapacityPerReplica =
    configuredNumber("OpenJibo__State__PostgreSql__MaxPoolSize", 8) +
    configuredNumber("OpenJibo__PersonalMemory__PostgreSql__MaxPoolSize", 4);
  const postgresServer = options.postgresServer ?? env.find((item) =>
    item.name === "OpenJibo__Deployment__PostgreSqlServerName")?.value ?? null;
  let postgresMaxConnections = null;
  if (postgresServer) {
    const parameter = azure(["postgres", "flexible-server", "parameter", "show", "--resource-group",
      options.resourceGroup, "--server-name", postgresServer, "--name", "max_connections", "--output", "json"]);
    postgresMaxConnections = Number(parameter?.value);
  }
  return buildCapacityReport({ options, container, applicationRows: tableRows(appPayload), platform,
    postgresMaxConnections, configuredPoolCapacityPerReplica });
}

function usage() {
  return `Usage: node scripts/cloud/openjibo-capacity-report.mjs [options]\n\n` +
    `  --resource-group NAME       Azure resource group (default rg-openjibo-staging)\n` +
    `  --container-app NAME        Container App (default openjibo-cloud)\n` +
    `  --application-insights NAME Application Insights component (default appi-openjibo-managed)\n` +
    `  --postgres-server NAME      PostgreSQL server (otherwise read from non-secret app metadata)\n` +
    `  --days 1-30                 Observation window (default 7)\n` +
    `  --average-robots NUMBER     Average connected robots assumption (default 2.5)\n` +
    `  --format markdown|json      Output format (default markdown)\n` +
    `  --output PATH               Write output to a file instead of stdout\n` +
    `  --help                      Show this help\n`;
}

const invokedPath = process.argv[1]?.replaceAll("\\", "/");
if (invokedPath && fileURLToPath(import.meta.url).replaceAll("\\", "/") === invokedPath) {
  try {
    const options = parseArgs(process.argv.slice(2));
    if (options.help) process.stdout.write(usage());
    else {
      const report = await collectCapacityReport(options);
      const output = options.format === "json" ? `${JSON.stringify(report, null, 2)}\n` : renderMarkdown(report);
      if (options.output) writeFileSync(options.output, output, "utf8"); else process.stdout.write(output);
    }
  } catch (error) { process.stderr.write(`${error.message}\n`); process.exitCode = 1; }
}
