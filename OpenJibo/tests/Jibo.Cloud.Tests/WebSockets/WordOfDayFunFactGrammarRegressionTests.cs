using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Content;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Tests.WebSockets;

// These cloud-side tests do not execute the stock MIM or establish physical completion.
public sealed class WordOfDayFunFactGrammarRegressionTests
{
    private const string Token = "synthetic-wod-follow-up";
    private readonly InMemoryCloudStateStore _store = new();
    private readonly JiboWebSocketService _service;

    public WordOfDayFunFactGrammarRegressionTests()
    {
        var content = new JiboExperienceContentCache(new InMemoryJiboExperienceContentRepository());
        var broker = new DemoConversationBroker(new JiboInteractionService(content, new LastItemRandomizer(),
            new InMemoryPersonalMemoryStore()));
        var selector = new DefaultSttStrategySelector([new SyntheticBufferedAudioSttStrategy()]);
        var turns = new WebSocketTurnFinalizationService(broker, selector, new NullTurnTelemetrySink());
        _service = new JiboWebSocketService(_store, new NullWebSocketTelemetrySink(), turns);
    }

    [Theory]
    [InlineData("no")]
    [InlineData("yes")]
    public async Task FunFactFreeAsrYesNo_DoesNotInventGrammarMatch(string text)
    {
        Assert.Empty(await SendAsync("""{"type":"LISTEN","transID":"fact-grammar","data":{"rules":["globals/gui_nav","word-of-the-day/fun_fact","globals/global_commands_launch"]}}"""));
        var replies = await SendAsync(JsonSerializer.Serialize(new { type = "CLIENT_ASR", transID = "fact-grammar", data = new { text } }));
        AssertReplyTypes(replies, "LISTEN", "EOS");
        using var payload = JsonDocument.Parse(replies[0].Text!);
        var data = payload.RootElement.GetProperty("data");
        Assert.Equal(text, data.GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal(string.Empty, data.GetProperty("nlu").GetProperty("intent").GetString());
        Assert.False(data.TryGetProperty("match", out _));
        AssertSingleRule(data, "word-of-the-day/fun_fact");
    }

    [Theory]
    [InlineData("no")]
    [InlineData("yes")]
    public async Task ExplicitClientGrammarIntent_RemainsIntact(string intent)
    {
        Assert.Empty(await SendAsync("""{"type":"LISTEN","transID":"explicit","data":{"rules":["word-of-the-day/fun_fact","globals/gui_nav"]}}"""));
        var replies = await SendAsync(JsonSerializer.Serialize(new { type = "CLIENT_NLU", transID = "explicit", data = new { intent, rules = new[] { "word-of-the-day/fun_fact" } } }));
        AssertReplyTypes(replies, "LISTEN", "EOS");
        using var payload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal(intent, payload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
    }

    [Theory]
    [InlineData("listenRules", "$YESNO")]
    [InlineData("clientRules", "$YESNO")]
    [InlineData("listenAsrHints", "$YESNO")]
    [InlineData("listenRules", "shared/yes_no")]
    [InlineData("clientRules", "shared/yes_no")]
    [InlineData("listenAsrHints", "shared/yes_no")]
    public async Task ExplicitYesNoRule_PreservesBrokerClassification(string field, string rule)
    {
        var attributes = new Dictionary<string, object?> { ["listenRules"] = new[] { "word-of-the-day/fun_fact" } };
        attributes[field] = field == "listenRules" ? new[] { "word-of-the-day/fun_fact", rule } : new[] { rule };
        var content = new JiboExperienceContentCache(new InMemoryJiboExperienceContentRepository());
        var service = new JiboInteractionService(content, new LastItemRandomizer(), new InMemoryPersonalMemoryStore());
        var decision = await service.BuildDecisionAsync(new TurnContext { RawTranscript = "no", NormalizedTranscript = "no", Attributes = attributes });
        Assert.Equal("no", decision.IntentName);
    }

    [Fact]
    public async Task FunFactAmbiguousYesNo_RemainsLocalUnmatched()
    {
        Assert.Empty(await SendAsync("""{"type":"LISTEN","transID":"ambiguous","data":{"rules":["word-of-the-day/fun_fact","globals/gui_nav"]}}"""));
        var replies = await SendAsync("""{"type":"CLIENT_ASR","transID":"ambiguous","data":{"text":"no yes"}}""");
        AssertReplyTypes(replies, "LISTEN", "EOS");
        using var payload = JsonDocument.Parse(replies[0].Text!);
        var data = payload.RootElement.GetProperty("data");
        Assert.Equal(string.Empty, data.GetProperty("nlu").GetProperty("intent").GetString());
        Assert.False(data.TryGetProperty("match", out _));
    }

    [Fact]
    public async Task BufferedFunFactNo_AutoFinalizesWithoutGrammarMatch()
    {
        Assert.Empty(await SendAsync("""{"type":"LISTEN","transID":"buffered-no","data":{"rules":["word-of-the-day/fun_fact","globals/gui_nav"]}}"""));
        Assert.Empty(await SendAsync("""{"type":"CONTEXT","transID":"buffered-no","data":{"audioTranscriptHint":"no"}}"""));
        for (var i = 0; i < 2; i++)
            Assert.Empty(await _service.HandleMessageAsync(new WebSocketMessageEnvelope { HostName = "neo-hub.jibo.com", Path = "/listen", Kind = "neo-hub-listen", Token = Token, Binary = new byte[3000] }));
        var session = Assert.IsType<CloudSession>(_store.FindSessionByToken(Token));
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(3);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2);
        var replies = await _service.HandleMessageAsync(new WebSocketMessageEnvelope { HostName = "neo-hub.jibo.com", Path = "/listen", Kind = "neo-hub-listen", Token = Token, Binary = new byte[3000] });
        AssertReplyTypes(replies, "LISTEN", "EOS");
        using var payload = JsonDocument.Parse(replies[0].Text!);
        var data = payload.RootElement.GetProperty("data");
        Assert.Equal("no", data.GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal(string.Empty, data.GetProperty("nlu").GetProperty("intent").GetString());
        Assert.False(data.TryGetProperty("match", out _));
        Assert.Equal(0, session.TurnState.BufferedAudioBytes);
    }

    [Theory]
    [InlineData("word-of-the-day/right_word")]
    [InlineData("exercise/want_to")]
    public async Task OtherSkillYesNo_PreservesBrokerClassification(string rule)
    {
        var content = new JiboExperienceContentCache(new InMemoryJiboExperienceContentRepository());
        var service = new JiboInteractionService(content, new LastItemRandomizer(), new InMemoryPersonalMemoryStore());
        var decision = await service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = "no", NormalizedTranscript = "no",
            Attributes = new Dictionary<string, object?> { ["listenRules"] = new[] { rule } }
        });
        Assert.Equal("no", decision.IntentName);
    }

    [Theory]
    [InlineData("$YESNO")]
    [InlineData("shared/yes_no")]
    public async Task ExplicitYesNoListenRule_PreservesWireIntent(string rule)
    {
        Assert.Empty(await SendAsync(JsonSerializer.Serialize(new { type = "LISTEN", transID = "yesno", data = new { rules = new[] { "word-of-the-day/fun_fact", rule } } })));
        var replies = await SendAsync("""{"type":"CLIENT_ASR","transID":"yesno","data":{"text":"no"}}""");
        AssertReplyTypes(replies, "LISTEN", "EOS");
        using var payload = JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("no", payload.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
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
