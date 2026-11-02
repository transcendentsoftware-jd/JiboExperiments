using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jibo.Cloud.Api.Hosting;

internal sealed class WebSocketRequestCoordinator(
    JiboWebSocketService webSocketService,
    HomeAssistantWebSocketHandler homeAssistantWebSocketHandler,
    IWebSocketTelemetrySink telemetrySink,
    RobotNotificationRegistry robotNotificationRegistry,
    RobotPresenceRegistry robotPresenceRegistry,
    ICloudStateStore cloudStateStore,
    ITransportMetrics transportMetrics,
    SingleRobotHttpHubAccessGuard singleRobotHttpHubAccessGuard,
    WebSocketTransportPolicy transportPolicy,
    ILogger<WebSocketRequestCoordinator> logger,
    HubChannelLeasePolicy? channelLeases = null,
    IRuntimePairingInitiationClient? pairingClient = null,
    RuntimePairingCredentialEvidenceKey? pairingCredentialKey = null)
{
    private static readonly TimeSpan TurnWatchdogInterval = TimeSpan.FromMilliseconds(50);
    private const string ActiveConnectionIdMetadataKey = "openjibo.activeWebSocketConnectionId";
    private static readonly object SessionGateRegistryLock = new();
    private static readonly Dictionary<string, SessionGate> SessionGates = new(StringComparer.OrdinalIgnoreCase);
    internal HubChannelLeasePolicy LeasePolicy { get; init; } = channelLeases ?? HubChannelLeasePolicy.Disabled;
    internal IRuntimePairingInitiationClient PairingClient { get; init; } = pairingClient ?? RejectingRuntimePairingInitiationClient.Instance;
    internal RuntimePairingCredentialEvidenceKey? PairingCredentialKey { get; init; } = pairingCredentialKey;
    internal TimeSpan LeaseRenewalInterval { get; init; } = TimeSpan.FromSeconds(5);
    internal TimeSpan CredentialRevalidationInterval { get; init; } = HubTokenContinuityMonitor.DefaultInterval;

    /// <summary>
    /// Test helper constructor. Production DI injects the full primary constructor.
    /// </summary>
    internal WebSocketRequestCoordinator(
        JiboWebSocketService webSocketService,
        HomeAssistantWebSocketHandler homeAssistantWebSocketHandler,
        IWebSocketTelemetrySink telemetrySink,
        ICloudStateStore cloudStateStore)
        : this(
            webSocketService,
            homeAssistantWebSocketHandler,
            telemetrySink,
            new RobotNotificationRegistry(),
            new RobotPresenceRegistry(),
            cloudStateStore,
            NullTransportMetrics.Instance,
            new SingleRobotHttpHubAccessGuard(false),
            new WebSocketTransportPolicy(true),
            NullLogger<WebSocketRequestCoordinator>.Instance)
    {
    }

    internal WebSocketRequestCoordinator(
        JiboWebSocketService webSocketService,
        HomeAssistantWebSocketHandler homeAssistantWebSocketHandler,
        IWebSocketTelemetrySink telemetrySink,
        ICloudStateStore cloudStateStore,
        SingleRobotHttpHubAccessGuard singleRobotHttpHubAccessGuard)
        : this(
            webSocketService,
            homeAssistantWebSocketHandler,
            telemetrySink,
            new RobotNotificationRegistry(),
            new RobotPresenceRegistry(),
            cloudStateStore,
            NullTransportMetrics.Instance,
            singleRobotHttpHubAccessGuard,
            new WebSocketTransportPolicy(true),
            NullLogger<WebSocketRequestCoordinator>.Instance)
    {
    }

    internal WebSocketRequestCoordinator(
        JiboWebSocketService webSocketService,
        HomeAssistantWebSocketHandler homeAssistantWebSocketHandler,
        IWebSocketTelemetrySink telemetrySink,
        RobotNotificationRegistry robotNotificationRegistry,
        ICloudStateStore cloudStateStore,
        ILogger<WebSocketRequestCoordinator> logger)
        : this(
            webSocketService,
            homeAssistantWebSocketHandler,
            telemetrySink,
            robotNotificationRegistry,
            new RobotPresenceRegistry(),
            cloudStateStore,
            NullTransportMetrics.Instance,
            new SingleRobotHttpHubAccessGuard(false),
            new WebSocketTransportPolicy(true),
            logger)
    {
    }

    internal async Task HandleAsync(HttpContext context)
    {
        var kind = SocketKindResolver.Resolve(context.Request.Host.Host, context.Request.Path);
        var safePath = RequestLogSanitizer.RedactWebSocketPath(kind, context.Request.Path);
        if (string.Equals(kind, "openjibo", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            logger.LogWarning(
                "WebSocket request rejected because the route has no authenticated socket kind host={Host} path={Path}",
                context.Request.Host.Host,
                safePath);
            return;
        }

        if (!transportPolicy.IsAllowed(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            logger.LogWarning(
                "WebSocket request rejected because secure transport is required kind={Kind} host={Host} path={Path}",
                kind,
                context.Request.Host.Host,
                safePath);
            return;
        }

        if (string.Equals(kind, "home-assistant", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation(
                "WebSocket request routed to Home Assistant handler traceId={TraceId} host={Host} path={Path} remoteIp={RemoteIp}",
                context.TraceIdentifier,
                context.Request.Host.Host,
                safePath,
                context.Connection.RemoteIpAddress?.ToString());
            await homeAssistantWebSocketHandler.HandleAsync(context);
            return;
        }

        var token = TokenResolver.Resolve(context.Request);
        var tokenFingerprint = Fingerprint(token);
        IDisposable? singleRobotHttpHubLease = null;
        logger.LogInformation(
            "WebSocket request received traceId={TraceId} kind={Kind} tokenFingerprint={TokenFingerprint} " +
            "host={Host} path={Path} remoteIp={RemoteIp} userAgent={UserAgent}",
            context.TraceIdentifier,
            kind,
            tokenFingerprint,
            context.Request.Host.Host,
            safePath,
            context.Connection.RemoteIpAddress?.ToString(),
            context.Request.Headers.UserAgent.ToString());
        switch (kind)
        {
            case "unknown":
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                logger.LogWarning("WebSocket request rejected as unknown kind={Kind} host={Host} path={Path}",
                    kind, context.Request.Host.Host, safePath);
                return;
            case "api-socket" when string.IsNullOrWhiteSpace(token):
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                logger.LogWarning(
                    "WebSocket request rejected due to missing token kind={Kind} host={Host} path={Path} remoteIp={RemoteIp}",
                    kind, context.Request.Host.Host, safePath,
                    context.Connection.RemoteIpAddress?.ToString());
                return;
            case "neo-hub-listen" or "neo-hub-proactive" when string.IsNullOrWhiteSpace(token):
                var access = singleRobotHttpHubAccessGuard.TryAcquire(context, kind);
                if (!access.IsAllowed)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    logger.LogWarning(
                        "WebSocket request rejected due to missing token kind={Kind} host={Host} path={Path} " +
                        "remoteIp={RemoteIp} tokenlessCompatibilityReason={TokenlessCompatibilityReason}",
                        kind, context.Request.Host.Host, safePath,
                        context.Connection.RemoteIpAddress?.ToString(), access.Reason);
                    return;
                }

                singleRobotHttpHubLease = access.Lease;
                logger.LogWarning(
                    "Tokenless single-robot HTTP Hub compatibility accepted kind={Kind} host={Host} path={Path} " +
                    "remoteIp={RemoteIp}",
                    kind, context.Request.Host.Host, safePath,
                    context.Connection.RemoteIpAddress?.ToString());
                break;
        }

        using var singleRobotHttpHubLeaseScope = singleRobotHttpHubLease;
        HubTokenCredentialBinding? credentialBinding = null;
        RuntimePairingCredentialEvidence? pairingEvidence = null;
        string? issuedBearer = null;
        if (!string.IsNullOrWhiteSpace(token) && TryResolveRequiredTokenKind(kind, out var requiredTokenKind))
        {
            var issuedToken = cloudStateStore.FindIssuedToken(token);
            if (issuedToken is null ||
                !string.Equals(issuedToken.Kind, requiredTokenKind, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                logger.LogWarning(
                    "WebSocket request rejected due to invalid token kind={Kind} tokenFingerprint={TokenFingerprint} " +
                    "requiredTokenKind={RequiredTokenKind} host={Host} path={Path} remoteIp={RemoteIp}",
                    kind, tokenFingerprint, requiredTokenKind, context.Request.Host.Host, safePath,
                    context.Connection.RemoteIpAddress?.ToString());
                return;
            }
            issuedBearer = issuedToken.Token;
            pairingEvidence = RuntimePairingCredentialEvidence.Read(issuedToken.Metadata);
            if (requiredTokenKind == "hub" && HubTokenCredentialBinding.ContainsMetadata(issuedToken.Metadata) &&
                !HubTokenCredentialBinding.TryRead(issuedToken.Metadata, out credentialBinding))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }

        var useChannelLease = LeasePolicy.Enabled && kind is ("neo-hub-listen" or "neo-hub-proactive");
        if (useChannelLease && !LeasePolicy.IsReady)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        // Some compatibility stores accept case-insensitive aliases. Strict lease
        // keys must use one authoritative bearer spelling across all replicas.
        if (useChannelLease && (credentialBinding is null || !string.Equals(token, issuedBearer, StringComparison.Ordinal)))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        if (useChannelLease)
        {
            using var validation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            validation.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                if (!await cloudStateStore.IsHubTokenCredentialBindingCurrentAsync(token!, credentialBinding!, validation.Token)
                        .WaitAsync(TimeSpan.FromSeconds(2), context.RequestAborted))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                validation.Cancel();
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
        }
        await using var channelLease = useChannelLease
            ? await LeasePolicy.AcquireAsync(token!, kind == "neo-hub-listen" ? HubChannel.Listen : HubChannel.Proactive,
                context.RequestAborted) : null;
        if (useChannelLease && channelLease is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        var ownershipKey = channelLease is null ? ActiveConnectionIdMetadataKey : ActiveConnectionIdMetadataKey + "." + kind;

        // Stock OS 1.9's Neo Hub client closes when ASP.NET sends a WebSocket
        // control-frame keepalive: first at the default two-minute interval,
        // then at 30 seconds when that interval was trialled. Limit the
        // compatibility behavior to the legacy Hub routes; other sockets keep
        // the framework default.
        var acceptContext = kind is "neo-hub-listen" or "neo-hub-proactive"
            ? new WebSocketAcceptContext { KeepAliveInterval = Timeout.InfiniteTimeSpan }
            : null;
        using var socket = acceptContext is null
            ? await context.WebSockets.AcceptWebSocketAsync()
            : await context.WebSockets.AcceptWebSocketAsync(acceptContext);
        transportMetrics.WebSocketConnectionOpened(kind);
        var connectionId = Guid.NewGuid().ToString("N");
        var openEnvelope = CreateEnvelope(context, kind, token, connectionId);
        var sessionKey = ResolveSessionKey(openEnvelope);
        CloudSession session;
        using (var gateLease = AcquireSessionGate(sessionKey))
        {
            lock (gateLease.Gate.SyncRoot)
            {
                session = webSocketService.GetOrCreateSession(openEnvelope);
                // Replacement sockets for one token share the active session. Record ownership
                // so an older socket cannot tear down state established by its successor.
                session.Metadata[ownershipKey] = connectionId;
            }
        }
        var registeredApiSocket = false;
        var presenceConnectionId = Guid.Empty;

        var isPrematureClose = false;
        var loopTransId = session.TurnState.TransId;
        var sessionStateOwnedOnClose = false;
        using var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var connectionCancellation = connectionLifetime.Token;
        using var pairingContext = AmbientRuntimePairingSession.Bind(
            kind == "neo-hub-listen" && channelLease is not null && credentialBinding is not null && pairingEvidence is not null &&
            PairingClient.IsReady && PairingCredentialKey?.IsReady == true
                ? new HubRuntimePairingSession(cloudStateStore,token!,credentialBinding,pairingEvidence,PairingCredentialKey,
                    channelLease,PairingClient,connectionCancellation) : null);

        var continuity = credentialBinding is not null
            ? new HubTokenContinuityMonitor(cloudStateStore, CredentialRevalidationInterval)
                .RunAsync(socket, token!, credentialBinding, connectionLifetime)
            : Task.FromResult<string?>(null);
        string? credentialFailure = null;
        var leaseMonitor = channelLease is not null
            ? new HubChannelLeaseMonitor(LeaseRenewalInterval).RunAsync(socket, channelLease, connectionLifetime)
            : Task.CompletedTask;

        Task GuardLeaseAsync(CancellationToken cancellation) => GuardConnectionLeaseAsync(cancellation, idle: false);

        async Task GuardConnectionLeaseAsync(CancellationToken cancellation, bool idle)
        {
            connectionCancellation.ThrowIfCancellationRequested();
            cancellation.ThrowIfCancellationRequested();
            if (channelLease is null) return;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, connectionCancellation);
            var current = idle ? await channelLease.CheckIdleAsync(linked.Token) : await channelLease.CheckAsync(linked.Token);
            if (current) return;
            connectionLifetime.Cancel(); socket.Abort();
            throw new OperationCanceledException(connectionCancellation);
        }

        try
        {
            // All setup after claiming local ownership participates in cleanup,
            // including identity lookups, telemetry and notification registration.
            connectionCancellation.ThrowIfCancellationRequested();
            var initialRobotKeys = ResolveRobotKeys(token, session);
            logger.LogInformation(
                "WebSocket connection accepted connectionId={ConnectionId} traceId={TraceId} kind={Kind} " +
                "tokenFingerprint={TokenFingerprint} sessionId={SessionId} deviceId={DeviceId} " +
                "robotId={RobotId} friendlyName={FriendlyName} robotKeyCount={RobotKeyCount}",
                connectionId,
                context.TraceIdentifier,
                kind,
                tokenFingerprint,
                session.SessionId,
                session.DeviceId,
                ReadSessionMetadata(session, "registeredRobotId") ?? ReadSessionMetadata(session, "robotId"),
                ReadSessionMetadata(session, "robotFriendlyId") ?? ReadSessionMetadata(session, "friendlyId"),
                initialRobotKeys.Count);
            await telemetrySink.RecordConnectionOpenedAsync(openEnvelope, session, connectionCancellation);
            connectionCancellation.ThrowIfCancellationRequested();
            presenceConnectionId = robotPresenceRegistry.Register(kind, socket, initialRobotKeys);
            if (string.Equals(kind, "api-socket", StringComparison.OrdinalIgnoreCase))
            {
                var robotKeys = ResolveRobotKeys(token, session);
                robotNotificationRegistry.Register(robotKeys, socket);
                registeredApiSocket = true;
                var drained = await robotNotificationRegistry.DrainPendingAsync(
                    robotKeys,
                    socket,
                    context.RequestAborted);
                logger.LogInformation(
                    "api-socket registered for LoopUpdated push keyCount={KeyCount} pendingDrained={PendingDrained}",
                    robotKeys.Count,
                    drained);
            }

            Task<ReceivedSocketMessage>? pendingReceive = null;
            var watchdogDelay = Task.Delay(TurnWatchdogInterval, connectionLifetime.Token);
            while (socket.State == WebSocketState.Open)
            {
                connectionLifetime.Token.ThrowIfCancellationRequested();
                ReceivedSocketMessage received;
                try
                {
                    pendingReceive ??= ReceiveAsync(socket, connectionLifetime.Token);
                    var completedTask = await Task.WhenAny(pendingReceive, watchdogDelay);
                    if (completedTask != pendingReceive)
                    {
                        watchdogDelay = Task.Delay(TurnWatchdogInterval, connectionLifetime.Token);
                        await GuardConnectionLeaseAsync(connectionLifetime.Token, idle: true);
                        var idleEnvelope = CreateEnvelope(context, kind, token, connectionId);
                        var idleReplies = await webSocketService.HandleIdleAsync(
                            session,
                            idleEnvelope,
                            connectionLifetime.Token);
                        if (idleReplies.Count > 0)
                        {
                            logger.LogInformation(
                                "WebSocket turn watchdog reply batch ready connectionId={ConnectionId} kind={Kind} replyCount={ReplyCount}",
                                connectionId,
                                kind,
                                idleReplies.Count);
                            await SendRepliesAsync(socket, idleReplies, kind, transportMetrics,
                                connectionLifetime.Token, GuardLeaseAsync);
                            await telemetrySink.RecordOutboundAsync(
                                idleEnvelope,
                                session,
                                idleReplies,
                                connectionLifetime.Token);
                        }
                        continue;
                    }

                    received = await pendingReceive;
                    pendingReceive = null;
                    logger.LogDebug(
                        "WebSocket frame received connectionId={ConnectionId} kind={Kind} messageType={MessageType} bytes={Bytes}",
                        connectionId,
                        kind,
                        received.MessageType,
                        received.Buffer.Length);
                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", connectionLifetime.Token);
                        logger.LogInformation("WebSocket close frame received connectionId={ConnectionId} kind={Kind}",
                            connectionId, kind);
                        break;
                    }
                }
                catch (WebSocketException exception)
                {
                    if (exception.WebSocketErrorCode != WebSocketError.ConnectionClosedPrematurely) throw;
                    isPrematureClose = true;
                    logger.LogDebug(exception,
                        "WebSocket connection closed prematurely connectionId={ConnectionId} kind={Kind}",
                        connectionId, kind);
                    break;
                }

                await GuardLeaseAsync(connectionLifetime.Token);
                var envelope = CreateEnvelope(
                    context,
                    kind,
                    token,
                    connectionId,
                    received.MessageType == WebSocketMessageType.Text ? Encoding.UTF8.GetString(received.Buffer) : null,
                    received.MessageType == WebSocketMessageType.Binary ? received.Buffer : null);
                var inboundMessageClass = SocketMessageTypeReader.Read(envelope.Text);
                transportMetrics.WebSocketMessage(
                    "in",
                    kind,
                    received.MessageType == WebSocketMessageType.Binary ? "binary-audio" : "text-json",
                    inboundMessageClass,
                    received.Buffer.Length);

                IReadOnlyList<WebSocketReply> replies;
                using (AmbientTurnProgressPublisher.Begin(
                           (reply, cancellationToken) => SendRepliesAsync(socket, [reply], kind, transportMetrics,
                               cancellationToken, GuardLeaseAsync)))
                {
                    replies = await webSocketService.HandleMessageAsync(envelope, connectionLifetime.Token);
                }

                robotPresenceRegistry.UpdateRobotKeys(presenceConnectionId, ResolveRobotKeys(token, session));
                if (!string.IsNullOrWhiteSpace(session.TurnState.TransId))
                    loopTransId = session.TurnState.TransId;
                logger.LogDebug(
                    "WebSocket reply batch ready connectionId={ConnectionId} kind={Kind} sessionId={SessionId} " +
                    "deviceId={DeviceId} messageType={MessageType} replyCount={ReplyCount}",
                    connectionId,
                    kind,
                    session.SessionId,
                    session.DeviceId,
                    SocketMessageTypeReader.Read(envelope.Text),
                    replies.Count);
                await telemetrySink.RecordInboundAsync(envelope, session, SocketMessageTypeReader.Read(envelope.Text),
                    connectionLifetime.Token);
                await SendRepliesAsync(socket, replies, kind, transportMetrics, connectionLifetime.Token, GuardLeaseAsync);

                await telemetrySink.RecordOutboundAsync(envelope, session, replies, connectionLifetime.Token);
            }
        }
        catch (OperationCanceledException) when (connectionLifetime.IsCancellationRequested)
        {
        }
        catch (WebSocketException) when (connectionLifetime.IsCancellationRequested)
        {
        }
        finally
        {
            connectionLifetime.Cancel();
            credentialFailure = await continuity;
            await leaseMonitor;
            if (registeredApiSocket)
                robotNotificationRegistry.Remove(socket);
            if (presenceConnectionId != Guid.Empty) robotPresenceRegistry.Remove(presenceConnectionId);
            using (var gateLease = AcquireSessionGate(sessionKey))
            {
                lock (gateLease.Gate.SyncRoot)
                {
                    var ownsLocalState = session.Metadata.TryGetValue(ownershipKey, out var localOwner) &&
                        string.Equals(localOwner?.ToString(), connectionId, StringComparison.Ordinal);
                    if (ownsLocalState)
                    {
                        // The leased channels share process-local session state. Proactive
                        // teardown must not release listen audio. CloseSession only removes
                        // this process's active registry entry; it does not revoke durable
                        // tokens or mutate the lease rows owned by another replica.
                        if (channelLease is null || kind == "neo-hub-listen")
                            WebSocketTurnFinalizationService.ReleaseBufferedAudio(session);
                        session.Metadata.Remove(ownershipKey);
                        if (channelLease is null)
                        {
                            sessionStateOwnedOnClose = true;
                            cloudStateStore.CloseSession(session.SessionId);
                        }
                        else if (!session.Metadata.ContainsKey(ActiveConnectionIdMetadataKey) &&
                                 !session.Metadata.ContainsKey(ActiveConnectionIdMetadataKey + ".neo-hub-listen") &&
                                 !session.Metadata.ContainsKey(ActiveConnectionIdMetadataKey + ".neo-hub-proactive"))
                        {
                            cloudStateStore.CloseSession(session.SessionId);
                        }
                    }
                }
            }
            transportMetrics.WebSocketConnectionClosed(kind);
        }

        var closeEnvelope = CreateEnvelope(context, kind, token, connectionId);
        if (isPrematureClose && sessionStateOwnedOnClose && credentialFailure is null)
            webSocketService.MarkPrematureSocketLoopEnded(session, loopTransId);

        var continuityFailure = credentialFailure ?? channelLease?.FailureReason;
        var closeReason = continuityFailure is not null
            ? $"socket-loop-ended-{continuityFailure}"
            : $"socket-loop-ended{(isPrematureClose ? "-prematurely" : string.Empty)}";
        await telemetrySink.RecordConnectionClosedAsync(closeEnvelope, session, closeReason, context.RequestAborted);
        logger.LogInformation(
            "WebSocket connection closed connectionId={ConnectionId} kind={Kind} sessionId={SessionId} " +
            "deviceId={DeviceId} tokenFingerprint={TokenFingerprint} prematureClose={PrematureClose} " +
            "credentialContinuity={CredentialContinuity}",
            connectionId,
            kind,
            session.SessionId,
            session.DeviceId,
            tokenFingerprint,
            isPrematureClose,
            continuityFailure ?? "not-failed");
    }

    private HashSet<string> ResolveRobotKeys(string? token, CloudSession session)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(token))
            keys.Add(token.Trim());

        // Stock/OpenJibo tokens look like token-{friendlyOrDeviceId}-{suffix}. Portal keys on
        // friendlyId, so extract that segment so LoopUpdated push can find this socket.
        if (TryParseRobotIdFromToken(token, out var tokenRobotId))
        {
            keys.Add(tokenRobotId);
            var tokenDevice = cloudStateStore.FindDeviceByFriendlyId(tokenRobotId);
            if (tokenDevice is not null)
            {
                if (!string.IsNullOrWhiteSpace(tokenDevice.DeviceId))
                    keys.Add(tokenDevice.DeviceId.Trim());
                if (!string.IsNullOrWhiteSpace(tokenDevice.RobotId))
                    keys.Add(tokenDevice.RobotId.Trim());
                if (!string.IsNullOrWhiteSpace(tokenDevice.FriendlyName))
                    keys.Add(tokenDevice.FriendlyName.Trim());
            }
        }

        var deviceId = session.DeviceId;
        if (string.IsNullOrWhiteSpace(deviceId) && !string.IsNullOrWhiteSpace(token))
            deviceId = cloudStateStore.FindSessionByToken(token)?.DeviceId;

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            keys.Add(deviceId.Trim());
            var device = cloudStateStore.FindDeviceByFriendlyId(deviceId);
            if (device is not null)
            {
                if (!string.IsNullOrWhiteSpace(device.DeviceId))
                    keys.Add(device.DeviceId.Trim());
                if (!string.IsNullOrWhiteSpace(device.RobotId))
                    keys.Add(device.RobotId.Trim());
                if (!string.IsNullOrWhiteSpace(device.FriendlyName))
                    keys.Add(device.FriendlyName.Trim());
            }
        }

        foreach (var identityValue in GetSessionIdentityValues(session))
        {
            keys.Add(identityValue);
            var registeredDevice = cloudStateStore.FindDeviceByFriendlyId(identityValue);
            if (registeredDevice is null)
                continue;

            if (!string.IsNullOrWhiteSpace(registeredDevice.DeviceId))
                keys.Add(registeredDevice.DeviceId.Trim());
            if (!string.IsNullOrWhiteSpace(registeredDevice.RobotId))
                keys.Add(registeredDevice.RobotId.Trim());
            if (!string.IsNullOrWhiteSpace(registeredDevice.FriendlyName))
                keys.Add(registeredDevice.FriendlyName.Trim());
        }

        return keys;
    }

    private static IEnumerable<string> GetSessionIdentityValues(CloudSession session)
    {
        var values = new[]
        {
            session.DeviceId,
            ReadSessionMetadata(session, "registeredDeviceId"),
            ReadSessionMetadata(session, "registeredRobotId"),
            ReadSessionMetadata(session, "robotID"),
            ReadSessionMetadata(session, "robotId"),
            ReadSessionMetadata(session, "robotFriendlyId"),
            ReadSessionMetadata(session, "friendlyId"),
            ReadSessionMetadata(session, "deviceId")
        };

        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                yield return value.Trim();
        }
    }

    private static string? ReadSessionMetadata(CloudSession session, string key)
    {
        return session.Metadata.TryGetValue(key, out var value) ? value?.ToString() : null;
    }

    /// <summary>
    /// Parses <c>token-{robotId}-{suffix}</c> (stock and OpenJibo IssueRobotToken shapes).
    /// Hyphenated friendlyIds are preserved; the final numeric/guid segment is the suffix.
    /// </summary>
    internal static bool TryParseRobotIdFromToken(string? token, out string robotId)
    {
        robotId = string.Empty;
        if (string.IsNullOrWhiteSpace(token)) return false;

        var trimmed = token.Trim();
        if (!trimmed.StartsWith("token-", StringComparison.OrdinalIgnoreCase))
            return false;

        var body = trimmed["token-".Length..];
        if (string.IsNullOrWhiteSpace(body)) return false;

        var lastDash = body.LastIndexOf('-');
        if (lastDash <= 0)
        {
            robotId = body;
            return !string.IsNullOrWhiteSpace(robotId);
        }

        var suffix = body[(lastDash + 1)..];
        // OpenJibo uses Guid:N; stock often uses a unix-ms timestamp. Either way the robot id
        // is everything before the final segment.
        if (suffix.Length >= 6 && suffix.All(static c => char.IsAsciiHexDigit(c) || char.IsDigit(c)))
        {
            robotId = body[..lastDash];
            return !string.IsNullOrWhiteSpace(robotId);
        }

        robotId = body;
        return true;
    }

    private static WebSocketMessageEnvelope CreateEnvelope(
        HttpContext context,
        string kind,
        string? token,
        string connectionId,
        string? text = null,
        byte[]? binary = null)
    {
        return new WebSocketMessageEnvelope
        {
            ConnectionId = connectionId,
            HostName = context.Request.Host.Host,
            Path = RequestLogSanitizer.RedactWebSocketPath(kind, context.Request.Path),
            Kind = kind,
            Token = token,
            Text = text,
            Binary = binary
        };
    }

    private static string Fingerprint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "none";

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    private static async Task<ReceivedSocketMessage> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();

        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return new ReceivedSocketMessage(result.MessageType, ms.ToArray());
    }

    internal static async Task SendRepliesAsync(
        WebSocket socket,
        IReadOnlyList<WebSocketReply> replies,
        string socketKind,
        ITransportMetrics transportMetrics,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforeSend = null)
    {
        foreach (var reply in replies)
        {
            if (string.IsNullOrWhiteSpace(reply.Text)) continue;
            if (reply.DelayMs > 0) await Task.Delay(reply.DelayMs, cancellationToken);

            if (beforeSend is not null) await beforeSend(cancellationToken);
            var payload = Encoding.UTF8.GetBytes(reply.Text);
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
            transportMetrics.WebSocketMessage("out", socketKind, "text-json",
                SocketMessageTypeReader.Read(reply.Text), payload.Length);
        }
    }

    internal static bool OwnsSessionState(CloudSession session, string connectionId)
    {
        return session.Metadata.TryGetValue(ActiveConnectionIdMetadataKey, out var owner) &&
               string.Equals(owner?.ToString(), connectionId, StringComparison.Ordinal);
    }

    private static string ResolveSessionKey(WebSocketMessageEnvelope envelope)
    {
        var token = envelope.Token?.Trim();
        if (string.IsNullOrWhiteSpace(token) ||
            token.Equals("v1/listen", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("listen", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("v1/proactive", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("proactive", StringComparison.OrdinalIgnoreCase))
            return $"conn:{envelope.ConnectionId}";

        return token;
    }

    private static bool TryResolveRequiredTokenKind(string socketKind, out string tokenKind)
    {
        tokenKind = socketKind switch
        {
            "api-socket" => "robot",
            "neo-hub-listen" or "neo-hub-proactive" => "hub",
            _ => string.Empty
        };

        return tokenKind.Length > 0;
    }

    private static SessionGateLease AcquireSessionGate(string sessionKey)
    {
        lock (SessionGateRegistryLock)
        {
            if (!SessionGates.TryGetValue(sessionKey, out var gate))
            {
                gate = new SessionGate();
                SessionGates[sessionKey] = gate;
            }

            gate.ReferenceCount += 1;
            return new SessionGateLease(sessionKey, gate);
        }
    }

    private sealed class SessionGate
    {
        public object SyncRoot { get; } = new();

        public int ReferenceCount { get; set; }
    }

    private sealed class SessionGateLease(string sessionKey, SessionGate gate) : IDisposable
    {
        public SessionGate Gate { get; } = gate;

        public void Dispose()
        {
            lock (SessionGateRegistryLock)
            {
                Gate.ReferenceCount -= 1;
                if (Gate.ReferenceCount == 0 && SessionGates.TryGetValue(sessionKey, out var current) &&
                    ReferenceEquals(current, Gate))
                    SessionGates.Remove(sessionKey);
            }
        }
    }

    private sealed record ReceivedSocketMessage(WebSocketMessageType MessageType, byte[] Buffer);
}
