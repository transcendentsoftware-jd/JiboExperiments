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
}