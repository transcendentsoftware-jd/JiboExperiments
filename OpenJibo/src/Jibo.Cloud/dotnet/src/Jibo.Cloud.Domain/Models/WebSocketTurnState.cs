namespace Jibo.Cloud.Domain.Models;

public sealed class WebSocketTurnState
{
    public string? TransId { get; set; }
    public string? ContextPayload { get; set; }
    public bool ListenHotphrase { get; set; }
    public string? AudioTranscriptHint { get; set; }
    public string? LastSttError { get; set; }
    public DateTimeOffset? LastSttErrorUtc { get; set; }
    public DateTimeOffset? FirstAudioReceivedUtc { get; set; }
    public DateTimeOffset? LastAudioReceivedUtc { get; set; }
    public int BufferedAudioChunkCount { get; set; }
    public int BufferedAudioBytes { get; set; }
    public List<byte[]> BufferedAudioFrames { get; } = [];
    public int FinalizeAttemptCount { get; set; }
    public bool AwaitingTurnCompletion { get; set; }
    public bool SawListen { get; set; }
    public bool SawContext { get; set; }
    public IReadOnlyList<string> ListenRules { get; set; } = [];
    public IReadOnlyList<string> ListenAsrHints { get; set; } = [];
}
