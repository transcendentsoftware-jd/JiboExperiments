namespace Jibo.Cloud.Domain.Models;

public sealed class UploadReference
{
    public string UploadId { get; init; } = Guid.NewGuid().ToString("N");
    public string Path { get; init; } = string.Empty;
    public string ContentType { get; init; } = "application/octet-stream";
    public long Length { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
}
