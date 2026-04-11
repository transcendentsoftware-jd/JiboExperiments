namespace Jibo.Cloud.Domain.Models;

public sealed class UpdateManifest
{
    public string UpdateId { get; init; } = "noop-update";
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public string FromVersion { get; init; } = "unknown";
    public string ToVersion { get; init; } = "unknown";
    public string Changes { get; init; } = "No update available";
    public string Url { get; init; } = "https://api.jibo.com/update/noop";
    public string ShaHash { get; init; } = "noop";
    public long Length { get; init; }
    public string Subsystem { get; init; } = "robot";
    public string? Filter { get; init; }
}
