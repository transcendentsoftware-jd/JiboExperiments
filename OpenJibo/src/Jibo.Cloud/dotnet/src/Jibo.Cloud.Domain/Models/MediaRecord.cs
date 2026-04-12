namespace Jibo.Cloud.Domain.Models;

public sealed class MediaRecord
{
    public string Path { get; init; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public string MediaType { get; init; } = "unknown";
    public string Reference { get; init; } = string.Empty;
    public string AccountId { get; init; } = "usr_openjibo_owner";
    public string LoopId { get; init; } = "openjibo-default-loop";
    public string Url { get; init; } = string.Empty;
    public bool IsEncrypted { get; init; }
    public bool IsDeleted { get; init; }
    public IDictionary<string, object?> Meta { get; init; } = new Dictionary<string, object?>();
}
