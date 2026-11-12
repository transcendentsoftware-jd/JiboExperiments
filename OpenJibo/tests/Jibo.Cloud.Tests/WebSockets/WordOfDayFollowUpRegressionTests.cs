using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Content;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Tests.WebSockets;

// These cloud-side tests do not execute the stock MIM or establish physical completion.
public sealed class WordOfDayFollowUpRegressionTests
{
    private const string Token = "synthetic-wod-follow-up";
    private readonly InMemoryCloudStateStore _store = new();
    private readonly JiboWebSocketService _service;

    public WordOfDayFollowUpRegressionTests()
    {
        var content = new JiboExperienceContentCache(new InMemoryJiboExperienceContentRepository());
        var broker = new DemoConversationBroker(new JiboInteractionService(content, new LastItemRandomizer(),
            new InMemoryPersonalMemoryStore()));
        var selector = new DefaultSttStrategySelector([new SyntheticBufferedAudioSttStrategy()]);
        var turns = new WebSocketTurnFinalizationService(broker, selector, new NullTurnTelemetrySink());
        _service = new JiboWebSocketService(_store, new NullWebSocketTelemetrySink(), turns);
    }

    [Fact]
    public async Task FunFactSilence_UsesLocalNoInputWithoutGlobalMatchOrRedirect()
    {
        Assert.Empty(await SendAsync("""{"type":"LISTEN","transID":"fact-silence","data":{"rules":["word-of-the-day/fun_fact","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"]}}"""));
        var replies = await SendAsync("""{"type":"CLIENT_ASR","transID":"fact-silence","data":{"text":""}}""");

        AssertReplyTypes(replies, "LISTEN", "EOS");
        using var payload = JsonDocument.Parse(replies[0].Text!);
        var data = payload.RootElement.GetProperty("data");
        Assert.Equal("fact-silence", payload.RootElement.GetProperty("transID").GetString());
        Assert.Equal(string.Empty, data.GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal(string.Empty, data.GetProperty("nlu").GetProperty("intent").GetString());
        Assert.False(data.TryGetProperty("match", out _));
        AssertSingleRule(data, "word-of-the-day/fun_fact");
        var session = Assert.IsType<CloudSession>(_store.FindSessionByToken(Token));
        Assert.False(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal("no-input", session.LastListenType);
    }

    [Fact]
    public async Task FunFactUnmatchedSpeech_PreservesTextAndLocalRuleWithoutCloudAction()
    {
        Assert.Empty(await SendAsync("""{"type":"LISTEN","transID":"fact-speech","data":{"rules":["word-of-the-day/fun_fact","globals/gui_nav","globals/mim_repeat","globals/global_commands_launch"]}}"""));
        var replies = await SendAsync("""{"type":"CLIENT_ASR","transID":"fact-speech","data":{"text":"which is a"}}""");

        AssertReplyTypes(replies, "LISTEN", "EOS");
        using var payload = JsonDocument.Parse(replies[0].Text!);
        var data = payload.RootElement.GetProperty("data");
        Assert.Equal("fact-speech", payload.RootElement.GetProperty("transID").GetString());
        Assert.Equal("which is a", data.GetProperty("asr").GetProperty("text").GetString());
        AssertSingleRule(data, "word-of-the-day/fun_fact");
        Assert.Equal(string.Empty, data.GetProperty("nlu").GetProperty("intent").GetString());
        Assert.False(data.TryGetProperty("match", out _));
    }

    [Fact]
    public async Task RightWordCleanup_ThenNewFunFactListen_UsesNewTransactionAndLocalRule()
    {
        var rightWord = await SendAsync("""{"type":"LISTEN","transID":"right-1","data":{"rules":["word-of-the-day/right_word","globals/gui_nav","globals/global_commands_launch"]}}""");
        AssertReplyTypes(rightWord, "LISTEN", "EOS");
        using (var payload = JsonDocument.Parse(rightWord[0].Text!))
        {
            var data = payload.RootElement.GetProperty("data");
            Assert.Equal("right-1", payload.RootElement.GetProperty("transID").GetString());
            AssertSingleRule(data, "word-of-the-day/right_word");
            Assert.False(data.TryGetProperty("match", out _));
        }

        // The stock skill is not run here: the next LISTEN is supplied by this test.
        Assert.Empty(await SendAsync("""{"type":"LISTEN","transID":"fact-2","data":{"rules":["word-of-the-day/fun_fact","globals/gui_nav","globals/global_commands_launch"]}}"""));
        var followUp = await SendAsync("""{"type":"CLIENT_ASR","transID":"fact-2","data":{"text":""}}""");
        AssertReplyTypes(followUp, "LISTEN", "EOS");
        using var fact = JsonDocument.Parse(followUp[0].Text!);
        Assert.Equal("fact-2", fact.RootElement.GetProperty("transID").GetString());
        AssertSingleRule(fact.RootElement.GetProperty("data"), "word-of-the-day/fun_fact");
        Assert.False(fact.RootElement.GetProperty("data").TryGetProperty("match", out _));
    }

    [Fact]
    public async Task BufferedFunFactTranscriptHint_AutoFinalizesAsLocalUnmatchedSpeech()
    {
        Assert.Empty(await SendAsync("""{"type":"LISTEN","transID":"fact-buffered","data":{"rules":["word-of-the-day/fun_fact","globals/gui_nav","globals/global_commands_launch"]}}"""));
        Assert.Empty(await SendAsync("""{"type":"CONTEXT","transID":"fact-buffered","data":{"audioTranscriptHint":"which is a"}}"""));
        for (var i = 0; i < 2; i++)
            Assert.Empty(await _service.HandleMessageAsync(new WebSocketMessageEnvelope
            {
                HostName = "neo-hub.jibo.com", Path = "/listen", Kind = "neo-hub-listen",
                Token = Token, Binary = new byte[3000]
            }));

        var session = Assert.IsType<CloudSession>(_store.FindSessionByToken(Token));
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(3);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2);
        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com", Path = "/listen", Kind = "neo-hub-listen",
            Token = Token, Binary = new byte[3000]
        });

