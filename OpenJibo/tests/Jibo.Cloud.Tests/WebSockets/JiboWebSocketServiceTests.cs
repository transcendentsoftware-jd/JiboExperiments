using System.Text.Json;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Persistence;

namespace Jibo.Cloud.Tests.WebSockets;

public sealed class JiboWebSocketServiceTests
{
    private readonly JiboWebSocketService _service;

    public JiboWebSocketServiceTests()
    {
        var store = new InMemoryCloudStateStore();
        _service = new JiboWebSocketService(
            store,
            new ProtocolToTurnContextMapper(),
            new DemoConversationBroker(),
            new ResponsePlanToSocketMessagesMapper());
    }

    [Fact]
    public async Task ListenMessage_ReturnsResponseAndEos()
    {
        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-test-token",
            Text = """{"type":"LISTEN","data":{"text":"hello jibo"}}"""
        });

        Assert.Equal(2, replies.Count);
        Assert.Contains("OPENJIBO_RESPONSE", replies[0].Text);
        Assert.Contains("EOS", replies[1].Text);
    }

    [Fact]
    public async Task BinaryMessage_ReturnsAcknowledgementPayload()
    {
        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-test-token",
            Binary = [1, 2, 3, 4]
        });

        using var payload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("OPENJIBO_AUDIO_RECEIVED", payload.RootElement.GetProperty("type").GetString());
        Assert.Equal(4, payload.RootElement.GetProperty("data").GetProperty("bytes").GetInt32());
    }
}
