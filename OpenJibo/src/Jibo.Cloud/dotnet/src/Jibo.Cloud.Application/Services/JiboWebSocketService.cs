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
        session.LastSeenUtc = DateTimeOffset.UtcNow;

        if (envelope.IsBinary)
        {
            session.LastMessageType = "BINARY_AUDIO";
            session.Metadata["lastAudioBytes"] = envelope.Binary?.Length ?? 0;
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
        session.LastMessageType = parsedType;
        var parsedTransId = ReadTransId(envelope.Text);
        if (!string.IsNullOrWhiteSpace(parsedTransId))
        {
            session.LastTransId = parsedTransId;
        }

        if (parsedType == "CONTEXT")
        {
            session.Metadata["context"] = ExtractDataPayload(envelope.Text);
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

        if (parsedType is "LISTEN" or "CLIENT_NLU" or "CLIENT_ASR")
        {
            PersistTurnHints(session, envelope.Text, parsedType);

            var turn = turnContextMapper.MapListenMessage(envelope, session, parsedType);
            if (string.IsNullOrWhiteSpace(turn.NormalizedTranscript) &&
                string.IsNullOrWhiteSpace(turn.RawTranscript))
            {
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
                                sessionId = session.SessionId,
                                transID = session.LastTransId
                            }
                        })
                    }
                ];
            }

            var plan = await conversationBroker.HandleTurnAsync(turn, cancellationToken);
            var listenAction = plan.Actions.OfType<ListenAction>().OrderBy(action => action.Sequence).LastOrDefault();
            session.LastTranscript = turn.NormalizedTranscript ?? turn.RawTranscript;
            session.LastIntent = plan.IntentName;
            session.LastListenType = listenAction?.Mode;
            session.FollowUpExpiresUtc = plan.FollowUp.KeepMicOpen
                ? DateTimeOffset.UtcNow.Add(plan.FollowUp.Timeout)
                : null;

            var emitSkillActions = parsedType != "CLIENT_NLU";
            return replyMapper.Map(plan, turn, session, emitSkillActions).Select(text => new WebSocketReply
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

    private static void PersistTurnHints(CloudSession session, string? text, string messageType)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                if (data.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array)
                {
                    session.Metadata["listenRules"] = rules.EnumerateArray()
                        .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : item.ToString())
                        .Where(rule => !string.IsNullOrWhiteSpace(rule))
                        .ToArray();
                }

                if (data.TryGetProperty("intent", out var intent) && intent.ValueKind == JsonValueKind.String)
                {
                    session.LastIntent = intent.GetString();
                }

                if (messageType == "CONTEXT")
                {
                    session.Metadata["context"] = data.GetRawText();
                }
            }
        }
        catch
        {
            // Keep the compatibility layer permissive while captures are still incomplete.
        }
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
}
