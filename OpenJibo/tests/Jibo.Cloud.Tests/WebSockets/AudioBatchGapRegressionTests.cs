using System.Text;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Runtime.Abstractions;
using Moq;

namespace Jibo.Cloud.Tests.WebSockets;

public sealed class AudioBatchGapRegressionTests
{
    [Theory]
    [InlineData(420)]
    [InlineData(500)]
    [InlineData(1000)]
    public async Task NetworkBatchGapDoesNotEndSpeech(int gapMs)
    {
        var store = new InMemoryCloudStateStore();
        var stt = new Mock<ISttStrategy>();
        stt.SetupGet(strategy => strategy.Name).Returns("batch-gap-test");
        stt.Setup(strategy => strategy.CanHandle(It.IsAny<TurnContext>())).Returns(true);
        stt.Setup(strategy => strategy.TranscribeAsync(It.IsAny<TurnContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SttResult { Text = "what time is it", Provider = "batch-gap-test" });
        var broker = new Mock<IConversationBroker>();
        broker.Setup(value => value.HandleTurnAsync(It.IsAny<TurnContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResponsePlan
            {
                Status = ResponseStatus.Succeeded,
                IntentName = "time",
                FollowUp = FollowUpPolicy.None,
                Actions = { new SpeakAction { Text = "Test clock response." } }
            });
        var service = new JiboWebSocketService(store, new NullWebSocketTelemetrySink(),
            new WebSocketTurnFinalizationService(broker.Object,
                new DefaultSttStrategySelector([stt.Object]), new NullTurnTelemetrySink()));
        var listen = new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "hub-network-batch-gap",
            Text = """{"type":"LISTEN","transID":"network-batch-gap","data":{"hotphrase":true,"rules":["launch","globals/global_commands_launch"]}}"""
        };
        WebSocketMessageEnvelope Audio(byte[] binary) => new()
        {
            HostName = listen.HostName,
            Path = listen.Path,
            Kind = listen.Kind,
            Token = listen.Token,
            Binary = binary
        };

        await service.HandleMessageAsync(listen);
        var session = Assert.IsType<CloudSession>(store.FindSessionByToken(listen.Token!));
        // Include context so arrival-based early probes cannot bypass the gap guard.
        session.TurnState.SawContext = true;
        await service.HandleMessageAsync(Audio(BuildOggFrame(0x02, "OpusHead")));
        await service.HandleMessageAsync(Audio(BuildOggFrame(0x00, "OpusTags")));
        await service.HandleMessageAsync(Audio(BuildOggFrame(0x00)));
        await service.HandleMessageAsync(Audio(BuildOggFrame(0x00)));
        session.TurnState.FirstAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(1500);
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(gapMs);

        // Another batch is arriving: the preceding network gap is not acoustic silence.
        Assert.Empty(await service.HandleMessageAsync(Audio(BuildOggFrame(0x00))));
        Assert.True(session.TurnState.AwaitingTurnCompletion);
        Assert.Null(session.LastTranscript);

        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(gapMs);
        Assert.Empty(await service.HandleIdleAsync(session, listen));
        stt.Verify(strategy => strategy.TranscribeAsync(It.IsAny<TurnContext>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Explicit end-of-stream must still complete promptly.
        Assert.NotEmpty(await service.HandleMessageAsync(Audio(BuildOggFrame(0x04))));
        stt.Verify(strategy => strategy.TranscribeAsync(It.IsAny<TurnContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.False(session.TurnState.AwaitingTurnCompletion);
        Assert.Equal(0, session.TurnState.BufferedAudioBytes);
    }

    private static byte[] BuildOggFrame(byte headerType, string marker = "audio")
    {
        // Same framing fixture as JiboWebSocketServiceTests; no content-silence signal.
        var frame = Enumerable.Range(0, 4300).Select(index => (byte)(index % 251)).ToArray();
        "OggS"u8.CopyTo(frame);
        frame[4] = 0;
        frame[5] = headerType;
        Encoding.ASCII.GetBytes(marker).CopyTo(frame, 32);
        return frame;
    }
}
