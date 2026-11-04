namespace Jibo.Cloud.Domain.Models;

public sealed class MemberCalendarFeedRecord
{
    public string? OwnershipEpoch { get; init; }
    public string FeedId { get; init; } = Guid.NewGuid().ToString("N");
    public string LoopId { get; init; } = "openjibo-default-loop";
    public string MemberId { get; init; } = string.Empty;
    public string IcalUrl { get; init; } = string.Empty;
    public bool IsEnabled { get; init; } = true;
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSuccessUtc { get; init; }
    public string? LastError { get; init; }
}
