using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Content;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Cloud.Tests.Fixtures;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Tests.WebSockets;

public sealed class JiboWebSocketServiceTests
{
    private readonly InMemoryCloudStateStore _store;
    private readonly JiboWebSocketService _service;

    public JiboWebSocketServiceTests()
    {
        _store = new InMemoryCloudStateStore();
        var contentRepository = new InMemoryJiboExperienceContentRepository();
        var contentCache = new JiboExperienceContentCache(contentRepository);
        var conversationBroker = new DemoConversationBroker(new JiboInteractionService(contentCache, new DefaultJiboRandomizer(), new InMemoryPersonalMemoryStore()));
        var sttSelector = new DefaultSttStrategySelector(
        [
            new SyntheticBufferedAudioSttStrategy()
        ]);
        var sink = new NullTurnTelemetrySink();

        _service = new JiboWebSocketService(
            _store,
            new NullWebSocketTelemetrySink(),
            new WebSocketTurnFinalizationService(conversationBroker,
                sttSelector,
                sink));
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
        Assert.Equal("hello", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.True(listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skipSurprises").GetBoolean());

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
    public async Task Listen_CloudVersion_DoesNotOpenFollowUpAndClosesLateTailListen()
    {
        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-cloud-version-token",
            Text = """{"type":"LISTEN","transID":"trans-cloud-version","data":{"text":"What's your cloud version?","hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("cloud_version", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.True(listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skipSurprises").GetBoolean());

        using var skillPayload = JsonDocument.Parse(replies[2].Text!);
        var esml = skillPayload.RootElement
            .GetProperty("data")
            .GetProperty("action")
            .GetProperty("config")
            .GetProperty("jcp")
            .GetProperty("config")
            .GetProperty("play")
            .GetProperty("esml")
            .GetString();
        Assert.Contains("Cloud version", esml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Jibo", esml, StringComparison.OrdinalIgnoreCase);

        var session = _store.FindSessionByToken("hub-cloud-version-token");
        Assert.NotNull(session);
        Assert.Equal("cloud_version", session.LastIntent);
        Assert.False(session.FollowUpOpen);
        Assert.False(session.TurnState.AwaitingTurnCompletion);
        Assert.False(session.TurnState.SawListen);
        Assert.True(session.TurnState.IgnoreAdditionalAudioUntilUtc > DateTimeOffset.UtcNow.AddSeconds(3));

        var tailListenReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-cloud-version-token",
            Text = """{"type":"LISTEN","transID":"trans-cloud-version-tail","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        Assert.Equal(3, tailListenReplies.Count);
        Assert.Equal("LISTEN", ReadReplyType(tailListenReplies[0]));
        Assert.Equal("EOS", ReadReplyType(tailListenReplies[1]));
        Assert.Equal("SKILL_REDIRECT", ReadReplyType(tailListenReplies[2]));
        using (var lateListenPayload = JsonDocument.Parse(tailListenReplies[0].Text!))
        {
            Assert.Equal("trans-cloud-version-tail", lateListenPayload.RootElement.GetProperty("transID").GetString());
            Assert.Equal("launch", lateListenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules")[0].GetString());
        }
        Assert.Equal("trans-cloud-version", session.TurnState.TransId);
        Assert.False(session.TurnState.AwaitingTurnCompletion);
        Assert.False(session.TurnState.SawListen);

        var tailAudioReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-cloud-version-token",
            Binary = new byte[4096]
        });

        Assert.Empty(tailAudioReplies);
        Assert.Equal(0, session.TurnState.BufferedAudioBytes);
        Assert.Equal(0, session.TurnState.BufferedAudioChunkCount);
    }

    [Fact]
    public async Task BinaryMessage_BuffersAudioWithoutEmittingSyntheticAck()
    {
        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-test-token",
            Binary = [1, 2, 3, 4]
        });

        Assert.Empty(replies);
        var session = _store.FindSessionByToken("hub-test-token");
        Assert.NotNull(session);
        Assert.Equal(4, session.TurnState.BufferedAudioBytes);
        Assert.Equal(1, session.TurnState.BufferedAudioChunkCount);
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

        IReadOnlyList<WebSocketReply> replies;
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

            Assert.Empty(replies);
        }

        var session = _store.FindSessionByToken("hub-auto-finalize-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2);

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

        IReadOnlyList<WebSocketReply> replies;
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

            Assert.Empty(replies);
        }

        var session = _store.FindSessionByToken("hub-auto-fallback-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2);

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
        Assert.True(listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skipSurprises").GetBoolean());
    }

    [Fact]
    public async Task BufferedAudio_WithIncompletePreferenceHint_DefersThenFinalizesWhenContinuationArrives()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-preference-continuation-token",
            Text = """{"type":"LISTEN","transID":"trans-preference-continuation","data":{"rules":["launch"]}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-preference-continuation-token",
            Text = """{"type":"CONTEXT","transID":"trans-preference-continuation","data":{"audioTranscriptHint":"my favorite sport"}}"""
        });

        for (var index = 0; index < 4; index += 1)
        {
            var chunkReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-preference-continuation-token",
                Binary = new byte[3000]
            });

            Assert.Empty(chunkReplies);
        }

        var session = _store.FindSessionByToken("hub-preference-continuation-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2);

        var deferredReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-preference-continuation-token",
            Binary = new byte[3000]
        });

        Assert.Empty(deferredReplies);

        var finalizedReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-preference-continuation-token",
            Text = """{"type":"CONTEXT","transID":"trans-preference-continuation","data":{"audioTranscriptHint":"my favorite sport is football"}}"""
        });

        Assert.Equal(3, finalizedReplies.Count);
        Assert.Equal("LISTEN", ReadReplyType(finalizedReplies[0]));
        Assert.Equal("EOS", ReadReplyType(finalizedReplies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(finalizedReplies[2]));

        using var listenPayload = JsonDocument.Parse(finalizedReplies[0].Text!);
        Assert.Equal("memory_set_preference", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("my favorite sport is football", listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
    }

    [Fact]
    public async Task BufferedAudio_WithBarePreferenceSetHint_FinalizesWithoutDeferral()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-preference-bare-token",
            Text = """{"type":"LISTEN","transID":"trans-preference-bare","data":{"rules":["launch"]}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-preference-bare-token",
            Text = """{"type":"CONTEXT","transID":"trans-preference-bare","data":{"audioTranscriptHint":"my favorite sport football"}}"""
        });

        for (var index = 0; index < 4; index += 1)
        {
            var chunkReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-preference-bare-token",
                Binary = new byte[3000]
            });

