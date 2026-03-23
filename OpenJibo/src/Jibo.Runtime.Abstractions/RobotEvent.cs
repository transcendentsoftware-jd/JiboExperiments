namespace Jibo.Runtime.Abstractions;

public sealed class RobotEvent
{
    public string EventId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    public string EventType { get; init; } = string.Empty;
    public string? SessionId { get; init; }
    public string? Transcript { get; init; }
    public string? WakePhrase { get; init; }

    public IDictionary<string, object?> Payload { get; init; } = new Dictionary<string, object?>();
}