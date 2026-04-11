using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Domain.Models;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Application.Services;

public sealed class JiboWebSocketService(
    ICloudStateStore stateStore,
    ProtocolToTurnContextMapper turnContextMapper,
    IConversationBroker conversationBroker,
    ResponsePlanToSocketMessagesMapper replyMapper)
{
    public async Task<IReadOnlyList<WebSocketReply>> HandleMessageAsync(WebSocketMessageEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var session = stateStore.FindSessionByToken(envelope.Token ?? string.Empty) ??
                      stateStore.OpenSession(envelope.Kind, null, envelope.Token, envelope.HostName, envelope.Path);

        if (envelope.IsBinary)
        {
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
                            sessionId = session.SessionId
                        }
                    })
                }
            ];
        }

        var parsedType = ReadMessageType(envelope.Text);
        session.LastListenType = parsedType;

        if (parsedType is "LISTEN" or "CLIENT_NLU" or "CLIENT_ASR")
        {
            var turn = turnContextMapper.MapListenMessage(envelope, session);
            var plan = await conversationBroker.HandleTurnAsync(turn, cancellationToken);

            return replyMapper.Map(plan).Select(text => new WebSocketReply
            {
                Text = text
            }).ToArray();
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
}
