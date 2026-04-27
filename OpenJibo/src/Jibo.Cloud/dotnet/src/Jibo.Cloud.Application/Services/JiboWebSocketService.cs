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
        WebSocketTurnFinalizationService.ObserveIncomingMessage(session, envelope.Text);

        switch (parsedType)
        {
            case "CONTEXT":
            {
                var replies = await turnFinalizationService.HandleContextAsync(session, envelope, cancellationToken);
                await telemetrySink.RecordTurnEventAsync(envelope, session, "context_received", new Dictionary<string, object?>
                {
                    ["transID"] = session.TurnState.TransId
                }, cancellationToken);
                return replies;
            }
            case "LISTEN":
            {
                var replies = ContainsInlineTurnPayload(envelope.Text)
                    ? await turnFinalizationService.HandleTurnAsync(session, envelope, parsedType, cancellationToken)
                    : WebSocketTurnFinalizationService.HandleListenSetup(session, envelope);
                await telemetrySink.RecordTurnEventAsync(envelope, session, "turn_processed", new Dictionary<string, object?>
                {
                    ["messageType"] = parsedType,
                    ["replyCount"] = replies.Count,
                    ["transcript"] = session.LastTranscript,
                    ["intent"] = session.LastIntent
                }, cancellationToken);
                return replies;
            }
            case "CLIENT_NLU" or "CLIENT_ASR":
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
            default:
                return [];
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

    private static bool ContainsInlineTurnPayload(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (data.TryGetProperty("text", out var transcript) &&
                transcript.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(transcript.GetString()))
            {
                return true;
            }

            return data.TryGetProperty("asr", out var asr) &&
                   asr.ValueKind == JsonValueKind.Object &&
                   asr.TryGetProperty("text", out var asrText) &&
                   asrText.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(asrText.GetString());
        }
        catch
        {
            return false;
        }
    }
}
