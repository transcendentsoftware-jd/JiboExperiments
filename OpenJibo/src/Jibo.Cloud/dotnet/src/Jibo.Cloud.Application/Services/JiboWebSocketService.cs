using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Domain.Models;

namespace Jibo.Cloud.Application.Services;

public sealed class JiboWebSocketService(
    ICloudStateStore stateStore,
    IWebSocketTelemetrySink telemetrySink,
    WebSocketTurnFinalizationService turnFinalizationService)
{
    public CloudSession GetOrCreateSession(WebSocketMessageEnvelope envelope)
    {
        return stateStore.FindSessionByToken(envelope.Token ?? string.Empty) ??
               stateStore.OpenSession(envelope.Kind, null, envelope.Token, envelope.HostName, envelope.Path);
    }

    public async Task<IReadOnlyList<WebSocketReply>> HandleMessageAsync(WebSocketMessageEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var session = GetOrCreateSession(envelope);
        session.LastSeenUtc = DateTimeOffset.UtcNow;

        if (envelope.IsBinary)
        {
            var replies = await turnFinalizationService.HandleBinaryAudioAsync(session, envelope, cancellationToken);
            await telemetrySink.RecordTurnEventAsync(envelope, session, "binary_audio_received", new Dictionary<string, object?>
            {
                ["bytes"] = envelope.Binary?.Length ?? 0
            }, cancellationToken);
            return replies;
        }

        var parsedType = ReadMessageType(envelope.Text);
        session.LastMessageType = parsedType;
        var parsedTransId = ReadTransId(envelope.Text);
        if (!string.IsNullOrWhiteSpace(parsedTransId))
        {
            session.LastTransId = parsedTransId;
            session.TurnState.TransId = parsedTransId;
        }

        if (parsedType == "CONTEXT")
        {
            var replies = await turnFinalizationService.HandleContextAsync(session, envelope, cancellationToken);
            await telemetrySink.RecordTurnEventAsync(envelope, session, "context_received", new Dictionary<string, object?>
            {
                ["transID"] = session.TurnState.TransId
            }, cancellationToken);
            return replies;
        }

        if (parsedType is "LISTEN" or "CLIENT_NLU" or "CLIENT_ASR")
        {
            var replies = await turnFinalizationService.HandleTurnAsync(session, envelope, parsedType, cancellationToken);
            await telemetrySink.RecordTurnEventAsync(envelope, session, "turn_processed", new Dictionary<string, object?>
            {
                ["messageType"] = parsedType,
                ["replyCount"] = replies.Count,
                ["transcript"] = session.LastTranscript,
                ["intent"] = session.LastIntent
            }, cancellationToken);
            return replies;
        }

        return
        [
            new WebSocketReply
            {
                Text = JsonSerializer.Serialize(new
                {
                    type = "OPENJIBO_ACK",
                    data = new
                    {
                        messageType = parsedType,
                        sessionId = session.SessionId
                    }
                })
            }
        ];
    }

    private static string ReadMessageType(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "UNKNOWN";
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
            {
                return type.GetString() ?? "UNKNOWN";
            }
        }
        catch
        {
            return "TEXT";
        }

        return "UNKNOWN";
    }

    private static string? ReadTransId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.TryGetProperty("transID", out var transId) && transId.ValueKind == JsonValueKind.String)
            {
                return transId.GetString();
            }
        }
        catch
        {
            return null;
        }

        return null;
    }
}
