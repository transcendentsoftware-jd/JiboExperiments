using System.Text;
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
    private readonly JiboWebSocketService _service;
    private readonly InMemoryCloudStateStore _store;

    public JiboWebSocketServiceTests()
    {
        _store = new InMemoryCloudStateStore();
        var contentRepository = new InMemoryJiboExperienceContentRepository();
        var contentCache = new JiboExperienceContentCache(contentRepository);
        var conversationBroker = new DemoConversationBroker(new JiboInteractionService(contentCache,
            new LastItemRandomizer(), new InMemoryPersonalMemoryStore()));
        var sttSelector = new DefaultSttStrategySelector(
        [
            new SyntheticBufferedAudioSttStrategy()
        ]);
        var sink = new NullTurnTelemetrySink();
        var turnFinalizationService = new WebSocketTurnFinalizationService(conversationBroker,
            sttSelector,
            sink);

        _service = new JiboWebSocketService(
            _store,
            new NullWebSocketTelemetrySink(),
            turnFinalizationService);
    }

    [Fact]
    public async Task BinaryAudio_RejectsOversizedFrameWithoutRetainingPayload()
    {
        var token = _store.IssueHubToken("audio-limit-robot");
        var session = _store.OpenSession("neo-hub-listen", null, token, "neo-hub.jibo.com", "/listen");
        session.TurnState.SawListen = true;
        session.TurnState.SawContext = true;
        session.TurnState.AwaitingTurnCompletion = true;

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Binary = new byte[1024 * 1024 + 1]
        });

        var active = Assert.IsType<CloudSession>(_store.FindActiveSessionByToken(token));
        Assert.Empty(replies);
        Assert.Equal(0, active.TurnState.BufferedAudioBytes);
        Assert.Empty(active.TurnState.BufferedAudioFrames);
        Assert.False(active.TurnState.AwaitingTurnCompletion);
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
        Assert.Equal("hello jibo",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("hello",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.True(listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skipSurprises")
            .GetBoolean());

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
    public async Task IdleWatchdog_ClosesStalledListenWithEos()
    {
        var envelope = new WebSocketMessageEnvelope
        {
            ConnectionId = "connection-idle-watchdog",
            HostName = "neohub.openjibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-idle-watchdog-token",
            Text =
                """{"type":"LISTEN","transID":"trans-idle-watchdog","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        };

        var setupReplies = await _service.HandleMessageAsync(envelope);
        Assert.Empty(setupReplies);

        var session = _store.FindSessionByToken(envelope.Token!);
        Assert.NotNull(session);
        session.TurnState.ListenOpenedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10);

        var replies = await _service.HandleIdleAsync(session, envelope);

        Assert.Contains(replies, reply => string.Equals(ReadReplyType(reply), "EOS", StringComparison.Ordinal));
        Assert.False(session.TurnState.AwaitingTurnCompletion);
        Assert.False(session.TurnState.SawListen);
        Assert.Equal("no-input", session.LastListenType);
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
            Text =
                """{"type":"LISTEN","transID":"trans-cloud-version","data":{"text":"What's your cloud version?","hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("cloud_version",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.True(listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skipSurprises")
            .GetBoolean());

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
        Assert.True(session.TurnState.IgnoreAdditionalAudioUntilUtc > DateTimeOffset.UtcNow.AddSeconds(6));
        Assert.True(session.TurnState.IgnoreLateListenSetupUntilUtc > DateTimeOffset.UtcNow);
        Assert.True(session.TurnState.IgnoreLateListenSetupUntilUtc < DateTimeOffset.UtcNow.AddSeconds(3));

        session.TurnState.IgnoreLateListenSetupUntilUtc = DateTimeOffset.UtcNow.AddSeconds(1);
        session.TurnState.IgnoreAdditionalAudioUntilUtc = DateTimeOffset.UtcNow.AddSeconds(5);

        var guardedListenReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-cloud-version-token",
            Text =
                """{"type":"LISTEN","transID":"trans-cloud-version","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        Assert.Equal(3, guardedListenReplies.Count);
        Assert.Equal("LISTEN", ReadReplyType(guardedListenReplies[0]));
        Assert.Equal("EOS", ReadReplyType(guardedListenReplies[1]));
        Assert.Equal("SKILL_REDIRECT", ReadReplyType(guardedListenReplies[2]));
        using (var lateListenPayload = JsonDocument.Parse(guardedListenReplies[0].Text!))
        {
            Assert.Equal("trans-cloud-version",
                lateListenPayload.RootElement.GetProperty("transID").GetString());
            Assert.Equal("launch",
                lateListenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules")[0]
                    .GetString());
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

        session.TurnState.IgnoreLateListenSetupUntilUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(1);

        var freshListenReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-cloud-version-token",
            Text =
                """{"type":"LISTEN","transID":"trans-cloud-version-fresh","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        Assert.Empty(freshListenReplies);
        Assert.Equal("trans-cloud-version-fresh", session.TurnState.TransId);
        Assert.True(session.TurnState.AwaitingTurnCompletion);
        Assert.True(session.TurnState.SawListen);
        Assert.Null(session.TurnState.IgnoreAdditionalAudioUntilUtc);
        Assert.Null(session.TurnState.IgnoreLateListenSetupUntilUtc);
    }

    [Fact]
    public async Task Listen_AfterClockCommand_AcceptsNewHotphraseListenWithinLateListenWindow()
    {
        var firstReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-then-hotphrase-token",
            Text =
                """{"type":"LISTEN","transID":"trans-clock-then-hotphrase","data":{"text":"what time is it","hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        Assert.Equal(2, firstReplies.Count);
        Assert.Equal("LISTEN", ReadReplyType(firstReplies[0]));
        Assert.Equal("EOS", ReadReplyType(firstReplies[1]));
        Assert.DoesNotContain(firstReplies, reply =>
            string.Equals(ReadReplyType(reply), "SKILL_REDIRECT", StringComparison.Ordinal));

        var session = _store.FindSessionByToken("hub-clock-then-hotphrase-token");
        Assert.NotNull(session);
        Assert.False(session.TurnState.AwaitingTurnCompletion);
        Assert.True(session.TurnState.IgnoreLateListenSetupUntilUtc > DateTimeOffset.UtcNow);

        session.TurnState.IgnoreLateListenSetupUntilUtc = DateTimeOffset.UtcNow.AddSeconds(1);

        var setupReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-then-hotphrase-token",
            Text =
                """{"type":"LISTEN","transID":"trans-clock-then-hotphrase-retry","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        Assert.Empty(setupReplies);
        Assert.Equal("trans-clock-then-hotphrase-retry", session.TurnState.TransId);
        Assert.True(session.TurnState.AwaitingTurnCompletion);
        Assert.True(session.TurnState.SawListen);

        var commandReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-then-hotphrase-token",
            Text =
                """{"type":"CLIENT_ASR","transID":"trans-clock-then-hotphrase-retry","data":{"text":"what time is it"}}"""
        });

        Assert.Equal(2, commandReplies.Count);
        Assert.Equal("LISTEN", ReadReplyType(commandReplies[0]));
        Assert.Equal("EOS", ReadReplyType(commandReplies[1]));
        Assert.DoesNotContain(commandReplies, reply =>
            string.Equals(ReadReplyType(reply), "SKILL_REDIRECT", StringComparison.Ordinal));

        using var listenPayload = JsonDocument.Parse(commandReplies[0].Text!);
        Assert.Equal("askForTime",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("@be/clock",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("skill").GetString());
    }

    [Fact]
    public async Task BufferedAudio_CloudVersionThenStop_FinalizesPromptlyAndKeepsStopClean()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-cloud-version-stop-token",
            Text =
                """{"type":"LISTEN","transID":"trans-cloud-version-stop","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-cloud-version-stop-token",
            Text =
                """{"type":"CONTEXT","transID":"trans-cloud-version-stop","data":{"audioTranscriptHint":"what's your cloud version"}}"""
        });

        IReadOnlyList<WebSocketReply> replies = [];
        for (var index = 0; index < 2; index += 1)
        {
            replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-cloud-version-stop-token",
                Binary = new byte[3000]
            });

            Assert.Empty(replies);
        }

        var session = _store.FindSessionByToken("hub-cloud-version-stop-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(600);

        replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-cloud-version-stop-token",
            Binary = new byte[3000]
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("cloud_version",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());

        var stopReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-cloud-version-stop-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-cloud-version-stop-stop","data":{"text":"stop"}}"""
        });

        Assert.Equal(2, stopReplies.Count);
        Assert.Equal("LISTEN", ReadReplyType(stopReplies[0]));
        Assert.Equal("EOS", ReadReplyType(stopReplies[1]));

        var reopenedListenReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-cloud-version-stop-token",
            Text =
                """{"type":"LISTEN","transID":"trans-cloud-version-stop-reopen","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        Assert.Empty(reopenedListenReplies);
        Assert.Equal("trans-cloud-version-stop-reopen", session.TurnState.TransId);
        Assert.True(session.TurnState.AwaitingTurnCompletion);
        Assert.True(session.TurnState.SawListen);
    }

    [Fact]
    public async Task BufferedHotphraseOggAudio_WaitsThroughMidStreamGapButFinalizesOnEos()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-gap-token",
            Text =
                """{"type":"LISTEN","transID":"trans-hotphrase-ogg-gap","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-gap-token",
            Text =
                """{"type":"CONTEXT","transID":"trans-hotphrase-ogg-gap","data":{"audioTranscriptHint":"tell me something"}}"""
        });

        foreach (var frame in new[]
                 {
                     BuildOggFrame(0x02, "OpusHead"),
                     BuildOggFrame(0x00, "OpusTags")
                 })
        {
            var interimReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-hotphrase-ogg-gap-token",
                Binary = frame
            });

            Assert.Empty(interimReplies);
        }

        var session = _store.FindSessionByToken("hub-hotphrase-ogg-gap-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(600);

        var gapReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-gap-token",
            Binary = BuildOggFrame(0x00)
        });

        Assert.Empty(gapReplies);
        Assert.True(session.TurnState.AwaitingTurnCompletion);

        var eosReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-gap-token",
            Binary = BuildOggFrame(0x04)
        });

        Assert.Empty(eosReplies);
        Assert.True(session.TurnState.AwaitingTurnCompletion);
    }

    [Fact]
    public async Task BufferedHotphraseOggAudio_FinalizesOnHardAge_WhenOggNeverSendsEos()
    {
        var stateStore = new InMemoryCloudStateStore();
        var service = CreateService(stateStore, sttStrategies:
        [
            new QueuedBufferedAudioSttStrategy("Hey Jibo, what's your cloud version?")
        ]);

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-hard-timeout-token",
            Text =
                """{"type":"LISTEN","transID":"trans-hotphrase-ogg-hard-timeout","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-hard-timeout-token",
            Text =
                """{"type":"CONTEXT","transID":"trans-hotphrase-ogg-hard-timeout","data":{"audioTranscriptHint":"tell me something"}}"""
        });

        foreach (var frame in new[]
                 {
                     BuildOggFrame(0x02, "OpusHead"),
                     BuildOggFrame(0x00, "OpusTags"),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00)
                 })
        {
            var interimReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-hotphrase-ogg-hard-timeout-token",
                Binary = frame
            });

            Assert.Empty(interimReplies);
        }

        var session = stateStore.FindSessionByToken("hub-hotphrase-ogg-hard-timeout-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(8500);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(900);

        var replies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-hard-timeout-token",
            Binary = BuildOggFrame(0x00)
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("cloud_version",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
    }

    [Fact]
    public async Task BufferedHotphraseOggAudio_EarlyProbeCanFinalizeBeforeSilenceWindow()
    {
        var stateStore = new InMemoryCloudStateStore();
        var service = CreateService(stateStore, sttStrategies:
        [
            new QueuedBufferedAudioSttStrategy("Hey Jibo, what's your cloud version?")
        ]);

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-silence-token",
            Text =
                """{"type":"LISTEN","transID":"trans-hotphrase-ogg-silence","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-silence-token",
            Text =
                """{"type":"CONTEXT","transID":"trans-hotphrase-ogg-silence","data":{"audioTranscriptHint":"tell me something"}}"""
        });

        foreach (var frame in new[]
                 {
                     BuildOggFrame(0x02, "OpusHead"),
                     BuildOggFrame(0x00, "OpusTags"),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00)
                 })
        {
            var interimReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-hotphrase-ogg-silence-token",
                Binary = frame
            });

            Assert.Empty(interimReplies);
        }

        var session = stateStore.FindSessionByToken("hub-hotphrase-ogg-silence-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(5);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(1300);

        var replies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-silence-token",
            Binary = BuildOggFrame(0x00)
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));
        Assert.False(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal(0, session.TurnState.BufferedAudioBytes);
    }

    [Fact]
    public async Task BufferedHotphraseOggAudio_StreamingAtNineHundredMs_DoesNotCloseUntilSilence()
    {
        var stateStore = new InMemoryCloudStateStore();
        var service = CreateService(stateStore, sttStrategies:
        [
            new QueuedBufferedAudioSttStrategy("Hey Jibo, what time is it?")
        ]);

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-mid-utterance-token",
            Text =
                """{"type":"LISTEN","transID":"trans-hotphrase-ogg-mid-utterance","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-mid-utterance-token",
            Text =
                """{"type":"CONTEXT","transID":"trans-hotphrase-ogg-mid-utterance","data":{"skill":{"id":"@be/nimbus"}}}"""
        });

        foreach (var frame in new[]
                 {
                     BuildOggFrame(0x02, "OpusHead"),
                     BuildOggFrame(0x00, "OpusTags"),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00)
                 })
        {
            var interimReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-hotphrase-ogg-mid-utterance-token",
                Binary = frame
            });

            Assert.Empty(interimReplies);
        }

        var session = stateStore.FindSessionByToken("hub-hotphrase-ogg-mid-utterance-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(900);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow;

        var midUtteranceReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-mid-utterance-token",
            Binary = BuildOggFrame(0x00)
        });

        Assert.Empty(midUtteranceReplies);
        Assert.True(session.TurnState.AwaitingTurnCompletion);
        Assert.Null(session.LastIntent);

        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(2500);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(1300);

        var replies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-mid-utterance-token",
            Binary = BuildOggFrame(0x00)
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("what time is it",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("askForTime",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("@be/clock",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("skill").GetString());
        Assert.Equal("@be/clock",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.True(listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("onRobot")
            .GetBoolean());
        Assert.False(session.TurnState.AwaitingTurnCompletion);
    }

    [Fact]
    public async Task BufferedHotphraseOggAudio_ContinuousStreamWithoutEos_LaunchesClockAfterProbeAge()
    {
        var stateStore = new InMemoryCloudStateStore();
        var service = CreateService(stateStore, sttStrategies:
        [
            new QueuedBufferedAudioSttStrategy("Hey Jibo, what time is it?")
        ]);

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-continuous-clock-token",
            Text =
                """{"type":"LISTEN","transID":"trans-hotphrase-ogg-continuous-clock","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-continuous-clock-token",
            Text =
                """{"type":"CONTEXT","transID":"trans-hotphrase-ogg-continuous-clock","data":{"skill":{"id":"@be/nimbus"}}}"""
        });

        foreach (var frame in new[]
                 {
                     BuildOggFrame(0x02, "OpusHead"),
                     BuildOggFrame(0x00, "OpusTags"),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00)
                 })
        {
            var interimReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-hotphrase-ogg-continuous-clock-token",
                Binary = frame
            });

            Assert.Empty(interimReplies);
        }

        var session = stateStore.FindSessionByToken("hub-hotphrase-ogg-continuous-clock-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(2900);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow;

        var replies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-continuous-clock-token",
            Binary = BuildOggFrame(0x00)
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("what time is it",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("askForTime",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("@be/clock",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("skill").GetString());
        Assert.Equal("@be/clock",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.True(listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("onRobot")
            .GetBoolean());
        Assert.False(session.TurnState.AwaitingTurnCompletion);
    }

    [Fact]
    public async Task BufferedHotphraseOggAudio_BlankSttBeforeHardTimeoutKeepsListening()
    {
        var stateStore = new InMemoryCloudStateStore();
        var service = CreateService(stateStore, sttStrategies:
        [
            new QueuedBufferedAudioSttStrategy("", "", "")
        ]);

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-blank-before-hard-token",
            Text =
                """{"type":"LISTEN","transID":"trans-hotphrase-ogg-blank-before-hard","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        foreach (var frame in new[]
                 {
                     BuildOggFrame(0x02, "OpusHead"),
                     BuildOggFrame(0x00, "OpusTags"),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00)
                 })
        {
            var interimReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-hotphrase-ogg-blank-before-hard-token",
                Binary = frame
            });

            Assert.Empty(interimReplies);
        }

        var session = stateStore.FindSessionByToken("hub-hotphrase-ogg-blank-before-hard-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(5);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(1300);

        var firstBlankReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-blank-before-hard-token",
            Binary = BuildOggFrame(0x00)
        });

        Assert.Empty(firstBlankReplies);
        Assert.True(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal(1, session.TurnState.FinalizeAttemptCount);

        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(1300);

        var secondBlankReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-blank-before-hard-token",
            Binary = BuildOggFrame(0x00)
        });

        Assert.Empty(secondBlankReplies);
        Assert.True(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal(2, session.TurnState.FinalizeAttemptCount);

        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(8500);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(1300);

        var hardTimeoutReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-blank-before-hard-token",
            Binary = BuildOggFrame(0x00)
        });

        Assert.Equal(2, hardTimeoutReplies.Count);
        Assert.Equal("LISTEN", ReadReplyType(hardTimeoutReplies[0]));
        Assert.Equal("EOS", ReadReplyType(hardTimeoutReplies[1]));
        Assert.False(session.TurnState.AwaitingTurnCompletion);
    }

    [Fact]
    public async Task BufferedHotphraseOggAudio_MetadataOnlyListenClosesAsNoInputAfterTimeout()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-metadata-timeout-token",
            Text =
                """{"type":"LISTEN","transID":"trans-hotphrase-metadata-timeout","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        foreach (var frame in new[]
                 {
                     BuildOggFrame(0x02, "OpusHead"),
                     BuildOggFrame(0x00, "OpusTags")
                 })
        {
            var interimReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-hotphrase-metadata-timeout-token",
                Binary = frame
            });

            Assert.Empty(interimReplies);
        }

        var session = _store.FindSessionByToken("hub-hotphrase-metadata-timeout-token");
        Assert.NotNull(session);
        session.TurnState.ListenOpenedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(9500);

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-metadata-timeout-token",
            Binary = BuildOggFrame(0x00, "OpusTags")
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.False(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal(0, session.TurnState.BufferedAudioBytes);
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
    public async Task BufferedHotphraseOggAudio_HeaderOnlyPagesDoNotStartSpeechTimer()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-header-only-token",
            Text =
                """{"type":"LISTEN","transID":"trans-hotphrase-header-only","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        var headReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-header-only-token",
            Binary = BuildOggFrame(0x02, "OpusHead")
        });

        Assert.Empty(headReplies);

        var tagsReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-header-only-token",
            Binary = BuildOggFrame(0x00, "OpusTags")
        });

        Assert.Empty(tagsReplies);

        var session = _store.FindSessionByToken("hub-hotphrase-header-only-token");
        Assert.NotNull(session);
        Assert.Equal(2, session.TurnState.BufferedAudioChunkCount);
        Assert.Null(session.TurnState.FirstAudioReceivedUtc);
        Assert.Null(session.TurnState.LastAudioReceivedUtc);
        Assert.True(session.TurnState.AwaitingTurnCompletion);
    }

    [Fact]
    public async Task BufferedHotphraseOggAudio_HeadersThenAudioStartsSpeechTimerOnRealAudioOnly()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-headers-then-audio-token",
            Text =
                """{"type":"LISTEN","transID":"trans-hotphrase-headers-then-audio","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-headers-then-audio-token",
            Binary = BuildOggFrame(0x02, "OpusHead")
        });

        var afterHead = _store.FindSessionByToken("hub-hotphrase-headers-then-audio-token");
        Assert.NotNull(afterHead);
        Assert.Null(afterHead.TurnState.FirstAudioReceivedUtc);
        Assert.Null(afterHead.TurnState.LastAudioReceivedUtc);

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-headers-then-audio-token",
            Binary = BuildOggFrame(0x00, "OpusTags")
        });

        var afterTags = _store.FindSessionByToken("hub-hotphrase-headers-then-audio-token");
        Assert.NotNull(afterTags);
        Assert.Null(afterTags.TurnState.FirstAudioReceivedUtc);
        Assert.Null(afterTags.TurnState.LastAudioReceivedUtc);

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-headers-then-audio-token",
            Binary = BuildOggFrame(0x00)
        });

        var session = _store.FindSessionByToken("hub-hotphrase-headers-then-audio-token");
        Assert.NotNull(session);
        Assert.NotNull(session.TurnState.FirstAudioReceivedUtc);
        Assert.NotNull(session.TurnState.LastAudioReceivedUtc);
        Assert.True(session.TurnState.AwaitingTurnCompletion);
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
        for (var index = 0; index < 2; index += 1)
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
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(600);

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
        Assert.Equal("tell me a joke",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("joke",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
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
        for (var index = 0; index < 2; index += 1)
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
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(600);

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
        Assert.Equal("heyJibo",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal(string.Empty,
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.True(listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skipSurprises")
            .GetBoolean());
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
            Text =
                """{"type":"CONTEXT","transID":"trans-preference-continuation","data":{"audioTranscriptHint":"my favorite sport"}}"""
        });

        for (var index = 0; index < 3; index += 1)
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
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(600);

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
            Text =
                """{"type":"CONTEXT","transID":"trans-preference-continuation","data":{"audioTranscriptHint":"my favorite sport is football"}}"""
        });

        Assert.Equal(3, finalizedReplies.Count);
        Assert.Equal("LISTEN", ReadReplyType(finalizedReplies[0]));
        Assert.Equal("EOS", ReadReplyType(finalizedReplies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(finalizedReplies[2]));

        using var listenPayload = JsonDocument.Parse(finalizedReplies[0].Text!);
        Assert.Equal("memory_set_preference",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("my favorite sport is football",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
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
            Text =
                """{"type":"CONTEXT","transID":"trans-preference-bare","data":{"audioTranscriptHint":"my favorite sport football"}}"""
        });

        for (var index = 0; index < 3; index += 1)
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
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(600);

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
        Assert.Equal("memory_set_preference",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("my favorite sport football",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
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
            Text =
                """{"type":"CONTEXT","transID":"trans-affinity-continuation","data":{"audioTranscriptHint":"i do like"}}"""
        });

        for (var index = 0; index < 3; index += 1)
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
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(600);

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
            Text =
                """{"type":"CONTEXT","transID":"trans-affinity-continuation","data":{"audioTranscriptHint":"i do like pizza"}}"""
        });

        Assert.Equal(3, finalizedReplies.Count);
        Assert.Equal("LISTEN", ReadReplyType(finalizedReplies[0]));
        Assert.Equal("EOS", ReadReplyType(finalizedReplies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(finalizedReplies[2]));

        using var listenPayload = JsonDocument.Parse(finalizedReplies[0].Text!);
        Assert.Equal("memory_set_affinity",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("i do like pizza",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
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
            Text =
                """{"type":"CONTEXT","transID":"trans-affinity-we-continuation","data":{"audioTranscriptHint":"we like"}}"""
        });

        for (var index = 0; index < 3; index += 1)
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
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(600);

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
            Text =
                """{"type":"CONTEXT","transID":"trans-affinity-we-continuation","data":{"audioTranscriptHint":"we like pizza"}}"""
        });

        Assert.Equal(3, finalizedReplies.Count);
        Assert.Equal("LISTEN", ReadReplyType(finalizedReplies[0]));
        Assert.Equal("EOS", ReadReplyType(finalizedReplies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(finalizedReplies[2]));

        using var listenPayload = JsonDocument.Parse(finalizedReplies[0].Text!);
        Assert.Equal("memory_set_affinity",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("we like pizza",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-follow-up","data":{"text":"hello jibo","rules":["wake-word"]}}"""
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
            Text =
                """{"type":"LISTEN","transID":"trans-clock-time","data":{"lang":"en-US","rules":["clock/clock_menu","globals/global_commands_launch"],"mode":"CLIENT_NLU"}}"""
        });

        Assert.Empty(listenReplies);

        var nluReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-menu-token",
            Text =
                """{"type":"CLIENT_NLU","transID":"trans-clock-time","data":{"entities":{"domain":"clock"},"intent":"askForTime","rules":["clock/clock_menu"]}}"""
        });

        Assert.Equal(2, nluReplies.Count);
        Assert.Equal("LISTEN", ReadReplyType(nluReplies[0]));
        Assert.Equal("EOS", ReadReplyType(nluReplies[1]));

        using var listenPayload = JsonDocument.Parse(nluReplies[0].Text!);
        Assert.Equal("askForTime",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("askForTime",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("clock",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities")
                .GetProperty("domain").GetString());
        Assert.Equal("clock/clock_menu",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules")[0].GetString());
        Assert.Equal("clock/clock_menu",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-clock-timer","data":{"rules":["globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-timer-token",
            Text =
                """{"type":"CLIENT_ASR","transID":"trans-clock-timer","data":{"text":"set a timer for five minutes"}}"""
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.DoesNotContain(replies, reply =>
            string.Equals(ReadReplyType(reply), "SKILL_REDIRECT", StringComparison.Ordinal));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("start",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("@be/clock",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("skill").GetString());
        Assert.Equal("@be/clock",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.True(listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("launch")
            .GetBoolean());
        Assert.True(listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skipSurprises")
            .GetBoolean());
        Assert.Equal("timer",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities")
                .GetProperty("domain").GetString());
        Assert.Equal("0",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities")
                .GetProperty("hours").GetString());
        Assert.Equal("5",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities")
                .GetProperty("minutes").GetString());
        Assert.Equal("null",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities")
                .GetProperty("seconds").GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-clock-open","data":{"rules":["globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-open-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-clock-open","data":{"text":"open the clock"}}"""
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.DoesNotContain(replies, reply =>
            string.Equals(ReadReplyType(reply), "SKILL_REDIRECT", StringComparison.Ordinal));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("askForTime",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("clock",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities")
                .GetProperty("domain").GetString());
        Assert.Equal("@be/clock",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.True(listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("launch")
            .GetBoolean());
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
            Text =
                """{"type":"LISTEN","transID":"trans-clock-voice-time","data":{"rules":["globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-voice-time-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-clock-voice-time","data":{"text":"what time is it"}}"""
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.DoesNotContain(replies, reply =>
            string.Equals(ReadReplyType(reply), "SKILL_REDIRECT", StringComparison.Ordinal));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("askForTime",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("@be/clock",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("skill").GetString());
        Assert.Equal("@be/clock",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.True(listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("launch")
            .GetBoolean());
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
            Text =
                """{"type":"LISTEN","transID":"trans-clock-alarm","data":{"rules":["globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-alarm-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-clock-alarm","data":{"text":"set an alarm for 7:30 am"}}"""
        });

        Assert.Equal(2, replies.Count);
        Assert.DoesNotContain(replies, reply =>
            string.Equals(ReadReplyType(reply), "SKILL_REDIRECT", StringComparison.Ordinal));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("start",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("@be/clock",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("skill").GetString());
        Assert.Equal("@be/clock",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.Equal("alarm",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities")
                .GetProperty("domain").GetString());
        Assert.Equal("7:30",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("time")
                .GetString());
        Assert.Equal("am",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("ampm")
                .GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-clock-compact-alarm","data":{"rules":["globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-compact-alarm-token",
            Text =
                """{"type":"CLIENT_ASR","transID":"trans-clock-compact-alarm","data":{"text":"set an alarm for 830"}}"""
        });

        Assert.Equal(2, replies.Count);
        Assert.DoesNotContain(replies, reply =>
            string.Equals(ReadReplyType(reply), "SKILL_REDIRECT", StringComparison.Ordinal));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("8:30",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("time")
                .GetString());
        Assert.Equal("am",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("ampm")
                .GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-clock-hyphen-alarm","data":{"rules":["globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-hyphen-alarm-token",
            Text =
                """{"type":"CLIENT_ASR","transID":"trans-clock-hyphen-alarm","data":{"text":"set an alarm for 10-25"}}"""
        });

        Assert.Equal(2, replies.Count);
        Assert.DoesNotContain(replies, reply =>
            string.Equals(ReadReplyType(reply), "SKILL_REDIRECT", StringComparison.Ordinal));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("start",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("alarm",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities")
                .GetProperty("domain").GetString());
        Assert.Equal("10:25",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("time")
                .GetString());
        Assert.Equal("am",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("ampm")
                .GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-clock-pm-alarm","data":{"rules":["globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-pm-alarm-token",
            Text =
                """{"type":"CLIENT_ASR","transID":"trans-clock-pm-alarm","data":{"text":"set an alarm for 10:25 pm"}}"""
        });

        Assert.Equal(2, replies.Count);
        Assert.DoesNotContain(replies, reply =>
            string.Equals(ReadReplyType(reply), "SKILL_REDIRECT", StringComparison.Ordinal));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("10:25",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("time")
                .GetString());
        Assert.Equal("pm",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("ampm")
                .GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-clock-next-occurrence","data":{"rules":["globals/global_commands_launch"]}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-next-occurrence-token",
            Text =
                """{"type":"CONTEXT","transID":"trans-clock-next-occurrence","data":{"runtime":{"location":{"iso":"2026-04-22T07:15:00-05:00"}}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-next-occurrence-token",
            Text =
                """{"type":"CLIENT_ASR","transID":"trans-clock-next-occurrence","data":{"text":"set an alarm for 7:10"}}"""
        });

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("7:10",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("time")
                .GetString());
        Assert.Equal("pm",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("ampm")
                .GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-clock-timer-followup","data":{"rules":["clock/timer_set_value"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-timer-followup-token",
            Text =
                """{"type":"CLIENT_ASR","transID":"trans-clock-timer-followup","data":{"text":"twenty five minutes"}}"""
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("start",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("timer",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities")
                .GetProperty("domain").GetString());
        Assert.Equal("25",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities")
                .GetProperty("minutes").GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-clock-alarm-followup","data":{"rules":["clock/alarm_set_value"]}}"""
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
        Assert.Equal("start",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("alarm",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities")
                .GetProperty("domain").GetString());
        Assert.Equal("10:25",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("time")
                .GetString());
        Assert.Equal("am",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("ampm")
                .GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-clock-alarm-comma-followup","data":{"rules":["clock/alarm_set_value"]}}"""
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
        Assert.Equal("start",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("alarm",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities")
                .GetProperty("domain").GetString());
        Assert.Equal("7:44",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty("time")
                .GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-clock-clarify-alarm","data":{"rules":["globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-clarify-alarm-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-clock-clarify-alarm","data":{"text":"set an alarm"}}"""
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.DoesNotContain(replies, reply =>
            string.Equals(ReadReplyType(reply), "SKILL_REDIRECT", StringComparison.Ordinal));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("set",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("alarm",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities")
                .GetProperty("domain").GetString());
        Assert.False(listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities")
            .TryGetProperty("time", out _));
        Assert.Equal("@be/clock",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.True(listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("launch")
            .GetBoolean());
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
            Text =
                """{"type":"LISTEN","transID":"trans-clock-cancel-alarm","data":{"rules":["globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-cancel-alarm-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-clock-cancel-alarm","data":{"text":"cancel alarm"}}"""
        });

        Assert.Equal(2, replies.Count);
        Assert.DoesNotContain(replies, reply =>
            string.Equals(ReadReplyType(reply), "SKILL_REDIRECT", StringComparison.Ordinal));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("delete",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("alarm",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities")
                .GetProperty("domain").GetString());
        Assert.Equal("@be/clock",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.True(listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("launch")
            .GetBoolean());

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
            Text =
                """{"type":"LISTEN","transID":"trans-clock-set-alarm-query","data":{"rules":["clock/clock_menu","globals/global_commands_launch"],"mode":"CLIENT_NLU"}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-set-alarm-query-token",
            Text =
                """{"type":"CLIENT_NLU","transID":"trans-clock-set-alarm-query","data":{"entities":{"domain":"alarm"},"intent":"set","rules":["clock/clock_menu"]}}"""
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("set",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("alarm",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities")
                .GetProperty("domain").GetString());
        Assert.False(listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities")
            .TryGetProperty("time", out _));
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
            Text =
                """{"type":"LISTEN","transID":"trans-clock-cancel-alarm-value","data":{"rules":["clock/alarm_set_value","globals/gui_nav","globals/global_commands_launch"],"mode":"CLIENT_NLU"}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-cancel-alarm-value-token",
            Text =
                """{"type":"CLIENT_NLU","transID":"trans-clock-cancel-alarm-value","data":{"entities":{},"intent":"cancel","rules":["clock/alarm_set_value"]}}"""
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("cancel",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("clock/alarm_set_value",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules")[0].GetString());
        Assert.Equal("alarm",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities")
                .GetProperty("domain").GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-clock-cancel-query","data":{"rules":["globals/global_commands_launch"]}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-cancel-query-token",
            Text =
                """{"type":"CLIENT_ASR","transID":"trans-clock-cancel-query","data":{"text":"set an alarm for 7:16 am"}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-cancel-query-token",
            Text =
                """{"type":"LISTEN","transID":"trans-clock-cancel-query-menu","data":{"rules":["clock/alarm_timer_query_menu","globals/global_commands_launch"],"mode":"CLIENT_NLU"}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-clock-cancel-query-token",
            Text =
                """{"type":"CLIENT_NLU","transID":"trans-clock-cancel-query-menu","data":{"entities":{},"intent":"cancel","rules":["clock/alarm_timer_query_menu"]}}"""
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("delete",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("alarm",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities")
                .GetProperty("domain").GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-photo-gallery","data":{"rules":["globals/global_commands_launch"]}}"""
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
        Assert.Equal("menu",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("@be/gallery",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("skill").GetString());

        using var redirectPayload = JsonDocument.Parse(replies[2].Text!);
        Assert.Equal("@be/gallery",
            redirectPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.Equal("menu",
            redirectPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-photo-gallery-context","data":{"rules":["globals/global_commands_launch"]}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-photo-gallery-context-token",
            Text =
                """{"type":"CLIENT_ASR","transID":"trans-photo-gallery-context","data":{"text":"open photo gallery"}}"""
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
            Text =
                """{"type":"CONTEXT","transID":"trans-photo-gallery-context","data":{"skill":{"id":"@be/gallery"}}}"""
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
            Text =
                """{"type":"LISTEN","transID":"trans-gallery-yesno-context","data":{"rules":["shared/yes_no","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"],"asr":{"hints":["$YESNO"]}}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-gallery-yesno-context-token",
            Text =
                """{"type":"CONTEXT","transID":"trans-gallery-yesno-context","data":{"audioTranscriptHint":"yes","skill":{"id":"@be/gallery"}}}"""
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
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(600);

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-gallery-yesno-context-token",
            Binary = new byte[3000]
        });

        Assert.Equal(2, replies.Count);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("yes",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("yes",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("shared/yes_no",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules")[0].GetString());
        Assert.Equal("shared/yes_no",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-settings-volume-tail","data":{"rules":["settings/volume_control","globals/gui_nav","globals/global_commands_launch"]}}"""
        });

        var contextReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-settings-volume-tail-token",
            Text =
                """{"type":"CONTEXT","transID":"trans-settings-volume-tail","data":{"skill":{"id":"@be/settings"}}}"""
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
    public async Task Listen_VolumeUp_EmitsNimbusCompletionAfterGlobalResult()
    {
        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-volume-up-token",
            Text =
                """{"type":"LISTEN","transID":"trans-volume-up","data":{"text":"increase your volume","hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));
        Assert.Equal(75, replies[2].DelayMs);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        var nlu = listenPayload.RootElement.GetProperty("data").GetProperty("nlu");
        Assert.Equal("volumeUp", nlu.GetProperty("intent").GetString());
        Assert.Equal("global_commands", nlu.GetProperty("domain").GetString());
        Assert.Equal("null", nlu.GetProperty("entities").GetProperty("volumeLevel").GetString());

        using var skillPayload = JsonDocument.Parse(replies[2].Text!);
        Assert.Equal("@be/nimbus",
            skillPayload.RootElement.GetProperty("data").GetProperty("skill").GetProperty("id").GetString());
        Assert.True(skillPayload.RootElement.GetProperty("data").GetProperty("final").GetBoolean());
        Assert.Equal("runtime-silent-complete",
            skillPayload.RootElement
                .GetProperty("data")
                .GetProperty("action")
                .GetProperty("config")
                .GetProperty("jcp")
                .GetProperty("config")
                .GetProperty("play")
                .GetProperty("meta")
                .GetProperty("mim_id")
                .GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-clock-alarm-okay","data":{"rules":["clock/alarm_timer_okay","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"]}}"""
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
        Assert.Equal(string.Empty,
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("clock/alarm_timer_okay",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules")[0].GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-clock-alarm-value-noinput","data":{"rules":["clock/alarm_set_value","globals/gui_nav","globals/global_commands_launch"]}}"""
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
        Assert.Equal(string.Empty,
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("clock/alarm_set_value",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules")[0].GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-gallery-preview-noinput","data":{"rules":["gallery/gallery_preview","globals/gui_nav","globals/mim_repeat","globals/mim_thanks","globals/global_commands_launch"]}}"""
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
        Assert.Equal(string.Empty,
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("gallery/gallery_preview",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules")[0].GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-snapshot","data":{"rules":["globals/global_commands_launch"]}}"""
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
        Assert.Equal("createOnePhoto",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("@be/create",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("skill").GetString());

        using var redirectPayload = JsonDocument.Parse(replies[2].Text!);
        Assert.Equal("@be/create",
            redirectPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.Equal("createOnePhoto",
            redirectPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-photobooth","data":{"rules":["globals/global_commands_launch"]}}"""
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
        Assert.Equal("createSomePhotos",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("@be/create",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("skill").GetString());

        using var redirectPayload = JsonDocument.Parse(replies[2].Text!);
        Assert.Equal("@be/create",
            redirectPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.Equal("createSomePhotos",
            redirectPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
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

        Assert.Equal(2, replies.Count);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("yeah",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("yes",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("create/is_it_a_keeper",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules")[0].GetString());
        Assert.Equal("create",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities")
                .GetProperty("domain").GetString());
        Assert.Equal("create/is_it_a_keeper",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
        Assert.Equal("EOS", ReadReplyType(replies[1]));
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
            Text =
                """{"type":"LISTEN","transID":"trans-yesno-hints","data":{"rules":["surprises-ota/want_to_download_now"],"asr":{"hints":["$YESNO"]}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-yesno-hints-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-yesno-hints","data":{"text":"no"}}"""
        });

        Assert.Equal(2, replies.Count);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("no",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("no",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("surprises-ota/want_to_download_now",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules")[0].GetString());
        Assert.Equal("surprises-ota/want_to_download_now",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-yesno-hints-yep","data":{"rules":["surprises-ota/want_to_download_now"],"asr":{"hints":["$YESNO"]}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-yesno-hints-yep-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-yesno-hints-yep","data":{"text":"yep"}}"""
        });

        Assert.Equal(2, replies.Count);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("yep",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("yes",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("surprises-ota/want_to_download_now",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules")[0].GetString());
        Assert.Equal("surprises-ota/want_to_download_now",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
    }

    [Fact]
    public async Task ClientAsr_YesNoPromptFromAsrHints_MapsUhHuhToYesIntent()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-yesno-huh-token",
            Text =
                """{"type":"LISTEN","transID":"trans-yesno-huh","data":{"rules":["surprises-ota/want_to_download_now"],"asr":{"hints":["$YESNO"]}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-yesno-huh-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-yesno-huh","data":{"text":"uh huh"}}"""
        });

        Assert.Equal(2, replies.Count);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("uh huh",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("yes",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("surprises-ota/want_to_download_now",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules")[0].GetString());
        Assert.Equal("surprises-ota/want_to_download_now",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
    }

    [Fact]
    public async Task ClientAsr_YesNoPromptFromAsrHints_StripsPunctuationMashupAndMapsYesIntent()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-yesno-punctuation-token",
            Text =
                """{"type":"LISTEN","transID":"trans-yesno-punctuation","data":{"rules":["surprises-ota/want_to_download_now"],"asr":{"hints":["$YESNO"]}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-yesno-punctuation-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-yesno-punctuation","data":{"text":"- Thank you. - Yes."}}"""
        });

        Assert.Equal(2, replies.Count);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("thank you yes",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("yes",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("surprises-ota/want_to_download_now",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules")[0].GetString());
        Assert.Equal("surprises-ota/want_to_download_now",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-shared-yesno","data":{"rules":["shared/yes_no","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"],"asr":{"hints":["$YESNO"]}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-shared-yesno-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-shared-yesno","data":{"text":"yes"}}"""
        });

        Assert.Equal(2, replies.Count);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("yes",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        var rules = listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules");
        Assert.Single(rules.EnumerateArray());
        Assert.Equal("shared/yes_no", rules[0].GetString());
        Assert.Equal("shared/yes_no",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-shared-yesno-negative","data":{"rules":["shared/yes_no","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"],"asr":{"hints":["$YESNO"]}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-shared-yesno-negative-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-shared-yesno-negative","data":{"text":"negative"}}"""
        });

        Assert.Equal(2, replies.Count);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("no",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        var rules = listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules");
        Assert.Single(rules.EnumerateArray());
        Assert.Equal("shared/yes_no", rules[0].GetString());
        Assert.Equal("shared/yes_no",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
    }

    [Fact]
    public async Task ClientAsr_SharedYesNoPrompt_MixedReplyRequestsClarification()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-shared-yesno-clarify-token",
            Text =
                """{"type":"LISTEN","transID":"trans-shared-yesno-clarify","data":{"rules":["shared/yes_no","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"],"asr":{"hints":["$YESNO"]}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-shared-yesno-clarify-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-shared-yesno-clarify","data":{"text":"no yes"}}"""
        });

        Assert.Equal(3, replies.Count);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("yes_no_clarify",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("no yes",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-alarm-change-yesno","data":{"rules":["clock/alarm_timer_change","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"],"asr":{"hints":["$YESNO"]}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-alarm-change-yesno-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-alarm-change-yesno","data":{"text":"yes"}}"""
        });

        Assert.Equal(2, replies.Count);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("yes",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        var rules = listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules");
        Assert.Single(rules.EnumerateArray());
        Assert.Equal("clock/alarm_timer_change", rules[0].GetString());
        Assert.Equal("clock/alarm_timer_change",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
    }

    [Fact]
    public async Task ClientAsr_ExerciseWantTo_MapsNoWithoutCompetingSpeech()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-exercise-want-to-token",
            Text =
                """{"type":"LISTEN","transID":"trans-exercise-want-to","data":{"hotphrase":false,"rules":["exercise/want_to","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"],"asr":{"hints":["$YESNO"]}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-exercise-want-to-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-exercise-want-to","data":{"text":"no"}}"""
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("no",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("exercise/want_to",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules")[0].GetString());
        Assert.Equal("exercise/want_to",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
        Assert.DoesNotContain(replies, reply => string.Equals(ReadReplyType(reply), "SKILL_ACTION",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClientAsr_ExerciseWantToWithoutHints_MapsNoWithoutKnowledgeSearch()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-exercise-want-to-nohint-token",
            Text =
                """{"type":"LISTEN","transID":"trans-exercise-want-to-nohint","data":{"hotphrase":false,"rules":["exercise/want_to","globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-exercise-want-to-nohint-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-exercise-want-to-nohint","data":{"text":"no"}}"""
        });

        Assert.Equal(2, replies.Count);
        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("no",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("exercise/want_to",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
        Assert.DoesNotContain(replies, reply =>
            reply.Text is not null &&
            reply.Text.Contains("According to", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ClientAsr_GallerySharedYesNo_MapsNoWithoutCompetingSpeech()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-gallery-yesno-no-token",
            Text =
                """{"type":"LISTEN","transID":"trans-gallery-yesno-no","data":{"hotphrase":false,"rules":["shared/yes_no","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"],"asr":{"hints":["$YESNO"]}}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-gallery-yesno-no-token",
            Text =
                """{"type":"CONTEXT","transID":"trans-gallery-yesno-no","data":{"skill":{"id":"@be/gallery"}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-gallery-yesno-no-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-gallery-yesno-no","data":{"text":"no"}}"""
        });

        Assert.Equal(2, replies.Count);
        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("no",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("shared/yes_no",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
        Assert.DoesNotContain(replies, reply => string.Equals(ReadReplyType(reply), "SKILL_ACTION",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClientAsr_GalleryPromptEcho_DoesNotLaunchSnapshot()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-gallery-prompt-echo-token",
            Text =
                """{"type":"LISTEN","transID":"trans-gallery-prompt-echo","data":{"hotphrase":false,"rules":["shared/yes_no","globals/gui_nav"],"asr":{"hints":["$YESNO"]}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-gallery-prompt-echo-token",
            Text =
                """{"type":"CLIENT_ASR","transID":"trans-gallery-prompt-echo","data":{"text":"do you want to take a picture"}}"""
        });

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.NotEqual("snapshot",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.NotEqual("knowledge_search",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.DoesNotContain(replies, reply =>
            reply.Text is not null &&
            reply.Text.Contains("According to", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ClientAsr_YogaPromptEcho_DoesNotSearchKnowledge()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-yoga-prompt-echo-token",
            Text =
                """{"type":"LISTEN","transID":"trans-yoga-prompt-echo","data":{"hotphrase":false,"rules":["exercise/want_to","globals/gui_nav"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-yoga-prompt-echo-token",
            Text =
                """{"type":"CLIENT_ASR","transID":"trans-yoga-prompt-echo","data":{"text":"do you want to do yoga now"}}"""
        });

        Assert.Equal(2, replies.Count);
        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("skill_listen",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("exercise/want_to",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
        Assert.DoesNotContain(replies, reply =>
            reply.Text is not null &&
            reply.Text.Contains("According to", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(replies, reply => string.Equals(ReadReplyType(reply), "SKILL_ACTION",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClientAsr_ExerciseRoutineSelector_StaysLocal()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-exercise-routine-token",
            Text =
                """{"type":"LISTEN","transID":"trans-exercise-routine","data":{"hotphrase":false,"rules":["exercise/routine_selector","globals/gui_nav"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-exercise-routine-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-exercise-routine","data":{"text":"sun salutation"}}"""
        });

        Assert.Equal(2, replies.Count);
        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("skill_listen",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("exercise/routine_selector",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
        Assert.Equal("sun salutation",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.DoesNotContain(replies, reply => string.Equals(ReadReplyType(reply), "SKILL_ACTION",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClientAsr_HotphrasePromptEcho_DoesNotLaunchSnapshot()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-prompt-echo-token",
            Text =
                """{"type":"LISTEN","transID":"trans-hotphrase-prompt-echo","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-prompt-echo-token",
            Text =
                """{"type":"CLIENT_ASR","transID":"trans-hotphrase-prompt-echo","data":{"text":"do you want to take a picture"}}"""
        });

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.NotEqual("snapshot",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.DoesNotContain(replies, reply =>
            reply.Text is not null &&
            reply.Text.Contains("According to", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(replies, reply => string.Equals(ReadReplyType(reply), "SKILL_ACTION",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClientAsr_HotphraseNo_DoesNotSpeakKnowledgeSearch()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-no-token",
            Text =
                """{"type":"LISTEN","transID":"trans-hotphrase-no","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-no-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-hotphrase-no","data":{"text":"no"}}"""
        });

        Assert.DoesNotContain(replies, reply =>
            reply.Text is not null &&
            reply.Text.Contains("According to", StringComparison.OrdinalIgnoreCase));
        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.NotEqual("knowledge_search",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
    }

    [Fact]
    public async Task ClientAsr_NimbusFollowUp_WhatTimeIsIt_DoesNotLaunchClock()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-nimbus-followup-time-token",
            Text =
                """{"type":"LISTEN","transID":"trans-nimbus-hello","data":{"text":"hello jibo","rules":["wake-word"]}}"""
        });

        var session = _store.FindSessionByToken("hub-nimbus-followup-time-token");
        Assert.NotNull(session);
        Assert.True(session.FollowUpOpen);

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-nimbus-followup-time-token",
            Text =
                """{"type":"LISTEN","transID":"trans-nimbus-followup-time","data":{"hotphrase":false,"rules":["follow-up"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-nimbus-followup-time-token",
            Text =
                """{"type":"CLIENT_ASR","transID":"trans-nimbus-followup-time","data":{"text":"what time is it"}}"""
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.NotEqual("askForTime",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.DoesNotContain(replies, reply =>
            string.Equals(ReadReplyType(reply), "SKILL_REDIRECT", StringComparison.Ordinal));

        using var skillPayload = JsonDocument.Parse(replies[2].Text!);
        Assert.NotEqual("@be/clock",
            skillPayload.RootElement.GetProperty("data").GetProperty("skill").GetProperty("id").GetString());
    }

    [Fact]
    public async Task ClientNlu_HotphraseNimbusContext_AskForTime_LaunchesClock()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-nimbus-clientnlu-time-token",
            Text =
                """{"type":"LISTEN","transID":"trans-nimbus-hello","data":{"text":"hello jibo","rules":["wake-word"]}}"""
        });

        var session = _store.FindSessionByToken("hub-nimbus-clientnlu-time-token");
        Assert.NotNull(session);
        Assert.Equal("chitchat-skill", session.Metadata[SkillListenOwnership.LastContextSkillIdKey]?.ToString());

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-nimbus-clientnlu-time-token",
            Text =
                """{"type":"LISTEN","transID":"trans-nimbus-ask-time","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-nimbus-clientnlu-time-token",
            Text =
                """{"type":"CONTEXT","transID":"trans-nimbus-ask-time","data":{"skill":{"id":"@be/nimbus"}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-nimbus-clientnlu-time-token",
            Text =
                """{"type":"CLIENT_NLU","transID":"trans-nimbus-ask-time","data":{"intent":"askForTime","text":"what time is it","rules":["launch","globals/global_commands_launch"]}}"""
        });

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("askForTime",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("@be/clock",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("skill").GetString());
        Assert.Equal("@be/clock",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.True(listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("onRobot")
            .GetBoolean());
        Assert.True(listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("launch")
            .GetBoolean());
        Assert.DoesNotContain(replies, reply =>
            string.Equals(ReadReplyType(reply), "SKILL_REDIRECT", StringComparison.Ordinal));
        Assert.Contains(replies, reply => string.Equals(ReadReplyType(reply), "EOS", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClientAsr_HotphraseNimbusContext_HowOldAreYou_SpeaksAgeThroughNimbus()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-nimbus-how-old-token",
            Text =
                """{"type":"LISTEN","transID":"trans-nimbus-how-old","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-nimbus-how-old-token",
            Text =
                """{"type":"CONTEXT","transID":"trans-nimbus-how-old","data":{"skill":{"id":"@be/nimbus"}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-nimbus-how-old-token",
            Text =
                """{"type":"CLIENT_ASR","transID":"trans-nimbus-how-old","data":{"text":"how old are you"}}"""
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("how old are you",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("robot_how_old_are_you",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("@be/nimbus",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.False(listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("onRobot")
            .GetBoolean());
        Assert.Equal("chitchat-skill",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("cloudSkill").GetString());

        using var skillPayload = JsonDocument.Parse(replies[2].Text!);
        Assert.Equal("chitchat-skill",
            skillPayload.RootElement.GetProperty("data").GetProperty("skill").GetProperty("id").GetString());
        var esml = skillPayload.RootElement
            .GetProperty("data")
            .GetProperty("action")
            .GetProperty("config")
            .GetProperty("jcp")
            .GetProperty("config")
            .GetProperty("play")
            .GetProperty("esml")
            .GetString();
        Assert.False(string.IsNullOrWhiteSpace(esml));
        Assert.DoesNotContain("${jibo.", esml, StringComparison.Ordinal);
        Assert.Contains("old", esml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClientAsr_HotphraseNimbusContext_FavoriteColor_SpeaksThroughNimbus()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-nimbus-favorite-color-token",
            Text =
                """{"type":"LISTEN","transID":"trans-nimbus-favorite-color","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-nimbus-favorite-color-token",
            Text =
                """{"type":"CONTEXT","transID":"trans-nimbus-favorite-color","data":{"skill":{"id":"@be/nimbus"}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-nimbus-favorite-color-token",
            Text =
                """{"type":"CLIENT_ASR","transID":"trans-nimbus-favorite-color","data":{"text":"what's your favorite color"}}"""
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));
        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("robot_favorite_color",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("@be/nimbus",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
    }

    [Fact]
    public async Task BufferedHotphraseOggAudio_IncompleteHowOldEarlyProbeStaysOpenBeforeHardTimeout()
    {
        var stateStore = new InMemoryCloudStateStore();
        var service = CreateService(stateStore, sttStrategies:
        [
            new QueuedBufferedAudioSttStrategy("Hey Jibo, how old")
        ]);

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-incomplete-how-old-token",
            Text =
                """{"type":"LISTEN","transID":"trans-hotphrase-ogg-incomplete-how-old","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-incomplete-how-old-token",
            Binary = BuildOggFrame(0x02, "OpusHead")
        });
        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-incomplete-how-old-token",
            Binary = BuildOggFrame(0x00, "OpusTags")
        });

        for (var index = 0; index < 8; index += 1)
        {
            var interimReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-hotphrase-ogg-incomplete-how-old-token",
                Binary = BuildOggFrame(0x00)
            });

            Assert.Empty(interimReplies);
        }

        var session = stateStore.FindSessionByToken("hub-hotphrase-ogg-incomplete-how-old-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(5000);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(1300);

        var replies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-incomplete-how-old-token",
            Binary = BuildOggFrame(0x00)
        });

        Assert.Empty(replies);
        Assert.True(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal(1, session.TurnState.FinalizeAttemptCount);
        Assert.Null(session.LastIntent);
        Assert.Null(session.LastTranscript);
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
            Text =
                """{"type":"LISTEN","transID":"trans-yesno-noinput","data":{"rules":["surprises-ota/want_to_download_now","globals/gui_nav","globals/global_commands_launch"],"asr":{"hints":["$YESNO"]}}}"""
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
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(600);
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
        Assert.Equal(string.Empty,
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal(string.Empty,
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-shared-yesno-noinput","data":{"rules":["shared/yes_no","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"],"asr":{"hints":["$YESNO"]}}}"""
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
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(600);
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
    public async Task BufferedYesNoOggAudio_BlankAutoFinalizeStaysOpenBeforeHardTimeout()
    {
        var stateStore = new InMemoryCloudStateStore();
        var service = CreateService(stateStore, sttStrategies:
        [
            new QueuedBufferedAudioSttStrategy("", "")
        ]);

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-yesno-ogg-blank-token",
            Text =
                """{"type":"LISTEN","transID":"trans-yesno-ogg-blank","data":{"hotphrase":false,"rules":["surprises-date/offer_date_fact","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"],"asr":{"hints":["$YESNO"],"earlyEOS":["$YESNO"],"encoding":"OGG_OPUS","sampleRate":16000}}}"""
        });

        foreach (var frame in new[]
                 {
                     BuildOggFrame(0x02, "OpusHead"),
                     BuildOggFrame(0x00, "OpusTags"),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00)
                 })
        {
            var interimReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-yesno-ogg-blank-token",
                Binary = frame
            });

            Assert.Empty(interimReplies);
        }

        var session = stateStore.FindSessionByToken("hub-yesno-ogg-blank-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(3);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(600);

        var blankProbeReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-yesno-ogg-blank-token",
            Binary = BuildOggFrame(0x00)
        });

        Assert.Empty(blankProbeReplies);
        Assert.True(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal(1, session.TurnState.FinalizeAttemptCount);
        Assert.Null(session.LastIntent);
        Assert.Null(session.LastTranscript);

        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(8500);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(600);

        var timeoutReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-yesno-ogg-blank-token",
            Binary = BuildOggFrame(0x00)
        });

        Assert.Equal(2, timeoutReplies.Count);
        Assert.Equal("LISTEN", ReadReplyType(timeoutReplies[0]));
        Assert.Equal("EOS", ReadReplyType(timeoutReplies[1]));

        using var listenPayload = JsonDocument.Parse(timeoutReplies[0].Text!);
        Assert.Equal(string.Empty,
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("surprises-date/offer_date_fact",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules")[0].GetString());
        Assert.False(session.TurnState.AwaitingTurnCompletion);
    }

    [Fact]
    public async Task BufferedYesNoOggAudio_NonYesNoPromptBleedDoesNotRouteAsChat()
    {
        var stateStore = new InMemoryCloudStateStore();
        var service = CreateService(stateStore, sttStrategies:
        [
            new QueuedBufferedAudioSttStrategy("want to hear", "want to hear")
        ]);

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-yesno-ogg-prompt-bleed-token",
            Text =
                """{"type":"LISTEN","transID":"trans-yesno-ogg-prompt-bleed","data":{"hotphrase":false,"rules":["surprises-date/offer_date_fact","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"],"asr":{"hints":["$YESNO"],"earlyEOS":["$YESNO"],"encoding":"OGG_OPUS","sampleRate":16000}}}"""
        });

        foreach (var frame in new[]
                 {
                     BuildOggFrame(0x02, "OpusHead"),
                     BuildOggFrame(0x00, "OpusTags"),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00)
                 })
        {
            var interimReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-yesno-ogg-prompt-bleed-token",
                Binary = frame
            });

            Assert.Empty(interimReplies);
        }

        var session = stateStore.FindSessionByToken("hub-yesno-ogg-prompt-bleed-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(3);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(600);

        var bleedProbeReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-yesno-ogg-prompt-bleed-token",
            Binary = BuildOggFrame(0x00)
        });

        Assert.Empty(bleedProbeReplies);
        Assert.True(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal(1, session.TurnState.FinalizeAttemptCount);
        Assert.Null(session.LastIntent);
        Assert.Null(session.LastTranscript);

        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(8500);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(600);

        var timeoutReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-yesno-ogg-prompt-bleed-token",
            Binary = BuildOggFrame(0x00)
        });

        Assert.Equal(2, timeoutReplies.Count);
        Assert.Equal("LISTEN", ReadReplyType(timeoutReplies[0]));
        Assert.Equal("EOS", ReadReplyType(timeoutReplies[1]));

        using var listenPayload = JsonDocument.Parse(timeoutReplies[0].Text!);
        Assert.Equal(string.Empty,
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("surprises-date/offer_date_fact",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules")[0].GetString());
        Assert.Null(session.LastIntent);
        Assert.False(session.TurnState.AwaitingTurnCompletion);
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
            Text =
                """{"type":"LISTEN","transID":"trans-create-noinput-1","data":{"rules":["create/is_it_a_keeper","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"]}}"""
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
            Text =
                """{"type":"LISTEN","transID":"trans-create-noinput-2","data":{"rules":["create/is_it_a_keeper","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"]}}"""
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
        Assert.Equal("@be/idle",
            redirectPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-share-yes","data":{"rules":["surprises-date/offer_date_fact","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"],"asr":{"hints":["$YESNO"]}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-share-yesno-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-share-yes","data":{"text":"Yes!"}}"""
        });

        Assert.Equal(2, replies.Count);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("yes",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        var rules = listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules");
        Assert.Single(rules.EnumerateArray());
        var selectedRule = rules[0].GetString();
        Assert.Equal("surprises-date/offer_date_fact", selectedRule);
        Assert.Equal(selectedRule,
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
        Assert.Empty(listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities")
            .EnumerateObject());
        Assert.Equal("EOS", ReadReplyType(replies[1]));
    }

    [Fact]
    public async Task ClientAsr_WordOfDayOfferPrompt_MapsYesToWordOfDayLaunch()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-offer-yesno-token",
            Text =
                """{"type":"LISTEN","transID":"trans-wod-offer-yes","data":{"rules":["word-of-the-day/surprise","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"],"asr":{"hints":["$YESNO"]}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-offer-yesno-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-wod-offer-yes","data":{"text":"Yes!"}}"""
        });

        Assert.True(replies.Count >= 3);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        var rules = listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules");
        Assert.Single(rules.EnumerateArray());
        var selectedRule = rules[0].GetString();
        Assert.True(
            string.Equals(selectedRule, "word-of-the-day/surprise", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(selectedRule, "word-of-the-day/menu", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(selectedRule,
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
        Assert.Equal(
            "word-of-the-day",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities")
                .GetProperty("domain").GetString());
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

        var replies = ResponsePlanToSocketMessagesMapper.Map(plan, turn, new CloudSession(), true);
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

    [Theory]
    [InlineData("SURPRISE", true)]
    [InlineData("surprise", true)]
    [InlineData("PRESENCE", false)]
    [InlineData(null, false)]
    public void ResponsePlanMapper_ProactiveSkipSurprisesMatchesTriggerSource(
        string? triggerSource,
        bool expected)
    {
        var plan = new ResponsePlan
        {
            IntentName = "proactive_greeting",
            Actions =
            {
                new SpeakAction
                {
                    Sequence = 0,
                    Text = "Hello.",
                    Voice = "griffin"
                }
            }
        };
        var attributes = new Dictionary<string, object?>
        {
            ["transID"] = "trans-proactive-skip-surprises"
        };
        if (triggerSource is not null)
            attributes["triggerSource"] = triggerSource;

        var replies = ResponsePlanToSocketMessagesMapper.MapProactive(
            plan,
            new TurnContext { Attributes = attributes },
            new CloudSession());

        using var payload = JsonDocument.Parse(replies[0].Text);
        Assert.Equal(expected, payload.RootElement
            .GetProperty("data")
            .GetProperty("match")
            .GetProperty("skipSurprises")
            .GetBoolean());
    }

    [Fact]
    public void ResponsePlan_WithListenContexts_EmitsFollowUpListenPacket()
    {
        var plan = new ResponsePlan
        {
            SessionId = "session-grocery",
            Actions =
            {
                new SpeakAction
                {
                    Sequence = 0,
                    Text = "What should I add to your grocery list?",
                    Voice = "griffin"
                },
                new InvokeNativeSkillAction
                {
                    Sequence = 1,
                    SkillName = "chitchat-skill",
                    Payload = new Dictionary<string, object?>
                    {
                        ["mim_id"] = "runtime-household-list",
                        ["mim_type"] = "question",
                        ["prompt_id"] = "RUNTIME_PROMPT",
                        ["prompt_sub_category"] = "Q",
                        ["listen_contexts"] = new[] { "household-list/follow_up_item" }
                    }
                }
            }
        };

        var turn = new TurnContext
        {
            Attributes = new Dictionary<string, object?>
            {
                ["transID"] = "trans-grocery-follow-up"
            }
        };

        var replies = ResponsePlanToSocketMessagesMapper.Map(plan, turn, new CloudSession(), true);
        Assert.Equal(3, replies.Count);
        using var payload = JsonDocument.Parse(replies[2].Text);
        Assert.Equal("SKILL_ACTION", payload.RootElement.GetProperty("type").GetString());

        var contexts = payload.RootElement
            .GetProperty("data")
            .GetProperty("action")
            .GetProperty("config")
            .GetProperty("jcp")
            .GetProperty("config")
            .GetProperty("listen")
            .GetProperty("contexts")
            .EnumerateArray()
            .Select(element => element.GetString())
            .ToArray();

        Assert.Contains("household-list/follow_up_item", contexts);
    }

    [Fact]
    public async Task ClientAsr_GroceryList_PromptsFollowUpAndAcceptsNextItem()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-grocery-followup-token",
            Text =
                """{"type":"LISTEN","transID":"trans-grocery-followup","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        var promptReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-grocery-followup-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-grocery-followup","data":{"text":"grocery list"}}"""
        });

        Assert.Equal(3, promptReplies.Count);

        var session = _store.FindSessionByToken("hub-grocery-followup-token");
        Assert.NotNull(session);
        Assert.True(session.FollowUpOpen);
        Assert.Equal("shopping_list_prompt", session.LastIntent);
        Assert.Equal("follow-up", session.LastListenType);

        using var promptListenPayload = JsonDocument.Parse(promptReplies[0].Text!);
        Assert.Equal("shopping_list_prompt",
            promptListenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent")
                .GetString());

        using var promptSkillPayload = JsonDocument.Parse(promptReplies[2].Text!);
        Assert.Equal("SKILL_ACTION", promptSkillPayload.RootElement.GetProperty("type").GetString());
        Assert.Contains("household-list/follow_up_item",
            promptSkillPayload.RootElement
                .GetProperty("data")
                .GetProperty("action")
                .GetProperty("config")
                .GetProperty("jcp")
                .GetProperty("config")
                .GetProperty("listen")
                .GetProperty("contexts")
                .EnumerateArray()
                .Select(element => element.GetString())
                .Where(value => value is not null)
                .Select(value => value!));
        Assert.Contains("$ANYTHING",
            promptSkillPayload.RootElement
                .GetProperty("data")
                .GetProperty("action")
                .GetProperty("config")
                .GetProperty("jcp")
                .GetProperty("config")
                .GetProperty("listen")
                .GetProperty("asr")
                .GetProperty("hints")
                .EnumerateArray()
                .Select(element => element.GetString())
                .Where(value => value is not null)
                .Select(value => value!));

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-grocery-followup-token",
            Text =
                """{"type":"LISTEN","transID":"trans-grocery-followup-item","data":{"rules":["household-list/follow_up_item"],"asr":{"hints":["$ANYTHING"]}}}"""
        });

        var addReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-grocery-followup-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-grocery-followup","data":{"text":"milk and eggs"}}"""
        });

        Assert.Equal(3, addReplies.Count);
        Assert.True(session.FollowUpOpen);
        Assert.Equal("shopping_list_add", session.LastIntent);

        using var addListenPayload = JsonDocument.Parse(addReplies[0].Text!);
        Assert.Equal("shopping_list_add",
            addListenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent")
                .GetString());
        Assert.Equal("milk and eggs",
            addListenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());

        using var addSkillPayload = JsonDocument.Parse(addReplies[2].Text!);
        Assert.Equal("SKILL_ACTION", addSkillPayload.RootElement.GetProperty("type").GetString());
        Assert.Contains("household-list/follow_up_item",
            addSkillPayload.RootElement
                .GetProperty("data")
                .GetProperty("action")
                .GetProperty("config")
                .GetProperty("jcp")
                .GetProperty("config")
                .GetProperty("listen")
                .GetProperty("contexts")
                .EnumerateArray()
                .Select(element => element.GetString())
                .Where(value => value is not null)
                .Select(value => value!));
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
            Text =
                """{"type":"LISTEN","transID":"trans-news","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
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
        Assert.Equal("news",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("news",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("cloudSkill").GetString());

        using var skillPayload = JsonDocument.Parse(replies[2].Text!);
        Assert.Equal("news",
            skillPayload.RootElement.GetProperty("data").GetProperty("skill").GetProperty("id").GetString());
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
                        new NewsHeadline("Robotics club opens a new community lab",
                            "Neighbors can try hands-on robot projects this weekend."),
                        new NewsHeadline("Local students win a regional coding challenge",
                            "The team advanced after presenting an assistive coding prototype.")
                    ],
                    "NewsAPI")));

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-news-provider-token",
            Text =
                """{"type":"LISTEN","transID":"trans-news-provider","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
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
        var jcp = speakPayload.RootElement
            .GetProperty("data")
            .GetProperty("action")
            .GetProperty("config")
            .GetProperty("jcp");
        Assert.Equal("SEQUENCE", jcp.GetProperty("type").GetString());
        var children = jcp.GetProperty("children");
        Assert.True(children.GetArrayLength() >= 3);

        var introEsml = children[0].GetProperty("config").GetProperty("play").GetProperty("esml").GetString();
        Assert.Contains("news-intro", introEsml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Here's today's news", introEsml, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(6, children[0].GetProperty("config").GetProperty("timeout").GetInt32());

        var joinedChildren = string.Join(
            ' ',
            children.EnumerateArray()
                .Select(child => child.GetProperty("config").GetProperty("play").GetProperty("esml").GetString()));
        Assert.Contains("Robotics club opens a new community lab", joinedChildren, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("news-stinger", joinedChildren, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Source: NewsAPI.", joinedChildren, StringComparison.OrdinalIgnoreCase);
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
            Text =
                """{"type":"LISTEN","transID":"trans-weather","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
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
        Assert.Equal("weather",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.False(listenPayload.RootElement.GetProperty("data").GetProperty("nlu").TryGetProperty("skill", out _));
        Assert.Equal("@be/nimbus",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.False(listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("onRobot")
            .GetBoolean());
        Assert.Equal("chitchat-skill",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("cloudSkill").GetString());

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
        Assert.Contains("weather service is offline", esml, StringComparison.OrdinalIgnoreCase);
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
            Text =
                """{"type":"LISTEN","transID":"trans-weather-entities","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-weather-entities-token",
            Text =
                """{"type":"CLIENT_ASR","transID":"trans-weather-entities","data":{"text":"will it rain tomorrow"}}"""
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("weather",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());

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
        Assert.Contains("weather service is offline", esml, StringComparison.OrdinalIgnoreCase);
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
            Text =
                """{"type":"LISTEN","transID":"trans-weather-provider","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
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
        Assert.Contains(replies,
            static reply => string.Equals(ReadReplyType(reply), "SKILL_ACTION", StringComparison.Ordinal));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.False(listenPayload.RootElement.GetProperty("data").GetProperty("nlu").TryGetProperty("skill", out _));
        Assert.Equal("weather",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("cloudSkill").GetString());

        var skillReply = replies.Last(static reply =>
            string.Equals(ReadReplyType(reply), "SKILL_ACTION", StringComparison.Ordinal));
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
            display.GetProperty("view").GetProperty("context").GetProperty("data").GetProperty("viewConfig")
                .GetProperty("id").GetString());

        Assert.True(jcpConfig.TryGetProperty("views", out var views));
        var weatherHiLo = views.GetProperty("weatherHiLo");
        Assert.Equal("weatherTempView", weatherHiLo.GetProperty("viewConfig").GetProperty("id").GetString());

        Assert.True(jcpConfig.TryGetProperty("local", out var local));
        Assert.Equal(
            "weatherTempView",
            local.GetProperty("views").GetProperty("weatherHiLo").GetProperty("viewConfig").GetProperty("id")
                .GetString());

        var payloadText = skillReply.Text!;
        Assert.Contains("assets/personal-report-skill/weather/icons/rain_v01.crn", payloadText,
            StringComparison.Ordinal);
        Assert.Contains("tempNormal_v01.crn", payloadText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClientAsr_WeatherNextWeek_WithProvider_EmitsWeatherHiLoSequenceCards()
    {
        var customStore = new InMemoryCloudStateStore();
        var customService = CreateService(
            customStore,
            new StubWeatherReportProvider(
                new WeatherReportSnapshot("Seattle, US", "light rain", 58, 61, 52, "rain", false)));

        await customService.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-weather-next-week-token",
            Text =
                """{"type":"LISTEN","transID":"trans-weather-next-week","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        var replies = await customService.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-weather-next-week-token",
            Text =
                """{"type":"CLIENT_ASR","transID":"trans-weather-next-week","data":{"text":"forecast for seattle next week"}}"""
        });

        Assert.True(replies.Count >= 3);
        var skillReply = replies.Last(static reply =>
            string.Equals(ReadReplyType(reply), "SKILL_ACTION", StringComparison.Ordinal));
        using var skillPayload = JsonDocument.Parse(skillReply.Text!);
        var jcp = skillPayload.RootElement
            .GetProperty("data")
            .GetProperty("action")
            .GetProperty("config")
            .GetProperty("jcp");

        Assert.Equal("SEQUENCE", jcp.GetProperty("type").GetString());
        var children = jcp.GetProperty("children");
        Assert.Equal(5, children.GetArrayLength());

        var firstChildConfig = children[0].GetProperty("config");
        Assert.True(firstChildConfig.TryGetProperty("display", out var firstDisplay));
        Assert.Equal(
            "weatherTempView",
            firstDisplay.GetProperty("view").GetProperty("data").GetProperty("viewConfig").GetProperty("id")
                .GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-radio-open","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
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
        Assert.Equal("menu",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("@be/radio",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("skill").GetString());
        Assert.Equal(0,
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules").GetArrayLength());
        Assert.Empty(listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities")
            .EnumerateObject());

        using var redirectPayload = JsonDocument.Parse(replies[2].Text!);
        Assert.Equal("@be/radio",
            redirectPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.True(redirectPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("launch")
            .GetBoolean());
        Assert.Equal("menu",
            redirectPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());

        using var completionPayload = JsonDocument.Parse(replies[3].Text!);
        Assert.Equal("@be/radio",
            completionPayload.RootElement.GetProperty("data").GetProperty("skill").GetProperty("id").GetString());
    }

    [Fact]
    public async Task ClientAsr_HotwordLeakageBeforeCommand_StripsNoiseAndRoutesToRadio()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-radio-noise-token",
            Text =
                """{"type":"LISTEN","transID":"trans-radio-noise","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-radio-noise-token",
            Text =
                """{"type":"CLIENT_ASR","transID":"trans-radio-noise","data":{"text":"Hey Jibo - so open the radio"}}"""
        });

        Assert.Equal(4, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_REDIRECT", ReadReplyType(replies[2]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[3]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("menu",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("@be/radio",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("skill").GetString());
        Assert.Equal(0,
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules").GetArrayLength());

        using var redirectPayload = JsonDocument.Parse(replies[2].Text!);
        Assert.Equal("@be/radio",
            redirectPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.True(redirectPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("launch")
            .GetBoolean());
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
            Text =
                """{"type":"LISTEN","transID":"trans-radio-country","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
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
        Assert.Equal("menu",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("Country",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities")
                .GetProperty("station").GetString());

        using var redirectPayload = JsonDocument.Parse(replies[2].Text!);
        Assert.Equal("Country",
            redirectPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities")
                .GetProperty("station").GetString());
        Assert.Equal("play country music",
            redirectPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
    }

    [Fact]
    public async Task ClientAsr_StopThat_EmitsGlobalStopWithoutReply()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-stop-token",
            Text =
                """{"type":"LISTEN","transID":"trans-stop","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-stop-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-stop","data":{"text":"stop that"}}"""
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        var nlu = listenPayload.RootElement.GetProperty("data").GetProperty("nlu");
        Assert.Equal("stop", nlu.GetProperty("intent").GetString());
        Assert.Equal("global_commands", nlu.GetProperty("domain").GetString());
        Assert.Equal("globals/global_commands_launch", nlu.GetProperty("rules")[0].GetString());

        var session = _store.FindSessionByToken("hub-stop-token");
        Assert.NotNull(session);
        Assert.False(session.FollowUpOpen);
        Assert.True(session.TurnState.IgnoreAdditionalAudioUntilUtc.HasValue);
        Assert.True(session.TurnState.IgnoreAdditionalAudioUntilUtc > DateTimeOffset.UtcNow.AddSeconds(3));

        var reopenListenReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-stop-token",
            Text =
                """{"type":"LISTEN","transID":"trans-stop-reopen","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        Assert.Empty(reopenListenReplies);
        Assert.Equal("trans-stop-reopen", session.TurnState.TransId);
        Assert.True(session.TurnState.AwaitingTurnCompletion);
        Assert.True(session.TurnState.SawListen);

        var reopenAudioReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-stop-token",
            Binary = new byte[4096]
        });

        Assert.Empty(reopenAudioReplies);
    }

    [Fact]
    public async Task ClientAsr_StopIt_EmitsGlobalStopWithoutReply()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-stop-it-token",
            Text =
                """{"type":"LISTEN","transID":"trans-stop-it","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-stop-it-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-stop-it","data":{"text":"stop it"}}"""
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        var nlu = listenPayload.RootElement.GetProperty("data").GetProperty("nlu");
        Assert.Equal("stop", nlu.GetProperty("intent").GetString());
        Assert.Equal("global_commands", nlu.GetProperty("domain").GetString());
    }

    [Fact]
    public async Task ClientAsr_ForgetIt_EmitsGlobalStopWithoutReply()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-forget-it-token",
            Text =
                """{"type":"LISTEN","transID":"trans-forget-it","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-forget-it-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-forget-it","data":{"text":"forget it"}}"""
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        var nlu = listenPayload.RootElement.GetProperty("data").GetProperty("nlu");
        Assert.Equal("stop", nlu.GetProperty("intent").GetString());
        Assert.Equal("global_commands", nlu.GetProperty("domain").GetString());
    }

    [Fact]
    public async Task ClientAsr_GoToSleep_RoutesDirectlyToLocalIdle()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-sleep-token",
            Text =
                """{"type":"LISTEN","transID":"trans-sleep","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-sleep-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-sleep","data":{"text":"go to sleep"}}"""
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        var nlu = listenPayload.RootElement.GetProperty("data").GetProperty("nlu");
        Assert.Equal("sleep", nlu.GetProperty("intent").GetString());
        Assert.Equal("global_commands", nlu.GetProperty("domain").GetString());
        Assert.Equal("globals/global_commands_launch", nlu.GetProperty("rules")[0].GetString());
        var match = listenPayload.RootElement.GetProperty("data").GetProperty("match");
        Assert.Equal("@be/idle", match.GetProperty("skillID").GetString());
        Assert.True(match.GetProperty("onRobot").GetBoolean());

        var session = _store.FindSessionByToken("hub-sleep-token");
        Assert.NotNull(session);
        Assert.Equal("sleeping", session!.Metadata["sleepState"]);
        Assert.Equal("ASLEEP", WebSocketTurnFinalizationService.ResolveGlsmPhase(session));
    }

    [Fact]
    public async Task ClientAsr_WakeUp_RoutesDirectlyToLocalGreetings()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wake-token",
            Text =
                """{"type":"LISTEN","transID":"trans-wake","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wake-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-wake","data":{"text":"please wake back up"}}"""
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        var nlu = listenPayload.RootElement.GetProperty("data").GetProperty("nlu");
        Assert.Equal("wake_up", nlu.GetProperty("intent").GetString());
        var match = listenPayload.RootElement.GetProperty("data").GetProperty("match");
        Assert.Equal("@be/greetings", match.GetProperty("skillID").GetString());
        Assert.True(match.GetProperty("onRobot").GetBoolean());

        var session = _store.FindSessionByToken("hub-wake-token");
        Assert.NotNull(session);
        Assert.Equal("awake", session!.Metadata["sleepState"]);
    }

    [Fact]
    public async Task ClientAsr_TurnAround_EmitsIdleSpinAroundRedirect()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-spin-token",
            Text =
                """{"type":"LISTEN","transID":"trans-spin","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-spin-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-spin","data":{"text":"turn around"}}"""
        });

        Assert.Equal(5, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_REDIRECT", ReadReplyType(replies[2]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[3]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[4]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        var nlu = listenPayload.RootElement.GetProperty("data").GetProperty("nlu");
        Assert.Equal("turnAround", nlu.GetProperty("intent").GetString());
        Assert.Equal("global_commands", nlu.GetProperty("domain").GetString());
        Assert.Equal("globals/global_commands_launch", nlu.GetProperty("rules")[0].GetString());

        using var redirectPayload = JsonDocument.Parse(replies[2].Text!);
        Assert.Equal("@be/idle",
            redirectPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.Equal("turnAround",
            redirectPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
    }

    [Fact]
    public async Task ClientAsr_SpinAround_EmitsIdleTurnAroundRedirect()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-spin-around-token",
            Text =
                """{"type":"LISTEN","transID":"trans-spin-around","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-spin-around-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-spin-around","data":{"text":"spin around"}}"""
        });

        Assert.Equal(5, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_REDIRECT", ReadReplyType(replies[2]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[3]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[4]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        var nlu = listenPayload.RootElement.GetProperty("data").GetProperty("nlu");
        Assert.Equal("turnAround", nlu.GetProperty("intent").GetString());
        Assert.Equal("global_commands", nlu.GetProperty("domain").GetString());

        using var redirectPayload = JsonDocument.Parse(replies[2].Text!);
        Assert.Equal("@be/idle",
            redirectPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.Equal("turnAround",
            redirectPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
    }

    [Fact]
    public async Task ClientAsr_Twirl_EmitsIdleTurnAroundRedirect()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-twirl-token",
            Text =
                """{"type":"LISTEN","transID":"trans-twirl","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-twirl-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-twirl","data":{"text":"twirl"}}"""
        });

        Assert.Equal(5, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_REDIRECT", ReadReplyType(replies[2]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[3]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[4]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        var nlu = listenPayload.RootElement.GetProperty("data").GetProperty("nlu");
        Assert.Equal("turnAround", nlu.GetProperty("intent").GetString());
        Assert.Equal("global_commands", nlu.GetProperty("domain").GetString());

        using var redirectPayload = JsonDocument.Parse(replies[2].Text!);
        Assert.Equal("@be/idle",
            redirectPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.Equal("turnAround",
            redirectPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-volume-down","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-volume-down-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-volume-down","data":{"text":"turn it down"}}"""
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        var nlu = listenPayload.RootElement.GetProperty("data").GetProperty("nlu");
        Assert.Equal("volumeDown", nlu.GetProperty("intent").GetString());
        Assert.Equal("global_commands", nlu.GetProperty("domain").GetString());
        Assert.Equal("null", nlu.GetProperty("entities").GetProperty("volumeLevel").GetString());
        Assert.Equal("globals/global_commands_launch", nlu.GetProperty("rules")[0].GetString());

        using var skillPayload = JsonDocument.Parse(replies[2].Text!);
        Assert.Equal("@be/nimbus",
            skillPayload.RootElement.GetProperty("data").GetProperty("skill").GetProperty("id").GetString());
        Assert.True(skillPayload.RootElement.GetProperty("data").GetProperty("final").GetBoolean());
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
            Text =
                """{"type":"LISTEN","transID":"trans-volume-value","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-volume-value-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-volume-value","data":{"text":"set volume to six"}}"""
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        var nlu = listenPayload.RootElement.GetProperty("data").GetProperty("nlu");
        Assert.Equal("volumeToValue", nlu.GetProperty("intent").GetString());
        Assert.Equal("6", nlu.GetProperty("entities").GetProperty("volumeLevel").GetString());
        Assert.Equal("global_commands", nlu.GetProperty("domain").GetString());

        using var skillPayload = JsonDocument.Parse(replies[2].Text!);
        Assert.Equal("@be/nimbus",
            skillPayload.RootElement.GetProperty("data").GetProperty("skill").GetProperty("id").GetString());
        Assert.True(skillPayload.RootElement.GetProperty("data").GetProperty("final").GetBoolean());
    }

    [Fact]
    public async Task ClientAsr_IncreaseTheVolume_EmitsGlobalVolumeUp()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-volume-increase-token",
            Text =
                """{"type":"LISTEN","transID":"trans-volume-increase","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-volume-increase-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-volume-increase","data":{"text":"increase the volume"}}"""
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        var nlu = listenPayload.RootElement.GetProperty("data").GetProperty("nlu");
        Assert.Equal("volumeUp", nlu.GetProperty("intent").GetString());
        Assert.Equal("global_commands", nlu.GetProperty("domain").GetString());
    }

    [Fact]
    public async Task ClientAsr_DecreaseTheVolume_EmitsGlobalVolumeDown()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-volume-decrease-token",
            Text =
                """{"type":"LISTEN","transID":"trans-volume-decrease","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-volume-decrease-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-volume-decrease","data":{"text":"decrease the volume"}}"""
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        var nlu = listenPayload.RootElement.GetProperty("data").GetProperty("nlu");
        Assert.Equal("volumeDown", nlu.GetProperty("intent").GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-volume-query","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
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
        Assert.Equal("volumeQuery",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("@be/settings",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("skill").GetString());

        using var redirectPayload = JsonDocument.Parse(replies[2].Text!);
        Assert.Equal("@be/settings",
            redirectPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.Equal("volumeQuery",
            redirectPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-wod-guess","data":{"rules":["word-of-the-day/puzzle","globals/gui_nav"],"asr":{"hints":["pastoral","doodad","escarpment"],"earlyEOS":["pastoral","doodad","escarpment"]}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-guess-token",
            Text =
                """{"type":"CLIENT_NLU","transID":"trans-wod-guess","data":{"entities":{"guess":"pastoral"},"intent":"guess","rules":["word-of-the-day/puzzle"]}}"""
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("pastoral",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("guess",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("pastoral",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities")
                .GetProperty("guess").GetString());
        Assert.Equal("word-of-the-day/puzzle",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-wod-spoken-guess","data":{"rules":["word-of-the-day/puzzle"],"asr":{"hints":["pastoral","doodad","escarpment"]}}}"""
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
        Assert.Equal("pastoral",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("guess",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("pastoral",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities")
                .GetProperty("guess").GetString());
        Assert.Equal("word-of-the-day/puzzle",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-wod-line-guess","data":{"rules":["word-of-the-day/puzzle"],"asr":{"hints":["doodad","pastoral","escarpment"]}}}"""
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
        Assert.Equal("pastoral",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("guess",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("pastoral",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities")
                .GetProperty("guess").GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-wod-fuzzy-guess","data":{"rules":["word-of-the-day/puzzle"],"asr":{"hints":["aglet","hovel","wisenheimer"]}}}"""
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
        Assert.Equal("aglet",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("aglet",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities")
                .GetProperty("guess").GetString());
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));
    }

    [Fact]
    public async Task ClientAsr_WordOfDayGuess_PhoneticTokenMatchesClosestHint()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-phonetic-guess-token",
            Text =
                """{"type":"LISTEN","transID":"trans-wod-phonetic-guess","data":{"rules":["word-of-the-day/puzzle"],"asr":{"hints":["expunge","abscond","corrugate"]}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-phonetic-guess-token",
            Text =
                """{"type":"CLIENT_ASR","transID":"trans-wod-phonetic-guess","data":{"text":"expansion that's come"}}"""
        });

        Assert.Equal(3, replies.Count);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("expunge",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("expunge",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities")
                .GetProperty("guess").GetString());
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));
    }

    [Fact]
    public async Task ClientAsr_WordOfDayGuess_PrefixTokenMatchesClosestHint()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-prefix-guess-token",
            Text =
                """{"type":"LISTEN","transID":"trans-wod-prefix-guess","data":{"rules":["word-of-the-day/puzzle"],"asr":{"hints":["expunge","corrugate","abscond"]}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-prefix-guess-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-wod-prefix-guess","data":{"text":"expo expo"}}"""
        });

        Assert.Equal(3, replies.Count);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("expunge",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("expunge",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities")
                .GetProperty("guess").GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-wod-guess-rules","data":{"rules":["word-of-the-day/puzzle","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"],"asr":{"hints":["aglet","hovel","wisenheimer"]}}}"""
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
        Assert.Equal("word-of-the-day/puzzle",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
    }

    [Fact]
    public async Task BufferedWordOfDayPuzzleOggAudio_BlankAutoFinalizeStaysLocalAndClearsTurn()
    {
        var stateStore = new InMemoryCloudStateStore();
        var service = CreateService(stateStore, sttStrategies:
        [
            new QueuedBufferedAudioSttStrategy("")
        ]);

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-puzzle-blank-token",
            Text =
                """{"type":"LISTEN","transID":"trans-wod-puzzle-blank","data":{"rules":["word-of-the-day/puzzle","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"],"asr":{"hints":["expunge","abscond","corrugate"],"encoding":"OGG_OPUS","sampleRate":16000}}}"""
        });

        foreach (var frame in new[]
                 {
                     BuildOggFrame(0x02, "OpusHead"),
                     BuildOggFrame(0x00, "OpusTags"),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00)
                 })
        {
            var interimReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-wod-puzzle-blank-token",
                Binary = frame
            });

            Assert.Empty(interimReplies);
        }

        var session = stateStore.FindSessionByToken("hub-wod-puzzle-blank-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2);

        var blankReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-puzzle-blank-token",
            Binary = BuildOggFrame(0x04)
        });

        Assert.Equal(2, blankReplies.Count);
        Assert.Equal("LISTEN", ReadReplyType(blankReplies[0]));
        Assert.Equal("EOS", ReadReplyType(blankReplies[1]));

        using var listenPayload = JsonDocument.Parse(blankReplies[0].Text!);
        Assert.Equal(string.Empty,
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        var rules = listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules");
        Assert.Single(rules.EnumerateArray());
        Assert.Equal("word-of-the-day/puzzle", rules[0].GetString());

        Assert.Null(session.LastIntent);
        Assert.Equal("no-input", session.LastListenType);
        Assert.False(session.TurnState.AwaitingTurnCompletion);
        Assert.False(session.TurnState.SawListen);
        Assert.False(session.TurnState.SawContext);
        Assert.Equal(0, session.TurnState.BufferedAudioBytes);
        Assert.Equal(0, session.TurnState.BufferedAudioChunkCount);

        var lateAudioReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-puzzle-blank-token",
            Binary = BuildOggFrame(0x00)
        });

        Assert.Empty(lateAudioReplies);
        Assert.Equal(0, session.TurnState.BufferedAudioBytes);
        Assert.Equal(0, session.TurnState.BufferedAudioChunkCount);
    }

    [Fact]
    public async Task ClientAsr_WordOfDayPuzzleRobotSelfAudio_StaysLocalNoInput()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-self-audio-token",
            Text =
                """{"type":"LISTEN","transID":"trans-wod-self-audio","data":{"rules":["word-of-the-day/puzzle","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"],"asr":{"hints":["expunge","corrugate","abscond"]}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-self-audio-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-wod-self-audio","data":{"text":"I didn't catch that"}}"""
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal(string.Empty,
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        var rules = listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules");
        Assert.Single(rules.EnumerateArray());
        Assert.Equal("word-of-the-day/puzzle", rules[0].GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-settings-no","data":{"rules":["settings/download_now_later","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"],"asr":{"hints":["$YESNO"]}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-settings-no-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-settings-no","data":{"text":"No."}}"""
        });

        Assert.Equal(2, replies.Count);

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        var rules = listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules");
        Assert.Single(rules.EnumerateArray());
        Assert.Equal("settings/download_now_later", rules[0].GetString());
        Assert.Equal("settings/download_now_later",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
        Assert.Equal("no",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-wod-launch","data":{"rules":["launch","globals/global_commands_launch"]}}"""
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
        Assert.Equal("menu",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal(string.Empty,
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("word-of-the-day",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities")
                .GetProperty("domain").GetString());
        Assert.Equal("@be/word-of-the-day",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("skill").GetString());
        Assert.Equal("word-of-the-day/menu",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule").GetString());
        Assert.Equal("SKILL_REDIRECT", ReadReplyType(replies[2]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[3]));

        using var redirectPayload = JsonDocument.Parse(replies[2].Text!);
        Assert.Equal("@be/word-of-the-day",
            redirectPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.True(redirectPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("onRobot")
            .GetBoolean());
        Assert.True(redirectPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("launch")
            .GetBoolean());

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
            Text =
                """{"type":"LISTEN","transID":"trans-wod-auto","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-auto-token",
            Text =
                """{"type":"CONTEXT","transID":"trans-wod-auto","data":{"audioTranscriptHint":"play word of the day"}}"""
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
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(600);

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
        Assert.Equal("menu",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("word-of-the-day",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities")
                .GetProperty("domain").GetString());
        Assert.Equal("@be/word-of-the-day",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("skill").GetString());

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
            Text =
                """{"type":"CLIENT_NLU","transID":"trans-wod-late-empty","data":{"entities":{"guess":"pastoral"},"intent":"guess","rules":["word-of-the-day/puzzle"]}}"""
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
            Text =
                """{"type":"LISTEN","transID":"trans-wod-right-word","data":{"rules":["word-of-the-day/right_word","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-right-word-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-wod-right-word","data":{}}"""
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.False(listenPayload.RootElement.GetProperty("data").TryGetProperty("match", out _));
        Assert.Equal("trans-wod-right-word", listenPayload.RootElement.GetProperty("transID").GetString());
        var rules = listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules");
        Assert.Single(rules.EnumerateArray());
        Assert.Equal("word-of-the-day/right_word", rules[0].GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-listen-setup","data":{"rules":["main-menu/execute_fun_stuff","globals/global_commands_launch"],"mode":"CLIENT_NLU"}}"""
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
            Text =
                """{"type":"LISTEN","transID":"trans-stale-listen","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
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
            Text =
                """{"type":"LISTEN","transID":"trans-stale-listen","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
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
        var initialReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-right-word-audio-token",
            Text =
                """{"type":"LISTEN","transID":"trans-wod-right-word-audio","data":{"rules":["word-of-the-day/right_word","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"]}}"""
        });

        Assert.Equal(2, initialReplies.Count);
        Assert.Equal("LISTEN", ReadReplyType(initialReplies[0]));
        Assert.Equal("EOS", ReadReplyType(initialReplies[1]));
        using (var initialPayload = JsonDocument.Parse(initialReplies[0].Text!))
        {
            Assert.Equal("trans-wod-right-word-audio", initialPayload.RootElement.GetProperty("transID").GetString());
            var initialRules = initialPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules");
            Assert.Single(initialRules.EnumerateArray());
            Assert.Equal("word-of-the-day/right_word", initialRules[0].GetString());
        }

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-wod-right-word-audio-token",
            Text =
                """{"type":"LISTEN","transID":"trans-wod-right-word-audio","data":{"rules":["word-of-the-day/right_word","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"]}}"""
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal(string.Empty,
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.False(listenPayload.RootElement.GetProperty("data").TryGetProperty("match", out _));
        Assert.Equal("trans-wod-right-word-audio", listenPayload.RootElement.GetProperty("transID").GetString());
        var rules = listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules");
        Assert.Single(rules.EnumerateArray());
        Assert.Equal("word-of-the-day/right_word", rules[0].GetString());

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
            Text =
                """{"type":"LISTEN","transID":"trans-blank-audio","data":{"text":"[BLANK_AUDIO]","rules":["launch","globals/global_commands_launch"]}}"""
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
            Text =
                """{"type":"LISTEN","transID":"trans-context-no-listen-hello","data":{"text":"hello","rules":["launch","globals/global_commands_launch"]}}"""
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
            Text =
                """{"type":"LISTEN","transID":"trans-delete-the-alarm","data":{"text":"So, delete the alarm.","rules":["launch","globals/global_commands_launch"]}}"""
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.DoesNotContain(replies, reply =>
            string.Equals(ReadReplyType(reply), "SKILL_REDIRECT", StringComparison.Ordinal));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("delete",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("@be/clock",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("skill").GetString());
        Assert.Equal("@be/clock",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.Equal("alarm",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("entities")
                .GetProperty("domain").GetString());

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
            Text =
                """{"type":"LISTEN","transID":"trans-initial-hotphrase","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
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
            Text =
                """{"type":"LISTEN","transID":"trans-empty-hotphrase","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
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
        Assert.Equal("hello",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("hello",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());

        var session = _store.FindSessionByToken("hub-empty-hotphrase-token");
        Assert.NotNull(session);
        Assert.True(session.FollowUpOpen);
    }

    [Fact]
    public async Task ClientAsr_LeadingFillerBeforeGreeting_IsStrippedAndRoutesToGreeting()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-filler-greeting-token",
            Text = """{"type":"LISTEN","transID":"trans-filler-greeting","data":{"rules":["wake-word"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-filler-greeting-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-filler-greeting","data":{"text":"um hello"}}"""
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("hello",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("hello",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());

        var session = _store.FindSessionByToken("hub-filler-greeting-token");
        Assert.NotNull(session);
        Assert.Equal("hello", session.LastTranscript);
        Assert.Equal("hello", session.LastIntent);
    }

    [Fact]
    public async Task ClientAsr_FillerOnlyTranscript_IsIgnoredAsNoise()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-filler-only-token",
            Text = """{"type":"LISTEN","transID":"trans-filler-only","data":{"rules":["wake-word"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-filler-only-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-filler-only","data":{"text":"hmm"}}"""
        });

        Assert.Empty(replies);

        var session = _store.FindSessionByToken("hub-filler-only-token");
        Assert.NotNull(session);
        Assert.Null(session.LastIntent);
        Assert.Null(session.LastTranscript);
    }

    [Fact]
    public async Task ClientAsr_FillerPlusGenericCommand_IsIgnoredAsLowSignalNoise()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-low-signal-command-token",
            Text = """{"type":"LISTEN","transID":"trans-low-signal-command","data":{"rules":["wake-word"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-low-signal-command-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-low-signal-command","data":{"text":"so command"}}"""
        });

        Assert.Empty(replies);

        var session = _store.FindSessionByToken("hub-low-signal-command-token");
        Assert.NotNull(session);
        Assert.Null(session.LastIntent);
        Assert.Null(session.LastTranscript);
    }

    [Fact]
    public async Task Listen_BareTwerk_IsPreservedAndRoutesToDanceIntent()
    {
        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-twerk-token",
            Text =
                """{"type":"LISTEN","transID":"trans-twerk","data":{"text":"twerk","rules":["launch","globals/global_commands_launch"]}}"""
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("twerk",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("twerk",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());

        var session = _store.FindSessionByToken("hub-twerk-token");
        Assert.NotNull(session);
        Assert.Equal("twerk", session.LastIntent);
        Assert.Equal("twerk", session.LastTranscript);
    }

    [Fact]
    public async Task ClientAsr_CanYouTwerk_IsPreservedAndRoutesToDanceIntent()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-can-twerk-token",
            Text =
                """{"type":"LISTEN","transID":"trans-can-twerk","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-can-twerk-token",
            Text = """{"type":"CLIENT_ASR","transID":"trans-can-twerk","data":{"text":"can you twerk"}}"""
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("twerk",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("can you twerk",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());

        var session = _store.FindSessionByToken("hub-can-twerk-token");
        Assert.NotNull(session);
        Assert.Equal("twerk", session.LastIntent);
        Assert.Equal("can you twerk", session.LastTranscript);
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
            Text =
                """{"type":"CONTEXT","transID":"trans-audio","data":{"topic":"conversation","audioTranscriptHint":"tell me a joke"}}"""
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
        Assert.Equal("tell me a joke",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("joke",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());

        var session = _store.FindSessionByToken("hub-audio-token");
        Assert.NotNull(session);
        Assert.Equal(0, session.TurnState.BufferedAudioBytes);
        Assert.Equal(0, session.TurnState.BufferedAudioChunkCount);
        Assert.Null(session.TurnState.AudioTranscriptHint);
        Assert.False(session.Metadata.ContainsKey("audioTranscriptHint"));
    }

    [Fact]
    public async Task BufferedAudio_HotphraseLaunch_EarlyFinalizesBeforeHardTimeout()
    {
        var stateStore = new InMemoryCloudStateStore();
        var service = CreateService(stateStore, sttStrategies:
        [
            new QueuedBufferedAudioSttStrategy("Hey Jibo, what's your cloud version?")
        ]);

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-early-probe-cloud-version-token",
            Text =
                """{"type":"LISTEN","transID":"trans-early-probe-cloud-version","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-early-probe-cloud-version-token",
            Text =
                """{"type":"CONTEXT","transID":"trans-early-probe-cloud-version","data":{"topic":"conversation"}}"""
        });

        foreach (var frame in new[]
                 {
                     BuildOggFrame(0x02, "OpusHead"),
                     BuildOggFrame(0x00, "OpusTags"),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00)
                 })
        {
            var interimReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-early-probe-cloud-version-token",
                Binary = frame
            });

            Assert.Empty(interimReplies);
        }

        var session = stateStore.FindSessionByToken("hub-early-probe-cloud-version-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(2500);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(1300);

        var replies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-early-probe-cloud-version-token",
            Binary = BuildOggFrame(0x00)
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("cloud_version",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("what's your cloud version",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());

        Assert.False(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal("cloud_version", session.LastIntent);
        Assert.Equal("what's your cloud version", session.LastTranscript);
    }

    [Fact]
    public async Task BufferedAudio_HotphraseLaunch_TranscriptHintFinalizesWithoutSilenceGap()
    {
        var stateStore = new InMemoryCloudStateStore();
        var service = CreateService(stateStore, sttStrategies:
        [
            new QueuedBufferedAudioSttStrategy("Hey Jibo, what's your cloud version?")
        ]);

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-early-probe-cloud-version-hint-token",
            Text =
                """{"type":"LISTEN","transID":"trans-early-probe-cloud-version-hint","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        foreach (var frame in new[]
                 {
                     BuildOggFrame(0x02, "OpusHead"),
                     BuildOggFrame(0x00, "OpusTags"),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00)
                 })
        {
            var interimReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-early-probe-cloud-version-hint-token",
                Binary = frame
            });

            Assert.Empty(interimReplies);
        }

        var replies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-early-probe-cloud-version-hint-token",
            Text =
                """{"type":"CONTEXT","transID":"trans-early-probe-cloud-version-hint","data":{"audioTranscriptHint":"what's your cloud version"}}"""
        });

        Assert.Empty(replies);

        var session = stateStore.FindSessionByToken("hub-early-probe-cloud-version-hint-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(4);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(100);

        replies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-early-probe-cloud-version-hint-token",
            Binary = BuildOggFrame(0x00)
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("cloud_version",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("what's your cloud version",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());

        Assert.False(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal("cloud_version", session.LastIntent);
        Assert.Equal("what's your cloud version", session.LastTranscript);
    }

    [Fact]
    public async Task BufferedAudio_HotphraseLaunch_ThreePageProbeFinalizesEarlyWithoutTranscriptHint()
    {
        var stateStore = new InMemoryCloudStateStore();
        var service = CreateService(stateStore, sttStrategies:
        [
            new QueuedBufferedAudioSttStrategy("Hey Jibo, what's your cloud version?")
        ]);

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-early-probe-cloud-version-three-page-token",
            Text =
                """{"type":"LISTEN","transID":"trans-early-probe-cloud-version-three-page","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        foreach (var frame in new[]
                 {
                     BuildOggFrame(0x02, "OpusHead"),
                     BuildOggFrame(0x00, "OpusTags"),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00)
                 })
        {
            var interimReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-early-probe-cloud-version-three-page-token",
                Binary = frame
            });

            Assert.Empty(interimReplies);
        }

        var contextReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-early-probe-cloud-version-three-page-token",
            Text =
                """{"type":"CONTEXT","transID":"trans-early-probe-cloud-version-three-page","data":{"audioTranscriptHint":"what's your cloud version"}}"""
        });

        Assert.Empty(contextReplies);

        var session = stateStore.FindSessionByToken("hub-early-probe-cloud-version-three-page-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(4);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(100);

        var replies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-early-probe-cloud-version-three-page-token",
            Binary = BuildOggFrame(0x00)
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("cloud_version",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("what's your cloud version",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());

        Assert.False(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal("cloud_version", session.LastIntent);
        Assert.Equal("what's your cloud version", session.LastTranscript);
    }

    [Fact]
    public async Task BufferedAudio_SharedYesNoPrompt_EarlyFinalizesBeforeHardTimeout()
    {
        var stateStore = new InMemoryCloudStateStore();
        var service = CreateService(stateStore, sttStrategies:
        [
            new QueuedBufferedAudioSttStrategy("yes")
        ]);

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-early-probe-yesno-token",
            Text =
                """{"type":"LISTEN","transID":"trans-early-probe-yesno","data":{"rules":["shared/yes_no","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"],"asr":{"hints":["$YESNO"]}}}"""
        });

        foreach (var frame in new[]
                 {
                     BuildOggFrame(0x02, "OpusHead"),
                     BuildOggFrame(0x00, "OpusTags"),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00)
                 })
        {
            var interimReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-early-probe-yesno-token",
                Binary = frame
            });

            Assert.Empty(interimReplies);
        }

        var session = stateStore.FindSessionByToken("hub-early-probe-yesno-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(4);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(1300);

        var replies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-early-probe-yesno-token",
            Binary = BuildOggFrame(0x00)
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("yes",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("yes",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("shared/yes_no",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules")[0].GetString());

        Assert.False(session.TurnState.AwaitingTurnCompletion);
    }

    [Fact]
    public async Task BufferedAudio_SurprisesDateOfferPrompt_ReturnsLocalYesWithoutSkillAction()
    {
        var stateStore = new InMemoryCloudStateStore();
        var service = CreateService(stateStore, sttStrategies:
        [
            new QueuedBufferedAudioSttStrategy("Yes.")
        ]);

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-surprises-date-yesno-token",
            Text =
                """{"type":"LISTEN","transID":"trans-surprises-date-yesno","data":{"hotphrase":false,"rules":["surprises-date/offer_date_fact","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"],"asr":{"hints":["$YESNO"],"earlyEOS":["$YESNO"],"encoding":"OGG_OPUS","sampleRate":16000}}}"""
        });

        foreach (var frame in new[]
                 {
                     BuildOggFrame(0x02, "OpusHead"),
                     BuildOggFrame(0x00, "OpusTags"),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00)
                 })
        {
            var interimReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-surprises-date-yesno-token",
                Binary = frame
            });

            Assert.Empty(interimReplies);
        }

        var session = stateStore.FindSessionByToken("hub-surprises-date-yesno-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(4);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(1300);

        var replies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-surprises-date-yesno-token",
            Binary = BuildOggFrame(0x00)
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("yes",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("yes",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("surprises-date/offer_date_fact",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules")[0].GetString());
        Assert.Equal("yes", session.LastIntent);
        Assert.False(session.TurnState.AwaitingTurnCompletion);
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
    public async Task PrematureSocketClose_DefersAutoFinalizeUntilGraceExpires()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-premature-close-token",
            Text = """{"type":"LISTEN","transID":"trans-premature-close","data":{"rules":["wake-word"]}}"""
        });

        for (var index = 0; index < 4; index += 1)
        {
            var interimReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-premature-close-token",
                Binary = new byte[3000]
            });

            Assert.Empty(interimReplies);
        }

        var session = _store.FindSessionByToken("hub-premature-close-token");
        Assert.NotNull(session);

        _service.MarkPrematureSocketLoopEnded(session);

        var deferredReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-premature-close-token",
            Binary = new byte[3000]
        });

        Assert.Empty(deferredReplies);
        Assert.True(session.TurnState.AwaitingTurnCompletion);
        Assert.True(session.TurnState.BufferedAudioChunkCount >= 4);
        Assert.True(session.TurnState.BufferedAudioBytes >= 12000);

        session.TurnState.AwaitingTurnCompletion = false;
        session.TurnState.SawListen = true;
        session.TurnState.SawContext = false;
        session.TurnState.BufferedAudioBytes = 0;
        session.TurnState.BufferedAudioChunkCount = 0;
        session.FollowUpExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(1);

        var ignoredFollowUpReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-premature-close-token",
            Binary = new byte[3000]
        });

        Assert.Empty(ignoredFollowUpReplies);
        Assert.True(session.FollowUpOpen);

        session.TurnState.AutoFinalizeBlockedUntilUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(600);
        session.TurnState.AwaitingTurnCompletion = true;
        session.TurnState.SawListen = true;
        session.TurnState.SawContext = false;
        session.TurnState.BufferedAudioChunkCount = 4;
        session.TurnState.BufferedAudioBytes = 12000;

        var finalReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-premature-close-token",
            Binary = new byte[3000]
        });

        Assert.Equal(3, finalReplies.Count);
        Assert.Equal("LISTEN", ReadReplyType(finalReplies[0]));
        Assert.Equal("EOS", ReadReplyType(finalReplies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(finalReplies[2]));
        Assert.Equal("heyJibo",
            JsonDocument.Parse(finalReplies[0].Text!).RootElement.GetProperty("data").GetProperty("nlu")
                .GetProperty("intent").GetString());
    }

    [Fact]
    public async Task PrematureSocketClose_ForPreviousTransId_DoesNotPoisonCurrentListen()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-overlap-close-token",
            Text =
                """{"type":"LISTEN","transID":"trans-overlap-old","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-overlap-close-token",
            Binary = BuildOggFrame(0x02, "OpusHead")
        });

        var session = _store.FindSessionByToken("hub-overlap-close-token");
        Assert.NotNull(session);

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-overlap-close-token",
            Text =
                """{"type":"LISTEN","transID":"trans-overlap-new","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        var marked = _service.MarkPrematureSocketLoopEnded(session, "trans-overlap-old");

        Assert.False(marked);
        Assert.Null(session.TurnState.AutoFinalizeBlockedUntilUtc);
        Assert.Null(session.TurnState.IgnoreAdditionalAudioUntilUtc);
        Assert.Equal("trans-overlap-new", session.TurnState.TransId);

        var audioReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-overlap-close-token",
            Binary = BuildOggFrame(0x00)
        });

        Assert.Empty(audioReplies);
        Assert.True(session.TurnState.BufferedAudioBytes > 0);
        Assert.False(session.TurnState.AutoFinalizeBlockedUntilUtc > DateTimeOffset.UtcNow);
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
            Text =
                """{"type":"CONTEXT","transID":"trans-audio-chat","data":{"audioTranscriptHint":"hello from buffered audio"}}"""
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
        Assert.Equal("hello from buffered audio",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("hello",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());

        using var skillPayload = JsonDocument.Parse(finalizeReplies[2].Text!);
        Assert.Equal("chitchat-skill",
            skillPayload.RootElement.GetProperty("data").GetProperty("skill").GetProperty("id").GetString());
    }

    [Fact]
    public async Task BufferedHotphraseAudio_WithSttFailure_DoesNotSyntheticGreeting()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-greeting-token",
            Text =
                """{"type":"LISTEN","transID":"trans-hotphrase-greeting","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
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
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(600);
        session.TurnState.LastSttError = "ffmpeg decode failed";

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-greeting-token",
            Binary = new byte[3000]
        });

        Assert.Empty(replies);
        Assert.True(session.TurnState.AwaitingTurnCompletion);
    }

    [Fact]
    public async Task BufferedHotphraseAudio_FirstMissingTranscriptStaysPending()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-pending-token",
            Text =
                """{"type":"LISTEN","transID":"trans-hotphrase-pending","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-pending-token",
            Text = """{"type":"CONTEXT","transID":"trans-hotphrase-pending","data":{"topic":"conversation"}}"""
        });

        for (var index = 0; index < 4; index += 1)
        {
            var interimReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-hotphrase-pending-token",
                Binary = new byte[3000]
            });

            Assert.Empty(interimReplies);
        }

        var session = _store.FindSessionByToken("hub-hotphrase-pending-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(600);

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-pending-token",
            Binary = new byte[3000]
        });

        Assert.Empty(replies);
        Assert.True(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal(0, session.TurnState.HotphraseEmptyTurnCount);
    }

    [Fact]
    public async Task BufferedHotphraseOggAudio_NimbusContext_EarlyProbeLaunchesClockBeforeHardTimeout()
    {
        var stateStore = new InMemoryCloudStateStore();
        var service = CreateService(stateStore, sttStrategies:
        [
            new QueuedBufferedAudioSttStrategy("Hey Jibo, what time is it?")
        ]);

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-early-probe-token",
            Text =
                """{"type":"LISTEN","transID":"trans-hotphrase-ogg-early-probe","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-early-probe-token",
            Text =
                """{"type":"CONTEXT","transID":"trans-hotphrase-ogg-early-probe","data":{"skill":{"id":"@be/nimbus"}}}"""
        });

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-early-probe-token",
            Binary = BuildOggFrame(0x02, "OpusHead")
        });
        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-early-probe-token",
            Binary = BuildOggFrame(0x00, "OpusTags")
        });

        for (var index = 0; index < 8; index += 1)
        {
            var interimReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-hotphrase-ogg-early-probe-token",
                Binary = BuildOggFrame(0x00)
            });

            Assert.Empty(interimReplies);
        }

        var session = stateStore.FindSessionByToken("hub-hotphrase-ogg-early-probe-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(5000);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(1300);

        var replies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-early-probe-token",
            Binary = BuildOggFrame(0x04)
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("what time is it",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("askForTime",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("@be/clock",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("skill").GetString());
        Assert.Equal("@be/clock",
            listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.True(listenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("onRobot")
            .GetBoolean());
        Assert.False(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal(0, session.TurnState.BufferedAudioBytes);
    }

    [Fact]
    public async Task BufferedHotphraseOggAudio_ConcurrentIdleAndFrameFinalization_TranscribesOnce()
    {
        var stateStore = new InMemoryCloudStateStore();
        var stt = new BlockingBufferedAudioSttStrategy("Hey Jibo, what time is it?");
        var service = CreateService(stateStore, sttStrategies: [stt]);
        var envelope = new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-concurrent-finalize-token",
            Text =
                """{"type":"LISTEN","transID":"trans-concurrent-finalize","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        };
        WebSocketMessageEnvelope AudioEnvelope(byte[] binary) => new()
        {
            HostName = envelope.HostName,
            Path = envelope.Path,
            Kind = envelope.Kind,
            Token = envelope.Token,
            Binary = binary
        };

        await service.HandleMessageAsync(envelope);
        await service.HandleMessageAsync(AudioEnvelope(BuildOggFrame(0x02, "OpusHead")));
        await service.HandleMessageAsync(AudioEnvelope(BuildOggFrame(0x00, "OpusTags")));
        for (var index = 0; index < 8; index += 1)
            await service.HandleMessageAsync(AudioEnvelope(BuildOggFrame(0x00)));

        var session = stateStore.FindSessionByToken(envelope.Token!);
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(5);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(1300);

        var idleFinalization = service.HandleIdleAsync(session, envelope);
        await stt.Started.WaitAsync(TimeSpan.FromSeconds(5));

        var frameReplies = await service.HandleMessageAsync(AudioEnvelope(BuildOggFrame(0x04)));
        Assert.Empty(frameReplies);

        stt.Release();
        var idleReplies = await idleFinalization;

        Assert.Equal(1, stt.TranscriptionCount);
        using var listenPayload = JsonDocument.Parse(idleReplies[0].Text!);
        Assert.Equal("askForTime",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
    }

    [Fact]
    public async Task BufferedHotphraseOggAudio_BlankEarlyProbeStaysOpenBeforeHardTimeout()
    {
        var stateStore = new InMemoryCloudStateStore();
        var service = CreateService(stateStore, sttStrategies:
        [
            new QueuedBufferedAudioSttStrategy("")
        ]);

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-blank-early-probe-token",
            Text =
                """{"type":"LISTEN","transID":"trans-hotphrase-ogg-blank-early-probe","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-blank-early-probe-token",
            Binary = BuildOggFrame(0x02, "OpusHead")
        });
        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-blank-early-probe-token",
            Binary = BuildOggFrame(0x00, "OpusTags")
        });

        for (var index = 0; index < 8; index += 1)
        {
            var interimReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-hotphrase-ogg-blank-early-probe-token",
                Binary = BuildOggFrame(0x00)
            });

            Assert.Empty(interimReplies);
        }

        var session = stateStore.FindSessionByToken("hub-hotphrase-ogg-blank-early-probe-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(5000);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(1300);

        var replies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-blank-early-probe-token",
            Binary = BuildOggFrame(0x00)
        });

        Assert.Empty(replies);
        Assert.True(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal(1, session.TurnState.FinalizeAttemptCount);
        Assert.Null(session.LastIntent);
        Assert.Null(session.LastTranscript);
    }

    [Fact]
    public async Task BufferedHotphraseOggAudio_BlankEarlyProbeRetriesNextAudioBeforeHardTimeout()
    {
        var stateStore = new InMemoryCloudStateStore();
        var service = CreateService(stateStore, sttStrategies:
        [
            new QueuedBufferedAudioSttStrategy("", "Hey Jibo, what's your cloud version?")
        ]);

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-blank-early-probe-retry-token",
            Text =
                """{"type":"LISTEN","transID":"trans-hotphrase-ogg-blank-early-probe-retry","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-blank-early-probe-retry-token",
            Binary = BuildOggFrame(0x02, "OpusHead")
        });
        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-blank-early-probe-retry-token",
            Binary = BuildOggFrame(0x00, "OpusTags")
        });

        for (var index = 0; index < 8; index += 1)
        {
            var interimReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-hotphrase-ogg-blank-early-probe-retry-token",
                Binary = BuildOggFrame(0x00)
            });

            Assert.Empty(interimReplies);
        }

        var session = stateStore.FindSessionByToken("hub-hotphrase-ogg-blank-early-probe-retry-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(5000);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(1300);

        var blankProbeReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-blank-early-probe-retry-token",
            Binary = BuildOggFrame(0x00)
        });

        Assert.Empty(blankProbeReplies);
        Assert.True(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal(1, session.TurnState.FinalizeAttemptCount);
        Assert.Null(session.LastIntent);
        Assert.Null(session.LastTranscript);

        var commandReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-blank-early-probe-retry-token",
            Binary = BuildOggFrame(0x00)
        });

        Assert.Equal(3, commandReplies.Count);
        Assert.Equal("LISTEN", ReadReplyType(commandReplies[0]));
        Assert.Equal("EOS", ReadReplyType(commandReplies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(commandReplies[2]));

        using var listenPayload = JsonDocument.Parse(commandReplies[0].Text!);
        Assert.Equal("what's your cloud version",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("cloud_version",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("cloud_version", session.LastIntent);
        Assert.False(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal(0, session.TurnState.BufferedAudioBytes);
    }

    [Fact]
    public async Task BufferedHotphraseOggAudio_HotphraseOnlyEarlyProbeRetriesNextAudioBeforeHardTimeout()
    {
        var stateStore = new InMemoryCloudStateStore();
        var service = CreateService(stateStore, sttStrategies:
        [
            new QueuedBufferedAudioSttStrategy("Hey, Gebo.", "Hey Jibo, what's your cloud version?")
        ]);

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-early-probe-retry-token",
            Text =
                """{"type":"LISTEN","transID":"trans-hotphrase-ogg-early-probe-retry","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-early-probe-retry-token",
            Binary = BuildOggFrame(0x02, "OpusHead")
        });
        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-early-probe-retry-token",
            Binary = BuildOggFrame(0x00, "OpusTags")
        });

        for (var index = 0; index < 6; index += 1)
        {
            var interimReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-hotphrase-ogg-early-probe-retry-token",
                Binary = BuildOggFrame(0x00)
            });

            Assert.Empty(interimReplies);
        }

        var session = stateStore.FindSessionByToken("hub-hotphrase-ogg-early-probe-retry-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(4000);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(1300);

        var hotphraseOnlyReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-early-probe-retry-token",
            Binary = BuildOggFrame(0x00)
        });

        Assert.Empty(hotphraseOnlyReplies);
        Assert.True(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal(1, session.TurnState.FinalizeAttemptCount);
        Assert.Null(session.LastIntent);
        Assert.Null(session.LastTranscript);

        var commandReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-early-probe-retry-token",
            Binary = BuildOggFrame(0x00)
        });

        Assert.Equal(3, commandReplies.Count);
        Assert.Equal("LISTEN", ReadReplyType(commandReplies[0]));
        Assert.Equal("EOS", ReadReplyType(commandReplies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(commandReplies[2]));

        using var listenPayload = JsonDocument.Parse(commandReplies[0].Text!);
        Assert.Equal("what's your cloud version",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("cloud_version",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("cloud_version", session.LastIntent);
        Assert.False(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal(0, session.TurnState.BufferedAudioBytes);
    }

    [Fact]
    public async Task BufferedHotphraseOggAudio_HotphraseOnlyHonorsListenMaxSpeechTimeout()
    {
        var stateStore = new InMemoryCloudStateStore();
        var service = CreateService(stateStore, sttStrategies:
        [
            new QueuedBufferedAudioSttStrategy("Hey Jubo")
        ]);

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-max-speech-token",
            Text =
                """{"type":"LISTEN","transID":"trans-hotphrase-ogg-max-speech","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"],"asr":{"encoding":"OGG_OPUS","sosTimeout":7000,"maxSpeechTimeout":20000}}}"""
        });

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-max-speech-token",
            Text = """{"type":"CONTEXT","transID":"trans-hotphrase-ogg-max-speech","data":{"topic":"conversation"}}"""
        });

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-max-speech-token",
            Binary = BuildOggFrame(0x02, "OpusHead")
        });
        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-max-speech-token",
            Binary = BuildOggFrame(0x00, "OpusTags")
        });

        for (var index = 0; index < 8; index += 1)
        {
            var interimReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-hotphrase-ogg-max-speech-token",
                Binary = BuildOggFrame(0x00)
            });

            Assert.Empty(interimReplies);
        }

        var session = stateStore.FindSessionByToken("hub-hotphrase-ogg-max-speech-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(7500);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(1300);

        var replies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-max-speech-token",
            Binary = BuildOggFrame(0x00)
        });

        Assert.Empty(replies);
        Assert.True(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal(1, session.TurnState.FinalizeAttemptCount);
        Assert.Null(session.LastIntent);
        Assert.Null(session.LastTranscript);
    }

    [Fact]
    public async Task BufferedHotphraseOggAudio_HotphraseOnlyCanRecoverAfterAttemptLimitBeforeMaxSpeechTimeout()
    {
        var stateStore = new InMemoryCloudStateStore();
        var service = CreateService(stateStore, sttStrategies:
        [
            new QueuedBufferedAudioSttStrategy(
                "Hey Jubo",
                "Hey Jubo",
                "Hey Jubo",
                "Hey Jubo",
                "Hey Jibo, what's your cloud version?")
        ]);

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-max-speech-recover-token",
            Text =
                """{"type":"LISTEN","transID":"trans-hotphrase-ogg-max-speech-recover","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"],"asr":{"encoding":"OGG_OPUS","sosTimeout":7000,"maxSpeechTimeout":20000}}}"""
        });

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-max-speech-recover-token",
            Text =
                """{"type":"CONTEXT","transID":"trans-hotphrase-ogg-max-speech-recover","data":{"topic":"conversation"}}"""
        });

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-max-speech-recover-token",
            Binary = BuildOggFrame(0x02, "OpusHead")
        });
        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-max-speech-recover-token",
            Binary = BuildOggFrame(0x00, "OpusTags")
        });

        for (var index = 0; index < 8; index += 1)
        {
            var interimReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-hotphrase-ogg-max-speech-recover-token",
                Binary = BuildOggFrame(0x00)
            });

            Assert.Empty(interimReplies);
        }

        var session = stateStore.FindSessionByToken("hub-hotphrase-ogg-max-speech-recover-token");
        Assert.NotNull(session);

        for (var attempt = 1; attempt <= 4; attempt += 1)
        {
            session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(7500);
            session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(1300);

            var hotphraseOnlyReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-hotphrase-ogg-max-speech-recover-token",
                Binary = BuildOggFrame(0x00)
            });

            Assert.Empty(hotphraseOnlyReplies);
            Assert.True(session.TurnState.AwaitingTurnCompletion);
            Assert.Equal(attempt, session.TurnState.FinalizeAttemptCount);
        }

        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(8000);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(1300);

        var commandReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-max-speech-recover-token",
            Binary = BuildOggFrame(0x00)
        });

        Assert.Equal(3, commandReplies.Count);
        Assert.Equal("LISTEN", ReadReplyType(commandReplies[0]));
        Assert.Equal("EOS", ReadReplyType(commandReplies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(commandReplies[2]));

        using var listenPayload = JsonDocument.Parse(commandReplies[0].Text!);
        Assert.Equal("what's your cloud version",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("cloud_version",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("cloud_version", session.LastIntent);
        Assert.False(session.TurnState.AwaitingTurnCompletion);
    }

    [Fact]
    public async Task BufferedHotphraseOggAudio_TerminalWakePhraseBleedRetriesNextAudioBeforeHardTimeout()
    {
        var stateStore = new InMemoryCloudStateStore();
        var service = CreateService(stateStore, sttStrategies:
        [
            new QueuedBufferedAudioSttStrategy(
                "the snail said when he was riding on the turtle's back whee hey jibo",
                "Hey Jibo, what's your cloud version?")
        ]);

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-terminal-wake-retry-token",
            Text =
                """{"type":"LISTEN","transID":"trans-hotphrase-ogg-terminal-wake-retry","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-terminal-wake-retry-token",
            Binary = BuildOggFrame(0x02, "OpusHead")
        });
        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-terminal-wake-retry-token",
            Binary = BuildOggFrame(0x00, "OpusTags")
        });

        for (var index = 0; index < 8; index += 1)
        {
            var interimReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-hotphrase-ogg-terminal-wake-retry-token",
                Binary = BuildOggFrame(0x00)
            });

            Assert.Empty(interimReplies);
        }

        var session = stateStore.FindSessionByToken("hub-hotphrase-ogg-terminal-wake-retry-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(5000);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(1300);

        var terminalWakeReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-terminal-wake-retry-token",
            Binary = BuildOggFrame(0x00)
        });

        Assert.Empty(terminalWakeReplies);
        Assert.True(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal(1, session.TurnState.FinalizeAttemptCount);
        Assert.Null(session.LastIntent);
        Assert.Null(session.LastTranscript);

        var commandReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-terminal-wake-retry-token",
            Binary = BuildOggFrame(0x00)
        });

        Assert.Equal(3, commandReplies.Count);
        Assert.Equal("LISTEN", ReadReplyType(commandReplies[0]));
        Assert.Equal("EOS", ReadReplyType(commandReplies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(commandReplies[2]));

        using var listenPayload = JsonDocument.Parse(commandReplies[0].Text!);
        Assert.Equal("what's your cloud version",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("cloud_version",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("cloud_version", session.LastIntent);
        Assert.False(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal(0, session.TurnState.BufferedAudioBytes);
    }

    [Fact]
    public async Task BufferedHotphraseOggAudio_RobotVersionSelfAudioEarlyProbeStaysOpenBeforeHardTimeout()
    {
        var stateStore = new InMemoryCloudStateStore();
        var service = CreateService(stateStore, sttStrategies:
        [
            new QueuedBufferedAudioSttStrategy("cloud version 1 dot 0 dot 20")
        ]);

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-version-self-audio-early-probe-token",
            Text =
                """{"type":"LISTEN","transID":"trans-hotphrase-ogg-version-self-audio-early-probe","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-version-self-audio-early-probe-token",
            Binary = BuildOggFrame(0x02, "OpusHead")
        });
        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-version-self-audio-early-probe-token",
            Binary = BuildOggFrame(0x00, "OpusTags")
        });

        for (var index = 0; index < 8; index += 1)
        {
            var interimReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-hotphrase-ogg-version-self-audio-early-probe-token",
                Binary = BuildOggFrame(0x00)
            });

            Assert.Empty(interimReplies);
        }

        var session = stateStore.FindSessionByToken("hub-hotphrase-ogg-version-self-audio-early-probe-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(5000);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(1300);

        var replies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-version-self-audio-early-probe-token",
            Binary = BuildOggFrame(0x00)
        });

        Assert.Empty(replies);
        Assert.True(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal(1, session.TurnState.FinalizeAttemptCount);
        Assert.Null(session.LastIntent);
        Assert.Null(session.LastTranscript);
    }

    [Fact]
    public async Task BufferedHotphraseOggAudio_IncompleteClockEarlyProbeStaysOpenBeforeHardTimeout()
    {
        var stateStore = new InMemoryCloudStateStore();
        var service = CreateService(stateStore, sttStrategies:
        [
            new QueuedBufferedAudioSttStrategy("first 1.0.20 Hey Geebo, what time is")
        ]);

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-incomplete-clock-early-probe-token",
            Text =
                """{"type":"LISTEN","transID":"trans-hotphrase-ogg-incomplete-clock-early-probe","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-incomplete-clock-early-probe-token",
            Binary = BuildOggFrame(0x02, "OpusHead")
        });
        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-incomplete-clock-early-probe-token",
            Binary = BuildOggFrame(0x00, "OpusTags")
        });

        for (var index = 0; index < 8; index += 1)
        {
            var interimReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-hotphrase-ogg-incomplete-clock-early-probe-token",
                Binary = BuildOggFrame(0x00)
            });

            Assert.Empty(interimReplies);
        }

        var session = stateStore.FindSessionByToken("hub-hotphrase-ogg-incomplete-clock-early-probe-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(5000);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(1300);

        var replies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-incomplete-clock-early-probe-token",
            Binary = BuildOggFrame(0x00)
        });

        Assert.Empty(replies);
        Assert.True(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal(1, session.TurnState.FinalizeAttemptCount);
        Assert.Null(session.LastIntent);
        Assert.Null(session.LastTranscript);
    }

    [Fact]
    public async Task BufferedHotphraseOggAudio_IncompleteCloudVersionQuestionEarlyProbeRetriesNextAudio()
    {
        var stateStore = new InMemoryCloudStateStore();
        var service = CreateService(stateStore, sttStrategies:
        [
            new QueuedBufferedAudioSttStrategy("Jubo, what's your", "Jubo, what's your cloud version?")
        ]);

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-incomplete-cloud-version-token",
            Text =
                """{"type":"LISTEN","transID":"trans-hotphrase-ogg-incomplete-cloud-version","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-incomplete-cloud-version-token",
            Binary = BuildOggFrame(0x02, "OpusHead")
        });
        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-incomplete-cloud-version-token",
            Binary = BuildOggFrame(0x00, "OpusTags")
        });

        for (var index = 0; index < 8; index += 1)
        {
            var interimReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-hotphrase-ogg-incomplete-cloud-version-token",
                Binary = BuildOggFrame(0x00)
            });

            Assert.Empty(interimReplies);
        }

        var session = stateStore.FindSessionByToken("hub-hotphrase-ogg-incomplete-cloud-version-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(5000);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(1300);

        var partialReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-incomplete-cloud-version-token",
            Binary = BuildOggFrame(0x00)
        });

        Assert.Empty(partialReplies);
        Assert.True(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal(1, session.TurnState.FinalizeAttemptCount);
        Assert.Null(session.LastIntent);
        Assert.Null(session.LastTranscript);

        var commandReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-ogg-incomplete-cloud-version-token",
            Binary = BuildOggFrame(0x00)
        });

        Assert.Equal(3, commandReplies.Count);
        Assert.Equal("LISTEN", ReadReplyType(commandReplies[0]));
        Assert.Equal("EOS", ReadReplyType(commandReplies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(commandReplies[2]));

        using var listenPayload = JsonDocument.Parse(commandReplies[0].Text!);
        Assert.Equal("what's your cloud version",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("cloud_version",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("cloud_version", session.LastIntent);
        Assert.False(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal(0, session.TurnState.BufferedAudioBytes);
    }

    [Fact]
    public async Task BufferedHotphraseAudio_HotphraseOnlySttDefersUntilCommandAudioArrives()
    {
        var stateStore = new InMemoryCloudStateStore();
        var service = CreateService(stateStore, sttStrategies:
        [
            new QueuedBufferedAudioSttStrategy("", "Hey, Gebo.", "what is your cloud version")
        ]);

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-only-audio-token",
            Text =
                """{"type":"LISTEN","transID":"trans-hotphrase-only-audio","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-only-audio-token",
            Text = """{"type":"CONTEXT","transID":"trans-hotphrase-only-audio","data":{"topic":"conversation"}}"""
        });

        for (var index = 0; index < 4; index += 1)
        {
            var interimReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-hotphrase-only-audio-token",
                Binary = new byte[3000]
            });

            Assert.Empty(interimReplies);
        }

        var session = stateStore.FindSessionByToken("hub-hotphrase-only-audio-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(3);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(600);

        var blankReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-only-audio-token",
            Binary = new byte[3000]
        });

        Assert.Empty(blankReplies);
        Assert.True(session.TurnState.AwaitingTurnCompletion);
        Assert.Null(session.LastIntent);
        Assert.Equal(1, session.TurnState.FinalizeAttemptCount);

        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(600);

        var hotphraseReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-only-audio-token",
            Binary = new byte[3000]
        });

        Assert.Empty(hotphraseReplies);
        Assert.True(session.TurnState.AwaitingTurnCompletion);
        Assert.Null(session.LastIntent);
        Assert.Equal(2, session.TurnState.FinalizeAttemptCount);

        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(600);

        var commandReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-only-audio-token",
            Binary = new byte[3000]
        });

        Assert.Equal(3, commandReplies.Count);
        Assert.Equal("LISTEN", ReadReplyType(commandReplies[0]));
        Assert.Equal("EOS", ReadReplyType(commandReplies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(commandReplies[2]));

        using var listenPayload = JsonDocument.Parse(commandReplies[0].Text!);
        Assert.Equal("cloud_version",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("cloud_version", session.LastIntent);
        Assert.False(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal(0, session.TurnState.BufferedAudioBytes);
    }

    [Fact]
    public async Task BufferedHotphraseAudio_StripsLeadingWakePhraseVariantFromCommandTranscript()
    {
        var stateStore = new InMemoryCloudStateStore();
        var service = CreateService(stateStore, sttStrategies:
        [
            new QueuedBufferedAudioSttStrategy("Jupo. What's your cloud version?")
        ]);

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-strip-command-token",
            Text =
                """{"type":"LISTEN","transID":"trans-hotphrase-strip-command","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        for (var index = 0; index < 4; index += 1)
        {
            var interimReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-hotphrase-strip-command-token",
                Binary = new byte[3000]
            });

            Assert.Empty(interimReplies);
        }

        var session = stateStore.FindSessionByToken("hub-hotphrase-strip-command-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(600);

        var replies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-strip-command-token",
            Binary = new byte[3000]
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("what's your cloud version",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("cloud_version",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("what's your cloud version", session.LastTranscript);
    }

    [Fact]
    public async Task BufferedHotphraseOggAudio_ContinuousStreamEarlyProbesBeforeHardTimeout()
    {
        var stateStore = new InMemoryCloudStateStore();
        var service = CreateService(stateStore, sttStrategies:
        [
            new QueuedBufferedAudioSttStrategy("Hey Jibo, what's your cloud version?")
        ]);

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-continuous-probe-token",
            Text =
                """{"type":"LISTEN","transID":"trans-hotphrase-continuous-probe","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        foreach (var frame in new[]
                 {
                     BuildOggFrame(0x02, "OpusHead"),
                     BuildOggFrame(0x00, "OpusTags"),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00),
                     BuildOggFrame(0x00)
                 })
        {
            var interimReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-hotphrase-continuous-probe-token",
                Binary = frame
            });

            Assert.Empty(interimReplies);
        }

        var session = stateStore.FindSessionByToken("hub-hotphrase-continuous-probe-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(3100);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow;

        var replies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-continuous-probe-token",
            Binary = BuildOggFrame(0x04)
        });

        Assert.Equal(3, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(replies[2]));
        Assert.Equal("cloud_version", session.LastIntent);
        Assert.False(session.TurnState.AwaitingTurnCompletion);
    }

    [Theory]
    [InlineData("Thank you.")]
    [InlineData("to my sources, maybe ask me again in a little while.")]
    [InlineData("I don't know where it's, did you check under the bed?")]
    [InlineData("if I was just about to make a pizza.")]
    public async Task BufferedHotphraseAudio_NonCommandRobotAudioClosesAsNoInput(string transcript)
    {
        var stateStore = new InMemoryCloudStateStore();
        var service = CreateService(stateStore, sttStrategies:
        [
            new QueuedBufferedAudioSttStrategy(transcript)
        ]);

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-non-command-audio-token",
            Text =
                """{"type":"LISTEN","transID":"trans-hotphrase-non-command-audio","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        for (var index = 0; index < 4; index += 1)
        {
            var interimReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-hotphrase-non-command-audio-token",
                Binary = new byte[3000]
            });

            Assert.Empty(interimReplies);
        }

        var session = stateStore.FindSessionByToken("hub-hotphrase-non-command-audio-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(600);

        var replies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-non-command-audio-token",
            Binary = new byte[3000]
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.DoesNotContain(replies, reply => string.Equals(ReadReplyType(reply), "SKILL_ACTION",
            StringComparison.Ordinal));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal(string.Empty,
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Null(session.LastIntent);
        Assert.Equal("no-input", session.LastListenType);
        Assert.False(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal(0, session.TurnState.BufferedAudioBytes);
    }

    [Theory]
    [InlineData("Hey, G-Bell.")]
    [InlineData("Hey, Jim.")]
    public async Task BufferedHotphraseAudio_MisheardWakeOnlyDoesNotRouteAsHello(string transcript)
    {
        var stateStore = new InMemoryCloudStateStore();
        var service = CreateService(stateStore, sttStrategies:
        [
            new QueuedBufferedAudioSttStrategy(transcript)
        ]);

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-misheard-wake-only-token",
            Text =
                """{"type":"LISTEN","transID":"trans-hotphrase-misheard-wake-only","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        for (var index = 0; index < 4; index += 1)
        {
            var interimReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-hotphrase-misheard-wake-only-token",
                Binary = new byte[3000]
            });

            Assert.Empty(interimReplies);
        }

        var session = stateStore.FindSessionByToken("hub-hotphrase-misheard-wake-only-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(4);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(600);

        var replies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-misheard-wake-only-token",
            Binary = new byte[3000]
        });

        Assert.Empty(replies);
        Assert.True(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal(1, session.TurnState.FinalizeAttemptCount);
        Assert.Null(session.LastIntent);
        Assert.Null(session.LastTranscript);
    }

    [Fact]
    public async Task BufferedHotphraseAudio_EmbeddedGBongStopRoutesAsStop()
    {
        var stateStore = new InMemoryCloudStateStore();
        var service = CreateService(stateStore, sttStrategies:
        [
            new QueuedBufferedAudioSttStrategy(
                "it's the definition again long out order is complete hey g bong stop")
        ]);

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-gbong-stop-token",
            Text =
                """{"type":"LISTEN","transID":"trans-hotphrase-gbong-stop","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        for (var index = 0; index < 4; index += 1)
        {
            var interimReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-hotphrase-gbong-stop-token",
                Binary = new byte[3000]
            });

            Assert.Empty(interimReplies);
        }

        var session = stateStore.FindSessionByToken("hub-hotphrase-gbong-stop-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(600);

        var replies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-gbong-stop-token",
            Binary = new byte[3000]
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("stop",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("stop", session.LastIntent);
        Assert.False(session.TurnState.AwaitingTurnCompletion);
    }

    [Fact]
    public async Task BufferedHotphraseAudio_StripsPriorRobotSpeechBeforeEmbeddedWakePhraseCommand()
    {
        var stateStore = new InMemoryCloudStateStore();
        var service = CreateService(stateStore, sttStrategies:
        [
            new QueuedBufferedAudioSttStrategy("first 1.0.20 Hey Geebo, what time is it?")
        ]);

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-self-audio-command-token",
            Text =
                """{"type":"LISTEN","transID":"trans-hotphrase-self-audio-command","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        for (var index = 0; index < 4; index += 1)
        {
            var interimReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-hotphrase-self-audio-command-token",
                Binary = new byte[3000]
            });

            Assert.Empty(interimReplies);
        }

        var session = stateStore.FindSessionByToken("hub-hotphrase-self-audio-command-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(600);

        var replies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-self-audio-command-token",
            Binary = new byte[3000]
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.DoesNotContain(replies, reply =>
            string.Equals(ReadReplyType(reply), "SKILL_REDIRECT", StringComparison.Ordinal));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("what time is it",
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal("askForTime",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("@be/clock",
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("skill").GetString());
        Assert.Equal("what time is it", session.LastTranscript);
    }

    [Fact]
    public async Task BufferedHotphraseAudio_HotphraseOnlySttEventuallyClosesAsNoInput()
    {
        var stateStore = new InMemoryCloudStateStore();
        var service = CreateService(stateStore, sttStrategies:
        [
            new QueuedBufferedAudioSttStrategy("Hey, Gebo.")
        ]);

        await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-only-noinput-token",
            Text =
                """{"type":"LISTEN","transID":"trans-hotphrase-only-noinput","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        for (var index = 0; index < 4; index += 1)
        {
            var interimReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-hotphrase-only-noinput-token",
                Binary = new byte[3000]
            });

            Assert.Empty(interimReplies);
        }

        var session = stateStore.FindSessionByToken("hub-hotphrase-only-noinput-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(600);
        session.TurnState.FinalizeAttemptCount = 3;

        var replies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-only-noinput-token",
            Binary = new byte[3000]
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));
        Assert.DoesNotContain(replies, reply => string.Equals(ReadReplyType(reply), "SKILL_ACTION",
            StringComparison.Ordinal));
        Assert.Null(session.LastIntent);
        Assert.False(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal(0, session.TurnState.BufferedAudioBytes);
    }

    [Fact]
    public async Task BufferedHotphraseAudio_RepeatedMissingTranscriptClosesAsNoInputWithoutNimbusFallback()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-no-input-token",
            Text =
                """{"type":"LISTEN","transID":"trans-hotphrase-no-input","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-no-input-token",
            Text = """{"type":"CONTEXT","transID":"trans-hotphrase-no-input","data":{"topic":"conversation"}}"""
        });

        for (var index = 0; index < 4; index += 1)
        {
            var interimReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = "hub-hotphrase-no-input-token",
                Binary = new byte[3000]
            });

            Assert.Empty(interimReplies);
        }

        var session = _store.FindSessionByToken("hub-hotphrase-no-input-token");
        Assert.NotNull(session);
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(5);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(600);
        session.TurnState.FinalizeAttemptCount = 1;

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-hotphrase-no-input-token",
            Binary = new byte[3000]
        });

        Assert.Equal(2, replies.Count);
        Assert.Equal("LISTEN", ReadReplyType(replies[0]));
        Assert.Equal("EOS", ReadReplyType(replies[1]));

        using var listenPayload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal(string.Empty,
            listenPayload.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal(string.Empty,
            listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.False(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal(0, session.TurnState.BufferedAudioBytes);
        Assert.Equal(0, session.TurnState.BufferedAudioChunkCount);
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
        Assert.Equal("@be/joke",
            skillPayload.RootElement.GetProperty("data").GetProperty("skill").GetProperty("id").GetString());

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
        Assert.Equal("chitchat-skill",
            skillPayload.RootElement.GetProperty("data").GetProperty("skill").GetProperty("id").GetString());
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
        Assert.Equal("chitchat-skill",
            skillPayload.RootElement.GetProperty("data").GetProperty("skill").GetProperty("id").GetString());

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
        Assert.StartsWith("RA_JBO_ShowPizzaMaking_AN_", meta.GetProperty("prompt_id").GetString(),
            StringComparison.Ordinal);
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
        Assert.Equal("chitchat-skill",
            skillPayload.RootElement.GetProperty("data").GetProperty("skill").GetProperty("id").GetString());

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
            Assert.Equal("memory_set_birthday",
                setListenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
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
            Assert.Equal("proactive_offer_pizza_fact",
                offerListenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent")
                    .GetString());
            Assert.Equal("surprises-date/offer_date_fact",
                offerListenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules")[0]
                    .GetString());
            Assert.Equal("surprises-date/offer_date_fact",
                offerListenPayload.RootElement.GetProperty("data").GetProperty("match").GetProperty("rule")
                    .GetString());
        }

        using (var offerSkillPayload = JsonDocument.Parse(offerReplies[2].Text!))
        {
            var jcpConfig = offerSkillPayload.RootElement
                .GetProperty("data")
                .GetProperty("action")
                .GetProperty("config")
                .GetProperty("jcp")
                .GetProperty("config");
            Assert.Equal("Q",
                jcpConfig.GetProperty("play").GetProperty("meta").GetProperty("prompt_sub_category").GetString());
            Assert.Equal("question",
                jcpConfig.GetProperty("play").GetProperty("meta").GetProperty("mim_type").GetString());
            Assert.Equal("LISTEN", jcpConfig.GetProperty("listen").GetProperty("type").GetString());
            Assert.Equal("surprises-date/offer_date_fact",
                jcpConfig.GetProperty("listen").GetProperty("contexts")[0].GetString());
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
            Assert.Equal("proactive_pizza_fact",
                followUpListenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent")
                    .GetString());
        }

        session = _store.FindSessionByToken(token);
        Assert.NotNull(session);
        Assert.False(session.Metadata.ContainsKey("pendingProactivityOffer"));
    }

    [Fact]
    public async Task ClientAsr_BackToBackSurpriseAnnouncements_CloseEachTurn()
    {
        var store = new InMemoryCloudStateStore();
        var service = CreateService(store, randomizer: new FirstItemRandomizer());
        var token = store.IssueRobotToken("back-to-back-surprise-device");

        foreach (var transId in new[] { "trans-surprise-first", "trans-surprise-second" })
        {
            var replies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = token,
                Text = JsonSerializer.Serialize(new
                {
                    type = "CLIENT_ASR",
                    transID = transId,
                    data = new { text = "surprise me" }
                })
            });

            Assert.Equal(3, replies.Count);
            using var listenPayload = JsonDocument.Parse(replies[0].Text!);
            Assert.Equal("proactive_fun_fact",
                listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());

            var session = store.FindSessionByToken(token);
            Assert.NotNull(session);
            Assert.False(session.FollowUpOpen);
            Assert.Null(session.FollowUpExpiresUtc);
        }
    }

    [Fact]
    public async Task ClientAsr_BackToBackSurpriseHotphraseListens_AcceptNewTransaction()
    {
        var store = new InMemoryCloudStateStore();
        var service = CreateService(store, randomizer: new FirstItemRandomizer());
        var token = store.IssueRobotToken("back-to-back-hotphrase-surprise-device");

        foreach (var transId in new[] { "trans-surprise-hotphrase-first", "trans-surprise-hotphrase-second" })
        {
            var setupReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = token,
                Text = $"{{\"type\":\"LISTEN\",\"transID\":\"{transId}\",\"data\":{{\"hotphrase\":true,\"rules\":[\"launch\",\"globals/global_commands_launch\"]}}}}"
            });
            Assert.Empty(setupReplies);

            var replies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com",
                Path = "/listen",
                Kind = "neo-hub-listen",
                Token = token,
                Text = $"{{\"type\":\"CLIENT_ASR\",\"transID\":\"{transId}\",\"data\":{{\"text\":\"surprise me\"}}}}"
            });

            Assert.Equal(3, replies.Count);
            using var listenPayload = JsonDocument.Parse(replies[0].Text!);
            Assert.Equal("proactive_fun_fact",
                listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
            var session = store.FindSessionByToken(token);
            Assert.NotNull(session);
            Assert.False(session.FollowUpOpen);
            Assert.False(session.TurnState.AwaitingTurnCompletion);
            Assert.False(session.TurnState.SawListen);
        }
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
            Text =
                """{"type":"CLIENT_ASR","transID":"trans-proactive-offer-tail-yes","data":{"text":"yes I want to"}}"""
        });

        Assert.Equal(3, followUpReplies.Count);
        using var followUpListenPayload = JsonDocument.Parse(followUpReplies[0].Text!);
        Assert.Equal("proactive_pizza_fact",
            followUpListenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());

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
            Assert.Equal("proactive_offer_pizza_fact",
                offerListenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent")
                    .GetString());
            Assert.Equal("surprises-date/offer_date_fact",
                offerListenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("rules")[0]
                    .GetString());
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
            Assert.Equal("proactive_offer_declined",
                followUpListenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent")
                    .GetString());
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
            Text =
                """{"type":"CLIENT_ASR","transID":"trans-proactive-offer-no-tail-followup","data":{"text":"no I do not"}}"""
        });

        Assert.Equal(3, followUpReplies.Count);
        using var followUpListenPayload = JsonDocument.Parse(followUpReplies[0].Text!);
        Assert.Equal("proactive_offer_declined",
            followUpListenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());

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
            Text =
                """{"type":"LISTEN","transID":"trans-greeting-trigger","data":{"rules":["launch","globals/global_commands_launch"],"mode":"CLIENT_NLU"}}"""
        });
        Assert.Empty(listenSetupReplies);

        var contextReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text =
                """{"type":"CONTEXT","transID":"trans-greeting-trigger","data":{"runtime":{"perception":{"speaker":"person-1","peoplePresent":[{"id":"person-1"}]},"loop":{"users":[{"id":"person-1","firstName":"jake"}]}}}}"""
        });
        Assert.Empty(contextReplies);

        var triggerReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text =
                """{"type":"TRIGGER","transID":"trans-greeting-trigger","data":{"triggerSource":"PRESENCE","triggerData":{"looperID":"person-1"}}}"""
        });

        Assert.Equal(3, triggerReplies.Count);
        Assert.Equal("LISTEN", ReadReplyType(triggerReplies[0]));
        Assert.Equal("EOS", ReadReplyType(triggerReplies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(triggerReplies[2]));

        using (var listenPayload = JsonDocument.Parse(triggerReplies[0].Text!))
        {
            Assert.Equal("proactive_greeting",
                listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-greeting-trigger-ignore","data":{"rules":["launch"],"mode":"CLIENT_NLU"}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text =
                """{"type":"CONTEXT","transID":"trans-greeting-trigger-ignore","data":{"runtime":{"perception":{"speaker":"person-1"},"loop":{"users":[{"id":"person-1","firstName":"jake"}]}}}}"""
        });

        var triggerReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text =
                """{"type":"TRIGGER","transID":"trans-greeting-trigger-ignore","data":{"triggerSource":"SURPRISE","triggerData":{"looperID":"person-1"}}}"""
        });

        Assert.Equal(3, triggerReplies.Count);
        Assert.Equal("LISTEN", ReadReplyType(triggerReplies[0]));
        Assert.Equal("EOS", ReadReplyType(triggerReplies[1]));
        Assert.Equal("SKILL_ACTION", ReadReplyType(triggerReplies[2]));

        using (var listenPayload = JsonDocument.Parse(triggerReplies[0].Text!))
        {
            Assert.Equal("trigger_ignored",
                listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        }

        var session = _store.FindSessionByToken(token);
        Assert.NotNull(session);
        Assert.False(session.FollowUpOpen);
        Assert.False(session.Metadata.ContainsKey("greetingsLastProactiveUtc"));
    }

    [Fact]
    public async Task ProactiveTrigger_WaitsForContext_AndEmitsFinalNoActionWithoutListenFrames()
    {
        var token = _store.IssueHubToken("proactive-contract-device-a");
        const string connectionId = "proactive-contract-connection-a";

        var triggerReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            ConnectionId = connectionId,
            HostName = "neo-hub.jibo.com",
            Path = "/v1/proactive",
            Kind = "neo-hub-proactive",
            Token = token,
            Text =
                """{"type":"TRIGGER","transID":"trans-proactive-contract-a","data":{"triggerSource":"SURPRISE","triggerData":{"looperID":"person-1"}}}"""
        });

        Assert.Empty(triggerReplies);

        var contextReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            ConnectionId = connectionId,
            HostName = "neo-hub.jibo.com",
            Path = "/v1/proactive",
            Kind = "neo-hub-proactive",
            Token = token,
            Text =
                """{"type":"CONTEXT","transID":"trans-proactive-contract-a","data":{"runtime":{"perception":{"speaker":"person-1"},"loop":{"users":[{"id":"person-1","firstName":"jake"}]}}}}"""
        });

        var reply = Assert.Single(contextReplies);
        Assert.Equal("PROACTIVE", ReadReplyType(reply));
        Assert.DoesNotContain(contextReplies, candidate => ReadReplyType(candidate) is "LISTEN" or "EOS");
        using var payload = JsonDocument.Parse(reply.Text!);
        Assert.True(payload.RootElement.GetProperty("final").GetBoolean());
        Assert.Empty(payload.RootElement.GetProperty("data").EnumerateObject());
    }

    [Fact]
    public async Task ProactiveContext_ReinheritsDurableObservedIdentityBinding()
    {
        _store.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "proactive-observed-runtime", RobotId = "robot-proactive-observed-runtime",
            FriendlyName = "OpenJibo Registered Robot"
        });
        var target = _store.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "proactive-canonical", RobotId = "robot-proactive-canonical",
            FriendlyName = "Proactive Canonical"
        });
        var seed = _store.OpenSession("neo-hub-listen", "proactive-observed-runtime",
            "seed-proactive-binding", "neo-hub.jibo.com", "/v1/listen");
        Assert.True(_store.BindSessionToDevice(seed.SessionId, target.DeviceId));

        const string connectionId = "proactive-identity-reconnect";
        const string token = "proactive-identity-reconnect-token";
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            ConnectionId = connectionId,
            HostName = "neo-hub.jibo.com",
            Path = "/v1/proactive",
            Kind = "neo-hub-proactive",
            Token = token,
            Text = """{"type":"TRIGGER","transID":"trans-proactive-identity","data":{"triggerSource":"SURPRISE"}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            ConnectionId = connectionId,
            HostName = "neo-hub.jibo.com",
            Path = "/v1/proactive",
            Kind = "neo-hub-proactive",
            Token = token,
            Text = """{"type":"CONTEXT","transID":"trans-proactive-identity","data":{"runtime":{"loop":{"jibo":{"id":"proactive-observed-runtime"},"users":[]}}}}"""
        });

        var reconnect = Assert.IsType<CloudSession>(_store.FindActiveSessionByToken(token));
        Assert.Equal("proactive-observed-runtime", reconnect.DeviceId);
        Assert.Equal(target.DeviceId, reconnect.Metadata["registeredDeviceId"]?.ToString());
    }

    [Fact]
    public async Task ProactivePresence_EmitsMatchThenSkillActionWithoutListenFrames()
    {
        var token = _store.IssueHubToken("proactive-contract-device-b");
        const string connectionId = "proactive-contract-connection-b";

        var triggerReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            ConnectionId = connectionId,
            HostName = "neo-hub.jibo.com",
            Path = "/v1/proactive",
            Kind = "neo-hub-proactive",
            Token = token,
            Text =
                """{"type":"TRIGGER","transID":"trans-proactive-contract-b","data":{"triggerSource":"PRESENCE","triggerData":{"looperID":"person-1"}}}"""
        });
        Assert.Empty(triggerReplies);

        var contextReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            ConnectionId = connectionId,
            HostName = "neo-hub.jibo.com",
            Path = "/v1/proactive",
            Kind = "neo-hub-proactive",
            Token = token,
            Text =
                """{"type":"CONTEXT","transID":"trans-proactive-contract-b","data":{"runtime":{"perception":{"speaker":"person-1","peoplePresent":[{"id":"person-1"}]},"loop":{"users":[{"id":"person-1","firstName":"jake"}]}}}}"""
        });

        Assert.NotEmpty(contextReplies);
        Assert.Equal("PROACTIVE", ReadReplyType(contextReplies[0]));
        Assert.DoesNotContain(contextReplies, candidate => ReadReplyType(candidate) is "LISTEN" or "EOS");
        Assert.Contains(contextReplies, candidate => ReadReplyType(candidate) == "SKILL_ACTION");
        using var payload = JsonDocument.Parse(contextReplies[0].Text!);
        Assert.False(payload.RootElement.GetProperty("final").GetBoolean());
        Assert.True(payload.RootElement.GetProperty("data").TryGetProperty("match", out var match));
        Assert.False(match.GetProperty("skipSurprises").GetBoolean());
    }

    [Fact]
    public async Task WhoAmI_DoesNotLeaveFollowUpOpen()
    {
        var token = _store.IssueRobotToken("identity-close-token");

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text = """{"type":"LISTEN","transID":"trans-identity-close","data":{"rules":["launch"]}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text =
                """{"type":"CONTEXT","transID":"trans-identity-close","data":{"runtime":{"perception":{"speaker":"person-1"},"loop":{"users":[{"id":"person-1","firstName":"erin"}]}}}}"""
        });

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text = """{"type":"CLIENT_ASR","transID":"trans-identity-close","data":{"text":"who am i"}}"""
        });

        Assert.Equal(3, replies.Count);
        using (var listenPayload = JsonDocument.Parse(replies[0].Text!))
        {
            Assert.Equal("memory_get_name",
                listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        }

        var session = _store.FindSessionByToken(token);
        Assert.NotNull(session);
        Assert.False(session.FollowUpOpen);
        Assert.False(session.TurnState.AwaitingTurnCompletion);
    }

    [Fact]
    public async Task ClientAsrPersonalReport_OptInYesWithSharedYesNoListen_ContinuesStateMachine()
    {
        const string stateKey = "personalReportState";
        var token = _store.IssueRobotToken("personal-report-yesno-device");

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text = """{"type":"CLIENT_ASR","transID":"trans-pr-yesno-start","data":{"text":"personal report"}}"""
        });

        var session = _store.FindSessionByToken(token);
        Assert.NotNull(session);
        Assert.Equal("awaiting_opt_in", session.Metadata[stateKey]?.ToString());

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text =
                """{"type":"LISTEN","transID":"trans-pr-yesno-listen","data":{"rules":["shared/yes_no","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"],"asr":{"hints":["$YESNO"]}}}"""
        });

        session = _store.FindSessionByToken(token);
        Assert.NotNull(session);
        Assert.Equal("awaiting_opt_in", session.Metadata[stateKey]?.ToString());

        var optInReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text = """{"type":"CLIENT_ASR","transID":"trans-pr-yesno-listen","data":{"text":"yes"}}"""
        });

        Assert.Equal(3, optInReplies.Count);
        using (var optInListenPayload = JsonDocument.Parse(optInReplies[0].Text!))
        {
            Assert.Equal("personal_report_request_name",
                optInListenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent")
                    .GetString());
        }

        using (var optInSkillPayload = JsonDocument.Parse(optInReplies[2].Text!))
        {
            var play = optInSkillPayload.RootElement
                .GetProperty("data")
                .GetProperty("action")
                .GetProperty("config")
                .GetProperty("jcp")
                .GetProperty("config")
                .GetProperty("play");
            var esml = play.GetProperty("esml").GetString();
            Assert.DoesNotContain("Yes.", esml, StringComparison.Ordinal);
            Assert.Contains("Who is this?", esml, StringComparison.OrdinalIgnoreCase);
        }

        session = _store.FindSessionByToken(token);
        Assert.NotNull(session);
        Assert.Equal("awaiting_identity_name", session.Metadata[stateKey]?.ToString());
    }

    [Fact]
    public async Task ClientNluPersonalReport_OptInYesWithSharedYesNoListen_EmitsFollowUpSkillAction()
    {
        const string stateKey = "personalReportState";
        var token = _store.IssueRobotToken("personal-report-yesno-nlu-device");

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text = """{"type":"CLIENT_ASR","transID":"trans-pr-nlu-start","data":{"text":"personal report"}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text =
                """{"type":"LISTEN","transID":"trans-pr-nlu-listen","data":{"rules":["shared/yes_no","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"],"asr":{"hints":["$YESNO"]}}}"""
        });

        var optInReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text =
                """{"type":"CLIENT_NLU","transID":"trans-pr-nlu-listen","data":{"intent":"yes","rules":["shared/yes_no"],"text":"yes"}}"""
        });

        Assert.True(optInReplies.Count >= 2, $"expected listen+eos(+skill), got {optInReplies.Count}");
        using (var optInListenPayload = JsonDocument.Parse(optInReplies[0].Text!))
        {
            Assert.Equal("personal_report_request_name",
                optInListenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent")
                    .GetString());
        }

        Assert.Equal(3, optInReplies.Count);
        using (var optInSkillPayload = JsonDocument.Parse(optInReplies[2].Text!))
        {
            Assert.Equal("SKILL_ACTION", optInSkillPayload.RootElement.GetProperty("type").GetString());
            var esml = optInSkillPayload.RootElement
                .GetProperty("data")
                .GetProperty("action")
                .GetProperty("config")
                .GetProperty("jcp")
                .GetProperty("config")
                .GetProperty("play")
                .GetProperty("esml")
                .GetString();
            Assert.Contains("Who is this?", esml, StringComparison.OrdinalIgnoreCase);
        }

        var session = _store.FindSessionByToken(token);
        Assert.NotNull(session);
        Assert.Equal("awaiting_identity_name", session.Metadata[stateKey]?.ToString());
    }

    [Fact]
    public async Task PathToken_PersonalReportOptInThenYesOnNewConnection_ContinuesStateMachine()
    {
        // Live robots on path-token mode open a fresh websocket for the constrained yes/no listen.
        // Dialog metadata must carry across connection-scoped sessions for the same robotID.
        const string stateKey = "personalReportState";
        const string robotId = "Ghost-Instance-Kitchen";

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            ConnectionId = "connection-personal-report-optin",
            HostName = "192.168.7.142",
            Path = "/v1/listen",
            Kind = "neo-hub-listen",
            Token = "v1/listen",
            Text =
                """{"type":"CLIENT_ASR","transID":"trans-pr-path-start","data":{"text":"give me my personal report"}}"""
        });

        var optInSession = _store.FindSessionByToken("conn:connection-personal-report-optin");
        Assert.NotNull(optInSession);
        Assert.Equal("awaiting_opt_in", optInSession.Metadata[stateKey]?.ToString());

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            ConnectionId = "connection-personal-report-optin",
            HostName = "192.168.7.142",
            Path = "/v1/listen",
            Kind = "neo-hub-listen",
            Token = "v1/listen",
            Text =
                "{\"type\":\"CONTEXT\",\"transID\":\"trans-pr-path-start\",\"data\":{\"general\":{\"accountID\":\"acct-1\",\"robotID\":\"" +
                robotId + "\"}}}"
        });
        Assert.Equal(robotId, optInSession.DeviceId);

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            ConnectionId = "connection-personal-report-yesno",
            HostName = "192.168.7.142",
            Path = "/v1/listen",
            Kind = "neo-hub-listen",
            Token = "v1/listen",
            Text =
                "{\"type\":\"CONTEXT\",\"transID\":\"trans-pr-path-yes\",\"data\":{\"general\":{\"accountID\":\"acct-1\",\"robotID\":\"" +
                robotId + "\"}}}"
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            ConnectionId = "connection-personal-report-yesno",
            HostName = "192.168.7.142",
            Path = "/v1/listen",
            Kind = "neo-hub-listen",
            Token = "v1/listen",
            Text =
                """{"type":"LISTEN","transID":"trans-pr-path-yes","data":{"rules":["shared/yes_no","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"],"asr":{"hints":["$YESNO"]}}}"""
        });

        var yesSession = _store.FindSessionByToken("conn:connection-personal-report-yesno");
        Assert.NotNull(yesSession);
        Assert.NotSame(optInSession, yesSession);
        Assert.Equal(robotId, yesSession.DeviceId);
        Assert.Equal("awaiting_opt_in", yesSession.Metadata[stateKey]?.ToString());

        var yesReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            ConnectionId = "connection-personal-report-yesno",
            HostName = "192.168.7.142",
            Path = "/v1/listen",
            Kind = "neo-hub-listen",
            Token = "v1/listen",
            Text = """{"type":"CLIENT_ASR","transID":"trans-pr-path-yes","data":{"text":"Yes."}}"""
        });

        Assert.Equal(3, yesReplies.Count);
        using (var yesListenPayload = JsonDocument.Parse(yesReplies[0].Text!))
        {
            Assert.Equal("personal_report_request_name",
                yesListenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent")
                    .GetString());
        }

        using (var yesSkillPayload = JsonDocument.Parse(yesReplies[2].Text!))
        {
            var esml = yesSkillPayload.RootElement
                .GetProperty("data")
                .GetProperty("action")
                .GetProperty("config")
                .GetProperty("jcp")
                .GetProperty("config")
                .GetProperty("play")
                .GetProperty("esml")
                .GetString();
            Assert.Contains("Who is this?", esml, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(">Yes.<", esml, StringComparison.Ordinal);
        }

        yesSession = _store.FindSessionByToken("conn:connection-personal-report-yesno");
        Assert.NotNull(yesSession);
        Assert.Equal("awaiting_identity_name", yesSession.Metadata[stateKey]?.ToString());
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
            Assert.Equal("personal_report_opt_in",
                startListenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent")
                    .GetString());
        }

        using (var startSkillPayload = JsonDocument.Parse(startReplies[2].Text!))
        {
            var jcpConfig = startSkillPayload.RootElement
                .GetProperty("data")
                .GetProperty("action")
                .GetProperty("config")
                .GetProperty("jcp")
                .GetProperty("config");
            Assert.Equal("Q",
                jcpConfig.GetProperty("play").GetProperty("meta").GetProperty("prompt_sub_category").GetString());
            Assert.Equal("question",
                jcpConfig.GetProperty("play").GetProperty("meta").GetProperty("mim_type").GetString());
            Assert.Equal("LISTEN", jcpConfig.GetProperty("listen").GetProperty("type").GetString());
            Assert.Equal("shared/yes_no",
                jcpConfig.GetProperty("listen").GetProperty("contexts")[0].GetString());
        }

        var session = _store.FindSessionByToken(token);
        Assert.NotNull(session);
        Assert.True(session.Metadata.TryGetValue(stateKey, out var stateValue));
        Assert.Equal("awaiting_opt_in", stateValue?.ToString());
        Assert.True(session.FollowUpOpen);

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
            Assert.Equal("personal_report_request_name",
                optInListenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent")
                    .GetString());
        }

        session = _store.FindSessionByToken(token);
        Assert.NotNull(session);
        Assert.True(session.Metadata.TryGetValue(stateKey, out stateValue));
        Assert.Equal("awaiting_identity_name", stateValue?.ToString());
        Assert.True(session.FollowUpOpen);

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
            Assert.Equal("personal_report_delivered",
                identifyListenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent")
                    .GetString());
        }

        session = _store.FindSessionByToken(token);
        Assert.NotNull(session);
        Assert.True(session.Metadata.TryGetValue(stateKey, out stateValue));
        Assert.Equal("idle", stateValue?.ToString());
    }

    [Fact]
    public async Task Smoke_ClassicUserJourney_CoversGreetingMemoryPersonalityWeatherAndPersonalReport()
    {
        var customStore = new InMemoryCloudStateStore();
        var service = CreateService(
            customStore,
            new StubWeatherReportProvider(
                new WeatherReportSnapshot("Lone Jack, US", "overcast clouds", 79, 82, 78, "clouds", false)),
            null,
            null,
            new StubNewsBriefingProvider(
                new NewsBriefingSnapshot(
                    [
                        new NewsHeadline("Space missions are preparing for new launches",
                            "Teams are preparing new orbital missions."),
                        new NewsHeadline("AI tools keep pushing into everyday products",
                            "Consumer products continue adopting AI features.")
                    ],
                    "NewsAPI")));

        const string token = "hub-smoke-journey-token";

        var greetingReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text =
                """{"type":"LISTEN","transID":"trans-smoke-greeting","data":{"text":"hello jibo","rules":["wake-word"]}}"""
        });

        Assert.Equal(3, greetingReplies.Count);
        Assert.Equal("hello",
            JsonDocument.Parse(greetingReplies[0].Text!).RootElement.GetProperty("data").GetProperty("nlu")
                .GetProperty("intent").GetString());

        var nameReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text = """{"type":"CLIENT_ASR","transID":"trans-smoke-name","data":{"text":"my name is erin"}}"""
        });

        Assert.Equal(3, nameReplies.Count);
        Assert.Equal("memory_set_name",
            JsonDocument.Parse(nameReplies[0].Text!).RootElement.GetProperty("data").GetProperty("nlu")
                .GetProperty("intent").GetString());

        var session = customStore.FindSessionByToken(token);
        Assert.NotNull(session);

        var memoryReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text = """{"type":"CLIENT_ASR","transID":"trans-smoke-memory","data":{"text":"who am i"}}"""
        });

        Assert.Equal(3, memoryReplies.Count);
        Assert.Equal("memory_get_name",
            JsonDocument.Parse(memoryReplies[0].Text!).RootElement.GetProperty("data").GetProperty("nlu")
                .GetProperty("intent").GetString());

        var personalityReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text =
                """{"type":"CLIENT_ASR","transID":"trans-smoke-color","data":{"text":"what is your favorite color"}}"""
        });

        Assert.Equal(3, personalityReplies.Count);
        Assert.Equal("robot_favorite_color",
            JsonDocument.Parse(personalityReplies[0].Text!).RootElement.GetProperty("data").GetProperty("nlu")
                .GetProperty("intent").GetString());

        var weatherReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text = """{"type":"CLIENT_ASR","transID":"trans-smoke-weather","data":{"text":"how is the weather"}}"""
        });

        Assert.Equal(3, weatherReplies.Count);
        Assert.Equal("weather",
            JsonDocument.Parse(weatherReplies[0].Text!).RootElement.GetProperty("data").GetProperty("nlu")
                .GetProperty("intent").GetString());

        var reportStartReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text = """{"type":"CLIENT_ASR","transID":"trans-smoke-report-start","data":{"text":"personal report"}}"""
        });

        Assert.Equal(3, reportStartReplies.Count);
        Assert.Equal("personal_report_opt_in",
            JsonDocument.Parse(reportStartReplies[0].Text!).RootElement.GetProperty("data").GetProperty("nlu")
                .GetProperty("intent").GetString());

        var reportVerifyReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text = """{"type":"CLIENT_ASR","transID":"trans-smoke-report-verify","data":{"text":"uh huh"}}"""
        });

        Assert.Equal(3, reportVerifyReplies.Count);
        Assert.Equal("personal_report_verify_user",
            JsonDocument.Parse(reportVerifyReplies[0].Text!).RootElement.GetProperty("data").GetProperty("nlu")
                .GetProperty("intent").GetString());

        var reportReplies = await service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = token,
            Text = """{"type":"CLIENT_ASR","transID":"trans-smoke-report-deliver","data":{"text":"yes"}}"""
        });

        Assert.Equal(3, reportReplies.Count);
        Assert.Equal("personal_report_delivered",
            JsonDocument.Parse(reportReplies[0].Text!).RootElement.GetProperty("data").GetProperty("nlu")
                .GetProperty("intent").GetString());

        using var skillPayload = JsonDocument.Parse(reportReplies[2].Text!);
        var jcp = skillPayload.RootElement
            .GetProperty("data")
            .GetProperty("action")
            .GetProperty("config")
            .GetProperty("jcp");

        Assert.Equal("SEQUENCE", jcp.GetProperty("type").GetString());
        var children = jcp.GetProperty("children");
        Assert.True(children.GetArrayLength() >= 4,
            $"Expected sectioned personal-report SEQUENCE, got {children.GetArrayLength()} children.");

        var weatherEsml = children[0].GetProperty("config").GetProperty("play").GetProperty("esml").GetString();
        Assert.NotNull(weatherEsml);
        Assert.Equal("views.weatherHiLo",
            children[0].GetProperty("config").GetProperty("gui").GetProperty("data").GetString());

        var totalLength = 0;
        var sawNews = false;
        var sawCalendar = false;
        var sawOutro = false;
        for (var i = 0; i < children.GetArrayLength(); i += 1)
        {
            var childConfig = children[i].GetProperty("config");
            var childEsml = childConfig.GetProperty("play").GetProperty("esml").GetString();
            Assert.NotNull(childEsml);
            var stripped = StripMarkup(childEsml);
            totalLength += stripped.Length;

            if (i == 0)
            {
                Assert.Contains("weather", stripped, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("news", stripped, StringComparison.OrdinalIgnoreCase);
            }
            else if (childConfig.TryGetProperty("gui", out _))
            {
                Assert.Fail("Only the weather section should attach a paused GUI view.");
            }

            if (childEsml.Contains("news-stinger", StringComparison.OrdinalIgnoreCase) ||
                childEsml.Contains("news-intro", StringComparison.OrdinalIgnoreCase) ||
                stripped.Contains("news", StringComparison.OrdinalIgnoreCase))
            {
                sawNews = true;
                Assert.True(childConfig.TryGetProperty("timeout", out var newsTimeout));
                Assert.Equal(6, newsTimeout.GetInt32());
            }

            if (stripped.Contains("calendar", StringComparison.OrdinalIgnoreCase))
            {
                sawCalendar = true;
                Assert.False(childConfig.TryGetProperty("timeout", out _),
                    "Calendar section should be speech-only without a GUI hold timeout.");
                Assert.False(childConfig.TryGetProperty("gui", out _),
                    "Calendar section should not attach a paused GUI view.");
            }
            if (i == children.GetArrayLength() - 1 &&
                (stripped.Contains("wraps up your report", StringComparison.OrdinalIgnoreCase) ||
                 stripped.Contains("that's your report", StringComparison.OrdinalIgnoreCase) ||
                 stripped.Contains("personal report", StringComparison.OrdinalIgnoreCase)))
                sawOutro = true;

            Assert.DoesNotContain("And that's it.", stripped, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("what's new in the news", stripped, StringComparison.OrdinalIgnoreCase);
        }

        Assert.True(sawNews);
        Assert.True(sawCalendar);
        Assert.True(sawOutro);
        Assert.True(totalLength < 1200, $"Personal report speech was still too long: {totalLength} chars.");
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
            Assert.Equal("emotion_command",
                listenPayload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
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
            Text =
                """{"type":"LISTEN","transID":"trans-second","data":{"text":"what time is it","rules":["follow-up"]}}"""
        });

        Assert.Equal(3, followUpReplies.Count);
        using var payload = JsonDocument.Parse(followUpReplies[0].Text!);
        Assert.NotEqual("askForTime",
            payload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("trans-second", payload.RootElement.GetProperty("transID").GetString());
        Assert.DoesNotContain(followUpReplies, reply =>
            string.Equals(ReadReplyType(reply), "SKILL_REDIRECT", StringComparison.Ordinal));

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

    [Fact]
    public async Task NewTransId_WhileAudioIsBuffered_DoesNotBlowAwayTheActiveTurn()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-buffered-transid-token",
            Text = """{"type":"LISTEN","transID":"trans-first","data":{"rules":["wake-word"]}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-buffered-transid-token",
            Text = """{"type":"CONTEXT","transID":"trans-first","data":{"audioTranscriptHint":"tell me a joke"}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-buffered-transid-token",
            Binary = [1, 2, 3, 4]
        });

        var session = _store.FindSessionByToken("hub-buffered-transid-token");
        Assert.NotNull(session);
        Assert.Equal("trans-first", session.TurnState.TransId);
        Assert.Equal(4, session.TurnState.BufferedAudioBytes);
        Assert.Equal("tell me a joke", session.TurnState.AudioTranscriptHint);

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-buffered-transid-token",
            Text = """{"type":"LISTEN","transID":"trans-second","data":{"rules":["follow-up"]}}"""
        });

        session = _store.FindSessionByToken("hub-buffered-transid-token");
        Assert.NotNull(session);
        Assert.Equal("trans-second", session.TurnState.TransId);
        Assert.Equal(0, session.TurnState.BufferedAudioBytes);
        Assert.Equal(0, session.TurnState.BufferedAudioChunkCount);
        Assert.Null(session.TurnState.AudioTranscriptHint);
        Assert.True(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal("trans-second", session.LastTransId);
    }

    [Fact]
    public async Task GraceWindow_StillIgnoresReplyAudioFromPreviousTurn()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-grace-window-token",
            Text = """{"type":"LISTEN","transID":"trans-first","data":{"rules":["wake-word"]}}"""
        });

        var session = _store.FindSessionByToken("hub-grace-window-token");
        Assert.NotNull(session);
        session.TurnState.AwaitingTurnCompletion = false;
        session.TurnState.SawListen = false;
        session.TurnState.SawContext = false;
        session.TurnState.BufferedAudioBytes = 0;
        session.TurnState.BufferedAudioChunkCount = 0;
        session.TurnState.IgnoreAdditionalAudioUntilUtc = DateTimeOffset.UtcNow.AddSeconds(7);

        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-grace-window-token",
            Binary = new byte[4096]
        });

        Assert.Empty(replies);
        Assert.Equal(0, session.TurnState.BufferedAudioBytes);
        Assert.Equal(0, session.TurnState.BufferedAudioChunkCount);
        Assert.False(session.TurnState.AwaitingTurnCompletion);
    }

    [Theory]
    [InlineData("fixtures\\neo-hub-client-asr-joke.flow.json")]
    [InlineData("fixtures\\neo-hub-context-client-nlu.flow.json")]
    [InlineData("fixtures\\neo-hub-client-nlu-clock-ask-time.flow.json")]
    [InlineData("fixtures\\neo-hub-buffered-audio-synthetic-asr.flow.json")]
    [InlineData("fixtures\\neo-hub-buffered-audio-header-only.flow.json")]
    [InlineData("fixtures\\neo-hub-buffered-audio-mixed-short-burst.flow.json")]
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

                if (expectedReply.DelayMs.HasValue) Assert.Equal(expectedReply.DelayMs.Value, replies[index].DelayMs);

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
                    Assert.True(actual.TryGetProperty(property.Name, out var actualProperty),
                        $"Expected property '{property.Name}' was not found.");
                    AssertJsonContains(property.Value, actualProperty);
                }

                break;
            case JsonValueKind.Array:
            {
                var expectedItems = expected.EnumerateArray().ToArray();
                var actualItems = actual.EnumerateArray().ToArray();
                Assert.Equal(expectedItems.Length, actualItems.Length);
                for (var index = 0; index < expectedItems.Length; index += 1)
                    AssertJsonContains(expectedItems[index], actualItems[index]);
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
        ICalendarReportProvider? calendarReportProvider = null,
        ICommuteReportProvider? commuteReportProvider = null,
        INewsBriefingProvider? newsBriefingProvider = null,
        IReadOnlyList<ISttStrategy>? sttStrategies = null,
        IJiboRandomizer? randomizer = null)
    {
        var contentRepository = new InMemoryJiboExperienceContentRepository();
        var contentCache = new JiboExperienceContentCache(contentRepository);
        var interactionService = new JiboInteractionService(
            contentCache,
            randomizer ?? new DefaultJiboRandomizer(),
            new InMemoryPersonalMemoryStore(),
            weatherReportProvider,
            calendarReportProvider,
            commuteReportProvider,
            newsBriefingProvider);
        var conversationBroker = new DemoConversationBroker(interactionService);
        var sttSelector = new DefaultSttStrategySelector(sttStrategies ?? [new SyntheticBufferedAudioSttStrategy()]);
        var sink = new NullTurnTelemetrySink();

        return new JiboWebSocketService(
            stateStore,
            new NullWebSocketTelemetrySink(),
            new WebSocketTurnFinalizationService(conversationBroker, sttSelector, sink));
    }

    [Fact]
    public async Task SimultaneousHotphrase_BothRobotsRemainIndependentAndListening()
    {
        var tokenA = _store.IssueHubToken("device-kitchen");
        var tokenB = _store.IssueHubToken("device-office");
        Assert.NotEqual(tokenA, tokenB);

        var firstReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = tokenA,
            Text =
                """{"type":"LISTEN","transID":"trans-kitchen","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        Assert.Empty(firstReplies);

        var secondReplies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = tokenB,
            Text =
                """{"type":"LISTEN","transID":"trans-office","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        Assert.Empty(secondReplies);

        var sessionA = _store.FindSessionByToken(tokenA);
        var sessionB = _store.FindSessionByToken(tokenB);
        Assert.NotNull(sessionA);
        Assert.NotNull(sessionB);
        Assert.NotSame(sessionA, sessionB);
        Assert.Equal("device-kitchen", sessionA.DeviceId);
        Assert.Equal("device-office", sessionB.DeviceId);
        Assert.Equal("trans-kitchen", sessionA.TurnState.TransId);
        Assert.Equal("trans-office", sessionB.TurnState.TransId);
        Assert.True(sessionA.TurnState.AwaitingTurnCompletion);
        Assert.True(sessionB.TurnState.AwaitingTurnCompletion);
    }

    [Fact]
    public void PathToken_TwoConnections_GetDistinctSessions()
    {
        var sessionA = _service.GetOrCreateSession(new WebSocketMessageEnvelope
        {
            ConnectionId = "connection-kitchen",
            HostName = "192.168.7.142",
            Path = "/v1/listen",
            Kind = "neo-hub-listen",
            Token = "v1/listen"
        });

        var sessionB = _service.GetOrCreateSession(new WebSocketMessageEnvelope
        {
            ConnectionId = "connection-office",
            HostName = "192.168.7.142",
            Path = "/v1/listen",
            Kind = "neo-hub-listen",
            Token = "v1/listen"
        });

        Assert.NotSame(sessionA, sessionB);
        Assert.Null(_store.FindSessionByToken("v1/listen"));
        Assert.Same(sessionA, _store.FindSessionByToken("conn:connection-kitchen"));
        Assert.Same(sessionB, _store.FindSessionByToken("conn:connection-office"));
    }

    [Fact]
    public async Task PathToken_SimultaneousHotphrase_BothAcceptAudio()
    {
        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            ConnectionId = "connection-kitchen",
            HostName = "192.168.7.142",
            Path = "/v1/listen",
            Kind = "neo-hub-listen",
            Token = "v1/listen",
            Text =
                """{"type":"LISTEN","transID":"trans-kitchen","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            ConnectionId = "connection-office",
            HostName = "192.168.7.142",
            Path = "/v1/listen",
            Kind = "neo-hub-listen",
            Token = "v1/listen",
            Text =
                """{"type":"LISTEN","transID":"trans-office","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            ConnectionId = "connection-kitchen",
            HostName = "192.168.7.142",
            Path = "/v1/listen",
            Kind = "neo-hub-listen",
            Token = "v1/listen",
            Binary = new byte[4096]
        });

        await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            ConnectionId = "connection-office",
            HostName = "192.168.7.142",
            Path = "/v1/listen",
            Kind = "neo-hub-listen",
            Token = "v1/listen",
            Binary = new byte[4096]
        });

        var sessionA = _store.FindSessionByToken("conn:connection-kitchen");
        var sessionB = _store.FindSessionByToken("conn:connection-office");
        Assert.NotNull(sessionA);
        Assert.NotNull(sessionB);
        Assert.NotSame(sessionA, sessionB);
        Assert.Equal("trans-kitchen", sessionA.TurnState.TransId);
        Assert.Equal("trans-office", sessionB.TurnState.TransId);
        Assert.True(sessionA.TurnState.SawListen);
        Assert.True(sessionB.TurnState.SawListen);
        Assert.Equal(4096, sessionA.TurnState.BufferedAudioBytes);
        Assert.Equal(4096, sessionB.TurnState.BufferedAudioBytes);
        Assert.True(sessionA.TurnState.AwaitingTurnCompletion);
        Assert.True(sessionB.TurnState.AwaitingTurnCompletion);
    }

    [Fact]
    public void HubBearerToken_StillSharesByToken()
    {
        var hubToken = _store.IssueHubToken("device-shared");

        var sessionA = _service.GetOrCreateSession(new WebSocketMessageEnvelope
        {
            ConnectionId = "connection-one",
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = hubToken
        });

        var sessionB = _service.GetOrCreateSession(new WebSocketMessageEnvelope
        {
            ConnectionId = "connection-two",
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = hubToken
        });

        Assert.Same(sessionA, sessionB);
        Assert.Same(sessionA, _store.FindSessionByToken(hubToken));
    }

    private static string ReadReplyType(WebSocketReply reply)
    {
        using var payload = JsonDocument.Parse(reply.Text!);
        return payload.RootElement.GetProperty("type").GetString() ?? string.Empty;
    }

    private static string StripMarkup(string text)
    {
        var builder = new StringBuilder(text.Length);
        var inTag = false;

        foreach (var character in text)
        {
            switch (character)
            {
                case '<':
                    inTag = true;
                    continue;
                case '>':
                    inTag = false;
                    continue;
            }

            if (!inTag) builder.Append(character);
        }

        return builder.ToString();
    }

    private static byte[] BuildOggFrame(byte headerType, string marker = "audio")
    {
        var frame = Enumerable.Range(0, 4300).Select(index => (byte)(index % 251)).ToArray();
        "OggS"u8.CopyTo(frame);
        frame[4] = 0;
        frame[5] = headerType;
        Encoding.ASCII.GetBytes(marker).CopyTo(frame, 32);
        return frame;
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

    private sealed class LastItemRandomizer : IJiboRandomizer
    {
        public T Choose<T>(IReadOnlyList<T> items)
        {
            return items[^1];
        }
    }

    private sealed class FirstItemRandomizer : IJiboRandomizer
    {
        public T Choose<T>(IReadOnlyList<T> items)
        {
            return items[0];
        }
    }

    private sealed class QueuedBufferedAudioSttStrategy(params string[] transcripts) : ISttStrategy
    {
        private readonly Queue<string> _transcripts = new(transcripts);

        public string Name => "queued-buffered-audio";

        public bool CanHandle(TurnContext turn)
        {
            return ReadBufferedAudioBytes(turn) > 0 && _transcripts.Count > 0;
        }

        public Task<SttResult> TranscribeAsync(TurnContext turn, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SttResult
            {
                Text = _transcripts.Dequeue(),
                Provider = Name,
                Confidence = 0.75f,
                Locale = turn.Locale
            });
        }

        private static int ReadBufferedAudioBytes(TurnContext turn)
        {
            if (!turn.Attributes.TryGetValue("bufferedAudioBytes", out var bufferedAudioBytes)) return 0;

            return bufferedAudioBytes switch
            {
                int value => value,
                long value => (int)value,
                string value when int.TryParse(value, out var parsed) => parsed,
                _ => 0
            };
        }
    }

    private sealed class BlockingBufferedAudioSttStrategy(string transcript) : ISttStrategy
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _transcriptionCount;

        public string Name => "blocking-buffered-audio";
        public Task Started => _started.Task;
        public int TranscriptionCount => Volatile.Read(ref _transcriptionCount);

        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CanHandle(TurnContext turn)
        {
            return turn.Attributes.TryGetValue("bufferedAudioBytes", out var value) &&
                   value is int bytes &&
                   bytes > 0;
        }

        public async Task<SttResult> TranscribeAsync(
            TurnContext turn,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _transcriptionCount);
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new SttResult
            {
                Text = transcript,
                Provider = Name,
                Confidence = 0.95f,
                Locale = turn.Locale
            };
        }

        public void Release()
        {
            _release.TrySetResult();
        }
    }
}
