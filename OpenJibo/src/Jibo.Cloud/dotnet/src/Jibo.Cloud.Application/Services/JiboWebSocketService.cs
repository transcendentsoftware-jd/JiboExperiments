using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Domain.Models;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Application.Services;

public sealed class JiboWebSocketService(
    ICloudStateStore stateStore,
    WebSocketTurnFinalizationService turnFinalizationService)
{
    public async Task<IReadOnlyList<WebSocketReply>> HandleMessageAsync(WebSocketMessageEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var session = stateStore.FindSessionByToken(envelope.Token ?? string.Empty) ??
                      stateStore.OpenSession(envelope.Kind, null, envelope.Token, envelope.HostName, envelope.Path);
        session.LastSeenUtc = DateTimeOffset.UtcNow;

        if (envelope.IsBinary)
        {
            return turnFinalizationService.HandleBinaryAudio(session, envelope);
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
            return turnFinalizationService.HandleContext(session, envelope.Text);
        }

        if (parsedType is "LISTEN" or "CLIENT_NLU" or "CLIENT_ASR")
        {
            return await turnFinalizationService.HandleTurnAsync(session, envelope, parsedType, cancellationToken);
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
