namespace Jibo.Cloud.Domain.Models;

public sealed class WebSocketReply
{
    public string? Text { get; init; }
    public int DelayMs { get; init; }
    public bool Close { get; init; }
}
