namespace Jibo.Cloud.Domain.Models;

public sealed class WebSocketMessageEnvelope
{
    public string ConnectionId { get; init; } = Guid.NewGuid().ToString("N");
    public string HostName { get; init; } = string.Empty;
    public string Path { get; init; } = "/";
    public string Kind { get; init; } = "unknown";
    public string? Token { get; init; }
    public string? Text { get; init; }
    public byte[]? Binary { get; init; }
    public bool IsBinary => Binary is { Length: > 0 };
}
