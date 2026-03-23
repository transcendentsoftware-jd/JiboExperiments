namespace Jibo.Runtime.Abstractions;

public sealed class TurnContext
{
    public string TurnId { get; init; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; init; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    public TurnInputMode InputMode { get; init; }
    public TurnSourceKind SourceKind { get; init; }

    public string? WakePhrase { get; init; }
    public string? RawTranscript { get; init; }
    public string? NormalizedTranscript { get; init; }

    public string? Locale { get; init; } = "en-US";
    public string? TimeZone { get; init; }

    public bool IsFollowUpEligible { get; init; }
    public IDictionary<string, object?> Attributes { get; init; } = new Dictionary<string, object?>();
}