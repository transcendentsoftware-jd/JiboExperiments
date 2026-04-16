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
        Assert.Equal(75, replies[2].DelayMs);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("hello jibo", listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("chat", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());

        using var eosPayload = JsonDocument.Parse(replies[1].Text!);
        Assert.True(eosPayload.RootElement.TryGetProperty("ts", out _));
        Assert.StartsWith("mid-", eosPayload.RootElement.GetProperty("msgID").GetString());
        Assert.Equal("trans-hello", eosPayload.RootElement.GetProperty("transID").GetString());
        Assert.Equal(JsonValueKind.Object, eosPayload.RootElement.GetProperty("data").ValueKind);

        using var skillPayload = JsonDocument.Parse(replies[2].Text!);
        Assert.StartsWith("mid-", skillPayload.RootElement.GetProperty("msgID").GetString());
        var meta = skillPayload.RootElement
            .GetProperty("data")
            .GetProperty("action")
            .GetProperty("config")
            .GetProperty("jcp")
            .GetProperty("config")
            .GetProperty("play")
            .GetProperty("meta");
        Assert.False(meta.TryGetProperty("intent", out _));
        Assert.False(meta.TryGetProperty("transcript", out _));
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
    public async Task BufferedAudio_WithContextAndTranscriptHint_AutoFinalizesAfterThreshold()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-auto-finalize-token",
            Text = """{"type":"LISTEN","transID":"trans-auto","data":{"rules":["launch"]}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-auto-finalize-token",
            Text = """{"type":"CONTEXT","transID":"trans-auto","data":{"audioTranscriptHint":"tell me a joke"}}"""
        });

        IReadOnlyList<WebSocketReply> replies = [];
        for (var index = 0; index < 4; index += 1)
        {
            replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-auto-finalize-token",
                Binary = new byte[3000]
            });

            Assert.Single(replies);
            Assert.Equal("OPENJIBO_AUDIO_RECEIVED", ReadReplyType(replies[0]));
        }

        replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-auto-finalize-token",
            Binary = new byte[3000]
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));
        Assert.Equal(75, replies[2].DelayMs);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("tell me a joke", listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("joke", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
    }

    [Fact]
    public async Task BufferedAudio_WithoutTranscriptHint_AutoFinalizesWithFallbackAndEos()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-auto-fallback-token",
            Text = """{"type":"LISTEN","transID":"trans-auto-fallback","data":{"rules":["launch"]}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-auto-fallback-token",
            Text = """{"type":"CONTEXT","transID":"trans-auto-fallback","data":{"topic":"conversation"}}"""
        });

        IReadOnlyList<WebSocketReply> replies = [];
        for (var index = 0; index < 4; index += 1)
        {
            replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-auto-fallback-token",
                Binary = new byte[3000]
            });

            Assert.Single(replies);
            Assert.Equal("OPENJIBO_AUDIO_RECEIVED", ReadReplyType(replies[0]));
        }

        replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-auto-fallback-token",
            Binary = new byte[3000]
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));
        Assert.Equal(75, replies[2].DelayMs);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("heyJibo", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal(string.Empty, listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
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
        Assert.Equal(75, finalizeReplies[2].DelayMs);

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
    public async Task ClientAsrJokeFlow_MatchesNodePayloadShapeForEosAndSkillAction()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-client-asr-joke-token",
            Text = """{"type":"LISTEN","transID":"trans-joke-shape","data":{"rules":["wake-word"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-client-asr-joke-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-joke-shape","data":{"text":"tell me a joke"}}"""
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal(75, replies[2].DelayMs);

        using var eosPayload = JsonDocument.Parse(replies[1].Text!);
        Assert.Equal("EOS", eosPayload.RootElement.GetProperty("type").GetString());
        Assert.Equal("trans-joke-shape", eosPayload.RootElement.GetProperty("transID").GetString());
        Assert.True(eosPayload.RootElement.TryGetProperty("ts", out _));
        Assert.StartsWith("mid-", eosPayload.RootElement.GetProperty("msgID").GetString());
        Assert.Empty(eosPayload.RootElement.GetProperty("data").EnumerateObject());

        using var skillPayload = JsonDocument.Parse(replies[2].Text!);
        Assert.Equal("SKILL_ACTION", skillPayload.RootElement.GetProperty("type").GetString());
        Assert.Equal("trans-joke-shape", skillPayload.RootElement.GetProperty("transID").GetString());
        Assert.StartsWith("mid-", skillPayload.RootElement.GetProperty("msgID").GetString());
        Assert.Equal("@be/joke", skillPayload.RootElement.GetProperty("data").GetProperty("skill").GetProperty("id").GetString());

        var meta = skillPayload.RootElement
            .GetProperty("data")
            .GetProperty("action")
            .GetProperty("config")
            .GetProperty("jcp")
            .GetProperty("config")
            .GetProperty("play")
            .GetProperty("meta");

        Assert.Equal("RUNTIME_PROMPT", meta.GetProperty("prompt_id").GetString());
        Assert.Equal("AN", meta.GetProperty("prompt_sub_category").GetString());
        Assert.Equal("runtime-joke", meta.GetProperty("mim_id").GetString());
        Assert.Equal("announcement", meta.GetProperty("mim_type").GetString());
        Assert.False(meta.TryGetProperty("intent", out _));
        Assert.False(meta.TryGetProperty("transcript", out _));
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

            if (step.ExpectedReplies.Count > 0)
            {
                Assert.Equal(replies.Count, step.ExpectedReplies.Count);

                for (var index = 0; index < step.ExpectedReplies.Count; index += 1)
                {
                    var expectedReply = step.ExpectedReplies[index];
                    Assert.Equal(expectedReply.Type, actualTypes[index]);

                    if (expectedReply.DelayMs.HasValue)
                    {
                        Assert.Equal(expectedReply.DelayMs.Value, replies[index].DelayMs);
                    }

                    if (expectedReply.JsonSubset is { ValueKind: JsonValueKind.Object } jsonSubset)
                    {
                        using var actualPayload = JsonDocument.Parse(replies[index].Text!);
                        AssertJsonContains(jsonSubset, actualPayload.RootElement);
                    }
                }
            }
        }
    }

    private static void AssertJsonContains(JsonElement expected, JsonElement actual)
    {
        Assert.Equal(expected.ValueKind, actual.ValueKind);

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in expected.EnumerateObject())
                {
                    Assert.True(actual.TryGetProperty(property.Name, out var actualProperty), $"Expected property '{property.Name}' was not found.");
                    AssertJsonContains(property.Value, actualProperty);
                }
                break;
            case JsonValueKind.Array:
            {
                var expectedItems = expected.EnumerateArray().ToArray();
                var actualItems = actual.EnumerateArray().ToArray();
                Assert.Equal(expectedItems.Length, actualItems.Length);
                for (var index = 0; index < expectedItems.Length; index += 1)
                {
                    AssertJsonContains(expectedItems[index], actualItems[index]);
                }
                break;
            }
            case JsonValueKind.String:
                Assert.Equal(expected.GetString(), actual.GetString());
                break;
            case JsonValueKind.Number:
                Assert.Equal(expected.GetRawText(), actual.GetRawText());
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                Assert.Equal(expected.GetBoolean(), actual.GetBoolean());
                break;
            case JsonValueKind.Null:
                Assert.Equal(JsonValueKind.Null, actual.ValueKind);
                break;
            default:
                Assert.Equal(expected.GetRawText(), actual.GetRawText());
                break;
        }
    }

    private static string ReadReplyType(WebSocketReply reply)
    {
        using var payload = JsonDocument.Parse(reply.Text!);
        return payload.RootElement.GetProperty("type").GetString() ?? string.Empty;
    }
}
