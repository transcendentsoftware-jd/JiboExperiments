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

namespace Jibo.Cloud.Tests.Api;

public sealed class WebSocketRequestCoordinatorTests
{
    [Fact]
    public async Task HandleAsync_AllowsNeoHubConnectionWithoutToken_UsingConnectionScopedSession()
    {
        var socket = new FakeWebSocket(new FakeWebSocketFrame(WebSocketMessageType.Close, []));
        var context = CreateContext(socket);
        context.Request.Host = new HostString("neo-hub.jibo.com");
        context.Request.Path = "/v1/listen";

        var coordinator = CreateCoordinator(out var telemetrySink);

        await coordinator.HandleAsync(context);

        Assert.True(socket.Accepted);
        Assert.Equal(WebSocketState.Closed, socket.State);
        Assert.Contains(telemetrySink.Events, eventName => eventName == "opened");
        Assert.Contains(telemetrySink.Events, eventName => eventName == "closed:socket-loop-ended");
    }

    [Fact]
    public async Task HandleAsync_RejectsMissingTokenOutsideHubFallbackPaths()
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

    [Fact]
    public async Task HandleAsync_ProcessesBinaryFrame_AndClosesCleanly()
    {
        var socket = new FakeWebSocket(
            new FakeWebSocketFrame(WebSocketMessageType.Binary, [1, 2, 3]),
            new FakeWebSocketFrame(WebSocketMessageType.Close, []));
        var context = CreateContext(socket);
        context.Request.Host = new HostString("neo-hub.jibo.com");
        context.Request.Path = "/listen";
        context.Request.Headers.Authorization = "Bearer test-token";

        var coordinator = CreateCoordinator(out var telemetrySink, out var store);

        await coordinator.HandleAsync(context);

        Assert.True(socket.Accepted);
        Assert.Equal(WebSocketState.Closed, socket.State);
        Assert.Empty(socket.SentPayloads);
        Assert.Contains(telemetrySink.Events, eventName => eventName == "opened");
        Assert.Contains(telemetrySink.Events, eventName => eventName == "inbound:BINARY_OR_EMPTY");
        Assert.Contains(telemetrySink.Events, eventName => eventName == "outbound:0");
        Assert.Contains(telemetrySink.Events, eventName => eventName == "closed:socket-loop-ended");

        Assert.Null(store.FindSessionByToken("test-token"));
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
        context.Request.Path = "/v1/listen/test-token";

        var coordinator = CreateCoordinator(out var telemetrySink, out var store);

        await coordinator.HandleAsync(context);

        Assert.True(socket.Accepted);
        Assert.Null(store.FindSessionByToken("test-token"));
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
        context.Request.Path = "/token-Ghost-Instance-Onion-Silk-123456";

        var service = CreateWebSocketService(out var store);
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
        var token = store.IssueRobotToken("robot-replaced-concurrency");
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
