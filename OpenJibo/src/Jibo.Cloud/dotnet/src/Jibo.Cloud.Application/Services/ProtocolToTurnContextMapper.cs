using System.Text.Json;
using Jibo.Cloud.Domain.Models;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Application.Services;

public sealed class ProtocolToTurnContextMapper
{
    public TurnContext MapListenMessage(WebSocketMessageEnvelope envelope, CloudSession session, string messageType)
    {
        var text = ExtractTranscript(envelope.Text);
        var protocolOperation = messageType.ToLowerInvariant();
        var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["messageType"] = messageType
        };

        if (!string.IsNullOrWhiteSpace(session.LastTransId))
        {
            attributes["transID"] = session.LastTransId;
        }

        if (session.Metadata.TryGetValue("context", out var context))
        {
            attributes["context"] = context;
        }

        if (session.Metadata.TryGetValue("listenRules", out var listenRules))
        {
            attributes["listenRules"] = listenRules;
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

    private static string? ExtractTranscript(string? text)
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

                if (data.TryGetProperty("intent", out var intent) && intent.ValueKind == JsonValueKind.String)
                {
                    return intent.GetString();
                }
            }
        }
        catch
        {
            return text;
        }

        return text;
    }
}
