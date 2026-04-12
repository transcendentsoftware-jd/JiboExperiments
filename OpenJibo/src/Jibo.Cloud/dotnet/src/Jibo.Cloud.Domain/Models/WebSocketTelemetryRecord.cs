namespace Jibo.Cloud.Domain.Models;

public sealed class WebSocketTelemetryRecord
{
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public string EventType { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string ConnectionId { get; init; } = string.Empty;
    public string? Token { get; init; }
    public string HostName { get; init; } = string.Empty;
    public string Path { get; init; } = "/";
    public string Kind { get; init; } = "unknown";
    public string? TransId { get; init; }
    public string? MessageType { get; init; }
    public string Direction { get; init; } = "internal";
    public string? Text { get; init; }
    public int? BinaryLength { get; init; }
    public IReadOnlyList<string> ReplyTypes { get; init; } = [];
    public int BufferedAudioBytes { get; init; }
    public int BufferedAudioChunks { get; init; }
    public int FinalizeAttempts { get; init; }
    public bool AwaitingTurnCompletion { get; init; }
    public IReadOnlyDictionary<string, object?> Details { get; init; } = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
}
