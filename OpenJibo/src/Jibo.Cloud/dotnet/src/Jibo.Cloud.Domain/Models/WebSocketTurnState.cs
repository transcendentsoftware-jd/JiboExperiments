namespace Jibo.Cloud.Domain.Models;

public sealed class WebSocketTurnState
{
    public string? TransId { get; set; }
    public string? ContextPayload { get; set; }
    public string? AudioTranscriptHint { get; set; }
    public DateTimeOffset? LastAudioReceivedUtc { get; set; }
    public int BufferedAudioChunkCount { get; set; }
    public int BufferedAudioBytes { get; set; }
    public int FinalizeAttemptCount { get; set; }
    public bool AwaitingTurnCompletion { get; set; }
    public IReadOnlyList<string> ListenRules { get; set; } = [];
}
