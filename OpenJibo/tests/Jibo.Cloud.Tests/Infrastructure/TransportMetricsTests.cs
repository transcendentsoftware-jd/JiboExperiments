using System.Diagnostics.Metrics;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Cloud.Infrastructure.Telemetry;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class TransportMetricsTests
{
    [Fact]
    public void HttpPayload_RecordsExactBytesWithOnlyBoundedTags()
    {
        var measurements = new List<MeasurementRecord>();
        using var metrics = new TransportMetrics();
        using var listener = CreateListener(measurements, meter: metrics.Meter);
        metrics.HttpPayload("out", "device/secret-id", "attacker-method", 701, 19);

        var bytes = Assert.Single(measurements,
            item => item.Name == "openjibo.transport.http.payload_bytes");
        Assert.Equal(19, bytes.Value);
        Assert.Equal("out", bytes.Tags["direction"]);
        Assert.Equal("other", bytes.Tags["endpoint_class"]);
        Assert.Equal("OTHER", bytes.Tags["method"]);
        Assert.Equal("other", bytes.Tags["status_class"]);
        Assert.DoesNotContain(bytes.Tags.Values,
            value => value?.Contains("secret", StringComparison.Ordinal) == true);
        Assert.Equal(4, bytes.Tags.Count);
    }

    [Theory]
    [InlineData(199, "1xx")]
    [InlineData(200, "2xx")]
    [InlineData(302, "3xx")]
    [InlineData(404, "4xx")]
    [InlineData(503, "5xx")]
    public void HttpPayload_UsesBoundedStatusClass(int statusCode, string expectedClass)
    {
        var measurements = new List<MeasurementRecord>();
        using var metrics = new TransportMetrics();
        using var listener = CreateListener(measurements, meter: metrics.Meter);
        metrics.HttpPayload("in", "protocol", "post", statusCode, -4);

        var bytes = Assert.Single(measurements,
            item => item.Name == "openjibo.transport.http.payload_bytes");
        Assert.Equal(0, bytes.Value);
        Assert.Equal("in", bytes.Tags["direction"]);
        Assert.Equal("protocol", bytes.Tags["endpoint_class"]);
        Assert.Equal("POST", bytes.Tags["method"]);
        Assert.Equal(expectedClass, bytes.Tags["status_class"]);
    }

    [Fact]
    public void WebSocketMessage_RecordsExactBytesWithOnlyBoundedTags()
    {
        var measurements = new List<MeasurementRecord>();
        using var metrics = new TransportMetrics();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (ReferenceEquals(instrument.Meter, metrics.Meter))
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            measurements.Add(new MeasurementRecord(instrument.Name, measurement,
                tags.ToArray().ToDictionary(pair => pair.Key, pair => pair.Value?.ToString())));
        });
        listener.Start();

        metrics.WebSocketMessage("out", "attacker-controlled-kind", "attacker-controlled-payload",
            "token-should-never-be-a-tag-value", 7);

        var bytes = Assert.Single(measurements,
            item => item.Name == "openjibo.transport.websocket.payload_bytes");
        Assert.Equal(7, bytes.Value);
        Assert.Equal("out", bytes.Tags["direction"]);
        Assert.Equal("other", bytes.Tags["socket_kind"]);
        Assert.Equal("other", bytes.Tags["payload_class"]);
        Assert.Equal("other", bytes.Tags["message_class"]);
        Assert.DoesNotContain(bytes.Tags.Values, value => value?.Contains("token", StringComparison.Ordinal) == true);
        Assert.Equal(4, bytes.Tags.Count);
    }

    [Fact]
    public void ActiveConnections_UsesBoundedSocketKind()
    {
        var values = new List<(long Value, string? Kind)>();
        using var metrics = new TransportMetrics();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (ReferenceEquals(instrument.Meter, metrics.Meter) &&
                instrument.Name == "openjibo.transport.websocket.active_connections")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
            values.Add((measurement, tags.ToArray().Single().Value?.ToString())));
        listener.Start();

        metrics.WebSocketConnectionOpened("neo-hub-listen");
        metrics.WebSocketConnectionClosed("neo-hub-listen");

        Assert.Equal([(1, "neo-hub-listen"), (-1, "neo-hub-listen")], values);
    }

    [Fact]
    public void SessionRegistry_ReportsActiveCountChangesWithoutIdentifiers()
    {
        var measurements = new List<MeasurementRecord>();
        using var metrics = new TransportMetrics();
        using var listener = CreateListener(measurements, meter: metrics.Meter);
        var registry = new BoundedCloudSessionRegistry(2, 2, metrics);

        registry.RegisterActive("secret-token-1", new CloudSession { SessionId = "secret-session-1" });
        registry.RegisterActive("secret-token-2", new CloudSession { SessionId = "secret-session-2" });
        registry.RegisterActive("secret-token-3", new CloudSession { SessionId = "secret-session-3" });
        registry.Clear();

        var values = measurements.Where(item => item.Name == "openjibo.runtime.active_sessions").ToArray();
        Assert.Equal([1, 1, 1, -1, -2], values.Select(item => item.Value));
        Assert.All(values, item => Assert.Empty(item.Tags));
    }

    [Fact]
    public void SessionRegistry_DoesNotReturnExpiredDurableToken()
    {
        var registry = new BoundedCloudSessionRegistry(2, 2);
        registry.RegisterDurableToken("expired-token", new CloudSession
        {
            Token = "expired-token",
            Kind = "robot",
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
        });

        Assert.Null(registry.FindDurable("expired-token"));
        Assert.Empty(registry.DurableTokenValues);
    }

    [Fact]
    public void BufferedAudio_RecordsAcceptedAndRejectedBytesWithoutTags()
    {
        var measurements = new List<MeasurementRecord>();
        using var metrics = new TransportMetrics();
        using var listener = CreateListener(measurements, meter: metrics.Meter);

        metrics.BufferedAudioAccepted(4096);
        metrics.BufferedAudioLimitRejected(1_048_577);

        listener.RecordObservableInstruments();

        var accepted = Assert.Single(measurements, item => item.Name == "openjibo.audio.accepted_bytes");
        var current = Assert.Single(measurements,
            item => item.Name == "openjibo.audio.current_buffered_bytes");
        var highWater = Assert.Single(measurements,
            item => item.Name == "openjibo.audio.buffered_high_water_bytes");
        var rejected = Assert.Single(measurements, item => item.Name == "openjibo.audio.rejected_bytes");
        var rejection = Assert.Single(measurements,
            item => item.Name == "openjibo.audio.buffer_limit_rejections");
        Assert.Equal(4096, accepted.Value);
        Assert.Equal(1_048_577, rejected.Value);
        Assert.Equal(1, rejection.Value);
        Assert.True(current.Value >= 0);
        Assert.True(highWater.Value >= 0);
        Assert.Empty(accepted.Tags);
        Assert.Empty(current.Tags);
        Assert.Empty(highWater.Tags);
        Assert.Empty(rejected.Tags);
        Assert.Empty(rejection.Tags);
    }

    [Fact]
    public void TurnMetrics_RecordTimingAndOutcomesWithOnlyBoundedTags()
    {
        var measurements = new List<MeasurementRecord>();
        var doubleMeasurements = new List<DoubleMeasurementRecord>();
        using var metrics = new TransportMetrics();
        using var listener = CreateListener(measurements, doubleMeasurements, metrics.Meter);

        metrics.ActiveTurnsChanged(1);
        metrics.TurnPhaseCompleted("secret-phase", "secret-outcome", -12.5);
        metrics.TurnFinalizationSuppressed("secret-reason");
        metrics.TurnRepliesEmitted(-4, true);
        metrics.ActiveTurnsChanged(-1);

        var operations = Assert.Single(measurements,
            item => item.Name == "openjibo.turn.phase.operations");
        Assert.Equal(1, operations.Value);
        Assert.Equal("other", operations.Tags["phase"]);
        Assert.Equal("other", operations.Tags["outcome"]);

        var duration = Assert.Single(doubleMeasurements,
            item => item.Name == "openjibo.turn.phase.duration");
        Assert.Equal(0, duration.Value);
        Assert.Equal(operations.Tags, duration.Tags);

        Assert.Equal([1L, -1L], measurements
            .Where(item => item.Name == "openjibo.turn.active")
            .Select(item => item.Value));
        Assert.Equal("other", Assert.Single(measurements,
            item => item.Name == "openjibo.turn.finalization_suppressions").Tags["reason"]);
        Assert.Equal(0, Assert.Single(measurements,
            item => item.Name == "openjibo.turn.reply_count").Value);
        Assert.Equal("true", Assert.Single(measurements,
            item => item.Name == "openjibo.turn.reply_batches").Tags["has_eos"]);
        Assert.DoesNotContain(measurements.SelectMany(item => item.Tags.Values),
            value => value?.Contains("secret", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void PersistenceMetrics_RecordBoundedCacheAndPoolDimensions()
    {
        var measurements = new List<MeasurementRecord>();
        using var metrics = new TransportMetrics();
        using var listener = CreateListener(measurements, meter: metrics.Meter);

        metrics.PersistenceCacheAccess("secret-store", "secret-result");
        metrics.PersistenceCacheAccess("cloud_device", "hit");
        metrics.PostgreSqlPoolConfigured("cloud_state", 8);
        metrics.PostgreSqlPoolConfigured("secret-store", -2);
        listener.RecordObservableInstruments();

        var accesses = measurements.Where(item => item.Name == "openjibo.persistence.cache.accesses").ToArray();
        Assert.Contains(accesses, item => item.Tags["store"] == "other" && item.Tags["result"] == "other");
        Assert.Contains(accesses, item => item.Tags["store"] == "cloud_device" && item.Tags["result"] == "hit");

        var poolLimits = measurements
            .Where(item => item.Name == "openjibo.persistence.postgresql.configured_max_connections")
            .ToArray();
        Assert.Contains(poolLimits, item => item.Value == 8 && item.Tags["store"] == "cloud_state");
        Assert.Contains(poolLimits, item => item.Value == 1 && item.Tags["store"] == "other");
        Assert.DoesNotContain(measurements.SelectMany(item => item.Tags.Values),
            value => value?.Contains("secret", StringComparison.Ordinal) == true);
    }

    private sealed record MeasurementRecord(string Name, long Value, Dictionary<string, string?> Tags);

    private sealed record DoubleMeasurementRecord(string Name, double Value, Dictionary<string, string?> Tags);

    private static MeterListener CreateListener(List<MeasurementRecord> measurements,
        List<DoubleMeasurementRecord>? doubleMeasurements = null,
        Meter? meter = null)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (meter is not null ? ReferenceEquals(instrument.Meter, meter) :
                instrument.Meter.Name == TransportMetrics.MeterName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            measurements.Add(new MeasurementRecord(instrument.Name, measurement,
                tags.ToArray().ToDictionary(pair => pair.Key, pair => pair.Value?.ToString())));
        });
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
        {
            doubleMeasurements?.Add(new DoubleMeasurementRecord(instrument.Name, measurement,
                tags.ToArray().ToDictionary(pair => pair.Key, pair => pair.Value?.ToString())));
        });
        listener.Start();
        return listener;
    }
}
