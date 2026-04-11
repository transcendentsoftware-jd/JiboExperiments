namespace Jibo.Runtime.Abstractions;

public sealed class RobotEvent
{
    public string EventId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    public string EventType { get; init; } = string.Empty;
    public string? SessionId { get; init; }
    public string? Transcript { get; init; }
    public string? WakePhrase { get; init; }
    public string? DeviceId { get; init; }
    public string? HostName { get; init; }
    public string? RequestId { get; init; }
    public string? ProtocolService { get; init; }
    public string? ProtocolOperation { get; init; }
    public string? FirmwareVersion { get; init; }
    public string? ApplicationVersion { get; init; }

    public IDictionary<string, object?> Payload { get; init; } = new Dictionary<string, object?>();
}
