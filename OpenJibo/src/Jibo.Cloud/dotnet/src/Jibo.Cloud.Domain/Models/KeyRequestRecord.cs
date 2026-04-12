namespace Jibo.Cloud.Domain.Models;

public sealed class KeyRequestRecord
{
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
    public string LoopId { get; init; } = "openjibo-default-loop";
    public string PublicKey { get; init; } = string.Empty;
    public string EncryptedKey { get; init; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
}
