using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Telemetry;
using Jibo.Runtime.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Jibo.Cloud.Tests.Turn;

public sealed class FileTurnTelemetrySinkTests
{
    [Fact]
    public async Task RecordsTurnDiagnosticSnapshot()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), "OpenJibo.Tests", Guid.NewGuid().ToString("N"));
        var sink = new FileTurnTelemetrySink(
            NullLogger<FileTurnTelemetrySink>.Instance,
            Options.Create(new TurnTelemetryOptions
            {
                Enabled = true,
                DirectoryPath = directoryPath
            }));

        await sink.RecordTurnDiagnosticAsync("yes_no_turn_received", new Dictionary<string, object?>
        {
            ["transID"] = "trans-1",
            ["bufferedAudioBytes"] = 1234,
            ["listenRules"] = new[] { "shared/yes_no", "globals/gui_nav" },
            ["awaitingTurnCompletion"] = true
        });

        var filePath = Directory.GetFiles(directoryPath, "*.events.ndjson").Single();
        var payload = JsonDocument.Parse(await File.ReadAllTextAsync(filePath)).RootElement;
        Assert.Equal("yes_no_turn_received", payload.GetProperty("type").GetString());
        Assert.Equal("trans-1", payload.GetProperty("details").GetProperty("transID").GetString());
        Assert.Equal(1234, payload.GetProperty("details").GetProperty("bufferedAudioBytes").GetInt32());
    }

    [Fact]
    public async Task RecordsTranscriptErrorOnTurnError()
    {
        var sink = new Mock<ITurnTelemetrySink>();
        var sttStrategySelector = new Mock<ISttStrategySelector>();
        sttStrategySelector.Setup(s => s.SelectAsync(It.IsAny<TurnContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("dummy"));

        var turnService = new WebSocketTurnFinalizationService(Mock.Of<IConversationBroker>(),
            sttStrategySelector.Object,
            sink.Object
        );

        await turnService.HandleTurnAsync(new CloudSession { TurnState = { BufferedAudioBytes = 100 } },
            new WebSocketMessageEnvelope(), "dummy",
            CancellationToken.None);

        sink.Verify(
            s => s.RecordTranscriptError(It.IsAny<Exception>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once());
    }

    [Fact]
    public async Task AutoFinalize_DoesNotFallbackImmediately_WhenSttThrows()
    {
        var sink = new Mock<ITurnTelemetrySink>();
        var sttStrategySelector = new Mock<ISttStrategySelector>();
        sttStrategySelector.Setup(s => s.SelectAsync(It.IsAny<TurnContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("ffmpeg failed"));

        var turnService = new WebSocketTurnFinalizationService(Mock.Of<IConversationBroker>(),
            sttStrategySelector.Object,
            sink.Object
        );

        var session = new CloudSession
        {
            TurnState =
            {
                AwaitingTurnCompletion = true,
                SawListen = true,
                SawContext = true,
                BufferedAudioBytes = 15000,
                BufferedAudioChunkCount = 5,
                FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2)
            }
        };

        var replies = await turnService.HandleContextAsync(
            session,
            new WebSocketMessageEnvelope { Text = """{"type":"CONTEXT","data":{"topic":"conversation"}}""" },
            CancellationToken.None);

        Assert.Empty(replies);
        Assert.True(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal(15000, session.TurnState.BufferedAudioBytes);
        Assert.Equal("ffmpeg failed", session.TurnState.LastSttError);

        sink.Verify(
            s => s.RecordTranscriptError(It.IsAny<Exception>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once());
    }
}
