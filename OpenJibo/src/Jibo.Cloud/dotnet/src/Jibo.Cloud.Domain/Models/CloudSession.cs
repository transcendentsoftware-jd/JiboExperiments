namespace Jibo.Cloud.Domain.Models;

public sealed class CloudSession
{
    public string SessionId { get; init; } = Guid.NewGuid().ToString("N");
    public string Kind { get; init; } = "http";
    public string? AccountId { get; init; }
    public string? DeviceId { get; init; }
    public string? Token { get; init; }
    public string? HostName { get; init; }
    public string? Path { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastAudioReceivedUtc { get; set; }
    public DateTimeOffset? FollowUpExpiresUtc { get; set; }
    public string? LastMessageType { get; set; }
    public string? LastListenType { get; set; }
    public string? LastIntent { get; set; }
    public string? LastTranscript { get; set; }
    public string? LastTransId { get; set; }
    public int BufferedAudioChunkCount { get; set; }
    public int BufferedAudioBytes { get; set; }
    public bool AwaitingTurnCompletion { get; set; }
    public bool FollowUpOpen => FollowUpExpiresUtc.HasValue && FollowUpExpiresUtc > DateTimeOffset.UtcNow;
    public IDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>();
}