        AssertReplyTypes(replies, "LISTEN", "EOS");
        using var payload = JsonDocument.Parse(replies[0].Text!);
        var data = payload.RootElement.GetProperty("data");
        Assert.Equal("fact-buffered", payload.RootElement.GetProperty("transID").GetString());
        Assert.Equal("which is a", data.GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal(string.Empty, data.GetProperty("nlu").GetProperty("intent").GetString());
        AssertSingleRule(data, "word-of-the-day/fun_fact");
        Assert.False(data.TryGetProperty("match", out _));
        Assert.False(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal(0, session.TurnState.BufferedAudioBytes);
        Assert.Equal(0, session.TurnState.BufferedAudioChunkCount);
    }

    [Fact]
    public async Task FunFactExplicitStop_PreservesGlobalStopRouting()
    {
        Assert.Empty(await SendAsync("""{"type":"LISTEN","transID":"fact-stop","data":{"rules":["word-of-the-day/fun_fact","globals/gui_nav","globals/global_commands_launch"]}}"""));
        var replies = await SendAsync("""{"type":"CLIENT_ASR","transID":"fact-stop","data":{"text":"stop that"}}""");
        AssertReplyTypes(replies, "LISTEN", "EOS");
        using var payload = JsonDocument.Parse(replies[0].Text!);
        var nlu = payload.RootElement.GetProperty("data").GetProperty("nlu");
        Assert.Equal("stop", nlu.GetProperty("intent").GetString());
        Assert.Equal("global_commands", nlu.GetProperty("domain").GetString());
        Assert.Equal("globals/global_commands_launch", nlu.GetProperty("rules")[0].GetString());
    }

    [Fact]
    public async Task FunFactExplicitClientGrammarTag_IsNotReplacedByUnmatchedHandling()
    {
        Assert.Empty(await SendAsync("""{"type":"LISTEN","transID":"fact-nlu","data":{"rules":["word-of-the-day/fun_fact","globals/gui_nav"]}}"""));
        var replies = await SendAsync("""{"type":"CLIENT_NLU","transID":"fact-nlu","data":{"intent":"noResponse","rules":["word-of-the-day/fun_fact"]}}""");
        AssertReplyTypes(replies, "LISTEN", "EOS");
        using var payload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("noResponse", payload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        AssertSingleRule(payload.RootElement.GetProperty("data"), "word-of-the-day/fun_fact");
    }

    [Fact]
    public async Task OtherSkillUnmatchedSpeech_RetainsExistingSkillListenResponse()
    {
        Assert.Empty(await SendAsync("""{"type":"LISTEN","transID":"other-skill","data":{"rules":["exercise/want_to","globals/gui_nav"]}}"""));
        var replies = await SendAsync("""{"type":"CLIENT_ASR","transID":"other-skill","data":{"text":"which is a"}}""");
        AssertReplyTypes(replies, "LISTEN", "EOS");
        using var payload = JsonDocument.Parse(replies[0].Text!);
        var data = payload.RootElement.GetProperty("data");
        Assert.Equal("skill_listen", data.GetProperty("nlu").GetProperty("intent").GetString());
        Assert.Equal("skill_listen", data.GetProperty("match").GetProperty("intent").GetString());
        AssertSingleRule(data, "exercise/want_to");
    }

    [Fact]
    public async Task GlobalOnlyFollowUp_DoesNotInventWordOfDayRule()
    {
        Assert.Empty(await SendAsync("""{"type":"LISTEN","transID":"global-only","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""));
        var replies = await SendAsync("""{"type":"CLIENT_ASR","transID":"global-only","data":{"text":"which is a"}}""");
        Assert.NotEmpty(replies);
        using var payload = JsonDocument.Parse(replies[0].Text!);
        var data = payload.RootElement.GetProperty("data");
        var rules = data.GetProperty("nlu").GetProperty("rules").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.DoesNotContain("word-of-the-day/fun_fact", rules);
    }

    private Task<IReadOnlyList<WebSocketReply>> SendAsync(string text) => _service.HandleMessageAsync(new WebSocketMessageEnvelope
    {
        HostName = "neo-hub.jibo.com", Path = "/listen", Kind = "neo-hub-listen", Token = Token, Text = text
    });

    private static void AssertReplyTypes(IReadOnlyList<WebSocketReply> replies, params string[] expected)
    {
        Assert.Equal(expected.Length, replies.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            using var doc = JsonDocument.Parse(replies[i].Text!);
            Assert.Equal(expected[i], doc.RootElement.GetProperty("type").GetString());
        }
    }

    private static void AssertSingleRule(JsonElement data, string expected)
    {
        var rules = data.GetProperty("nlu").GetProperty("rules").EnumerateArray().Select(x => x.GetString()!).ToArray();
        Assert.Equal([expected], rules);
    }

    private sealed class LastItemRandomizer : IJiboRandomizer
    {
        public T Choose<T>(IReadOnlyList<T> items) => items[^1];
    }
}
