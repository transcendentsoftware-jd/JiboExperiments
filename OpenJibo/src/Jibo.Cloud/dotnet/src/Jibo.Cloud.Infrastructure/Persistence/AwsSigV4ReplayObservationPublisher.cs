using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jibo.Cloud.Infrastructure.Persistence;

public sealed class AwsSigV4ReplayObservationOptions
{
    public bool Enabled { get; set; }
    public string? HmacKey { get; set; }
    public short KeyVersion { get; set; } = 1;
    public string? PreviousHmacKey { get; set; }
    public short PreviousKeyVersion { get; set; }
    public int Capacity { get; set; } = 256;
    public string? ConnectionString { get; set; }
}

/// <summary>
/// Computes the replay HMAC on the request thread, then queues only the opaque
/// digest and bounded labels for best-effort cross-replica observation.
/// </summary>
public sealed class AwsSigV4ReplayObservationPublisher : BackgroundService,
    IAwsSigV4ReplayObservationPublisher
{
    public const string MeterName = "Jibo.Cloud.SigV4ReplayObservation";
    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Counter<long> Outcomes = Meter.CreateCounter<long>(
        "openjibo.sigv4_replay_observation.outcomes");
    private static long _degradedPublisherCount;
    private static readonly ObservableGauge<long> DegradedPublishers = Meter.CreateObservableGauge(
        "openjibo.sigv4_replay_observation.degraded_publishers",
        () => Volatile.Read(ref _degradedPublisherCount));
    private static readonly Counter<long> HealthTransitions = Meter.CreateCounter<long>(
        "openjibo.sigv4_replay_observation.health_transitions");
    private static readonly HashSet<string> SupportedOperations =
    [
        "Account.CreateHubToken",
        "Notification.NewRobotToken"
    ];

    private readonly IAwsSigV4ReplayObservationStore _store;
    private readonly ReplayDigestKeySlot[] _keys;
    private readonly Channel<ObservationWorkItem> _channel;
    private readonly ILogger<AwsSigV4ReplayObservationPublisher> _logger;
    private bool _persistenceDegraded;
    private long _suppressedPersistenceFailures;

    public AwsSigV4ReplayObservationPublisher(
        IAwsSigV4ReplayObservationStore store,
        AwsSigV4ReplayDigestKey key,
        int capacity,
        ILogger<AwsSigV4ReplayObservationPublisher> logger,
        AwsSigV4ReplayDigestKey? previousKey = null)
    {
        _store = store;
        if (previousKey is not null && previousKey.Version == key.Version)
            throw new ArgumentException("Current and previous replay digest key versions must differ.",
                nameof(previousKey));
        if (previousKey is not null && previousKey.HasSameMaterial(key))
            throw new ArgumentException("Current and previous replay digest keys must use different material.",
                nameof(previousKey));
        _keys = previousKey is null
            ? [new ReplayDigestKeySlot("current", key)]
            : [new ReplayDigestKeySlot("current", key), new ReplayDigestKeySlot("previous", previousKey)];
        _logger = logger;
        _channel = Channel.CreateBounded<ObservationWorkItem>(new BoundedChannelOptions(
            Math.Clamp(capacity, 1, 4096))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    }

    public bool TryPublish(AwsSigV4VerifiedProof proof, string operation)
    {
        ArgumentNullException.ThrowIfNull(proof);
        if (!SupportedOperations.Contains(operation))
            throw new ArgumentException("Replay observation operation is not supported.", nameof(operation));

        var observations = _keys.Select(slot => new ReplayDigestObservation(
            proof.CreateReplayDigest(slot.Key, operation),
            slot.Key.Version,
            slot.Name)).ToArray();
        var item = new ObservationWorkItem(observations, operation);
        var accepted = _channel.Writer.TryWrite(item);
        foreach (var observation in observations)
            Record(operation, observation.KeySlot, accepted ? "enqueued" : "dropped");
        return accepted;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    Exception? firstFailure = null;
                    string? firstFailedKeySlot = null;
                    var failedObservations = 0;
                    foreach (var observation in item.Observations)
                    {
                        try
                        {
                            await _store.ObserveAsync(
                                observation.Digest,
                                observation.KeyVersion,
                                item.Operation,
                                stoppingToken);
                            Record(item.Operation, observation.KeySlot, "persisted");
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (Exception exception)
                        {
                            Record(item.Operation, observation.KeySlot, "failed");
                            firstFailure ??= exception;
                            firstFailedKeySlot ??= observation.KeySlot;
                            failedObservations++;
                        }
                    }

                    if (firstFailure is not null)
                    {
                        ReportPersistenceFailure(
                            firstFailure,
                            item.Operation,
                            firstFailedKeySlot!,
                            failedObservations);
                    }
                    else
                    {
                        ReportPersistenceRecovery();
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            ClearDegradedGauge();
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }

    private static void Record(string operation, string keySlot, string outcome) => Outcomes.Add(1,
        new KeyValuePair<string, object?>("operation", operation),
        new KeyValuePair<string, object?>("key_slot", keySlot),
        new KeyValuePair<string, object?>("outcome", outcome));

    private void ReportPersistenceFailure(
        Exception exception,
        string operation,
        string keySlot,
        int failedObservations)
    {
        if (_persistenceDegraded)
        {
            _suppressedPersistenceFailures += failedObservations;
            return;
        }

        _persistenceDegraded = true;
        _suppressedPersistenceFailures = 0;
        Interlocked.Increment(ref _degradedPublisherCount);
        RecordHealthTransition("degraded");
        _logger.LogWarning(exception,
            "Legacy SigV4 replay observation persistence degraded operation={Operation} keySlot={KeySlot} failedObservations={FailedObservations} shadow=true",
            operation,
            keySlot,
            failedObservations);
    }

    private void ReportPersistenceRecovery()
    {
        if (!_persistenceDegraded) return;

        _persistenceDegraded = false;
        Interlocked.Decrement(ref _degradedPublisherCount);
        RecordHealthTransition("recovered");
        _logger.LogInformation(
            "Legacy SigV4 replay observation persistence recovered suppressedFailures={SuppressedFailures} shadow=true",
            _suppressedPersistenceFailures);
        _suppressedPersistenceFailures = 0;
    }

    private void ClearDegradedGauge()
    {
        if (!_persistenceDegraded) return;

        _persistenceDegraded = false;
        Interlocked.Decrement(ref _degradedPublisherCount);
        _suppressedPersistenceFailures = 0;
    }

    private static void RecordHealthTransition(string state) => HealthTransitions.Add(1,
        new KeyValuePair<string, object?>("state", state));

    private sealed record ReplayDigestKeySlot(string Name, AwsSigV4ReplayDigestKey Key);
    private sealed record ReplayDigestObservation(byte[] Digest, short KeyVersion, string KeySlot);
    private sealed record ObservationWorkItem(ReplayDigestObservation[] Observations, string Operation);
}
