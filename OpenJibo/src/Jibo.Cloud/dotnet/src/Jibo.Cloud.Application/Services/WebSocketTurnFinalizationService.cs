using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Domain.Models;
using Jibo.Runtime.Abstractions;
using System.Text.RegularExpressions;

namespace Jibo.Cloud.Application.Services;

public sealed class WebSocketTurnFinalizationService(
    ProtocolToTurnContextMapper turnContextMapper,
    IConversationBroker conversationBroker,
    ResponsePlanToSocketMessagesMapper replyMapper,
    ISttStrategySelector sttStrategySelector,
    ITurnTelemetrySink sink
)
{
    private const int AutoFinalizeMinBufferedAudioBytes = 12000;
    private const int AutoFinalizeMinBufferedAudioChunks = 5;
    private static readonly TimeSpan AutoFinalizeMinTurnAge = TimeSpan.FromMilliseconds(1400);

    public void ObserveIncomingMessage(CloudSession session, string? text)
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
        if (ShouldIgnoreLateAudio(session))
        {
            return [];
        }

        session.LastMessageType = "BINARY_AUDIO";
        turnState.FirstAudioReceivedUtc ??= DateTimeOffset.UtcNow;
        turnState.BufferedAudioChunkCount += 1;
        turnState.BufferedAudioBytes += envelope.Binary?.Length ?? 0;
        if (envelope.Binary is { Length: > 0 })
        {
            turnState.BufferedAudioFrames.Add(envelope.Binary.ToArray());
        }
        turnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow;
        turnState.AwaitingTurnCompletion = true;
        session.Metadata["lastAudioBytes"] = envelope.Binary?.Length ?? 0;

        if (ShouldAutoFinalize(session))
        {
            return await FinalizeTurnAsync(session, envelope, "AUTO_FINALIZE", allowFallbackOnMissingTranscript: true, cancellationToken);
        }

        return
        [
            new WebSocketReply
            {
                Text = JsonSerializer.Serialize(new
                {
                    type = "OPENJIBO_AUDIO_RECEIVED",
                    data = new
                    {
                        bytes = envelope.Binary?.Length ?? 0,
                        bufferedBytes = turnState.BufferedAudioBytes,
                        bufferedChunks = turnState.BufferedAudioChunkCount,
                        sessionId = session.SessionId
                    }
                })
            }
        ];
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

        if (ShouldAutoFinalize(session))
        {
            return await FinalizeTurnAsync(session, envelope, "AUTO_FINALIZE", allowFallbackOnMissingTranscript: true, cancellationToken);
        }

        return
        [
            new WebSocketReply
            {
                Text = JsonSerializer.Serialize(new
                {
                    type = "OPENJIBO_CONTEXT_ACK",
                    data = new
                    {
                        sessionId = session.SessionId,
                        transID = session.LastTransId
                    }
                })
            }
        ];
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

    public IReadOnlyList<WebSocketReply> HandleListenSetup(CloudSession session, WebSocketMessageEnvelope envelope)
    {
        PersistTurnHints(session, envelope.Text);

        var turn = ProtocolToTurnContextMapper.MapListenMessage(envelope, session, "LISTEN");
        if (ShouldIgnoreCompletedWordOfDayTurn(turn))
        {
            session.TurnState.AwaitingTurnCompletion = false;
            session.TurnState.IgnoreAdditionalAudioUntilUtc = DateTimeOffset.UtcNow.Add(WebSocketTurnState.DefaultLateAudioIgnoreWindow);
            session.FollowUpExpiresUtc = null;
            ResetBufferedAudio(session);
            return ResponsePlanToSocketMessagesMapper.MapCompletionOnly(
                    session.TurnState.TransId ?? session.LastTransId ?? string.Empty,
                    "@be/word-of-the-day")
                .Select(map => new WebSocketReply
                {
                    Text = map.Text,
                    DelayMs = map.DelayMs
                })
                .ToArray();
        }

        session.TurnState.AwaitingTurnCompletion = true;
        return
        [
            new WebSocketReply
            {
                Text = JsonSerializer.Serialize(new
                {
                    type = "OPENJIBO_TURN_PENDING",
                    data = new
                    {
                        sessionId = session.SessionId,
                        transID = session.LastTransId,
                        bufferedAudioBytes = session.TurnState.BufferedAudioBytes,
                        bufferedAudioChunks = session.TurnState.BufferedAudioChunkCount,
                        awaitingAudio = session.TurnState.BufferedAudioBytes == 0,
                        awaitingTranscriptHint = session.TurnState.BufferedAudioBytes > 0 && string.IsNullOrWhiteSpace(session.TurnState.AudioTranscriptHint),
                        finalizeAttempts = session.TurnState.FinalizeAttemptCount
                    }
                })
            }
        ];
    }

    private async Task<TurnContext> ResolveTranscriptAsync(TurnContext turn, CloudSession session, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(turn.NormalizedTranscript) || !string.IsNullOrWhiteSpace(turn.RawTranscript))
        {
            return turn;
        }

        if (session.TurnState.BufferedAudioBytes <= 0)
        {
            return turn;
        }

        ISttStrategy? strategy = null;
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

            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                if (data.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array)
            {
                turnState.ListenRules = rules.EnumerateArray()
                    .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : item.ToString())
                    .Where(rule => !string.IsNullOrWhiteSpace(rule))
                        .ToArray();
                    session.Metadata["listenRules"] = turnState.ListenRules;
                }

                if (data.TryGetProperty("asr", out var asr) &&
                    asr.ValueKind == JsonValueKind.Object &&
                    asr.TryGetProperty("hints", out var hints) &&
                    hints.ValueKind == JsonValueKind.Array)
                {
                    turnState.ListenAsrHints = hints.EnumerateArray()
                        .Where(static item => item.ValueKind == JsonValueKind.String)
                        .Select(static item => item.GetString() ?? string.Empty)
                        .Where(static hint => !string.IsNullOrWhiteSpace(hint))
                        .ToArray();
                }

                if (data.TryGetProperty("hotphrase", out var hotphrase) &&
                    (hotphrase.ValueKind == JsonValueKind.True || hotphrase.ValueKind == JsonValueKind.False))
                {
                    turnState.ListenHotphrase = hotphrase.GetBoolean();
                    turnState.HotphraseEmptyTurnCount = 0;
                }

                if (data.TryGetProperty("intent", out var intent) && intent.ValueKind == JsonValueKind.String)
                {
                    session.LastIntent = intent.GetString();
                }

                if (data.TryGetProperty("transcriptHint", out var transcriptHint) && transcriptHint.ValueKind == JsonValueKind.String)
                {
                    turnState.AudioTranscriptHint = transcriptHint.GetString();
                    session.Metadata["audioTranscriptHint"] = turnState.AudioTranscriptHint;
                }
            }
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
        if (ShouldIgnoreBlankAudioHotphraseTurn(turn))
        {
            session.TurnState.AwaitingTurnCompletion = false;
            ResetBufferedAudio(session);
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

        var turnState = session.TurnState;
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
            return [];
        }

        if (ShouldIgnoreInitialEmptyHotphraseTurn(finalizedTurn, turnState))
        {
            turnState.HotphraseEmptyTurnCount += 1;
            turnState.AwaitingTurnCompletion = true;
            return
            [
                new WebSocketReply
                {
                    Text = JsonSerializer.Serialize(new
                    {
                        type = "OPENJIBO_TURN_PENDING",
                        data = new
                        {
                            sessionId = session.SessionId,
                            transID = session.LastTransId,
                            bufferedAudioBytes = turnState.BufferedAudioBytes,
                            bufferedAudioChunks = turnState.BufferedAudioChunkCount,
                            awaitingAudio = turnState.BufferedAudioBytes == 0,
                            awaitingTranscriptHint = turnState.BufferedAudioBytes > 0 && string.IsNullOrWhiteSpace(turnState.AudioTranscriptHint),
                            finalizeAttempts = turnState.FinalizeAttemptCount
                        }
                    })
                }
            ];
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

            if (allowFallbackOnMissingTranscript &&
                turnState.BufferedAudioBytes >= AutoFinalizeMinBufferedAudioBytes &&
                string.IsNullOrWhiteSpace(turnState.LastSttError))
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

            return
            [
                new WebSocketReply
                {
                    Text = JsonSerializer.Serialize(new
                    {
                        type = "OPENJIBO_TURN_PENDING",
                        data = new
                        {
                            sessionId = session.SessionId,
                            transID = session.LastTransId,
                            bufferedAudioBytes = turnState.BufferedAudioBytes,
                            bufferedAudioChunks = turnState.BufferedAudioChunkCount,
                            awaitingAudio = turnState.BufferedAudioBytes == 0,
                            awaitingTranscriptHint = turnState.BufferedAudioBytes > 0 && string.IsNullOrWhiteSpace(turnState.AudioTranscriptHint),
                            finalizeAttempts = turnState.FinalizeAttemptCount
                        }
                    })
                }
            ];
        }

        var plan = await conversationBroker.HandleTurnAsync(finalizedTurn, cancellationToken);
        var listenAction = plan.Actions.OfType<ListenAction>().OrderBy(action => action.Sequence).LastOrDefault();
        session.LastTranscript = finalizedTurn.NormalizedTranscript ?? finalizedTurn.RawTranscript;
        session.LastIntent = plan.IntentName;
        session.LastListenType = listenAction?.Mode;
        session.FollowUpExpiresUtc = plan.FollowUp.KeepMicOpen
            ? DateTimeOffset.UtcNow.Add(plan.FollowUp.Timeout)
            : null;
        turnState.AwaitingTurnCompletion = false;
        turnState.IgnoreAdditionalAudioUntilUtc = plan.FollowUp.KeepMicOpen
            ? null
            : DateTimeOffset.UtcNow.Add(WebSocketTurnState.DefaultLateAudioIgnoreWindow);

        var emitSkillActions = messageType != "CLIENT_NLU" ||
                               string.Equals(plan.IntentName, "word_of_the_day", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(plan.IntentName, "word_of_the_day_guess", StringComparison.OrdinalIgnoreCase);
        var replies = ResponsePlanToSocketMessagesMapper.Map(plan, finalizedTurn, session, emitSkillActions).Select(map => new WebSocketReply
        {
            Text = map.Text,
            DelayMs = map.DelayMs
        }).ToArray();

        ResetBufferedAudio(session);
        return replies;
    }

    private static bool ShouldAutoFinalize(CloudSession session)
    {
        var turnState = session.TurnState;
        var turnAge = turnState.FirstAudioReceivedUtc.HasValue
            ? DateTimeOffset.UtcNow - turnState.FirstAudioReceivedUtc.Value
            : TimeSpan.Zero;
        return turnState.AwaitingTurnCompletion &&
               turnState is
               {
                   SawListen: true, SawContext: true, BufferedAudioChunkCount: >= AutoFinalizeMinBufferedAudioChunks,
                   BufferedAudioBytes: >= AutoFinalizeMinBufferedAudioBytes
               } &&
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

        if (transcript.Length >= 6)
        {
            return true;
        }

        if (IsYesNoTurn(turn) && transcript is "yes" or "no" or "sure" or "nope" or "yup" or "uh huh" or "yeah" or "nah")
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
        return ReadRules(turn, "listenRules").Concat(ReadRules(turn, "clientRules"))
            .Any(static rule =>
                string.Equals(rule, "$YESNO", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rule, "create/is_it_a_keeper", StringComparison.OrdinalIgnoreCase));
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

        return Regex.Replace(transcript.Trim().ToLowerInvariant(), @"[^\w\s]", " ")
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
}
