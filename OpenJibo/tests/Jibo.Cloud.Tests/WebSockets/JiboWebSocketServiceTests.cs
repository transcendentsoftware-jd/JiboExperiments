using System.Text.Json;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Cloud.Tests.Fixtures;

namespace Jibo.Cloud.Tests.WebSockets;

public sealed class JiboWebSocketServiceTests
{
    private readonly InMemoryCloudStateStore _store;
    private readonly JiboWebSocketService _service;

    public JiboWebSocketServiceTests()
    {
        _store = new InMemoryCloudStateStore();
        var turnContextMapper = new ProtocolToTurnContextMapper();
        var conversationBroker = new DemoConversationBroker();
        var replyMapper = new ResponsePlanToSocketMessagesMapper();
        var sttSelector = new DefaultSttStrategySelector(
        [
            new SyntheticBufferedAudioSttStrategy()
        ]);

        _service = new JiboWebSocketService(
            _store,
            new WebSocketTurnFinalizationService(
                turnContextMapper,
                conversationBroker,
                replyMapper,
                sttSelector));
    }

    [Fact]
    public async Task ListenMessage_ReturnsSyntheticListenEosAndSkillAction()
    {
        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-test-token",
            Text = """{"type":"LISTEN","transID":"trans-hello","data":{"text":"hello jibo","rules":["wake-word"]}}"""
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("hello jibo", listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("chat", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
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
        Assert.Equal(4, payload.RootElement.GetProperty("data").GetProperty("bufferedBytes").GetInt32());
        Assert.Equal(1, payload.RootElement.GetProperty("data").GetProperty("bufferedChunks").GetInt32());
    }

    [Fact]
    public async Task ContextThenClientNlu_UsesFollowUpTurnStateAndSkipsSkillAction()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-follow-up-token",
            Text = """{"type":"LISTEN","transID":"trans-follow-up","data":{"text":"hello jibo","rules":["wake-word"]}}"""
        });

        var contextReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-follow-up-token",
            Text = """{"type":"CONTEXT","transID":"trans-follow-up","data":{"topic":"conversation","screen":"home"}}"""
        });

        Assert.Single(contextReplies);
        Assert.Equal("OPENJIBO_CONTEXT_ACK", ReadReplyType(contextReplies[0]));

        var nluReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-follow-up-token",
            Text = """{"type":"CLIENT_NLU","transID":"trans-follow-up","data":{"intent":"joke"}}"""
        });

        Assert.Equal(2, nluReplies.Count);
        Assert.Equal("LISTEN", ReadReplyType(nluReplies[0]));
        Assert.Equal("EOS", ReadReplyType(nluReplies[1]));

        var session = _store.FindSessionByToken("hub-follow-up-token");
        Assert.NotNull(session);
        Assert.True(session!.FollowUpOpen);
        Assert.Equal("joke", session.LastIntent);
        Assert.Equal("trans-follow-up", session.LastTransId);
    }

    [Fact]
    public async Task BufferedAudio_WithSyntheticTranscriptHint_FinalizesThroughSttSeam()
    {
        var listenReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-audio-token",
            Text = """{"type":"LISTEN","transID":"trans-audio","data":{"rules":["wake-word"]}}"""
        });

        Assert.Single(listenReplies);
        Assert.Equal("OPENJIBO_TURN_PENDING", ReadReplyType(listenReplies[0]));

        var contextReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-audio-token",
            Text = """{"type":"CONTEXT","transID":"trans-audio","data":{"topic":"conversation","audioTranscriptHint":"tell me a joke"}}"""
        });

        Assert.Single(contextReplies);
        Assert.Equal("OPENJIBO_CONTEXT_ACK", ReadReplyType(contextReplies[0]));

        var audioReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-audio-token",
            Binary = [1, 2, 3, 4, 5, 6]
        });

        Assert.Single(audioReplies);
        Assert.Equal("OPENJIBO_AUDIO_RECEIVED", ReadReplyType(audioReplies[0]));

        var finalizeReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-audio-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-audio","data":{}}"""
        });

        Assert.Equal(3, finalizeReplies.Count);
        Assert.Equal("LISTEN", ReadReplyType(finalizeReplies[0]));
        Assert.Equal("EOS", ReadReplyType(finalizeReplies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(finalizeReplies[2]));

        using var listenPayload = JsonDocument.Parse(finalizeReplies[0].Text!);
        Assert.Equal("tell me a joke", listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("joke", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());

        var session = _store.FindSessionByToken("hub-audio-token");
        Assert.NotNull(session);
        Assert.Equal(0, session!.BufferedAudioBytes);
        Assert.Equal(0, session.BufferedAudioChunkCount);
        Assert.False(session.Metadata.ContainsKey("audioTranscriptHint"));
    }

    [Theory]
    [InlineData("fixtures\\neo-hub-client-asr-joke.flow.json")]
    [InlineData("fixtures\\neo-hub-context-client-nlu.flow.json")]
    [InlineData("fixtures\\neo-hub-buffered-audio-synthetic-asr.flow.json")]
    public async Task WebSocketFixture_ReplaysSuccessfully(string relativePath)
    {
        var fixture = WebSocketFixtureLoader.Load(relativePath);

        foreach (var step in fixture.Steps)
        {
            var replies = await _service.HandleMessageAsync(step.Message);
            var actualTypes = replies.Select(ReadReplyType).ToArray();
            Assert.Equal(step.ExpectedReplyTypes, actualTypes);
        }
    }

    private static string ReadReplyType(WebSocketReply reply)
    {
        using var payload = JsonDocument.Parse(reply.Text!);
        return payload.RootElement.GetProperty("type").GetString() ?? string.Empty;
    }
}
