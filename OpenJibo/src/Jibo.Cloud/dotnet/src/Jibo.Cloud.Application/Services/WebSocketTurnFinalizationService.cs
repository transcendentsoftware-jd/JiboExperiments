using System.Text.Json;
using Jibo.Cloud.Domain.Models;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Application.Services;

public sealed class WebSocketTurnFinalizationService(
    ProtocolToTurnContextMapper turnContextMapper,
    IConversationBroker conversationBroker,
    ResponsePlanToSocketMessagesMapper replyMapper,
    ISttStrategySelector sttStrategySelector)
{
    private const int AutoFinalizeMinBufferedAudioBytes = 12000;
    private const int AutoFinalizeMinBufferedAudioChunks = 5;

    public async Task<IReadOnlyList<WebSocketReply>> HandleBinaryAudioAsync(
        CloudSession session,
        WebSocketMessageEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        var turnState = session.TurnState;
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

        try
        {
            var strategy = await sttStrategySelector.SelectAsync(turn, cancellationToken);
            var sttResult = await strategy.TranscribeAsync(turn, cancellationToken);

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
        catch
        {
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
        turnState.FirstAudioReceivedUtc = null;
        turnState.LastAudioReceivedUtc = null;
        turnState.BufferedAudioChunkCount = 0;
        turnState.BufferedAudioBytes = 0;
        turnState.BufferedAudioFrames.Clear();
        turnState.FinalizeAttemptCount = 0;
        turnState.AwaitingTurnCompletion = false;
        turnState.SawListen = false;
        turnState.SawContext = false;
        turnState.ListenRules = [];
    }

    private async Task<IReadOnlyList<WebSocketReply>> FinalizeTurnAsync(
        CloudSession session,
        WebSocketMessageEnvelope envelope,
        string messageType,
        bool allowFallbackOnMissingTranscript,
        CancellationToken cancellationToken)
    {
        var turn = turnContextMapper.MapListenMessage(envelope, session, messageType);
        var finalizedTurn = await ResolveTranscriptAsync(turn, session, cancellationToken);
        var turnState = session.TurnState;
        if (string.IsNullOrWhiteSpace(finalizedTurn.NormalizedTranscript) &&
            string.IsNullOrWhiteSpace(finalizedTurn.RawTranscript))
        {
            turnState.AwaitingTurnCompletion = true;
            if (turnState.BufferedAudioBytes > 0)
            {
                turnState.FinalizeAttemptCount += 1;
            }

            if (allowFallbackOnMissingTranscript && turnState.BufferedAudioBytes >= AutoFinalizeMinBufferedAudioBytes)
            {
                turnState.AwaitingTurnCompletion = false;
                session.LastTranscript = string.Empty;
                session.LastIntent = "heyJibo";
                session.LastListenType = "fallback";
                var fallbackReplies = replyMapper.MapFallback(session, turnState.TransId ?? session.LastTransId ?? string.Empty, turnState.ListenRules)
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

        var emitSkillActions = messageType != "CLIENT_NLU";
        var replies = replyMapper.Map(plan, finalizedTurn, session, emitSkillActions).Select(map => new WebSocketReply
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
        return turnState.AwaitingTurnCompletion &&
               turnState.SawListen &&
               turnState.SawContext &&
               turnState.BufferedAudioChunkCount >= AutoFinalizeMinBufferedAudioChunks &&
               turnState.BufferedAudioBytes >= AutoFinalizeMinBufferedAudioBytes;
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
}
