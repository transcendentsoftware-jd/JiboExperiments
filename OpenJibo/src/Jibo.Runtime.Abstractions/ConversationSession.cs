namespace Jibo.Runtime.Abstractions;

public sealed class ConversationSession
{
    public string SessionId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastActivityUtc { get; set; } = DateTimeOffset.UtcNow;

    public string? ActiveTopic { get; set; }
    public string? LastIntent { get; set; }
    public IDictionary<string, object?> Slots { get; } = new Dictionary<string, object?>();

    public DateTimeOffset? FollowUpExpiresUtc { get; set; }
    public bool FollowUpOpen => FollowUpExpiresUtc.HasValue && FollowUpExpiresUtc > DateTimeOffset.UtcNow;
}