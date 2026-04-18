using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Runtime.Abstractions;
using Moq;

namespace Jibo.Cloud.Tests.Turn;

public sealed class FileTurnTelemetrySinkTests
{
    [Fact]
    public async Task RecordsTranscriptErrorOnTurnError()
    {
        var sink = new Mock<ITurnTelemetrySink>();
        var sttStrategySelector = new Mock<ISttStrategySelector>();
        sttStrategySelector.Setup(s => s.SelectAsync(It.IsAny<TurnContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("dummy"));

        var turnService = new WebSocketTurnFinalizationService(
            new ProtocolToTurnContextMapper(),
            Mock.Of<IConversationBroker>(),
            new ResponsePlanToSocketMessagesMapper(),
            sttStrategySelector.Object,
            sink.Object
        );

        await turnService.HandleTurnAsync(new CloudSession() { TurnState = { BufferedAudioBytes = 100 }}, new WebSocketMessageEnvelope(), "dummy",
            CancellationToken.None);

        sink.Verify(s => s.RecordTranscriptError(It.IsAny<Exception>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task AutoFinalize_DoesNotFallbackImmediately_WhenSttThrows()
    {
        var sink = new Mock<ITurnTelemetrySink>();
        var sttStrategySelector = new Mock<ISttStrategySelector>();
        sttStrategySelector.Setup(s => s.SelectAsync(It.IsAny<TurnContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("ffmpeg failed"));

        var turnService = new WebSocketTurnFinalizationService(
            new ProtocolToTurnContextMapper(),
            Mock.Of<IConversationBroker>(),
            new ResponsePlanToSocketMessagesMapper(),
            sttStrategySelector.Object,
            sink.Object
        );

        var session = new CloudSession();
        session.TurnState.AwaitingTurnCompletion = true;
        session.TurnState.SawListen = true;
        session.TurnState.SawContext = true;
        session.TurnState.BufferedAudioBytes = 12000;
        session.TurnState.BufferedAudioChunkCount = 5;
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2);

        var replies = await turnService.HandleContextAsync(
            session,
            new WebSocketMessageEnvelope { Text = """{"type":"CONTEXT","data":{"topic":"conversation"}}""" },
            CancellationToken.None);

        Assert.Single(replies);
        using var payload = System.Text.Json.JsonDocument.Parse(replies[0].Text!);
        Assert.Equal("OPENJIBO_TURN_PENDING", payload.RootElement.GetProperty("type").GetString());
        Assert.Equal(12000, session.TurnState.BufferedAudioBytes);
        Assert.Equal("ffmpeg failed", session.TurnState.LastSttError);

        sink.Verify(s => s.RecordTranscriptError(It.IsAny<Exception>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once());
    }
}
