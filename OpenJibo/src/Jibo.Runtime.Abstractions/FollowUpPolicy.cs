namespace Jibo.Runtime.Abstractions;

public sealed class FollowUpPolicy
{
    public static FollowUpPolicy None => new() { KeepMicOpen = false, Timeout = TimeSpan.Zero };

    public bool KeepMicOpen { get; init; }
    public TimeSpan Timeout { get; init; }
    public string? ExpectedTopic { get; init; }
    public IDictionary<string, object?> Hints { get; init; } = new Dictionary<string, object?>();
}