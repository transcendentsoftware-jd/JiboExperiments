using System.Text.RegularExpressions;

namespace Jibo.Cloud.Application.Abstractions;

/// <summary>A single typed source observation. Keep the same instance and event ID across retries.</summary>
public sealed class RuntimeUsageEvent
{
    private readonly byte[] _sourceSubjectHmac;

    public RuntimeUsageEvent(
        byte[] sourceSubjectHmac, Guid eventId, DateTimeOffset occurredUtc, string serviceEnvironment,
        long successfulTurns, long failedTurns, long httpRequests, long httpRequestBytes,
        long httpResponseBytes, long webSocketInboundMessages, long webSocketOutboundMessages,
        long webSocketInboundBytes, long webSocketOutboundBytes, long audioInputBytes,
        string? incompleteReasonCode = null)
    {
        ArgumentNullException.ThrowIfNull(sourceSubjectHmac);
        if (sourceSubjectHmac.Length != 32)
            throw new ArgumentException("Source subject HMAC must contain exactly 32 bytes.", nameof(sourceSubjectHmac));
        if (eventId == Guid.Empty)
            throw new ArgumentException("Event ID must be nonzero.", nameof(eventId));
        if (occurredUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Event timestamp must use UTC offset zero.", nameof(occurredUtc));
        if (serviceEnvironment is null || !Regex.IsMatch(serviceEnvironment, "\\A[a-z][a-z0-9-]{0,31}\\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Service environment is invalid.", nameof(serviceEnvironment));
        if (incompleteReasonCode is not null &&
            !Regex.IsMatch(incompleteReasonCode, "\\A[a-z][a-z0-9-]{0,63}\\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Incomplete reason code is invalid.", nameof(incompleteReasonCode));

        Validate(successfulTurns, 1, nameof(successfulTurns));
        Validate(failedTurns, 1, nameof(failedTurns));
        if (successfulTurns + failedTurns > 1)
            throw new ArgumentException("At most one turn result is allowed per event.");
        Validate(httpRequests, 1, nameof(httpRequests));
        Validate(httpRequestBytes, 1_073_741_824, nameof(httpRequestBytes));
        Validate(httpResponseBytes, 1_073_741_824, nameof(httpResponseBytes));
        Validate(webSocketInboundMessages, 10_000, nameof(webSocketInboundMessages));
        Validate(webSocketOutboundMessages, 10_000, nameof(webSocketOutboundMessages));
        Validate(webSocketInboundBytes, 1_073_741_824, nameof(webSocketInboundBytes));
        Validate(webSocketOutboundBytes, 1_073_741_824, nameof(webSocketOutboundBytes));
        Validate(audioInputBytes, 1_073_741_824, nameof(audioInputBytes));
        if (incompleteReasonCode is null && successfulTurns == 0 && failedTurns == 0 &&
            httpRequests == 0 && httpRequestBytes == 0 && httpResponseBytes == 0 &&
            webSocketInboundMessages == 0 && webSocketOutboundMessages == 0 &&
            webSocketInboundBytes == 0 && webSocketOutboundBytes == 0 && audioInputBytes == 0)
            throw new ArgumentException("An event must contain a nonzero component or an incomplete reason.");

        _sourceSubjectHmac = (byte[])sourceSubjectHmac.Clone();
        EventId = eventId;
        // PostgreSQL stores timestamptz at microsecond precision; normalize once before retries.
        OccurredUtc = new DateTimeOffset(occurredUtc.Ticks - occurredUtc.Ticks % 10, TimeSpan.Zero);
        ServiceEnvironment = serviceEnvironment;
        SuccessfulTurns = successfulTurns;
        FailedTurns = failedTurns;
        HttpRequests = httpRequests;
        HttpRequestBytes = httpRequestBytes;
        HttpResponseBytes = httpResponseBytes;
        WebSocketInboundMessages = webSocketInboundMessages;
        WebSocketOutboundMessages = webSocketOutboundMessages;
        WebSocketInboundBytes = webSocketInboundBytes;
        WebSocketOutboundBytes = webSocketOutboundBytes;
        AudioInputBytes = audioInputBytes;
        IncompleteReasonCode = incompleteReasonCode;
    }

    public ReadOnlyMemory<byte> SourceSubjectHmac => (byte[])_sourceSubjectHmac.Clone();
    public Guid EventId { get; }
    public DateTimeOffset OccurredUtc { get; }
    public string ServiceEnvironment { get; }
    public long SuccessfulTurns { get; }
    public long FailedTurns { get; }
    public long HttpRequests { get; }
    public long HttpRequestBytes { get; }
    public long HttpResponseBytes { get; }
    public long WebSocketInboundMessages { get; }
    public long WebSocketOutboundMessages { get; }
    public long WebSocketInboundBytes { get; }
    public long WebSocketOutboundBytes { get; }
    public long AudioInputBytes { get; }
    public string? IncompleteReasonCode { get; }

    private static void Validate(long value, long maximum, string name)
    {
        if (value < 0 || value > maximum)
            throw new ArgumentOutOfRangeException(name);
    }
}

public sealed class RuntimeUsageWriteResult(
    Guid managedRobotId, DateOnly usageDate, long accumulatorRevision, bool wasReplay)
{
    public Guid ManagedRobotId { get; } = managedRobotId;
    public DateOnly UsageDate { get; } = usageDate;
    public long AccumulatorRevision { get; } = accumulatorRevision;
    public bool WasReplay { get; } = wasReplay;
}

public interface IRuntimeUsageEventWriter
{
    Task<RuntimeUsageWriteResult> WriteAsync(RuntimeUsageEvent usageEvent, CancellationToken cancellationToken = default);
}
