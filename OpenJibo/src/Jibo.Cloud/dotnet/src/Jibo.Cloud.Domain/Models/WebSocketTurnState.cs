namespace Jibo.Cloud.Domain.Models;

public sealed class WebSocketTurnState
{
    private int _finalizationInProgress;

    public static readonly TimeSpan DefaultLateAudioIgnoreWindow = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DiagnosticSpeechLateAudioIgnoreWindow = TimeSpan.FromSeconds(7);
    public static readonly TimeSpan StopCommandLateAudioIgnoreWindow = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan LateListenSetupIgnoreWindow = TimeSpan.FromMilliseconds(1500);

    public string? TransId { get; set; }
    public string? ContextPayload { get; set; }
    public DateTimeOffset? ListenOpenedUtc { get; set; }
    public bool ListenHotphrase { get; set; }
    public int HotphraseEmptyTurnCount { get; set; }
    public DateTimeOffset? IgnoreAdditionalAudioUntilUtc { get; set; }
    public DateTimeOffset? IgnoreLateListenSetupUntilUtc { get; set; }
    public DateTimeOffset? AutoFinalizeBlockedUntilUtc { get; set; }
    public string? AudioTranscriptHint { get; set; }
    public string? LastSttError { get; set; }
    public DateTimeOffset? LastSttErrorUtc { get; set; }
    public DateTimeOffset? FirstAudioReceivedUtc { get; set; }
    public DateTimeOffset? LastAudioReceivedUtc { get; set; }
    public DateTimeOffset? LastAutoFinalizeAttemptUtc { get; set; }
    public TimeSpan? ListenSosTimeout { get; set; }
    public TimeSpan? ListenMaxSpeechTimeout { get; set; }
    public int BufferedAudioChunkCount { get; set; }
    public int BufferedAudioBytes { get; set; }
    /// <summary>
    /// Buffered byte count when an early probe deferred for an incomplete command.
    /// Early probes must wait for additional audio before re-running STT.
    /// </summary>
    public int DeferredIncompleteAudioBytes { get; set; }
    public bool DeferredIncompletePegasusRequest { get; set; }
    public List<byte[]> BufferedAudioFrames { get; } = [];
    public int FinalizeAttemptCount { get; set; }
    public string? LastLocalNoInputRule { get; set; }
    public int LocalNoInputCount { get; set; }
    public bool AwaitingTurnCompletion { get; set; }
    public bool SawListen { get; set; }
    public bool SawContext { get; set; }
    public IReadOnlyList<string> ListenRules { get; set; } = [];
    public IReadOnlyList<string> ListenAsrHints { get; set; } = [];

    public bool TryBeginFinalization()
    {
        return Interlocked.CompareExchange(ref _finalizationInProgress, 1, 0) == 0;
    }

    public void EndFinalization()
    {
        Volatile.Write(ref _finalizationInProgress, 0);
    }
}