            Assert.Empty(chunkReplies);
        }

        var session = _store.FindSessionByToken("hub-preference-bare-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2);

        var finalizedReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-preference-bare-token",
            Binary = new byte[3000]
        });

        Assert.Equal(3, finalizedReplies.Count);
        Assert.Equal("LISTEN", ReadReplyType(finalizedReplies[0]));
        Assert.Equal("EOS", ReadReplyType(finalizedReplies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(finalizedReplies[2]));

        using var listenPayload = JsonDocument.Parse(finalizedReplies[0].Text!);
        Assert.Equal("memory_set_preference", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("my favorite sport football", listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
    }

    [Fact]
    public async Task BufferedAudio_WithIncompleteAffinityHint_DefersThenFinalizesWhenContinuationArrives()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-affinity-continuation-token",
            Text = """{"type":"LISTEN","transID":"trans-affinity-continuation","data":{"rules":["launch"]}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-affinity-continuation-token",
            Text = """{"type":"CONTEXT","transID":"trans-affinity-continuation","data":{"audioTranscriptHint":"i do like"}}"""
        });

        for (var index = 0; index < 4; index += 1)
        {
            var chunkReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-affinity-continuation-token",
                Binary = new byte[3000]
            });

            Assert.Empty(chunkReplies);
        }

        var session = _store.FindSessionByToken("hub-affinity-continuation-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2);

        var deferredReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-affinity-continuation-token",
            Binary = new byte[3000]
        });

        Assert.Empty(deferredReplies);

        var finalizedReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-affinity-continuation-token",
            Text = """{"type":"CONTEXT","transID":"trans-affinity-continuation","data":{"audioTranscriptHint":"i do like pizza"}}"""
        });

        Assert.Equal(3, finalizedReplies.Count);
        Assert.Equal("LISTEN", ReadReplyType(finalizedReplies[0]));
        Assert.Equal("EOS", ReadReplyType(finalizedReplies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(finalizedReplies[2]));

        using var listenPayload = JsonDocument.Parse(finalizedReplies[0].Text!);
        Assert.Equal("memory_set_affinity", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("i do like pizza", listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
    }

    [Fact]
    public async Task BufferedAudio_WithIncompletePegasusWeAffinityHint_DefersThenFinalizesWhenContinuationArrives()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-affinity-we-continuation-token",
            Text = """{"type":"LISTEN","transID":"trans-affinity-we-continuation","data":{"rules":["launch"]}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-affinity-we-continuation-token",
            Text = """{"type":"CONTEXT","transID":"trans-affinity-we-continuation","data":{"audioTranscriptHint":"we like"}}"""
        });

        for (var index = 0; index < 4; index += 1)
        {
            var chunkReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-affinity-we-continuation-token",
                Binary = new byte[3000]
            });

            Assert.Empty(chunkReplies);
        }

        var session = _store.FindSessionByToken("hub-affinity-we-continuation-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2);

        var deferredReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-affinity-we-continuation-token",
            Binary = new byte[3000]
        });

        Assert.Empty(deferredReplies);

        var finalizedReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-affinity-we-continuation-token",
            Text = """{"type":"CONTEXT","transID":"trans-affinity-we-continuation","data":{"audioTranscriptHint":"we like pizza"}}"""
        });

        Assert.Equal(3, finalizedReplies.Count);
        Assert.Equal("LISTEN", ReadReplyType(finalizedReplies[0]));
        Assert.Equal("EOS", ReadReplyType(finalizedReplies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(finalizedReplies[2]));

        using var listenPayload = JsonDocument.Parse(finalizedReplies[0].Text!);
        Assert.Equal("memory_set_affinity", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("we like pizza", listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
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

        Assert.Empty(firstAudioReplies);
        Assert.Empty(secondAudioReplies);

        var session = _store.FindSessionByToken("hub-multichunk-token");
        Assert.NotNull(session);
        Assert.Equal(7, session.TurnState.BufferedAudioBytes);
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

        Assert.Empty(contextReplies);

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
        Assert.True(session.FollowUpOpen);
        Assert.Equal("joke", session.LastIntent);
        Assert.Equal("trans-follow-up", session.LastTransId);
    }

    [Fact]
    public async Task ClientNlu_ClockAskForTime_PreservesObservedIntentRulesAndEntities()
    {
        var listenReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-menu-token",
            Text = """{"type":"LISTEN","transID":"trans-clock-time","data":{"lang":"en-US","rules":["clock/clock_menu","globals/global_commands_launch"],"mode":"CLIENT_NLU"}}"""
        });

        Assert.Empty(listenReplies);

        var nluReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-menu-token",
            Text = """{"type":"CLIENT_NLU","transID":"trans-clock-time","data":{"entities":{"domain":"clock"},"intent":"askForTime","rules":["clock/clock_menu"]}}"""
        });

        Assert.Equal(2, nluReplies.Count);
        Assert.Equal("LISTEN", ReadReplyType(nluReplies[0]));
        Assert.Equal("EOS", ReadReplyType(nluReplies[1]));

        using var listenPayload = JsonDocument.Parse(nluReplies[0].Text!);
        Assert.Equal("askForTime", listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("askForTime", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("clock", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("domain").GetString());
        Assert.Equal("clock/clock_menu", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules")[0].GetString());
        Assert.Equal("clock/clock_menu", listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
    }

    [Fact]
    public async Task ClientAsr_SetTimerForFiveMinutes_RedirectsIntoClockSkillWithTimerEntities()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-timer-token",
            Text = """{"type":"LISTEN","transID":"trans-clock-timer","data":{"rules":["globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-timer-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-clock-timer","data":{"text":"set a timer for five minutes"}}"""
        });

        Assert.Equal(4, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_REDIRECT", ReadReplyType(replies[2]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[3]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("start", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("@be/clock", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("skill").GetString());
        Assert.Equal("timer", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("domain").GetString());
        Assert.Equal("0", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("hours").GetString());
        Assert.Equal("5", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("minutes").GetString());
        Assert.Equal("null", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("seconds").GetString());

        using var redirectPayload = JsonDocument.Parse(replies[2].Text!);
        Assert.Equal("@be/clock", redirectPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.True(redirectPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skipSurprises").GetBoolean());
        Assert.Equal("start", redirectPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
    }

    [Fact]
    public async Task ClientAsr_OpenTheClock_RedirectsIntoClockSkillWithAskForTimeIntent()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-open-token",
            Text = """{"type":"LISTEN","transID":"trans-clock-open","data":{"rules":["globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-open-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-clock-open","data":{"text":"open the clock"}}"""
        });

        Assert.Equal(4, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_REDIRECT", ReadReplyType(replies[2]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("askForTime", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("clock", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("domain").GetString());

        using var redirectPayload = JsonDocument.Parse(replies[2].Text!);
        Assert.Equal("@be/clock", redirectPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.Equal("askForTime", redirectPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
    }

    [Fact]
    public async Task ClientAsr_WhatTimeIsIt_RedirectsIntoClockSkillWithAskForTimeIntent()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-voice-time-token",
            Text = """{"type":"LISTEN","transID":"trans-clock-voice-time","data":{"rules":["globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-voice-time-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-clock-voice-time","data":{"text":"what time is it"}}"""
        });

        Assert.Equal(4, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("askForTime", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("@be/clock", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("skill").GetString());
    }

    [Fact]
    public async Task ClientAsr_SetAlarmForSevenThirtyAm_RedirectsIntoClockSkillWithAlarmEntities()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-alarm-token",
            Text = """{"type":"LISTEN","transID":"trans-clock-alarm","data":{"rules":["globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-alarm-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-clock-alarm","data":{"text":"set an alarm for 7:30 am"}}"""
        });

        Assert.Equal(4, replies.Count);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("start", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("@be/clock", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("skill").GetString());
        Assert.Equal("alarm", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("domain").GetString());
        Assert.Equal("7:30", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("time").GetString());
        Assert.Equal("am", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("ampm").GetString());
    }

    [Fact]
    public async Task ClientAsr_SetAlarmForEightThirty_ParsesCompactAlarmTime()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-compact-alarm-token",
            Text = """{"type":"LISTEN","transID":"trans-clock-compact-alarm","data":{"rules":["globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-compact-alarm-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-clock-compact-alarm","data":{"text":"set an alarm for 830"}}"""
        });

        Assert.Equal(4, replies.Count);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("8:30", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("time").GetString());
        Assert.Equal("am", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("ampm").GetString());
    }

    [Fact]
    public async Task ClientAsr_SetAlarmForTenTwentyFiveWithHyphen_ParsesAlarmTime()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-hyphen-alarm-token",
            Text = """{"type":"LISTEN","transID":"trans-clock-hyphen-alarm","data":{"rules":["globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-hyphen-alarm-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-clock-hyphen-alarm","data":{"text":"set an alarm for 10-25"}}"""
        });

        Assert.Equal(4, replies.Count);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("start", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("alarm", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("domain").GetString());
        Assert.Equal("10:25", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("time").GetString());
        Assert.Equal("am", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("ampm").GetString());
    }

    [Fact]
    public async Task ClientAsr_SetAlarmForTenTwentyFivePm_ParsesAlarmTimeWithPm()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-pm-alarm-token",
            Text = """{"type":"LISTEN","transID":"trans-clock-pm-alarm","data":{"rules":["globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-pm-alarm-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-clock-pm-alarm","data":{"text":"set an alarm for 10:25 pm"}}"""
        });

        Assert.Equal(4, replies.Count);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("10:25", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("time").GetString());
        Assert.Equal("pm", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("ampm").GetString());
    }

    [Fact]
    public async Task ClientAsr_SetAlarmForSevenTen_UsesNextOccurrenceFromContext()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-next-occurrence-token",
            Text = """{"type":"LISTEN","transID":"trans-clock-next-occurrence","data":{"rules":["globals/global_commands_launch"]}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-next-occurrence-token",
            Text = """{"type":"CONTEXT","transID":"trans-clock-next-occurrence","data":{"runtime":{"location":{"iso":"2026-04-22T07:15:00-05:00"}}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-next-occurrence-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-clock-next-occurrence","data":{"text":"set an alarm for 7:10"}}"""
        });

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("7:10", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("time").GetString());
        Assert.Equal("pm", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("ampm").GetString());
    }

    [Fact]
    public async Task ClientAsr_TimerValueFollowUp_ParsesBareDurationIntoClockStartIntent()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-timer-followup-token",
            Text = """{"type":"LISTEN","transID":"trans-clock-timer-followup","data":{"rules":["clock/timer_set_value"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-timer-followup-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-clock-timer-followup","data":{"text":"twenty five minutes"}}"""
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("start", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("timer", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("domain").GetString());
        Assert.Equal("25", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("minutes").GetString());
    }

    [Fact]
    public async Task ClientAsr_AlarmValueFollowUp_ParsesBareSpokenTimeIntoClockStartIntent()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-alarm-followup-token",
            Text = """{"type":"LISTEN","transID":"trans-clock-alarm-followup","data":{"rules":["clock/alarm_set_value"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-alarm-followup-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-clock-alarm-followup","data":{"text":"ten twenty five"}}"""
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("start", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("alarm", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("domain").GetString());
        Assert.Equal("10:25", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("time").GetString());
        Assert.Equal("am", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("ampm").GetString());
    }

    [Fact]
    public async Task ClientAsr_AlarmValueFollowUp_ParsesCommaSeparatedSpokenDigitsIntoClockStartIntent()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-alarm-comma-followup-token",
            Text = """{"type":"LISTEN","transID":"trans-clock-alarm-comma-followup","data":{"rules":["clock/alarm_set_value"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-alarm-comma-followup-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-clock-alarm-comma-followup","data":{"text":"7, 44"}}"""
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("start", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("alarm", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("domain").GetString());
        Assert.Equal("7:44", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("time").GetString());
    }

    [Fact]
    public async Task ClientAsr_SetAlarmWithoutTime_RedirectsIntoClockSkillWithoutDefaultingTime()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-clarify-alarm-token",
            Text = """{"type":"LISTEN","transID":"trans-clock-clarify-alarm","data":{"rules":["globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-clarify-alarm-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-clock-clarify-alarm","data":{"text":"set an alarm"}}"""
        });

        Assert.Equal(4, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_REDIRECT", ReadReplyType(replies[2]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[3]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("set", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("alarm", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("domain").GetString());
        Assert.False(listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").TryGetProperty("time", out _));

        using var redirectPayload = JsonDocument.Parse(replies[2].Text!);
        Assert.Equal("@be/clock", redirectPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.Equal("set", redirectPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
    }

    [Fact]
    public async Task ClientAsr_CancelAlarm_RedirectsIntoClockSkillWithDeleteIntent()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-cancel-alarm-token",
            Text = """{"type":"LISTEN","transID":"trans-clock-cancel-alarm","data":{"rules":["globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-cancel-alarm-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-clock-cancel-alarm","data":{"text":"cancel alarm"}}"""
        });

        Assert.Equal(4, replies.Count);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("delete", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("alarm", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("domain").GetString());

        using var redirectPayload = JsonDocument.Parse(replies[2].Text!);
        Assert.Equal("@be/clock", redirectPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.Equal("delete", redirectPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());

        var session = _store.FindSessionByToken("hub-clock-cancel-alarm-token");
        Assert.NotNull(session);
        Assert.False(session.FollowUpOpen);
    }

    [Fact]
    public async Task ClientNlu_SetAlarmWithoutTime_StaysInClarificationInsteadOfDefaultingToSeven()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-set-alarm-query-token",
            Text = """{"type":"LISTEN","transID":"trans-clock-set-alarm-query","data":{"rules":["clock/clock_menu","globals/global_commands_launch"],"mode":"CLIENT_NLU"}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-set-alarm-query-token",
            Text = """{"type":"CLIENT_NLU","transID":"trans-clock-set-alarm-query","data":{"entities":{"domain":"alarm"},"intent":"set","rules":["clock/clock_menu"]}}"""
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("set", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("alarm", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("domain").GetString());
        Assert.False(listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").TryGetProperty("time", out _));
    }

    [Fact]
    public async Task ClientNlu_CancelFromAlarmValuePrompt_PassesClockCancelInsteadOfClarifyingAgain()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-cancel-alarm-value-token",
            Text = """{"type":"LISTEN","transID":"trans-clock-cancel-alarm-value","data":{"rules":["clock/alarm_set_value","globals/gui_nav","globals/global_commands_launch"],"mode":"CLIENT_NLU"}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-cancel-alarm-value-token",
            Text = """{"type":"CLIENT_NLU","transID":"trans-clock-cancel-alarm-value","data":{"entities":{},"intent":"cancel","rules":["clock/alarm_set_value"]}}"""
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("cancel", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("clock/alarm_set_value", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules")[0].GetString());
        Assert.Equal("alarm", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("domain").GetString());
    }

    [Fact]
    public async Task ClientNlu_CancelFromAlarmQueryMenu_UsesLastClockDomainAndDeletesAlarm()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-cancel-query-token",
            Text = """{"type":"LISTEN","transID":"trans-clock-cancel-query","data":{"rules":["globals/global_commands_launch"]}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-cancel-query-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-clock-cancel-query","data":{"text":"set an alarm for 7:16 am"}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-cancel-query-token",
            Text = """{"type":"LISTEN","transID":"trans-clock-cancel-query-menu","data":{"rules":["clock/alarm_timer_query_menu","globals/global_commands_launch"],"mode":"CLIENT_NLU"}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-cancel-query-token",
            Text = """{"type":"CLIENT_NLU","transID":"trans-clock-cancel-query-menu","data":{"entities":{},"intent":"cancel","rules":["clock/alarm_timer_query_menu"]}}"""
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("delete", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("alarm", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("domain").GetString());
    }

    [Fact]
    public async Task ClientAsr_OpenPhotoGallery_RedirectsIntoGallerySkill()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-photo-gallery-token",
            Text = """{"type":"LISTEN","transID":"trans-photo-gallery","data":{"rules":["globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-photo-gallery-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-photo-gallery","data":{"text":"open photo gallery"}}"""
        });

        Assert.Equal(4, replies.Count);
        Assert.Equal("SKILL_REDIRECT", ReadReplyType(replies[2]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("menu", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("@be/gallery", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("skill").GetString());

        using var redirectPayload = JsonDocument.Parse(replies[2].Text!);
        Assert.Equal("@be/gallery", redirectPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.Equal("menu", redirectPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
    }

    [Fact]
    public async Task Context_FromGalleryOpen_DoesNotReopenPendingTurnOrLeaveBufferedAudioArmed()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-photo-gallery-context-token",
            Text = """{"type":"LISTEN","transID":"trans-photo-gallery-context","data":{"rules":["globals/global_commands_launch"]}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-photo-gallery-context-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-photo-gallery-context","data":{"text":"open photo gallery"}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-photo-gallery-context-token",
            Binary = [1, 2, 3, 4, 5]
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-photo-gallery-context-token",
            Text = """{"type":"CONTEXT","transID":"trans-photo-gallery-context","data":{"skill":{"id":"@be/gallery"}}}"""
        });

        Assert.Empty(replies);

        var session = _store.FindSessionByToken("hub-photo-gallery-context-token");
        Assert.NotNull(session);
        Assert.False(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal(0, session.TurnState.BufferedAudioBytes);
        Assert.Equal(0, session.TurnState.BufferedAudioChunkCount);
    }

    [Fact]
    public async Task Context_FromGalleryYesNoPrompt_DoesNotSuppressYesAnswer()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-gallery-yesno-context-token",
            Text = """{"type":"LISTEN","transID":"trans-gallery-yesno-context","data":{"rules":["shared/yes_no","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"],"asr":{"hints":["$YESNO"]}}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-gallery-yesno-context-token",
            Text = """{"type":"CONTEXT","transID":"trans-gallery-yesno-context","data":{"audioTranscriptHint":"yes","skill":{"id":"@be/gallery"}}}"""
        });

        for (var index = 0; index < 4; index += 1)
        {
            var interimReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-gallery-yesno-context-token",
                Binary = new byte[3000]
            });

            Assert.Empty(interimReplies);
        }

        var session = _store.FindSessionByToken("hub-gallery-yesno-context-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2);

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-gallery-yesno-context-token",
            Binary = new byte[3000]
        });

        Assert.Equal(3, replies.Count);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("yes", listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("yes", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("shared/yes_no", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules")[0].GetString());
        Assert.Equal("shared/yes_no", listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
    }

    [Fact]
    public async Task Context_FromSettingsVolumeControl_IgnoresPassiveLocalAudioTail()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-settings-volume-tail-token",
            Text = """{"type":"LISTEN","transID":"trans-settings-volume-tail","data":{"rules":["settings/volume_control","globals/gui_nav","globals/global_commands_launch"]}}"""
        });

        var contextReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-settings-volume-tail-token",
            Text = """{"type":"CONTEXT","transID":"trans-settings-volume-tail","data":{"skill":{"id":"@be/settings"}}}"""
        });

        Assert.Empty(contextReplies);

        var session = _store.FindSessionByToken("hub-settings-volume-tail-token");
        Assert.NotNull(session);
        Assert.False(session.TurnState.AwaitingTurnCompletion);
        Assert.False(session.TurnState.SawListen);
        session.TurnState.IgnoreAdditionalAudioUntilUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1);

        for (var index = 0; index < 5; index += 1)
        {
            var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-settings-volume-tail-token",
                Binary = new byte[3000]
            });

            Assert.Empty(replies);
        }

        Assert.False(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal(0, session.TurnState.BufferedAudioBytes);
        Assert.Equal(0, session.TurnState.BufferedAudioChunkCount);
    }

    [Fact]
    public async Task ClientAsr_AlarmTimerOkayEmptyReply_MapsToLocalNoInputInsteadOfFallback()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-alarm-okay-token",
            Text = """{"type":"LISTEN","transID":"trans-clock-alarm-okay","data":{"rules":["clock/alarm_timer_okay","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-alarm-okay-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-clock-alarm-okay","data":{}}"""
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal(string.Empty, listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("clock/alarm_timer_okay", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules")[0].GetString());
    }

    [Fact]
    public async Task ClientAsr_AlarmValuePromptEmptyReply_MapsToLocalNoInputInsteadOfFallback()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-alarm-value-noinput-token",
            Text = """{"type":"LISTEN","transID":"trans-clock-alarm-value-noinput","data":{"rules":["clock/alarm_set_value","globals/gui_nav","globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-alarm-value-noinput-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-clock-alarm-value-noinput","data":{}}"""
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal(string.Empty, listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("clock/alarm_set_value", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules")[0].GetString());
    }

    [Fact]
    public async Task ClientAsr_GalleryPreviewEmptyReply_MapsToLocalNoInputInsteadOfFallback()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-gallery-preview-noinput-token",
            Text = """{"type":"LISTEN","transID":"trans-gallery-preview-noinput","data":{"rules":["gallery/gallery_preview","globals/gui_nav","globals/mim_repeat","globals/mim_thanks","globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-gallery-preview-noinput-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-gallery-preview-noinput","data":{}}"""
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal(string.Empty, listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("gallery/gallery_preview", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules")[0].GetString());
    }

    [Fact]
    public async Task ClientAsr_SnapAPicture_RedirectsIntoCreateSkill()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-snapshot-token",
            Text = """{"type":"LISTEN","transID":"trans-snapshot","data":{"rules":["globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-snapshot-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-snapshot","data":{"text":"snap a picture"}}"""
        });

        Assert.Equal(4, replies.Count);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("createOnePhoto", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("@be/create", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("skill").GetString());

        using var redirectPayload = JsonDocument.Parse(replies[2].Text!);
        Assert.Equal("@be/create", redirectPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.Equal("createOnePhoto", redirectPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
    }

    [Fact]
    public async Task ClientAsr_OpenPhotobooth_RedirectsIntoCreateSkill()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-photobooth-token",
            Text = """{"type":"LISTEN","transID":"trans-photobooth","data":{"rules":["globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-photobooth-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-photobooth","data":{"text":"open photobooth"}}"""
        });

        Assert.Equal(4, replies.Count);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("createSomePhotos", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("@be/create", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("skill").GetString());

        using var redirectPayload = JsonDocument.Parse(replies[2].Text!);
        Assert.Equal("@be/create", redirectPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.Equal("createSomePhotos", redirectPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
    }

    [Fact]
    public async Task ClientAsr_YesNoCreateFlow_PreservesCreateRuleAndDomain()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-yesno-token",
            Text = """{"type":"LISTEN","transID":"trans-yesno","data":{"rules":["create/is_it_a_keeper","$YESNO"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-yesno-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-yesno","data":{"text":"yeah"}}"""
        });

        Assert.Equal(3, replies.Count);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("yeah", listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("yes", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("create/is_it_a_keeper", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules")[0].GetString());
        Assert.Equal("create", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("domain").GetString());
        Assert.Equal("create/is_it_a_keeper", listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
    }

    [Fact]
    public async Task ClientAsr_YesNoPromptFromAsrHints_MapsShortDenialToNoIntent()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-yesno-hints-token",
            Text = """{"type":"LISTEN","transID":"trans-yesno-hints","data":{"rules":["surprises-ota/want_to_download_now"],"asr":{"hints":["$YESNO"]}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-yesno-hints-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-yesno-hints","data":{"text":"no"}}"""
        });

        Assert.Equal(3, replies.Count);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("no", listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("no", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("surprises-ota/want_to_download_now", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules")[0].GetString());
        Assert.Equal("surprises-ota/want_to_download_now", listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
    }

    [Fact]
    public async Task ClientAsr_YesNoPromptFromAsrHints_MapsYepToYesIntent()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-yesno-hints-yep-token",
            Text = """{"type":"LISTEN","transID":"trans-yesno-hints-yep","data":{"rules":["surprises-ota/want_to_download_now"],"asr":{"hints":["$YESNO"]}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-yesno-hints-yep-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-yesno-hints-yep","data":{"text":"yep"}}"""
        });

        Assert.Equal(3, replies.Count);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("yep", listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("yes", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("surprises-ota/want_to_download_now", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules")[0].GetString());
        Assert.Equal("surprises-ota/want_to_download_now", listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
    }

    [Fact]
    public async Task ClientAsr_SharedYesNoPrompt_StripsGlobalRulesAndStaysLocal()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-shared-yesno-token",
            Text = """{"type":"LISTEN","transID":"trans-shared-yesno","data":{"rules":["shared/yes_no","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"],"asr":{"hints":["$YESNO"]}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-shared-yesno-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-shared-yesno","data":{"text":"yes"}}"""
        });

        Assert.Equal(3, replies.Count);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("yes", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        var rules = listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules");
        Assert.Single(rules.EnumerateArray());
        Assert.Equal("shared/yes_no", rules[0].GetString());
        Assert.Equal("shared/yes_no", listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
    }

    [Fact]
    public async Task ClientAsr_SharedYesNoPrompt_MapsNegativeWordToNoIntent()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-shared-yesno-negative-token",
            Text = """{"type":"LISTEN","transID":"trans-shared-yesno-negative","data":{"rules":["shared/yes_no","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"],"asr":{"hints":["$YESNO"]}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-shared-yesno-negative-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-shared-yesno-negative","data":{"text":"negative"}}"""
        });

        Assert.Equal(3, replies.Count);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("no", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        var rules = listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules");
        Assert.Single(rules.EnumerateArray());
        Assert.Equal("shared/yes_no", rules[0].GetString());
        Assert.Equal("shared/yes_no", listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
    }

    [Fact]
    public async Task ClientAsr_AlarmTimerChangeYesNoPrompt_StripsGlobalRulesAndStaysLocal()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-alarm-change-yesno-token",
            Text = """{"type":"LISTEN","transID":"trans-alarm-change-yesno","data":{"rules":["clock/alarm_timer_change","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"],"asr":{"hints":["$YESNO"]}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-alarm-change-yesno-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-alarm-change-yesno","data":{"text":"yes"}}"""
        });

        Assert.Equal(3, replies.Count);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("yes", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        var rules = listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules");
        Assert.Single(rules.EnumerateArray());
        Assert.Equal("clock/alarm_timer_change", rules[0].GetString());
        Assert.Equal("clock/alarm_timer_change", listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
    }

    [Fact]
    public async Task BufferedAudio_YesNoPromptWithSttFailure_AutoFinalizesAsLocalNoInput()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-yesno-noinput-token",
            Text = """{"type":"LISTEN","transID":"trans-yesno-noinput","data":{"rules":["surprises-ota/want_to_download_now","globals/gui_nav","globals/global_commands_launch"],"asr":{"hints":["$YESNO"]}}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-yesno-noinput-token",
            Text = """{"type":"CONTEXT","transID":"trans-yesno-noinput","data":{"topic":"conversation"}}"""
        });

        for (var index = 0; index < 4; index += 1)
        {
            var interimReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-yesno-noinput-token",
                Binary = new byte[3000]
            });

            Assert.Empty(interimReplies);
        }

        var session = _store.FindSessionByToken("hub-yesno-noinput-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2);
        session.TurnState.LastSttError = "whisper.cpp returned no transcript";

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-yesno-noinput-token",
            Binary = new byte[3000]
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal(string.Empty, listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal(string.Empty, listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        var rules = listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules");
        Assert.Single(rules.EnumerateArray());
        Assert.Equal("surprises-ota/want_to_download_now", rules[0].GetString());
    }

    [Fact]
    public async Task BufferedAudio_SharedYesNoPromptWithSttFailure_AutoFinalizesAsLocalNoInput()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-shared-yesno-noinput-token",
            Text = """{"type":"LISTEN","transID":"trans-shared-yesno-noinput","data":{"rules":["shared/yes_no","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"],"asr":{"hints":["$YESNO"]}}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-shared-yesno-noinput-token",
            Text = """{"type":"CONTEXT","transID":"trans-shared-yesno-noinput","data":{"topic":"conversation"}}"""
        });

        for (var index = 0; index < 4; index += 1)
        {
            var interimReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-shared-yesno-noinput-token",
                Binary = new byte[3000]
            });

            Assert.Empty(interimReplies);
        }

        var session = _store.FindSessionByToken("hub-shared-yesno-noinput-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2);
        session.TurnState.LastSttError = "ffmpeg decode failed";

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-shared-yesno-noinput-token",
            Binary = new byte[3000]
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        var rules = listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules");
        Assert.Single(rules.EnumerateArray());
        Assert.Equal("shared/yes_no", rules[0].GetString());
    }

    [Fact]
    public async Task ClientAsr_CreateKeeperRepeatedNoInput_RedirectsToIdle()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-create-noinput-token",
            Text = """{"type":"LISTEN","transID":"trans-create-noinput-1","data":{"rules":["create/is_it_a_keeper","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"]}}"""
        });

        var firstReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-create-noinput-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-create-noinput-1","data":{}}"""
        });

        Assert.Equal(2, firstReplies.Count);
        Assert.Equal("LISTEN", ReadReplyType(firstReplies[0]));
        Assert.Equal("EOS", ReadReplyType(firstReplies[1]));

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-create-noinput-token",
            Text = """{"type":"LISTEN","transID":"trans-create-noinput-2","data":{"rules":["create/is_it_a_keeper","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"]}}"""
        });

        var secondReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-create-noinput-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-create-noinput-2","data":{}}"""
        });

        Assert.Equal(3, secondReplies.Count);
        Assert.Equal("LISTEN", ReadReplyType(secondReplies[0]));
        Assert.Equal("EOS", ReadReplyType(secondReplies[1]));
        Assert.Equal("SKILL_REDIRECT", ReadReplyType(secondReplies[2]));

        using var redirectPayload = JsonDocument.Parse(secondReplies[2].Text!);
        Assert.Equal("@be/idle", redirectPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
    }

    [Fact]
    public async Task ClientAsr_SurprisesDateOfferPrompt_MapsYesWithoutGlobalRuleLeak()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-share-yesno-token",
            Text = """{"type":"LISTEN","transID":"trans-share-yes","data":{"rules":["surprises-date/offer_date_fact","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"],"asr":{"hints":["$YESNO"]}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-share-yesno-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-share-yes","data":{"text":"Yes!"}}"""
        });

        Assert.Equal(3, replies.Count);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("yes", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        var rules = listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules");
        Assert.Single(rules.EnumerateArray());
        Assert.Equal("surprises-date/offer_date_fact", rules[0].GetString());
        Assert.Equal("surprises-date/offer_date_fact", listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
        Assert.Empty(listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").EnumerateObject());
    }

    [Fact]
    public void ResponsePlanMapper_EscapesSpeechWithoutEncodingApostrophes()
    {
        var plan = new ResponsePlan
        {
            IntentName = "chat",
            Actions =
            {
                new SpeakAction
                {
                    Sequence = 0,
                    Text = "I'm glad you're here.",
                    Voice = "griffin"
                },
                new InvokeNativeSkillAction
                {
                    Sequence = 1,
                    SkillName = "chitchat-skill",
                    Payload = new Dictionary<string, object?>()
                }
            }
        };

        var turn = new TurnContext
        {
            Attributes = new Dictionary<string, object?>
            {
                ["transID"] = "trans-apostrophe"
            }
        };

        var replies = ResponsePlanToSocketMessagesMapper.Map(plan, turn, new CloudSession(), emitSkillActions: true);
        using var payload = JsonDocument.Parse(replies[2].Text);
        var esml = payload.RootElement
            .GetProperty("data")
            .GetProperty("action")
            .GetProperty("config")
            .GetProperty("jcp")
            .GetProperty("config")
            .GetProperty("play")
            .GetProperty("esml")
            .GetString();

        Assert.Contains("I'm glad you're here.", esml, StringComparison.Ordinal);
        Assert.DoesNotContain("&apos;", esml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClientAsr_TellMeTheNews_EmitsNimbusCloudSkillMatchAndNewsSkillAction()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-news-token",
            Text = """{"type":"LISTEN","transID":"trans-news","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-news-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-news","data":{"text":"tell me the news"}}"""
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("news", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("news", listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("cloudSkill").GetString());

        using var skillPayload = JsonDocument.Parse(replies[2].Text!);
        Assert.Equal("news", skillPayload.RootElement.GetProperty("data").GetProperty("skill").GetProperty("id").GetString());
        var meta = skillPayload.RootElement
            .GetProperty("data")
            .GetProperty("action")
            .GetProperty("config")
            .GetProperty("jcp")
            .GetProperty("config")
            .GetProperty("play")
            .GetProperty("meta");
        Assert.Equal("runtime-news", meta.GetProperty("mim_id").GetString());
        Assert.Equal("announcement", meta.GetProperty("mim_type").GetString());
    }

    [Fact]
    public async Task ClientAsr_TellMeTheNews_WithProvider_UsesProviderHeadlinesInSpeech()
    {
        var service = CreateService(
            new InMemoryCloudStateStore(),
            newsBriefingProvider: new StubNewsBriefingProvider(
                new NewsBriefingSnapshot(
                    [
                        new NewsHeadline("Robotics club opens a new community lab"),
                        new NewsHeadline("Local students win a regional coding challenge")
                    ],
                    "NewsAPI")));

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-news-provider-token",
            Text = """{"type":"LISTEN","transID":"trans-news-provider","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        var replies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-news-provider-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-news-provider","data":{"text":"tell me the news"}}"""
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));

        using var speakPayload = JsonDocument.Parse(replies[2].Text!);
        var esml = speakPayload.RootElement
            .GetProperty("data")
            .GetProperty("action")
            .GetProperty("config")
            .GetProperty("jcp")
            .GetProperty("config")
            .GetProperty("play")
            .GetProperty("esml")
            .GetString();

        Assert.Contains("Robotics club opens a new community lab", esml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Source: NewsAPI.", esml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClientAsr_HowIsTheWeather_EmitsSpokenWeatherFallbackWithoutRedirect()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-weather-token",
            Text = """{"type":"LISTEN","transID":"trans-weather","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-weather-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-weather","data":{"text":"how is the weather"}}"""
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("weather", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.False(listenPayload.RootElement.GetProperty("data").GetProperty("nlu").TryGetProperty("skill", out _));
        Assert.Equal(JsonValueKind.Null, listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("cloudSkill").ValueKind);

        using var speakPayload = JsonDocument.Parse(replies[2].Text!);
        var esml = speakPayload.RootElement
            .GetProperty("data")
            .GetProperty("action")
            .GetProperty("config")
            .GetProperty("jcp")
            .GetProperty("config")
            .GetProperty("play")
            .GetProperty("esml")
            .GetString();
        Assert.Contains("weather service is connected", esml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClientAsr_WillItRainTomorrow_EmitsSpokenWeatherFallbackWithoutRedirect()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-weather-entities-token",
            Text = """{"type":"LISTEN","transID":"trans-weather-entities","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-weather-entities-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-weather-entities","data":{"text":"will it rain tomorrow"}}"""
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("weather", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());

        using var speakPayload = JsonDocument.Parse(replies[2].Text!);
        var esml = speakPayload.RootElement
            .GetProperty("data")
            .GetProperty("action")
            .GetProperty("config")
            .GetProperty("jcp")
            .GetProperty("config")
            .GetProperty("play")
            .GetProperty("esml")
            .GetString();
        Assert.Contains("weather service is connected", esml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClientAsr_HowIsTheWeather_WithProvider_EmitsWeatherHiLoGuiCard()
    {
        var customStore = new InMemoryCloudStateStore();
        var customService = CreateService(
            customStore,
            new StubWeatherReportProvider(
                new WeatherReportSnapshot("Boston, US", "light rain", 61, 65, 54, "rain", false)));

        await customService.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-weather-provider-token",
            Text = """{"type":"LISTEN","transID":"trans-weather-provider","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        var replies = await customService.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-weather-provider-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-weather-provider","data":{"text":"how is the weather"}}"""
        });

        Assert.True(replies.Count >= 3);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Contains(replies, static reply => string.Equals(ReadReplyType(reply), "SKILL_ACTION", StringComparison.Ordinal));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.False(listenPayload.RootElement.GetProperty("data").GetProperty("nlu").TryGetProperty("skill", out _));
        Assert.Equal("weather", listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("cloudSkill").GetString());

        var skillReply = replies.Last(static reply => string.Equals(ReadReplyType(reply), "SKILL_ACTION", StringComparison.Ordinal));
        using var skillPayload = JsonDocument.Parse(skillReply.Text!);
        Assert.Equal(
            "report-skill",
            skillPayload.RootElement.GetProperty("data").GetProperty("skill").GetProperty("id").GetString());
        var jcpConfig = skillPayload.RootElement
            .GetProperty("data")
            .GetProperty("action")
            .GetProperty("config")
            .GetProperty("jcp")
            .GetProperty("config");

        var esml = jcpConfig.GetProperty("play").GetProperty("esml").GetString();
        Assert.Contains("cat='weather'", esml, StringComparison.OrdinalIgnoreCase);

        Assert.True(jcpConfig.TryGetProperty("gui", out var gui));
        Assert.Equal("Javascript", gui.GetProperty("type").GetString());
        Assert.Equal("views.weatherHiLo", gui.GetProperty("data").GetString());
        Assert.True(gui.GetProperty("pause").GetBoolean());

        Assert.Equal(6, jcpConfig.GetProperty("timeout").GetInt32());
        Assert.Equal(0, jcpConfig.GetProperty("no_matches_for_gui").GetInt32());
        Assert.Equal(0, jcpConfig.GetProperty("no_inputs_for_gui").GetInt32());

        Assert.True(jcpConfig.TryGetProperty("display", out var display));
        Assert.Equal(
            "weatherTempView",
            display.GetProperty("view").GetProperty("data").GetProperty("viewConfig").GetProperty("id").GetString());
        Assert.Equal(
            "weatherTempView",
            display.GetProperty("view").GetProperty("context").GetProperty("data").GetProperty("viewConfig").GetProperty("id").GetString());

        Assert.True(jcpConfig.TryGetProperty("views", out var views));
        var weatherHiLo = views.GetProperty("weatherHiLo");
        Assert.Equal("weatherTempView", weatherHiLo.GetProperty("viewConfig").GetProperty("id").GetString());

        Assert.True(jcpConfig.TryGetProperty("local", out var local));
        Assert.Equal(
            "weatherTempView",
            local.GetProperty("views").GetProperty("weatherHiLo").GetProperty("viewConfig").GetProperty("id").GetString());

        var payloadText = skillReply.Text!;
        Assert.Contains("assets/personal-report-skill/weather/icons/rain_v01.crn", payloadText, StringComparison.Ordinal);
        Assert.Contains("tempNormal_v01.crn", payloadText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClientAsr_OpenTheRadio_EmitsRadioRedirectAndSilentCompletion()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-radio-open-token",
            Text = """{"type":"LISTEN","transID":"trans-radio-open","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-radio-open-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-radio-open","data":{"text":"open the radio"}}"""
        });

        Assert.Equal(4, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_REDIRECT", ReadReplyType(replies[2]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[3]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("menu", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("@be/radio", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("skill").GetString());
        Assert.Equal(0, listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules").GetArrayLength());
        Assert.Empty(listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").EnumerateObject());

        using var redirectPayload = JsonDocument.Parse(replies[2].Text!);
        Assert.Equal("@be/radio", redirectPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.True(redirectPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("launch").GetBoolean());
        Assert.Equal("menu", redirectPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());

        using var completionPayload = JsonDocument.Parse(replies[3].Text!);
        Assert.Equal("@be/radio", completionPayload.RootElement.GetProperty("data").GetProperty("skill").GetProperty("id").GetString());
    }

    [Fact]
    public async Task ClientAsr_PlayCountryMusic_EmitsRadioRedirectWithCountryStation()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-radio-country-token",
            Text = """{"type":"LISTEN","transID":"trans-radio-country","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-radio-country-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-radio-country","data":{"text":"play country music"}}"""
        });

        Assert.Equal(4, replies.Count);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("menu", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("Country", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("station").GetString());

        using var redirectPayload = JsonDocument.Parse(replies[2].Text!);
        Assert.Equal("Country", redirectPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("station").GetString());
        Assert.Equal("play country music", redirectPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
    }

    [Fact]
    public async Task ClientAsr_StopThat_EmitsGlobalStopAndIdleRedirect()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-stop-token",
            Text = """{"type":"LISTEN","transID":"trans-stop","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-stop-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-stop","data":{"text":"stop that"}}"""
        });

        Assert.Equal(4, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_REDIRECT", ReadReplyType(replies[2]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[3]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        var nlu = listenPayload.RootElement.GetProperty("data").GetProperty("nlu");
        Assert.Equal("stop", nlu.GetProperty("intent").GetString());
        Assert.Equal("global_commands", nlu.GetProperty("domain").GetString());
        Assert.Equal("globals/global_commands_launch", nlu.GetProperty("rules")[0].GetString());

        using var redirectPayload = JsonDocument.Parse(replies[2].Text!);
        Assert.Equal("@be/idle", redirectPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.Equal("stop", redirectPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
    }

    [Fact]
    public async Task ClientAsr_TurnItDown_EmitsGlobalVolumeDownWithoutCloudSpeech()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-volume-down-token",
            Text = """{"type":"LISTEN","transID":"trans-volume-down","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-volume-down-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-volume-down","data":{"text":"turn it down"}}"""
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        var nlu = listenPayload.RootElement.GetProperty("data").GetProperty("nlu");
        Assert.Equal("volumeDown", nlu.GetProperty("intent").GetString());
        Assert.Equal("global_commands", nlu.GetProperty("domain").GetString());
        Assert.Equal("null", nlu.GetProperty("entities").GetProperty("volumeLevel").GetString());
        Assert.Equal("globals/global_commands_launch", nlu.GetProperty("rules")[0].GetString());
    }

    [Fact]
    public async Task ClientAsr_SetVolumeToSix_EmitsGlobalVolumeToValue()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-volume-value-token",
            Text = """{"type":"LISTEN","transID":"trans-volume-value","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-volume-value-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-volume-value","data":{"text":"set volume to six"}}"""
        });

        Assert.Equal(2, replies.Count);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        var nlu = listenPayload.RootElement.GetProperty("data").GetProperty("nlu");
        Assert.Equal("volumeToValue", nlu.GetProperty("intent").GetString());
        Assert.Equal("6", nlu.GetProperty("entities").GetProperty("volumeLevel").GetString());
        Assert.Equal("global_commands", nlu.GetProperty("domain").GetString());
    }

    [Fact]
    public async Task ClientAsr_ShowVolumeControls_RedirectsIntoSettingsVolumeQuery()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-volume-query-token",
            Text = """{"type":"LISTEN","transID":"trans-volume-query","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-volume-query-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-volume-query","data":{"text":"show volume controls"}}"""
        });

        Assert.Equal(4, replies.Count);
        Assert.Equal("SKILL_REDIRECT", ReadReplyType(replies[2]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[3]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("volumeQuery", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("@be/settings", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("skill").GetString());

        using var redirectPayload = JsonDocument.Parse(replies[2].Text!);
        Assert.Equal("@be/settings", redirectPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.Equal("volumeQuery", redirectPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
    }

    [Fact]
    public async Task ClientNlu_WordOfDayGuess_UsesGuessEntityAsAsrTextAndCompletesTurn()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-guess-token",
            Text = """{"type":"LISTEN","transID":"trans-wod-guess","data":{"rules":["word-of-the-day/puzzle","globals/gui_nav"],"asr":{"hints":["pastoral","doodad","escarpment"],"earlyEOS":["pastoral","doodad","escarpment"]}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-guess-token",
            Text = """{"type":"CLIENT_NLU","transID":"trans-wod-guess","data":{"entities":{"guess":"pastoral"},"intent":"guess","rules":["word-of-the-day/puzzle"]}}"""
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("pastoral", listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("guess", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("pastoral", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("guess").GetString());
        Assert.Equal("word-of-the-day/puzzle", listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
    }

    [Fact]
    public async Task ClientAsr_WordOfDayGuess_UsesSpokenTranscriptDuringPuzzleTurn()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-spoken-guess-token",
            Text = """{"type":"LISTEN","transID":"trans-wod-spoken-guess","data":{"rules":["word-of-the-day/puzzle"],"asr":{"hints":["pastoral","doodad","escarpment"]}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-spoken-guess-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-wod-spoken-guess","data":{"text":"pastoral"}}"""
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("pastoral", listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("guess", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("pastoral", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("guess").GetString());
        Assert.Equal("word-of-the-day/puzzle", listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
    }

    [Fact]
    public async Task ClientAsr_WordOfDayGuess_LineNumberUsesHintOrder()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-line-guess-token",
            Text = """{"type":"LISTEN","transID":"trans-wod-line-guess","data":{"rules":["word-of-the-day/puzzle"],"asr":{"hints":["doodad","pastoral","escarpment"]}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-line-guess-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-wod-line-guess","data":{"text":"Two."}}"""
        });

        Assert.Equal(3, replies.Count);
        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("pastoral", listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("guess", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("pastoral", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("guess").GetString());
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));
    }

    [Fact]
    public async Task ClientAsr_WordOfDayGuess_FuzzyMatchesClosestHint()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-fuzzy-guess-token",
            Text = """{"type":"LISTEN","transID":"trans-wod-fuzzy-guess","data":{"rules":["word-of-the-day/puzzle"],"asr":{"hints":["aglet","hovel","wisenheimer"]}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-fuzzy-guess-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-wod-fuzzy-guess","data":{"text":"Haglet."}}"""
        });

        Assert.Equal(3, replies.Count);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("aglet", listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("aglet", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("guess").GetString());
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));
    }

    [Fact]
    public async Task ClientAsr_WordOfDayGuess_StripsGlobalRulesFromOutboundGuess()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-guess-rules-token",
            Text = """{"type":"LISTEN","transID":"trans-wod-guess-rules","data":{"rules":["word-of-the-day/puzzle","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"],"asr":{"hints":["aglet","hovel","wisenheimer"]}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-guess-rules-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-wod-guess-rules","data":{"text":"aglet"}}"""
        });

        Assert.Equal(3, replies.Count);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        var rules = listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules");
        Assert.Single(rules.EnumerateArray());
        Assert.Equal("word-of-the-day/puzzle", rules[0].GetString());
        Assert.Equal("word-of-the-day/puzzle", listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
    }

    [Fact]
    public async Task ClientAsr_SettingsDownloadNo_StripsGlobalRulesFromOutboundNo()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-settings-no-token",
            Text = """{"type":"LISTEN","transID":"trans-settings-no","data":{"rules":["settings/download_now_later","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"],"asr":{"hints":["$YESNO"]}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-settings-no-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-settings-no","data":{"text":"No."}}"""
        });

        Assert.Equal(3, replies.Count);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        var rules = listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules");
        Assert.Single(rules.EnumerateArray());
        Assert.Equal("settings/download_now_later", rules[0].GetString());
        Assert.Equal("settings/download_now_later", listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
        Assert.Equal("no", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
    }

    [Fact]
    public async Task ClientAsr_WordOfDayLaunch_EmitsMenuStyleLoadMenuAndRedirect()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-launch-token",
            Text = """{"type":"LISTEN","transID":"trans-wod-launch","data":{"rules":["launch","globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-launch-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-wod-launch","data":{"text":"Play word of the day."}}"""
        });

        Assert.Equal(4, replies.Count);
        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("menu", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal(string.Empty, listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("word-of-the-day", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("domain").GetString());
        Assert.Equal("@be/word-of-the-day", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("skill").GetString());
        Assert.Equal("word-of-the-day/menu", listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
        Assert.Equal("SKILL_REDIRECT", ReadReplyType(replies[2]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[3]));

        using var redirectPayload = JsonDocument.Parse(replies[2].Text!);
        Assert.Equal("@be/word-of-the-day", redirectPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.True(redirectPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("onRobot").GetBoolean());
        Assert.True(redirectPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("launch").GetBoolean());

        var session = _store.FindSessionByToken("hub-wod-launch-token");
        Assert.NotNull(session);
        Assert.False(session.FollowUpOpen);
    }

    [Fact]
    public async Task AutoFinalizedWordOfDayLaunch_IgnoresLateSameTurnAudio()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-auto-token",
            Text = """{"type":"LISTEN","transID":"trans-wod-auto","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-auto-token",
            Text = """{"type":"CONTEXT","transID":"trans-wod-auto","data":{"audioTranscriptHint":"play word of the day"}}"""
        });

        for (var index = 0; index < 4; index += 1)
        {
            var interimReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-wod-auto-token",
                Binary = new byte[3000]
            });

            Assert.Empty(interimReplies);
        }

        var session = _store.FindSessionByToken("hub-wod-auto-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2);

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-auto-token",
            Binary = new byte[3000]
        });

        Assert.Equal(4, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_REDIRECT", ReadReplyType(replies[2]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[3]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("menu", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("word-of-the-day", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("domain").GetString());
        Assert.Equal("@be/word-of-the-day", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("skill").GetString());

        var lateReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-auto-token",
            Binary = new byte[3000]
        });

        Assert.Empty(lateReplies);
        Assert.False(session.TurnState.AwaitingTurnCompletion);
    }

    [Fact]
    public async Task EmptyClientAsr_AfterCompletedWordOfDayTurn_IsIgnored()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-late-empty-token",
            Text = """{"type":"LISTEN","transID":"trans-wod-late-empty","data":{"rules":["word-of-the-day/puzzle"]}}"""
        });

        var winReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-late-empty-token",
            Text = """{"type":"CLIENT_NLU","transID":"trans-wod-late-empty","data":{"entities":{"guess":"pastoral"},"intent":"guess","rules":["word-of-the-day/puzzle"]}}"""
        });

        Assert.Equal(3, winReplies.Count);

        var lateReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-late-empty-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-wod-late-empty","data":{}}"""
        });

        Assert.Empty(lateReplies);
    }

    [Fact]
    public async Task EmptyClientAsr_AfterWordOfDayRightWordListen_IsIgnored()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-right-word-token",
            Text = """{"type":"LISTEN","transID":"trans-wod-right-word","data":{"rules":["word-of-the-day/right_word","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-right-word-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-wod-right-word","data":{}}"""
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_REDIRECT", ReadReplyType(replies[2]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.False(listenPayload.RootElement.GetProperty("data").TryGetProperty("match", out _));

        using var redirectPayload = JsonDocument.Parse(replies[2].Text!);
        Assert.Equal("@be/idle", redirectPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
    }

    [Fact]
    public async Task ListenSetupWithoutTranscript_ReturnsPendingInsteadOfFinalizingTurn()
    {
        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-listen-setup-token",
            Text = """{"type":"LISTEN","transID":"trans-listen-setup","data":{"rules":["main-menu/execute_fun_stuff","globals/global_commands_launch"],"mode":"CLIENT_NLU"}}"""
        });

        Assert.Empty(replies);

        var session = _store.FindSessionByToken("hub-listen-setup-token");
        Assert.NotNull(session);
        Assert.True(session.TurnState.AwaitingTurnCompletion);
        Assert.Null(session.LastIntent);
    }

    [Fact]
    public async Task StaleListenSetup_IsRecoveredWhenNextHotphraseListenArrives()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-stale-listen-token",
            Text = """{"type":"LISTEN","transID":"trans-stale-listen","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        var session = _store.FindSessionByToken("hub-stale-listen-token");
        Assert.NotNull(session);
        session.TurnState.ListenOpenedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(12);
        session.TurnState.AwaitingTurnCompletion = true;
        session.TurnState.SawListen = true;
        session.TurnState.SawContext = false;
        session.TurnState.BufferedAudioBytes = 0;
        session.TurnState.BufferedAudioChunkCount = 0;
        session.TurnState.HotphraseEmptyTurnCount = 2;

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-stale-listen-token",
            Text = """{"type":"LISTEN","transID":"trans-stale-listen","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        Assert.Empty(replies);
        Assert.True(session.TurnState.AwaitingTurnCompletion);
        Assert.True(session.TurnState.SawListen);
        Assert.False(session.TurnState.SawContext);
        Assert.Equal(0, session.TurnState.BufferedAudioBytes);
        Assert.Equal(0, session.TurnState.BufferedAudioChunkCount);
        Assert.Equal(0, session.TurnState.HotphraseEmptyTurnCount);
        Assert.True(session.TurnState.ListenOpenedUtc > DateTimeOffset.UtcNow - TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task BinaryAudio_AfterWordOfDayRightWordListen_IsIgnoredDuringCleanupWindow()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-right-word-audio-token",
            Text = """{"type":"LISTEN","transID":"trans-wod-right-word-audio","data":{"rules":["word-of-the-day/right_word","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-right-word-audio-token",
            Text = """{"type":"LISTEN","transID":"trans-wod-right-word-audio","data":{"rules":["word-of-the-day/right_word","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"]}}"""
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_REDIRECT", ReadReplyType(replies[2]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal(string.Empty, listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.False(listenPayload.RootElement.GetProperty("data").TryGetProperty("match", out _));

        var binaryReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-right-word-audio-token",
            Binary = new byte[4096]
        });

        Assert.Empty(binaryReplies);

        var session = _store.FindSessionByToken("hub-wod-right-word-audio-token");
        Assert.NotNull(session);
        Assert.False(session.TurnState.AwaitingTurnCompletion);
        Assert.True(session.TurnState.IgnoreAdditionalAudioUntilUtc.HasValue);
    }

    [Fact]
    public async Task BlankAudioHotphraseTurn_IsIgnored()
    {
        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-blank-audio-token",
            Text = """{"type":"LISTEN","transID":"trans-blank-audio","data":{"text":"[BLANK_AUDIO]","rules":["launch","globals/global_commands_launch"]}}"""
        });

        Assert.Empty(replies);

        var session = _store.FindSessionByToken("hub-blank-audio-token");
        Assert.NotNull(session);
        Assert.False(session.TurnState.AwaitingTurnCompletion);
        Assert.False(session.TurnState.SawListen);
        Assert.True(session.TurnState.IgnoreAdditionalAudioUntilUtc.HasValue);

        var binaryReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-blank-audio-token",
            Binary = new byte[4096]
        });

        Assert.Empty(binaryReplies);
        Assert.Equal(0, session.TurnState.BufferedAudioBytes);
        Assert.Equal(0, session.TurnState.BufferedAudioChunkCount);
    }

    [Fact]
    public async Task ContextWithoutListen_DuringFollowUp_DoesNotBufferAudio()
    {
        var helloReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-context-no-listen-token",
            Text = """{"type":"LISTEN","transID":"trans-context-no-listen-hello","data":{"text":"hello","rules":["launch","globals/global_commands_launch"]}}"""
        });

        Assert.Equal(3, helloReplies.Count);

        var session = _store.FindSessionByToken("hub-context-no-listen-token");
        Assert.NotNull(session);
        Assert.True(session.FollowUpOpen);

        var contextReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-context-no-listen-token",
            Text = """{"type":"CONTEXT","transID":"trans-context-no-listen-tail","data":{"skill":{"id":null}}}"""
        });

        Assert.Empty(contextReplies);
        Assert.False(session.TurnState.SawListen);

        for (var index = 0; index < 6; index += 1)
        {
            var binaryReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-context-no-listen-token",
                Binary = new byte[4096]
            });

            Assert.Empty(binaryReplies);
        }

        Assert.False(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal(0, session.TurnState.BufferedAudioBytes);
        Assert.Equal(0, session.TurnState.BufferedAudioChunkCount);
    }

    [Fact]
    public async Task Listen_DeleteTheAlarm_UsesLocalClockWithoutKeepingFollowUpOpen()
    {
        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-delete-the-alarm-token",
            Text = """{"type":"LISTEN","transID":"trans-delete-the-alarm","data":{"text":"So, delete the alarm.","rules":["launch","globals/global_commands_launch"]}}"""
        });

        Assert.Equal(4, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_REDIRECT", ReadReplyType(replies[2]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[3]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("delete", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("@be/clock", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("skill").GetString());
        Assert.Equal("alarm", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("domain").GetString());

        var session = _store.FindSessionByToken("hub-delete-the-alarm-token");
        Assert.NotNull(session);
        Assert.False(session.FollowUpOpen);
    }

    [Fact]
    public async Task InitialHotphraseListen_RemainsPendingInsteadOfGreeting()
    {
        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-initial-hotphrase-token",
            Text = """{"type":"LISTEN","transID":"trans-initial-hotphrase","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        Assert.Empty(replies);

        var session = _store.FindSessionByToken("hub-initial-hotphrase-token");
        Assert.NotNull(session);
        Assert.Null(session.LastIntent);
        Assert.Null(session.LastTranscript);
    }

    [Fact]
    public async Task SecondEmptyHotphraseTurn_BecomesGreetingAndKeepsFollowUpOpen()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-empty-hotphrase-token",
            Text = """{"type":"LISTEN","transID":"trans-empty-hotphrase","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        var firstReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-empty-hotphrase-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-empty-hotphrase","data":{}}"""
        });

        Assert.Empty(firstReplies);

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-empty-hotphrase-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-empty-hotphrase","data":{}}"""
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("hello", listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("hello", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());

        var session = _store.FindSessionByToken("hub-empty-hotphrase-token");
        Assert.NotNull(session);
        Assert.True(session.FollowUpOpen);
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

        Assert.Empty(listenReplies);

        var contextReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-audio-token",
            Text = """{"type":"CONTEXT","transID":"trans-audio","data":{"topic":"conversation","audioTranscriptHint":"tell me a joke"}}"""
        });

        Assert.Empty(contextReplies);

        var audioReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-audio-token",
            Binary = [1, 2, 3, 4, 5, 6]
        });

        Assert.Empty(audioReplies);

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
        Assert.Equal(0, session.TurnState.BufferedAudioBytes);
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

        Assert.Empty(finalizeReplies);

        var session = _store.FindSessionByToken("hub-pending-token");
        Assert.NotNull(session);
        Assert.True(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal(1, session.TurnState.FinalizeAttemptCount);
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
        Assert.Equal("hello", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());

        using var skillPayload = JsonDocument.Parse(finalizeReplies[2].Text!);
        Assert.Equal("chitchat-skill", skillPayload.RootElement.GetProperty("data").GetProperty("skill").GetProperty("id").GetString());
    }

    [Fact]
    public async Task BufferedHotphraseAudio_WithSttFailure_BecomesGreetingAndKeepsFollowUpOpen()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-greeting-token",
            Text = """{"type":"LISTEN","transID":"trans-hotphrase-greeting","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-greeting-token",
            Text = """{"type":"CONTEXT","transID":"trans-hotphrase-greeting","data":{"topic":"conversation"}}"""
        });

        for (var index = 0; index < 4; index += 1)
        {
            var interimReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-hotphrase-greeting-token",
                Binary = new byte[3000]
            });

            Assert.Empty(interimReplies);
        }

        var session = _store.FindSessionByToken("hub-hotphrase-greeting-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2);
        session.TurnState.LastSttError = "ffmpeg decode failed";

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-greeting-token",
            Binary = new byte[3000]
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("hello", listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("hello", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());

        Assert.True(session.FollowUpOpen);
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
    public async Task ClientAsrDanceFlow_EmitsAnimatedSkillAction()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-client-asr-dance-token",
            Text = """{"type":"LISTEN","transID":"trans-dance-shape","data":{"rules":["wake-word"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-client-asr-dance-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-dance-shape","data":{"text":"do a dance"}}"""
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));

        using var skillPayload = JsonDocument.Parse(replies[2].Text!);
        var esml = skillPayload.RootElement
            .GetProperty("data")
            .GetProperty("action")
            .GetProperty("config")
            .GetProperty("jcp")
            .GetProperty("config")
            .GetProperty("play")
            .GetProperty("esml")
            .GetString();

        Assert.Contains("<anim cat='dance' filter='music, ", esml, StringComparison.Ordinal);
        Assert.Equal("chitchat-skill", skillPayload.RootElement.GetProperty("data").GetProperty("skill").GetProperty("id").GetString());
    }

    [Fact]
    public async Task ClientAsrMakePizzaFlow_UsesLegacyPizzaMimAndAnimation()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-client-asr-pizza-token",
            Text = """{"type":"LISTEN","transID":"trans-pizza-shape","data":{"rules":["wake-word"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-client-asr-pizza-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-pizza-shape","data":{"text":"make a pizza"}}"""
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));

        using var skillPayload = JsonDocument.Parse(replies[2].Text!);
        Assert.Equal("chitchat-skill", skillPayload.RootElement.GetProperty("data").GetProperty("skill").GetProperty("id").GetString());

        var play = skillPayload.RootElement
            .GetProperty("data")
            .GetProperty("action")
            .GetProperty("config")
            .GetProperty("jcp")
            .GetProperty("config")
            .GetProperty("play");

        var esml = play.GetProperty("esml").GetString();
        Assert.Contains("pizza-making", esml, StringComparison.Ordinal);

        var meta = play.GetProperty("meta");
        Assert.Equal("RA_JBO_MakePizza", meta.GetProperty("mim_id").GetString());
        Assert.Equal("announcement", meta.GetProperty("mim_type").GetString());
        Assert.StartsWith("RA_JBO_ShowPizzaMaking_AN_", meta.GetProperty("prompt_id").GetString(), StringComparison.Ordinal);
        Assert.Equal("AN", meta.GetProperty("prompt_sub_category").GetString());
    }

    [Fact]
    public async Task ClientAsrOrderAPizzaFlow_UsesLegacyOrderPizzaMim()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-client-asr-order-pizza-token",
            Text = """{"type":"LISTEN","transID":"trans-order-pizza-shape","data":{"rules":["wake-word"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-client-asr-order-pizza-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-order-pizza-shape","data":{"text":"order a pizza"}}"""
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));

        using var skillPayload = JsonDocument.Parse(replies[2].Text!);
        Assert.Equal("chitchat-skill", skillPayload.RootElement.GetProperty("data").GetProperty("skill").GetProperty("id").GetString());

        var play = skillPayload.RootElement
            .GetProperty("data")
            .GetProperty("action")
            .GetProperty("config")
            .GetProperty("jcp")
            .GetProperty("config")
            .GetProperty("play");

        Assert.Contains("I can't do that yet", play.GetProperty("esml").GetString(), StringComparison.Ordinal);

        var meta = play.GetProperty("meta");
        Assert.Equal("RA_JBO_OrderPizza", meta.GetProperty("mim_id").GetString());
        Assert.Equal("announcement", meta.GetProperty("mim_type").GetString());
        Assert.Equal("RA_JBO_OrderPizza_AN_01", meta.GetProperty("prompt_id").GetString());
        Assert.Equal("AN", meta.GetProperty("prompt_sub_category").GetString());
    }

    [Fact]
    public async Task ClientAsrPersonalMemory_BirthdayIsScopedPerDeviceTenant()
    {
        var tokenA = _store.IssueRobotToken("tenant-device-a");
        var tokenB = _store.IssueRobotToken("tenant-device-b");

        var setReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = tokenA,
            Text = """{"type":"CLIENT_ASR","transID":"trans-memory-set","data":{"text":"my birthday is april 12"}}"""
        });

        Assert.Equal(3, setReplies.Count);
        using (var setListenPayload = JsonDocument.Parse(setReplies[0].Text!))
        {
            Assert.Equal("memory_set_birthday", setListenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        }

        var sameTenantRecallReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = tokenA,
            Text = """{"type":"CLIENT_ASR","transID":"trans-memory-recall-a","data":{"text":"what is my birthday"}}"""
        });

        Assert.Equal(3, sameTenantRecallReplies.Count);
        using (var skillPayload = JsonDocument.Parse(sameTenantRecallReplies[2].Text!))
        {
            var esml = skillPayload.RootElement
                .GetProperty("data")
                .GetProperty("action")
                .GetProperty("config")
                .GetProperty("jcp")
                .GetProperty("config")
                .GetProperty("play")
                .GetProperty("esml")
                .GetString();
            Assert.Contains("You told me your birthday is april 12", esml, StringComparison.OrdinalIgnoreCase);
        }

        var otherTenantRecallReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = tokenB,
            Text = """{"type":"CLIENT_ASR","transID":"trans-memory-recall-b","data":{"text":"what is my birthday"}}"""
        });

        Assert.Equal(3, otherTenantRecallReplies.Count);
        using var otherSkillPayload = JsonDocument.Parse(otherTenantRecallReplies[2].Text!);
        var otherEsml = otherSkillPayload.RootElement
            .GetProperty("data")
            .GetProperty("action")
            .GetProperty("config")
            .GetProperty("jcp")
            .GetProperty("config")
            .GetProperty("play")
            .GetProperty("esml")
            .GetString();
        Assert.Contains("I do not know your birthday yet", otherEsml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClientAsrSurpriseOffer_PersistsPendingOfferAndResolvesYesFollowUp()
    {
        var token = _store.IssueRobotToken("proactivity-device-a");

        var offerReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text = """{"type":"CLIENT_ASR","transID":"trans-proactive-offer","data":{"text":"surprise me"}}"""
        });

        Assert.Equal(3, offerReplies.Count);
        using (var offerListenPayload = JsonDocument.Parse(offerReplies[0].Text!))
        {
            Assert.Equal("proactive_offer_pizza_fact", offerListenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
            Assert.Equal("shared/yes_no", offerListenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules")[0].GetString());
            Assert.Equal("shared/yes_no", offerListenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
        }
        using (var offerSkillPayload = JsonDocument.Parse(offerReplies[2].Text!))
        {
            var jcpConfig = offerSkillPayload.RootElement
                .GetProperty("data")
                .GetProperty("action")
                .GetProperty("config")
                .GetProperty("jcp")
                .GetProperty("config");
            Assert.Equal("Q", jcpConfig.GetProperty("play").GetProperty("meta").GetProperty("prompt_sub_category").GetString());
            Assert.Equal("question", jcpConfig.GetProperty("play").GetProperty("meta").GetProperty("mim_type").GetString());
            Assert.Equal("LISTEN", jcpConfig.GetProperty("listen").GetProperty("type").GetString());
            Assert.Equal("shared/yes_no", jcpConfig.GetProperty("listen").GetProperty("contexts")[0].GetString());
        }

        var session = _store.FindSessionByToken(token);
        Assert.NotNull(session);
        Assert.True(session.Metadata.TryGetValue("pendingProactivityOffer", out var pendingOffer));
        Assert.Equal("pizza_fact", pendingOffer?.ToString());

        var followUpReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text = """{"type":"CLIENT_ASR","transID":"trans-proactive-offer-yes","data":{"text":"yes"}}"""
        });

        Assert.Equal(3, followUpReplies.Count);
        using (var followUpListenPayload = JsonDocument.Parse(followUpReplies[0].Text!))
        {
            Assert.Equal("proactive_pizza_fact", followUpListenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        }

        session = _store.FindSessionByToken(token);
        Assert.NotNull(session);
        Assert.False(session.Metadata.ContainsKey("pendingProactivityOffer"));
    }

    [Fact]
    public async Task ClientAsrSurpriseOffer_PersistsPendingOfferAndResolvesYesFollowUpWithTail()
    {
        var token = _store.IssueRobotToken("proactivity-device-a-tail");

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text = """{"type":"CLIENT_ASR","transID":"trans-proactive-offer-tail","data":{"text":"surprise me"}}"""
        });

        var followUpReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text = """{"type":"CLIENT_ASR","transID":"trans-proactive-offer-tail-yes","data":{"text":"yes I want to"}}"""
        });

        Assert.Equal(3, followUpReplies.Count);
        using var followUpListenPayload = JsonDocument.Parse(followUpReplies[0].Text!);
        Assert.Equal("proactive_pizza_fact", followUpListenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());

        var session = _store.FindSessionByToken(token);
        Assert.NotNull(session);
        Assert.False(session.Metadata.ContainsKey("pendingProactivityOffer"));
    }

    [Fact]
    public async Task ClientAsrSurpriseOffer_PersistsPendingOfferAndResolvesNoFollowUp()
    {
        var token = _store.IssueRobotToken("proactivity-device-b");

        var offerReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text = """{"type":"CLIENT_ASR","transID":"trans-proactive-offer-no","data":{"text":"surprise me"}}"""
        });

        Assert.Equal(3, offerReplies.Count);
        using (var offerListenPayload = JsonDocument.Parse(offerReplies[0].Text!))
        {
            Assert.Equal("proactive_offer_pizza_fact", offerListenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
            Assert.Equal("shared/yes_no", offerListenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules")[0].GetString());
        }

        var session = _store.FindSessionByToken(token);
        Assert.NotNull(session);
        Assert.True(session.Metadata.TryGetValue("pendingProactivityOffer", out var pendingOffer));
        Assert.Equal("pizza_fact", pendingOffer?.ToString());

        var followUpReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text = """{"type":"CLIENT_ASR","transID":"trans-proactive-offer-no-followup","data":{"text":"no"}}"""
        });

        Assert.Equal(3, followUpReplies.Count);
        using (var followUpListenPayload = JsonDocument.Parse(followUpReplies[0].Text!))
        {
            Assert.Equal("proactive_offer_declined", followUpListenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        }

        using (var followUpSkillPayload = JsonDocument.Parse(followUpReplies[2].Text!))
        {
            var esml = followUpSkillPayload.RootElement
                .GetProperty("data")
                .GetProperty("action")
                .GetProperty("config")
                .GetProperty("jcp")
                .GetProperty("config")
                .GetProperty("play")
                .GetProperty("esml")
                .GetString();
            Assert.Contains("No problem", esml, StringComparison.OrdinalIgnoreCase);
        }

        session = _store.FindSessionByToken(token);
        Assert.NotNull(session);
        Assert.False(session.Metadata.ContainsKey("pendingProactivityOffer"));
    }

    [Fact]
    public async Task ClientAsrSurpriseOffer_PersistsPendingOfferAndResolvesNoFollowUpWithTail()
    {
        var token = _store.IssueRobotToken("proactivity-device-b-tail");

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text = """{"type":"CLIENT_ASR","transID":"trans-proactive-offer-no-tail","data":{"text":"surprise me"}}"""
        });

        var followUpReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text = """{"type":"CLIENT_ASR","transID":"trans-proactive-offer-no-tail-followup","data":{"text":"no I do not"}}"""
        });

        Assert.Equal(3, followUpReplies.Count);
        using var followUpListenPayload = JsonDocument.Parse(followUpReplies[0].Text!);
        Assert.Equal("proactive_offer_declined", followUpListenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());

        var session = _store.FindSessionByToken(token);
        Assert.NotNull(session);
        Assert.False(session.Metadata.ContainsKey("pendingProactivityOffer"));
    }

    [Fact]
    public async Task TriggerPresence_WithIdentity_EmitsProactiveGreetingAndPersistsGreetingMetadata()
    {
        var token = _store.IssueRobotToken("trigger-greeting-device-a");

        var listenSetupReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text = """{"type":"LISTEN","transID":"trans-greeting-trigger","data":{"rules":["launch","globals/global_commands_launch"],"mode":"CLIENT_NLU"}}"""
        });
        Assert.Empty(listenSetupReplies);

        var contextReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text = """{"type":"CONTEXT","transID":"trans-greeting-trigger","data":{"runtime":{"perception":{"speaker":"person-1","peoplePresent":[{"id":"person-1"}]},"loop":{"users":[{"id":"person-1","firstName":"jake"}]}}}}"""
        });
        Assert.Empty(contextReplies);

        var triggerReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text = """{"type":"TRIGGER","transID":"trans-greeting-trigger","data":{"triggerSource":"PRESENCE","triggerData":{"looperID":"person-1"}}}"""
        });

        Assert.Equal(3, triggerReplies.Count);
        Assert.Equal("LISTEN", ReadReplyType(triggerReplies[0]));
        Assert.Equal("EOS", ReadReplyType(triggerReplies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(triggerReplies[2]));

        using (var listenPayload = JsonDocument.Parse(triggerReplies[0].Text!))
        {
            Assert.Equal("proactive_greeting", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        }
        using (var skillPayload = JsonDocument.Parse(triggerReplies[2].Text!))
        {
            var esml = skillPayload.RootElement
                .GetProperty("data")
                .GetProperty("action")
                .GetProperty("config")
                .GetProperty("jcp")
                .GetProperty("config")
                .GetProperty("play")
                .GetProperty("esml")
                .GetString();
            Assert.Contains("Jake", esml, StringComparison.Ordinal);
        }

        var session = _store.FindSessionByToken(token);
        Assert.NotNull(session);
        Assert.False(session.FollowUpOpen);
        Assert.True(session.Metadata.TryGetValue("greetingsRoute", out var route));
        Assert.Equal("ProactiveGreeting", route?.ToString());
        Assert.True(session.Metadata.TryGetValue("greetingsSpeaker", out var speaker));
        Assert.Equal("person-1", speaker?.ToString());
        Assert.True(session.Metadata.TryGetValue("greetingsLastProactiveUtc", out var lastUtc));
        Assert.True(DateTimeOffset.TryParse(lastUtc?.ToString(), out _));
    }

    [Fact]
    public async Task TriggerSurprise_IsIgnoredWithoutLeavingMicOpen()
    {
        var token = _store.IssueRobotToken("trigger-greeting-device-b");

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text = """{"type":"LISTEN","transID":"trans-greeting-trigger-ignore","data":{"rules":["launch"],"mode":"CLIENT_NLU"}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text = """{"type":"CONTEXT","transID":"trans-greeting-trigger-ignore","data":{"runtime":{"perception":{"speaker":"person-1"},"loop":{"users":[{"id":"person-1","firstName":"jake"}]}}}}"""
        });

        var triggerReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text = """{"type":"TRIGGER","transID":"trans-greeting-trigger-ignore","data":{"triggerSource":"SURPRISE","triggerData":{"looperID":"person-1"}}}"""
        });

        Assert.Equal(3, triggerReplies.Count);
        Assert.Equal("LISTEN", ReadReplyType(triggerReplies[0]));
        Assert.Equal("EOS", ReadReplyType(triggerReplies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(triggerReplies[2]));

        using (var listenPayload = JsonDocument.Parse(triggerReplies[0].Text!))
        {
            Assert.Equal("trigger_ignored", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        }

        var session = _store.FindSessionByToken(token);
        Assert.NotNull(session);
        Assert.False(session.FollowUpOpen);
        Assert.False(session.Metadata.ContainsKey("greetingsLastProactiveUtc"));
    }

    [Fact]
    public async Task ClientAsrPersonalReport_StateMachinePersistsAcrossTurns()
    {
        const string stateKey = "personalReportState";
        var token = _store.IssueRobotToken("personal-report-device-a");

        var startReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text = """{"type":"CLIENT_ASR","transID":"trans-personal-report-start","data":{"text":"personal report"}}"""
        });

        Assert.Equal(3, startReplies.Count);
        using (var startListenPayload = JsonDocument.Parse(startReplies[0].Text!))
        {
            Assert.Equal("personal_report_opt_in", startListenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        }

        var session = _store.FindSessionByToken(token);
        Assert.NotNull(session);
        Assert.True(session.Metadata.TryGetValue(stateKey, out var stateValue));
        Assert.Equal("awaiting_opt_in", stateValue?.ToString());

        var optInReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text = """{"type":"CLIENT_ASR","transID":"trans-personal-report-optin","data":{"text":"yes"}}"""
        });

        Assert.Equal(3, optInReplies.Count);
        using (var optInListenPayload = JsonDocument.Parse(optInReplies[0].Text!))
        {
            Assert.Equal("personal_report_request_name", optInListenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        }

        session = _store.FindSessionByToken(token);
        Assert.NotNull(session);
        Assert.True(session.Metadata.TryGetValue(stateKey, out stateValue));
        Assert.Equal("awaiting_identity_name", stateValue?.ToString());

        var identifyReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text = """{"type":"CLIENT_ASR","transID":"trans-personal-report-name","data":{"text":"my name is alex"}}"""
        });

        Assert.Equal(3, identifyReplies.Count);
        using (var identifyListenPayload = JsonDocument.Parse(identifyReplies[0].Text!))
        {
            Assert.Equal("personal_report_delivered", identifyListenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        }

        session = _store.FindSessionByToken(token);
        Assert.NotNull(session);
        Assert.True(session.Metadata.TryGetValue(stateKey, out stateValue));
        Assert.Equal("idle", stateValue?.ToString());
    }

    [Fact]
    public async Task ClientAsrChitchatEmotionCommand_PersistsSplitRouteMetadata()
    {
        const string routeKey = "chitchatRoute";
        const string emotionKey = "chitchatEmotion";
        var token = _store.IssueRobotToken("chitchat-emotion-device-a");

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text = """{"type":"CLIENT_ASR","transID":"trans-chitchat-emotion","data":{"text":"smile"}}"""
        });

        Assert.Equal(3, replies.Count);
        using (var listenPayload = JsonDocument.Parse(replies[0].Text!))
        {
            Assert.Equal("emotion_command", listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        }

        var session = _store.FindSessionByToken(token);
        Assert.NotNull(session);
        Assert.True(session.Metadata.TryGetValue(routeKey, out var routeValue));
        Assert.Equal("EmotionCommand", routeValue?.ToString());
        Assert.True(session.Metadata.TryGetValue(emotionKey, out var emotionValue));
        Assert.Equal("happy", emotionValue?.ToString());
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

        Assert.Equal(4, followUpReplies.Count);
        using var payload = JsonDocument.Parse(followUpReplies[0].Text!);
        Assert.Equal("askForTime", payload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("trans-second", payload.RootElement.GetProperty("transID").GetString());

        var session = _store.FindSessionByToken("hub-followup-audio-token");
        Assert.NotNull(session);
        Assert.Equal("trans-second", session.TurnState.TransId);
        Assert.Equal(0, session.TurnState.BufferedAudioBytes);
        Assert.Equal(0, session.TurnState.BufferedAudioChunkCount);
    }

    [Fact]
    public async Task NewTransId_OnContext_LeavesNoStaleBufferedAudioBeforeFollowUpTurn()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-context-reset-token",
            Text = """{"type":"LISTEN","transID":"trans-first","data":{"rules":["wake-word"]}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-context-reset-token",
            Text = """{"type":"CONTEXT","transID":"trans-first","data":{"audioTranscriptHint":"tell me a joke"}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-context-reset-token",
            Binary = [1, 2, 3, 4]
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-context-reset-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-first","data":{}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-context-reset-token",
            Binary = "\t\t\t\t"u8.ToArray()
        });

        var session = _store.FindSessionByToken("hub-context-reset-token");
        Assert.NotNull(session);
        Assert.Equal(0, session.TurnState.BufferedAudioBytes);

        var contextReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-context-reset-token",
            Text = """{"type":"CONTEXT","transID":"trans-second","data":{"topic":"conversation"}}"""
        });

        Assert.Empty(contextReplies);

        session = _store.FindSessionByToken("hub-context-reset-token");
        Assert.NotNull(session);
        Assert.Equal("trans-second", session.TurnState.TransId);
        Assert.Equal(0, session.TurnState.BufferedAudioBytes);
        Assert.Equal(0, session.TurnState.BufferedAudioChunkCount);
    }

    [Theory]
    [InlineData("fixtures\\neo-hub-client-asr-joke.flow.json")]
    [InlineData("fixtures\\neo-hub-context-client-nlu.flow.json")]
    [InlineData("fixtures\\neo-hub-client-nlu-clock-ask-time.flow.json")]
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

            if (step.ExpectedReplies.Count <= 0) continue;

            Assert.Equal(replies.Count, step.ExpectedReplies.Count);

            for (var index = 0; index < step.ExpectedReplies.Count; index += 1)
            {
                var expectedReply = step.ExpectedReplies[index];
                Assert.Equal(expectedReply.Type, actualTypes[index]);

                if (expectedReply.DelayMs.HasValue)
                {
                    Assert.Equal(expectedReply.DelayMs.Value, replies[index].DelayMs);
                }

                if (expectedReply.JsonSubset is not { ValueKind: JsonValueKind.Object } jsonSubset) continue;

                using var actualPayload = JsonDocument.Parse(replies[index].Text!);
                AssertJsonContains(jsonSubset, actualPayload.RootElement);
            }
        }
    }

    private static void AssertJsonContains(JsonElement expected, JsonElement actual)
    {
        Assert.Equal(expected.ValueKind, actual.ValueKind);

        // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
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

    private static JiboWebSocketService CreateService(
        InMemoryCloudStateStore stateStore,
        IWeatherReportProvider? weatherReportProvider = null,
        INewsBriefingProvider? newsBriefingProvider = null)
    {
        var contentRepository = new InMemoryJiboExperienceContentRepository();
        var contentCache = new JiboExperienceContentCache(contentRepository);
        var interactionService = new JiboInteractionService(
            contentCache,
            new DefaultJiboRandomizer(),
            new InMemoryPersonalMemoryStore(),
            weatherReportProvider,
            newsBriefingProvider);
        var conversationBroker = new DemoConversationBroker(interactionService);
        var sttSelector = new DefaultSttStrategySelector(
        [
            new SyntheticBufferedAudioSttStrategy()
        ]);
        var sink = new NullTurnTelemetrySink();

        return new JiboWebSocketService(
            stateStore,
            new NullWebSocketTelemetrySink(),
            new WebSocketTurnFinalizationService(conversationBroker, sttSelector, sink));
    }

    private static string ReadReplyType(WebSocketReply reply)
    {
        using var payload = JsonDocument.Parse(reply.Text!);
        return payload.RootElement.GetProperty("type").GetString() ?? string.Empty;
    }

    private sealed class StubWeatherReportProvider(WeatherReportSnapshot snapshot) : IWeatherReportProvider
    {
        public Task<WeatherReportSnapshot?> GetReportAsync(
            WeatherReportRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<WeatherReportSnapshot?>(snapshot);
        }
    }

    private sealed class StubNewsBriefingProvider(NewsBriefingSnapshot snapshot) : INewsBriefingProvider
    {
        public Task<NewsBriefingSnapshot?> GetBriefingAsync(
            NewsBriefingRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<NewsBriefingSnapshot?>(snapshot);
        }
    }
}
