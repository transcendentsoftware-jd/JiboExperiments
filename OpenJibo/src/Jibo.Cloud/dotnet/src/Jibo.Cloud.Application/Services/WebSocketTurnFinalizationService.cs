using System.Diagnostics;
using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Audio;
using Jibo.Cloud.Domain.Models;
using Jibo.Runtime.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jibo.Cloud.Application.Services;

public sealed class WebSocketTurnFinalizationService(
    IConversationBroker conversationBroker,
    ISttStrategySelector sttStrategySelector,
    ITurnTelemetrySink sink,
    ILogger<WebSocketTurnFinalizationService> logger,
    HomeAssistantCommandService? homeAssistantCommandService = null,
    ICloudStateStore? cloudStateStore = null,
    IMediaContentStore? mediaContentStore = null,
    RobotIdentitySuggestionStore? identitySuggestionStore = null,
    ITransportMetrics? transportMetrics = null
)
{
    private readonly ITransportMetrics _metrics = transportMetrics ?? NullTransportMetrics.Instance;

    internal const int MaximumBufferedAudioBytes = 4 * 1024 * 1024;
    internal const int MaximumTotalBufferedAudioBytes = 64 * 1024 * 1024;
    internal const int MaximumBufferedAudioFrames = 2048;
    internal const int MaximumAudioFrameBytes = 1024 * 1024;
    private static readonly BoundedAudioBufferBudget AudioBufferBudget =
        new(MaximumTotalBufferedAudioBytes);
    public static long CurrentBufferedAudioBytes => AudioBufferBudget.ReservedBytes;
    public static long BufferedAudioHighWaterMarkBytes => AudioBufferBudget.HighWaterMarkBytes;
    private const int AutoFinalizeMinBufferedAudioBytes = 8500;
    private const int AutoFinalizeMinBufferedAudioPages = 3;
    private const int AutoFinalizeHotphraseOggEarlyProbeMinBufferedAudioBytes = 9_000;
    private const int AutoFinalizeHotphraseOggEarlyProbeMinAudioPages = 3;
    private const int AutoFinalizeHotphraseOggContinuousProbeMinBufferedAudioBytes = 7_000;
    private const int AutoFinalizeHotphraseOggContinuousProbeMinAudioPages = 4;
    private const string GlsmPhaseMetadataKey = "glsmPhase";
    private const int AutoFinalizeContinuationDeferralMaxAttempts = 4;
    private static readonly TimeSpan AutoFinalizeReconnectGrace = TimeSpan.FromSeconds(4);
    // Fast path when Opus content-silence (VAD) confirms end-of-speech.
    private static readonly TimeSpan AutoFinalizeMinTurnAge = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan AutoFinalizeSilenceWindow = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan AutoFinalizeHotphraseOggSilenceWindow = TimeSpan.FromMilliseconds(400);
    // Stock robots batch active speech about 420-500ms apart. Network gaps must
    // stay longer than that cadence; only audio-content silence gets the fast path.
    private static readonly TimeSpan AutoFinalizeHotphraseOggArrivalGap = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan AutoFinalizeHotphraseOggEarlyProbeMinTurnAge = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan AutoFinalizeHotphraseOggEarlyProbeGap = AutoFinalizeHotphraseOggArrivalGap;
    private static readonly TimeSpan AutoFinalizeEarlyProbeRetryInterval = TimeSpan.FromMilliseconds(250);

    // Safety net only when VAD cannot see trailing quiet. Mid-utterance probes at
    // ~1.2s caused incomplete STT retries that stacked to ~3-3.5s on longer commands.
    private static readonly TimeSpan
        AutoFinalizeHotphraseOggContinuousProbeMinTurnAge = TimeSpan.FromMilliseconds(2800);
    private static readonly TimeSpan AutoFinalizeHotphraseOggContinuousProbeTrailingSilence =
        TimeSpan.FromMilliseconds(300);

    private static readonly TimeSpan AutoFinalizeHardBufferedAudioAge = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan AutoFinalizeNoAudioListenAge = TimeSpan.FromSeconds(9);
    private static readonly TimeSpan AutoFinalizeMissingTranscriptFallbackAge = TimeSpan.FromMilliseconds(4200);
    private static readonly TimeSpan AutoFinalizeContinuationDeferralMaxAge = TimeSpan.FromMilliseconds(3600);
    private static readonly TimeSpan AutoFinalizeHotphraseOnlyNoInputAge = TimeSpan.FromSeconds(9);
    private static readonly TimeSpan StaleListenSetupRecoveryAge = TimeSpan.FromSeconds(9);

    private static readonly HashSet<string> PegasusAffinityContinuationStems = new(StringComparer.Ordinal)
    {
        "i love",
        "i like",
        "i like the",
        "i enjoy",
        "i do like",
        "we love",
        "we like",
        "we enjoy",
        "i dislike",
        "i hate",
        "i hate the",
        "i loathe",
        "i not like",
        "i dont like",
        "i don t like",
        "i do not like",
        "i did not like",
        "i didn t like",
        "i didnt like",
        "i didn t really like",
        "i didnt really like",
        "i don t really like",
        "i dont really like",
        "i dont enjoy",
        "i don t enjoy",
        "i do not enjoy",
        "i did not enjoy",
        "i didn t enjoy",
        "i didnt enjoy",
        "i didn t really enjoy",
        "i didnt really enjoy",
        "i dont love",
        "i don t love",
        "i do not love",
        "i don t love to",
        "i dont love to",
        "i do not love to",
        "i cant stand",
        "i can t stand",
        "i cant stand the",
        "i can t stand the",
        "we dislike",
        "we hate",
        "we despise",
        "we detest",
        "we loathe",
        "we cant stand",
        "we can t stand",
        "i despise",
        "i detest"
    };

    private static readonly string[] YesNoAcknowledgementPrefixes =
    [
        "uh",
        "um",
        "hmm",
        "well",
        "so",
        "actually",
        "honestly",
        "thanks",
        "thank you"
    ];

    private static readonly string[] TranscriptNoisePrefixes =
    [
        "o",
        "oh",
        "uh",
        "um",
        "hmm",
        "erm",
        "er",
        "ah",
        "eh",
        "mm",
        "mmm",
        "well",
        "so",
        "actually",
        "honestly"
    ];

    private static readonly HashSet<string> SingleTokenUsableTranscripts = new(StringComparer.Ordinal)
    {
        "joke",
        "funny",
        "dance",
        "boogie",
        "twirl",
        "time",
        "date",
        "today",
        "day",
        "hello",
        "hi",
        "hey",
        "twerk",
        "weather",
        "news",
        "radio",
        "stop",
        "sleep",
        "sing",
        "help",
        "yes",
        "yeah",
        "yep",
        "yup",
        "sure",
        "ok",
        "okay",
        "introductions",
        "meet",
        "no",
        "nope",
        "nah"
    };

    private static readonly HashSet<string> HotphraseNonCommandTranscripts = new(StringComparer.Ordinal)
    {
        "thank you",
        "thanks",
        "thank you jibo",
        "thanks jibo"
    };

    private static readonly HashSet<string> HotphraseRobotFallbackTranscripts = new(StringComparer.Ordinal)
    {
        "to my sources maybe ask me again in a little while",
        "maybe ask me again in a little while",
        "i don't know where it's did you check under the bed",
        "i dont know where its did you check under the bed",
        "i don't know where it is did you check under the bed",
        "i dont know where it is did you check under the bed",
        "oh i was just about to make a pizza",
        "i was just about to make a pizza",
        "if i was just about to make a pizza"
    };

    private static readonly HashSet<string> YesNoAffirmativeLeadTokens = new(StringComparer.Ordinal)
    {
        "yes",
        "yeah",
        "yep",
        "yup",
        "sure",
        "ok",
        "okay",
        "absolutely",
        "affirmative",
        "definitely",
        "certainly",
        "indeed"
    };

    private static readonly HashSet<string> YesNoNegativeLeadTokens = new(StringComparer.Ordinal)
    {
        "no",
        "nope",
        "nah",
        "negative",
        "never"
    };

    private static readonly HashSet<string> YesNoAffirmativeLeadPhrases = new(StringComparer.Ordinal)
    {
        "uh huh",
        "sounds good",
        "sure thing",
        "why not",
        "please do",
        "go ahead",
        "of course",
        "i guess so",
        "i think so"
    };

    private static readonly HashSet<string> YesNoNegativeLeadPhrases = new(StringComparer.Ordinal)
    {
        "not now",
        "not today",
        "not really",
        "no thanks",
        "no thank you",
        "maybe later",
        "i guess not",
        "i do not",
        "i dont",
        "i don t"
    };

    public WebSocketTurnFinalizationService(
        IConversationBroker conversationBroker,
        ISttStrategySelector sttStrategySelector,
        ITurnTelemetrySink sink)
        : this(conversationBroker, sttStrategySelector, sink, NullLogger<WebSocketTurnFinalizationService>.Instance)
    {
    }

    public static void ObserveIncomingMessage(CloudSession session, string? text)
    {
        if (!TryReadTransId(text, out var nextTransId) || string.IsNullOrWhiteSpace(nextTransId)) return;

        if (!string.Equals(session.TurnState.TransId, nextTransId, StringComparison.Ordinal))
            ResetTurnState(session.TurnState, nextTransId);

        session.LastTransId = nextTransId;
    }

    public static bool MarkPrematureSocketLoopEnded(CloudSession session, string? expectedTransId = null)
    {
        var turnState = session.TurnState;
        if (!string.IsNullOrWhiteSpace(expectedTransId) &&
            !string.Equals(turnState.TransId, expectedTransId, StringComparison.Ordinal))
            return false;

        if (!turnState.AwaitingTurnCompletion && turnState.BufferedAudioBytes <= 0) return false;

        var blockedUntilUtc = DateTimeOffset.UtcNow.Add(AutoFinalizeReconnectGrace);
        turnState.AutoFinalizeBlockedUntilUtc = blockedUntilUtc;
        turnState.IgnoreAdditionalAudioUntilUtc = blockedUntilUtc;
        return true;
    }

    private static void MarkSttFailureGrace(CloudSession session)
    {
        var turnState = session.TurnState;
        if (turnState.BufferedAudioBytes <= 0 && !turnState.AwaitingTurnCompletion) return;

        var blockedUntilUtc = DateTimeOffset.UtcNow.Add(AutoFinalizeReconnectGrace);
        turnState.AutoFinalizeBlockedUntilUtc = blockedUntilUtc;
        turnState.IgnoreAdditionalAudioUntilUtc = blockedUntilUtc;
    }

    public async Task<IReadOnlyList<WebSocketReply>> HandleBinaryAudioAsync(
        CloudSession session,
        WebSocketMessageEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var turnState = session.TurnState;
            var receivedAtUtc = DateTimeOffset.UtcNow;
            var pageCounts = DescribeBufferedAudioPages(turnState);
            var hasAudioBearingPage = false;
            logger.LogDebug(
                "Turn binary audio entered session={SessionId} transId={TransId} bytes={Bytes} rawFrames={RawFrames} audioPages={AudioPages} metadataPages={MetadataPages} awaiting={Awaiting} sawListen={SawListen} sawContext={SawContext}",
                session.SessionId,
                turnState.TransId,
                envelope.Binary?.Length ?? 0,
                turnState.BufferedAudioChunkCount,
                pageCounts.AudioBearingPageCount,
                pageCounts.MetadataPageCount,
                turnState.AwaitingTurnCompletion,
                turnState.SawListen,
                turnState.SawContext);
            var ignoreLateAudio = ShouldIgnoreLateAudio(session);
            var ignoreAudioWithoutListen = ShouldIgnoreAudioWithoutListen(turnState);
            if (ignoreLateAudio || ignoreAudioWithoutListen)
            {
                await sink.RecordTurnDiagnosticAsync("binary_audio_ignored", BuildTurnDiagnosticSnapshot(session,
                    envelope, new Dictionary<string, object?>
                    {
                        ["ignored"] = true,
                        ["ignoreLateAudio"] = ignoreLateAudio,
                        ["ignoreAudioWithoutListen"] = ignoreAudioWithoutListen,
                        ["awaitingTurnCompletion"] = turnState.AwaitingTurnCompletion,
                        ["bufferedAudioBytes"] = turnState.BufferedAudioBytes,
                        ["bufferedAudioChunks"] = turnState.BufferedAudioChunkCount,
                        ["bufferedAudioRawFrames"] = pageCounts.RawFrameCount,
                        ["bufferedAudioMetadataPages"] = pageCounts.MetadataPageCount,
                        ["bufferedAudioAudioBearingPages"] = pageCounts.AudioBearingPageCount,
                        ["sawListen"] = turnState.SawListen,
                        ["sawContext"] = turnState.SawContext
                    }), cancellationToken);
                logger.LogDebug(
                    "Turn binary audio ignored session={SessionId} transId={TransId} ignoreLateAudio={IgnoreLateAudio} ignoreWithoutListen={IgnoreWithoutListen}",
                    session.SessionId,
                    turnState.TransId,
                    ignoreLateAudio,
                    ignoreAudioWithoutListen);
                return [];
            }

            var incomingBytes = envelope.Binary?.Length ?? 0;
            if (incomingBytes > MaximumAudioFrameBytes ||
                turnState.BufferedAudioBytes > MaximumBufferedAudioBytes - incomingBytes ||
                turnState.BufferedAudioFrames.Count >= MaximumBufferedAudioFrames ||
                !AudioBufferBudget.TryReserve(session.SessionId, incomingBytes))
            {
                _metrics.BufferedAudioLimitRejected(incomingBytes);
                logger.LogWarning(
                    "Turn binary audio rejected at memory limit session={SessionId} transId={TransId} bufferedBytes={BufferedBytes} incomingBytes={IncomingBytes} frameCount={FrameCount}",
                    session.SessionId,
                    turnState.TransId,
                    turnState.BufferedAudioBytes,
                    incomingBytes,
                    turnState.BufferedAudioFrames.Count);
                await sink.RecordTurnDiagnosticAsync("binary_audio_limit_exceeded",
                    BuildTurnDiagnosticSnapshot(session, envelope, new Dictionary<string, object?>
                    {
                        ["bufferedAudioBytes"] = turnState.BufferedAudioBytes,
                        ["incomingBytes"] = incomingBytes,
                        ["bufferedAudioChunks"] = turnState.BufferedAudioChunkCount,
                        ["maximumBufferedAudioBytes"] = MaximumBufferedAudioBytes,
                        ["maximumTotalBufferedAudioBytes"] = MaximumTotalBufferedAudioBytes,
                        ["maximumAudioFrameBytes"] = MaximumAudioFrameBytes
                    }), cancellationToken);
                ResetBufferedAudio(session);
                turnState.AwaitingTurnCompletion = false;
                return [];
            }

            session.LastMessageType = "BINARY_AUDIO";
            turnState.BufferedAudioChunkCount += 1;
            turnState.BufferedAudioBytes += envelope.Binary?.Length ?? 0;
            _metrics.BufferedAudioAccepted(incomingBytes);
            if (envelope.Binary is { Length: > 0 }) turnState.BufferedAudioFrames.Add([.. envelope.Binary]);
            turnState.AwaitingTurnCompletion = true;
            session.Metadata["lastAudioBytes"] = envelope.Binary?.Length ?? 0;
            pageCounts = DescribeBufferedAudioPages(turnState);
            hasAudioBearingPage = pageCounts.AudioBearingPageCount > 0;
            if (hasAudioBearingPage)
            {
                turnState.FirstAudioReceivedUtc ??= receivedAtUtc;
            }
            await sink.RecordTurnDiagnosticAsync("binary_audio_received", BuildTurnDiagnosticSnapshot(session, envelope,
                new Dictionary<string, object?>
                {
                    ["bufferedAudioBytes"] = turnState.BufferedAudioBytes,
                    ["bufferedAudioChunks"] = turnState.BufferedAudioChunkCount,
                    ["bufferedAudioRawFrames"] = pageCounts.RawFrameCount,
                    ["bufferedAudioMetadataPages"] = pageCounts.MetadataPageCount,
                    ["bufferedAudioAudioBearingPages"] = pageCounts.AudioBearingPageCount,
                    ["awaitingTurnCompletion"] = turnState.AwaitingTurnCompletion,
                    ["sawListen"] = turnState.SawListen,
                    ["sawContext"] = turnState.SawContext,
                    ["listenRules"] = turnState.ListenRules,
                    ["listenAsrHints"] = turnState.ListenAsrHints,
                    ["yesNoRule"] = turnState.ListenRules.FirstOrDefault(IsConstrainedYesNoRule)
                }), cancellationToken);

            LogAutoFinalizeDecision("binary_audio", session, turnState);
            if (await TryCloseStalledListenAsNoInputAsync(session, envelope, "binary_audio", cancellationToken) is
                { } stalledListenReplies)
                return stalledListenReplies;

            if (ShouldAutoFinalize(session))
            {
                logger.LogDebug(
                    "Turn binary audio auto-finalizing session={SessionId} transId={TransId} bytes={Bytes} rawFrames={RawFrames} audioPages={AudioPages}",
                    session.SessionId,
                    turnState.TransId,
                    turnState.BufferedAudioBytes,
                    turnState.BufferedAudioChunkCount,
                    pageCounts.AudioBearingPageCount);
                return await FinalizeOrScheduleAsync(session, envelope, "AUTO_FINALIZE", true, cancellationToken);
            }

            if (ShouldEarlyFinalizeBufferedAudio(session))
            {
                logger.LogDebug(
                    "Turn binary audio early-probe auto-finalizing session={SessionId} transId={TransId} bytes={Bytes} rawFrames={RawFrames} audioPages={AudioPages}",
                    session.SessionId,
                    turnState.TransId,
                    turnState.BufferedAudioBytes,
                    turnState.BufferedAudioChunkCount,
                    pageCounts.AudioBearingPageCount);
                turnState.LastAutoFinalizeAttemptUtc = DateTimeOffset.UtcNow;
                return await FinalizeOrScheduleAsync(session, envelope, "AUTO_FINALIZE", true, cancellationToken);
            }

            // Update last-audio after the finalize decision so a frame that arrives
            // after a silence gap can still close the turn (tests and idle path).
            if (hasAudioBearingPage)
                turnState.LastAudioReceivedUtc = receivedAtUtc;
            logger.LogDebug(
                "Turn binary audio exit without finalization session={SessionId} transId={TransId} bytes={Bytes} rawFrames={RawFrames} audioPages={AudioPages}",
                session.SessionId,
                turnState.TransId,
                turnState.BufferedAudioBytes,
                turnState.BufferedAudioChunkCount,
                pageCounts.AudioBearingPageCount);
            return [];
        }
        finally
        {
            await TrackGlsmPhaseAsync(session, envelope, "binary_audio", cancellationToken);
        }
    }

    public async Task<IReadOnlyList<WebSocketReply>> HandleContextAsync(
        CloudSession session,
        WebSocketMessageEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var turnState = session.TurnState;
            logger.LogDebug(
                "Turn context entered session={SessionId} transId={TransId} textBytes={TextBytes} awaiting={Awaiting} bufferedBytes={BufferedBytes} bufferedChunks={BufferedChunks}",
                session.SessionId,
                turnState.TransId,
                envelope.Text?.Length ?? 0,
                turnState.AwaitingTurnCompletion,
                turnState.BufferedAudioBytes,
                turnState.BufferedAudioChunkCount);
            turnState.SawContext = true;
            turnState.ContextPayload = ExtractDataPayload(envelope.Text);
            session.Metadata["context"] = turnState.ContextPayload;
            var previouslyObservedDeviceId = session.Metadata.TryGetValue("registeredDeviceId", out var registeredValue)
                ? registeredValue?.ToString()
                : session.DeviceId;
            if (identitySuggestionStore is not null && !string.IsNullOrWhiteSpace(previouslyObservedDeviceId))
            {
                foreach (var candidate in RobotIdentityCandidateExtractor.Extract(
                             turnState.ContextPayload ?? envelope.Text))
                    identitySuggestionStore.Observe(previouslyObservedDeviceId, candidate.Value,
                        "websocket-context", candidate.Field);
                if (identitySuggestionStore.GetSuggestion(previouslyObservedDeviceId) is not null)
                    session.Metadata["identitySuggestionDeviceId"] = previouslyObservedDeviceId;
            }
            SessionRobotIdentityBinder.TryBindFromContextPayload(
                session,
                turnState.ContextPayload ?? envelope.Text);
            // CONTEXT establishes the runtime identity. Reapply any administrator
            // binding from an earlier session before persisting firmware hints so a
            // reconnect cannot create a second inventory record for the same robot.
            if (cloudStateStore is not null && !string.IsNullOrWhiteSpace(session.DeviceId))
                cloudStateStore.ReinheritDialogMetadata(session);
            PersistContextRelease(session, turnState.ContextPayload ?? envelope.Text);
            PersistContextSkillId(session, envelope.Text);

            if (TryReadContextProperty(envelope.Text, "audioTranscriptHint", out var transcriptHint) &&
                !string.IsNullOrWhiteSpace(transcriptHint))
            {
                turnState.AudioTranscriptHint = transcriptHint;
                session.Metadata["audioTranscriptHint"] = transcriptHint;
            }

            if (ShouldIgnorePassiveLocalSkillContext(session, envelope.Text))
            {
                logger.LogDebug("Turn context ignored for passive local skill session={SessionId} transId={TransId}",
                    session.SessionId,
                    turnState.TransId);
                turnState.AwaitingTurnCompletion = false;
                turnState.IgnoreAdditionalAudioUntilUtc =
                    DateTimeOffset.UtcNow.Add(WebSocketTurnState.DefaultLateAudioIgnoreWindow);
                ResetBufferedAudio(session);
                ClearListenTracking(turnState);
                return [];
            }

            LogAutoFinalizeDecision("context", session, turnState);
            if (await TryCloseStalledListenAsNoInputAsync(session, envelope, "context", cancellationToken) is
                { } stalledListenReplies)
                return stalledListenReplies;

            if (ShouldAutoFinalize(session))
            {
                logger.LogDebug(
                    "Turn context auto-finalizing session={SessionId} transId={TransId} bufferedBytes={BufferedBytes} bufferedChunks={BufferedChunks}",
                    session.SessionId,
                    turnState.TransId,
                    turnState.BufferedAudioBytes,
                    turnState.BufferedAudioChunkCount);
                return await FinalizeOrScheduleAsync(session, envelope, "AUTO_FINALIZE", true, cancellationToken);
            }

            if (ShouldEarlyFinalizeBufferedAudio(session))
            {
                logger.LogDebug(
                    "Turn context early-probe auto-finalizing session={SessionId} transId={TransId} bufferedBytes={BufferedBytes} bufferedChunks={BufferedChunks}",
                    session.SessionId,
                    turnState.TransId,
                    turnState.BufferedAudioBytes,
                    turnState.BufferedAudioChunkCount);
                turnState.LastAutoFinalizeAttemptUtc = DateTimeOffset.UtcNow;
                return await FinalizeOrScheduleAsync(session, envelope, "AUTO_FINALIZE", true, cancellationToken);
            }

            logger.LogDebug("Turn context exit without finalization session={SessionId} transId={TransId}",
                session.SessionId,
                turnState.TransId);
            return [];
        }
        finally
        {
            await TrackGlsmPhaseAsync(session, envelope, "context", cancellationToken);
        }
    }

    private void PersistContextRelease(CloudSession session, string? contextPayload)
    {
        if (cloudStateStore is null ||
            string.IsNullOrWhiteSpace(session.DeviceId) ||
            !SessionRobotIdentityBinder.TryReadRelease(contextPayload, out var release) ||
            string.IsNullOrWhiteSpace(release))
            return;

        session.Metadata["firmwareVersion"] = release;
        var registeredDeviceId = session.Metadata.TryGetValue("registeredDeviceId", out var registeredValue)
            ? registeredValue?.ToString()
            : null;
        // A context payload may replace the session's runtime DeviceId with its
        // friendly name. Persist release data on the record that actually received
        // the context while leaving the friendly name as suggestion evidence only.
        var observedDeviceId = session.Metadata.TryGetValue("identitySuggestionDeviceId", out var observedValue)
            ? observedValue?.ToString()
            : null;
        var observedCandidates = string.IsNullOrWhiteSpace(observedDeviceId)
            ? []
            : cloudStateStore.FindVisibleIdentityCandidates(observedDeviceId);
        if (observedCandidates.Count == 1)
            observedDeviceId = observedCandidates[0].DeviceId;
        else
            observedDeviceId = null;
        var persistenceDeviceId = !string.IsNullOrWhiteSpace(registeredDeviceId)
            ? registeredDeviceId
            : !string.IsNullOrWhiteSpace(observedDeviceId)
                ? observedDeviceId
                : ResolveVisibleDeviceId(session.DeviceId) ?? session.DeviceId;
        cloudStateStore.GetOrCreateDevice(persistenceDeviceId, release, null);
    }

    private string? ResolveVisibleDeviceId(string identity)
    {
        if (cloudStateStore is null || string.IsNullOrWhiteSpace(identity)) return null;
        var candidates = cloudStateStore.FindVisibleIdentityCandidates(identity.Trim());
        return candidates.Count == 1 ? candidates[0].DeviceId : null;
    }

    private static void PersistContextSkillId(CloudSession session, string? text)
    {
        var skillId = TryReadContextSkillId(text);
        if (string.IsNullOrWhiteSpace(skillId)) return;

        session.Metadata[SkillListenOwnership.LastContextSkillIdKey] = skillId;
    }

    private static void PersistInvokedSkillId(
        CloudSession session,
        InvokeNativeSkillAction? skill,
        bool keepMicOpen)
    {
        var skillId = SkillListenOwnership.ReadInvokedSkillId(skill);
        if (string.IsNullOrWhiteSpace(skillId) && keepMicOpen)
            skillId = "chitchat-skill";

        if (string.IsNullOrWhiteSpace(skillId)) return;

        session.Metadata[SkillListenOwnership.LastContextSkillIdKey] = skillId;
    }

    public async Task<IReadOnlyList<WebSocketReply>> HandleTurnAsync(
        CloudSession session,
        WebSocketMessageEnvelope envelope,
        string messageType,
        CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Turn direct message entered session={SessionId} messageType={MessageType} transId={TransId}",
            session.SessionId,
            messageType,
            session.TurnState.TransId);
        PersistTurnHints(session, envelope.Text);
        var replies = await FinalizeTurnAsync(session, envelope, messageType, false, cancellationToken);
        logger.LogDebug(
            "Turn direct message exit session={SessionId} messageType={MessageType} replyCount={ReplyCount}",
            session.SessionId,
            messageType,
            replies.Count);
        return replies;
    }

    public async Task<IReadOnlyList<WebSocketReply>> HandleProactiveAsync(
        CloudSession session,
        WebSocketMessageEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        var turnState = session.TurnState;
        if (!turnState.TryBeginFinalization())
            return ResponsePlanToSocketMessagesMapper.MapProactiveNoAction(
                    session.LastTransId ?? string.Empty)
                .Select(map => new WebSocketReply
                {
                    Text = map.Text,
                    DelayMs = map.DelayMs
                })
                .ToArray();

        try
        {
            var turn = ProtocolToTurnContextMapper.MapListenMessage(envelope, session, "TRIGGER");
            AmbientTurnProgressPublisher.BindTurn(turn, session);
            var plan = await conversationBroker.HandleTurnAsync(turn, cancellationToken);
            session.LastTranscript = turn.NormalizedTranscript ?? turn.RawTranscript;
            session.LastIntent = plan.IntentName;
            session.LastListenType = null;
            UpdatePendingProactivityOffer(session, plan.IntentName);
            await ApplyContextUpdatesAsync(
                session,
                plan.ContextUpdates,
                envelope,
                plan.IntentName,
                cancellationToken);
            PersistInvokedSkillId(
                session,
                plan.Actions.OfType<InvokeNativeSkillAction>().FirstOrDefault(),
                false);
            session.FollowUpExpiresUtc = null;
            turnState.AwaitingTurnCompletion = false;

            var replies = ResponsePlanToSocketMessagesMapper.MapProactive(plan, turn, session)
                .Select(map => new WebSocketReply
                {
                    Text = map.Text,
                    DelayMs = map.DelayMs
                })
                .ToArray();
            ResetBufferedAudio(session);
            ClearListenTracking(turnState);
            return replies;
        }
        finally
        {
            turnState.EndFinalization();
        }
    }

    public IReadOnlyList<WebSocketReply> HandleListenSetup(CloudSession session,
        WebSocketMessageEnvelope envelope)
    {
        var turnState = session.TurnState;
        logger.LogDebug("Listen setup entered session={SessionId} transId={TransId}",
            session.SessionId,
            turnState.TransId);
        // A live listen request is affirmative activity from the robot. Do not
        // leave the portal row in Sleeping after the robot has resumed listening.
        if (session.Metadata.TryGetValue("sleepState", out var sleepState) &&
            string.Equals(sleepState?.ToString(), "sleeping", StringComparison.OrdinalIgnoreCase))
            session.Metadata["sleepState"] = "awake";
        logger.LogDebug(
            "Listen setup state session={SessionId} transId={TransId} awaiting={Awaiting} sawListen={SawListen} sawContext={SawContext} bufferedBytes={BufferedBytes} bufferedChunks={BufferedChunks} firstAudioUtc={FirstAudioUtc} lastAudioUtc={LastAudioUtc} followUpOpen={FollowUpOpen}",
            session.SessionId,
            turnState.TransId,
            turnState.AwaitingTurnCompletion,
            turnState.SawListen,
            turnState.SawContext,
            turnState.BufferedAudioBytes,
            turnState.BufferedAudioChunkCount,
            turnState.FirstAudioReceivedUtc,
            turnState.LastAudioReceivedUtc,
            session.FollowUpOpen);
        PersistTurnHints(session, envelope.Text);

        var turn = ProtocolToTurnContextMapper.MapListenMessage(envelope, session, "LISTEN");
        if (ShouldIgnoreCompletedWordOfDayTurn(turn))
        {
            logger.LogDebug("Listen setup ignored completed word-of-day turn session={SessionId} transId={TransId}",
                session.SessionId,
                session.TurnState.TransId);
            session.TurnState.AwaitingTurnCompletion = false;
            session.TurnState.IgnoreAdditionalAudioUntilUtc =
                DateTimeOffset.UtcNow.Add(WebSocketTurnState.DefaultLateAudioIgnoreWindow);
            session.FollowUpExpiresUtc = null;
            ResetBufferedAudio(session);
            ClearListenTracking(session.TurnState);
            UpdateGlsmPhaseMarker(session);
            return
            [
                .. ResponsePlanToSocketMessagesMapper.MapNoInput(
                        session.TurnState.TransId ?? session.LastTransId ?? string.Empty,
                        ["word-of-the-day/right_word"])
                    .Select(map => new WebSocketReply
                    {
                        Text = map.Text,
                        DelayMs = map.DelayMs
                    })
            ];
        }

        session.TurnState.AwaitingTurnCompletion = true;
        session.TurnState.ListenOpenedUtc ??= DateTimeOffset.UtcNow;
        UpdateGlsmPhaseMarker(session);
        logger.LogDebug("Listen setup exit session={SessionId} transId={TransId} awaiting={Awaiting}",
            session.SessionId,
            session.TurnState.TransId,
            session.TurnState.AwaitingTurnCompletion);
        return [];
    }

    public async Task<IReadOnlyList<WebSocketReply>> HandleIdleAsync(
        CloudSession session,
        WebSocketMessageEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        var turnState = session.TurnState;
        if (!turnState.AwaitingTurnCompletion) return [];

        if (await TryCloseStalledListenAsNoInputAsync(session, envelope, "socket_idle", cancellationToken) is
            { } stalledListenReplies)
            return stalledListenReplies;

        if (!ShouldAutoFinalize(session) && !ShouldEarlyFinalizeBufferedAudio(session)) return [];

        turnState.LastAutoFinalizeAttemptUtc = DateTimeOffset.UtcNow;
        logger.LogInformation(
            "Turn socket-idle watchdog finalizing session={SessionId} transId={TransId} bufferedBytes={BufferedBytes} bufferedChunks={BufferedChunks}",
            session.SessionId,
            turnState.TransId,
            turnState.BufferedAudioBytes,
            turnState.BufferedAudioChunkCount);
        return await FinalizeOrScheduleAsync(session, envelope, "AUTO_FINALIZE", true, cancellationToken);
    }

    private Task<IReadOnlyList<WebSocketReply>> FinalizeOrScheduleAsync(
        CloudSession session,
        WebSocketMessageEnvelope envelope,
        string messageType,
        bool allowFallbackOnMissingTranscript,
        CancellationToken cancellationToken)
    {
        var sendAsync = AmbientTurnProgressPublisher.TryGetSendAsync();
        if (sendAsync is null)
            return FinalizeTurnAsync(session, envelope, messageType, allowFallbackOnMissingTranscript,
                cancellationToken);

        // Keep the websocket receive loop draining audio while STT/NLU runs.
        _ = Task.Run(async () =>
        {
            try
            {
                using (AmbientTurnProgressPublisher.Begin(sendAsync))
                {
                    var replies = await FinalizeTurnAsync(
                        session,
                        envelope,
                        messageType,
                        allowFallbackOnMissingTranscript,
                        cancellationToken);
                    foreach (var reply in replies)
                    {
                        // Match WebSocketRequestCoordinator.SendRepliesAsync: Nimbus needs a beat
                        // after LISTEN/EOS before chitchat SKILL_ACTION or play fails with the
                        // robot-local "something went wrong" path. On-robot launches (@be/clock)
                        // are more tolerant, which is why time/day still worked.
                        if (reply.DelayMs > 0)
                            await Task.Delay(reply.DelayMs, cancellationToken);
                        await sendAsync(reply, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug(
                    "Background turn finalization canceled session={SessionId} transId={TransId}",
                    session.SessionId,
                    session.TurnState.TransId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Background turn finalization failed session={SessionId} transId={TransId}",
                    session.SessionId,
                    session.TurnState.TransId);
            }
        }, CancellationToken.None);

        return Task.FromResult<IReadOnlyList<WebSocketReply>>([]);
    }

    private async Task<TurnContext> ResolveTranscriptAsync(TurnContext turn, CloudSession session,
        WebSocketMessageEnvelope envelope, CancellationToken cancellationToken)
    {
        var phaseStarted = Stopwatch.GetTimestamp();
        void RecordPhase(string outcome) =>
            _metrics.TurnPhaseCompleted("stt", outcome, Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds);

        logger.LogDebug("Resolve transcript entered session={SessionId} turnId={TurnId} bufferedBytes={BufferedBytes}",
            session.SessionId,
            turn.TurnId,
            session.TurnState.BufferedAudioBytes);
        if (!string.IsNullOrWhiteSpace(turn.NormalizedTranscript) || !string.IsNullOrWhiteSpace(turn.RawTranscript) ||
            session.TurnState.BufferedAudioBytes <= 0)
        {
            logger.LogDebug(
                "Resolve transcript bypassed session={SessionId} turnId={TurnId} hasTranscript={HasTranscript} bufferedBytes={BufferedBytes}",
                session.SessionId,
                turn.TurnId,
                !string.IsNullOrWhiteSpace(turn.NormalizedTranscript) ||
                !string.IsNullOrWhiteSpace(turn.RawTranscript),
                session.TurnState.BufferedAudioBytes);
            RecordPhase("bypassed");
            return turn;
        }

        ISttStrategy? strategy;
        try
        {
            strategy = await sttStrategySelector.SelectAsync(turn, cancellationToken);
            logger.LogDebug(
                "Resolve transcript selected strategy session={SessionId} turnId={TurnId} strategy={Strategy}",
                session.SessionId,
                turn.TurnId,
                strategy?.Name);
            if (strategy is null)
            {
                RecordPhase("unavailable");
                return turn;
            }
        }
        catch (InvalidOperationException ex) when (string.Equals(ex.Message,
                                                       "No STT strategy can handle the current turn.",
                                                       StringComparison.Ordinal))
        {
            logger.LogDebug(ex, "Resolve transcript no strategy available session={SessionId} turnId={TurnId}",
                session.SessionId,
                turn.TurnId);
            RecordPhase("unavailable");
            return turn;
        }
        catch (Exception ex)
        {
            RecordPhase(ex is OperationCanceledException ? "canceled" : "failure");
            session.TurnState.LastSttError = ex.Message;
            session.TurnState.LastSttErrorUtc = DateTimeOffset.UtcNow;
            MarkSttFailureGrace(session);
            await RecordSttProcessingFailureDiagnosticAsync(session, envelope, "strategy_selection", ex,
                cancellationToken);
            await sink.RecordTranscriptError(ex, "Error during STT processing", cancellationToken);
            return turn;
        }

        try
        {
            var sttResult = await strategy.TranscribeAsync(turn, cancellationToken);
            session.TurnState.LastSttError = null;
            session.TurnState.LastSttErrorUtc = null;
            logger.LogDebug(
                "Resolve transcript succeeded session={SessionId} turnId={TurnId} provider={Provider} locale={Locale} text={Text}",
                session.SessionId,
                turn.TurnId,
                sttResult.Provider,
                sttResult.Locale ?? turn.Locale,
                sttResult.Text);

            var attributes = new Dictionary<string, object?>(turn.Attributes, StringComparer.OrdinalIgnoreCase)
            {
                ["sttProvider"] = sttResult.Provider,
                ["sttConfidence"] = sttResult.Confidence
            };

            foreach (var pair in sttResult.Metadata) attributes[$"stt:{pair.Key}"] = pair.Value;

            var rawTranscript = sttResult.Text.Trim();
            var normalizedTranscript = NormalizeBufferedAudioTranscript(turn, rawTranscript);

            var resolvedTurn = new TurnContext
            {
                TurnId = turn.TurnId,
                SessionId = turn.SessionId,
                TimestampUtc = turn.TimestampUtc,
                InputMode = turn.InputMode,
                SourceKind = turn.SourceKind,
                WakePhrase = turn.WakePhrase,
                RawTranscript = rawTranscript,
                NormalizedTranscript = normalizedTranscript,
                DeviceId = turn.DeviceId,
                HostName = turn.HostName,
                RequestId = turn.RequestId,
                ProtocolService = turn.ProtocolService,
                ProtocolOperation = turn.ProtocolOperation,
                FirmwareVersion = turn.FirmwareVersion,
                ApplicationVersion = turn.ApplicationVersion,
                Locale = sttResult.Locale ?? turn.Locale,
                TimeZone = turn.TimeZone,
                IsFollowUpEligible = turn.IsFollowUpEligible,
                Attributes = attributes
            };
            RecordPhase("success");
            return resolvedTurn;
        }
        catch (Exception ex)
        {
            RecordPhase(ex is OperationCanceledException ? "canceled" : "failure");
            session.TurnState.LastSttError = ex.Message;
            session.TurnState.LastSttErrorUtc = DateTimeOffset.UtcNow;
            MarkSttFailureGrace(session);
            logger.LogDebug(ex, "Resolve transcript failed session={SessionId} turnId={TurnId}", session.SessionId,
                turn.TurnId);
            await RecordSttProcessingFailureDiagnosticAsync(session, envelope, "transcription", ex,
                cancellationToken);
            await sink.RecordTranscriptError(ex, "Error during STT processing", cancellationToken);
            return turn;
        }
    }

    private static void PersistTurnHints(CloudSession session, string? text)
    {
        var turnState = session.TurnState;
        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            var isListenMessage = root.TryGetProperty("type", out var type) &&
                                  type.ValueKind == JsonValueKind.String &&
                                  string.Equals(type.GetString(), "LISTEN", StringComparison.OrdinalIgnoreCase);

            if (root.TryGetProperty("transID", out var transId) && transId.ValueKind == JsonValueKind.String)
            {
                var nextTransId = transId.GetString();
                if (!string.IsNullOrWhiteSpace(nextTransId) &&
                    !string.Equals(turnState.TransId, nextTransId, StringComparison.Ordinal))
                {
                    ResetTurnState(turnState, nextTransId);
                    session.LastTransId = nextTransId;
                }
            }

            if (isListenMessage)
            {
                turnState.SawListen = true;
                turnState.ListenOpenedUtc ??= DateTimeOffset.UtcNow;
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return;

            if (data.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array)
            {
                turnState.ListenRules =
                [
                    .. rules.EnumerateArray()
                        .Select(item =>
                            item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : item.ToString())
                        .Where(rule => !string.IsNullOrWhiteSpace(rule))
                ];
                session.Metadata["listenRules"] = turnState.ListenRules;
            }

            if (data.TryGetProperty("asr", out var asr) &&
                asr.ValueKind == JsonValueKind.Object)
            {
                if (asr.TryGetProperty("hints", out var hints) &&
                    hints.ValueKind == JsonValueKind.Array)
                    turnState.ListenAsrHints =
                    [
                        .. hints.EnumerateArray()
                            .Where(static item => item.ValueKind == JsonValueKind.String)
                            .Select(static item => item.GetString() ?? string.Empty)
                            .Where(static hint => !string.IsNullOrWhiteSpace(hint))
                    ];

                if (TryReadMilliseconds(asr, "sosTimeout", out var sosTimeout))
                    turnState.ListenSosTimeout = sosTimeout;

                if (TryReadMilliseconds(asr, "maxSpeechTimeout", out var maxSpeechTimeout))
                    turnState.ListenMaxSpeechTimeout = maxSpeechTimeout;
            }

            if (data.TryGetProperty("hotphrase", out var hotphrase) &&
                hotphrase.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                turnState.ListenHotphrase = hotphrase.GetBoolean();
                turnState.HotphraseEmptyTurnCount = 0;
            }

            if (data.TryGetProperty("intent", out var intent) && intent.ValueKind == JsonValueKind.String)
                session.LastIntent = intent.GetString();

            if (!data.TryGetProperty("transcriptHint", out var transcriptHint) ||
                transcriptHint.ValueKind != JsonValueKind.String) return;

            turnState.AudioTranscriptHint = transcriptHint.GetString();
            session.Metadata["audioTranscriptHint"] = turnState.AudioTranscriptHint;
        }
        catch
        {
            // Keep the compatibility layer permissive while captures are still incomplete.
        }
    }

    private static void ResetBufferedAudio(CloudSession session)
    {
        AudioBufferBudget.Release(session.SessionId);
        session.TurnState.BufferedAudioBytes = 0;
        session.TurnState.BufferedAudioChunkCount = 0;
        session.TurnState.AudioTranscriptHint = null;
        session.TurnState.LastSttError = null;
        session.TurnState.LastSttErrorUtc = null;
        session.TurnState.FirstAudioReceivedUtc = null;
        session.TurnState.LastAudioReceivedUtc = null;
        session.TurnState.LastAutoFinalizeAttemptUtc = null;
        session.TurnState.DeferredIncompleteAudioBytes = 0;
        session.TurnState.BufferedAudioFrames.Clear();
        session.TurnState.FinalizeAttemptCount = 0;
        session.TurnState.AutoFinalizeBlockedUntilUtc = null;
        session.Metadata.Remove("audioTranscriptHint");
    }

    public static void ReleaseBufferedAudio(CloudSession session) => ResetBufferedAudio(session);

    private static void ResetHotphraseOnlyBufferedAudio(CloudSession session)
    {
        var turnState = session.TurnState;
        turnState.AudioTranscriptHint = null;
        turnState.LastSttError = null;
        turnState.LastSttErrorUtc = null;
        turnState.AutoFinalizeBlockedUntilUtc = null;
        turnState.AwaitingTurnCompletion = true;
        turnState.SawListen = true;
        turnState.SawContext = true;
        session.Metadata.Remove("audioTranscriptHint");
    }

    private static void ResetTurnState(WebSocketTurnState turnState, string? transId)
    {
        turnState.TransId = transId;
        turnState.ContextPayload = null;
        turnState.AudioTranscriptHint = null;
        turnState.ListenOpenedUtc = null;
        turnState.LastSttError = null;
        turnState.LastSttErrorUtc = null;
        turnState.FirstAudioReceivedUtc = null;
        turnState.LastAudioReceivedUtc = null;
        turnState.LastAutoFinalizeAttemptUtc = null;
        turnState.DeferredIncompleteAudioBytes = 0;
        turnState.ListenSosTimeout = null;
        turnState.ListenMaxSpeechTimeout = null;
        turnState.BufferedAudioChunkCount = 0;
        turnState.BufferedAudioBytes = 0;
        turnState.BufferedAudioFrames.Clear();
        turnState.FinalizeAttemptCount = 0;
        turnState.AwaitingTurnCompletion = false;
        turnState.SawListen = false;
        turnState.SawContext = false;
        turnState.ListenHotphrase = false;
        turnState.HotphraseEmptyTurnCount = 0;
        turnState.IgnoreAdditionalAudioUntilUtc = null;
        turnState.IgnoreLateListenSetupUntilUtc = null;
        turnState.ListenRules = [];
        turnState.ListenAsrHints = [];
        turnState.AutoFinalizeBlockedUntilUtc = null;
    }

    private bool ShouldFireAndForgetHomeAssistantCommand(TurnContext turn, string intentName)
    {
        if (homeAssistantCommandService is null) return false;

        if (string.Equals(intentName, "ha_lights_off", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(intentName, "ha_lights_on", StringComparison.OrdinalIgnoreCase))
            return !homeAssistantCommandService.IsNamedLightCommand(turn, intentName);

        if (string.Equals(intentName, "ha_climate_set_temp", StringComparison.OrdinalIgnoreCase))
            return !homeAssistantCommandService.IsRoomClimateCommand(turn, intentName);

        return false;
    }

    private static bool TryReadMilliseconds(JsonElement source, string propertyName, out TimeSpan timeout)
    {
        timeout = TimeSpan.Zero;
        if (!source.TryGetProperty(propertyName, out var value)) return false;

        double milliseconds;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number when value.TryGetDouble(out milliseconds):
                break;
            case JsonValueKind.String when double.TryParse(value.GetString(), out milliseconds):
                break;
            default:
                return false;
        }

        if (milliseconds <= 0) return false;
        timeout = TimeSpan.FromMilliseconds(milliseconds);
        return true;
    }

    private async Task<IReadOnlyList<WebSocketReply>> FinalizeTurnAsync(
        CloudSession session,
        WebSocketMessageEnvelope envelope,
        string messageType,
        bool allowFallbackOnMissingTranscript,
        CancellationToken cancellationToken)
    {
        var turnState = session.TurnState;
        if (!turnState.TryBeginFinalization())
        {
            _metrics.TurnFinalizationSuppressed("concurrent");
            logger.LogDebug(
                "Finalize turn ignored because another finalization is active session={SessionId} messageType={MessageType} transId={TransId}",
                session.SessionId,
                messageType,
                turnState.TransId);
            return [];
        }

        var finalizeStarted = Stopwatch.GetTimestamp();
        var finalizeOutcome = "success";
        _metrics.ActiveTurnsChanged(1);

        logger.LogDebug(
            "Finalize turn entered session={SessionId} messageType={MessageType} transId={TransId} allowFallback={AllowFallback} bufferedBytes={BufferedBytes} bufferedChunks={BufferedChunks} awaiting={Awaiting}",
            session.SessionId,
            messageType,
            session.TurnState.TransId,
            allowFallbackOnMissingTranscript,
            session.TurnState.BufferedAudioBytes,
            session.TurnState.BufferedAudioChunkCount,
            session.TurnState.AwaitingTurnCompletion);
        try
        {
            var turn = ProtocolToTurnContextMapper.MapListenMessage(envelope, session, messageType);
            await StoreBufferedAudioArtifactAsync(session, turn, cancellationToken);
            if (IsYesNoTurn(turn) || ReadPrimaryYesNoRule(turn) is not null)
                await sink.RecordTurnDiagnosticAsync("yes_no_turn_received", BuildTurnDiagnosticSnapshot(session,
                    envelope, new Dictionary<string, object?>
                    {
                        ["messageType"] = messageType,
                        ["listenRules"] = ReadRules(turn, "listenRules").ToArray(),
                        ["clientRules"] = ReadRules(turn, "clientRules").ToArray(),
                        ["listenAsrHints"] = ReadRules(turn, "listenAsrHints").ToArray(),
                        ["yesNoRule"] = ReadPrimaryYesNoRule(turn),
                        ["awaitingTurnCompletion"] = turnState.AwaitingTurnCompletion,
                        ["bufferedAudioBytes"] = turnState.BufferedAudioBytes,
                        ["bufferedAudioChunks"] = turnState.BufferedAudioChunkCount,
                        ["sawListen"] = turnState.SawListen,
                        ["sawContext"] = turnState.SawContext,
                        ["followUpOpen"] = session.FollowUpOpen,
                        ["followUpExpiresUtc"] = session.FollowUpExpiresUtc
                    }), cancellationToken);
            if (ShouldIgnoreBlankAudioHotphraseTurn(turn))
            {
                session.TurnState.AwaitingTurnCompletion = false;
                session.TurnState.IgnoreAdditionalAudioUntilUtc =
                    DateTimeOffset.UtcNow.Add(WebSocketTurnState.DefaultLateAudioIgnoreWindow);
                session.FollowUpExpiresUtc = null;
                ResetBufferedAudio(session);
                ClearListenTracking(session.TurnState);
                return [];
            }

            var finalizedTurn = await ResolveTranscriptAsync(turn, session, envelope, cancellationToken);
            if (ShouldCloseHotphraseNonCommandAsNoInput(finalizedTurn, turnState, messageType,
                    allowFallbackOnMissingTranscript, out var hotphraseNonCommandReason))
            {
                turnState.AwaitingTurnCompletion = false;
                session.LastTranscript = string.Empty;
                session.LastIntent = null;
                session.LastListenType = "no-input";
                await sink.RecordTurnDiagnosticAsync("auto_finalize_hotphrase_non_command_no_input",
                    BuildTurnDiagnosticSnapshot(session, envelope, new Dictionary<string, object?>
                    {
                        ["messageType"] = messageType,
                        ["transcript"] = finalizedTurn.NormalizedTranscript ?? finalizedTurn.RawTranscript,
                        ["reason"] = hotphraseNonCommandReason,
                        ["bufferedAudioBytes"] = turnState.BufferedAudioBytes,
                        ["bufferedAudioChunks"] = turnState.BufferedAudioChunkCount
                    }), cancellationToken);
                var noInputReplies = ResponsePlanToSocketMessagesMapper.MapNoInput(
                        turnState.TransId ?? session.LastTransId ?? string.Empty,
                        turnState.ListenRules)
                    .Select(map => new WebSocketReply { Text = map.Text, DelayMs = map.DelayMs })
                    .ToArray();
                ResetBufferedAudio(session);
                ClearListenTracking(turnState);
                turnState.IgnoreAdditionalAudioUntilUtc =
                    DateTimeOffset.UtcNow.Add(WebSocketTurnState.DefaultLateAudioIgnoreWindow);
                return noInputReplies;
            }

            if (!TryGetUsableTranscript(finalizedTurn, out var usableTranscript))
                finalizedTurn = new TurnContext
                {
                    TurnId = finalizedTurn.TurnId,
                    SessionId = finalizedTurn.SessionId,
                    TimestampUtc = finalizedTurn.TimestampUtc,
                    InputMode = finalizedTurn.InputMode,
                    SourceKind = finalizedTurn.SourceKind,
                    WakePhrase = finalizedTurn.WakePhrase,
                    RawTranscript = null,
                    NormalizedTranscript = null,
                    DeviceId = finalizedTurn.DeviceId,
                    HostName = finalizedTurn.HostName,
                    RequestId = finalizedTurn.RequestId,
                    ProtocolService = finalizedTurn.ProtocolService,
                    ProtocolOperation = finalizedTurn.ProtocolOperation,
                    FirmwareVersion = finalizedTurn.FirmwareVersion,
                    ApplicationVersion = finalizedTurn.ApplicationVersion,
                    Locale = finalizedTurn.Locale,
                    TimeZone = finalizedTurn.TimeZone,
                    IsFollowUpEligible = finalizedTurn.IsFollowUpEligible,
                    Attributes = finalizedTurn.Attributes
                };
            else if (string.Equals(messageType, "CLIENT_ASR", StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(usableTranscript, finalizedTurn.NormalizedTranscript ?? finalizedTurn.RawTranscript,
                         StringComparison.Ordinal))
                finalizedTurn = WithSanitizedTranscript(finalizedTurn, usableTranscript);

            if (ShouldIgnoreLateHotphraseTurn(session, finalizedTurn))
            {
                turnState.AwaitingTurnCompletion = false;
                session.FollowUpExpiresUtc = null;
                ResetBufferedAudio(session);
                ClearListenTracking(turnState);
                return [];
            }

            if (ShouldTreatBufferedHotphraseAsGreeting(finalizedTurn, turnState, allowFallbackOnMissingTranscript))
            {
                await sink.RecordTurnDiagnosticAsync(
                    "hotphrase_greeting_synthesized",
                    BuildTurnDiagnosticSnapshot(session, envelope, new Dictionary<string, object?>
                    {
                        ["messageType"] = messageType,
                        ["reason"] = "buffered_hotphrase_missing_transcript",
                        ["listenHotphrase"] = ReadBoolAttribute(finalizedTurn, "listenHotphrase"),
                        ["listenRules"] = ReadRules(finalizedTurn, "listenRules").ToArray(),
                        ["bufferedAudioBytes"] = turnState.BufferedAudioBytes,
                        ["bufferedAudioChunks"] = turnState.BufferedAudioChunkCount,
                        ["lastSttError"] = turnState.LastSttError,
                        ["allowFallbackOnMissingTranscript"] = allowFallbackOnMissingTranscript
                    }),
                    cancellationToken);
                finalizedTurn = WithSyntheticTranscript(finalizedTurn, "hello");
            }

            if (ShouldIgnoreCompletedWordOfDayTurn(finalizedTurn))
            {
                turnState.AwaitingTurnCompletion = false;
                turnState.IgnoreAdditionalAudioUntilUtc =
                    DateTimeOffset.UtcNow.Add(WebSocketTurnState.DefaultLateAudioIgnoreWindow);
                session.FollowUpExpiresUtc = null;
                ResetBufferedAudio(session);
                ClearListenTracking(turnState);
                return
                [
                    .. ResponsePlanToSocketMessagesMapper.MapNoInput(
                            turnState.TransId ?? session.LastTransId ?? string.Empty,
                            ["word-of-the-day/right_word"])
                        .Select(map => new WebSocketReply
                        {
                            Text = map.Text,
                            DelayMs = map.DelayMs
                        })
                ];
            }

            if (ShouldIgnoreLateEmptyTurn(finalizedTurn, session, messageType))
            {
                turnState.AwaitingTurnCompletion = false;
                ResetBufferedAudio(session);
                ClearListenTracking(turnState);
                return [];
            }

            if (ShouldHandleAsLocalNoInput(finalizedTurn))
            {
                var turnAge = turnState.FirstAudioReceivedUtc.HasValue
                    ? DateTimeOffset.UtcNow - turnState.FirstAudioReceivedUtc.Value
                    : TimeSpan.Zero;
                if (ShouldKeepOpenOggYesNoBlankBeforeHardTimeout(finalizedTurn, turnState, messageType,
                        allowFallbackOnMissingTranscript, turnAge))
                {
                    turnState.AwaitingTurnCompletion = true;
                    turnState.FinalizeAttemptCount += 1;
                    await sink.RecordTurnDiagnosticAsync("auto_finalize_deferred_for_yes_no_blank_ogg",
                        BuildTurnDiagnosticSnapshot(session, envelope, new Dictionary<string, object?>
                        {
                            ["messageType"] = messageType,
                            ["finalizeAttemptCount"] = turnState.FinalizeAttemptCount,
                            ["turnAgeMs"] = (int)turnAge.TotalMilliseconds,
                            ["bufferedAudioBytes"] = turnState.BufferedAudioBytes,
                            ["bufferedAudioChunks"] = turnState.BufferedAudioChunkCount
                        }), cancellationToken);
                    return [];
                }

                if (IsYesNoTurn(finalizedTurn))
                    await sink.RecordTurnDiagnosticAsync("yes_no_no_input", BuildTurnDiagnosticSnapshot(session,
                        envelope, new Dictionary<string, object?>
                        {
                            ["messageType"] = messageType,
                            ["listenRules"] = ReadRules(finalizedTurn, "listenRules").ToArray(),
                            ["clientRules"] = ReadRules(finalizedTurn, "clientRules").ToArray(),
                            ["listenAsrHints"] = ReadRules(finalizedTurn, "listenAsrHints").ToArray(),
                            ["awaitingTurnCompletion"] = turnState.AwaitingTurnCompletion,
                            ["bufferedAudioBytes"] = turnState.BufferedAudioBytes,
                            ["bufferedAudioChunks"] = turnState.BufferedAudioChunkCount,
                            ["sawListen"] = turnState.SawListen,
                            ["sawContext"] = turnState.SawContext,
                            ["followUpOpen"] = session.FollowUpOpen
                        }), cancellationToken);
                turnState.AwaitingTurnCompletion = false;
                session.LastTranscript = string.Empty;
                session.LastIntent = null;
                session.LastListenType = "no-input";
                var localRule = ReadPrimaryNoInputRule(finalizedTurn);
                var noInputReplies = BuildLocalNoInputReplies(session, turnState, localRule);
                ResetBufferedAudio(session);
                ClearListenTracking(turnState);
                return noInputReplies;
            }

            if (ShouldIgnoreInitialEmptyHotphraseTurn(finalizedTurn, turnState))
            {
                turnState.HotphraseEmptyTurnCount += 1;
                turnState.AwaitingTurnCompletion = true;
                return [];
            }

            if (ShouldTreatEmptyHotphraseTurnAsGreeting(finalizedTurn, turnState))
            {
                await sink.RecordTurnDiagnosticAsync(
                    "hotphrase_greeting_synthesized",
                    BuildTurnDiagnosticSnapshot(session, envelope, new Dictionary<string, object?>
                    {
                        ["messageType"] = messageType,
                        ["reason"] = "empty_hotphrase_turn",
                        ["listenHotphrase"] = ReadBoolAttribute(finalizedTurn, "listenHotphrase"),
                        ["listenRules"] = ReadRules(finalizedTurn, "listenRules").ToArray(),
                        ["bufferedAudioBytes"] = turnState.BufferedAudioBytes,
                        ["bufferedAudioChunks"] = turnState.BufferedAudioChunkCount,
                        ["lastSttError"] = turnState.LastSttError
                    }),
                    cancellationToken);
                finalizedTurn = WithSyntheticTranscript(finalizedTurn, "hello");
            }

            if (ShouldIgnoreLateEmptyTurn(finalizedTurn, session, messageType))
            {
                turnState.AwaitingTurnCompletion = false;
                ResetBufferedAudio(session);
                ClearListenTracking(turnState);
                return [];
            }

            var allowEmptyTranscriptForPersonalReport = IsActivePersonalReportTurn(finalizedTurn);
            var allowEmptyTranscriptForTrigger = string.Equals(ReadMessageType(finalizedTurn), "TRIGGER",
                StringComparison.OrdinalIgnoreCase);
            if (!allowEmptyTranscriptForPersonalReport &&
                !allowEmptyTranscriptForTrigger &&
                string.IsNullOrWhiteSpace(finalizedTurn.NormalizedTranscript) &&
                string.IsNullOrWhiteSpace(finalizedTurn.RawTranscript))
            {
                turnState.AwaitingTurnCompletion = true;
                turnState.FinalizeAttemptCount += 1;

                var turnAge = turnState.FirstAudioReceivedUtc.HasValue
                    ? DateTimeOffset.UtcNow - turnState.FirstAudioReceivedUtc.Value
                    : TimeSpan.Zero;

                if (ShouldKeepOpenOggHotphraseBlankBeforeHardTimeout(finalizedTurn, turnState, messageType,
                        allowFallbackOnMissingTranscript, turnAge))
                    return [];

                if (ShouldKeepOpenOggYesNoBlankBeforeHardTimeout(finalizedTurn, turnState, messageType,
                        allowFallbackOnMissingTranscript, turnAge))
                {
                    await sink.RecordTurnDiagnosticAsync("auto_finalize_deferred_for_yes_no_blank_ogg",
                        BuildTurnDiagnosticSnapshot(session, envelope, new Dictionary<string, object?>
                        {
                            ["messageType"] = messageType,
                            ["finalizeAttemptCount"] = turnState.FinalizeAttemptCount,
                            ["turnAgeMs"] = (int)turnAge.TotalMilliseconds,
                            ["bufferedAudioBytes"] = turnState.BufferedAudioBytes,
                            ["bufferedAudioChunks"] = turnState.BufferedAudioChunkCount
                        }), cancellationToken);
                    return [];
                }

                if (ShouldCloseEmptyHotphraseAutoFinalize(finalizedTurn, turnState, messageType,
                        allowFallbackOnMissingTranscript, turnAge))
                {
                    turnState.AwaitingTurnCompletion = false;
                    session.LastTranscript = string.Empty;
                    session.LastIntent = null;
                    session.LastListenType = "no-input";
                    await sink.RecordTurnDiagnosticAsync("auto_finalize_hotphrase_no_input",
                        BuildTurnDiagnosticSnapshot(session, envelope, new Dictionary<string, object?>
                        {
                            ["messageType"] = messageType,
                            ["finalizeAttemptCount"] = turnState.FinalizeAttemptCount,
                            ["turnAgeMs"] = (int)turnAge.TotalMilliseconds,
                            ["bufferedAudioBytes"] = turnState.BufferedAudioBytes,
                            ["bufferedAudioChunks"] = turnState.BufferedAudioChunkCount,
                            ["lastSttError"] = turnState.LastSttError
                        }), cancellationToken);
                    var noInputReplies = ResponsePlanToSocketMessagesMapper.MapNoInput(
                            turnState.TransId ?? session.LastTransId ?? string.Empty,
                            turnState.ListenRules)
                        .Select(map => new WebSocketReply { Text = map.Text, DelayMs = map.DelayMs })
                        .ToArray();
                    ResetBufferedAudio(session);
                    ClearListenTracking(turnState);
                    return noInputReplies;
                }

                switch (allowFallbackOnMissingTranscript)
                {
                    case true when
                        turnState.FinalizeAttemptCount >= 2 &&
                        turnAge >= AutoFinalizeMissingTranscriptFallbackAge:
                    {
                        turnState.AwaitingTurnCompletion = false;
                        session.LastTranscript = string.Empty;
                        session.LastIntent = "heyJibo";
                        session.LastListenType = "fallback";
                        await sink.RecordTurnDiagnosticAsync("auto_finalize_forced_fallback",
                            BuildTurnDiagnosticSnapshot(session, envelope, new Dictionary<string, object?>
                            {
                                ["messageType"] = messageType,
                                ["finalizeAttemptCount"] = turnState.FinalizeAttemptCount,
                                ["turnAgeMs"] = (int)turnAge.TotalMilliseconds,
                                ["bufferedAudioBytes"] = turnState.BufferedAudioBytes,
                                ["bufferedAudioChunks"] = turnState.BufferedAudioChunkCount,
                                ["lastSttError"] = turnState.LastSttError
                            }), cancellationToken);
                        var fallbackReplies = ResponsePlanToSocketMessagesMapper
                            .MapFallback(turnState.TransId ?? session.LastTransId ?? string.Empty,
                                turnState.ListenRules)
                            .Select(map => new WebSocketReply { Text = map.Text, DelayMs = map.DelayMs })
                            .ToArray();
                        ResetBufferedAudio(session);
                        ClearListenTracking(turnState);
                        return fallbackReplies;
                    }
                    case true when
                        turnState.BufferedAudioBytes >= AutoFinalizeMinBufferedAudioBytes &&
                        IsYesNoTurn(finalizedTurn):
                    {
                        turnState.AwaitingTurnCompletion = false;
                        session.LastTranscript = string.Empty;
                        session.LastIntent = null;
                        session.LastListenType = "no-input";
                        var localRule = ReadPrimaryYesNoRule(finalizedTurn);
                        var noInputReplies = BuildLocalNoInputReplies(session, turnState, localRule);
                        ResetBufferedAudio(session);
                        ClearListenTracking(turnState);
                        return noInputReplies;
                    }
                    case true when
                        turnState.BufferedAudioBytes >= AutoFinalizeMinBufferedAudioBytes &&
                        (!ReadBoolAttribute(finalizedTurn, "listenHotphrase") ||
                         turnState.HotphraseEmptyTurnCount > 0) &&
                        string.IsNullOrWhiteSpace(turnState.LastSttError):
                    {
                        turnState.AwaitingTurnCompletion = false;
                        session.LastTranscript = string.Empty;
                        session.LastIntent = "heyJibo";
                        session.LastListenType = "fallback";
                        var fallbackReplies = ResponsePlanToSocketMessagesMapper
                            .MapFallback(turnState.TransId ?? session.LastTransId ?? string.Empty,
                                turnState.ListenRules)
                            .Select(map => new WebSocketReply { Text = map.Text, DelayMs = map.DelayMs })
                            .ToArray();
                        ResetBufferedAudio(session);
                        ClearListenTracking(turnState);
                        return fallbackReplies;
                    }
                    default:
                        if (string.Equals(messageType, "AUTO_FINALIZE", StringComparison.OrdinalIgnoreCase))
                            turnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow;
                        return [];
                }
            }

            if (ShouldWaitForBufferedHotphraseOnlyTranscript(finalizedTurn, turnState, messageType,
                    allowFallbackOnMissingTranscript, out var hotphraseOnlyReason))
            {
                turnState.AwaitingTurnCompletion = true;
                turnState.FinalizeAttemptCount += 1;
                var turnAge = turnState.FirstAudioReceivedUtc.HasValue
                    ? DateTimeOffset.UtcNow - turnState.FirstAudioReceivedUtc.Value
                    : TimeSpan.Zero;
                await sink.RecordTurnDiagnosticAsync("auto_finalize_deferred_for_hotphrase_only",
                    BuildTurnDiagnosticSnapshot(session, envelope, new Dictionary<string, object?>
                    {
                        ["messageType"] = messageType,
                        ["transcript"] = finalizedTurn.NormalizedTranscript ?? finalizedTurn.RawTranscript,
                        ["reason"] = hotphraseOnlyReason,
                        ["finalizeAttemptCount"] = turnState.FinalizeAttemptCount,
                        ["turnAgeMs"] = (int)turnAge.TotalMilliseconds,
                        ["bufferedAudioBytes"] = turnState.BufferedAudioBytes,
                        ["bufferedAudioChunks"] = turnState.BufferedAudioChunkCount
                    }), cancellationToken);

                var noInputAge = ResolveHotphraseOnlyNoInputAge(turnState);
                if (turnAge < noInputAge)
                {
                    ResetHotphraseOnlyBufferedAudio(session);
                    return [];
                }

                turnState.AwaitingTurnCompletion = false;
                session.LastTranscript = string.Empty;
                session.LastIntent = null;
                session.LastListenType = "no-input";
                await sink.RecordTurnDiagnosticAsync("auto_finalize_hotphrase_only_no_input",
                    BuildTurnDiagnosticSnapshot(session, envelope, new Dictionary<string, object?>
                    {
                        ["messageType"] = messageType,
                        ["transcript"] = finalizedTurn.NormalizedTranscript ?? finalizedTurn.RawTranscript,
                        ["reason"] = hotphraseOnlyReason,
                        ["finalizeAttemptCount"] = turnState.FinalizeAttemptCount,
                        ["turnAgeMs"] = (int)turnAge.TotalMilliseconds,
                        ["maxSpeechTimeoutMs"] = (int)noInputAge.TotalMilliseconds,
                        ["bufferedAudioBytes"] = turnState.BufferedAudioBytes,
                        ["bufferedAudioChunks"] = turnState.BufferedAudioChunkCount
                    }), cancellationToken);
                var noInputReplies = ResponsePlanToSocketMessagesMapper.MapNoInput(
                        turnState.TransId ?? session.LastTransId ?? string.Empty,
                        turnState.ListenRules)
                    .Select(map => new WebSocketReply { Text = map.Text, DelayMs = map.DelayMs })
                    .ToArray();
                ResetBufferedAudio(session);
                ClearListenTracking(turnState);
                return noInputReplies;
            }

            finalizedTurn = CompleteIncompleteLaunchTranscriptIfClosing(
                finalizedTurn,
                turnState,
                messageType,
                allowFallbackOnMissingTranscript);

            if (ShouldDeferForIncompleteHotphraseLaunchCommand(finalizedTurn, turnState, messageType,
                    allowFallbackOnMissingTranscript, out var incompleteCommandReason))
            {
                turnState.AwaitingTurnCompletion = true;
                turnState.FinalizeAttemptCount += 1;
                // Block STT retry storms until more audio arrives. Early probes at
                // 100–400ms were re-transcribing the same truncated buffer and either
                // burned ~3s or eventually finalized incomplete text into the wrong skill.
                turnState.DeferredIncompleteAudioBytes = turnState.BufferedAudioBytes;
                turnState.LastAutoFinalizeAttemptUtc = DateTimeOffset.UtcNow;
                var turnAge = turnState.FirstAudioReceivedUtc.HasValue
                    ? DateTimeOffset.UtcNow - turnState.FirstAudioReceivedUtc.Value
                    : TimeSpan.Zero;
                await sink.RecordTurnDiagnosticAsync("auto_finalize_deferred_for_incomplete_hotphrase_command",
                    BuildTurnDiagnosticSnapshot(session, envelope, new Dictionary<string, object?>
                    {
                        ["messageType"] = messageType,
                        ["transcript"] = finalizedTurn.NormalizedTranscript ?? finalizedTurn.RawTranscript,
                        ["reason"] = incompleteCommandReason,
                        ["finalizeAttemptCount"] = turnState.FinalizeAttemptCount,
                        ["turnAgeMs"] = (int)turnAge.TotalMilliseconds,
                        ["bufferedAudioBytes"] = turnState.BufferedAudioBytes,
                        ["bufferedAudioChunks"] = turnState.BufferedAudioChunkCount,
                        ["deferredIncompleteAudioBytes"] = turnState.DeferredIncompleteAudioBytes
                    }), cancellationToken);
                return [];
            }

            if (ShouldHandleUnexpectedYesNoAutoFinalizeTranscript(finalizedTurn, turnState, messageType,
                    allowFallbackOnMissingTranscript, out var unexpectedYesNoReason, out var closeYesNoAsNoInput,
                    out var unexpectedYesNoTurnAge))
            {
                if (closeYesNoAsNoInput)
                {
                    turnState.AwaitingTurnCompletion = false;
                    session.LastTranscript = string.Empty;
                    session.LastIntent = null;
                    session.LastListenType = "no-input";
                    await sink.RecordTurnDiagnosticAsync("auto_finalize_yes_no_unexpected_transcript_no_input",
                        BuildTurnDiagnosticSnapshot(session, envelope, new Dictionary<string, object?>
                        {
                            ["messageType"] = messageType,
                            ["transcript"] = finalizedTurn.NormalizedTranscript ?? finalizedTurn.RawTranscript,
                            ["reason"] = unexpectedYesNoReason,
                            ["turnAgeMs"] = (int)unexpectedYesNoTurnAge.TotalMilliseconds,
                            ["bufferedAudioBytes"] = turnState.BufferedAudioBytes,
                            ["bufferedAudioChunks"] = turnState.BufferedAudioChunkCount
                        }), cancellationToken);
                    var localRule = ReadPrimaryNoInputRule(finalizedTurn);
                    var noInputReplies = BuildLocalNoInputReplies(session, turnState, localRule);
                    ResetBufferedAudio(session);
                    ClearListenTracking(turnState);
                    return noInputReplies;
                }

                turnState.AwaitingTurnCompletion = true;
                turnState.FinalizeAttemptCount += 1;
                await sink.RecordTurnDiagnosticAsync("auto_finalize_deferred_for_unexpected_yes_no_transcript",
                    BuildTurnDiagnosticSnapshot(session, envelope, new Dictionary<string, object?>
                    {
                        ["messageType"] = messageType,
                        ["transcript"] = finalizedTurn.NormalizedTranscript ?? finalizedTurn.RawTranscript,
                        ["reason"] = unexpectedYesNoReason,
                        ["finalizeAttemptCount"] = turnState.FinalizeAttemptCount,
                        ["turnAgeMs"] = (int)unexpectedYesNoTurnAge.TotalMilliseconds,
                        ["bufferedAudioBytes"] = turnState.BufferedAudioBytes,
                        ["bufferedAudioChunks"] = turnState.BufferedAudioChunkCount
                    }), cancellationToken);
                return [];
            }

            if (TryExtractTrailingYesNoReply(finalizedTurn, out var trailingYesNoReply))
            {
                await sink.RecordTurnDiagnosticAsync("yes_no_prompt_echo_trimmed",
                    BuildTurnDiagnosticSnapshot(session, envelope, new Dictionary<string, object?>
                    {
                        ["messageType"] = messageType,
                        ["transcript"] = finalizedTurn.NormalizedTranscript ?? finalizedTurn.RawTranscript,
                        ["trimmedTranscript"] = trailingYesNoReply,
                        ["bufferedAudioBytes"] = turnState.BufferedAudioBytes,
                        ["bufferedAudioChunks"] = turnState.BufferedAudioChunkCount
                    }), cancellationToken);
                finalizedTurn = WithSanitizedTranscript(finalizedTurn, trailingYesNoReply);
            }

            if (ShouldDeferForLikelyContinuation(finalizedTurn, turnState, messageType,
                    allowFallbackOnMissingTranscript, out var deferralReason))
            {
                turnState.AwaitingTurnCompletion = true;
                turnState.FinalizeAttemptCount += 1;
                var turnAge = turnState.FirstAudioReceivedUtc.HasValue
                    ? DateTimeOffset.UtcNow - turnState.FirstAudioReceivedUtc.Value
                    : TimeSpan.Zero;
                await sink.RecordTurnDiagnosticAsync("auto_finalize_deferred_for_continuation",
                    BuildTurnDiagnosticSnapshot(session, envelope, new Dictionary<string, object?>
                    {
                        ["messageType"] = messageType,
                        ["transcript"] = finalizedTurn.NormalizedTranscript ?? finalizedTurn.RawTranscript,
                        ["reason"] = deferralReason,
                        ["finalizeAttemptCount"] = turnState.FinalizeAttemptCount,
                        ["turnAgeMs"] = (int)turnAge.TotalMilliseconds,
                        ["bufferedAudioBytes"] = turnState.BufferedAudioBytes,
                        ["bufferedAudioChunks"] = turnState.BufferedAudioChunkCount
                    }), cancellationToken);
                return [];
            }

            AmbientTurnProgressPublisher.BindTurn(finalizedTurn, session);
            var plan = await MeasurePhaseAsync("plan",
                () => conversationBroker.HandleTurnAsync(finalizedTurn, cancellationToken), cancellationToken);

            var intentName = plan.IntentName;

            if (intentName == null)
            {
                logger.LogWarning(
                    "Could not determine intent for the finalized turn with transcript: {Transcript}, defaulting to chat",
                    finalizedTurn.NormalizedTranscript ?? finalizedTurn.RawTranscript);
                intentName = "chat";
            }

            if (homeAssistantCommandService is not null &&
                ShouldFireAndForgetHomeAssistantCommand(finalizedTurn, intentName))
            {
                var dispatched = string.Equals(intentName, "ha_lights_off", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(intentName, "ha_lights_on", StringComparison.OrdinalIgnoreCase)
                    ? await homeAssistantCommandService.TryDispatchLightCommandAsync(
                        finalizedTurn,
                        intentName,
                        cancellationToken)
                    : await homeAssistantCommandService.TryDispatchClimateCommandAsync(
                        finalizedTurn,
                        intentName,
                        cancellationToken);
                await sink.RecordTurnDiagnosticAsync(
                    "home_assistant_command_dispatch",
                    BuildTurnDiagnosticSnapshot(session, envelope, new Dictionary<string, object?>
                    {
                        ["intent"] = intentName,
                        ["transcript"] = finalizedTurn.NormalizedTranscript ?? finalizedTurn.RawTranscript,
                        ["dispatched"] = dispatched
                    }),
                    cancellationToken);
            }

            var listenAction = plan.Actions.OfType<ListenAction>().OrderBy(action => action.Sequence).LastOrDefault();
            session.LastTranscript = finalizedTurn.NormalizedTranscript ?? finalizedTurn.RawTranscript;
            session.LastIntent = plan.IntentName;
            session.LastListenType = listenAction?.Mode;
            turnState.LastLocalNoInputRule = null;
            turnState.LocalNoInputCount = 0;
            if (plan.Actions.OfType<InvokeNativeSkillAction>().FirstOrDefault() is
                    { SkillName: "@be/clock" } clockAction &&
                clockAction.Payload.TryGetValue("domain", out var lastClockDomainValue) &&
                lastClockDomainValue is not null)
                session.Metadata["lastClockDomain"] = lastClockDomainValue.ToString();

            UpdatePendingProactivityOffer(session, plan.IntentName);
            await ApplyContextUpdatesAsync(session, plan.ContextUpdates, envelope, plan.IntentName, cancellationToken);

            var invokedSkillAction = plan.Actions.OfType<InvokeNativeSkillAction>().FirstOrDefault();
            PersistInvokedSkillId(session, invokedSkillAction, plan.FollowUp.KeepMicOpen);
            if ((string.Equals(plan.IntentName, "weather", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(plan.IntentName, "news", StringComparison.OrdinalIgnoreCase)) &&
                invokedSkillAction is not null)
            {
                await sink.RecordTurnDiagnosticAsync(
                    "skill_payload_summary",
                    BuildTurnDiagnosticSnapshot(session, envelope, new Dictionary<string, object?>
                    {
                        ["intent"] = intentName,
                        ["skillName"] = invokedSkillAction.SkillName,
                        ["transcript"] = finalizedTurn.NormalizedTranscript ?? finalizedTurn.RawTranscript,
                        ["payload"] = invokedSkillAction.Payload
                    }),
                    cancellationToken);

                if (string.Equals(plan.IntentName, "news", StringComparison.OrdinalIgnoreCase) &&
                    invokedSkillAction.Payload.TryGetValue("news_provider_status", out var providerStatus))
                {
                    invokedSkillAction.Payload.TryGetValue("news_provider_requested_headlines",
                        out var requestedHeadlines);
                    invokedSkillAction.Payload.TryGetValue("news_provider_resolved_headlines",
                        out var resolvedHeadlines);
                    invokedSkillAction.Payload.TryGetValue("news_provider_preferred_categories",
                        out var preferredCategories);
                    invokedSkillAction.Payload.TryGetValue("news_source", out var newsSource);
                    invokedSkillAction.Payload.TryGetValue("news_provider_message", out var providerMessage);
                    invokedSkillAction.Payload.TryGetValue("news_provider_http_status", out var providerHttpStatus);
                    invokedSkillAction.Payload.TryGetValue("news_provider_endpoint", out var providerEndpoint);
                    invokedSkillAction.Payload.TryGetValue("news_provider_error_code", out var providerErrorCode);

                    await sink.RecordTurnDiagnosticAsync(
                        "news_provider_trace",
                        BuildTurnDiagnosticSnapshot(session, envelope, new Dictionary<string, object?>
                        {
                            ["intent"] = intentName,
                            ["skillName"] = invokedSkillAction.SkillName,
                            ["status"] = providerStatus,
                            ["requestedHeadlines"] = requestedHeadlines,
                            ["resolvedHeadlines"] = resolvedHeadlines,
                            ["preferredCategories"] = preferredCategories,
                            ["source"] = newsSource,
                            ["providerMessage"] = providerMessage,
                            ["providerHttpStatus"] = providerHttpStatus,
                            ["providerEndpoint"] = providerEndpoint,
                            ["providerErrorCode"] = providerErrorCode
                        }),
                        cancellationToken);
                }
            }

            session.FollowUpExpiresUtc = plan.FollowUp.KeepMicOpen
                ? DateTimeOffset.UtcNow.Add(plan.FollowUp.Timeout)
                : null;
            turnState.AwaitingTurnCompletion = false;
            var finalizedAtUtc = DateTimeOffset.UtcNow;
            turnState.IgnoreAdditionalAudioUntilUtc = plan.FollowUp.KeepMicOpen
                ? null
                : finalizedAtUtc.Add(ResolveLateAudioIgnoreWindow(plan));
            turnState.IgnoreLateListenSetupUntilUtc = plan.FollowUp.KeepMicOpen
                ? null
                : finalizedAtUtc.Add(WebSocketTurnState.LateListenSetupIgnoreWindow);

            await sink.RecordTurnDiagnosticAsync(
                "turn_finalize_plan",
                BuildTurnDiagnosticSnapshot(session, envelope, new Dictionary<string, object?>
                {
                    ["messageType"] = messageType,
                    ["intent"] = intentName,
                    ["actionCount"] = plan.Actions.Count,
                    ["actionTypes"] = plan.Actions.Select(action => action.GetType().Name).ToArray(),
                    ["keepMicOpen"] = plan.FollowUp.KeepMicOpen,
                    ["followUpTimeoutMs"] = plan.FollowUp.KeepMicOpen
                        ? (int)plan.FollowUp.Timeout.TotalMilliseconds
                        : null,
                    ["followUpOpen"] = session.FollowUpOpen,
                    ["lastTranscript"] = finalizedTurn.NormalizedTranscript ?? finalizedTurn.RawTranscript
                }),
                cancellationToken);
            logger.LogDebug(
                "Finalize turn plan session={SessionId} messageType={MessageType} intent={Intent} actionCount={ActionCount} keepMicOpen={KeepMicOpen} followUpOpen={FollowUpOpen}",
                session.SessionId,
                messageType,
                plan.IntentName,
                plan.Actions.Count,
                plan.FollowUp.KeepMicOpen,
                session.FollowUpOpen);

            var emitSkillActions =
                !string.Equals(plan.IntentName, "word_of_the_day", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(plan.IntentName, "radio", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(plan.IntentName, "radio_genre", StringComparison.OrdinalIgnoreCase) &&
                !IsBadAppleSkillLaunch(plan) &&
                !string.Equals(plan.IntentName, "stop", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(plan.IntentName, "volume_up", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(plan.IntentName, "volume_down", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(plan.IntentName, "volume_to_value", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(plan.IntentName, "volume_query", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(plan.IntentName, "time", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(plan.IntentName, "date", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(plan.IntentName, "day", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(plan.IntentName, "clock_open", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(plan.IntentName, "clock_menu", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(plan.IntentName, "timer_menu", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(plan.IntentName, "alarm_menu", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(plan.IntentName, "timer_delete", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(plan.IntentName, "alarm_delete", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(plan.IntentName, "timer_cancel", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(plan.IntentName, "alarm_cancel", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(plan.IntentName, "timer_clarify", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(plan.IntentName, "alarm_clarify", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(plan.IntentName, "timer_value", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(plan.IntentName, "alarm_value", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(plan.IntentName, "photo_gallery", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(plan.IntentName, "snapshot", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(plan.IntentName, "photobooth", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(plan.IntentName, "skill_listen", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(plan.IntentName, "prompt_echo", StringComparison.OrdinalIgnoreCase) &&
                !SkillListenOwnership.ShouldSuppressCompetingSpeech(finalizedTurn, plan.IntentName) &&
                (messageType != "CLIENT_NLU" ||
                 string.Equals(plan.IntentName, "word_of_the_day_guess", StringComparison.OrdinalIgnoreCase) ||
                 IsCloudOwnedPersonalReportIntent(plan.IntentName) ||
                 ShouldSpeakCloudOwnedClientNlu(finalizedTurn, plan.IntentName));
            var replies = ResponsePlanToSocketMessagesMapper.Map(plan, finalizedTurn, session, emitSkillActions)
                .Select(map => new WebSocketReply
                {
                    Text = map.Text,
                    DelayMs = map.DelayMs
                }).ToArray();
            var hasEndOfStream = replies.Any(reply => string.Equals(ReadReplyType(reply), "EOS",
                StringComparison.OrdinalIgnoreCase));
            _metrics.TurnRepliesEmitted(replies.Length, hasEndOfStream);

            await sink.RecordTurnDiagnosticAsync(
                "turn_reply_emitted",
                BuildTurnDiagnosticSnapshot(session, envelope, new Dictionary<string, object?>
                {
                    ["messageType"] = messageType,
                    ["intent"] = intentName,
                    ["replyCount"] = replies.Length,
                    ["replyTypes"] = ReadReplyTypes(replies),
                    ["hasEos"] = hasEndOfStream,
                    ["keepMicOpen"] = plan.FollowUp.KeepMicOpen,
                    ["followUpOpen"] = session.FollowUpOpen,
                    ["lastTranscript"] = finalizedTurn.NormalizedTranscript ?? finalizedTurn.RawTranscript
                }),
                cancellationToken);
            logger.LogDebug(
                "Finalize turn replies emitted session={SessionId} messageType={MessageType} intent={Intent} replyCount={ReplyCount} hasEos={HasEos} replyTypes={ReplyTypes}",
                session.SessionId,
                messageType,
                plan.IntentName,
                replies.Length,
                hasEndOfStream,
                ReadReplyTypes(replies));

            if (IsYesNoTurn(finalizedTurn))
                await sink.RecordTurnDiagnosticAsync("yes_no_turn_resolved", BuildTurnDiagnosticSnapshot(session,
                    envelope, new Dictionary<string, object?>
                    {
                        ["messageType"] = messageType,
                        ["transcript"] = finalizedTurn.NormalizedTranscript ?? finalizedTurn.RawTranscript,
                        ["intent"] = intentName,
                        ["listenRules"] = ReadRules(finalizedTurn, "listenRules").ToArray(),
                        ["clientRules"] = ReadRules(finalizedTurn, "clientRules").ToArray(),
                        ["listenAsrHints"] = ReadRules(finalizedTurn, "listenAsrHints").ToArray(),
                        ["awaitingTurnCompletion"] = turnState.AwaitingTurnCompletion,
                        ["bufferedAudioBytes"] = turnState.BufferedAudioBytes,
                        ["bufferedAudioChunks"] = turnState.BufferedAudioChunkCount,
                        ["followUpOpen"] = session.FollowUpOpen,
                        ["followUpExpiresUtc"] = session.FollowUpExpiresUtc
                    }), cancellationToken);

            ResetBufferedAudio(session);
            ClearListenTracking(turnState);
            return replies;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            finalizeOutcome = "canceled";
            throw;
        }
        catch
        {
            finalizeOutcome = "failure";
            throw;
        }
        finally
        {
            _metrics.TurnPhaseCompleted("finalize", finalizeOutcome,
                Stopwatch.GetElapsedTime(finalizeStarted).TotalMilliseconds);
            _metrics.ActiveTurnsChanged(-1);
            turnState.EndFinalization();
            await TrackGlsmPhaseAsync(session, envelope, $"finalize:{messageType}", cancellationToken);
            logger.LogDebug("Finalize turn exit session={SessionId} messageType={MessageType} transId={TransId}",
                session.SessionId,
                messageType,
                session.TurnState.TransId);
        }
    }

    private async Task<T> MeasurePhaseAsync<T>(string phase, Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await operation();
            _metrics.TurnPhaseCompleted(phase, "success", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _metrics.TurnPhaseCompleted(phase, "canceled", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
        catch
        {
            _metrics.TurnPhaseCompleted(phase, "failure", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
    }

    private async Task StoreBufferedAudioArtifactAsync(CloudSession session, TurnContext turn,
        CancellationToken cancellationToken)
    {
        if (mediaContentStore is null || session.TurnState.BufferedAudioFrames.Count == 0) return;

        var rawContent = session.TurnState.BufferedAudioFrames.SelectMany(frame => frame).ToArray();
        var isOgg = rawContent.Length >= 4 && rawContent.AsSpan(0, 4).SequenceEqual("OggS"u8);
        var content = rawContent;
        if (isOgg)
        {
            try
            {
                content = Audio.OggOpusAudioNormalizer.Normalize(session.TurnState.BufferedAudioFrames);
            }
            catch (InvalidOperationException exception)
            {
                logger.LogWarning(exception,
                    "Could not normalize buffered Ogg audio for artifact storage; retaining the raw WebSocket bytes. session={SessionId} turn={TurnId}",
                    session.SessionId, turn.TurnId);
                isOgg = false;
            }
        }
        if (content.Length == 0) return;

        var registeredDeviceId = session.Metadata.TryGetValue("registeredDeviceId", out var registeredValue)
            ? registeredValue?.ToString()
            : null;
        var deviceId = !string.IsNullOrWhiteSpace(registeredDeviceId)
            ? registeredDeviceId
            : turn.DeviceId ?? session.DeviceId;
        var artifactId = $"{turn.TimestampUtc:yyyyMMddTHHmmssfffZ}-{turn.TurnId}";
        var meta = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["artifactType"] = "websocket-asr-audio",
            ["category"] = "asr",
            ["deviceId"] = deviceId,
            ["identitySource"] = "websocket-session",
            ["sessionId"] = session.SessionId,
            ["turnId"] = turn.TurnId,
            ["transId"] = session.TurnState.TransId ?? session.LastTransId,
            ["audioFrameCount"] = session.TurnState.BufferedAudioFrames.Count,
            ["contentEncoding"] = isOgg ? "ogg" : "binary"
        };
        await mediaContentStore.StoreAsync($"asr/{artifactId}", isOgg ? "audio/ogg" : "application/octet-stream",
            content, meta, cancellationToken);
    }

    private static bool ShouldAutoFinalize(CloudSession session)
    {
        var turnState = session.TurnState;
        if (turnState.AutoFinalizeBlockedUntilUtc.HasValue &&
            turnState.AutoFinalizeBlockedUntilUtc.Value > DateTimeOffset.UtcNow)
            return false;

        var now = DateTimeOffset.UtcNow;
        var turnAge = turnState.FirstAudioReceivedUtc.HasValue
            ? now - turnState.FirstAudioReceivedUtc.Value
            : TimeSpan.Zero;
        var pageCounts = DescribeBufferedAudioPages(turnState);
        var silenceWindow = ResolveAutoFinalizeSilenceWindow(turnState);
        var receivedEndOfStream = HasReceivedOggEndOfStream(turnState);
        var hardTimeout = ResolveHardBufferedAudioAge(turnState);
        var reachedHardTimeout = turnAge >= hardTimeout;
        var elapsedSinceLastAudio = turnState.LastAudioReceivedUtc.HasValue
            ? now - turnState.LastAudioReceivedUtc.Value
            : TimeSpan.Zero;
        var reachedSilenceWindow = turnState.LastAudioReceivedUtc.HasValue &&
                                   elapsedSinceLastAudio >= silenceWindow;
        var reachedContentSilence = HasContentSilence(turnState, ResolveAutoFinalizeContentSilenceWindow(turnState));
        var transcriptHintEarlyFinalize = ShouldEarlyFinalizeFromTranscriptHint(turnState);

        // Incomplete-command deferrals must not re-enter finalize on arrival-time
        // silence alone — that re-ran STT on the same truncated buffer every ~450ms.
        if (turnState.DeferredIncompleteAudioBytes > 0 &&
            turnState.BufferedAudioBytes <= turnState.DeferredIncompleteAudioBytes &&
            !receivedEndOfStream &&
            !reachedHardTimeout &&
            !reachedContentSilence)
            return false;

        return turnState is
               {
                   AwaitingTurnCompletion: true, SawListen: true,
                   BufferedAudioChunkCount: >= 1,
                   BufferedAudioBytes: >= AutoFinalizeMinBufferedAudioBytes
               } &&
               pageCounts.AudioBearingPageCount >= AutoFinalizeMinBufferedAudioPages &&
               turnState.LastAudioReceivedUtc.HasValue &&
               (receivedEndOfStream || reachedHardTimeout ||
                transcriptHintEarlyFinalize ||
                reachedSilenceWindow ||
                reachedContentSilence) &&
               turnAge >= AutoFinalizeMinTurnAge;
    }

    private static bool ShouldEarlyFinalizeBufferedAudio(CloudSession session)
    {
        var turnState = session.TurnState;
        if (turnState.AutoFinalizeBlockedUntilUtc.HasValue &&
            turnState.AutoFinalizeBlockedUntilUtc.Value > DateTimeOffset.UtcNow)
            return false;

        if (!turnState.AwaitingTurnCompletion || !turnState.SawListen)
            return false;

        if (!turnState.FirstAudioReceivedUtc.HasValue)
            return false;

        var turnAge = DateTimeOffset.UtcNow - turnState.FirstAudioReceivedUtc.Value;
        var pageCounts = DescribeBufferedAudioPages(turnState);
        if (turnState.BufferedAudioBytes < AutoFinalizeMinBufferedAudioBytes)
            return false;

        if (pageCounts.AudioBearingPageCount < AutoFinalizeMinBufferedAudioPages)
            return false;

        if (!CanRetryEarlyAutoFinalizeProbe(turnState))
            return false;

        // Content-silence VAD is the fast path — do not wait for EarlyProbeMinTurnAge.
        // The previous gate forced even quiet-after-speech turns to sit until 900ms+,
        // and continuous streams without arrival gaps fell through to the 1.8s probe (~3-4s).
        if (HasContentSilence(turnState, AutoFinalizeHotphraseOggSilenceWindow) &&
            turnAge >= AutoFinalizeMinTurnAge &&
            IsHotphraseOggProbeCandidate(turnState))
            return true;

        if (HasReceivedOggEndOfStream(turnState) &&
            turnAge >= AutoFinalizeMinTurnAge &&
            IsHotphraseOggProbeCandidate(turnState))
            return true;

        if (turnAge < AutoFinalizeHotphraseOggEarlyProbeMinTurnAge)
            return false;

        var elapsedSinceLastAudio = turnState.LastAudioReceivedUtc.HasValue
            ? DateTimeOffset.UtcNow - turnState.LastAudioReceivedUtc.Value
            : TimeSpan.Zero;
        var transcriptHintEarlyFinalize = ShouldEarlyFinalizeFromTranscriptHint(turnState);
        var hotphraseOggProbeReady = ShouldEarlyProbeHotphraseOggAudio(
            turnState,
            pageCounts,
            turnAge);

        if (!transcriptHintEarlyFinalize && elapsedSinceLastAudio < AutoFinalizeHotphraseOggEarlyProbeGap)
            return ShouldContinuousProbeHotphraseOggAudio(turnState, pageCounts, turnAge);

        return transcriptHintEarlyFinalize ||
               hotphraseOggProbeReady ||
               turnState.ListenRules.Any(IsConstrainedYesNoRule);
    }

    private static bool ShouldEarlyProbeHotphraseOggAudio(
        WebSocketTurnState turnState,
        BufferedAudioPageCounts pageCounts,
        TimeSpan turnAge)
    {
        if (!IsHotphraseOggProbeCandidate(turnState))
            return false;

        if (HasReceivedOggEndOfStream(turnState))
            return turnAge >= AutoFinalizeMinTurnAge &&
                   turnState.BufferedAudioBytes >= AutoFinalizeMinBufferedAudioBytes &&
                   pageCounts.AudioBearingPageCount >= AutoFinalizeMinBufferedAudioPages;

        return (turnAge >= AutoFinalizeHotphraseOggEarlyProbeMinTurnAge &&
                turnState.BufferedAudioBytes >= AutoFinalizeHotphraseOggEarlyProbeMinBufferedAudioBytes &&
                pageCounts.AudioBearingPageCount >= AutoFinalizeHotphraseOggEarlyProbeMinAudioPages) ||
               ShouldContinuousProbeHotphraseOggAudio(turnState, pageCounts, turnAge);
    }

    private static bool ShouldContinuousProbeHotphraseOggAudio(
        WebSocketTurnState turnState,
        BufferedAudioPageCounts pageCounts,
        TimeSpan turnAge)
    {
        if (!IsHotphraseOggProbeCandidate(turnState) || HasReceivedOggEndOfStream(turnState))
            return false;

        if (turnState.BufferedAudioBytes < AutoFinalizeHotphraseOggContinuousProbeMinBufferedAudioBytes ||
            pageCounts.AudioBearingPageCount < AutoFinalizeHotphraseOggContinuousProbeMinAudioPages)
            return false;

        // Fast continuous-stream path: audio-clock trailing quiet after speech.
        if (turnAge >= AutoFinalizeMinTurnAge &&
            HasContentSilence(turnState, AutoFinalizeHotphraseOggContinuousProbeTrailingSilence))
            return true;

        // Hard fallback only — do not probe mid-utterance on wall-clock alone.
        return turnAge >= AutoFinalizeHotphraseOggContinuousProbeMinTurnAge;
    }

    private static bool IsHotphraseOggProbeCandidate(WebSocketTurnState turnState)
    {
        return turnState.ListenHotphrase &&
               turnState.SawContext &&
               HasOggOpusFrames(turnState) &&
               IsHotphraseLaunchListen(turnState);
    }

    private static bool CanRetryEarlyAutoFinalizeProbe(WebSocketTurnState turnState)
    {
        if (turnState.DeferredIncompleteAudioBytes > 0 &&
            turnState.BufferedAudioBytes <= turnState.DeferredIncompleteAudioBytes)
            return false;

        return !turnState.LastAutoFinalizeAttemptUtc.HasValue ||
               DateTimeOffset.UtcNow - turnState.LastAutoFinalizeAttemptUtc.Value >=
               AutoFinalizeEarlyProbeRetryInterval;
    }

    private static bool IsHotphraseLaunchListen(WebSocketTurnState turnState)
    {
        return turnState.ListenRules.Any(static rule =>
            string.Equals(rule, "launch", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rule, "globals/global_commands_launch", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyList<WebSocketReply>?> TryCloseStalledListenAsNoInputAsync(
        CloudSession session,
        WebSocketMessageEnvelope envelope,
        string phase,
        CancellationToken cancellationToken)
    {
        if (!ShouldCloseStalledListenAsNoInput(session, out var listenAge, out var pageCounts))
            return null;

        var turnState = session.TurnState;
        logger.LogWarning(
            "Turn stalled listen closing as no-input phase={Phase} session={SessionId} transId={TransId} listenAgeMs={ListenAgeMs} bufferedBytes={BufferedBytes} rawFrames={RawFrames} audioPages={AudioPages} metadataPages={MetadataPages}",
            phase,
            session.SessionId,
            turnState.TransId,
            (int)listenAge.TotalMilliseconds,
            turnState.BufferedAudioBytes,
            pageCounts.RawFrameCount,
            pageCounts.AudioBearingPageCount,
            pageCounts.MetadataPageCount);

        await sink.RecordTurnDiagnosticAsync(
            "auto_finalize_stalled_listen_no_input",
            BuildTurnDiagnosticSnapshot(session, envelope, new Dictionary<string, object?>
            {
                ["phase"] = phase,
                ["listenAgeMs"] = (int)listenAge.TotalMilliseconds,
                ["bufferedAudioBytes"] = turnState.BufferedAudioBytes,
                ["bufferedAudioChunks"] = turnState.BufferedAudioChunkCount,
                ["bufferedAudioRawFrames"] = pageCounts.RawFrameCount,
                ["bufferedAudioMetadataPages"] = pageCounts.MetadataPageCount,
                ["bufferedAudioAudioBearingPages"] = pageCounts.AudioBearingPageCount,
                ["sawListen"] = turnState.SawListen,
                ["sawContext"] = turnState.SawContext
            }),
            cancellationToken);

        turnState.AwaitingTurnCompletion = false;
        session.LastTranscript = string.Empty;
        session.LastIntent = null;
        session.LastListenType = "no-input";

        var replies = ResponsePlanToSocketMessagesMapper.MapNoInput(
                turnState.TransId ?? session.LastTransId ?? string.Empty,
                turnState.ListenRules)
            .Select(map => new WebSocketReply { Text = map.Text, DelayMs = map.DelayMs })
            .ToArray();

        ResetBufferedAudio(session);
        ClearListenTracking(turnState);
        return replies;
    }

    private static bool ShouldCloseStalledListenAsNoInput(
        CloudSession session,
        out TimeSpan listenAge,
        out BufferedAudioPageCounts pageCounts)
    {
        var turnState = session.TurnState;
        pageCounts = DescribeBufferedAudioPages(turnState);
        listenAge = TimeSpan.Zero;

        if (!turnState.AwaitingTurnCompletion || !turnState.SawListen)
            return false;

        var startedUtc = turnState.ListenOpenedUtc ?? turnState.FirstAudioReceivedUtc;
        if (!startedUtc.HasValue)
            return false;

        listenAge = DateTimeOffset.UtcNow - startedUtc.Value;
        if (listenAge < AutoFinalizeNoAudioListenAge)
            return false;

        return pageCounts.AudioBearingPageCount < AutoFinalizeMinBufferedAudioPages;
    }

    private static bool ShouldEarlyFinalizeFromTranscriptHint(WebSocketTurnState turnState)
    {
        if (string.IsNullOrWhiteSpace(turnState.AudioTranscriptHint)) return false;

        var normalized = NormalizeUsableTranscript(turnState.AudioTranscriptHint);
        if (string.IsNullOrWhiteSpace(normalized)) return false;

        var command = TranscriptTextNormalizer.ExtractWakePhraseCommand(normalized);
        if (!string.IsNullOrWhiteSpace(command))
            normalized = NormalizeUsableTranscript(command);

        if (string.IsNullOrWhiteSpace(normalized)) return false;
        if (LooksLikeIncompleteLaunchQuestion(normalized)) return false;
        if (IsHotphraseOnlyTranscript(normalized)) return false;
        if (TryClassifyYesNoReply(normalized) is not YesNoReply.None) return true;

        return new[]
            {
                "cloud version",
                "what time is it",
                "how old are you",
                "what is your age",
                "what's your age",
                "whats your age",
                "what's your favorite color",
                "what is your favorite color",
                "play word of the day",
                "word of the day",
                "stop",
                "cancel",
                "never mind",
                "nevermind"
            }
            .Any(candidate => normalized.Contains(candidate, StringComparison.Ordinal));
    }

    private static BufferedAudioPageCounts DescribeBufferedAudioPages(WebSocketTurnState turnState)
    {
        if (turnState.BufferedAudioFrames.Count > 0)
            return BufferedAudioPageClassifier.Describe(turnState.BufferedAudioFrames);

        if (turnState.BufferedAudioChunkCount <= 0)
            return new BufferedAudioPageCounts(0, 0, 0);

        return new BufferedAudioPageCounts(
            turnState.BufferedAudioChunkCount,
            turnState.BufferedAudioChunkCount,
            0);
    }

    private void LogAutoFinalizeDecision(string phase, CloudSession session, WebSocketTurnState turnState)
    {
        var now = DateTimeOffset.UtcNow;
        var firstAudioAgeMs = turnState.FirstAudioReceivedUtc.HasValue
            ? (now - turnState.FirstAudioReceivedUtc.Value).TotalMilliseconds
            : -1;
        var lastAudioAgeMs = turnState.LastAudioReceivedUtc.HasValue
            ? (now - turnState.LastAudioReceivedUtc.Value).TotalMilliseconds
            : -1;
        var pageCounts = DescribeBufferedAudioPages(turnState);
        var silenceWindow = ResolveAutoFinalizeSilenceWindow(turnState);
        var receivedEndOfStream = HasReceivedOggEndOfStream(turnState);
        var hardTimeout = ResolveHardBufferedAudioAge(turnState);
        var reachedHardTimeout = firstAudioAgeMs >= hardTimeout.TotalMilliseconds;
        var turnAge = turnState.FirstAudioReceivedUtc.HasValue
            ? now - turnState.FirstAudioReceivedUtc.Value
            : TimeSpan.Zero;
        var earlyProbeReady = ShouldEarlyProbeHotphraseOggAudio(turnState, pageCounts, turnAge);
        var transcriptHintEarlyFinalize = ShouldEarlyFinalizeFromTranscriptHint(turnState);

        logger.LogDebug(
            "Turn auto-finalize check phase={Phase} session={SessionId} transId={TransId} awaiting={Awaiting} sawListen={SawListen} rawFrames={RawFrames} audioPages={AudioPages} metadataPages={MetadataPages} bytes={Bytes} firstAudioAgeMs={FirstAudioAgeMs} lastAudioAgeMs={LastAudioAgeMs} minAgeMs={MinAgeMs} silenceMs={SilenceMs} hardTimeoutMs={HardTimeoutMs} reachedHardTimeout={ReachedHardTimeout} receivedEndOfStream={ReceivedEndOfStream} earlyProbeReady={EarlyProbeReady} transcriptHintEarlyFinalize={TranscriptHintEarlyFinalize}",
            phase,
            session.SessionId,
            turnState.TransId,
            turnState.AwaitingTurnCompletion,
            turnState.SawListen,
            pageCounts.RawFrameCount,
            pageCounts.AudioBearingPageCount,
            pageCounts.MetadataPageCount,
            turnState.BufferedAudioBytes,
            firstAudioAgeMs,
            lastAudioAgeMs,
            AutoFinalizeMinTurnAge.TotalMilliseconds,
            silenceWindow.TotalMilliseconds,
            hardTimeout.TotalMilliseconds,
            reachedHardTimeout,
            receivedEndOfStream,
            earlyProbeReady,
            transcriptHintEarlyFinalize);
    }

    private static bool HasContentSilence(WebSocketTurnState turnState, TimeSpan requiredSilence)
    {
        if (!HasOggOpusFrames(turnState) || turnState.BufferedAudioFrames.Count == 0)
            return false;

        try
        {
            return OpusSpeechActivityDetector.HasTrailingSilence(
                turnState.BufferedAudioFrames,
                requiredSilence);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static TimeSpan ResolveAutoFinalizeSilenceWindow(WebSocketTurnState turnState)
    {
        return turnState.ListenHotphrase && IsHotphraseLaunchListen(turnState) && HasOggOpusFrames(turnState)
            ? AutoFinalizeHotphraseOggArrivalGap
            : AutoFinalizeSilenceWindow;
    }

    private static TimeSpan ResolveAutoFinalizeContentSilenceWindow(WebSocketTurnState turnState)
    {
        if (!turnState.ListenHotphrase || !turnState.ListenRules.Any(static rule =>
                string.Equals(rule, "launch", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rule, "globals/global_commands_launch", StringComparison.OrdinalIgnoreCase)))
            return AutoFinalizeSilenceWindow;

        return HasOggOpusFrames(turnState) ? AutoFinalizeHotphraseOggSilenceWindow : AutoFinalizeSilenceWindow;
    }

    private static bool HasOggOpusFrames(WebSocketTurnState turnState)
    {
        return turnState.BufferedAudioFrames.Any(static frame => frame.AsSpan().IndexOf("OpusHead"u8) >= 0);
    }

    private static bool HasReceivedOggEndOfStream(WebSocketTurnState turnState)
    {
        return turnState.BufferedAudioFrames.LastOrDefault() is { Length: > 5 } frame &&
               frame.AsSpan(0, 4).SequenceEqual("OggS"u8) &&
               (frame[5] & 0x04) == 0x04;
    }

    private static bool ShouldIgnoreLateAudio(CloudSession session)
    {
        var ignoreUntilUtc = session.TurnState.IgnoreAdditionalAudioUntilUtc;
        if (session.TurnState.AutoFinalizeBlockedUntilUtc.HasValue &&
            session.TurnState.AutoFinalizeBlockedUntilUtc.Value > DateTimeOffset.UtcNow)
            return true;

        if (session.FollowUpOpen) return false;

        return ignoreUntilUtc.HasValue &&
               ignoreUntilUtc.Value > DateTimeOffset.UtcNow;
    }

    private static bool ShouldIgnoreLateHotphraseTurn(CloudSession session, TurnContext turn)
    {
        var ignoreUntilUtc = session.TurnState.IgnoreAdditionalAudioUntilUtc;
        if (!ignoreUntilUtc.HasValue || ignoreUntilUtc.Value <= DateTimeOffset.UtcNow) return false;
        if (!ReadBoolAttribute(turn, "listenHotphrase")) return false;

        return ReadRules(turn, "listenRules")
            .Any(static rule => string.Equals(rule, "launch", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(rule, "globals/global_commands_launch",
                                    StringComparison.OrdinalIgnoreCase));
    }

    public static bool ShouldIgnoreLateListenSetup(CloudSession session, string? text)
    {
        if (string.Equals(session.LastIntent, "stop", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!IsDiagnosticSpeechIntent(session.LastIntent))
            return false;

        var ignoreUntilUtc = session.TurnState.IgnoreLateListenSetupUntilUtc;
        if (!ignoreUntilUtc.HasValue || ignoreUntilUtc.Value <= DateTimeOffset.UtcNow ||
            !IsHotphraseLaunchListenSetup(text))
            return false;

        if (session.TurnState.AwaitingTurnCompletion)
            return true;

        return !TryReadTransId(text, out var nextTransId) ||
               string.Equals(session.TurnState.TransId, nextTransId, StringComparison.Ordinal);
    }

    public static bool TryRecoverStalePendingListen(CloudSession session, out int staleAgeMs)
    {
        staleAgeMs = 0;
        var turnState = session.TurnState;
        if (!turnState.AwaitingTurnCompletion ||
            !turnState.SawListen ||
            turnState.SawContext ||
            turnState.BufferedAudioBytes > 0 ||
            !turnState.ListenOpenedUtc.HasValue)
            return false;

        var age = DateTimeOffset.UtcNow - turnState.ListenOpenedUtc.Value;
        if (age < StaleListenSetupRecoveryAge) return false;

        staleAgeMs = (int)age.TotalMilliseconds;
        turnState.AwaitingTurnCompletion = false;
        ResetBufferedAudio(session);
        ClearListenTracking(turnState);
        turnState.ListenHotphrase = false;
        turnState.HotphraseEmptyTurnCount = 0;
        UpdateGlsmPhaseMarker(session);
        return true;
    }

    public static string ResolveGlsmPhase(CloudSession session)
    {
        if (session.Metadata.TryGetValue("sleepState", out var sleepState) &&
            sleepState is string sleepStateText &&
            string.Equals(sleepStateText, "sleeping", StringComparison.OrdinalIgnoreCase))
            return "ASLEEP";

        var turnState = session.TurnState;
        if (!turnState.AwaitingTurnCompletion)
            return session.FollowUpOpen ? "DISPATCH_DIALOG" : "PROCESS_LISTENER_QUEUE";

        return turnState.SawListen switch
        {
            true when turnState is { SawContext: false, BufferedAudioBytes: 0 } => "HJ_LISTENING",
            true when turnState is { SawContext: true, BufferedAudioBytes: 0 } => "LISTENING",
            _ => turnState.BufferedAudioBytes > 0 ? "WAIT_LISTEN_FINISHED" : "LISTENING"
        };
    }

    private static TimeSpan ResolveLateAudioIgnoreWindow(ResponsePlan plan)
    {
        if (string.Equals(plan.IntentName, "stop", StringComparison.OrdinalIgnoreCase))
            return WebSocketTurnState.StopCommandLateAudioIgnoreWindow;

        return IsDiagnosticSpeechIntent(plan.IntentName)
            ? WebSocketTurnState.DiagnosticSpeechLateAudioIgnoreWindow
            : WebSocketTurnState.DefaultLateAudioIgnoreWindow;
    }

    private static bool IsDiagnosticSpeechIntent(string? intentName)
    {
        return string.Equals(intentName, "cloud_version", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldIgnoreAudioWithoutListen(WebSocketTurnState turnState)
    {
        return !turnState.SawListen &&
               !string.IsNullOrWhiteSpace(turnState.TransId);
    }

    private static bool IsHotphraseLaunchListenSetup(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        try
        {
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object)
                return false;

            var isHotphrase = data.TryGetProperty("hotphrase", out var hotphrase) &&
                              hotphrase.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                              hotphrase.GetBoolean();
            if (!isHotphrase ||
                !data.TryGetProperty("rules", out var rules) ||
                rules.ValueKind != JsonValueKind.Array)
                return false;

            return rules.EnumerateArray()
                .Where(static rule => rule.ValueKind == JsonValueKind.String)
                .Select(static rule => rule.GetString())
                .Any(static rule => string.Equals(rule, "launch", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(rule, "globals/global_commands_launch",
                                        StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ShouldIgnorePassiveLocalSkillContext(CloudSession session, string? text)
    {
        if (session.FollowUpOpen) return false;

        if (HasCloudHandledLocalPromptOpen(session.TurnState)) return false;

        var skillId = TryReadContextSkillId(text);
        return string.Equals(skillId, "@be/gallery", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(skillId, "@be/create", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(skillId, "@be/settings", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasCloudHandledLocalPromptOpen(WebSocketTurnState turnState)
    {
        return turnState is { AwaitingTurnCompletion: true, SawListen: true } &&
               (turnState.ListenAsrHints.Any(static hint =>
                    string.Equals(hint, "$YESNO", StringComparison.OrdinalIgnoreCase)) ||
                turnState.ListenRules.Any(rule =>
                    IsClockValueRule(rule) ||
                    IsGalleryPreviewRule(rule) ||
                    IsConstrainedYesNoRule(rule) ||
                    IsIntroductionsRule(rule) ||
                    rule.StartsWith("exercise/", StringComparison.OrdinalIgnoreCase)));
    }

    private static string? ExtractDataPayload(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.TryGetProperty("data", out var data)) return data.GetRawText();
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static bool TryReadContextProperty(string? text, string propertyName, out string? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        try
        {
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty(propertyName, out var property) ||
                property.ValueKind != JsonValueKind.String)
                return false;

            value = property.GetString();
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    private static string? TryReadContextSkillId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("skill", out var skill) ||
                !skill.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.String)
                return null;

            return id.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryReadTransId(string? text, out string? transId)
    {
        transId = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        try
        {
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("transID", out var transIdProperty) ||
                transIdProperty.ValueKind != JsonValueKind.String)
                return false;

            transId = transIdProperty.GetString();
            return !string.IsNullOrWhiteSpace(transId);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetUsableTranscript(TurnContext turn, out string transcript)
    {
        var messageType = ReadMessageType(turn);
        var clientIntent = ReadAttribute(turn, "clientIntent");
        var pendingProactivityOffer = ReadAttribute(turn, "pendingProactivityOffer");
        var personalReportState = ReadAttribute(turn, PersonalReportOrchestrator.StateMetadataKey);
        transcript = NormalizeUsableTranscript(turn.NormalizedTranscript ?? turn.RawTranscript);
        var listenRules = ReadRules(turn, "listenRules").Concat(ReadRules(turn, "clientRules")).ToArray();

        if (string.Equals(messageType, "CLIENT_NLU", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(clientIntent))
            return true;

        if (string.IsNullOrWhiteSpace(transcript)) return false;

        if (TranscriptHeuristics.IsLikelyRobotSelfAudioTranscript(transcript))
            return false;

        if (!string.IsNullOrWhiteSpace(personalReportState) &&
            !string.Equals(personalReportState, PersonalReportOrchestrator.IdleState,
                StringComparison.OrdinalIgnoreCase))
            return true;

        if (transcript is "blank_audio" or "blank audio") return false;

        if (listenRules.Any(IsClockValueRule)) return true;

        if (listenRules.Any(IsIntroductionsRule)) return true;

        if (ChitchatStateMachine.IsLikelyEmotionUtterance(transcript)) return true;

        if (IsYesNoTurn(turn) && IsYesNoReplyTranscript(transcript)) return true;

        if (!string.IsNullOrWhiteSpace(pendingProactivityOffer) &&
            IsYesNoReplyTranscript(transcript))
            return true;

        if (listenRules.Any(rule =>
                string.Equals(rule, "word-of-the-day/puzzle", StringComparison.OrdinalIgnoreCase))) return true;

        if (SkillListenOwnership.IsSkillOwnedListen(turn) && IsYesNoReplyTranscript(transcript)) return true;

        if (IsLowSignalSingleTokenTranscript(transcript)) return false;

        if (SingleTokenUsableTranscripts.Contains(transcript)) return true;

        return transcript.Length >= 6;
    }

    private static bool IsLowSignalSingleTokenTranscript(string transcript)
    {
        var tokens = transcript.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length == 1 && !SingleTokenUsableTranscripts.Contains(tokens[0]);
    }

    private static bool IsYesNoTurn(TurnContext turn)
    {
        var listenRules = ReadRules(turn, "listenRules").ToArray();
        var clientRules = ReadRules(turn, "clientRules").ToArray();
        if (listenRules
            .Concat(clientRules)
            .Concat(ReadRules(turn, "listenAsrHints"))
            .Any(IsYesNoRule))
            return true;

        if (!SkillListenOwnership.IsSkillOwnedListen(
                SkillListenOwnership.ReadListenHotphrase(turn),
                listenRules,
                clientRules))
            return false;

        var transcript = NormalizeUsableTranscript(turn.NormalizedTranscript ?? turn.RawTranscript);
        return IsYesNoReplyTranscript(transcript) ||
               TryClassifyYesNoReply(transcript) == YesNoReply.Ambiguous;
    }

    private static bool IsActivePersonalReportTurn(TurnContext turn)
    {
        var state = ReadAttribute(turn, PersonalReportOrchestrator.StateMetadataKey);
        return !string.IsNullOrWhiteSpace(state) &&
               !string.Equals(state, PersonalReportOrchestrator.IdleState, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCloudOwnedPersonalReportIntent(string? intentName)
    {
        return !string.IsNullOrWhiteSpace(intentName) &&
               intentName.StartsWith("personal_report_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldSpeakCloudOwnedClientNlu(TurnContext turn, string? intentName)
    {
        if (!SkillListenOwnership.IsCloudOwnedFollowUp(turn)) return false;
        if (SkillListenOwnership.IsHeyJiboSkillLaunch(intentName)) return false;

        var clientIntent = ReadAttribute(turn, "clientIntent");
        if (!string.IsNullOrWhiteSpace(clientIntent) &&
            string.Equals(intentName, clientIntent, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static bool IsBadAppleSkillLaunch(ResponsePlan plan)
    {
        return string.Equals(plan.IntentName, "bad_apple", StringComparison.OrdinalIgnoreCase) &&
               plan.Actions.OfType<InvokeNativeSkillAction>().Any(action =>
                   string.Equals(action.SkillName, "@be/bad-apple", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldHandleAsLocalNoInput(TurnContext turn)
    {
        if (!string.IsNullOrWhiteSpace(turn.NormalizedTranscript) ||
            !string.IsNullOrWhiteSpace(turn.RawTranscript)) return false;

        return ReadRules(turn, "listenRules")
            .Concat(ReadRules(turn, "clientRules"))
            .Any(IsLocalNoInputRule);
    }

    private static string? ReadPrimaryNoInputRule(TurnContext turn)
    {
        return ReadRules(turn, "listenRules")
            .Concat(ReadRules(turn, "clientRules"))
            .FirstOrDefault(IsLocalNoInputRule);
    }

    private static string? ReadPrimaryYesNoRule(TurnContext turn)
    {
        return ReadRules(turn, "listenRules")
                   .Concat(ReadRules(turn, "clientRules"))
                   .FirstOrDefault(IsConstrainedYesNoRule) ??
               SkillListenOwnership.ReadPrimarySkillRule(turn);
    }

    private static WebSocketReply[] BuildLocalNoInputReplies(
        CloudSession session,
        WebSocketTurnState turnState,
        string? localRule)
    {
        var transId = turnState.TransId ?? session.LastTransId ?? string.Empty;
        var effectiveRule = string.IsNullOrWhiteSpace(localRule)
            ? turnState.ListenRules.FirstOrDefault(IsLocalNoInputRule)
            : localRule;
        var rules = string.IsNullOrWhiteSpace(effectiveRule) ? turnState.ListenRules : [effectiveRule];
        var maps = ShouldRedirectRepeatedNoInputToIdle(turnState, effectiveRule)
            ? ResponsePlanToSocketMessagesMapper.MapNoInputAndRedirectToSkill(transId, rules, "@be/idle")
            : ResponsePlanToSocketMessagesMapper.MapNoInput(transId, rules);

        return [.. maps.Select(map => new WebSocketReply { Text = map.Text, DelayMs = map.DelayMs })];
    }

    private static bool ShouldRedirectRepeatedNoInputToIdle(WebSocketTurnState turnState, string? localRule)
    {
        if (string.IsNullOrWhiteSpace(localRule))
        {
            turnState.LastLocalNoInputRule = null;
            turnState.LocalNoInputCount = 0;
            return false;
        }

        turnState.LocalNoInputCount =
            string.Equals(turnState.LastLocalNoInputRule, localRule, StringComparison.OrdinalIgnoreCase)
                ? turnState.LocalNoInputCount + 1
                : 1;
        turnState.LastLocalNoInputRule = localRule;

        return turnState.LocalNoInputCount >= 2 &&
               string.Equals(localRule, "create/is_it_a_keeper", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsYesNoRule(string rule)
    {
        return string.Equals(rule, "$YESNO", StringComparison.OrdinalIgnoreCase) ||
               IsConstrainedYesNoRule(rule);
    }

    private static bool IsLocalNoInputRule(string rule)
    {
        return string.Equals(rule, "clock/alarm_timer_okay", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "settings/volume_control", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "word-of-the-day/puzzle", StringComparison.OrdinalIgnoreCase) ||
               IsClockValueRule(rule) ||
               IsGalleryPreviewRule(rule) ||
               IsConstrainedYesNoRule(rule) ||
               SkillListenOwnership.IsSkillRule(rule);
    }

    private static bool IsClockValueRule(string rule)
    {
        return string.Equals(rule, "clock/alarm_set_value", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "clock/timer_set_value", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGalleryPreviewRule(string rule)
    {
        return string.Equals(rule, "gallery/gallery_preview", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIntroductionsRule(string rule)
    {
        return rule.StartsWith("introductions/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsConstrainedYesNoRule(string rule)
    {
        return string.Equals(rule, "clock/alarm_timer_change", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "clock/alarm_timer_none_set", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "create/is_it_a_keeper", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "shared/yes_no", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "settings/download_now_later", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "surprises-date/offer_date_fact", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "surprises-ota/want_to_download_now", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "word-of-the-day/surprise", StringComparison.OrdinalIgnoreCase) ||
               IsIntroductionsRule(rule);
    }

    private static bool IsYesNoReplyTranscript(string normalizedTranscript)
    {
        return YesNoTranscriptClassifier.Classify(normalizedTranscript) is
            YesNoTranscriptClassification.Affirmative or YesNoTranscriptClassification.Negative;
    }

    private static YesNoReply TryClassifyYesNoReply(string normalizedTranscript)
    {
        return YesNoTranscriptClassifier.Classify(normalizedTranscript) switch
        {
            YesNoTranscriptClassification.Affirmative => YesNoReply.Affirmative,
            YesNoTranscriptClassification.Negative => YesNoReply.Negative,
            YesNoTranscriptClassification.Ambiguous => YesNoReply.Ambiguous,
            _ => YesNoReply.None
        };
    }

    private static string NormalizeUsableTranscript(string? transcript)
    {
        var normalized = NormalizeTranscript(transcript);
        if (string.IsNullOrWhiteSpace(normalized)) return string.Empty;

        while (TryTrimLeadingTranscriptNoise(normalized, out var trimmed)) normalized = trimmed;
        return normalized;
    }

    private static bool TryTrimLeadingTranscriptNoise(string normalizedTranscript, out string trimmedTranscript)
    {
        foreach (var noisePrefix in TranscriptNoisePrefixes)
        {
            if (string.Equals(noisePrefix, "uh", StringComparison.Ordinal) &&
                (string.Equals(normalizedTranscript, "uh huh", StringComparison.Ordinal) ||
                 normalizedTranscript.StartsWith("uh huh ", StringComparison.Ordinal)))
                continue;

            if (string.Equals(normalizedTranscript, noisePrefix, StringComparison.Ordinal))
            {
                trimmedTranscript = string.Empty;
                return true;
            }

            if (!normalizedTranscript.StartsWith($"{noisePrefix} ", StringComparison.Ordinal)) continue;

            trimmedTranscript = normalizedTranscript[(noisePrefix.Length + 1)..].TrimStart();
            return true;
        }

        trimmedTranscript = normalizedTranscript;
        return false;
    }

    private async Task ApplyContextUpdatesAsync(
        CloudSession session,
        IDictionary<string, object?> contextUpdates,
        WebSocketMessageEnvelope envelope,
        string? intentName,
        CancellationToken cancellationToken)
    {
        if (contextUpdates.Count == 0) return;

        var previousState = ReadMetadataString(session.Metadata, PersonalReportOrchestrator.StateMetadataKey);
        var previousNoMatchCount =
            ReadMetadataInt(session.Metadata, PersonalReportOrchestrator.NoMatchCountMetadataKey);
        var previousNoInputCount =
            ReadMetadataInt(session.Metadata, PersonalReportOrchestrator.NoInputCountMetadataKey);
        var previousWeatherEnabled =
            ReadMetadataBool(session.Metadata, PersonalReportOrchestrator.WeatherEnabledMetadataKey) ?? true;
        var previousCalendarEnabled =
            ReadMetadataBool(session.Metadata, PersonalReportOrchestrator.CalendarEnabledMetadataKey) ?? true;
        var previousCommuteEnabled =
            ReadMetadataBool(session.Metadata, PersonalReportOrchestrator.CommuteEnabledMetadataKey) ?? true;
        var previousNewsEnabled =
            ReadMetadataBool(session.Metadata, PersonalReportOrchestrator.NewsEnabledMetadataKey) ?? true;
        var previousChitchatState = ReadMetadataString(session.Metadata, ChitchatStateMachine.StateMetadataKey);
        var previousChitchatRoute = ReadMetadataString(session.Metadata, ChitchatStateMachine.RouteMetadataKey);
        var previousChitchatEmotion = ReadMetadataString(session.Metadata, ChitchatStateMachine.EmotionMetadataKey);

        foreach (var pair in contextUpdates)
        {
            if (pair.Value is null)
            {
                session.Metadata.Remove(pair.Key);
                continue;
            }

            session.Metadata[pair.Key] = pair.Value;
        }

        var nextState = ReadMetadataString(session.Metadata, PersonalReportOrchestrator.StateMetadataKey);
        var nextNoMatchCount = ReadMetadataInt(session.Metadata, PersonalReportOrchestrator.NoMatchCountMetadataKey);
        var nextNoInputCount = ReadMetadataInt(session.Metadata, PersonalReportOrchestrator.NoInputCountMetadataKey);
        var nextWeatherEnabled =
            ReadMetadataBool(session.Metadata, PersonalReportOrchestrator.WeatherEnabledMetadataKey) ?? true;
        var nextCalendarEnabled =
            ReadMetadataBool(session.Metadata, PersonalReportOrchestrator.CalendarEnabledMetadataKey) ?? true;
        var nextCommuteEnabled =
            ReadMetadataBool(session.Metadata, PersonalReportOrchestrator.CommuteEnabledMetadataKey) ?? true;
        var nextNewsEnabled = ReadMetadataBool(session.Metadata, PersonalReportOrchestrator.NewsEnabledMetadataKey) ??
                              true;
        var serviceError = ReadMetadataString(session.Metadata, PersonalReportOrchestrator.LastServiceErrorMetadataKey);
        var nextChitchatState = ReadMetadataString(session.Metadata, ChitchatStateMachine.StateMetadataKey);
        var nextChitchatRoute = ReadMetadataString(session.Metadata, ChitchatStateMachine.RouteMetadataKey);
        var nextChitchatEmotion = ReadMetadataString(session.Metadata, ChitchatStateMachine.EmotionMetadataKey);

        if (!string.Equals(previousState, nextState, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(previousState) &&
                !string.Equals(previousState, PersonalReportOrchestrator.IdleState, StringComparison.OrdinalIgnoreCase))
                await sink.RecordTurnDiagnosticAsync("personal_report_state_exit", BuildTurnDiagnosticSnapshot(session,
                    envelope, new Dictionary<string, object?>
                    {
                        ["state"] = previousState,
                        ["nextState"] = nextState,
                        ["intent"] = intentName
                    }), cancellationToken);

            if (!string.IsNullOrWhiteSpace(nextState) &&
                !string.Equals(nextState, PersonalReportOrchestrator.IdleState, StringComparison.OrdinalIgnoreCase))
                await sink.RecordTurnDiagnosticAsync("personal_report_state_enter", BuildTurnDiagnosticSnapshot(session,
                    envelope, new Dictionary<string, object?>
                    {
                        ["state"] = nextState,
                        ["previousState"] = previousState,
                        ["intent"] = intentName
                    }), cancellationToken);
        }

        if (nextNoMatchCount != previousNoMatchCount)
            await sink.RecordTurnDiagnosticAsync("personal_report_nomatch_count", BuildTurnDiagnosticSnapshot(session,
                envelope, new Dictionary<string, object?>
                {
                    ["count"] = nextNoMatchCount,
                    ["previousCount"] = previousNoMatchCount,
                    ["state"] = nextState,
                    ["intent"] = intentName
                }), cancellationToken);

        if (nextNoInputCount != previousNoInputCount)
            await sink.RecordTurnDiagnosticAsync("personal_report_noinput_count", BuildTurnDiagnosticSnapshot(session,
                envelope, new Dictionary<string, object?>
                {
                    ["count"] = nextNoInputCount,
                    ["previousCount"] = previousNoInputCount,
                    ["state"] = nextState,
                    ["intent"] = intentName
                }), cancellationToken);

        await EmitServiceToggleDiagnosticAsync("weather", previousWeatherEnabled, nextWeatherEnabled, session, envelope,
            intentName, cancellationToken);
        await EmitServiceToggleDiagnosticAsync("calendar", previousCalendarEnabled, nextCalendarEnabled, session,
            envelope, intentName, cancellationToken);
        await EmitServiceToggleDiagnosticAsync("commute", previousCommuteEnabled, nextCommuteEnabled, session, envelope,
            intentName, cancellationToken);
        await EmitServiceToggleDiagnosticAsync("news", previousNewsEnabled, nextNewsEnabled, session, envelope,
            intentName, cancellationToken);

        if (!string.IsNullOrWhiteSpace(serviceError))
            await sink.RecordTurnDiagnosticAsync("personal_report_service_error", BuildTurnDiagnosticSnapshot(session,
                envelope, new Dictionary<string, object?>
                {
                    ["service"] = serviceError,
                    ["state"] = nextState,
                    ["intent"] = intentName
                }), cancellationToken);

        if (!string.Equals(previousChitchatState, nextChitchatState, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(nextChitchatState))
            await sink.RecordTurnDiagnosticAsync("chitchat_state_transition", BuildTurnDiagnosticSnapshot(session,
                envelope, new Dictionary<string, object?>
                {
                    ["previousState"] = previousChitchatState,
                    ["state"] = nextChitchatState,
                    ["intent"] = intentName
                }), cancellationToken);

        if (!string.Equals(previousChitchatRoute, nextChitchatRoute, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(nextChitchatRoute))
            await sink.RecordTurnDiagnosticAsync("chitchat_route_selected", BuildTurnDiagnosticSnapshot(session,
                envelope, new Dictionary<string, object?>
                {
                    ["route"] = nextChitchatRoute,
                    ["previousRoute"] = previousChitchatRoute,
                    ["emotion"] = nextChitchatEmotion,
                    ["previousEmotion"] = previousChitchatEmotion,
                    ["intent"] = intentName
                }), cancellationToken);
    }

    private async Task EmitServiceToggleDiagnosticAsync(
        string service,
        bool previousEnabled,
        bool nextEnabled,
        CloudSession session,
        WebSocketMessageEnvelope envelope,
        string? intentName,
        CancellationToken cancellationToken)
    {
        if (previousEnabled == nextEnabled) return;

        await sink.RecordTurnDiagnosticAsync(
            nextEnabled ? "personal_report_service_on" : "personal_report_service_off",
            BuildTurnDiagnosticSnapshot(session, envelope, new Dictionary<string, object?>
            {
                ["service"] = service,
                ["enabled"] = nextEnabled,
                ["intent"] = intentName
            }),
            cancellationToken);
    }

    private static void UpdatePendingProactivityOffer(CloudSession session, string? intentName)
    {
        if (string.Equals(intentName, "proactive_offer_pizza_fact", StringComparison.OrdinalIgnoreCase))
        {
            session.Metadata["pendingProactivityOffer"] = "pizza_fact";
            return;
        }

        session.Metadata.Remove("pendingProactivityOffer");
    }

    private static IEnumerable<string> ReadRules(TurnContext turn, string key)
    {
        if (!turn.Attributes.TryGetValue(key, out var value) || value is null) return [];

        return value switch
        {
            IReadOnlyList<string> typed => typed,
            IEnumerable<string> strings => strings,
            JsonElement { ValueKind: JsonValueKind.Array } json => json.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString() ?? string.Empty),
            _ => []
        };
    }

    private static string? ReadMetadataString(IDictionary<string, object?> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value) || value is null) return null;

        return value switch
        {
            string text => string.IsNullOrWhiteSpace(text) ? null : text.Trim(),
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            _ => value.ToString()
        };
    }

    private static int ReadMetadataInt(IDictionary<string, object?> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value) || value is null) return 0;

        return value switch
        {
            int integer => integer,
            long whole and <= int.MaxValue and >= int.MinValue => (int)whole,
            string text when int.TryParse(text, out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.Number } json when json.TryGetInt32(out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.String } json when int.TryParse(json.GetString(), out var parsed) =>
                parsed,
            _ => 0
        };
    }

    private static bool? ReadMetadataBool(IDictionary<string, object?> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value) || value is null) return null;

        return value switch
        {
            bool flag => flag,
            string text when bool.TryParse(text, out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement { ValueKind: JsonValueKind.String } json when bool.TryParse(json.GetString(), out var parsed) =>
                parsed,
            _ => null
        };
    }

    private static string NormalizeTranscript(string? transcript)
    {
        return TranscriptTextNormalizer.NormalizeLooseText(transcript);
    }

    private static string? ReadMessageType(TurnContext turn)
    {
        return ReadAttribute(turn, "messageType");
    }

    private static string? ReadAttribute(TurnContext turn, string key)
    {
        return turn.Attributes.TryGetValue(key, out var value)
            ? value?.ToString()
            : null;
    }

    private static bool ShouldIgnoreBlankAudioHotphraseTurn(TurnContext turn)
    {
        var transcript = NormalizeTranscript(turn.NormalizedTranscript ?? turn.RawTranscript);
        if (transcript is not ("blank_audio" or "blank audio")) return false;

        return ReadRules(turn, "listenRules")
            .Any(static rule => string.Equals(rule, "launch", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldCloseEmptyHotphraseAutoFinalize(
        TurnContext turn,
        WebSocketTurnState turnState,
        string messageType,
        bool allowFallbackOnMissingTranscript,
        TimeSpan turnAge)
    {
        if (!allowFallbackOnMissingTranscript ||
            !string.Equals(messageType, "AUTO_FINALIZE", StringComparison.OrdinalIgnoreCase))
            return false;

        if (turnState.FinalizeAttemptCount < 2 || turnAge < AutoFinalizeMissingTranscriptFallbackAge)
            return false;

        if (!ReadBoolAttribute(turn, "listenHotphrase")) return false;
        if (HasOggOpusFrames(turnState) &&
            !HasReceivedOggEndOfStream(turnState) &&
            turnAge < ResolveHardBufferedAudioAge(turnState))
            return false;

        return ReadRules(turn, "listenRules")
            .Any(static rule => string.Equals(rule, "launch", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(rule, "globals/global_commands_launch",
                                    StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldKeepOpenOggHotphraseBlankBeforeHardTimeout(
        TurnContext turn,
        WebSocketTurnState turnState,
        string messageType,
        bool allowFallbackOnMissingTranscript,
        TimeSpan turnAge)
    {
        if (!allowFallbackOnMissingTranscript ||
            !string.Equals(messageType, "AUTO_FINALIZE", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!ReadBoolAttribute(turn, "listenHotphrase")) return false;
        if (turnAge >= ResolveHardBufferedAudioAge(turnState)) return false;
        if (!HasOggOpusFrames(turnState) || HasReceivedOggEndOfStream(turnState)) return false;

        return ReadRules(turn, "listenRules")
            .Any(static rule => string.Equals(rule, "launch", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(rule, "globals/global_commands_launch",
                                    StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldKeepOpenOggYesNoBlankBeforeHardTimeout(
        TurnContext turn,
        WebSocketTurnState turnState,
        string messageType,
        bool allowFallbackOnMissingTranscript,
        TimeSpan turnAge)
    {
        if (!allowFallbackOnMissingTranscript ||
            !string.Equals(messageType, "AUTO_FINALIZE", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!IsYesNoTurn(turn)) return false;
        if (!string.IsNullOrWhiteSpace(turnState.LastSttError)) return false;
        if (turnAge >= ResolveHardBufferedAudioAge(turnState)) return false;
        if (!HasOggOpusFrames(turnState) || HasReceivedOggEndOfStream(turnState)) return false;

        return turnState.BufferedAudioBytes >= AutoFinalizeMinBufferedAudioBytes;
    }

    private static bool ShouldCloseHotphraseNonCommandAsNoInput(
        TurnContext turn,
        WebSocketTurnState turnState,
        string messageType,
        bool allowFallbackOnMissingTranscript,
        out string reason)
    {
        reason = string.Empty;
        if (!allowFallbackOnMissingTranscript ||
            !string.Equals(messageType, "AUTO_FINALIZE", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!IsHotphraseLaunchTurn(turn)) return false;
        if (turnState.BufferedAudioBytes < AutoFinalizeMinBufferedAudioBytes) return false;

        var normalized = NormalizeUsableTranscript(turn.NormalizedTranscript ?? turn.RawTranscript);
        if (string.IsNullOrWhiteSpace(normalized)) return false;

        var rawNormalized = NormalizeUsableTranscript(turn.RawTranscript);
        var embeddedCommand = TranscriptTextNormalizer.ExtractWakePhraseCommand(rawNormalized);
        if (!string.IsNullOrWhiteSpace(embeddedCommand) &&
            !string.Equals(embeddedCommand, rawNormalized, StringComparison.Ordinal))
        {
            var normalizedCommand = NormalizeUsableTranscript(embeddedCommand);
            if (!IsHotphraseNonCommandTranscript(normalizedCommand) &&
                !IsHotphraseRobotFallbackTranscript(normalizedCommand) &&
                !TranscriptHeuristics.IsLikelyRobotSelfAudioTranscript(normalizedCommand))
                return false;
        }

        if (IsHotphraseNonCommandTranscript(normalized))
        {
            reason = "hotphrase_non_command";
            return true;
        }

        if (TranscriptHeuristics.IsLikelySkillOfferPromptEcho(normalized))
        {
            reason = "hotphrase_prompt_echo";
            return true;
        }

        if (IsHotphraseRobotFallbackTranscript(normalized))
        {
            reason = "robot_fallback_audio";
            return true;
        }

        if (!TranscriptHeuristics.IsLikelyRobotSelfAudioTranscript(normalized)) return false;

        reason = "robot_self_audio";
        return true;
    }

    private static bool IsHotphraseNonCommandTranscript(string normalized)
    {
        return HotphraseNonCommandTranscripts.Contains(normalized);
    }

    private static bool IsHotphraseRobotFallbackTranscript(string normalized)
    {
        return HotphraseRobotFallbackTranscripts.Contains(normalized) ||
               HotphraseRobotFallbackTranscripts.Any(phrase =>
                   normalized.StartsWith($"{phrase} ", StringComparison.Ordinal));
    }

    private static bool ShouldWaitForBufferedHotphraseOnlyTranscript(
        TurnContext turn,
        WebSocketTurnState turnState,
        string messageType,
        bool allowFallbackOnMissingTranscript,
        out string reason)
    {
        reason = string.Empty;
        if (!allowFallbackOnMissingTranscript ||
            !string.Equals(messageType, "AUTO_FINALIZE", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!ReadBoolAttribute(turn, "listenHotphrase")) return false;

        if (!ReadRules(turn, "listenRules")
                .Any(static rule => string.Equals(rule, "launch", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(rule, "globals/global_commands_launch",
                                        StringComparison.OrdinalIgnoreCase)))
            return false;

        if (turnState.BufferedAudioBytes < AutoFinalizeMinBufferedAudioBytes) return false;

        var normalized = NormalizeUsableTranscript(turn.NormalizedTranscript ?? turn.RawTranscript);
        if (!IsHotphraseOnlyTranscript(normalized)) return false;

        reason = "hotphrase_only_transcript";
        return true;
    }

    private static bool ShouldDeferForIncompleteHotphraseLaunchCommand(
        TurnContext turn,
        WebSocketTurnState turnState,
        string messageType,
        bool allowFallbackOnMissingTranscript,
        out string reason)
    {
        reason = string.Empty;
        if (!allowFallbackOnMissingTranscript ||
            !string.Equals(messageType, "AUTO_FINALIZE", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!IsHotphraseLaunchTurn(turn) || !turnState.FirstAudioReceivedUtc.HasValue) return false;

        var turnAge = DateTimeOffset.UtcNow - turnState.FirstAudioReceivedUtc.Value;
        var hardAge = ResolveHardBufferedAudioAge(turnState);
        var forceCloseIncomplete =
            turnAge >= hardAge ||
            HasReceivedOggEndOfStream(turnState) ||
            HasContentSilence(turnState, AutoFinalizeHotphraseOggSilenceWindow);

        var normalized = NormalizeUsableTranscript(turn.NormalizedTranscript ?? turn.RawTranscript);
        if (string.IsNullOrWhiteSpace(normalized)) return false;
        var commandTranscript = TranscriptTextNormalizer.ExtractWakePhraseCommand(normalized);
        if (!string.IsNullOrWhiteSpace(commandTranscript))
            normalized = NormalizeUsableTranscript(commandTranscript);

        if (LooksLikeCloudVersionSelfAudioOnly(normalized))
        {
            if (forceCloseIncomplete) return false;
            reason = "cloud_version_self_audio";
            return true;
        }

        if (LooksLikeIncompleteLaunchQuestion(normalized))
        {
            if (forceCloseIncomplete) return false;

            reason = "launch_question_incomplete";
            return true;
        }

        if (normalized is not ("what time" or "what time is" or "what time is it in" or "what time is it at"))
            return false;

        if (forceCloseIncomplete) return false;

        reason = "clock_command_incomplete";
        return true;
    }

    private static TurnContext CompleteIncompleteLaunchTranscriptIfClosing(
        TurnContext turn,
        WebSocketTurnState turnState,
        string messageType,
        bool allowFallbackOnMissingTranscript)
    {
        if (!allowFallbackOnMissingTranscript ||
            !string.Equals(messageType, "AUTO_FINALIZE", StringComparison.OrdinalIgnoreCase) ||
            !IsHotphraseLaunchTurn(turn) ||
            !turnState.FirstAudioReceivedUtc.HasValue)
            return turn;

        var turnAge = DateTimeOffset.UtcNow - turnState.FirstAudioReceivedUtc.Value;
        var forceCloseIncomplete =
            turnAge >= ResolveHardBufferedAudioAge(turnState) ||
            HasReceivedOggEndOfStream(turnState) ||
            HasContentSilence(turnState, AutoFinalizeHotphraseOggSilenceWindow);
        if (!forceCloseIncomplete) return turn;

        var normalized = NormalizeUsableTranscript(turn.NormalizedTranscript ?? turn.RawTranscript);
        if (string.IsNullOrWhiteSpace(normalized)) return turn;
        var commandTranscript = TranscriptTextNormalizer.ExtractWakePhraseCommand(normalized);
        if (!string.IsNullOrWhiteSpace(commandTranscript))
            normalized = NormalizeUsableTranscript(commandTranscript);

        if (!LooksLikeIncompleteLaunchQuestion(normalized) ||
            !TryCompleteIncompleteLaunchQuestion(normalized, out var completed))
            return turn;

        return CloneTurnWithTranscript(turn, completed);
    }

    private static TurnContext CloneTurnWithTranscript(TurnContext turn, string transcript) =>
        new()
        {
            TurnId = turn.TurnId,
            SessionId = turn.SessionId,
            TimestampUtc = turn.TimestampUtc,
            InputMode = turn.InputMode,
            SourceKind = turn.SourceKind,
            WakePhrase = turn.WakePhrase,
            RawTranscript = transcript,
            NormalizedTranscript = transcript,
            DeviceId = turn.DeviceId,
            HostName = turn.HostName,
            RequestId = turn.RequestId,
            ProtocolService = turn.ProtocolService,
            ProtocolOperation = turn.ProtocolOperation,
            FirmwareVersion = turn.FirmwareVersion,
            ApplicationVersion = turn.ApplicationVersion,
            Locale = turn.Locale,
            TimeZone = turn.TimeZone,
            IsFollowUpEligible = turn.IsFollowUpEligible,
            Attributes = turn.Attributes
        };

    private static bool TryCompleteIncompleteLaunchQuestion(string normalized, out string completed)
    {
        completed = normalized switch
        {
            "how old" or "how old are" or "how old r" or "how old are yo" or "how old r yo" => "how old are you",
            "what is your ag" or "what's your ag" or "what s your ag" or "whats your ag" => "what is your age",
            "what's your favor" or "whats your favor" or "what is your favor" or
                "what's your favour" or "whats your favour" or "what is your favour" or
                "what's your favorite" or "whats your favorite" or "what s your favorite" or
                "what is your favorite" or "what's your favourite" or "whats your favourite" or
                "what s your favourite" or "what is your favourite" => "what's your favorite color",
            _ => string.Empty
        };

        return !string.IsNullOrWhiteSpace(completed);
    }

    private static bool ShouldHandleUnexpectedYesNoAutoFinalizeTranscript(
        TurnContext turn,
        WebSocketTurnState turnState,
        string messageType,
        bool allowFallbackOnMissingTranscript,
        out string reason,
        out bool closeAsNoInput,
        out TimeSpan turnAge)
    {
        reason = string.Empty;
        closeAsNoInput = false;
        turnAge = TimeSpan.Zero;

        if (!allowFallbackOnMissingTranscript ||
            !string.Equals(messageType, "AUTO_FINALIZE", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!IsYesNoTurn(turn) || !turnState.FirstAudioReceivedUtc.HasValue) return false;
        if (!HasOggOpusFrames(turnState)) return false;
        if (!string.IsNullOrWhiteSpace(turnState.LastSttError)) return false;
        if (turnState.BufferedAudioBytes < AutoFinalizeMinBufferedAudioBytes) return false;

        var normalized = NormalizeUsableTranscript(turn.NormalizedTranscript ?? turn.RawTranscript);
        if (string.IsNullOrWhiteSpace(normalized)) return false;
        if (IsAllowedYesNoAutoFinalizeTranscript(turn, normalized)) return false;
        if (TranscriptHeuristics.IsLikelyPromptEchoTranscript(normalized))
        {
            turnAge = DateTimeOffset.UtcNow - turnState.FirstAudioReceivedUtc.Value;
            reason = "yes_no_prompt_echo";
            return true;
        }

        turnAge = DateTimeOffset.UtcNow - turnState.FirstAudioReceivedUtc.Value;
        closeAsNoInput = turnAge >= ResolveHardBufferedAudioAge(turnState) || HasReceivedOggEndOfStream(turnState);
        reason = closeAsNoInput ? "unexpected_yes_no_transcript_timeout" : "unexpected_yes_no_transcript";
        return true;
    }

    private static TimeSpan ResolveHardBufferedAudioAge(WebSocketTurnState turnState)
    {
        return turnState.ListenMaxSpeechTimeout is { } maxSpeechTimeout && maxSpeechTimeout > TimeSpan.Zero
            ? maxSpeechTimeout
            : AutoFinalizeHardBufferedAudioAge;
    }

    private static TimeSpan ResolveHotphraseOnlyNoInputAge(WebSocketTurnState turnState)
    {
        return turnState.ListenMaxSpeechTimeout is { } maxSpeechTimeout && maxSpeechTimeout > TimeSpan.Zero
            ? maxSpeechTimeout
            : AutoFinalizeHotphraseOnlyNoInputAge;
    }

    private static bool IsAllowedYesNoAutoFinalizeTranscript(TurnContext turn, string normalizedTranscript)
    {
        var yesNoReply = TryClassifyYesNoReply(normalizedTranscript);
        if (yesNoReply is YesNoReply.Affirmative or YesNoReply.Negative or YesNoReply.Ambiguous) return true;

        if (!ReadRules(turn, "listenRules")
                .Concat(ReadRules(turn, "clientRules"))
                .Any(IsLaunchRule))
            return false;

        var command = TranscriptTextNormalizer.ExtractWakePhraseCommand(normalizedTranscript);
        var commandTranscript = string.IsNullOrWhiteSpace(command)
            ? normalizedTranscript
            : NormalizeUsableTranscript(command);

        return commandTranscript is "stop" or "cancel" or "never mind" or "nevermind";
    }

    private static bool TryExtractTrailingYesNoReply(TurnContext turn, out string replyTranscript)
    {
        replyTranscript = string.Empty;
        if (!IsYesNoTurn(turn)) return false;

        var normalizedTranscript = NormalizeUsableTranscript(turn.NormalizedTranscript ?? turn.RawTranscript);
        if (string.IsNullOrWhiteSpace(normalizedTranscript)) return false;

        var wholeReply = TryClassifyYesNoReply(normalizedTranscript);
        if (wholeReply is YesNoReply.Affirmative or YesNoReply.Negative &&
            normalizedTranscript.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Length <= 3)
            return false;

        if (TryFindTrailingYesNoPhrase(normalizedTranscript, out replyTranscript))
            return true;

        return false;
    }

    private static bool TryFindTrailingYesNoPhrase(string normalizedTranscript, out string replyTranscript)
    {
        replyTranscript = string.Empty;
        var tokens = normalizedTranscript.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 2) return false;

        for (var tokenCount = Math.Min(3, tokens.Length); tokenCount >= 1; tokenCount -= 1)
        {
            var candidateTokens = tokens[^tokenCount..];
            var candidate = string.Join(' ', candidateTokens);
            var reply = TryClassifyYesNoReply(candidate);
            if (reply is not (YesNoReply.Affirmative or YesNoReply.Negative)) continue;

            var prefix = string.Join(' ', tokens[..^tokenCount]);
            if (!LooksLikeYesNoPromptEchoPrefix(prefix)) continue;

            replyTranscript = candidate;
            return true;
        }

        return false;
    }

    private static bool LooksLikeYesNoPromptEchoPrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return false;
        if (TranscriptHeuristics.IsLikelyPromptEchoTranscript(prefix)) return true;

        return prefix.Contains("do you want", StringComparison.Ordinal) ||
               prefix.Contains("would you like", StringComparison.Ordinal) ||
               prefix.Contains("want to hear", StringComparison.Ordinal) ||
               prefix.Contains("hear something", StringComparison.Ordinal) ||
               prefix.Contains("yes or no", StringComparison.Ordinal);
    }

    private static bool LooksLikeCloudVersionSelfAudioOnly(string normalized)
    {
        return normalized.StartsWith("cloud version 1 ", StringComparison.Ordinal) ||
               normalized.StartsWith("cloud version one ", StringComparison.Ordinal) ||
               normalized.StartsWith("version 1 ", StringComparison.Ordinal) ||
               normalized.StartsWith("version one ", StringComparison.Ordinal) ||
               normalized.StartsWith("first 1 0 ", StringComparison.Ordinal) ||
               normalized.StartsWith("one dot zero dot twenty", StringComparison.Ordinal);
    }

    private static bool LooksLikeIncompleteLaunchQuestion(string normalized)
    {
        return normalized is "what's your" or "whats your" or "what s your" or "what is your" or
            "what's the" or "whats the" or "what s the" or "what is the" or
            "what's your cloud" or "whats your cloud" or "what s your cloud" or
            "what is your cloud" or "what's the cloud" or "whats the cloud" or
            "what s the cloud" or "what is the cloud" or
            "what's your favorite" or "whats your favorite" or "what s your favorite" or
            "what is your favorite" or "what's your favourite" or "whats your favourite" or
            "what s your favourite" or "what is your favourite" or
            "what's your favor" or "whats your favor" or "what is your favor" or
            "what's your favour" or "whats your favour" or "what is your favour" or
            "how old" or "how old are" or "how old r" or
            "how old are yo" or "how old r yo" or
            "what is your ag" or "what's your ag" or "what s your ag" or "whats your ag";
    }

    private static bool IsHotphraseOnlyTranscript(string normalized)
    {
        return TranscriptTextNormalizer.IsWakePhraseOnly(normalized) ||
               TranscriptTextNormalizer.HasTerminalWakePhraseWithoutCommand(normalized);
    }

    private static string NormalizeBufferedAudioTranscript(TurnContext turn, string transcript)
    {
        var normalized = NormalizeTranscript(transcript);
        if (string.IsNullOrWhiteSpace(normalized) || !IsHotphraseLaunchTurn(turn)) return normalized;

        var withoutWakePhrase = TranscriptTextNormalizer.ExtractWakePhraseCommand(normalized);
        return string.IsNullOrWhiteSpace(withoutWakePhrase) ? normalized : withoutWakePhrase;
    }

    private static bool IsHotphraseLaunchTurn(TurnContext turn)
    {
        if (!ReadBoolAttribute(turn, "listenHotphrase")) return false;

        return ReadRules(turn, "listenRules")
            .Concat(ReadRules(turn, "clientRules"))
            .Any(IsLaunchRule);
    }

    private static bool IsLaunchRule(string rule)
    {
        return string.Equals(rule, "launch", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "globals/global_commands_launch", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldIgnoreLateEmptyTurn(TurnContext turn, CloudSession session, string messageType)
    {
        if (messageType is not ("CLIENT_ASR" or "CLIENT_NLU")) return false;

        if (session.TurnState.AwaitingTurnCompletion || session.TurnState.BufferedAudioBytes > 0) return false;

        if (!string.IsNullOrWhiteSpace(turn.NormalizedTranscript) ||
            !string.IsNullOrWhiteSpace(turn.RawTranscript)) return false;

        var turnTransId = ReadAttribute(turn, "transID");
        return !string.IsNullOrWhiteSpace(turnTransId) &&
               string.Equals(turnTransId, session.LastTransId, StringComparison.Ordinal) &&
               !string.IsNullOrWhiteSpace(session.LastIntent);
    }

    private static bool ShouldIgnoreCompletedWordOfDayTurn(TurnContext turn)
    {
        if (!string.IsNullOrWhiteSpace(turn.NormalizedTranscript) ||
            !string.IsNullOrWhiteSpace(turn.RawTranscript)) return false;

        return ReadRules(turn, "listenRules")
            .Any(static rule => string.Equals(rule, "word-of-the-day/right_word", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldTreatBufferedHotphraseAsGreeting(
        TurnContext turn,
        WebSocketTurnState turnState,
        bool allowFallbackOnMissingTranscript)
    {
        if (!allowFallbackOnMissingTranscript || !ReadBoolAttribute(turn, "listenHotphrase")) return false;
        if (!string.IsNullOrWhiteSpace(turnState.LastSttError)) return false;

        if (!ReadRules(turn, "listenRules")
                .Any(static rule => string.Equals(rule, "launch", StringComparison.OrdinalIgnoreCase)))
            return false;

        if (!string.IsNullOrWhiteSpace(turn.NormalizedTranscript) ||
            !string.IsNullOrWhiteSpace(turn.RawTranscript)) return false;

        return turnState is
        {
            BufferedAudioBytes: >= AutoFinalizeMinBufferedAudioBytes, FinalizeAttemptCount: > 0,
            HotphraseEmptyTurnCount: > 0
        };
    }

    private static bool ShouldTreatEmptyHotphraseTurnAsGreeting(TurnContext turn, WebSocketTurnState turnState)
    {
        if (!string.IsNullOrWhiteSpace(turn.NormalizedTranscript) ||
            !string.IsNullOrWhiteSpace(turn.RawTranscript)) return false;

        if (!string.IsNullOrWhiteSpace(turnState.LastSttError)) return false;

        var messageType = ReadMessageType(turn);
        if (messageType is not ("CLIENT_ASR" or "CLIENT_NLU")) return false;

        if (!ReadBoolAttribute(turn, "listenHotphrase")) return false;

        return ReadRules(turn, "listenRules")
            .Any(static rule => string.Equals(rule, "launch", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldIgnoreInitialEmptyHotphraseTurn(TurnContext turn, WebSocketTurnState turnState)
    {
        if (!string.IsNullOrWhiteSpace(turn.NormalizedTranscript) ||
            !string.IsNullOrWhiteSpace(turn.RawTranscript)) return false;

        var messageType = ReadMessageType(turn);
        if (messageType is not ("CLIENT_ASR" or "CLIENT_NLU")) return false;

        if (!ReadBoolAttribute(turn, "listenHotphrase")) return false;

        if (turnState.HotphraseEmptyTurnCount > 0) return false;

        return ReadRules(turn, "listenRules")
            .Any(static rule => string.Equals(rule, "launch", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldDeferForLikelyContinuation(
        TurnContext turn,
        WebSocketTurnState turnState,
        string messageType,
        bool allowFallbackOnMissingTranscript,
        out string reason)
    {
        reason = string.Empty;
        if (!allowFallbackOnMissingTranscript ||
            !string.Equals(messageType, "AUTO_FINALIZE", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!turnState.FirstAudioReceivedUtc.HasValue ||
            DateTimeOffset.UtcNow - turnState.FirstAudioReceivedUtc.Value >= AutoFinalizeContinuationDeferralMaxAge ||
            turnState.FinalizeAttemptCount >= AutoFinalizeContinuationDeferralMaxAttempts)
            return false;

        var normalized = NormalizeUsableTranscript(turn.NormalizedTranscript ?? turn.RawTranscript);
        if (string.IsNullOrWhiteSpace(normalized)) return false;

        if (normalized is "my birthday" or "my birthday is")
        {
            reason = "birthday_set_incomplete";
            return true;
        }

        if (normalized.StartsWith("my favorite ", StringComparison.Ordinal) ||
            normalized.StartsWith("my favourite ", StringComparison.Ordinal))
        {
            var preferenceTail = normalized.StartsWith("my favourite ", StringComparison.Ordinal)
                ? normalized["my favourite ".Length..].Trim()
                : normalized["my favorite ".Length..].Trim();
            var missingCopula = !normalized.Contains(" is ", StringComparison.Ordinal) &&
                                !normalized.Contains(" are ", StringComparison.Ordinal);

            if (normalized.EndsWith(" is", StringComparison.Ordinal) ||
                normalized.EndsWith(" are", StringComparison.Ordinal) ||
                (missingCopula && !LooksLikeBarePreferenceSet(preferenceTail)))
            {
                reason = "preference_set_incomplete";
                return true;
            }
        }

        if (normalized.StartsWith("what s my favorite", StringComparison.Ordinal) ||
            normalized.StartsWith("what is my favorite", StringComparison.Ordinal) ||
            normalized.StartsWith("what s my favourite", StringComparison.Ordinal) ||
            normalized.StartsWith("what is my favourite", StringComparison.Ordinal))
            if (normalized is "what s my favorite" or "what is my favorite" or "what s my favourite"
                or "what is my favourite")
            {
                reason = "preference_recall_incomplete";
                return true;
            }

        if (!LooksLikeIncompleteAffinitySet(normalized)) return false;

        reason = "affinity_set_incomplete";
        return true;
    }

    private static bool LooksLikeIncompleteAffinitySet(string normalized)
    {
        return PegasusAffinityContinuationStems.Contains(normalized);
    }

    private static bool LooksLikeBarePreferenceSet(string preferenceTail)
    {
        if (string.IsNullOrWhiteSpace(preferenceTail)) return false;

        var tokens = preferenceTail.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length >= 2;
    }

    private static void ClearListenTracking(WebSocketTurnState turnState)
    {
        turnState.SawListen = false;
        turnState.SawContext = false;
        turnState.ListenOpenedUtc = null;
        turnState.ListenSosTimeout = null;
        turnState.ListenMaxSpeechTimeout = null;
    }

    private static void UpdateGlsmPhaseMarker(CloudSession session)
    {
        session.Metadata[GlsmPhaseMetadataKey] = ResolveGlsmPhase(session);
    }

    private async Task TrackGlsmPhaseAsync(
        CloudSession session,
        WebSocketMessageEnvelope envelope,
        string trigger,
        CancellationToken cancellationToken)
    {
        var nextPhase = ResolveGlsmPhase(session);
        var previousPhase = session.Metadata.TryGetValue(GlsmPhaseMetadataKey, out var rawPhase)
            ? rawPhase?.ToString()
            : null;
        session.Metadata[GlsmPhaseMetadataKey] = nextPhase;

        if (string.Equals(previousPhase, nextPhase, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            await sink.RecordTurnDiagnosticAsync("glsm_phase_transition", BuildTurnDiagnosticSnapshot(session, envelope,
                new Dictionary<string, object?>
                {
                    ["trigger"] = trigger,
                    ["previousState"] = previousPhase,
                    ["state"] = nextPhase,
                    ["listenOpenedUtc"] = session.TurnState.ListenOpenedUtc,
                    ["followUpOpen"] = session.FollowUpOpen,
                    ["listenRules"] = session.TurnState.ListenRules
                }), cancellationToken);
        }
        catch
        {
            // Diagnostics should not interrupt turn handling.
        }
    }

    private static Dictionary<string, object?> BuildTurnDiagnosticSnapshot(
        CloudSession session,
        WebSocketMessageEnvelope envelope,
        Dictionary<string, object?> details)
    {
        details["sessionToken"] = session.Token;
        details["hostName"] = envelope.HostName;
        details["path"] = envelope.Path;
        details["kind"] = envelope.Kind;
        details["transID"] = session.TurnState.TransId ?? session.LastTransId;
        details["lastMessageType"] = session.LastMessageType;
        details["awaitingTurnCompletion"] = session.TurnState.AwaitingTurnCompletion;
        details["bufferedAudioBytes"] = session.TurnState.BufferedAudioBytes;
        details["bufferedAudioChunks"] = session.TurnState.BufferedAudioChunkCount;
        var pageCounts = DescribeBufferedAudioPages(session.TurnState);
        details["bufferedAudioRawFrames"] = pageCounts.RawFrameCount;
        details["bufferedAudioRawFrames"] = pageCounts.RawFrameCount;
        details["bufferedAudioMetadataPages"] = pageCounts.MetadataPageCount;
        details["bufferedAudioAudioBearingPages"] = pageCounts.AudioBearingPageCount;
        details["sawListen"] = session.TurnState.SawListen;
        details["sawContext"] = session.TurnState.SawContext;
        details["glsmState"] = ResolveGlsmPhase(session);
        return details;
    }

    private static string[] ReadReplyTypes(IReadOnlyList<WebSocketReply> replies)
    {
        return replies
            .Select(ReadReplyType)
            .Where(replyType => !string.IsNullOrWhiteSpace(replyType))
            .ToArray();
    }

    private static string ReadReplyType(WebSocketReply reply)
    {
        if (string.IsNullOrWhiteSpace(reply.Text)) return string.Empty;

        try
        {
            using var document = JsonDocument.Parse(reply.Text);
            return document.RootElement.TryGetProperty("type", out var type) &&
                   type.ValueKind == JsonValueKind.String
                ? type.GetString() ?? string.Empty
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task RecordSttProcessingFailureDiagnosticAsync(
        CloudSession session,
        WebSocketMessageEnvelope envelope,
        string stage,
        Exception ex,
        CancellationToken cancellationToken)
    {
        await sink.RecordTurnDiagnosticAsync(
            "stt_processing_failure",
            BuildTurnDiagnosticSnapshot(session, envelope, new Dictionary<string, object?>
            {
                ["stage"] = stage,
                ["error"] = ex.Message,
                ["exceptionType"] = ex.GetType().FullName,
                ["listenRules"] = session.TurnState.ListenRules,
                ["listenAsrHints"] = session.TurnState.ListenAsrHints,
                ["audioTranscriptHint"] = session.TurnState.AudioTranscriptHint,
                ["lastSttError"] = session.TurnState.LastSttError,
                ["lastSttErrorUtc"] = session.TurnState.LastSttErrorUtc
            }),
            cancellationToken);
    }

    private static TurnContext WithSyntheticTranscript(TurnContext turn, string transcript)
    {
        var attributes = new Dictionary<string, object?>(turn.Attributes, StringComparer.OrdinalIgnoreCase)
        {
            ["syntheticTranscript"] = true
        };

        return new TurnContext
        {
            TurnId = turn.TurnId,
            SessionId = turn.SessionId,
            TimestampUtc = turn.TimestampUtc,
            InputMode = turn.InputMode,
            SourceKind = turn.SourceKind,
            WakePhrase = turn.WakePhrase,
            RawTranscript = transcript,
            NormalizedTranscript = transcript,
            DeviceId = turn.DeviceId,
            HostName = turn.HostName,
            RequestId = turn.RequestId,
            ProtocolService = turn.ProtocolService,
            ProtocolOperation = turn.ProtocolOperation,
            FirmwareVersion = turn.FirmwareVersion,
            ApplicationVersion = turn.ApplicationVersion,
            Locale = turn.Locale,
            TimeZone = turn.TimeZone,
            IsFollowUpEligible = turn.IsFollowUpEligible,
            Attributes = attributes
        };
    }

    private static TurnContext WithSanitizedTranscript(TurnContext turn, string transcript)
    {
        return new TurnContext
        {
            TurnId = turn.TurnId,
            SessionId = turn.SessionId,
            TimestampUtc = turn.TimestampUtc,
            InputMode = turn.InputMode,
            SourceKind = turn.SourceKind,
            WakePhrase = turn.WakePhrase,
            RawTranscript = transcript,
            NormalizedTranscript = transcript,
            DeviceId = turn.DeviceId,
            HostName = turn.HostName,
            RequestId = turn.RequestId,
            ProtocolService = turn.ProtocolService,
            ProtocolOperation = turn.ProtocolOperation,
            FirmwareVersion = turn.FirmwareVersion,
            ApplicationVersion = turn.ApplicationVersion,
            Locale = turn.Locale,
            TimeZone = turn.TimeZone,
            IsFollowUpEligible = turn.IsFollowUpEligible,
            Attributes = turn.Attributes
        };
    }

    private static bool ReadBoolAttribute(TurnContext turn, string key)
    {
        if (!turn.Attributes.TryGetValue(key, out var value) || value is null) return false;

        return value switch
        {
            bool boolValue => boolValue,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ when bool.TryParse(value.ToString(), out var parsed) => parsed,
            _ => false
        };
    }

    private enum YesNoReply
    {
        None = 0,
        Affirmative = 1,
        Negative = 2,
        Ambiguous = 3
    }
}
