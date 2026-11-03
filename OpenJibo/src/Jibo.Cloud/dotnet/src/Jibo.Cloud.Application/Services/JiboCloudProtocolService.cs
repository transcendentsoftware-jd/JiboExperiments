using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Domain.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jibo.Cloud.Application.Services;

public sealed class JiboCloudProtocolService(
    ICloudStateStore stateStore,
    IMediaContentStore? mediaContentStore = null,
    IConfiguration? configuration = null,
    ICloudAuthProtocolHandler? authHandler = null,
    RobotNotificationRegistry? robotNotificationRegistry = null,
    LoopUpdatedPushService? loopUpdatedPushService = null,
    ILogger<JiboCloudProtocolService>? logger = null,
    RobotIdentitySuggestionStore? identitySuggestionStore = null)
{
    private const int SchedulerBackupDelayMs = 250;
    private const int SchedulerDownloadTickMs = 100;
    private const int SchedulerDownloadFinishDelayMs = 150;

    private static readonly HashSet<string> SupportedOpenJiboTargetModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "open-jibo",
        "open-jibo-ai",
        "open-jibo-self-hosted",
        "open-jibo-developer"
    };

    private readonly HashSet<string> _acceptedHosts = BuildAcceptedHosts(configuration);

    private readonly ICloudAuthProtocolHandler _authHandler =
        authHandler ?? new CloudAuthProtocolHandler(stateStore, identitySuggestionStore: identitySuggestionStore);

    private readonly string? _configuredRobotId = ReadConfiguredRobotId(configuration);
    private readonly string? _canonicalApiBaseUrl = ReadCanonicalApiBaseUrl(configuration);
    private readonly bool _protocolAuthDiagnosticsEnabled = ReadProtocolAuthDiagnosticsEnabled(configuration);
    private readonly ILogger _logger = logger ?? NullLogger<JiboCloudProtocolService>.Instance;
    private readonly ProtocolRobotIdentityResolver _identityResolver = new(stateStore);

    private readonly IMediaContentStore _mediaContentStore = mediaContentStore ?? new NullMediaContentStore();
    private readonly RobotNotificationRegistry? _robotNotificationRegistry = robotNotificationRegistry;
    private readonly LoopUpdatedPushService? _loopUpdatedPushService = loopUpdatedPushService;
    private readonly ConcurrentDictionary<string, OobeTokenState> _oobeTokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OobeTokenState> _implicitOobeStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _oobeLock = new();
    private readonly Lock _schedulerLock = new();
    private readonly SchedulerRuntimeState _schedulerState = new();

    private static HashSet<string> BuildAcceptedHosts(IConfiguration? configuration)
    {
        var hosts = configuration?
            .GetSection("OpenJibo:AcceptedHosts")
            .GetChildren()
            .Select(child => child.Value)
            .OfType<string>()
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .ToArray() ?? [];

        return new HashSet<string>(hosts, StringComparer.OrdinalIgnoreCase);
    }

    private static string? ReadConfiguredRobotId(IConfiguration? configuration)
    {
        var robotId = configuration?["OpenJibo:Robot:RobotId"];
        return string.IsNullOrWhiteSpace(robotId) ? null : robotId.Trim();
    }

    private static string? ReadCanonicalApiBaseUrl(IConfiguration? configuration)
    {
        var value = configuration?["OpenJibo:CanonicalApiBaseUrl"];
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;
    }

    private static bool ReadProtocolAuthDiagnosticsEnabled(IConfiguration? configuration) =>
        bool.TryParse(configuration?["OpenJibo:ProtocolAuthDiagnostics:Enabled"], out var enabled) && enabled;

    private static string? ReadTargetHost(JsonElement? body)
    {
        return ReadString(body, "targetHost") ?? ReadString(body, "apiHost") ?? ReadString(body, "serverHost");
    }

    private static OobeBaselineEvidence ReadBaselineEvidence(JsonElement? body, ProtocolEnvelope envelope)
    {
        return new OobeBaselineEvidence
        {
            FirmwareVersion = ReadString(body, "firmwareVersion") ?? envelope.FirmwareVersion,
            ApplicationVersion = ReadString(body, "applicationVersion") ?? envelope.ApplicationVersion,
            Distribution = ReadString(body, "distribution") ?? ReadString(body, "distro"),
            StockMode = ReadString(body, "stockMode") ??
                        ReadString(body, "currentMode") ?? ReadString(body, "sourceMode"),
            RequireBaselineAudit = ReadBool(body, "requireBaselineAudit") || ReadBool(body, "requireBaselineEvidence")
        };
    }

    public Task<ProtocolDispatchResult> DispatchAsync(ProtocolEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ObserveIdentityCandidates(envelope);

        if (envelope.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
            envelope.Path == "/" &&
            string.IsNullOrWhiteSpace(envelope.ServicePrefix))
            return Task.FromResult(ProtocolDispatchResult.NoContent());

        if (envelope.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
            envelope.Path.Equals("/health", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(ProtocolDispatchResult.Ok(new { ok = true, host = envelope.HostName }));

        if (envelope.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
            envelope.Path.StartsWith("/media/", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(HandleMediaContent(envelope));

        if (TryHandleLocalSchedulerRequest(envelope, out var schedulerResult))
            return Task.FromResult(schedulerResult);

        if ((envelope.Method.Equals("PUT", StringComparison.OrdinalIgnoreCase) ||
             envelope.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)) &&
            (envelope.Path.StartsWith("/upload/asr-binary", StringComparison.OrdinalIgnoreCase) ||
             envelope.Path.StartsWith("/upload/log-events", StringComparison.OrdinalIgnoreCase) ||
             envelope.Path.StartsWith("/upload/log-binary", StringComparison.OrdinalIgnoreCase) ||
             envelope.Path.StartsWith("/log/binary", StringComparison.OrdinalIgnoreCase)))
            return Task.FromResult(HandleLogUpload(envelope, ResolveRobotIdentity(envelope, "log-upload")));

        if ((envelope.ServicePrefix ?? string.Empty).StartsWith("OOBE_", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(HandleOobe(envelope.Operation ?? string.Empty, envelope));

        if (_acceptedHosts.Count > 0 && !_acceptedHosts.Contains(envelope.HostName))
            return Task.FromResult(ProtocolDispatchResult.Ok(new
            {
                ok = true,
                accepted = false,
                host = envelope.HostName
            }));

        if (TryHandleLegacyRestRequest(envelope, out var legacyResult))
            return Task.FromResult(legacyResult);

        var servicePrefix = envelope.ServicePrefix ?? string.Empty;
        var operation = envelope.Operation ?? string.Empty;

        if (servicePrefix.StartsWith("Log_", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(HandleLog(operation, envelope, ResolveRobotIdentity(envelope, $"log.{operation}")));

        if (servicePrefix.StartsWith("Backup_", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(HandleBackup(operation, envelope));

        if (servicePrefix.StartsWith("Account_", StringComparison.OrdinalIgnoreCase))
            return _authHandler.HandleAccountAsync(operation, envelope, cancellationToken);

        if (servicePrefix.StartsWith("Notification_", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(HandleNotification(operation, envelope));

        if (servicePrefix.StartsWith("Loop_", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(HandleLoop(operation, envelope));

        if (servicePrefix.Equals("Media_20160725", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(HandleMedia(operation, envelope, ResolveRobotIdentity(envelope, $"media.{operation}")));

        if (servicePrefix.StartsWith("Key_", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(HandleKey(operation, envelope));

        if (servicePrefix.StartsWith("Person_", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(HandlePerson(operation, envelope));

        if (servicePrefix.StartsWith("Robot_", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(HandleRobot(operation, envelope));

        if (servicePrefix.StartsWith("Update_", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(HandleUpdate(operation, envelope));

        return Task.FromResult(ProtocolDispatchResult.Ok(new
        {
            ok = true,
            host = envelope.HostName,
            target = $"{servicePrefix}.{operation}".Trim('.'),
            operation,
            note = "unknown target default response"
        }));
    }

    private void ObserveIdentityCandidates(ProtocolEnvelope envelope)
    {
        if (identitySuggestionStore is null) return;

        var candidates = RobotIdentityCandidateExtractor.Extract(envelope.BodyText).ToList();
        if (RobotIdentitySuggestionStore.IsSafeIdentityName(envelope.DeviceId))
            candidates.Add(new RobotIdentityCandidate("X-Jibo-RobotId", envelope.DeviceId!.Trim()));
        if (candidates.Count == 0) return;

        var identity = _identityResolver.Resolve(envelope);
        var associatedDeviceId = identity.BearerIdentity ?? identity.CredentialIdentity ?? identity.DeviceId;
        if (string.IsNullOrWhiteSpace(associatedDeviceId)) return;

        var logEvidence = RobotLogIdentityEvidence.Extract(ReadBodyBytes(envelope));
        var associatedDevice = stateStore.GetDevicesForAdministration().FirstOrDefault(device =>
            device.DeviceId.Equals(associatedDeviceId, StringComparison.OrdinalIgnoreCase));
        if (logEvidence.HasConflictingEvidence || associatedDevice is not null &&
            logEvidence.HasSerialEvidence && !string.IsNullOrWhiteSpace(associatedDevice.VerifiedSerialNumber) &&
            !string.Equals(associatedDevice.VerifiedSerialNumber, logEvidence.SerialNumber,
                StringComparison.OrdinalIgnoreCase)) return;

        var operation = $"{envelope.ServicePrefix}.{envelope.Operation}".Trim('.');
        var source = string.IsNullOrWhiteSpace(operation)
            ? envelope.Transport
            : $"{envelope.Transport}:{operation}";
        foreach (var candidate in candidates)
            identitySuggestionStore.Observe(associatedDeviceId, candidate.Value, source, candidate.Field);
    }

    private static bool TryHandleLegacyRestRequest(ProtocolEnvelope envelope,
        out ProtocolDispatchResult result)
    {
        if (string.IsNullOrWhiteSpace(envelope.ServicePrefix) &&
            envelope.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
            envelope.Path.Equals("/v1/loop/suspend", StringComparison.OrdinalIgnoreCase))
        {
            result = ProtocolDispatchResult.Ok(new { ok = true });
            return true;
        }

        result = ProtocolDispatchResult.Ok(new { });
        return false;
    }

    private ProtocolDispatchResult HandleNotification(string operation, ProtocolEnvelope envelope)
    {
        return _authHandler.HandleNotification(operation, envelope);
    }

    private ProtocolDispatchResult HandleOobe(string operation, ProtocolEnvelope envelope)
    {
        // Tokens live in this singleton. Serialize their observation and mutation with
        // synchronous registration operations; this is not a database transaction.
        lock (_oobeLock)
            return HandleOobeCore(operation, envelope);
    }

    private ProtocolDispatchResult HandleOobeCore(string operation, ProtocolEnvelope envelope)
    {
        var body = envelope.TryParseBody();
        var token = ReadString(body, "token");

        if (ReadBool(body, "serialVerified") && ReadVerifiedSerialEvidence(body) is null)
            return ProtocolDispatchResult.Raw(400,
                "{\"error\":\"verified serial evidence must include a valid BOJW serial number and verification method\"}",
                "application/json");

        if (operation.Equals("PlanConversion", StringComparison.OrdinalIgnoreCase) ||
            operation.Equals("AuditConversion", StringComparison.OrdinalIgnoreCase))
        {
            var planState = new OobeTokenState
            {
                DeviceId = ReadString(body, "deviceId") ?? envelope.DeviceId,
                LoopId = ReadString(body, "loopId"),
                TargetMode = ResolveOpenJiboTargetMode(ReadString(body, "targetMode") ?? ReadString(body, "mode")),
                TargetHost = ReadTargetHost(body),
                RollbackSnapshotId = ReadString(body, "rollbackSnapshotId") ?? ReadString(body, "rollbackSnapshot"),
                BaselineEvidence = ReadBaselineEvidence(body, envelope),
                VerifiedSerialEvidence = ReadVerifiedSerialEvidence(body),
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1)
            };
            var planReadiness = EvaluateConversionReadiness(planState, false, envelope.HostName);

            return ProtocolDispatchResult.Ok(new
            {
                ok = true,
                operation,
                willWriteRobot = false,
                canPrepareRobot = planReadiness.CanWriteRobot,
                deviceId = planState.DeviceId,
                loopId = planState.LoopId,
                targetMode = planState.TargetMode,
                targetHost = ResolveOpenJiboTargetHost(planState.TargetMode, planState.TargetHost, envelope.HostName),
                rollbackSnapshotId = planState.RollbackSnapshotId,
                baselineEvidence = planState.BaselineEvidence.ToResponse(),
                hostMappings = BuildRobotHostMappings(planState.TargetMode, planState.TargetHost, envelope.HostName),
                onboardingSession = BuildOnboardingSessionPreview(planState, envelope.HostName),
                conversionReadiness = planReadiness.ToResponse()
            });
        }

        if (operation.Equals("PrepareRobot", StringComparison.OrdinalIgnoreCase))
        {
            var expiresUtc = DateTimeOffset.UtcNow.AddHours(1);
            var issuedToken = CreateOobeToken();
            var preparedState = new OobeTokenState
            {
                DeviceId = ReadString(body, "deviceId") ?? envelope.DeviceId,
                LoopId = ReadString(body, "loopId"),
                TargetMode = ResolveOpenJiboTargetMode(ReadString(body, "targetMode") ?? ReadString(body, "mode")),
                TargetHost = ReadTargetHost(body),
                RollbackSnapshotId = ReadString(body, "rollbackSnapshotId") ?? ReadString(body, "rollbackSnapshot"),
                BaselineEvidence = ReadBaselineEvidence(body, envelope),
                VerifiedSerialEvidence = ReadVerifiedSerialEvidence(body),
                ExpiresUtc = expiresUtc
            };
            _oobeTokens[issuedToken] = preparedState;
            var prepareReadiness = EvaluateConversionReadiness(preparedState, false, envelope.HostName);

            return ProtocolDispatchResult.Ok(new
            {
                token = issuedToken,
                expires = expiresUtc.ToUnixTimeMilliseconds(),
                deviceId = preparedState.DeviceId,
                loopId = preparedState.LoopId,
                targetMode = preparedState.TargetMode,
                targetHost = ResolveOpenJiboTargetHost(preparedState.TargetMode, preparedState.TargetHost,
                    envelope.HostName),
                rollbackSnapshotId = preparedState.RollbackSnapshotId,
                baselineEvidence = preparedState.BaselineEvidence.ToResponse(),
                hostMappings =
                    BuildRobotHostMappings(preparedState.TargetMode, preparedState.TargetHost, envelope.HostName),
                onboardingSession = BuildOnboardingSession(issuedToken, preparedState, envelope.HostName),
                conversionReadiness = prepareReadiness.ToResponse()
            });
        }

        if (operation.Equals("GetStatus", StringComparison.OrdinalIgnoreCase))
        {
            OobeTokenState? current = null;
            var hasTokenState = token is not null && _oobeTokens.TryGetValue(token, out current);
            var expired = hasTokenState && current!.ExpiresUtc <= DateTimeOffset.UtcNow;
            long? expires = hasTokenState ? current!.ExpiresUtc.ToUnixTimeMilliseconds() : null;
            var requestedTargetMode =
                ResolveOpenJiboTargetMode(ReadString(body, "targetMode") ?? ReadString(body, "mode"));
            var requestedTargetHost = ReadTargetHost(body);
            var targetMode = hasTokenState ? current!.TargetMode : requestedTargetMode;
            var targetHost = hasTokenState ? current!.TargetHost : requestedTargetHost;

            return ProtocolDispatchResult.Ok(new
            {
                prepared = hasTokenState,
                accepted = hasTokenState && !expired,
                complete = hasTokenState && !expired && current!.Complete,
                expired,
                deviceId = hasTokenState ? current!.DeviceId : null,
                loopId = hasTokenState ? current!.LoopId : null,
                targetMode,
                rollbackSnapshotId = hasTokenState ? current!.RollbackSnapshotId : null,
                baselineEvidence = hasTokenState
                    ? current!.BaselineEvidence.ToResponse()
                    : new OobeBaselineEvidence().ToResponse(),
                expires,
                targetHost = ResolveOpenJiboTargetHost(targetMode, targetHost, envelope.HostName),
                hostMappings = BuildRobotHostMappings(targetMode, targetHost, envelope.HostName),
                onboardingSession = hasTokenState ? BuildOnboardingSession(token!, current!, envelope.HostName) : null,
                conversionReadiness =
                    BuildConversionReadiness(hasTokenState ? current : null, expired, envelope.HostName)
            });
        }

        if (operation.Equals("VerifyConnection", StringComparison.OrdinalIgnoreCase) ||
            operation.Equals("ConnectionProof", StringComparison.OrdinalIgnoreCase))
        {
            OobeTokenState? current = null;
            var hasTokenState = token is not null && _oobeTokens.TryGetValue(token, out current);
            var expired = hasTokenState && current!.ExpiresUtc <= DateTimeOffset.UtcNow;
            // OOBE verification is scoped to its prepared robot. Do not read the service-wide
            // primary registration here: deployment smoke must not take ownership of it.
            var robot = hasTokenState
                ? stateStore.GetDevices().SingleOrDefault(device =>
                    string.Equals(device.DeviceId, current!.DeviceId, StringComparison.OrdinalIgnoreCase))
                : stateStore.GetRobot();
            var targetMode = hasTokenState ? current!.TargetMode : "open-jibo";
            var targetHost = hasTokenState
                ? ResolveOpenJiboTargetHost(current!.TargetMode, current.TargetHost, envelope.HostName)
                : ResolveOpenJiboTargetHost("open-jibo", null, envelope.HostName);
            var hostMappings = hasTokenState
                ? BuildRobotHostMappings(current!.TargetMode, current.TargetHost, envelope.HostName)
                : robot!.HostMappings;
            var readiness = EvaluateConversionReadiness(hasTokenState ? current : null, expired, envelope.HostName);
            var reportedConnectionHost = ReadReportedConnectionHost(body);
            var reportedHostMappings = ReadReportedHostMappings(body);
            var requiresReportedConnectionProof = ReadBool(body, "requireReportedConnectionProof") ||
                                                  ReadBool(body, "requirePhysicalConnectionProof") ||
                                                  ReadBool(body, "requireLiveRobotProof");
            var requiresFreshConnectionProof = ReadBool(body, "requireFreshConnectionProof") ||
                                               ReadBool(body, "requireFreshLiveRobotProof");
            var proofObservedAt = ReadProofObservedAt(body);
            var proofFreshnessPolicy = ReadProofFreshnessPolicy(body);
            var proofObservedAgeSeconds = proofObservedAt is null
                ? null
                : (long?)Math.Max(0, (long)(DateTimeOffset.UtcNow - proofObservedAt.Value).TotalSeconds);
            var proofFreshUntil = proofObservedAt?.AddSeconds(proofFreshnessPolicy.MaxAgeSeconds)
                .ToUnixTimeMilliseconds();
            var proofSource = ReadString(body, "connectionProofSource") ??
                              ReadString(body, "proofSource") ??
                              ReadString(body, "reportedProofSource");
            var proofId = ReadString(body, "connectionProofId") ??
                          ReadString(body, "proofId") ??
                          ReadString(body, "captureId");
            var connectionBlockers = BuildConnectionBlockers(hasTokenState, current, expired, robot, hostMappings,
                targetHost, reportedConnectionHost, reportedHostMappings, requiresReportedConnectionProof,
                requiresFreshConnectionProof, proofObservedAt, proofFreshnessPolicy);
            if (token is not null && !hasTokenState)
                connectionBlockers.Add("missing-prepared-oobe-token");

            return ProtocolDispatchResult.Ok(new
            {
                ok = true,
                operation,
                connected = connectionBlockers.Count == 0,
                prepared = hasTokenState,
                complete = hasTokenState ? !expired && current!.Complete : token is null && robot?.IsActive == true,
                expired,
                cloudVersion = OpenJiboCloudBuildInfo.Version,
                robotId = robot?.RobotId,
                deviceId = hasTokenState ? current!.DeviceId : robot?.DeviceId,
                loopId = hasTokenState ? current!.LoopId : stateStore.GetLoops().FirstOrDefault()?.LoopId,
                targetMode,
                targetHost,
                rollbackSnapshotId = hasTokenState ? current!.RollbackSnapshotId : null,
                baselineEvidence = hasTokenState
                    ? current!.BaselineEvidence.ToResponse()
                    : new OobeBaselineEvidence().ToResponse(),
                hostMappings,
                storedHostMappings = robot?.HostMappings ?? new Dictionary<string, string>(),
                reportedConnectionHost,
                reportedConnectionHostMatches = string.IsNullOrWhiteSpace(reportedConnectionHost) ||
                                                string.Equals(reportedConnectionHost, targetHost,
                                                    StringComparison.OrdinalIgnoreCase),
                reportedHostMappings,
                reportedHostMappingsMatch = reportedHostMappings.Count == 0 ||
                                           HostMappingsMatch(reportedHostMappings, hostMappings),
                reportedHostMappingCompleteness = BuildReportedHostMappingCompleteness(hostMappings, reportedHostMappings),
                requiresReportedConnectionProof,
                requiresFreshConnectionProof,
                reportedConnectionProofComplete = !string.IsNullOrWhiteSpace(reportedConnectionHost) &&
                                                  RequiredHostMappingsPresent(hostMappings, reportedHostMappings),
                reportedConnectionProof = new
                {
                    complete = !string.IsNullOrWhiteSpace(reportedConnectionHost) &&
                               RequiredHostMappingsPresent(hostMappings, reportedHostMappings),
                    fresh = !requiresFreshConnectionProof || IsFreshProofObservation(proofObservedAt, proofFreshnessPolicy),
                    observedAt = proofObservedAt?.ToUnixTimeMilliseconds(),
                    observedAgeSeconds = proofObservedAgeSeconds,
                    maxAgeSeconds = proofFreshnessPolicy.MaxAgeSeconds,
                    acceptedFutureClockSkewSeconds = proofFreshnessPolicy.AcceptedFutureClockSkewSeconds,
                    freshUntil = proofFreshUntil,
                    source = proofSource,
                    id = proofId
                },
                hostMappingsMatch = connectionBlockers.All(blocker => blocker != "host-mapping-mismatch"),
                connectionBlockers,
                connectionProofGuidance = BuildConnectionProofGuidance(connectionBlockers),
                conversionReadiness = readiness.ToResponse()
            });
        }

        if (!operation.Equals("SetupRobot", StringComparison.OrdinalIgnoreCase) &&
            !operation.Equals("ReconnectRobot", StringComparison.OrdinalIgnoreCase))
            return ProtocolDispatchResult.Ok(new { ok = true, operation });

        var robotId = ReadString(body, "id") ??
                      ReadString(body, "robotId") ??
                      (string.IsNullOrWhiteSpace(envelope.DeviceId) ? "unknown-robot" : envelope.DeviceId!);
        var setupRegistrationSource = envelope.Headers.TryGetValue("X-OpenJibo-Registration-Source",
            out var setupSourceHeader)
            ? setupSourceHeader
            : null;
        if (RobotRegistrationSources.Normalize(setupRegistrationSource, robotId) ==
            RobotRegistrationSources.DeploymentSmoke)
            return ProtocolDispatchResult.Raw(403,
                "{\"message\":\"Deployment smoke identities cannot be created through OOBE.\"}",
                "application/x-amz-json-1.1");

        OobeTokenState? state;
        var hasPreparedToken = token is not null;
        if (hasPreparedToken && !_oobeTokens.TryGetValue(token!, out state))
            return ProtocolDispatchResult.Raw(409, "{\"error\":\"unknown prepared OOBE token\"}", "application/json");
        if (!hasPreparedToken && !_implicitOobeStates.TryGetValue(robotId, out state))
        {
            state = new OobeTokenState
            {
                DeviceId = robotId,
                LoopId = ReadString(body, "loopId"),
                TargetMode = ResolveOpenJiboTargetMode(ReadString(body, "targetMode") ?? ReadString(body, "mode")),
                TargetHost = ReadTargetHost(body),
                RollbackSnapshotId = ReadString(body, "rollbackSnapshotId") ?? ReadString(body, "rollbackSnapshot"),
                BaselineEvidence = ReadBaselineEvidence(body, envelope),
                VerifiedSerialEvidence = ReadVerifiedSerialEvidence(body),
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1)
            };
            _implicitOobeStates[robotId] = state;
        }
        // Each branch above either resolves or creates the state.
        state = hasPreparedToken ? _oobeTokens[token!] : _implicitOobeStates[robotId];
        if (state.ExpiresUtc <= DateTimeOffset.UtcNow)
            return ProtocolDispatchResult.Raw(410, "{\"error\":\"oobe token expired\"}", "application/json");

        if (state.Complete && !string.Equals(state.DeviceId, robotId, StringComparison.OrdinalIgnoreCase))
            return ProtocolDispatchResult.Raw(409, "{\"error\":\"completed OOBE token belongs to another robot\"}", "application/json");
        var setupState = BuildTentativeSetupState(state, body, envelope, robotId);
        var setupReadiness =
            EvaluateConversionReadiness(hasPreparedToken ? setupState : null, false, envelope.HostName);
        if (hasPreparedToken && !setupReadiness.CanWriteRobot)
            return ProtocolDispatchResult.Raw(409, JsonSerializer.Serialize(new
            {
                error = "conversion readiness blocked",
                conversionReadiness = setupReadiness.ToResponse()
            }), "application/json");

        if (setupState.VerifiedSerialEvidence is not null && !hasPreparedToken)
            return ProtocolDispatchResult.Raw(409,
                "{\"error\":\"verified serial evidence requires a prepared OOBE token\"}", "application/json");

        var existingDevice = stateStore.GetDevices().SingleOrDefault(device =>
            string.Equals(device.DeviceId, robotId, StringComparison.OrdinalIgnoreCase));
        if (setupState.VerifiedSerialEvidence is not null &&
            !string.IsNullOrWhiteSpace(existingDevice?.VerifiedSerialNumber) &&
            !existingDevice.VerifiedSerialNumber.Equals(setupState.VerifiedSerialEvidence.SerialNumber,
                StringComparison.OrdinalIgnoreCase))
            return ProtocolDispatchResult.Raw(409,
                "{\"error\":\"verified serial evidence conflicts with the registered robot\"}", "application/json");

        var registeredDevice = stateStore.GetOrCreateDevice(robotId, envelope.FirmwareVersion,
            envelope.ApplicationVersion,
            envelope.Headers.TryGetValue("X-OpenJibo-Registration-Source", out var sourceHeader)
                ? sourceHeader
                : null);
        if (string.IsNullOrWhiteSpace(setupState.LoopId))
        {
            var robotLoop = stateStore.AddLoop(null, stateStore.GetAccount().AccountId, registeredDevice.RobotId,
                registeredDevice.DeviceId);
            setupState.LoopId = robotLoop.LoopId;
        }

        var isDeploymentSmoke = RobotRegistrationSources.Normalize(registeredDevice.RegistrationSource,
            registeredDevice.DeviceId).Equals(RobotRegistrationSources.DeploymentSmoke,
            StringComparison.OrdinalIgnoreCase);
        var updatedRegistration = new DeviceRegistration
        {
            DeviceId = registeredDevice.DeviceId,
            RobotId = registeredDevice.RobotId,
            FriendlyName = registeredDevice.FriendlyName,
            FirmwareVersion = registeredDevice.FirmwareVersion,
            ApplicationVersion = registeredDevice.ApplicationVersion,
            IsActive = registeredDevice.IsActive,
            CertificateThumbprint = registeredDevice.CertificateThumbprint,
            IssuedIdentityId = registeredDevice.IssuedIdentityId,
            BuildHash = registeredDevice.BuildHash,
            ConfigHash = registeredDevice.ConfigHash,
            VerifiedSerialNumber = setupState.VerifiedSerialEvidence?.SerialNumber ?? registeredDevice.VerifiedSerialNumber,
            SerialEvidenceSource = setupState.VerifiedSerialEvidence?.Source ?? registeredDevice.SerialEvidenceSource,
            SerialEvidenceVerifiedUtc = setupState.VerifiedSerialEvidence?.VerifiedUtc ?? registeredDevice.SerialEvidenceVerifiedUtc,
            RegistrationSource = registeredDevice.RegistrationSource,
            IsHidden = registeredDevice.IsHidden,
            ArchivedUtc = registeredDevice.ArchivedUtc,
            HostMappings = BuildRobotHostMappings(setupState.TargetMode, setupState.TargetHost, envelope.HostName)
        };

        var primary = stateStore.GetRobot();
        if (!isDeploymentSmoke &&
            (string.Equals(primary.DeviceId, registeredDevice.DeviceId, StringComparison.OrdinalIgnoreCase) ||
             RobotRegistrationSources.Normalize(primary.RegistrationSource, primary.DeviceId) == RobotRegistrationSources.Bootstrap))
            stateStore.UpdateRobot(updatedRegistration);
        else
            stateStore.UpsertDevice(updatedRegistration);

        // Publish completion only after registration and loop operations succeed.
        state.DeviceId = registeredDevice.DeviceId;
        state.LoopId = setupState.LoopId;
        state.TargetMode = setupState.TargetMode;
        state.TargetHost = setupState.TargetHost;
        state.RollbackSnapshotId = setupState.RollbackSnapshotId;
        state.BaselineEvidence = setupState.BaselineEvidence;
        state.VerifiedSerialEvidence = setupState.VerifiedSerialEvidence;
        state.Complete = true;

        var acceptedReadiness = EvaluateConversionReadiness(state, false, envelope.HostName);
        var acceptedTargetHost = ResolveOpenJiboTargetHost(state.TargetMode, state.TargetHost, envelope.HostName);
        var acceptedHostMappings = BuildRobotHostMappings(state.TargetMode, state.TargetHost, envelope.HostName);

        if (operation.Equals("ReconnectRobot", StringComparison.OrdinalIgnoreCase))
            return ProtocolDispatchResult.Ok(new
            {
                result = "ok",
                robotId,
                deviceId = state.DeviceId,
                loopId = state.LoopId,
                targetMode = state.TargetMode,
                targetHost = acceptedTargetHost,
                rollbackSnapshotId = state.RollbackSnapshotId,
                baselineEvidence = state.BaselineEvidence.ToResponse(),
                hostMappings = acceptedHostMappings,
                onboardingSession = hasPreparedToken ? BuildOnboardingSession(token!, state, envelope.HostName) : null,
                conversionReadiness = acceptedReadiness.ToResponse()
            });

        var account = stateStore.GetAccount();
        return ProtocolDispatchResult.Ok(new
        {
            accessKeyId = account.AccessKeyId,
            secretAccessKey = account.SecretAccessKey,
            serviceMode = false,
            robotId,
            deviceId = state.DeviceId,
            loopId = state.LoopId,
            targetMode = state.TargetMode,
            targetHost = acceptedTargetHost,
            rollbackSnapshotId = state.RollbackSnapshotId,
            baselineEvidence = state.BaselineEvidence.ToResponse(),
            hostMappings = acceptedHostMappings,
            onboardingSession = hasPreparedToken ? BuildOnboardingSession(token!, state, envelope.HostName) : null,
            conversionReadiness = acceptedReadiness.ToResponse()
        });
    }

    private static object[] BuildConnectionProofGuidance(IEnumerable<string> blockers)
    {
        return blockers
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(blocker => new
            {
                blocker,
                severity = ConnectionBlockerSeverity(blocker),
                ownerAction = ConnectionBlockerOwnerAction(blocker)
            })
            .ToArray<object>();
    }

    private static string ConnectionBlockerSeverity(string blocker) => blocker switch
    {
        "missing-proof-observed-at" or "stale-proof-observed-at" or
            "missing-reported-connection-host" or "missing-reported-host-mappings" or
            "incomplete-reported-host-mappings" or "reported-connection-host-mismatch" or
            "reported-host-mapping-mismatch" or "host-mapping-mismatch" => "release-gate",
        "expired-prepared-oobe-token" or "missing-prepared-oobe-token" or "prepared-device-not-registered" or
            "setup-incomplete" or "robot-inactive" => "setup-gate",
        _ => "needs-review"
    };

    private static string ConnectionBlockerOwnerAction(string blocker) => blocker switch
    {
        "expired-prepared-oobe-token" => "Prepare a fresh OOBE token, then rerun SetupRobot before verifying the connection.",
        "missing-prepared-oobe-token" => "Use a token issued by PrepareRobot; an unknown explicit token is not a prepared session.",
        "prepared-device-not-registered" => "Reconcile the exact prepared device registration before verifying; the primary robot cannot substitute for it.",
        "setup-incomplete" => "Complete SetupRobot or ReconnectRobot with the prepared token before using VerifyConnection as release proof.",
        "robot-inactive" => "Register or reactivate the robot identity before relying on stored cloud state for connection proof.",
        "host-mapping-mismatch" => "Rerun setup with the intended target mode/host so stored api.jibo.com, api-socket.jibo.com, open-jibo-socket.openjibo.com, and neohub.openjibo.com mappings match the proof target.",
        "missing-reported-connection-host" => "Have the physical robot or conversion helper report the host it actually reached with reportedConnectionHost/connectedHost/currentHost.",
        "missing-reported-host-mappings" => "Have the physical robot or conversion helper report resolved host mappings for api.jibo.com, api-socket.jibo.com, open-jibo-socket.openjibo.com, neo-hub.jibo.com, and neohub.openjibo.com.",
        "incomplete-reported-host-mappings" => "Include all required host mappings in reportedHostMappings, reportedDnsMappings, or resolvedHostMappings.",
        "reported-connection-host-mismatch" => "Check DNS/TLS retargeting: the robot-reported connected host does not match the selected Open Jibo target host.",
        "reported-host-mapping-mismatch" => "Check DNS/static host rewrites: at least one robot-reported host mapping points away from the selected Open Jibo target host.",
        "missing-proof-observed-at" => "Include connectionProofObservedAt from the live robot capture when the gate requires fresh proof.",
        "stale-proof-observed-at" => "Capture a new live robot proof; fresh proof must be recent enough for the release/video gate.",
        _ => "Review the connection proof payload and conversion readiness details."
    };

    private static List<string> BuildConnectionBlockers(
        bool hasTokenState,
        OobeTokenState? state,
        bool expired,
        DeviceRegistration? robot,
        IDictionary<string, string> expectedHostMappings,
        string expectedConnectionHost,
        string? reportedConnectionHost,
        IDictionary<string, string> reportedHostMappings,
        bool requiresReportedConnectionProof,
        bool requiresFreshConnectionProof,
        DateTimeOffset? proofObservedAt,
        ProofFreshnessPolicy proofFreshnessPolicy)
    {
        var blockers = new List<string>();
        if (expired)
            blockers.Add("expired-prepared-oobe-token");
        if (hasTokenState && state?.Complete != true)
            blockers.Add("setup-incomplete");
        if (robot is null)
            blockers.Add("prepared-device-not-registered");
        else if (!robot.IsActive)
            blockers.Add("robot-inactive");
        if (robot is not null && !HostMappingsMatch(robot.HostMappings, expectedHostMappings))
            blockers.Add("host-mapping-mismatch");
        if (requiresReportedConnectionProof && string.IsNullOrWhiteSpace(reportedConnectionHost))
            blockers.Add("missing-reported-connection-host");
        if (requiresReportedConnectionProof && reportedHostMappings.Count == 0)
            blockers.Add("missing-reported-host-mappings");
        else if (requiresReportedConnectionProof && !RequiredHostMappingsPresent(expectedHostMappings, reportedHostMappings))
            blockers.Add("incomplete-reported-host-mappings");
        if (!string.IsNullOrWhiteSpace(reportedConnectionHost) &&
            !string.Equals(reportedConnectionHost, expectedConnectionHost, StringComparison.OrdinalIgnoreCase))
            blockers.Add("reported-connection-host-mismatch");
        if (reportedHostMappings.Count > 0 && !HostMappingsMatch(reportedHostMappings, expectedHostMappings))
            blockers.Add("reported-host-mapping-mismatch");
        if (requiresFreshConnectionProof && proofObservedAt is null)
            blockers.Add("missing-proof-observed-at");
        else if (requiresFreshConnectionProof && !IsFreshProofObservation(proofObservedAt, proofFreshnessPolicy))
            blockers.Add("stale-proof-observed-at");

        return blockers;
    }

    private static ProofFreshnessPolicy ReadProofFreshnessPolicy(JsonElement? body)
    {
        const long defaultMaxAgeSeconds = 15 * 60;
        const long minMaxAgeSeconds = 60;
        const long maxMaxAgeSeconds = 60 * 60;
        const long acceptedFutureClockSkewSeconds = 2 * 60;

        var requestedMaxAgeSeconds = ReadLong(body, "connectionProofMaxAgeSeconds") ??
                                     ReadLong(body, "proofMaxAgeSeconds") ??
                                     ReadLong(body, "freshnessMaxAgeSeconds");
        var maxAgeSeconds = requestedMaxAgeSeconds is null
            ? defaultMaxAgeSeconds
            : Math.Clamp(requestedMaxAgeSeconds.Value, minMaxAgeSeconds, maxMaxAgeSeconds);

        return new ProofFreshnessPolicy(maxAgeSeconds, acceptedFutureClockSkewSeconds);
    }

    private static DateTimeOffset? ReadProofObservedAt(JsonElement? body)
    {
        var value = ReadString(body, "connectionProofObservedAt") ??
                    ReadString(body, "proofObservedAt") ??
                    ReadString(body, "observedAt");
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
            return epoch > 9_999_999_999
                ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                : DateTimeOffset.FromUnixTimeSeconds(epoch);

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    private static bool IsFreshProofObservation(DateTimeOffset? observedAt, ProofFreshnessPolicy policy) =>
        observedAt is not null && observedAt.Value >= DateTimeOffset.UtcNow.AddSeconds(-policy.MaxAgeSeconds) &&
        observedAt.Value <= DateTimeOffset.UtcNow.AddSeconds(policy.AcceptedFutureClockSkewSeconds);

    private sealed record ProofFreshnessPolicy(long MaxAgeSeconds, long AcceptedFutureClockSkewSeconds);


    private static Dictionary<string, string> ReadReportedHostMappings(JsonElement? body)
    {
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (body is null)
            return mappings;

        var source = ReadObject(body, "reportedHostMappings") ??
                     ReadObject(body, "reportedDnsMappings") ??
                     ReadObject(body, "resolvedHostMappings");
        if (source is null)
            return mappings;

        foreach (var (host, target) in source)
        {
            if (target is null)
                continue;

            var normalized = NormalizeHostName(Convert.ToString(target, CultureInfo.InvariantCulture) ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(normalized))
                mappings[host] = normalized;
        }

        return mappings;
    }

    private static string? ReadReportedConnectionHost(JsonElement? body)
    {
        var reportedHost = ReadString(body, "reportedConnectionHost") ??
                           ReadString(body, "connectedHost") ??
                           ReadString(body, "resolvedHost") ??
                           ReadString(body, "currentHost");
        if (!string.IsNullOrWhiteSpace(reportedHost))
            return NormalizeHostName(reportedHost);

        return null;
    }

    private static string NormalizeHostName(string hostName)
    {
        var trimmed = hostName.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            return uri.Host;

        var slashIndex = trimmed.IndexOf('/', StringComparison.Ordinal);
        if (slashIndex >= 0)
            trimmed = trimmed[..slashIndex];

        var portIndex = trimmed.LastIndexOf(':');
        if (portIndex > 0 && trimmed.IndexOf(':') == portIndex)
            trimmed = trimmed[..portIndex];

        return trimmed;
    }

    private static bool HostMappingsMatch(
        IDictionary<string, string> actual,
        IDictionary<string, string> expected)
    {
        foreach (var (host, expectedTarget) in expected)
        {
            if (!actual.TryGetValue(host, out var actualTarget) ||
                !string.Equals(actualTarget, expectedTarget, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static bool RequiredHostMappingsPresent(
        IDictionary<string, string> expected,
        IDictionary<string, string> actual) =>
        expected.Keys.All(actual.ContainsKey);

    private static object BuildReportedHostMappingCompleteness(
        IDictionary<string, string> expected,
        IDictionary<string, string> reported)
    {
        var missing = expected.Keys
            .Where(host => !reported.ContainsKey(host))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new
        {
            complete = missing.Length == 0,
            requiredHosts = expected.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            missingHosts = missing
        };
    }


    private object BuildOnboardingSessionPreview(OobeTokenState state, string hostName)
    {
        var previewToken = $"preview-{CreateOobeToken()}";
        return BuildOnboardingSession(previewToken, state, hostName);
    }

    private object BuildOnboardingSession(string token, OobeTokenState state, string hostName)
    {
        state.OnboardingNonce ??= CreateOobeToken();
        state.OnboardingState ??= $"openjibo-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

        var targetHost = ResolveOpenJiboTargetHost(state.TargetMode, state.TargetHost, hostName);
        var expires = state.ExpiresUtc.ToUnixTimeMilliseconds();
        var payload = string.Join("|",
            token,
            state.OnboardingNonce,
            state.OnboardingState,
            state.DeviceId ?? string.Empty,
            state.LoopId ?? string.Empty,
            state.TargetMode,
            targetHost,
            state.RollbackSnapshotId ?? string.Empty,
            expires.ToString(CultureInfo.InvariantCulture));

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(stateStore.GetAccount().SecretAccessKey));
        var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        return new
        {
            token,
            nonce = state.OnboardingNonce,
            state = state.OnboardingState,
            deviceId = state.DeviceId,
            loopId = state.LoopId,
            targetMode = state.TargetMode,
            targetHost,
            rollbackSnapshotId = state.RollbackSnapshotId,
            expires,
            signatureAlgorithm = "HMAC-SHA256",
            signaturePayload = payload,
            signature
        };
    }

    private static OobeTokenState BuildTentativeSetupState(
        OobeTokenState current,
        JsonElement? body,
        ProtocolEnvelope envelope,
        string robotId)
    {
        return new OobeTokenState
        {
            DeviceId = robotId,
            LoopId = ReadString(body, "loopId") ?? current.LoopId,
            TargetMode =
                ResolveOpenJiboTargetMode(ReadString(body, "targetMode") ??
                                          ReadString(body, "mode") ?? current.TargetMode),
            TargetHost = ReadTargetHost(body) ?? current.TargetHost,
            RollbackSnapshotId = ReadString(body, "rollbackSnapshotId") ??
                                 ReadString(body, "rollbackSnapshot") ?? current.RollbackSnapshotId,
            BaselineEvidence = current.BaselineEvidence.Merge(ReadBaselineEvidence(body, envelope)),
            VerifiedSerialEvidence = ReadVerifiedSerialEvidence(body) ?? current.VerifiedSerialEvidence,
            Complete = current.Complete,
            ExpiresUtc = current.ExpiresUtc
        };
    }

    private static string ResolveOpenJiboTargetHost(string targetMode, string? targetHost, string hostName)
    {
        if (!string.IsNullOrWhiteSpace(targetHost))
            return targetHost.Trim();

        if (targetMode.Equals("open-jibo-ai", StringComparison.OrdinalIgnoreCase))
            return "api.openjibo.ai";

        if (targetMode.Equals("open-jibo-developer", StringComparison.OrdinalIgnoreCase) ||
            targetMode.Equals("open-jibo-self-hosted", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(hostName) ? string.Empty : hostName.Trim();

        return "api.openjibo.com";
    }

    private static string ResolveOpenJiboTargetMode(string? mode)
    {
        return string.IsNullOrWhiteSpace(mode) ? "open-jibo" : mode.Trim().ToLowerInvariant();
    }

    private static object BuildConversionReadiness(OobeTokenState? state, bool expired, string hostName)
    {
        return EvaluateConversionReadiness(state, expired, hostName).ToResponse();
    }

    private static ConversionReadiness EvaluateConversionReadiness(OobeTokenState? state, bool expired, string hostName)
    {
        var blockers = new List<string>();
        if (state is null)
            blockers.Add("missing-prepared-oobe-token");
        else if (expired)
            blockers.Add("expired-prepared-oobe-token");
        if (state is not null && string.IsNullOrWhiteSpace(state.RollbackSnapshotId))
            blockers.Add("missing-rollback-snapshot");
        if (state is not null && !SupportedOpenJiboTargetModes.Contains(state.TargetMode))
            blockers.Add("unsupported-target-mode");
        if (state is not null &&
            state.TargetMode.Equals("open-jibo-self-hosted", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(state.TargetHost))
            blockers.Add("missing-self-hosted-target-host");
        if (state?.BaselineEvidence.RequireBaselineAudit == true && !state.BaselineEvidence.HasMinimumBaseline)
            blockers.Add("missing-baseline-audit");
        if (string.IsNullOrWhiteSpace(ResolveOpenJiboTargetHost(state?.TargetMode ?? "open-jibo", state?.TargetHost,
                hostName)))
            blockers.Add("missing-target-host");

        return new ConversionReadiness(blockers);
    }

    private static Dictionary<string, string> BuildRobotHostMappings(string targetMode, string? targetHost,
        string hostName)
    {
        var resolvedHost = ResolveOpenJiboTargetHost(targetMode, targetHost, hostName);

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["api.jibo.com"] = resolvedHost,
            ["api-socket.jibo.com"] = resolvedHost,
            ["open-jibo-socket.openjibo.com"] = resolvedHost,
            ["neo-hub.jibo.com"] = resolvedHost,
            ["neohub.openjibo.com"] = resolvedHost
        };
    }

    private ProtocolDispatchResult HandleLoop(string operation, ProtocolEnvelope envelope)
    {
        if (operation is "ListMembers" or "ListLoopMembers")
        {
            var listBody = envelope.TryParseBody();
            var loopId = ReadString(listBody, "loopId") ??
                         ReadString(listBody, "id") ??
                         stateStore.GetLoops().FirstOrDefault()?.LoopId;

            var members = stateStore.GetLoopMembers(loopId ?? string.Empty)
                .Where(static member => !string.Equals(member.Type, "robot", StringComparison.OrdinalIgnoreCase))
                .Select(MapLoopMember)
                .ToArray();

            return ProtocolDispatchResult.Ok(members);
        }

        if (operation is "ListRecognitionObservations" or "ListRecognitions")
        {
            var listBody = envelope.TryParseBody();
            var loopId = ReadString(listBody, "loopId") ??
                         ReadString(listBody, "id") ??
                         stateStore.GetLoops().FirstOrDefault()?.LoopId;

            var observations = stateStore.GetRecognitionObservations(loopId ?? string.Empty)
                .Select(MapRecognitionObservation)
                .ToArray();

            return ProtocolDispatchResult.Ok(observations);
        }

        var body = envelope.TryParseBody();
        var loopIdForMutation = ReadString(body, "loopId") ??
                                ReadString(body, "id") ??
                                stateStore.GetLoops().FirstOrDefault()?.LoopId ??
                                "openjibo-default-loop";

        switch (operation)
        {
            case "AddLoop" or "CreateLoop":
            {
                var loop = stateStore.AddLoop(
                    ReadString(body, "name") ?? ReadString(body, "loopName"),
                    ReadString(body, "ownerAccountId") ?? stateStore.GetAccount().AccountId,
                    ReadString(body, "robotId") ?? ReadString(body, "deviceId"),
                    ReadString(body, "robotFriendlyId") ?? ReadString(body, "friendlyId") ?? ReadString(body, "deviceId"));
                TryPushLoopUpdatedForLoop(loop.LoopId);
                return ProtocolDispatchResult.Ok(MapLoopRecord(loop, stateStore.GetLoopMembers(loop.LoopId)));
            }
            case "InviteMember" or "InviteLoopMember":
            {
                stateStore.AddLoopMember(
                    loopIdForMutation,
                    null,
                    ReadString(body, "email"),
                    ReadString(body, "firstName"),
                    ReadString(body, "lastName"),
                    ReadString(body, "gender"),
                    ReadLong(body, "birthday"),
                    ReadBool(body, "isChild"),
                    "member",
                    ReadString(body, "legalGuardianId"));

                var loop = stateStore.GetLoops().FirstOrDefault(l =>
                    l.LoopId.Equals(loopIdForMutation, StringComparison.OrdinalIgnoreCase));
                TryPushLoopUpdatedForLoop(loopIdForMutation);
                return ProtocolDispatchResult.Ok(loop is null
                    ? new { result = "ok" }
                    : MapLoopRecord(loop, stateStore.GetLoopMembers(loopIdForMutation)));
            }
            case "UpdateMember" or "UpdateLoopMember":
            {
                var memberId = ReadString(body, "id") ?? string.Empty;
                try
                {
                    stateStore.UpdateLoopMember(loopIdForMutation, memberId,
                        ReadString(body, "firstName"), ReadString(body, "lastName"),
                        ReadString(body, "gender"), ReadLong(body, "birthday"),
                        ReadBool(body, "isChild"), ReadString(body, "nickname"), ReadString(body, "phoneticName"));
                }
                catch (InvalidOperationException)
                {
                    // Member not found - keep protocol flow moving.
                }

                var loop = stateStore.GetLoops().FirstOrDefault(l =>
                    l.LoopId.Equals(loopIdForMutation, StringComparison.OrdinalIgnoreCase));
                TryPushLoopUpdatedForLoop(loopIdForMutation);
                return ProtocolDispatchResult.Ok(loop is null
                    ? new { result = "ok" }
                    : MapLoopRecord(loop, stateStore.GetLoopMembers(loopIdForMutation)));
            }
            case "RemoveMember" or "RemoveLoopMember":
            {
                stateStore.RemoveLoopMember(loopIdForMutation, ReadString(body, "id") ?? string.Empty);
                var loop = stateStore.GetLoops().FirstOrDefault(l =>
                    l.LoopId.Equals(loopIdForMutation, StringComparison.OrdinalIgnoreCase));
                TryPushLoopUpdatedForLoop(loopIdForMutation);
                return ProtocolDispatchResult.Ok(loop is null
                    ? new { result = "ok" }
                    : MapLoopRecord(loop, stateStore.GetLoopMembers(loopIdForMutation)));
            }
            case "AcceptInvitation" or "AcceptLoopInvitation" or
                "DeclineInvitation" or "DeclineLoopInvitation":
            {
                var loop = stateStore.GetLoops().FirstOrDefault(l =>
                    l.LoopId.Equals(loopIdForMutation, StringComparison.OrdinalIgnoreCase));
                TryPushLoopUpdatedForLoop(loopIdForMutation);
                return ProtocolDispatchResult.Ok(loop is null
                    ? new { result = "ok" }
                    : MapLoopRecord(loop, stateStore.GetLoopMembers(loopIdForMutation)));
            }
            case "SetEnrollment":
            {
                var memberId = ReadString(body, "id") ?? string.Empty;
                bool? face = body?.TryGetProperty("face", out var faceEl) == true ? faceEl.GetBoolean() : null;
                bool? voice = body?.TryGetProperty("voice", out var voiceEl) == true ? voiceEl.GetBoolean() : null;
                try
                {
                    stateStore.SetMemberEnrollment(loopIdForMutation, memberId, face, voice);
                }
                catch (InvalidOperationException)
                {
                    // Member not found - keep protocol flow moving.
                }

                var loop = stateStore.GetLoops().FirstOrDefault(l =>
                    l.LoopId.Equals(loopIdForMutation, StringComparison.OrdinalIgnoreCase));
                TryPushLoopUpdatedForLoop(loopIdForMutation);
                return ProtocolDispatchResult.Ok(loop is null
                    ? new { result = "ok" }
                    : MapLoopRecord(loop, stateStore.GetLoopMembers(loopIdForMutation)));
            }
            case "RecordRecognitionObservation" or "RecordRecognition":
            {
                var memberId = ReadString(body, "memberId") ?? ReadString(body, "id") ?? string.Empty;
                try
                {
                    var observation = stateStore.RecordRecognitionObservation(
                        loopIdForMutation,
                        memberId,
                        ReadString(body, "modality") ?? "face",
                        ReadString(body, "outcome") ?? "recognized",
                        ReadDouble(body, "confidence"),
                        ReadString(body, "source") ?? "loop-protocol");

                    return ProtocolDispatchResult.Ok(new
                    {
                        result = "ok",
                        observation = MapRecognitionObservation(observation)
                    });
                }
                catch (InvalidOperationException)
                {
                    // Member not found - keep protocol flow moving while surfacing a soft failure
                    // that conversion smoke clients can display without breaking stock callers.
                    return ProtocolDispatchResult.Ok(new { result = "member-not-found" });
                }
            }
            case "UpdateNickname" or "UpdatePhoneticName":
            {
                var memberId = ReadString(body, "id") ?? string.Empty;
                var nickname = operation is "UpdateNickname" ? ReadString(body, "nickname") : null;
                var phoneticName = operation is "UpdatePhoneticName" ? ReadString(body, "phoneticName") : null;
                try
                {
                    stateStore.UpdateLoopMember(loopIdForMutation, memberId,
                        null, null, null, null, false, nickname, phoneticName);
                }
                catch (InvalidOperationException)
                {
                    // Member not found - keep protocol flow moving.
                }

                TryPushLoopUpdatedForLoop(loopIdForMutation);
                return ProtocolDispatchResult.Ok(new { result = "ok" });
            }
            case "SuspendLoop" or "Remove" or "RemoveLoop" or
                "SetLegalGuardian" or "UpdateAgreementStatus" or "Update" or "UpdateLoop":
                return ProtocolDispatchResult.Ok(new { result = "ok" });
        }

        if (operation is not ("List" or "ListLoops")) return ProtocolDispatchResult.Ok(Array.Empty<object>());

        // Stock Neo Hub requires exactly one loop. A partially onboarded robot
        // may arrive with an empty persisted loop collection, so materialize a
        // loop from the strongest currently resolved request identity before
        // returning the list. This creates protocol state, not an automatic
        // inventory/household identity claim.
        var loops = stateStore.GetLoops();
        var identity = _identityResolver.Resolve(envelope);
        var requestedRobotId = identity.DeviceId ?? envelope.DeviceId;
        if (!string.IsNullOrWhiteSpace(requestedRobotId) && loops.Count > 1)
        {
            var identityCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { requestedRobotId };
            foreach (var session in stateStore.GetSessions())
            {
                var registeredDeviceId = session.Metadata.TryGetValue("registeredDeviceId", out var registered)
                    ? registered?.ToString()
                    : null;
                if (!string.Equals(registeredDeviceId, requestedRobotId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.IsNullOrWhiteSpace(session.DeviceId)) identityCandidates.Add(session.DeviceId);
                if (session.Metadata.TryGetValue("loopId", out var sessionLoopId) &&
                    !string.IsNullOrWhiteSpace(sessionLoopId?.ToString()))
                    identityCandidates.Add(sessionLoopId.ToString()!);
            }
            var matchingLoops = loops.Where(loop =>
                    identityCandidates.Contains(loop.LoopId) ||
                    identityCandidates.Contains(loop.RobotId) ||
                    identityCandidates.Contains(loop.RobotFriendlyId))
                .ToArray();
            if (matchingLoops.Length > 0)
                loops = [matchingLoops[0]];
            else
            {
                var loop = stateStore.AddLoop(null, stateStore.GetAccount().AccountId,
                    requestedRobotId, requestedRobotId);
                loops = [loop];
            }
        }
        if (loops.Count > 1)
        {
            // Neo Hub's acquireLoopId contract is one loop per account/session.
            // Do not leak other robots' household loops to this robot.
            var defaultLoop = loops.FirstOrDefault(loop =>
                loop.OwnerAccountId.Equals(stateStore.GetAccount().AccountId, StringComparison.OrdinalIgnoreCase) &&
                loop.RobotId.Equals(stateStore.GetRobot().RobotId, StringComparison.OrdinalIgnoreCase));
            loops = [defaultLoop ?? loops[0]];
        }
        if (loops.Count == 0)
        {
            var robotId = identity.DeviceId ?? envelope.DeviceId ?? _configuredRobotId ?? stateStore.GetRobot().DeviceId;
            var loop = stateStore.AddLoop(null, stateStore.GetAccount().AccountId, robotId, robotId);
            loops = [loop];
        }

        return ProtocolDispatchResult.Ok(loops
            .Select(loop => MapLoopRecord(loop, stateStore.GetLoopMembers(loop.LoopId)))
            .ToArray());
    }

    public static object MapLoopMember(LoopMemberRecord member)
    {
        return new
        {
            id = member.Id,
            loopId = member.LoopId,
            accountId = member.AccountId,
            account = new
            {
                email = member.Email,
                firstName = member.FirstName,
                lastName = member.LastName,
                gender = member.Gender,
                birthday = member.Birthday,
                isChild = member.IsChild,
                phoneNumber = member.PhoneNumber
            },
            enrolled = new { face = member.FaceEnrolled, voice = member.VoiceEnrolled },
            status = member.Status,
            type = member.Type,
            nickname = member.Nickname,
            phoneticName = member.PhoneticName,
            legalGuardianId = member.LegalGuardianId,
            agreementId = member.AgreementId,
            created = member.CreatedUtc.ToUnixTimeMilliseconds()
        };
    }

    private static object MapRecognitionObservation(RecognitionObservationRecord observation)
    {
        return new
        {
            id = observation.ObservationId,
            loopId = observation.LoopId,
            memberId = observation.MemberId,
            robotId = observation.RobotId,
            modality = observation.Modality,
            outcome = observation.Outcome,
            confidence = observation.Confidence,
            source = observation.Source,
            observed = observation.ObservedUtc.ToUnixTimeMilliseconds()
        };
    }

    public static object MapLoopRecord(LoopRecord loop, IEnumerable<LoopMemberRecord> members)
    {
        return new
        {
            id = loop.LoopId,
            name = loop.Name,
            owner = loop.OwnerAccountId,
            robot = loop.RobotId,
            robotFriendlyId = loop.RobotFriendlyId,
            members = members
                .Where(static m => !string.Equals(m.Type, "robot", StringComparison.OrdinalIgnoreCase))
                .Select(MapLoopMember)
                .ToArray(),
            isSuspended = loop.IsSuspended,
            created = loop.CreatedUtc.ToUnixTimeMilliseconds(),
            updated = loop.UpdatedUtc.ToUnixTimeMilliseconds(),
            eventKey = "LoopUpdated"
        };
    }

    public static object BuildLoopNotificationPayload(
        LoopRecord loop,
        IEnumerable<LoopMemberRecord> members)
    {
        return MapLoopRecord(loop, members);
    }

    private void TryPushLoopUpdatedForLoop(string loopId)
    {
        if (_loopUpdatedPushService is not null)
        {
            _ = _loopUpdatedPushService.PushForLoopIdAsync(loopId, cancellationToken: CancellationToken.None);
            return;
        }

        if (_robotNotificationRegistry is null) return;
        var loop = stateStore.GetLoops()
            .FirstOrDefault(item => item.LoopId.Equals(loopId, StringComparison.OrdinalIgnoreCase));
        if (loop is null) return;
        var payload = BuildLoopNotificationPayload(loop, stateStore.GetLoopMembers(loopId));
        var keys = BuildLoopRobotKeys(loop);
        if (keys.Count == 0) return;
        _ = _robotNotificationRegistry.PushLoopUpdatedAsync(keys, payload, CancellationToken.None);
    }

    private HashSet<string> BuildLoopRobotKeys(LoopRecord loop)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                keys.Add(value.Trim());
        }

        Add(loop.RobotId);
        Add(loop.RobotFriendlyId);
        foreach (var seed in keys.ToArray())
        {
            var device = stateStore.FindDeviceByFriendlyId(seed);
            if (device is null) continue;
            Add(device.DeviceId);
            Add(device.RobotId);
            Add(device.FriendlyName);
        }

        return keys;
    }

    private ProtocolDispatchResult HandleLog(string operation, ProtocolEnvelope envelope, ProtocolRobotIdentity identity)
    {
        var requestContent = ReadBodyBytes(envelope);
        if (requestContent.Length > 0)
            StoreLogContent($"{GetLogCategory(operation)}-request", CreateLogUploadId(),
                ReadHeader(envelope, "Content-Type") ?? "application/octet-stream",
                requestContent, envelope, identity);

        var uploadId = CreateLogUploadId();
        return operation switch
        {
            "PutEventsAsync" => ProtocolDispatchResult.Ok(new
            {
                contentEncoding = "gzip",
                uploadUrl = BuildLogUploadUrl(envelope, "log-events", uploadId)
            }),
            "PutEvents" => ProtocolDispatchResult.Ok(new { }),
            "PutBinaryAsync" => ProtocolDispatchResult.Ok(new
            {
                url = $"{ResolveApiBaseUrl(envelope)}/log/binary/{uploadId}",
                uploadUrl = BuildLogUploadUrl(envelope, "log-binary", uploadId)
            }),
            "PutAsrBinary" => ProtocolDispatchResult.Ok(new
            {
                bucketName = "openjibo-media",
                key = $"logs/asr/{uploadId}",
                uploadUrl = BuildLogUploadUrl(envelope, "asr-binary", uploadId)
            }),
            "NewKinesisCredentials" => ProtocolDispatchResult.Ok(new { }),
            _ => ProtocolDispatchResult.Ok(new { })
        };
    }

    private static VerifiedSerialEvidence? ReadVerifiedSerialEvidence(JsonElement? body)
    {
        if (!ReadBool(body, "serialVerified")) return null;

        var serialNumber = ReadString(body, "serialNumber");
        var method = ReadString(body, "serialVerificationMethod");
        if (string.IsNullOrWhiteSpace(serialNumber) || string.IsNullOrWhiteSpace(method) ||
            !System.Text.RegularExpressions.Regex.IsMatch(serialNumber.Trim(), "^BOJW-(?:[0-9]{4}-){3}[0-9]{4}$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            return null;

        return new VerifiedSerialEvidence(serialNumber.Trim(), $"oobe-verified:{method.Trim()}", DateTimeOffset.UtcNow);
    }

    private ProtocolDispatchResult HandleLogUpload(ProtocolEnvelope envelope, ProtocolRobotIdentity identity)
    {
        var category = envelope.Path.Contains("asr-binary", StringComparison.OrdinalIgnoreCase)
            ? "asr"
            : envelope.Path.Contains("log-events", StringComparison.OrdinalIgnoreCase)
                ? "events"
                : "binary";
        var uploadId = GetLogUploadId(envelope.Path);
        var contentType = ReadHeader(envelope, "Content-Type") ?? "application/octet-stream";
        var content = ReadBodyBytes(envelope);
        StoreLogContent(category, uploadId, contentType, content, envelope, identity);
        return ProtocolDispatchResult.Raw(200, string.Empty);
    }

    private void StoreLogContent(string category, string uploadId, string contentType, byte[] content,
        ProtocolEnvelope envelope, ProtocolRobotIdentity identity)
    {
        var logIdentity = RobotLogIdentityEvidence.Extract(content);
        var devices = stateStore.GetDevicesForAdministration();
        var existingDevice = string.IsNullOrWhiteSpace(identity.DeviceId) ? null : devices.FirstOrDefault(device =>
            device.DeviceId.Equals(identity.DeviceId, StringComparison.OrdinalIgnoreCase));
        var matchedDevice = existingDevice is null && !identity.Source.Equals("conflict", StringComparison.OrdinalIgnoreCase)
            ? logIdentity.ResolveDevice(devices) : null;
        var serialConflictsWithExisting = existingDevice is not null && logIdentity.HasSerialEvidence &&
            !string.IsNullOrWhiteSpace(existingDevice.VerifiedSerialNumber) &&
            !string.Equals(existingDevice.VerifiedSerialNumber, logIdentity.SerialNumber, StringComparison.OrdinalIgnoreCase);
        var attributedDevice = existingDevice ?? matchedDevice;
        if (attributedDevice is not null && identitySuggestionStore is not null && !logIdentity.HasConflictingEvidence &&
            !serialConflictsWithExisting && RobotIdentitySuggestionStore.IsSafeIdentityName(logIdentity.RobotName))
        {
            identitySuggestionStore.Observe(attributedDevice.DeviceId, logIdentity.RobotName,
                "log-header", "name");
        }
        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["artifactType"] = "robot-log",
            ["category"] = category,
            ["uploadId"] = uploadId,
            ["deviceId"] = matchedDevice?.DeviceId ?? identity.DeviceId,
            ["identitySource"] = matchedDevice is null ? identity.Source :
                !string.IsNullOrWhiteSpace(logIdentity.SerialNumber) &&
                string.Equals(matchedDevice.VerifiedSerialNumber, logIdentity.SerialNumber, StringComparison.OrdinalIgnoreCase)
                    ? "log-header-verified-serial" : "log-header-name",
            ["observedSerialNumber"] = logIdentity.SerialNumber,
            ["observedRobotName"] = logIdentity.RobotName,
            ["identityEvidenceConflict"] = logIdentity.HasConflictingEvidence || serialConflictsWithExisting ||
                identity.Source.Equals("conflict", StringComparison.OrdinalIgnoreCase),
            ["authScheme"] = identity.Aws.AuthScheme,
            ["awsSigV4"] = identity.Aws.IsSigV4,
            ["awsSigV3"] = identity.Aws.IsSigV3,
            ["awsAccessKeyFingerprint"] = identity.Aws.AccessKeyFingerprint,
            ["awsSignedHeadersPresent"] = identity.Aws.SignedHeadersPresent,
            ["awsSignsRobotHeader"] = identity.Aws.SignsRobotHeader,
            ["awsSignsTransactionHeader"] = identity.Aws.SignsTransactionHeader,
            ["requestId"] = envelope.RequestId,
            ["correlationId"] = envelope.CorrelationId,
            ["firmwareVersion"] = envelope.FirmwareVersion,
            ["applicationVersion"] = envelope.ApplicationVersion
        };
        _mediaContentStore.StoreAsync($"logs/{category}/{uploadId}", contentType, content, metadata,
            CancellationToken.None).GetAwaiter().GetResult();
    }

    private ProtocolRobotIdentity ResolveRobotIdentity(ProtocolEnvelope envelope, string operation)
    {
        var identity = _identityResolver.Resolve(envelope);
        if (_protocolAuthDiagnosticsEnabled)
            _logger.LogInformation(
                "Protocol identity diagnostic requestId={RequestId} traceId={TraceId} operation={Operation} host={Host} " +
                "path={Path} robotHeaderPresent={RobotHeaderPresent} bearerTokenPresent={BearerTokenPresent} " +
                "bearerTokenResolved={BearerTokenResolved} identitySource={IdentitySource} resolvedDeviceId={ResolvedDeviceId} " +
                "authScheme={AuthScheme} awsSigV4={AwsSigV4} awsSigV3={AwsSigV3} awsAccessKeyFingerprint={AwsAccessKeyFingerprint} " +
                "awsSecurityTokenPresent={AwsSecurityTokenPresent} awsDatePresent={AwsDatePresent} awsSignaturePresent={AwsSignaturePresent} " +
                "awsSignedHeadersPresent={AwsSignedHeadersPresent} awsSignsRobotHeader={AwsSignsRobotHeader} " +
                "awsSignsTransactionHeader={AwsSignsTransactionHeader} bodyBytes={BodyBytes}",
                envelope.RequestId, envelope.CorrelationId, operation, envelope.HostName, envelope.Path,
                identity.HeaderPresent, identity.BearerTokenPresent, identity.BearerTokenResolved, identity.Source,
                identity.DeviceId, identity.Aws.AuthScheme, identity.Aws.IsSigV4, identity.Aws.IsSigV3,
                identity.Aws.AccessKeyFingerprint,
                identity.Aws.SecurityTokenPresent, identity.Aws.DatePresent, identity.Aws.SignaturePresent,
                identity.Aws.SignedHeadersPresent, identity.Aws.SignsRobotHeader, identity.Aws.SignsTransactionHeader,
                ReadBodyBytes(envelope).Length);
        return identity;
    }

    private string BuildLogUploadUrl(ProtocolEnvelope envelope, string endpoint, string uploadId) =>
        $"{ResolveApiBaseUrl(envelope)}/upload/{endpoint}/{uploadId}";

    private string ResolveApiBaseUrl(ProtocolEnvelope envelope) =>
        _canonicalApiBaseUrl ?? $"https://{envelope.HostName}";

    private static string GetLogCategory(string operation) => operation switch
    {
        "PutEvents" or "PutEventsAsync" => "events",
        "PutAsrBinary" => "asr",
        "PutBinaryAsync" => "binary",
        _ => "requests"
    };

    private static string CreateLogUploadId() => $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";

    private static string GetLogUploadId(string path)
    {
        var candidate = path.TrimEnd('/').Split('/').LastOrDefault();
        return string.IsNullOrWhiteSpace(candidate) ||
               candidate is "asr-binary" or "log-events" or "log-binary" ||
               candidate.Contains(".", StringComparison.Ordinal)
            ? CreateLogUploadId()
            : candidate;
    }

    private ProtocolDispatchResult HandleMedia(string operation, ProtocolEnvelope envelope, ProtocolRobotIdentity identity)
    {
        var body = envelope.TryParseBody();

        if (operation.Equals("List", StringComparison.OrdinalIgnoreCase))
            return ProtocolDispatchResult.Ok(stateStore.ListMedia(
                ReadStringArray(body, "loopIds"),
                ReadLong(body, "after"),
                ReadLong(body, "before")).Select(MapMedia).ToArray());

        if (operation.Equals("Get", StringComparison.OrdinalIgnoreCase))
            return ProtocolDispatchResult.Ok(stateStore.GetMedia(ReadStringArray(body, "paths")).Select(MapMedia)
                .ToArray());

        if (operation.Equals("Remove", StringComparison.OrdinalIgnoreCase))
            return ProtocolDispatchResult.Ok(stateStore.RemoveMedia(ReadStringArray(body, "paths")).Select(MapMedia)
                .ToArray());

        if (!operation.Equals("Create", StringComparison.OrdinalIgnoreCase))
            return ProtocolDispatchResult.Ok(Array.Empty<object>());

        var loopId = ReadHeader(envelope, "x-loop-id") ?? ReadString(body, "loopId") ?? stateStore.GetLoops()[0].LoopId;
        var path = ReadHeader(envelope, "x-path") ??
                   ReadString(body, "path") ?? $"/media/{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        var type = ReadHeader(envelope, "x-type") ?? ReadString(body, "type") ?? "unknown";
        var reference = ReadHeader(envelope, "x-reference") ?? ReadString(body, "reference") ?? string.Empty;
        var isEncrypted = ReadBooleanHeader(envelope, "x-encrypted") || ReadBool(body, "isEncrypted");
        var meta = ReadObject(body, "meta") ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        meta["deviceId"] = identity.DeviceId;
        meta["identitySource"] = identity.Source;
        meta["authScheme"] = identity.Aws.AuthScheme;
        meta["awsSigV4"] = identity.Aws.IsSigV4;
        meta["awsSigV3"] = identity.Aws.IsSigV3;
        meta["awsAccessKeyFingerprint"] = identity.Aws.AccessKeyFingerprint;
        meta["awsSignedHeadersPresent"] = identity.Aws.SignedHeadersPresent;
        meta["awsSignsRobotHeader"] = identity.Aws.SignsRobotHeader;
        meta["awsSignsTransactionHeader"] = identity.Aws.SignsTransactionHeader;
        var contentType = ReadHeader(envelope, "Content-Type") ?? "application/octet-stream";
        meta["contentType"] = contentType;
        var bodyBytes = ReadBodyBytes(envelope);
        meta["contentLength"] = bodyBytes.Length;
        meta["contentSha256"] = Convert.ToHexString(SHA256.HashData(bodyBytes)).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(envelope.BodyText)) meta["bodyText"] = envelope.BodyText;

        _mediaContentStore.StoreAsync(path, contentType,
            bodyBytes,
            meta as IReadOnlyDictionary<string, object?>, CancellationToken.None).GetAwaiter().GetResult();

        return ProtocolDispatchResult.Ok(
            MapMedia(stateStore.CreateMedia(loopId, path, type, reference, isEncrypted, meta)));
    }

    private static byte[] ReadBodyBytes(ProtocolEnvelope envelope) =>
        envelope.BodyBytes is { Length: > 0 } bodyBytes ? bodyBytes : Encoding.UTF8.GetBytes(envelope.BodyText);

    private ProtocolDispatchResult HandlePerson(string operation, ProtocolEnvelope envelope)
    {
        var body = envelope.TryParseBody();

        if (operation.Equals("ListHolidays", StringComparison.OrdinalIgnoreCase))
        {
            var loopId = ReadString(body, "loopId");
            return ProtocolDispatchResult.Ok(stateStore.GetHolidays(loopId).Select(MapHoliday));
        }

        if (operation.Equals("ListCommute", StringComparison.OrdinalIgnoreCase))
        {
            var loopId = ReadString(body, "loopId");
            return ProtocolDispatchResult.Ok(stateStore.GetCommuteProfiles(loopId).Select(MapCommute));
        }

        if (!operation.Equals("UpsertCommute", StringComparison.OrdinalIgnoreCase))
            return ProtocolDispatchResult.Ok(Array.Empty<object>());

        var hasIsEnabled = body is { } enabledBody && enabledBody.TryGetProperty("isEnabled", out _);
        var hasIsComplete = body is { } completeBody && completeBody.TryGetProperty("isComplete", out _);
        var workHour = ReadLong(body, "workHour");
        var workMinute = ReadLong(body, "workMinute");
        var typicalDurationMinutes = ReadLong(body, "typicalDurationMinutes");
        var commute = new CommuteProfileRecord
        {
            Id = ReadString(body, "id") ?? string.Empty,
            LoopId = ReadString(body, "loopId") ?? string.Empty,
            MemberId = ReadString(body, "memberId"),
            IsEnabled = !hasIsEnabled || ReadBool(body, "isEnabled"),
            IsComplete = !hasIsComplete || ReadBool(body, "isComplete"),
            Mode = ReadString(body, "mode") ?? "driving",
            WorkHour = workHour is > 0 and < 24 ? (int)workHour.Value : 8,
            WorkMinute = workMinute is >= 0 and < 60 ? (int)workMinute.Value : 30,
            OriginName = ReadString(body, "originName"),
            DestinationName = ReadString(body, "destinationName"),
            TypicalDurationMinutes = typicalDurationMinutes is > 0
                ? (int)typicalDurationMinutes.Value
                : 25
        };
        return ProtocolDispatchResult.Ok(MapCommute(stateStore.UpsertCommuteProfile(commute)));
    }

    private ProtocolDispatchResult HandleBackup(string operation, ProtocolEnvelope envelope)
    {
        if (operation.Equals("List", StringComparison.OrdinalIgnoreCase))
            return ProtocolDispatchResult.Ok(stateStore.GetBackups().Select(MapBackup).ToArray());

        if (operation.Equals("New", StringComparison.OrdinalIgnoreCase))
        {
            var body = envelope.TryParseBody();
            var loopId = ReadString(body, "loopId") ?? stateStore.GetLoops()[0].LoopId;
            var backupName = ReadString(body, "name") ?? ReadString(body, "backupName")
                ?? $"backup-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            var backup = stateStore.CreateBackup(loopId, backupName);
            return ProtocolDispatchResult.Ok(new
            {
                uploadUrl = $"https://api.jibo.com/upload/backup/{backup.BackupId}"
            });
        }

        if (operation.Equals("Restore", StringComparison.OrdinalIgnoreCase))
        {
            var body = envelope.TryParseBody();
            var backupId = ResolveBackupId(body);
            var restored = stateStore.RestoreBackup(backupId);
            return restored is null
                ? ProtocolDispatchResult.Raw(404, "{\"error\":\"backup not found\"}", "application/json")
                : ProtocolDispatchResult.Ok(new
                {
                    result = "ok",
                    rebootRequired = true,
                    backupId = restored.BackupId
                });
        }

        if (!operation.Equals("Create", StringComparison.OrdinalIgnoreCase))
            return ProtocolDispatchResult.Ok(Array.Empty<object>());

        var createBody = envelope.TryParseBody();
        var requestedName = ReadString(createBody, "name") ?? ReadString(createBody, "backupName");
        var createLoopId = ReadString(createBody, "loopId") ?? stateStore.GetLoops()[0].LoopId;
        return ProtocolDispatchResult.Ok(
            MapBackup(stateStore.CreateBackup(createLoopId,
                requestedName ?? $"backup-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}")));
    }

    private ProtocolDispatchResult HandleKey(string operation, ProtocolEnvelope envelope)
    {
        var body = envelope.TryParseBody();
        var loopId = ReadString(body, "loopId") ?? ReadString(body, "id") ?? stateStore.GetLoops()[0].LoopId;

        if (operation.Equals("ShouldCreate", StringComparison.OrdinalIgnoreCase))
            return ProtocolDispatchResult.Ok(new
            {
                shouldCreate = stateStore.ShouldCreateSymmetricKey(loopId)
            });

        string? symmetricKey;
        if (operation.Equals("CreateSymmetricKey", StringComparison.OrdinalIgnoreCase))
        {
            symmetricKey = stateStore.GetOrCreateSymmetricKey(loopId);
            return ProtocolDispatchResult.Ok(new
            {
                loopId,
                key = symmetricKey,
                symmetricKey,
                created = true
            });
        }

        if (operation is "CreateRequest" or "RequestSymmetricKey")
        {
            var record = stateStore.CreateKeyRequest(loopId, ReadString(body, "publicKey") ?? string.Empty);
            return ProtocolDispatchResult.Ok(new
            {
                id = record.RequestId,
                loopId = record.LoopId
            });
        }

        if (operation.Equals("GetRequest", StringComparison.OrdinalIgnoreCase))
            return ProtocolDispatchResult.Ok(stateStore.GetKeyRequest(loopId, ReadString(body, "id"),
                ReadString(body, "publicKey")));

        if (operation.Equals("ListIncomingRequests", StringComparison.OrdinalIgnoreCase))
            return ProtocolDispatchResult.Ok(stateStore.GetIncomingKeyRequests());

        if (operation.Equals("ListBinaryRequests", StringComparison.OrdinalIgnoreCase))
            return ProtocolDispatchResult.Ok(stateStore.GetBinaryRequests());

        if (operation is "Share" or "ShareSymmetricKey" or "ShareBinary")
            return ProtocolDispatchResult.Ok(new { ok = true });

        if (!operation.Equals("LoadSymmetricKey", StringComparison.OrdinalIgnoreCase))
            return ProtocolDispatchResult.Ok(new { ok = true, operation });

        symmetricKey = stateStore.GetOrCreateSymmetricKey(loopId);
        return ProtocolDispatchResult.Ok(new
        {
            loopId,
            key = symmetricKey,
            symmetricKey
        });
    }

    private ProtocolDispatchResult HandleRobot(string operation, ProtocolEnvelope envelope)
    {
        var robot = stateStore.GetRobot();
        var effectiveRobotId = _configuredRobotId ?? robot.RobotId;

        if (operation.Equals("UpdateRobot", StringComparison.OrdinalIgnoreCase))
        {
            var body = envelope.TryParseBody();
            var explicitRobotId = _configuredRobotId ?? ReadString(body, "id");
            if (RobotRegistrationSources.Normalize(null, explicitRobotId) ==
                RobotRegistrationSources.DeploymentSmoke)
                return ProtocolDispatchResult.Raw(403,
                    "{\"message\":\"Deployment smoke identities cannot be created through robot setup.\"}",
                    "application/x-amz-json-1.1");
            var registeredDevice = !string.IsNullOrWhiteSpace(explicitRobotId)
                ? stateStore.FindDeviceByFriendlyId(explicitRobotId) ??
                  stateStore.GetOrCreateDevice(explicitRobotId, envelope.FirmwareVersion, envelope.ApplicationVersion)
                : robot;
            var updated = new DeviceRegistration
            {
                // Physical clients identify themselves here before requesting an empty-body hub token.
                // Promote that exact registered device so the following hub socket retains its real identity.
                DeviceId = registeredDevice.DeviceId,
                RobotId = explicitRobotId ?? registeredDevice.RobotId,
                FriendlyName = registeredDevice.FriendlyName,
                FirmwareVersion = envelope.FirmwareVersion ?? registeredDevice.FirmwareVersion,
                ApplicationVersion = envelope.ApplicationVersion ?? registeredDevice.ApplicationVersion,
                IsActive = registeredDevice.IsActive,
                CertificateThumbprint = registeredDevice.CertificateThumbprint,
                IssuedIdentityId = registeredDevice.IssuedIdentityId,
                BuildHash = registeredDevice.BuildHash,
                ConfigHash = registeredDevice.ConfigHash,
                VerifiedSerialNumber = registeredDevice.VerifiedSerialNumber,
                SerialEvidenceSource = registeredDevice.SerialEvidenceSource,
                SerialEvidenceVerifiedUtc = registeredDevice.SerialEvidenceVerifiedUtc,
                RegistrationSource = registeredDevice.RegistrationSource,
                IsHidden = registeredDevice.IsHidden,
                ArchivedUtc = registeredDevice.ArchivedUtc,
                HostMappings = registeredDevice.HostMappings
            };

            stateStore.UpdateRobot(updated);
            return ProtocolDispatchResult.Ok(new
            {
                result = "ok"
            });
        }

        if (!operation.Equals("GetRobot", StringComparison.OrdinalIgnoreCase))
            return ProtocolDispatchResult.Ok(new
            {
                result = "ok"
            });

        var profile = stateStore.GetRobotProfile();
        var requestedRobotId = _configuredRobotId ?? ReadString(envelope.TryParseBody(), "id");
        return ProtocolDispatchResult.Ok(new
        {
            id = requestedRobotId ?? profile.RobotId,
            payload = profile.Payload,
            calibrationPayload = profile.CalibrationPayload,
            updated = profile.UpdatedUtc.ToUnixTimeMilliseconds(),
            created = profile.CreatedUtc.ToUnixTimeMilliseconds()
        });
    }

    private ProtocolDispatchResult HandleUpdate(string operation, ProtocolEnvelope envelope)
    {
        var body = envelope.TryParseBody();
        var subsystem = ReadString(body, "subsystem");
        var filter = ReadString(body, "filter");
        var fromVersion = ReadString(body, "fromVersion");

        return operation switch
        {
            "ListUpdates" => ProtocolDispatchResult.Ok(stateStore.ListUpdates(subsystem, filter).Select(MapUpdate)
                .ToArray()),
            "ListUpdatesFrom" => ProtocolDispatchResult.Ok(stateStore.ListUpdates(subsystem, filter)
                .Where(update =>
                    IsUpdateNewerThanRequest(update.ToVersion, fromVersion))
                .Select(MapUpdate)
                .ToArray()),
            "GetUpdateFrom" => HandleGetUpdateFrom(subsystem, fromVersion, filter),
            "CreateUpdate" => ProtocolDispatchResult.Ok(MapUpdate(stateStore.CreateUpdate(
                fromVersion,
                ReadString(body, "toVersion"),
                ReadString(body, "changes"),
                ReadString(body, "shaHash"),
                ReadLong(body, "length"),
                subsystem,
                filter,
                ReadOpaqueObject(body, "dependencies")))),
            "RemoveUpdate" => ProtocolDispatchResult.Ok(MapUpdate(stateStore.RemoveUpdate(ReadString(body, "id")))),
            _ => ProtocolDispatchResult.Ok(Array.Empty<object>())
        };
    }

    private ProtocolDispatchResult HandleMediaContent(ProtocolEnvelope envelope)
    {
        var path = Uri.UnescapeDataString(envelope.Path["/media/".Length..]);
        var candidatePaths = new[] { path, $"/{path}" };
        var media = stateStore.GetMedia(candidatePaths).FirstOrDefault();
        if (media is null || media.IsDeleted) return ProtocolDispatchResult.Raw(404, string.Empty);

        var storedContent = _mediaContentStore.LoadAsync(media.Path, CancellationToken.None).GetAwaiter().GetResult();
        var contentType = storedContent?.ContentType ?? TryReadMetaString(media.Meta, "contentType") ??
            "application/octet-stream";
        var bodyText = storedContent is not null
            ? Encoding.UTF8.GetString(storedContent.Content)
            : TryReadMetaString(media.Meta, "bodyText") ?? string.Empty;
        return ProtocolDispatchResult.Raw(200, bodyText, contentType);
    }

    private ProtocolDispatchResult HandleGetUpdateFrom(string? subsystem, string? fromVersion, string? filter)
    {
        var update = stateStore.GetUpdateFrom(subsystem, fromVersion, filter);
        return update is null
            ? ProtocolDispatchResult.NoContent()
            : ProtocolDispatchResult.Ok(MapUpdate(update));
    }

    private static string? ReadSchedulerFilterFromPath(string path)
    {
        const string prefix = "/update/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || path.Length <= prefix.Length)
            return null;

        return Uri.UnescapeDataString(path[prefix.Length..]);
    }

    private bool TryHandleLocalSchedulerRequest(ProtocolEnvelope envelope, out ProtocolDispatchResult result)
    {
        result = ProtocolDispatchResult.Ok(new { });

        if (string.IsNullOrWhiteSpace(envelope.Path) || !string.IsNullOrWhiteSpace(envelope.ServicePrefix))
            return false;

        if (envelope.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
            envelope.Path.StartsWith("/update", StringComparison.OrdinalIgnoreCase))
        {
            var filter = ReadSchedulerFilterFromPath(envelope.Path);
            result = ProtocolDispatchResult.Ok(new
            {
                updates = ListSchedulerUpdates(filter)
            });
            return true;
        }

        if (!envelope.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            return false;

        if (envelope.Path.Equals("/backup-status", StringComparison.OrdinalIgnoreCase))
        {
            result = ProtocolDispatchResult.Ok(new
            {
                status = "OK",
                data = GetSchedulerBackupStatus()
            });
            return true;
        }

        if (envelope.Path.Equals("/download-status", StringComparison.OrdinalIgnoreCase))
        {
            result = ProtocolDispatchResult.Ok(new
            {
                status = "OK",
                data = GetSchedulerDownloadStatus()
            });
            return true;
        }

        if (envelope.Path.Equals("/apply-update", StringComparison.OrdinalIgnoreCase))
        {
            result = ApplySchedulerUpdate(envelope);
            return true;
        }

        if (envelope.Path.Equals("/check-updates", StringComparison.OrdinalIgnoreCase))
        {
            var body = envelope.TryParseBody();
            var filter = ReadString(body, "filter");
            result = ProtocolDispatchResult.Ok(new
            {
                status = "OK",
                data = ListSchedulerUpdates(filter)
            });
            return true;
        }

        if (!envelope.Path.Equals("/backup-robot", StringComparison.OrdinalIgnoreCase) &&
            !envelope.Path.Equals("/ota-update", StringComparison.OrdinalIgnoreCase)) return false;

        if (envelope.Path.Equals("/backup-robot", StringComparison.OrdinalIgnoreCase))
            StartSchedulerBackupCycle();
        else
            StartSchedulerUpdateCycle(ReadSchedulerFilter(envelope));

        result = ProtocolDispatchResult.Ok(new
        {
            status = "OK"
        });
        return true;
    }

    private ProtocolDispatchResult ApplySchedulerUpdate(ProtocolEnvelope envelope)
    {
        var body = envelope.TryParseBody();
        var updateId = ReadString(body, "id") ?? ReadString(body, "updateId");
        var subsystem = ReadString(body, "subsystem") ?? "robot";
        var update = stateStore.ListUpdates()
            .FirstOrDefault(candidate => updateId is not null
                ? candidate.UpdateId.Equals(updateId, StringComparison.OrdinalIgnoreCase)
                : candidate.Subsystem.Equals(subsystem, StringComparison.OrdinalIgnoreCase));

        if (update is null)
            return ProtocolDispatchResult.Raw(404, JsonSerializer.Serialize(new
            {
                status = "NOT_FOUND",
                updateId
            }), "application/json");

        var robot = stateStore.GetRobot();
        stateStore.UpdateRobot(new DeviceRegistration
        {
            DeviceId = robot.DeviceId,
            RobotId = robot.RobotId,
            FriendlyName = robot.FriendlyName,
            FirmwareVersion = update.ToVersion,
            ApplicationVersion = robot.ApplicationVersion,
            CertificateThumbprint = robot.CertificateThumbprint,
            IssuedIdentityId = robot.IssuedIdentityId,
            BuildHash = robot.BuildHash,
            ConfigHash = robot.ConfigHash,
            RegistrationSource = robot.RegistrationSource,
            IsHidden = robot.IsHidden,
            ArchivedUtc = robot.ArchivedUtc,
            HostMappings = robot.HostMappings
        });

        lock (_schedulerLock)
        {
            _schedulerState.DownloadedUpdateIds.Remove(update.UpdateId);
            if (_schedulerState.DownloadStatus?.Updates.Any(item =>
                    item.Id.Equals(update.UpdateId, StringComparison.OrdinalIgnoreCase)) == true)
                _schedulerState.DownloadStatus = null;
        }

        return ProtocolDispatchResult.Ok(new
        {
            status = "OK",
            updateId = update.UpdateId,
            fromVersion = update.FromVersion,
            toVersion = update.ToVersion,
            firmwareVersion = update.ToVersion,
            rebootRequired = true
        });
    }

    private static string? ReadSchedulerFilter(ProtocolEnvelope envelope)
    {
        var pathFilter = ReadSchedulerFilterFromPath(envelope.Path);
        if (!string.IsNullOrWhiteSpace(pathFilter)) return pathFilter;

        var body = envelope.TryParseBody();
        return ReadString(body, "filter") ?? ReadString(body, "subsystem");
    }

    private object[] ListSchedulerUpdates(string? filter)
    {
        var robotVersion = stateStore.GetRobot().FirmwareVersion ?? "12.10.0";

        return stateStore.ListUpdates()
            .Where(update => IsUpdateNewerThanRequest(update.ToVersion, robotVersion))
            .Where(update =>
                string.IsNullOrWhiteSpace(filter) ||
                update.Subsystem.Equals(filter, StringComparison.OrdinalIgnoreCase) ||
                (update.Filter is not null &&
                 update.Filter.Equals(filter, StringComparison.OrdinalIgnoreCase)))
            .Select(update => MapSchedulerUpdate(update, IsSchedulerUpdateDownloaded(update.UpdateId)))
            .ToArray();
    }

    private bool GetSchedulerBackupStatus()
    {
        lock (_schedulerLock)
        {
            return _schedulerState.BackingUp || _schedulerState.BackingBeforeUpdate;
        }
    }

    private object? GetSchedulerDownloadStatus()
    {
        lock (_schedulerLock)
        {
            if (_schedulerState.DownloadStatus is null) return null;

            return new
            {
                updates = _schedulerState.DownloadStatus.Updates
                    .Select(update =>
                        MapSchedulerUpdate(update, _schedulerState.DownloadedUpdateIds.Contains(update.Id)))
                    .ToArray(),
                status = _schedulerState.DownloadStatus.Status is null
                    ? null
                    : new
                    {
                        id = _schedulerState.DownloadStatus.Status.Id,
                        length = _schedulerState.DownloadStatus.Status.Length,
                        received = _schedulerState.DownloadStatus.Status.Received,
                        status = _schedulerState.DownloadStatus.Status.Status,
                        reason = _schedulerState.DownloadStatus.Status.Reason,
                        error = _schedulerState.DownloadStatus.Status.Error
                    }
            };
        }
    }

    private void StartSchedulerUpdateCycle(string? filter)
    {
        lock (_schedulerLock)
        {
            if (_schedulerState.BackingUp || _schedulerState.BackingBeforeUpdate ||
                _schedulerState.DownloadStatus is not null)
                return;

            _schedulerState.BackingUp = true;
            _schedulerState.BackingBeforeUpdate = true;
            _schedulerState.ActiveFilter = filter;
            _schedulerState.DownloadedUpdateIds.Clear();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SchedulerBackupDelayMs).ConfigureAwait(false);

                var robotVersion = stateStore.GetRobot().FirmwareVersion ?? "12.10.0";
                var pendingUpdates = stateStore.ListUpdates()
                    .Where(update => IsUpdateNewerThanRequest(update.ToVersion, robotVersion))
                    .Where(update =>
                        string.IsNullOrWhiteSpace(filter) ||
                        update.Subsystem.Equals(filter, StringComparison.OrdinalIgnoreCase) ||
                        (update.Filter is not null &&
                         update.Filter.Equals(filter, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();

                lock (_schedulerLock)
                {
                    _schedulerState.BackingUp = false;
                    _schedulerState.BackingBeforeUpdate = false;

                    _schedulerState.DownloadStatus = pendingUpdates.Length == 0
                        ? null
                        : new SchedulerDownloadState
                        {
                            Updates = pendingUpdates.Select(update => new SchedulerUpdateState
                            {
                                Id = update.UpdateId,
                                Subsystem = update.Subsystem,
                                Changes = update.Changes,
                                Length = update.Length,
                                ToVersion = update.ToVersion,
                                Dependencies = new Dictionary<string, object?>(),
                                Downloaded = false
                            }).ToArray(),
                            Status = new SchedulerDownloadProgress
                            {
                                Id = pendingUpdates[0].UpdateId,
                                Length = pendingUpdates[0].Length,
                                Received = 0,
                                Status = "downloading"
                            }
                        };
                }

                if (pendingUpdates.Length == 0) return;

                for (var i = 0; i < pendingUpdates.Length; i++)
                {
                    var update = pendingUpdates[i];
                    var updateLength = update.Length > 0 ? update.Length : 1000;
                    var received = 0L;

                    while (received < updateLength)
                    {
                        await Task.Delay(SchedulerDownloadTickMs).ConfigureAwait(false);
                        received = Math.Min(updateLength, received + Math.Max(100L, updateLength / 4));
                        lock (_schedulerLock)
                        {
                            _schedulerState.DownloadStatus?.Status = new SchedulerDownloadProgress
                            {
                                Id = update.UpdateId,
                                Length = updateLength,
                                Received = received,
                                Status = "downloading"
                            };
                        }
                    }

                    lock (_schedulerLock)
                    {
                        _schedulerState.DownloadedUpdateIds.Add(update.UpdateId);

                        if (_schedulerState.DownloadStatus is null) continue;

                        _schedulerState.DownloadStatus.Updates[i] =
                            _schedulerState.DownloadStatus.Updates[i] with { Downloaded = true };
                        _schedulerState.DownloadStatus.Status = new SchedulerDownloadProgress
                        {
                            Id = update.UpdateId,
                            Length = updateLength,
                            Received = updateLength,
                            Status = "finished"
                        };
                    }
                }

                await Task.Delay(SchedulerDownloadFinishDelayMs).ConfigureAwait(false);
                lock (_schedulerLock)
                {
                    _schedulerState.DownloadStatus = null;
                }
            }
            catch
            {
                lock (_schedulerLock)
                {
                    _schedulerState.BackingUp = false;
                    _schedulerState.BackingBeforeUpdate = false;
                    _schedulerState.DownloadStatus = null;
                }
            }
        });
    }

    private void StartSchedulerBackupCycle()
    {
        lock (_schedulerLock)
        {
            if (_schedulerState.BackingUp || _schedulerState.BackingBeforeUpdate ||
                _schedulerState.DownloadStatus is not null)
                return;

            _schedulerState.BackingUp = true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SchedulerBackupDelayMs).ConfigureAwait(false);
            }
            finally
            {
                lock (_schedulerLock)
                {
                    _schedulerState.BackingUp = false;
                }
            }
        });
    }

    private bool IsSchedulerUpdateDownloaded(string updateId)
    {
        lock (_schedulerLock)
        {
            return _schedulerState.DownloadedUpdateIds.Contains(updateId);
        }
    }

    private static object MapSchedulerUpdate(UpdateManifest update, bool downloaded)
    {
        return new
        {
            id = update.UpdateId,
            subsystem = update.Subsystem,
            changes = update.Changes,
            length = update.Length,
            toVersion = update.ToVersion,
            dependencies = new Dictionary<string, object?>(),
            downloaded
        };
    }

    private static object MapSchedulerUpdate(SchedulerUpdateState update, bool downloaded)
    {
        return new
        {
            id = update.Id,
            subsystem = update.Subsystem,
            changes = update.Changes,
            length = update.Length,
            toVersion = update.ToVersion,
            dependencies = update.Dependencies ?? new Dictionary<string, object?>(),
            downloaded
        };
    }

    private static object MapUpdate(UpdateManifest update)
    {
        return new
        {
            _id = update.UpdateId,
            created = update.CreatedUtc.ToUnixTimeMilliseconds(),
            accountId = "usr_openjibo_owner",
            fromVersion = update.FromVersion,
            toVersion = update.ToVersion,
            changes = update.Changes,
            url = update.Url,
            shaHash = update.ShaHash,
            length = update.Length,
            subsystem = update.Subsystem,
            filter = update.Filter,
            dependencies = update.Dependencies
        };
    }

    private static bool IsUpdateNewerThanRequest(string candidateVersion, string? fromVersion)
    {
        if (string.IsNullOrWhiteSpace(fromVersion)) return true;

        if (string.IsNullOrWhiteSpace(candidateVersion)) return false;

        if (Version.TryParse(candidateVersion, out var candidate) &&
            Version.TryParse(fromVersion, out var requested))
            return candidate > requested;

        return string.Compare(candidateVersion, fromVersion, StringComparison.OrdinalIgnoreCase) > 0;
    }

    private static object MapBackup(BackupRecord backup)
    {
        return new
        {
            modified = backup.CreatedUtc.ToString("O"),
            etag = backup.BackupId,
            size = "0",
            location = new
            {
                expires = backup.CreatedUtc.AddHours(1).ToString("O"),
                url = $"https://api.jibo.com/backup/{backup.BackupId}"
            }
        };
    }

    private static object MapHoliday(HolidayRecord holiday)
    {
        return new
        {
            id = holiday.Id,
            eventId = holiday.EventId,
            name = holiday.Name,
            category = holiday.Category,
            subcategory = holiday.Subcategory,
            loopId = holiday.LoopId,
            memberId = holiday.MemberId,
            isEnabled = holiday.IsEnabled,
            date = holiday.Date,
            endDate = holiday.EndDate,
            source = holiday.Source,
            countryCode = holiday.CountryCode,
            created = holiday.Created
        };
    }

    private static object MapCommute(CommuteProfileRecord commute)
    {
        return new
        {
            id = commute.Id,
            loopId = commute.LoopId,
            memberId = commute.MemberId,
            isEnabled = commute.IsEnabled,
            isComplete = commute.IsComplete,
            mode = commute.Mode,
            workHour = commute.WorkHour,
            workMinute = commute.WorkMinute,
            originName = commute.OriginName,
            destinationName = commute.DestinationName,
            typicalDurationMinutes = commute.TypicalDurationMinutes,
            created = commute.Created,
            updated = commute.Updated
        };
    }

    private static object MapMedia(MediaRecord item)
    {
        return new
        {
            path = item.Path,
            created = item.CreatedUtc.ToUnixTimeMilliseconds(),
            type = item.MediaType,
            reference = item.Reference,
            accountId = item.AccountId,
            loopId = item.LoopId,
            url = item.Url,
            thumbnailUrl = item.Url,
            originalUrl = item.Url,
            isEncrypted = item.IsEncrypted,
            isDeleted = item.IsDeleted,
            meta = item.Meta
        };
    }

    private static string? TryReadMetaString(IDictionary<string, object?> meta, string key)
    {
        return meta.TryGetValue(key, out var value)
            ? value?.ToString()
            : null;
    }

    private static string? ResolveBackupId(JsonElement? body)
    {
        var rawBackupId = ReadString(body, "backupId") ?? ReadString(body, "id") ?? ReadString(body, "etag");
        if (!string.IsNullOrWhiteSpace(rawBackupId)) return ExtractBackupId(rawBackupId);

        if (body is null || !body.Value.TryGetProperty("location", out var location))
            return null;

        if (location.ValueKind == JsonValueKind.Object && location.TryGetProperty("url", out var url))
            return ExtractBackupId(url.ValueKind == JsonValueKind.String ? url.GetString() : url.ToString());

        return ExtractBackupId(location.ValueKind == JsonValueKind.String ? location.GetString() : location.ToString());
    }

    private static string? ExtractBackupId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var trimmed = value.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return trimmed;

        var segments = uri.Segments
            .Select(segment => segment.Trim('/'))
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToArray();
        var backupSegmentIndex = Array.FindLastIndex(segments, segment =>
            segment.Equals("backup", StringComparison.OrdinalIgnoreCase));
        return backupSegmentIndex >= 0 && backupSegmentIndex + 1 < segments.Length
            ? Uri.UnescapeDataString(segments[backupSegmentIndex + 1])
            : Uri.UnescapeDataString(segments.LastOrDefault() ?? trimmed);
    }

    private static string? ReadString(JsonElement? element, string propertyName)
    {
        if (element is null || !element.Value.TryGetProperty(propertyName, out var property)) return null;

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.ToString();
    }

    private static long? ReadLong(JsonElement? element, string propertyName)
    {
        if (element is null || !element.Value.TryGetProperty(propertyName, out var property)) return null;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number)) return number;

        return long.TryParse(property.ToString(), out var parsed) ? parsed : null;
    }

    private static double? ReadDouble(JsonElement? element, string propertyName)
    {
        if (element is null || !element.Value.TryGetProperty(propertyName, out var property)) return null;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var number)) return number;

        return double.TryParse(property.ToString(), out var parsed) ? parsed : null;
    }

    private static bool ReadBool(JsonElement? element, string propertyName)
    {
        if (element is null || !element.Value.TryGetProperty(propertyName, out var property)) return false;

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => bool.TryParse(property.ToString(), out var parsed) && parsed
        };
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement? element, string propertyName)
    {
        if (element is null || !element.Value.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Array) return [];

        return
        [
            .. property.EnumerateArray()
                .Select(item =>
                    item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : item.ToString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
        ];
    }

    private static IDictionary<string, object?>? ReadObject(JsonElement? element, string propertyName)
    {
        if (element is null || !element.Value.TryGetProperty(propertyName, out var property)) return null;

        if (property.ValueKind != JsonValueKind.Object) return null;

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in property.EnumerateObject())
            result[child.Name] = child.Value.ValueKind switch
            {
                JsonValueKind.String => child.Value.GetString(),
                JsonValueKind.Number when child.Value.TryGetInt64(out var longValue) => longValue,
                JsonValueKind.Number when child.Value.TryGetDouble(out var doubleValue) => doubleValue,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => child.Value.ToString()
            };

        return result;
    }

    private static IDictionary<string, object?>? ReadOpaqueObject(JsonElement? element, string propertyName)
    {
        if (element is null || !element.Value.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Object)
            return null;

        return JsonSerializer.Deserialize<Dictionary<string, object?>>(property.GetRawText());
    }

    private static string? ReadHeader(ProtocolEnvelope envelope, string headerName)
    {
        return envelope.Headers.TryGetValue(headerName, out var value) ? value : null;
    }

    private static string CreateOobeToken()
    {
        Span<byte> bytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(bytes);
        return $"oobe-{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }

    private static bool ReadBooleanHeader(ProtocolEnvelope envelope, string headerName)
    {
        return envelope.Headers.TryGetValue(headerName, out var value) &&
               bool.TryParse(value, out var parsed) &&
               parsed;
    }

    private sealed record ConversionReadiness(IReadOnlyList<string> Blockers)
    {
        public bool CanWriteRobot => Blockers.Count == 0;

        public object ToResponse()
        {
            return new
            {
                canWriteRobot = CanWriteRobot,
                blockers = Blockers,
                supportedTargetModes = SupportedOpenJiboTargetModes.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                requiredEvidence = new[]
                {
                    "prepared-oobe-token",
                    "rollback-snapshot",
                    "target-host-mapping",
                    "supported-target-mode",
                    "self-hosted-target-host-when-self-hosted",
                    "baseline-audit-when-required"
                }
            };
        }
    }

    private sealed class SchedulerRuntimeState
    {
        public bool BackingUp { get; set; }
        public bool BackingBeforeUpdate { get; set; }
        public string? ActiveFilter { get; set; }
        public SchedulerDownloadState? DownloadStatus { get; set; }
        public HashSet<string> DownloadedUpdateIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class SchedulerDownloadState
    {
        public SchedulerUpdateState[] Updates { get; set; } = [];
        public SchedulerDownloadProgress? Status { get; set; }
    }

    private sealed record SchedulerUpdateState
    {
        public string Id { get; init; } = string.Empty;
        public string Subsystem { get; init; } = "robot";
        public string Changes { get; init; } = string.Empty;
        public long Length { get; init; }
        public string ToVersion { get; init; } = string.Empty;
        public IDictionary<string, object?> Dependencies { get; init; } = new Dictionary<string, object?>();
        public bool Downloaded { get; init; }
    }

    private sealed class SchedulerDownloadProgress
    {
        public string Id { get; set; } = string.Empty;
        public long Length { get; set; }
        public long Received { get; set; }
        public string Status { get; set; } = "downloading";
        public string? Reason { get; set; }
        public string? Error { get; set; }
    }

    private sealed class OobeBaselineEvidence
    {
        public string? FirmwareVersion { get; init; }
        public string? ApplicationVersion { get; init; }
        public string? Distribution { get; init; }
        public string? StockMode { get; init; }
        public bool RequireBaselineAudit { get; init; }

        public bool HasMinimumBaseline =>
            !string.IsNullOrWhiteSpace(FirmwareVersion) &&
            !string.IsNullOrWhiteSpace(StockMode);

        public OobeBaselineEvidence Merge(OobeBaselineEvidence next)
        {
            return new OobeBaselineEvidence
            {
                FirmwareVersion = next.FirmwareVersion ?? FirmwareVersion,
                ApplicationVersion = next.ApplicationVersion ?? ApplicationVersion,
                Distribution = next.Distribution ?? Distribution,
                StockMode = next.StockMode ?? StockMode,
                RequireBaselineAudit = RequireBaselineAudit || next.RequireBaselineAudit
            };
        }

        public object ToResponse()
        {
            return new
            {
                firmwareVersion = FirmwareVersion,
                applicationVersion = ApplicationVersion,
                distribution = Distribution,
                stockMode = StockMode,
                requireBaselineAudit = RequireBaselineAudit,
                hasMinimumBaseline = HasMinimumBaseline
            };
        }
    }

    private sealed class OobeTokenState
    {
        public string? DeviceId { get; set; }
        public string? LoopId { get; set; }
        public string TargetMode { get; set; } = "open-jibo";
        public string? TargetHost { get; set; }
        public string? RollbackSnapshotId { get; set; }
        public OobeBaselineEvidence BaselineEvidence { get; set; } = new();
        public VerifiedSerialEvidence? VerifiedSerialEvidence { get; set; }
        public bool Complete { get; set; }
        public string? OnboardingNonce { get; set; }
        public string? OnboardingState { get; set; }
        public DateTimeOffset ExpiresUtc { get; init; }
    }

    private sealed record VerifiedSerialEvidence(string SerialNumber, string Source, DateTimeOffset VerifiedUtc);

    private sealed class NullMediaContentStore : IMediaContentStore
    {
        public Task StoreAsync(string path, string contentType, byte[] content,
            IReadOnlyDictionary<string, object?>? meta, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<MediaContentSnapshot?> LoadAsync(string path, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<MediaContentSnapshot?>(null);
        }

        public async IAsyncEnumerable<MediaContentItem> EnumerateAsync(string prefix,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IReadOnlyList<MediaContentItem>> ListAsync(string prefix, int maxCount = 100,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MediaContentItem>>([]);
    }
}
