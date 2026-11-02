using System.Net;
using System.Net.WebSockets;
using System.Text;
using Jibo.Cloud.Api.Hosting;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Content;
using Jibo.Cloud.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jibo.Cloud.Tests.Api;

public sealed class WebSocketRequestCoordinatorTests
{
    [Theory]
    [InlineData("unbound", StatusCodes.Status401Unauthorized)]
    [InlineData("invalid-config", StatusCodes.Status503ServiceUnavailable)]
    [InlineData("store-failure", StatusCodes.Status503ServiceUnavailable)]
    [InlineData("binding-invalid", StatusCodes.Status401Unauthorized)]
    [InlineData("binding-unavailable", StatusCodes.Status503ServiceUnavailable)]
    public async Task EnabledLeaseProfileRejectsIneligibleOrUnavailableHandshakeBeforeAccept(string scenario, int status)
    {
        var service = CreateWebSocketService(out var store);
        var token = IssueLeaseBoundToken(store, scenario != "unbound");
        var leaseStore = HubChannelLeasePolicyTests.WorkingStore();
        if (scenario == "store-failure")
            leaseStore.Setup(s => s.AcquireAsync(It.IsAny<byte[]>(), It.IsAny<HubChannel>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("private-connection-data"));
        using var policy = scenario == "invalid-config" ? HubChannelLeasePolicy.Unavailable :
            new HubChannelLeasePolicy(leaseStore.Object, new byte[32]);
        ICloudStateStore authoritativeStore = store;
        if (scenario.StartsWith("binding-", StringComparison.Ordinal))
        {
            var observed = new Mock<ICloudStateStore>();
            observed.Setup(s => s.FindIssuedToken(token)).Returns(() => store.FindIssuedToken(token));
            var check = observed.Setup(s => s.IsHubTokenCredentialBindingCurrentAsync(token,
                It.IsAny<HubTokenCredentialBinding>(), It.IsAny<CancellationToken>()));
            if (scenario == "binding-invalid") check.ReturnsAsync(false);
            else check.ThrowsAsync(new InvalidOperationException("private-provider-details"));
            authoritativeStore = observed.Object;
        }
        var socket = new FakeWebSocket(); var context = LeaseContext(socket, token, "/listen");
        var coordinator = new WebSocketRequestCoordinator(service,
            new HomeAssistantWebSocketHandler(new HomeAssistantConnectionRegistry()), new RecordingWebSocketTelemetrySink(), authoritativeStore)
        { LeasePolicy = policy };
        await coordinator.HandleAsync(context);
        Assert.False(socket.Accepted); Assert.Equal(status, context.Response.StatusCode);
        Assert.Null(store.FindActiveSessionByToken(token));
        if (scenario.StartsWith("binding-", StringComparison.Ordinal))
            leaseStore.Verify(s => s.AcquireAsync(It.IsAny<byte[]>(), It.IsAny<HubChannel>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("/listen")]
    [InlineData("/v1/proactive")]
    public async Task LeaseLossAbortsAnIdleReceive_AndReleasesItsLastLocalSession(string path)
    {
        var service = CreateWebSocketService(out var store); var token = IssueLeaseBoundToken(store);
        var leaseStore = HubChannelLeasePolicyTests.WorkingStore(); var renewals = 0;
        leaseStore.Setup(s => s.TryRenewAsync(It.IsAny<byte[]>(), It.IsAny<HubChannel>(), It.IsAny<Guid>(), 1, It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult<HubChannelLease?>(Interlocked.Increment(ref renewals) == 1
                ? new HubChannelLease(1, DateTimeOffset.UtcNow.AddSeconds(30)) : null));
        using var policy = new HubChannelLeasePolicy(leaseStore.Object, new byte[32]);
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var socket = new FakeWebSocket(new FakeWebSocketFrame(WebSocketMessageType.Close, []))
        {
            PauseBeforeReceiveNumber = 1, ReceiveGate = new(TaskCreationOptions.RunContinuationsAsynchronously), FirstFrameReceived = received
        };
        var telemetry = new RecordingWebSocketTelemetrySink();
        var coordinator = new WebSocketRequestCoordinator(service,
            new HomeAssistantWebSocketHandler(new HomeAssistantConnectionRegistry()), telemetry, store)
        { LeasePolicy = policy, LeaseRenewalInterval = TimeSpan.FromMilliseconds(10) };
        var handling = coordinator.HandleAsync(LeaseContext(socket, token, path));
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5)); await handling.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WebSocketState.Aborted, socket.State); Assert.Empty(socket.SentPayloads);
        Assert.DoesNotContain(telemetry.Events, e => e.StartsWith("inbound:", StringComparison.Ordinal));
        Assert.Contains("closed:socket-loop-ended-lease-invalid", telemetry.Events);
        Assert.Null(store.FindActiveSessionByToken(token));
        leaseStore.Verify(s => s.TryReleaseAsync(It.IsAny<byte[]>(), It.IsAny<HubChannel>(), It.IsAny<Guid>(), 1, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StaleLeaseCannotProcessAnInboundFrame()
    {
        var service = CreateWebSocketService(out var store); var token = IssueLeaseBoundToken(store);
        var leaseStore = HubChannelLeasePolicyTests.WorkingStore();
        leaseStore.Setup(s => s.IsCurrentAsync(It.IsAny<byte[]>(), It.IsAny<HubChannel>(), It.IsAny<Guid>(), 1, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        using var policy = new HubChannelLeasePolicy(leaseStore.Object, new byte[32]);
        var socket = new FakeWebSocket(new FakeWebSocketFrame(WebSocketMessageType.Text,
            Encoding.UTF8.GetBytes("{\"type\":\"LISTEN\",\"transID\":\"must-not-process\"}")));
        var telemetry = new RecordingWebSocketTelemetrySink();
        var coordinator = new WebSocketRequestCoordinator(service,
            new HomeAssistantWebSocketHandler(new HomeAssistantConnectionRegistry()), telemetry, store) { LeasePolicy = policy };
        await coordinator.HandleAsync(LeaseContext(socket, token, "/listen"));
        Assert.Equal(WebSocketState.Aborted, socket.State); Assert.Empty(socket.SentPayloads);
        Assert.False(telemetry.ClosedSession!.TurnState.SawListen);
        Assert.Null(store.FindActiveSessionByToken(token));
        Assert.DoesNotContain(telemetry.Events, e => e.StartsWith("inbound:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChannelTeardownPreservesOtherConnection_AndLastCloseReleasesLocalSession(bool closeProactiveFirst)
    {
        var service = CreateWebSocketService(out var store); var token = IssueLeaseBoundToken(store);
        var leaseStore = HubChannelLeasePolicyTests.WorkingStore();
        using var policy = new HubChannelLeasePolicy(leaseStore.Object, new byte[32]);
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listen = new FakeWebSocket(new FakeWebSocketFrame(WebSocketMessageType.Close, []))
        { PauseBeforeReceiveNumber = 1, ReceiveGate = gate, FirstFrameReceived = received };
        var listenCoordinator = new WebSocketRequestCoordinator(service,
            new HomeAssistantWebSocketHandler(new HomeAssistantConnectionRegistry()), new RecordingWebSocketTelemetrySink(), store) { LeasePolicy = policy };
        var listening = listenCoordinator.HandleAsync(LeaseContext(listen, token, closeProactiveFirst ? "/listen" : "/v1/proactive"));
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var shared = store.FindActiveSessionByToken(token)!; shared.TurnState.BufferedAudioBytes = 4096;
        var proactive = new FakeWebSocket(new FakeWebSocketFrame(WebSocketMessageType.Close, []));
        var proactiveCoordinator = new WebSocketRequestCoordinator(service,
            new HomeAssistantWebSocketHandler(new HomeAssistantConnectionRegistry()), new RecordingWebSocketTelemetrySink(), store) { LeasePolicy = policy };
        await proactiveCoordinator.HandleAsync(LeaseContext(proactive, token, closeProactiveFirst ? "/v1/proactive" : "/listen"));
        Assert.Same(shared, store.FindActiveSessionByToken(token)); Assert.Equal(closeProactiveFirst ? 4096 : 0, shared.TurnState.BufferedAudioBytes);
        Assert.Equal(WebSocketState.Open, listen.State);
        gate.SetResult(true); await listening.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, shared.TurnState.BufferedAudioBytes);
        Assert.Null(store.FindActiveSessionByToken(token)); // local entry is released only after the last channel
        Assert.NotNull(store.FindIssuedToken(token)); // local closure does not revoke the bearer
    }

    [Fact]
    public async Task OldLocalConnectionFinishingAfterReplacementCannotClearSuccessorSessionOrAudio()
    {
        var service = CreateWebSocketService(out var store); var token = IssueLeaseBoundToken(store);
        var leaseStore = HubChannelLeasePolicyTests.WorkingStore();
        using var policy = new HubChannelLeasePolicy(leaseStore.Object, new byte[32]);
        var oldReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldSocket = new FakeWebSocket(new FakeWebSocketFrame(WebSocketMessageType.Close, []))
        { PauseBeforeReceiveNumber = 1, ReceiveGate = oldGate, FirstFrameReceived = oldReceived };
        var newSocket = new FakeWebSocket(new FakeWebSocketFrame(WebSocketMessageType.Close, []))
        { PauseBeforeReceiveNumber = 1, ReceiveGate = newGate, FirstFrameReceived = newReceived };
        WebSocketRequestCoordinator Coordinator() => new(service,
            new HomeAssistantWebSocketHandler(new HomeAssistantConnectionRegistry()), new RecordingWebSocketTelemetrySink(), store)
        { LeasePolicy = policy };
        var oldHandling = Coordinator().HandleAsync(LeaseContext(oldSocket, token, "/listen"));
        await oldReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var newHandling = Coordinator().HandleAsync(LeaseContext(newSocket, token, "/listen"));
        await newReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var shared = store.FindActiveSessionByToken(token)!; shared.TurnState.BufferedAudioBytes = 4096;
        oldGate.SetResult(true); await oldHandling.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(shared, store.FindActiveSessionByToken(token)); Assert.Equal(4096, shared.TurnState.BufferedAudioBytes);
        newGate.SetResult(true); await newHandling.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(store.FindActiveSessionByToken(token)); Assert.Equal(0, shared.TurnState.BufferedAudioBytes);
    }

    [Fact]
    public async Task EveryReplyChecksLeaseAgainBeforeSend()
    {
        var leaseStore = HubChannelLeasePolicyTests.WorkingStore(); var checks = 0;
        leaseStore.Setup(s => s.IsCurrentAsync(It.IsAny<byte[]>(), It.IsAny<HubChannel>(), It.IsAny<Guid>(), 1, It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(Interlocked.Increment(ref checks) == 1));
        using var policy = new HubChannelLeasePolicy(leaseStore.Object, new byte[32]);
        await using var lease = (await policy.AcquireAsync("opaque", HubChannel.Listen, default))!;
        var socket = new FakeWebSocket();
        async Task Guard(CancellationToken cancellation)
        { if (!await lease.CheckAsync(cancellation)) throw new OperationCanceledException(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WebSocketRequestCoordinator.SendRepliesAsync(socket,
            [new WebSocketReply { Text = "first" }, new WebSocketReply { Text = "second", DelayMs = 1 }],
            "neo-hub-listen", NullTransportMetrics.Instance, default, Guard));
        Assert.Single(socket.SentPayloads); Assert.Equal("first", Encoding.UTF8.GetString(socket.SentPayloads[0]));
        Assert.Equal(2, checks);
    }

    private static string IssueLeaseBoundToken(InMemoryCloudStateStore store, bool bound = true) =>
        store.IssueHubToken("lease-robot", useDefaultRobot: false, credentialBinding: bound
            ? new HubTokenCredentialBinding(AwsSigV4RequestVerifier.CreateAccessKeyFingerprint(store.GetAccount().AccessKeyId), DateTimeOffset.UtcNow, false) : null);

    private static DefaultHttpContext LeaseContext(FakeWebSocket socket, string token, string path)
    {
        var context = CreateContext(socket); context.Request.Host = new HostString("neo-hub.jibo.com");
        context.Request.Path = path; context.Request.Headers.Authorization = "Bearer " + token; return context;
    }

    [Fact]
    public async Task UnboundCompatibilitySocket_DoesNotStartCredentialMonitor()
    {
        var service = CreateWebSocketService(out var store);
        var token = store.IssueHubToken("compatibility-robot", useDefaultRobot: false);
        var observedStore = new Mock<ICloudStateStore>();
        observedStore.Setup(s => s.FindIssuedToken(token)).Returns(() => store.FindIssuedToken(token));
        var socket = new FakeWebSocket(new FakeWebSocketFrame(WebSocketMessageType.Close, []));
        var context = CreateContext(socket);
        context.Request.Host = new HostString("neo-hub.jibo.com");
        context.Request.Path = "/listen";
        context.Request.Headers.Authorization = "Bearer " + token;
        var coordinator = new WebSocketRequestCoordinator(service,
            new HomeAssistantWebSocketHandler(new HomeAssistantConnectionRegistry()),
            new RecordingWebSocketTelemetrySink(), observedStore.Object);

        await coordinator.HandleAsync(context);

        Assert.True(socket.Accepted);
        Assert.Equal(WebSocketState.Closed, socket.State);
        observedStore.Verify(s => s.IsHubTokenCredentialBindingCurrentAsync(It.IsAny<string>(),
            It.IsAny<HubTokenCredentialBinding>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("/listen", "remove")]
    [InlineData("/v1/proactive", "remove")]
    [InlineData("/listen", "replace")]
    [InlineData("/v1/proactive", "replace")]
    public async Task BoundIdleSocket_AbortsAndReleasesSessionWhenOriginalBindingChanges(string path, string change)
    {
        var receiveEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var socket = new FakeWebSocket(new FakeWebSocketFrame(WebSocketMessageType.Text, Encoding.UTF8.GetBytes("{}")))
        {
            PauseBeforeReceiveNumber = 1,
            ReceiveGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            FirstFrameReceived = receiveEntered
        };
        var context = CreateContext(socket);
        context.Request.Host = new HostString("neo-hub.jibo.com");
        context.Request.Path = path;
        var service = CreateWebSocketService(out var store);
        var telemetry = new RecordingWebSocketTelemetrySink();
        var coordinator = new WebSocketRequestCoordinator(service,
            new HomeAssistantWebSocketHandler(new HomeAssistantConnectionRegistry()), telemetry, store)
        { CredentialRevalidationInterval = TimeSpan.FromMilliseconds(10) };
        var account = store.GetAccount();
        var binding = new HubTokenCredentialBinding(
            AwsSigV4RequestVerifier.CreateAccessKeyFingerprint(account.AccessKeyId), DateTimeOffset.UtcNow, false);
        var token = store.IssueHubToken("bound-robot", useDefaultRobot: false, credentialBinding: binding);
        context.Request.Headers.Authorization = "Bearer " + token;
        var issued = store.FindIssuedToken(token)!;
        var handling = coordinator.HandleAsync(context);
        await receiveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        if (change == "remove") issued.Metadata.Clear();
        else new HubTokenCredentialBinding(binding.CredentialFingerprint, binding.SignedAt.AddSeconds(1), true)
            .WriteTo(issued.Metadata);

        await handling.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WebSocketState.Aborted, socket.State);
        Assert.Empty(socket.SentPayloads);
        Assert.DoesNotContain(telemetry.Events, e => e.StartsWith("inbound:", StringComparison.Ordinal));
        Assert.Contains("closed:socket-loop-ended-credential-invalid", telemetry.Events);
        Assert.Null(store.FindActiveSessionByToken(token));
    }

    [Theory]
    [InlineData("openjibo.com", "/", StatusCodes.Status404NotFound)]
    [InlineData("api.openjibo.com", "/", StatusCodes.Status404NotFound)]
    [InlineData("openjibo.ai", "/unexpected", StatusCodes.Status401Unauthorized)]
    public async Task HandleAsync_RejectsGenericOpenJiboWebSocketRoutes(string host, string path, int expectedStatus)
    {
        var socket = new FakeWebSocket();
        var context = CreateContext(socket);
        context.Request.Host = new HostString(host);
        context.Request.Path = path;
        context.Request.Headers.Authorization = "Bearer attacker-supplied-token";
        var coordinator = CreateCoordinator(out var telemetrySink);

        await coordinator.HandleAsync(context);

        Assert.Equal(expectedStatus, context.Response.StatusCode);
        Assert.False(socket.Accepted);
        Assert.Empty(telemetrySink.Events);
    }

    [Fact]
    public async Task HandleAsync_ReturnsUnauthorized_WhenNeoHubTokenIsMissing()
    {
        var socket = new FakeWebSocket();
        var context = CreateContext(socket);
        context.Request.Host = new HostString("neo-hub.jibo.com");
        context.Request.Path = "/";

        var coordinator = CreateCoordinator(out var telemetrySink);

        await coordinator.HandleAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(socket.Accepted);
        Assert.Empty(telemetrySink.Events);
    }

    [Theory]
    [InlineData("/listen")]
    [InlineData("/v1/proactive")]
    public async Task HandleAsync_AcceptsCredentialObservedHubToken(string path)
    {
        var socket = new FakeWebSocket(new FakeWebSocketFrame(WebSocketMessageType.Close, []));
        var context = CreateContext(socket);
        context.Request.Host = new HostString("neo-hub.jibo.com");
        context.Request.Path = path;
        var coordinator = CreateCoordinator(out var telemetrySink, out var store);
        var account = store.GetAccount();
        var token = store.IssueHubToken(
            "credential-observed-robot",
            useDefaultRobot: false,
            credentialBinding: new HubTokenCredentialBinding(
                AwsSigV4RequestVerifier.CreateAccessKeyFingerprint(account.AccessKeyId),
                DateTimeOffset.UtcNow,
                operationAuthenticated: false));
        context.Request.Headers.Authorization = $"Bearer {token}";

        await coordinator.HandleAsync(context);

        Assert.True(socket.Accepted);
        Assert.Equal(WebSocketState.Closed, socket.State);
        Assert.Contains("opened", telemetrySink.Events);
        Assert.NotNull(store.FindIssuedToken(token));
    }

    [Theory]
    [InlineData("neo-hub.jibo.com", true)]
    [InlineData("192.168.7.142", false)]
    public async Task HandleAsync_RejectsIssuedTokenWithWrongSocketKind(string host, bool useRobotToken)
    {
        var socket = new FakeWebSocket();
        var context = CreateContext(socket);
        context.Request.Host = new HostString(host);

        var coordinator = CreateCoordinator(out _, out var store);
        var token = useRobotToken
            ? store.IssueRobotToken("wrong-kind-robot")
            : store.IssueHubToken("wrong-kind-hub");
        if (host.Equals("neo-hub.jibo.com", StringComparison.OrdinalIgnoreCase))
        {
            context.Request.Path = "/listen";
            context.Request.Headers.Authorization = $"Bearer {token}";
        }
        else
        {
            context.Request.Path = $"/{token}";
        }

        await coordinator.HandleAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(socket.Accepted);
    }

    [Fact]
    public async Task HandleAsync_ExplicitSingleRobotHttpModeAcceptsPrivateTokenlessHubSocket()
    {
        var socket = new FakeWebSocket(new FakeWebSocketFrame(WebSocketMessageType.Close, []));
        var context = CreateContext(socket);
        context.Request.Host = new HostString("10.0.0.80");
        context.Request.Path = "/v1/listen";
        context.Request.Scheme = "http";
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.81");

        var service = CreateWebSocketService(out var store);
        var telemetrySink = new RecordingWebSocketTelemetrySink();
        var haHandler = new HomeAssistantWebSocketHandler(new HomeAssistantConnectionRegistry());
        var coordinator = new WebSocketRequestCoordinator(
            service,
            haHandler,
            telemetrySink,
            store,
            new SingleRobotHttpHubAccessGuard(true));

        await coordinator.HandleAsync(context);

        Assert.True(socket.Accepted);
        Assert.Equal(WebSocketState.Closed, socket.State);
    }

    [Fact]
    public async Task HandleAsync_ProcessesBinaryFrame_AndClosesCleanly()
    {
        var socket = new FakeWebSocket(
            new FakeWebSocketFrame(WebSocketMessageType.Binary, [1, 2, 3]),
            new FakeWebSocketFrame(WebSocketMessageType.Close, []));
        var context = CreateContext(socket);
        context.Request.Host = new HostString("neo-hub.jibo.com");
        context.Request.Path = "/listen";
        var coordinator = CreateCoordinator(out var telemetrySink, out var store);
        var token = store.IssueHubToken("robot-binary-frame");
        context.Request.Headers.Authorization = $"Bearer {token}";

        await coordinator.HandleAsync(context);

        Assert.True(socket.Accepted);
        Assert.Equal(WebSocketState.Closed, socket.State);
        Assert.Empty(socket.SentPayloads);
        Assert.Contains(telemetrySink.Events, eventName => eventName == "opened");
        Assert.Contains(telemetrySink.Events, eventName => eventName == "inbound:BINARY_OR_EMPTY");
        Assert.Contains(telemetrySink.Events, eventName => eventName == "outbound:0");
        Assert.Contains(telemetrySink.Events, eventName => eventName == "closed:socket-loop-ended");

        Assert.NotNull(store.FindIssuedToken(token));
        Assert.Null(store.FindActiveSessionByToken(token));
        Assert.NotNull(telemetrySink.ClosedSession);
        Assert.Equal(0, telemetrySink.ClosedSession.TurnState.BufferedAudioBytes);
        Assert.Empty(telemetrySink.ClosedSession.TurnState.BufferedAudioFrames);
    }

    [Fact]
    public async Task HandleAsync_PathTokenFrames_UseSingleConnectionScopedSession()
    {
        var listen =
            """{"type":"LISTEN","transID":"trans-path","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}""";
        var socket = new FakeWebSocket(
            new FakeWebSocketFrame(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(listen)),
            new FakeWebSocketFrame(WebSocketMessageType.Binary, [1, 2, 3, 4]),
            new FakeWebSocketFrame(WebSocketMessageType.Close, []));
        var context = CreateContext(socket);
        context.Request.Host = new HostString("192.168.7.142");
        var coordinator = CreateCoordinator(out var telemetrySink, out var store);
        var token = store.IssueHubToken("robot-path-token");
        context.Request.Path = $"/v1/listen/{token}";

        await coordinator.HandleAsync(context);

        Assert.True(socket.Accepted);
        Assert.NotNull(store.FindIssuedToken(token));
        Assert.Null(store.FindActiveSessionByToken(token));
        Assert.NotNull(telemetrySink.LastConnectionId);
        Assert.Null(store.FindSessionByToken($"conn:{telemetrySink.LastConnectionId}"));
        Assert.NotNull(telemetrySink.ClosedSession);
        Assert.Equal("trans-path", telemetrySink.ClosedSession.TurnState.TransId);
        Assert.True(telemetrySink.ClosedSession.TurnState.SawListen);
        Assert.Equal(0, telemetrySink.ClosedSession.TurnState.BufferedAudioBytes);
        Assert.Empty(telemetrySink.ClosedSession.TurnState.BufferedAudioFrames);
        Assert.Equal(telemetrySink.LastConnectionId, telemetrySink.FirstConnectionId);
    }

    [Fact]
    public async Task HandleAsync_ApiSocketDrainsPendingLoopUpdatedOnRegister()
    {
        var socket = new FakeWebSocket(new FakeWebSocketFrame(WebSocketMessageType.Close, []));
        var context = CreateContext(socket);
        context.Request.Host = new HostString("192.168.7.142");
        var service = CreateWebSocketService(out var store);
        var token = store.IssueRobotToken("Ghost-Instance-Onion-Silk");
        context.Request.Path = $"/{token}";
        var telemetrySink = new RecordingWebSocketTelemetrySink();
        var pendingStore = new RobotPendingNotificationStore();
        var registry = new RobotNotificationRegistry(pendingStore);
        var haHandler = new HomeAssistantWebSocketHandler(new HomeAssistantConnectionRegistry());
        var coordinator = new WebSocketRequestCoordinator(
            service,
            haHandler,
            telemetrySink,
            registry,
            store,
            NullLogger<WebSocketRequestCoordinator>.Instance);

        await registry.PushLoopUpdatedAsync(
            ["Ghost-Instance-Onion-Silk"],
            new { id = "loop-1", eventKey = "LoopUpdated" });

        await coordinator.HandleAsync(context);

        Assert.True(socket.Accepted);
        Assert.NotEmpty(socket.SentPayloads);
    }

    [Fact]
    public void ReplacedSocket_CannotOwnCleanupForNewerSharedSessionState()
    {
        var store = new InMemoryCloudStateStore();
        var session = store.OpenSession("neo-hub-listen", "robot-replaced", "shared-token",
            "neo-hub.jibo.com", "/listen");
        session.TurnState.TransId = "trans-b";
        session.TurnState.SawListen = true;
        session.TurnState.SawContext = true;
        session.TurnState.BufferedAudioBytes = 4096;
        session.Metadata["openjibo.activeWebSocketConnectionId"] = "connection-b";

        Assert.False(WebSocketRequestCoordinator.OwnsSessionState(session, "connection-a"));
        Assert.True(WebSocketRequestCoordinator.OwnsSessionState(session, "connection-b"));
        Assert.Equal("trans-b", session.TurnState.TransId);
        Assert.Equal(4096, session.TurnState.BufferedAudioBytes);
        Assert.NotNull(store.FindActiveSessionByToken("shared-token"));
    }

    [Fact]
    public async Task ReplacedSocket_OldPrematureCloseCannotClearNewSocketTurn()
    {
        var service = CreateWebSocketService(out var store);
        var token = store.IssueHubToken("robot-replaced-concurrency");
        var firstReceive = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOldSocket = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNewSocket = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldSocket = new FakeWebSocket(
            new FakeWebSocketFrame(WebSocketMessageType.Text,
                Encoding.UTF8.GetBytes(
                    "{\"type\":\"LISTEN\",\"transID\":\"trans-a\",\"data\":{\"hotphrase\":true,\"rules\":[\"launch\"]}}")));
        oldSocket.PauseBeforeReceiveNumber = 2;
        oldSocket.ReceiveGate = releaseOldSocket;
        oldSocket.ThrowPrematureOnNextReceive = true;
        oldSocket.FirstFrameReceived = firstReceive;
        var newSocket = new FakeWebSocket(
            new FakeWebSocketFrame(WebSocketMessageType.Text,
                Encoding.UTF8.GetBytes(
                    "{\"type\":\"LISTEN\",\"transID\":\"trans-b\",\"data\":{\"hotphrase\":true,\"rules\":[\"launch\"]}}")),
            new FakeWebSocketFrame(WebSocketMessageType.Text,
                Encoding.UTF8.GetBytes("{\"type\":\"CONTEXT\",\"transID\":\"trans-b\",\"data\":{\"skill\":{\"id\":null}}}")),
            new FakeWebSocketFrame(WebSocketMessageType.Binary, [1, 2, 3, 4]),
            new FakeWebSocketFrame(WebSocketMessageType.Close, []));
        newSocket.PauseBeforeReceiveNumber = 4;
        newSocket.ReceiveGate = releaseNewSocket;
        var oldContext = CreateContext(oldSocket);
        oldContext.Request.Host = new HostString("neo-hub.jibo.com");
        oldContext.Request.Path = "/listen";
        oldContext.Request.Headers.Authorization = $"Bearer {token}";
        var newContext = CreateContext(newSocket);
        newContext.Request.Host = new HostString("neo-hub.jibo.com");
        newContext.Request.Path = "/listen";
        newContext.Request.Headers.Authorization = $"Bearer {token}";
        var oldCoordinator = CreateCoordinator(service, store);
        var newCoordinator = CreateCoordinator(service, store);

        var oldTask = oldCoordinator.HandleAsync(oldContext);
        await firstReceive.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var newTask = newCoordinator.HandleAsync(newContext);
        await newSocket.BinaryFrameReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseOldSocket.TrySetResult(true);
        await oldTask.WaitAsync(TimeSpan.FromSeconds(5));

        var active = store.FindActiveSessionByToken(token);
        Assert.NotNull(active);
        Assert.Equal("trans-b", active.TurnState.TransId);
        Assert.True(active.TurnState.SawListen);
        Assert.True(active.TurnState.SawContext);
        Assert.Equal(4, active.TurnState.BufferedAudioBytes);

        releaseNewSocket.TrySetResult(true);
        await newSocket.CloseFrameReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await newTask.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Null(store.FindActiveSessionByToken(token));
        Assert.Equal(WebSocketState.Closed, newSocket.State);
    }

    private static WebSocketRequestCoordinator CreateCoordinator(out RecordingWebSocketTelemetrySink telemetrySink)
    {
        var service = CreateWebSocketService(out var store);
        telemetrySink = new RecordingWebSocketTelemetrySink();
        var haHandler = new HomeAssistantWebSocketHandler(new HomeAssistantConnectionRegistry());
        return new WebSocketRequestCoordinator(service, haHandler, telemetrySink, store);
    }

    private static WebSocketRequestCoordinator CreateCoordinator(
        out RecordingWebSocketTelemetrySink telemetrySink,
        out InMemoryCloudStateStore store)
    {
        var service = CreateWebSocketService(out store);
        telemetrySink = new RecordingWebSocketTelemetrySink();
        var haHandler = new HomeAssistantWebSocketHandler(new HomeAssistantConnectionRegistry());
        return new WebSocketRequestCoordinator(service, haHandler, telemetrySink, store);
    }

    private static WebSocketRequestCoordinator CreateCoordinator(
        JiboWebSocketService service,
        InMemoryCloudStateStore store)
    {
        var telemetrySink = new RecordingWebSocketTelemetrySink();
        var haHandler = new HomeAssistantWebSocketHandler(new HomeAssistantConnectionRegistry());
        return new WebSocketRequestCoordinator(service, haHandler, telemetrySink, store);
    }

    private static JiboWebSocketService CreateWebSocketService(out InMemoryCloudStateStore store)
    {
        store = new InMemoryCloudStateStore();
        var contentRepository = new InMemoryJiboExperienceContentRepository();
        var contentCache = new JiboExperienceContentCache(contentRepository);
        var conversationBroker = new DemoConversationBroker(new JiboInteractionService(contentCache,
            new LastItemRandomizer(), new InMemoryPersonalMemoryStore()));
        var sttSelector = new DefaultSttStrategySelector(
        [
            new SyntheticBufferedAudioSttStrategy()
        ]);
        var sink = new NullTurnTelemetrySink();

        return new JiboWebSocketService(
            store,
            new NullWebSocketTelemetrySink(),
            new WebSocketTurnFinalizationService(conversationBroker,
                sttSelector,
                sink));
    }

    private static DefaultHttpContext CreateContext(FakeWebSocket socket)
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpWebSocketFeature>(new FakeWebSocketFeature(socket));
        return context;
    }

    private sealed class RecordingWebSocketTelemetrySink : IWebSocketTelemetrySink
    {
        public List<string> Events { get; } = [];

        public string? FirstConnectionId { get; private set; }

        public string? LastConnectionId { get; private set; }

        public CloudSession? ClosedSession { get; private set; }

        public Task RecordConnectionOpenedAsync(WebSocketMessageEnvelope envelope, CloudSession session,
            CancellationToken cancellationToken = default)
        {
            Events.Add("opened");
            FirstConnectionId ??= envelope.ConnectionId;
            LastConnectionId = envelope.ConnectionId;
            return Task.CompletedTask;
        }

        public Task RecordInboundAsync(WebSocketMessageEnvelope envelope, CloudSession session, string? messageType,
            CancellationToken cancellationToken = default)
        {
            Events.Add($"inbound:{messageType}");
            FirstConnectionId ??= envelope.ConnectionId;
            LastConnectionId = envelope.ConnectionId;
            return Task.CompletedTask;
        }

        public Task RecordTurnEventAsync(WebSocketMessageEnvelope envelope, CloudSession session, string eventType,
            IReadOnlyDictionary<string, object?> details, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task RecordOutboundAsync(WebSocketMessageEnvelope envelope, CloudSession session,
            IReadOnlyList<WebSocketReply> replies, CancellationToken cancellationToken = default)
        {
            Events.Add($"outbound:{replies.Count}");
            return Task.CompletedTask;
        }

        public Task RecordConnectionClosedAsync(WebSocketMessageEnvelope envelope, CloudSession session, string reason,
            CancellationToken cancellationToken = default)
        {
            Events.Add($"closed:{reason}");
            ClosedSession = session;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWebSocketFeature(FakeWebSocket socket) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest { get; set; } = true;

        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context)
        {
            socket.Accepted = true;
            return Task.FromResult<WebSocket>(socket);
        }
    }

    private sealed class FakeWebSocket(params FakeWebSocketFrame[] frames) : WebSocket
    {
        private readonly Queue<FakeWebSocketFrame> _frames = new(frames);
        private WebSocketState _state = WebSocketState.Open;
        private int _receiveCount;

        public bool Accepted { get; set; }

        public List<byte[]> SentPayloads { get; } = [];

        public int PauseBeforeReceiveNumber { get; set; }

        public TaskCompletionSource<bool>? ReceiveGate { get; set; }

        public bool ThrowPrematureOnNextReceive { get; set; }

        public TaskCompletionSource<bool>? FirstFrameReceived { get; set; }

        public TaskCompletionSource<bool> BinaryFrameReceived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> CloseFrameReceived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override WebSocketCloseStatus? CloseStatus { get; } = null;

        public override string? CloseStatusDescription { get; } = null;

        // ReSharper disable once ConvertToAutoPropertyWithPrivateSetter
        public override WebSocketState State => _state;

        public override string? SubProtocol { get; } = null;

        public override void Abort()
        {
            _state = WebSocketState.Aborted;
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            _receiveCount += 1;
            if (_receiveCount == 1)
                FirstFrameReceived?.TrySetResult(true);
            if (_receiveCount == PauseBeforeReceiveNumber && ReceiveGate is not null)
                await ReceiveGate.Task.WaitAsync(cancellationToken);
            if (ThrowPrematureOnNextReceive)
            {
                ThrowPrematureOnNextReceive = false;
                throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
            }

            var frame = _frames.Dequeue();
            if (frame.Payload.Length > 0)
                Array.Copy(frame.Payload, 0, buffer.Array!, buffer.Offset, frame.Payload.Length);

            if (frame.MessageType == WebSocketMessageType.Binary)
                BinaryFrameReceived.TrySetResult(true);
            if (frame.MessageType == WebSocketMessageType.Close)
                CloseFrameReceived.TrySetResult(true);
            return new WebSocketReceiveResult(
                frame.Payload.Length,
                frame.MessageType,
                frame.EndOfMessage);
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
            CancellationToken cancellationToken)
        {
            SentPayloads.Add(buffer.ToArray());
            return Task.CompletedTask;
        }
    }

    private sealed record FakeWebSocketFrame(
        WebSocketMessageType MessageType,
        byte[] Payload,
        bool EndOfMessage = true);

    private sealed class LastItemRandomizer : IJiboRandomizer
    {
        public T Choose<T>(IReadOnlyList<T> items)
        {
            return items[^1];
        }
    }
}
