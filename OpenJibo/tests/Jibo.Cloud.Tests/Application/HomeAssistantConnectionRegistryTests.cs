using System.Net.WebSockets;
using System.Text.Json;
using Jibo.Cloud.Application.Services;

namespace Jibo.Cloud.Tests.Application;

public sealed class HomeAssistantConnectionRegistryTests
{
    private const string TestLinkId = "link-test-1";
    private const string TestCommandSecret = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void TryGetPendingByCode_ReturnsRegistration()
    {
        var registry = new HomeAssistantConnectionRegistry();
        using var socket = new FakeWebSocket();

        var pending = registry.RegisterConnection("ha-instance-1", socket);

        var lookup = registry.TryGetPendingByCode(pending.Code);

        Assert.NotNull(lookup);
        Assert.Equal("ha-instance-1", lookup.InstanceId);
    }

    [Fact]
    public void RegisterPairedConnection_DoesNotCreatePendingVerificationCode()
    {
        var registry = new HomeAssistantConnectionRegistry();
        using var socket = new FakeWebSocket();

        registry.RegisterPairedConnection("ha-instance-1", socket);

        Assert.Null(registry.TryGetPendingByCode("ABCDEF"));
    }

    [Fact]
    public async Task SendCommandAsync_SendsSignedCommandPayload()
    {
        var registry = new HomeAssistantConnectionRegistry();
        using var socket = new CapturingWebSocket();
        registry.RegisterConnection("ha-instance-1", socket);

        var sent = await registry.SendCommandAsync(
            "ha-instance-1",
            TestLinkId,
            TestCommandSecret,
            "lights_off_named",
            new Dictionary<string, string> { ["targetName"] = "zanes" });

        Assert.True(sent);
        Assert.NotNull(socket.LastPayload);
        Assert.Equal("command", socket.LastPayload!.Value.GetProperty("type").GetString());
        Assert.Equal("lights_off_named", socket.LastPayload.Value.GetProperty("command").GetString());
        Assert.Equal("zanes", socket.LastPayload.Value.GetProperty("targetName").GetString());
        Assert.Equal(TestLinkId, socket.LastPayload.Value.GetProperty("linkId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(socket.LastPayload.Value.GetProperty("timestamp").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(socket.LastPayload.Value.GetProperty("nonce").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(socket.LastPayload.Value.GetProperty("signature").GetString()));

        var fields = PayloadToFields(socket.LastPayload.Value);
        Assert.True(HomeAssistantCommandAuthentication.Verify(
            fields,
            TestCommandSecret,
            fields["signature"]));
    }

    [Fact]
    public async Task SendCommandAsync_SendsMultiParameterPayload()
    {
        var registry = new HomeAssistantConnectionRegistry();
        using var socket = new CapturingWebSocket();
        registry.RegisterConnection("ha-instance-1", socket);

        var sent = await registry.SendCommandAsync(
            "ha-instance-1",
            TestLinkId,
            TestCommandSecret,
            "climate_set_temperature_named",
            new Dictionary<string, string>
            {
                ["targetName"] = "bedroom",
                ["temperature"] = "72"
            });

        Assert.True(sent);
        Assert.NotNull(socket.LastPayload);
        Assert.Equal("climate_set_temperature_named", socket.LastPayload!.Value.GetProperty("command").GetString());
        Assert.Equal("bedroom", socket.LastPayload.Value.GetProperty("targetName").GetString());
        Assert.Equal("72", socket.LastPayload.Value.GetProperty("temperature").GetString());
        Assert.False(string.IsNullOrWhiteSpace(socket.LastPayload.Value.GetProperty("signature").GetString()));
    }

    [Fact]
    public async Task SendCommandAsync_ReturnsFalse_WhenCommandSecretMissing()
    {
        var registry = new HomeAssistantConnectionRegistry();
        using var socket = new CapturingWebSocket();
        registry.RegisterConnection("ha-instance-1", socket);

        var sent = await registry.SendCommandAsync(
            "ha-instance-1",
            TestLinkId,
            "",
            "lights_off_named",
            new Dictionary<string, string> { ["targetName"] = "zanes" });

        Assert.False(sent);
        Assert.Null(socket.LastPayload);
    }

    [Fact]
    public async Task SendPairedAsync_IncludesCommandSecret()
    {
        var registry = new HomeAssistantConnectionRegistry();
        using var socket = new CapturingWebSocket();
        registry.RegisterConnection("ha-instance-1", socket);

        var delivered = await registry.SendPairedAsync(
            "ha-instance-1",
            "Ghost-Instance-Onion-Silk",
            TestLinkId,
            TestCommandSecret);

        Assert.True(delivered);
        Assert.NotNull(socket.LastPayload);
        Assert.Equal("paired", socket.LastPayload!.Value.GetProperty("type").GetString());
        Assert.Equal(TestLinkId, socket.LastPayload.Value.GetProperty("linkId").GetString());
        Assert.Equal(TestCommandSecret, socket.LastPayload.Value.GetProperty("commandSecret").GetString());
    }

    [Fact]
    public async Task CommandResult_IsBoundToItsOriginalSocketAndReplacementSurvivesOldCleanup()
    {
        var registry = new HomeAssistantConnectionRegistry();
        using var original = new CapturingWebSocket();
        using var unrelated = new CapturingWebSocket();
        registry.RegisterConnection("original-instance", original);
        var command = registry.SendCommandAndWaitAsync("original-instance", TestLinkId, TestCommandSecret, "lights_off");
        var requestId = original.LastPayload!.Value.GetProperty("requestId").GetString();
        var result = JsonSerializer.SerializeToElement(new { type = "command_result", requestId, status = "ok" });
        Assert.False(registry.TryCompleteCommandResult(result, "original-instance", unrelated));
        Assert.False(registry.TryCompleteCommandResult(result, "unrelated-instance", original));
        Assert.True(registry.TryCompleteCommandResult(result, "original-instance", original));
        Assert.NotNull(await command);
        registry.RegisterConnection("original-instance", unrelated);
        registry.RemoveConnection("original-instance", original);
        Assert.True(registry.IsCurrentConnection("original-instance", unrelated));
    }

    private static Dictionary<string, string> PayloadToFields(JsonElement payload)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in payload.EnumerateObject())
            fields[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? "",
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => property.Value.GetRawText()
            };
        return fields;
    }

    private sealed class CapturingWebSocket : WebSocket
    {
        public JsonElement? LastPayload { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;

        public override void Abort()
        {
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(buffer.Array!.AsMemory(buffer.Offset, buffer.Count));
            LastPayload = document.RootElement.Clone();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWebSocket : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;

        public override void Abort()
        {
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
