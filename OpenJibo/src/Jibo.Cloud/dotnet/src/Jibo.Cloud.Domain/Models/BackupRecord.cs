namespace Jibo.Cloud.Domain.Models;

public sealed class BackupRecord
{
    public string BackupId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public string Name { get; init; } = "backup";
}
