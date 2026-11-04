namespace Jibo.Cloud.Domain.Models;

public sealed class HomeAssistantLinkRecord
{
    public string? OwnershipEpoch { get; init; }
    public string LinkId { get; init; } = Guid.NewGuid().ToString("N");
    public string JiboDeviceId { get; init; } = string.Empty;
    public string JiboFriendlyName { get; init; } = string.Empty;
    public string HaInstanceId { get; init; } = string.Empty;
    public string CommandSecret { get; init; } = string.Empty;
    public bool BlacklistHeat { get; init; }
    public bool BlacklistCool { get; init; }
    public DateTimeOffset PairedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenUtc { get; init; } = DateTimeOffset.UtcNow;
}
