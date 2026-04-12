using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
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
            new NullWebSocketTelemetrySink(),
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
    public async Task MultiChunkAudio_AccumulatesBufferedStateAcrossMessages()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-multichunk-token",
            Text = """{"type":"LISTEN","transID":"trans-multi","data":{"rules":["wake-word"]}}"""
        });

        var firstAudioReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-multichunk-token",
            Binary = [1, 2, 3]
        });

        var secondAudioReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-multichunk-token",
            Binary = [4, 5, 6, 7]
        });

        using var firstPayload = JsonDocument.Parse(firstAudioReplies[0].Text!);
        using var secondPayload = JsonDocument.Parse(secondAudioReplies[0].Text!);
        Assert.Equal(3, firstPayload.RootElement.GetProperty("data").GetProperty("bufferedBytes").GetInt32());
        Assert.Equal(7, secondPayload.RootElement.GetProperty("data").GetProperty("bufferedBytes").GetInt32());
        Assert.Equal(2, secondPayload.RootElement.GetProperty("data").GetProperty("bufferedChunks").GetInt32());

        var session = _store.FindSessionByToken("hub-multichunk-token");
        Assert.NotNull(session);
        Assert.Equal(7, session!.TurnState.BufferedAudioBytes);
        Assert.Equal(2, session.TurnState.BufferedAudioChunkCount);
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
        Assert.Equal(0, session!.TurnState.BufferedAudioBytes);
        Assert.Equal(0, session.TurnState.BufferedAudioChunkCount);
        Assert.False(session.Metadata.ContainsKey("audioTranscriptHint"));
    }

    [Fact]
    public async Task BufferedAudio_WithoutTranscriptHint_RemainsPending()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-pending-token",
            Text = """{"type":"LISTEN","transID":"trans-pending","data":{"rules":["wake-word"]}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-pending-token",
            Binary = [1, 2, 3, 4]
        });

        var finalizeReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-pending-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-pending","data":{}}"""
        });

        Assert.Single(finalizeReplies);
        Assert.Equal("OPENJIBO_TURN_PENDING", ReadReplyType(finalizeReplies[0]));

        using var payload = JsonDocument.Parse(finalizeReplies[0].Text!);
        Assert.True(payload.RootElement.GetProperty("data").GetProperty("awaitingTranscriptHint").GetBoolean());
        Assert.Equal(1, payload.RootElement.GetProperty("data").GetProperty("finalizeAttempts").GetInt32());
    }

    [Fact]
    public async Task BufferedAudio_WithChatTranscriptHint_FinalizesAsChat()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-audio-chat-token",
            Text = """{"type":"LISTEN","transID":"trans-audio-chat","data":{"rules":["wake-word"]}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-audio-chat-token",
            Text = """{"type":"CONTEXT","transID":"trans-audio-chat","data":{"audioTranscriptHint":"hello from buffered audio"}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-audio-chat-token",
            Binary = [1, 2, 3, 4, 5]
        });

        var finalizeReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-audio-chat-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-audio-chat","data":{}}"""
        });

        Assert.Equal(3, finalizeReplies.Count);
        using var listenPayload = JsonDocument.Parse(finalizeReplies[0].Text!);
        Assert.Equal("hello from buffered audio", listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("chat", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());

        using var skillPayload = JsonDocument.Parse(finalizeReplies[2].Text!);
        Assert.Equal("chitchat-skill", skillPayload.RootElement.GetProperty("data").GetProperty("skill").GetProperty("id").GetString());
    }

    [Fact]
    public async Task FollowUpTurn_UsesNewTurnStateWithoutLeakingBufferedAudio()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-followup-audio-token",
            Text = """{"type":"LISTEN","transID":"trans-first","data":{"rules":["wake-word"]}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-followup-audio-token",
            Text = """{"type":"CONTEXT","transID":"trans-first","data":{"audioTranscriptHint":"tell me a joke"}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-followup-audio-token",
            Binary = [1, 2, 3, 4]
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-followup-audio-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-first","data":{}}"""
        });

        var followUpReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-followup-audio-token",
            Text = """{"type":"LISTEN","transID":"trans-second","data":{"text":"what time is it","rules":["follow-up"]}}"""
        });

        Assert.Equal(3, followUpReplies.Count);
        using var payload = JsonDocument.Parse(followUpReplies[0].Text!);
        Assert.Equal("time", payload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("trans-second", payload.RootElement.GetProperty("transID").GetString());

        var session = _store.FindSessionByToken("hub-followup-audio-token");
        Assert.NotNull(session);
        Assert.Equal("trans-second", session!.TurnState.TransId);
        Assert.Equal(0, session.TurnState.BufferedAudioBytes);
        Assert.Equal(0, session.TurnState.BufferedAudioChunkCount);
    }

    [Theory]
    [InlineData("fixtures\\neo-hub-client-asr-joke.flow.json")]
    [InlineData("fixtures\\neo-hub-context-client-nlu.flow.json")]
    [InlineData("fixtures\\neo-hub-buffered-audio-synthetic-asr.flow.json")]
    [InlineData("fixtures\\neo-hub-multichunk-audio-chat.flow.json")]
    [InlineData("fixtures\\neo-hub-buffered-audio-pending.flow.json")]
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
