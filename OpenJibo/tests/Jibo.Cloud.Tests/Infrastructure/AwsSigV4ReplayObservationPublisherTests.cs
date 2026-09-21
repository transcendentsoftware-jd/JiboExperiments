using System.Security.Cryptography;
using System.Diagnostics.Metrics;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class AwsSigV4ReplayObservationPublisherTests
{
    private static readonly AwsSigV4ReplayDigestKey Key = new(
        SHA256.HashData("replay-publisher-key"u8), 3);
    private static readonly AwsSigV4ReplayDigestKey PreviousKey = new(
        SHA256.HashData("previous-replay-publisher-key"u8), 2);

    [Fact]
    public void TryPublish_DropsWhenBoundedQueueIsFull()
    {
        var outcomes = new List<(string Operation, string KeySlot, string Outcome)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == AwsSigV4ReplayObservationPublisher.MeterName)
                    meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var values = tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value?.ToString());
            outcomes.Add((values["operation"]!, values["key_slot"]!, values["outcome"]!));
        });
        listener.Start();
        var publisher = new AwsSigV4ReplayObservationPublisher(
            new RecordingStore(),
            Key,
            1,
            NullLogger<AwsSigV4ReplayObservationPublisher>.Instance);
        var proof = CreateProof();

        Assert.True(publisher.TryPublish(proof, "Account.CreateHubToken"));
        Assert.False(publisher.TryPublish(proof, "Account.CreateHubToken"));
        Assert.Contains(("Account.CreateHubToken", "current", "enqueued"), outcomes);
        Assert.Contains(("Account.CreateHubToken", "current", "dropped"), outcomes);
    }

    [Fact]
    public async Task ExecuteAsync_PersistsOnlyOpaqueDigestAndContinuesAfterFailure()
    {
        var store = new RecordingStore(failuresBeforeSuccess: 1);
        var publisher = new AwsSigV4ReplayObservationPublisher(
            store,
            Key,
            4,
            NullLogger<AwsSigV4ReplayObservationPublisher>.Instance);
        var proof = CreateProof();
        var expectedDigest = proof.CreateReplayDigest(Key, "Notification.NewRobotToken");

        await publisher.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(publisher.TryPublish(proof, "Account.CreateHubToken"));
            Assert.True(publisher.TryPublish(proof, "Notification.NewRobotToken"));
            await store.SecondObservation.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await publisher.StopAsync(CancellationToken.None);
            publisher.Dispose();
        }

        Assert.Equal(2, store.Observations.Count);
        var persisted = store.Observations[1];
        Assert.Equal(expectedDigest, persisted.Digest);
        Assert.Equal(3, persisted.KeyVersion);
        Assert.Equal("Notification.NewRobotToken", persisted.Operation);
    }

    [Fact]
    public async Task ExecuteAsync_CoalescesOutageWarningsAndReportsRecovery()
    {
        var measurements = new System.Collections.Concurrent.ConcurrentQueue<
            (string Instrument, long Value, string? State, string? Outcome)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == AwsSigV4ReplayObservationPublisher.MeterName)
                    meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var values = tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value?.ToString());
            values.TryGetValue("state", out var state);
            values.TryGetValue("outcome", out var outcome);
            measurements.Enqueue((instrument.Name, value, state, outcome));
        });
        listener.Start();

        var store = new RecordingStore(failuresBeforeSuccess: 3);
        var logger = new RecordingLogger<AwsSigV4ReplayObservationPublisher>();
        var publisher = new AwsSigV4ReplayObservationPublisher(store, Key, 8, logger);

        await publisher.StartAsync(CancellationToken.None);
        try
        {
            for (var index = 0; index < 3; index++)
                Assert.True(publisher.TryPublish(CreateProof(), "Account.CreateHubToken"));
            await logger.WarningLogged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            listener.RecordObservableInstruments();

            Assert.True(publisher.TryPublish(CreateProof(), "Account.CreateHubToken"));
            await store.FirstSuccessfulObservation.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await publisher.StopAsync(CancellationToken.None);
            publisher.Dispose();
        }
        listener.RecordObservableInstruments();

        Assert.Equal(3, measurements.Count(measurement => measurement.Outcome == "failed"));
        Assert.Equal(1, measurements.Count(measurement => measurement.Outcome == "persisted"));
        Assert.Contains(measurements, measurement =>
            measurement.Instrument == "openjibo.sigv4_replay_observation.degraded_publishers" &&
            measurement.Value == 1);
        Assert.Contains(measurements, measurement =>
            measurement.Instrument == "openjibo.sigv4_replay_observation.degraded_publishers" &&
            measurement.Value == 0);
        Assert.Contains(measurements, measurement => measurement.State == "degraded");
        Assert.Contains(measurements, measurement => measurement.State == "recovered");
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        var recovery = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Information);
        Assert.Contains("suppressedFailures=2", recovery.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_DualPublishesDistinctCurrentAndPreviousDigests()
    {
        var store = new RecordingStore();
        var publisher = new AwsSigV4ReplayObservationPublisher(
            store,
            Key,
            2,
            NullLogger<AwsSigV4ReplayObservationPublisher>.Instance,
            PreviousKey);
        var proof = CreateProof();

        await publisher.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(publisher.TryPublish(proof, "Account.CreateHubToken"));
            await store.SecondObservation.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await publisher.StopAsync(CancellationToken.None);
            publisher.Dispose();
        }

        Assert.Collection(store.Observations,
            current => Assert.Equal(3, current.KeyVersion),
            previous => Assert.Equal(2, previous.KeyVersion));
        Assert.NotEqual(store.Observations[0].Digest, store.Observations[1].Digest);
    }

    [Fact]
    public void Constructor_RejectsVersionCollision()
    {
        var duplicateVersion = new AwsSigV4ReplayDigestKey(
            SHA256.HashData("different-material"u8), Key.Version);

        Assert.Throws<ArgumentException>(() => new AwsSigV4ReplayObservationPublisher(
            new RecordingStore(),
            Key,
            1,
            NullLogger<AwsSigV4ReplayObservationPublisher>.Instance,
            duplicateVersion));
    }

    [Fact]
    public void Constructor_RejectsSameMaterialWithDifferentVersion()
    {
        var sameMaterial = new AwsSigV4ReplayDigestKey(
            SHA256.HashData("replay-publisher-key"u8), 4);

        Assert.Throws<ArgumentException>(() => new AwsSigV4ReplayObservationPublisher(
            new RecordingStore(),
            Key,
            1,
            NullLogger<AwsSigV4ReplayObservationPublisher>.Instance,
            sameMaterial));
    }

    [Fact]
    public void TryPublish_RejectsUnboundedOperationLabel()
    {
        var publisher = new AwsSigV4ReplayObservationPublisher(
            new RecordingStore(),
            Key,
            1,
            NullLogger<AwsSigV4ReplayObservationPublisher>.Instance);

        Assert.Throws<ArgumentException>(() =>
            publisher.TryPublish(CreateProof(), "attacker-controlled-operation"));
    }

    private static AwsSigV4VerifiedProof CreateProof() => new(
        "access-key-sentinel",
        "20260920",
        "api",
        "jibo",
        new DateTimeOffset(2026, 9, 20, 12, 34, 56, TimeSpan.Zero),
        SHA256.HashData("signature-sentinel"u8));

    private sealed class RecordingStore(int failuresBeforeSuccess = 0) : IAwsSigV4ReplayObservationStore
    {
        private readonly int _failuresBeforeSuccess = failuresBeforeSuccess;
        private int _calls;

        public List<(byte[] Digest, short KeyVersion, string Operation)> Observations { get; } = [];
        public TaskCompletionSource SecondObservation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstSuccessfulObservation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AwsSigV4ReplayObservation> ObserveAsync(
            ReadOnlyMemory<byte> replayDigest,
            short keyVersion,
            string operation,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            Observations.Add((replayDigest.ToArray(), keyVersion, operation));
            if (call <= _failuresBeforeSuccess)
                throw new InvalidOperationException("simulated persistence failure");
            if (call == 2) SecondObservation.TrySetResult();
            FirstSuccessfulObservation.TrySetResult();

            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new AwsSigV4ReplayObservation(
                AwsSigV4ReplayObservationStatus.FirstSeen,
                1,
                now,
                now,
                now.AddMinutes(15)));
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public TaskCompletionSource WarningLogged { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
            if (logLevel == LogLevel.Warning) WarningLogged.TrySetResult();
        }
    }
}
