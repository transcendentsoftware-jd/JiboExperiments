using System.Text.Json;
using Jibo.Cloud.Domain.Models;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Application.Services;

public sealed class ProtocolToTurnContextMapper
{
    public static TurnContext MapListenMessage(WebSocketMessageEnvelope envelope, CloudSession session, string messageType)
    {
        var turnState = session.TurnState;
        var protocolOperation = messageType.ToLowerInvariant();
        var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["messageType"] = messageType
        };
        var text = ExtractTranscript(envelope.Text, attributes);

        if (!string.IsNullOrWhiteSpace(turnState.TransId))
        {
            attributes["transID"] = turnState.TransId;
        }

        if (!string.IsNullOrWhiteSpace(turnState.ContextPayload))
        {
            attributes["context"] = turnState.ContextPayload;
        }

        if (session.Metadata.TryGetValue("lastClockDomain", out var lastClockDomain) &&
            lastClockDomain is string lastClockDomainText &&
            !string.IsNullOrWhiteSpace(lastClockDomainText))
        {
            attributes["lastClockDomain"] = lastClockDomainText;
        }

        attributes["listenHotphrase"] = turnState.ListenHotphrase;

        if (turnState.ListenRules.Count > 0)
        {
            attributes["listenRules"] = turnState.ListenRules;
        }

        if (turnState.ListenAsrHints.Count > 0)
        {
            attributes["listenAsrHints"] = turnState.ListenAsrHints;
        }

        if (turnState.BufferedAudioBytes > 0)
        {
            attributes["bufferedAudioBytes"] = turnState.BufferedAudioBytes;
            attributes["bufferedAudioChunks"] = turnState.BufferedAudioChunkCount;
            attributes["bufferedAudioFrames"] = turnState.BufferedAudioFrames.Select(frame => frame.ToArray()).ToArray();
        }

        if (!string.IsNullOrWhiteSpace(turnState.AudioTranscriptHint))
        {
            attributes["audioTranscriptHint"] = turnState.AudioTranscriptHint;
        }

        if (turnState.FinalizeAttemptCount > 0)
        {
            attributes["finalizeAttemptCount"] = turnState.FinalizeAttemptCount;
        }

        return new TurnContext
        {
            SessionId = session.SessionId,
            InputMode = session.FollowUpOpen ? TurnInputMode.FollowUp : TurnInputMode.DirectText,
            SourceKind = TurnSourceKind.Api,
            RawTranscript = text,
            NormalizedTranscript = text?.Trim(),
            DeviceId = session.DeviceId,
            HostName = envelope.HostName,
            RequestId = envelope.ConnectionId,
            ProtocolService = "neo-hub",
            ProtocolOperation = protocolOperation,
            FirmwareVersion = session.Metadata.TryGetValue("firmwareVersion", out var firmwareVersion) ? firmwareVersion as string : null,
            ApplicationVersion = session.Metadata.TryGetValue("applicationVersion", out var applicationVersion) ? applicationVersion as string : null,
            IsFollowUpEligible = true,
            Attributes = attributes
        };
    }

    private static string? ExtractTranscript(string? text, IDictionary<string, object?> attributes)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            if (root.TryGetProperty("data", out var data))
            {
                if (data.TryGetProperty("text", out var transcript) && transcript.ValueKind == JsonValueKind.String)
                {
                    return transcript.GetString();
                }

                if (data.TryGetProperty("asr", out var asr) &&
                    asr.ValueKind == JsonValueKind.Object &&
                    asr.TryGetProperty("text", out var asrText) &&
                    asrText.ValueKind == JsonValueKind.String)
                {
                    return asrText.GetString();
                }

                if (data.TryGetProperty("transcriptHint", out var transcriptHint) && transcriptHint.ValueKind == JsonValueKind.String)
                {
                    return transcriptHint.GetString();
                }

                if (data.TryGetProperty("intent", out var intent) && intent.ValueKind == JsonValueKind.String)
                {
                    attributes["clientIntent"] = intent.GetString();
                }

                if (data.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array)
                {
                    attributes["clientRules"] = rules.EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString() ?? string.Empty)
                        .Where(rule => !string.IsNullOrWhiteSpace(rule))
                        .ToArray();
                }

                if (data.TryGetProperty("entities", out var entities) && entities.ValueKind == JsonValueKind.Object)
                {
                    attributes["clientEntities"] = entities.Clone();
                }

                if (intent.ValueKind == JsonValueKind.String)
                {
                    return intent.GetString();
                }
            }

            return null;
        }
        catch
        {
            return text;
        }
    }
}
