using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Domain.Models;
using Jibo.Runtime.Abstractions;
using System.Text.RegularExpressions;

namespace Jibo.Cloud.Application.Services;

public sealed partial class WebSocketTurnFinalizationService(
    IConversationBroker conversationBroker,
    ISttStrategySelector sttStrategySelector,
    ITurnTelemetrySink sink
)
{
    private const int AutoFinalizeMinBufferedAudioBytes = 12000;
    private const int AutoFinalizeMinBufferedAudioChunks = 4;
    private static readonly TimeSpan AutoFinalizeMinTurnAge = TimeSpan.FromMilliseconds(1400);

    public static void ObserveIncomingMessage(CloudSession session, string? text)
    {
        if (!TryReadTransId(text, out var nextTransId) || string.IsNullOrWhiteSpace(nextTransId))
        {
            return;
        }

        if (!string.Equals(session.TurnState.TransId, nextTransId, StringComparison.Ordinal))
        {
            ResetTurnState(session.TurnState, nextTransId);
        }

        session.LastTransId = nextTransId;
    }

    public async Task<IReadOnlyList<WebSocketReply>> HandleBinaryAudioAsync(
        CloudSession session,
        WebSocketMessageEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        var turnState = session.TurnState;
        var ignoreLateAudio = ShouldIgnoreLateAudio(session);
        var ignoreAudioWithoutListen = ShouldIgnoreAudioWithoutListen(turnState);
        if (ignoreLateAudio || ignoreAudioWithoutListen)
        {
            await sink.RecordTurnDiagnosticAsync("binary_audio_ignored", BuildTurnDiagnosticSnapshot(session, envelope, new Dictionary<string, object?>
            {
                ["ignored"] = true,
                ["ignoreLateAudio"] = ignoreLateAudio,
                ["ignoreAudioWithoutListen"] = ignoreAudioWithoutListen,
                ["awaitingTurnCompletion"] = turnState.AwaitingTurnCompletion,
                ["bufferedAudioBytes"] = turnState.BufferedAudioBytes,
                ["bufferedAudioChunks"] = turnState.BufferedAudioChunkCount,
                ["sawListen"] = turnState.SawListen,
                ["sawContext"] = turnState.SawContext
            }), cancellationToken);
            return [];
        }

        session.LastMessageType = "BINARY_AUDIO";
        turnState.FirstAudioReceivedUtc ??= DateTimeOffset.UtcNow;
        turnState.BufferedAudioChunkCount += 1;
        turnState.BufferedAudioBytes += envelope.Binary?.Length ?? 0;
        if (envelope.Binary is { Length: > 0 })
        {
            turnState.BufferedAudioFrames.Add([.. envelope.Binary]);
        }
        turnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow;
        turnState.AwaitingTurnCompletion = true;
        session.Metadata["lastAudioBytes"] = envelope.Binary?.Length ?? 0;
        await sink.RecordTurnDiagnosticAsync("binary_audio_received", BuildTurnDiagnosticSnapshot(session, envelope, new Dictionary<string, object?>
        {
            ["bufferedAudioBytes"] = turnState.BufferedAudioBytes,
            ["bufferedAudioChunks"] = turnState.BufferedAudioChunkCount,
            ["awaitingTurnCompletion"] = turnState.AwaitingTurnCompletion,
            ["sawListen"] = turnState.SawListen,
            ["sawContext"] = turnState.SawContext,
            ["listenRules"] = turnState.ListenRules,
            ["listenAsrHints"] = turnState.ListenAsrHints,
            ["yesNoRule"] = turnState.ListenRules.FirstOrDefault(IsConstrainedYesNoRule)
        }), cancellationToken);

        if (ShouldAutoFinalize(session))
        {
            return await FinalizeTurnAsync(session, envelope, "AUTO_FINALIZE", allowFallbackOnMissingTranscript: true, cancellationToken);
        }

        return [];
    }

    public async Task<IReadOnlyList<WebSocketReply>> HandleContextAsync(
        CloudSession session,
        WebSocketMessageEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        var turnState = session.TurnState;
        turnState.SawContext = true;
        turnState.ContextPayload = ExtractDataPayload(envelope.Text);
        session.Metadata["context"] = turnState.ContextPayload;

        if (TryReadContextProperty(envelope.Text, "audioTranscriptHint", out var transcriptHint) &&
            !string.IsNullOrWhiteSpace(transcriptHint))
        {
            turnState.AudioTranscriptHint = transcriptHint;
            session.Metadata["audioTranscriptHint"] = transcriptHint;
        }

        if (ShouldIgnorePassiveLocalSkillContext(session, envelope.Text))
        {
            turnState.AwaitingTurnCompletion = false;
            turnState.IgnoreAdditionalAudioUntilUtc = DateTimeOffset.UtcNow.Add(WebSocketTurnState.DefaultLateAudioIgnoreWindow);
            ResetBufferedAudio(session);
            turnState.SawListen = false;
            turnState.SawContext = false;
            return [];
        }

        if (ShouldAutoFinalize(session))
        {
            return await FinalizeTurnAsync(session, envelope, "AUTO_FINALIZE", allowFallbackOnMissingTranscript: true, cancellationToken);
        }

        return [];
    }

    public async Task<IReadOnlyList<WebSocketReply>> HandleTurnAsync(
        CloudSession session,
        WebSocketMessageEnvelope envelope,
        string messageType,
        CancellationToken cancellationToken = default)
    {
        PersistTurnHints(session, envelope.Text);
        return await FinalizeTurnAsync(session, envelope, messageType, allowFallbackOnMissingTranscript: false, cancellationToken);
    }

    public static IReadOnlyList<WebSocketReply> HandleListenSetup(CloudSession session, WebSocketMessageEnvelope envelope)
    {
        PersistTurnHints(session, envelope.Text);

        var turn = ProtocolToTurnContextMapper.MapListenMessage(envelope, session, "LISTEN");
        if (ShouldIgnoreCompletedWordOfDayTurn(turn))
        {
            session.TurnState.AwaitingTurnCompletion = false;
            session.TurnState.IgnoreAdditionalAudioUntilUtc = DateTimeOffset.UtcNow.Add(WebSocketTurnState.DefaultLateAudioIgnoreWindow);
            session.FollowUpExpiresUtc = null;
            ResetBufferedAudio(session);
            session.TurnState.SawListen = false;
            session.TurnState.SawContext = false;
            return [.. ResponsePlanToSocketMessagesMapper.MapNoInputAndRedirectToSkill(
                    session.TurnState.TransId ?? session.LastTransId ?? string.Empty,
                    session.TurnState.ListenRules,
                    "@be/idle")
                .Select(map => new WebSocketReply
                {
                    Text = map.Text,
                    DelayMs = map.DelayMs
                })];
        }

        session.TurnState.AwaitingTurnCompletion = true;
        return [];
    }

    private async Task<TurnContext> ResolveTranscriptAsync(TurnContext turn, CloudSession session, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(turn.NormalizedTranscript) || !string.IsNullOrWhiteSpace(turn.RawTranscript) || session.TurnState.BufferedAudioBytes <= 0)
        {
            return turn;
        }

        ISttStrategy? strategy;
        try
        {
            strategy = await sttStrategySelector.SelectAsync(turn, cancellationToken);
        }
        catch (InvalidOperationException ex) when (string.Equals(ex.Message, "No STT strategy can handle the current turn.", StringComparison.Ordinal))
        {
            return turn;
        }
        catch (Exception ex)
        {
            session.TurnState.LastSttError = ex.Message;
            session.TurnState.LastSttErrorUtc = DateTimeOffset.UtcNow;
            await sink.RecordTranscriptError(ex, "Error during STT processing", cancellationToken);
            return turn;
        }

        try
        {
            var sttResult = await strategy.TranscribeAsync(turn, cancellationToken);
            session.TurnState.LastSttError = null;
            session.TurnState.LastSttErrorUtc = null;

            var attributes = new Dictionary<string, object?>(turn.Attributes, StringComparer.OrdinalIgnoreCase)
            {
                ["sttProvider"] = sttResult.Provider,
                ["sttConfidence"] = sttResult.Confidence
            };

            foreach (var pair in sttResult.Metadata)
            {
                attributes[$"stt:{pair.Key}"] = pair.Value;
            }

            return new TurnContext
            {
                TurnId = turn.TurnId,
                SessionId = turn.SessionId,
                TimestampUtc = turn.TimestampUtc,
                InputMode = turn.InputMode,
                SourceKind = turn.SourceKind,
                WakePhrase = turn.WakePhrase,
                RawTranscript = sttResult.Text,
                NormalizedTranscript = sttResult.Text.Trim(),
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
        }
        catch (Exception ex)
        {
            session.TurnState.LastSttError = ex.Message;
            session.TurnState.LastSttErrorUtc = DateTimeOffset.UtcNow;
            await sink.RecordTranscriptError(ex, "Error during STT processing", cancellationToken);
            return turn;
        }
    }

    private static void PersistTurnHints(CloudSession session, string? text)
    {
        var turnState = session.TurnState;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            if (root.TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String &&
                string.Equals(type.GetString(), "LISTEN", StringComparison.OrdinalIgnoreCase))
            {
                turnState.SawListen = true;
            }

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

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return;

            if (data.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array)
            {
                turnState.ListenRules = [.. rules.EnumerateArray()
                    .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : item.ToString())
                    .Where(rule => !string.IsNullOrWhiteSpace(rule))];
                session.Metadata["listenRules"] = turnState.ListenRules;
            }

            if (data.TryGetProperty("asr", out var asr) &&
                asr.ValueKind == JsonValueKind.Object &&
                asr.TryGetProperty("hints", out var hints) &&
                hints.ValueKind == JsonValueKind.Array)
            {
                turnState.ListenAsrHints = [.. hints.EnumerateArray()
                    .Where(static item => item.ValueKind == JsonValueKind.String)
                    .Select(static item => item.GetString() ?? string.Empty)
                    .Where(static hint => !string.IsNullOrWhiteSpace(hint))];
            }

            if (data.TryGetProperty("hotphrase", out var hotphrase) &&
                hotphrase.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                turnState.ListenHotphrase = hotphrase.GetBoolean();
                turnState.HotphraseEmptyTurnCount = 0;
            }

            if (data.TryGetProperty("intent", out var intent) && intent.ValueKind == JsonValueKind.String)
            {
                session.LastIntent = intent.GetString();
            }

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
        session.TurnState.BufferedAudioBytes = 0;
        session.TurnState.BufferedAudioChunkCount = 0;
        session.TurnState.LastSttError = null;
        session.TurnState.LastSttErrorUtc = null;
        session.TurnState.FirstAudioReceivedUtc = null;
        session.TurnState.LastAudioReceivedUtc = null;
        session.TurnState.BufferedAudioFrames.Clear();
        session.TurnState.FinalizeAttemptCount = 0;
        session.Metadata.Remove("audioTranscriptHint");
    }

    private static void ResetTurnState(WebSocketTurnState turnState, string? transId)
    {
        turnState.TransId = transId;
        turnState.ContextPayload = null;
        turnState.AudioTranscriptHint = null;
        turnState.LastSttError = null;
        turnState.LastSttErrorUtc = null;
        turnState.FirstAudioReceivedUtc = null;
        turnState.LastAudioReceivedUtc = null;
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
        turnState.ListenRules = [];
        turnState.ListenAsrHints = [];
    }

    private async Task<IReadOnlyList<WebSocketReply>> FinalizeTurnAsync(
        CloudSession session,
        WebSocketMessageEnvelope envelope,
        string messageType,
        bool allowFallbackOnMissingTranscript,
        CancellationToken cancellationToken)
    {
        var turn = ProtocolToTurnContextMapper.MapListenMessage(envelope, session, messageType);
        var turnState = session.TurnState;
        if (IsYesNoTurn(turn) || ReadPrimaryYesNoRule(turn) is not null)
        {
            await sink.RecordTurnDiagnosticAsync("yes_no_turn_received", BuildTurnDiagnosticSnapshot(session, envelope, new Dictionary<string, object?>
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
        }
        if (ShouldIgnoreBlankAudioHotphraseTurn(turn))
        {
            session.TurnState.AwaitingTurnCompletion = false;
            session.TurnState.IgnoreAdditionalAudioUntilUtc = DateTimeOffset.UtcNow.Add(WebSocketTurnState.DefaultLateAudioIgnoreWindow);
            session.FollowUpExpiresUtc = null;
            ResetBufferedAudio(session);
            session.TurnState.SawListen = false;
            session.TurnState.SawContext = false;
            return [];
        }

        var finalizedTurn = await ResolveTranscriptAsync(turn, session, cancellationToken);
        if (!IsTranscriptUsable(finalizedTurn))
        {
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
        }

        if (ShouldTreatBufferedHotphraseAsGreeting(finalizedTurn, turnState, allowFallbackOnMissingTranscript))
        {
            finalizedTurn = WithSyntheticTranscript(finalizedTurn, "hello");
        }

        if (ShouldIgnoreCompletedWordOfDayTurn(finalizedTurn))
        {
            turnState.AwaitingTurnCompletion = false;
            turnState.IgnoreAdditionalAudioUntilUtc = DateTimeOffset.UtcNow.Add(WebSocketTurnState.DefaultLateAudioIgnoreWindow);
            session.FollowUpExpiresUtc = null;
            ResetBufferedAudio(session);
            turnState.SawListen = false;
            turnState.SawContext = false;
            return [.. ResponsePlanToSocketMessagesMapper.MapNoInputAndRedirectToSkill(
                    turnState.TransId ?? session.LastTransId ?? string.Empty,
                    turnState.ListenRules,
                    "@be/idle")
                .Select(map => new WebSocketReply
                {
                    Text = map.Text,
                    DelayMs = map.DelayMs
                })];
        }

        if (ShouldHandleAsLocalNoInput(finalizedTurn))
        {
            if (IsYesNoTurn(finalizedTurn))
            {
                await sink.RecordTurnDiagnosticAsync("yes_no_no_input", BuildTurnDiagnosticSnapshot(session, envelope, new Dictionary<string, object?>
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
            }
            turnState.AwaitingTurnCompletion = false;
            session.LastTranscript = string.Empty;
            session.LastIntent = null;
            session.LastListenType = "no-input";
            var localRule = ReadPrimaryNoInputRule(finalizedTurn);
            var noInputReplies = BuildLocalNoInputReplies(session, turnState, localRule);
            ResetBufferedAudio(session);
            turnState.SawListen = false;
            turnState.SawContext = false;
            return noInputReplies;
        }

        if (ShouldIgnoreInitialEmptyHotphraseTurn(finalizedTurn, turnState))
        {
            turnState.HotphraseEmptyTurnCount += 1;
            turnState.AwaitingTurnCompletion = true;
            return [];
        }

        if (ShouldTreatEmptyHotphraseTurnAsGreeting(finalizedTurn))
        {
            finalizedTurn = WithSyntheticTranscript(finalizedTurn, "hello");
        }

        if (ShouldIgnoreLateEmptyTurn(finalizedTurn, session, messageType))
        {
            turnState.AwaitingTurnCompletion = false;
            ResetBufferedAudio(session);
            return [];
        }

        if (string.IsNullOrWhiteSpace(finalizedTurn.NormalizedTranscript) &&
            string.IsNullOrWhiteSpace(finalizedTurn.RawTranscript))
        {
            turnState.AwaitingTurnCompletion = true;
            if (turnState.BufferedAudioBytes > 0)
            {
                turnState.FinalizeAttemptCount += 1;
            }

            switch (allowFallbackOnMissingTranscript)
            {
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
                    return noInputReplies;
                }
                case true when
                    turnState.BufferedAudioBytes >= AutoFinalizeMinBufferedAudioBytes &&
                    string.IsNullOrWhiteSpace(turnState.LastSttError):
                {
                    turnState.AwaitingTurnCompletion = false;
                    session.LastTranscript = string.Empty;
                    session.LastIntent = "heyJibo";
                    session.LastListenType = "fallback";
                    var fallbackReplies = ResponsePlanToSocketMessagesMapper.MapFallback(session, turnState.TransId ?? session.LastTransId ?? string.Empty, turnState.ListenRules)
                        .Select(map => new WebSocketReply { Text = map.Text, DelayMs = map.DelayMs })
                        .ToArray();
                    ResetBufferedAudio(session);
                    return fallbackReplies;
                }
                default:
                    return [];
            }
        }

        var plan = await conversationBroker.HandleTurnAsync(finalizedTurn, cancellationToken);
        var listenAction = plan.Actions.OfType<ListenAction>().OrderBy(action => action.Sequence).LastOrDefault();
        session.LastTranscript = finalizedTurn.NormalizedTranscript ?? finalizedTurn.RawTranscript;
        session.LastIntent = plan.IntentName;
        session.LastListenType = listenAction?.Mode;
        turnState.LastLocalNoInputRule = null;
        turnState.LocalNoInputCount = 0;
        if (plan.Actions.OfType<InvokeNativeSkillAction>().FirstOrDefault() is { SkillName: "@be/clock" } clockAction &&
            clockAction.Payload.TryGetValue("domain", out var lastClockDomainValue) &&
            lastClockDomainValue is not null)
        {
            session.Metadata["lastClockDomain"] = lastClockDomainValue.ToString();
        }

        UpdatePendingProactivityOffer(session, plan.IntentName);

        session.FollowUpExpiresUtc = plan.FollowUp.KeepMicOpen
            ? DateTimeOffset.UtcNow.Add(plan.FollowUp.Timeout)
            : null;
        turnState.AwaitingTurnCompletion = false;
        turnState.IgnoreAdditionalAudioUntilUtc = plan.FollowUp.KeepMicOpen
            ? null
            : DateTimeOffset.UtcNow.Add(ResolveLateAudioIgnoreWindow(plan));

        var emitSkillActions = !string.Equals(plan.IntentName, "word_of_the_day", StringComparison.OrdinalIgnoreCase) &&
                               !string.Equals(plan.IntentName, "radio", StringComparison.OrdinalIgnoreCase) &&
                               !string.Equals(plan.IntentName, "radio_genre", StringComparison.OrdinalIgnoreCase) &&
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
                                (messageType != "CLIENT_NLU" ||
                                 string.Equals(plan.IntentName, "word_of_the_day_guess", StringComparison.OrdinalIgnoreCase));
        var replies = ResponsePlanToSocketMessagesMapper.Map(plan, finalizedTurn, session, emitSkillActions).Select(map => new WebSocketReply
        {
            Text = map.Text,
            DelayMs = map.DelayMs
        }).ToArray();

        if (IsYesNoTurn(finalizedTurn))
        {
            await sink.RecordTurnDiagnosticAsync("yes_no_turn_resolved", BuildTurnDiagnosticSnapshot(session, envelope, new Dictionary<string, object?>
            {
                ["messageType"] = messageType,
                ["transcript"] = finalizedTurn.NormalizedTranscript ?? finalizedTurn.RawTranscript,
                ["intent"] = plan.IntentName,
                ["listenRules"] = ReadRules(finalizedTurn, "listenRules").ToArray(),
                ["clientRules"] = ReadRules(finalizedTurn, "clientRules").ToArray(),
                ["listenAsrHints"] = ReadRules(finalizedTurn, "listenAsrHints").ToArray(),
                ["awaitingTurnCompletion"] = turnState.AwaitingTurnCompletion,
                ["bufferedAudioBytes"] = turnState.BufferedAudioBytes,
                ["bufferedAudioChunks"] = turnState.BufferedAudioChunkCount,
                ["followUpOpen"] = session.FollowUpOpen,
                ["followUpExpiresUtc"] = session.FollowUpExpiresUtc
            }), cancellationToken);
        }

        ResetBufferedAudio(session);
        turnState.SawListen = false;
        turnState.SawContext = false;
        return replies;
    }

    private static bool ShouldAutoFinalize(CloudSession session)
    {
        var turnState = session.TurnState;
        var turnAge = turnState.FirstAudioReceivedUtc.HasValue
            ? DateTimeOffset.UtcNow - turnState.FirstAudioReceivedUtc.Value
            : TimeSpan.Zero;
        return turnState is { AwaitingTurnCompletion: true, SawListen: true, BufferedAudioChunkCount: >= AutoFinalizeMinBufferedAudioChunks, BufferedAudioBytes: >= AutoFinalizeMinBufferedAudioBytes } &&
               turnAge >= AutoFinalizeMinTurnAge;
    }

    private static bool ShouldIgnoreLateAudio(CloudSession session)
    {
        var ignoreUntilUtc = session.TurnState.IgnoreAdditionalAudioUntilUtc;
        return !session.TurnState.AwaitingTurnCompletion &&
               !session.FollowUpOpen &&
               ignoreUntilUtc.HasValue &&
               ignoreUntilUtc.Value > DateTimeOffset.UtcNow;
    }

    public static bool ShouldIgnoreLateListenSetup(CloudSession session, string? text)
    {
        return ShouldIgnoreLateAudio(session) && IsHotphraseLaunchListenSetup(text);
    }

    private static TimeSpan ResolveLateAudioIgnoreWindow(ResponsePlan plan)
    {
        return string.Equals(plan.IntentName, "cloud_version", StringComparison.OrdinalIgnoreCase)
            ? WebSocketTurnState.DiagnosticSpeechLateAudioIgnoreWindow
            : WebSocketTurnState.DefaultLateAudioIgnoreWindow;
    }

    private static bool ShouldIgnoreAudioWithoutListen(WebSocketTurnState turnState)
    {
        return !turnState.SawListen &&
               !string.IsNullOrWhiteSpace(turnState.TransId);
    }

    private static bool IsHotphraseLaunchListenSetup(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var isHotphrase = data.TryGetProperty("hotphrase", out var hotphrase) &&
                              hotphrase.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                              hotphrase.GetBoolean();
            if (!isHotphrase ||
                !data.TryGetProperty("rules", out var rules) ||
                rules.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            return rules.EnumerateArray()
                .Where(static rule => rule.ValueKind == JsonValueKind.String)
                .Select(static rule => rule.GetString())
                .Any(static rule => string.Equals(rule, "launch", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(rule, "globals/global_commands_launch", StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ShouldIgnorePassiveLocalSkillContext(CloudSession session, string? text)
    {
        if (session.FollowUpOpen)
        {
            return false;
        }

        if (HasCloudHandledLocalPromptOpen(session.TurnState))
        {
            return false;
        }

        var skillId = TryReadContextSkillId(text);
        return string.Equals(skillId, "@be/gallery", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(skillId, "@be/create", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(skillId, "@be/settings", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasCloudHandledLocalPromptOpen(WebSocketTurnState turnState)
    {
        return turnState is { AwaitingTurnCompletion: true, SawListen: true } &&
               turnState.ListenRules.Any(rule =>
                   IsClockValueRule(rule) ||
                   IsGalleryPreviewRule(rule) ||
                   IsConstrainedYesNoRule(rule));
    }

    private static string? ExtractDataPayload(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.TryGetProperty("data", out var data))
            {
                return data.GetRawText();
            }
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
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty(propertyName, out var property) ||
                property.ValueKind != JsonValueKind.String)
            {
                return false;
            }

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
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("skill", out var skill) ||
                !skill.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.String)
            {
                return null;
            }

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
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("transID", out var transIdProperty) ||
                transIdProperty.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            transId = transIdProperty.GetString();
            return !string.IsNullOrWhiteSpace(transId);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTranscriptUsable(TurnContext turn)
    {
        var messageType = ReadMessageType(turn);
        var clientIntent = ReadAttribute(turn, "clientIntent");
        var pendingProactivityOffer = ReadAttribute(turn, "pendingProactivityOffer");
        var transcript = NormalizeTranscript(turn.NormalizedTranscript ?? turn.RawTranscript);
        var listenRules = ReadRules(turn, "listenRules").Concat(ReadRules(turn, "clientRules")).ToArray();

        if (string.Equals(messageType, "CLIENT_NLU", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(clientIntent))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(transcript))
        {
            return false;
        }

        if (transcript is "blank_audio" or "blank audio")
        {
            return false;
        }

        if (listenRules.Any(IsClockValueRule))
        {
            return true;
        }

        if (transcript.Length >= 6)
        {
            return true;
        }

        if (IsYesNoTurn(turn) && transcript is "yes" or "no" or "sure" or "nope" or "yup" or "uh huh" or "yeah" or "nah")
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(pendingProactivityOffer) &&
            transcript is "yes" or "no" or "sure" or "nope" or "yup" or "uh huh" or "yeah" or "nah")
        {
            return true;
        }

        if (listenRules.Any(rule => string.Equals(rule, "word-of-the-day/puzzle", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return transcript is "joke" or "dance" or "time" or "date" or "today" or "day" or "hello" or "hi" or "hey";
    }

    private static bool IsYesNoTurn(TurnContext turn)
    {
        return ReadRules(turn, "listenRules")
            .Concat(ReadRules(turn, "clientRules"))
            .Concat(ReadRules(turn, "listenAsrHints"))
            .Any(IsYesNoRule);
    }

    private static bool ShouldHandleAsLocalNoInput(TurnContext turn)
    {
        if (!string.IsNullOrWhiteSpace(turn.NormalizedTranscript) || !string.IsNullOrWhiteSpace(turn.RawTranscript))
        {
            return false;
        }

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
            .FirstOrDefault(IsConstrainedYesNoRule);
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

        turnState.LocalNoInputCount = string.Equals(turnState.LastLocalNoInputRule, localRule, StringComparison.OrdinalIgnoreCase)
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
               IsClockValueRule(rule) ||
               IsGalleryPreviewRule(rule) ||
               IsConstrainedYesNoRule(rule);
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

    private static bool IsConstrainedYesNoRule(string rule)
    {
        return string.Equals(rule, "clock/alarm_timer_change", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "clock/alarm_timer_none_set", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "create/is_it_a_keeper", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "shared/yes_no", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "settings/download_now_later", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "surprises-date/offer_date_fact", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "surprises-ota/want_to_download_now", StringComparison.OrdinalIgnoreCase);
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
        if (!turn.Attributes.TryGetValue(key, out var value) || value is null)
        {
            return [];
        }

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

    private static string NormalizeTranscript(string? transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return string.Empty;
        }

        return TranscriptNormalizationRegex().Replace(transcript.Trim().ToLowerInvariant(), " ")
            .Replace("  ", " ", StringComparison.Ordinal)
            .Trim();
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
        if (transcript is not ("blank_audio" or "blank audio"))
        {
            return false;
        }

        return ReadRules(turn, "listenRules")
            .Any(static rule => string.Equals(rule, "launch", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldIgnoreLateEmptyTurn(TurnContext turn, CloudSession session, string messageType)
    {
        if (messageType is not ("CLIENT_ASR" or "CLIENT_NLU"))
        {
            return false;
        }

        if (session.TurnState.AwaitingTurnCompletion || session.TurnState.BufferedAudioBytes > 0)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(turn.NormalizedTranscript) || !string.IsNullOrWhiteSpace(turn.RawTranscript))
        {
            return false;
        }

        var turnTransId = ReadAttribute(turn, "transID");
        return !string.IsNullOrWhiteSpace(turnTransId) &&
               string.Equals(turnTransId, session.LastTransId, StringComparison.Ordinal) &&
               !string.IsNullOrWhiteSpace(session.LastIntent);
    }

    private static bool ShouldIgnoreCompletedWordOfDayTurn(TurnContext turn)
    {
        if (!string.IsNullOrWhiteSpace(turn.NormalizedTranscript) || !string.IsNullOrWhiteSpace(turn.RawTranscript))
        {
            return false;
        }

        return ReadRules(turn, "listenRules")
            .Any(static rule => string.Equals(rule, "word-of-the-day/right_word", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldTreatBufferedHotphraseAsGreeting(
        TurnContext turn,
        WebSocketTurnState turnState,
        bool allowFallbackOnMissingTranscript)
    {
        if (!allowFallbackOnMissingTranscript || !ReadBoolAttribute(turn, "listenHotphrase"))
        {
            return false;
        }

        if (!ReadRules(turn, "listenRules")
            .Any(static rule => string.Equals(rule, "launch", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(turn.NormalizedTranscript) || !string.IsNullOrWhiteSpace(turn.RawTranscript))
        {
            return false;
        }

        return turnState.BufferedAudioBytes >= AutoFinalizeMinBufferedAudioBytes;
    }

    private static bool ShouldTreatEmptyHotphraseTurnAsGreeting(TurnContext turn)
    {
        if (!string.IsNullOrWhiteSpace(turn.NormalizedTranscript) || !string.IsNullOrWhiteSpace(turn.RawTranscript))
        {
            return false;
        }

        var messageType = ReadMessageType(turn);
        if (messageType is not ("CLIENT_ASR" or "CLIENT_NLU"))
        {
            return false;
        }

        if (!ReadBoolAttribute(turn, "listenHotphrase"))
        {
            return false;
        }

        return ReadRules(turn, "listenRules")
            .Any(static rule => string.Equals(rule, "launch", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldIgnoreInitialEmptyHotphraseTurn(TurnContext turn, WebSocketTurnState turnState)
    {
        if (!string.IsNullOrWhiteSpace(turn.NormalizedTranscript) || !string.IsNullOrWhiteSpace(turn.RawTranscript))
        {
            return false;
        }

        var messageType = ReadMessageType(turn);
        if (messageType is not ("CLIENT_ASR" or "CLIENT_NLU"))
        {
            return false;
        }

        if (!ReadBoolAttribute(turn, "listenHotphrase"))
        {
            return false;
        }

        if (turnState.HotphraseEmptyTurnCount > 0)
        {
            return false;
        }

        return ReadRules(turn, "listenRules")
            .Any(static rule => string.Equals(rule, "launch", StringComparison.OrdinalIgnoreCase));
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
        details["sawListen"] = session.TurnState.SawListen;
        details["sawContext"] = session.TurnState.SawContext;
        return details;
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

    private static bool ReadBoolAttribute(TurnContext turn, string key)
    {
        if (!turn.Attributes.TryGetValue(key, out var value) || value is null)
        {
            return false;
        }

        return value switch
        {
            bool boolValue => boolValue,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ when bool.TryParse(value.ToString(), out var parsed) => parsed,
            _ => false
        };
    }

    [GeneratedRegex(@"[^\w\s]")]
    private static partial Regex TranscriptNormalizationRegex();
}
