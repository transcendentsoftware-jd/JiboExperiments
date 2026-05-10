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
                ["bytes"] = envelope.Binary?.Length ?? 0,
                ["glsmPhase"] = WebSocketTurnFinalizationService.ResolveGlsmPhase(session)
            }, cancellationToken);
            return replies;
        }

        var parsedType = ReadMessageType(envelope.Text);
        session.LastMessageType = parsedType;
        var containsInlineTurnPayload = parsedType == "LISTEN" && ContainsInlineTurnPayload(envelope.Text);
        var staleListenRecovered = false;
        var staleListenAgeMs = 0;
        if (parsedType == "LISTEN" &&
            !containsInlineTurnPayload &&
            WebSocketTurnFinalizationService.ShouldIgnoreLateListenSetup(session, envelope.Text))
        {
            var (lateTransId, lateRules) = ResolveLateListenNoInputPayload(session, envelope.Text);
            var replies = ResponsePlanToSocketMessagesMapper
                .MapNoInputAndRedirectToSkill(lateTransId, lateRules, "@be/idle")
                .Select(map => new WebSocketReply
                {
                    Text = map.Text,
                    DelayMs = map.DelayMs
                })
                .ToArray();

            await telemetrySink.RecordTurnEventAsync(envelope, session, "late_listen_ignored", new Dictionary<string, object?>
            {
                ["messageType"] = parsedType,
                ["activeTransID"] = session.TurnState.TransId,
                ["ignoredTransID"] = lateTransId,
                ["replyCount"] = replies.Length
            }, cancellationToken);
            return replies;
        }

        if (parsedType == "LISTEN" &&
            !containsInlineTurnPayload &&
            WebSocketTurnFinalizationService.TryRecoverStalePendingListen(session, out staleListenAgeMs))
        {
            staleListenRecovered = true;
            await telemetrySink.RecordTurnEventAsync(envelope, session, "glsm_stale_listen_recovered", new Dictionary<string, object?>
            {
                ["staleAgeMs"] = staleListenAgeMs,
                ["transID"] = session.TurnState.TransId,
                ["glsmPhase"] = WebSocketTurnFinalizationService.ResolveGlsmPhase(session)
            }, cancellationToken);
        }

        WebSocketTurnFinalizationService.ObserveIncomingMessage(session, envelope.Text);

        switch (parsedType)
        {
            case "CONTEXT":
            {
                var replies = await turnFinalizationService.HandleContextAsync(session, envelope, cancellationToken);
                await telemetrySink.RecordTurnEventAsync(envelope, session, "context_received", new Dictionary<string, object?>
                {
                    ["transID"] = session.TurnState.TransId,
                    ["glsmPhase"] = WebSocketTurnFinalizationService.ResolveGlsmPhase(session)
                }, cancellationToken);
                return replies;
            }
            case "LISTEN":
            {
                var replies = containsInlineTurnPayload
                    ? await turnFinalizationService.HandleTurnAsync(session, envelope, parsedType, cancellationToken)
                    : WebSocketTurnFinalizationService.HandleListenSetup(session, envelope);
                await telemetrySink.RecordTurnEventAsync(envelope, session, "turn_processed", new Dictionary<string, object?>
                {
                    ["messageType"] = parsedType,
                    ["replyCount"] = replies.Count,
                    ["transcript"] = session.LastTranscript,
                    ["intent"] = session.LastIntent,
                    ["glsmPhase"] = WebSocketTurnFinalizationService.ResolveGlsmPhase(session),
                    ["staleListenRecovered"] = staleListenRecovered,
                    ["staleListenAgeMs"] = staleListenAgeMs
                }, cancellationToken);
                return replies;
            }
            case "CLIENT_NLU" or "CLIENT_ASR" or "TRIGGER":
            {
                var replies = await turnFinalizationService.HandleTurnAsync(session, envelope, parsedType, cancellationToken);
                await telemetrySink.RecordTurnEventAsync(envelope, session, "turn_processed", new Dictionary<string, object?>
                {
                    ["messageType"] = parsedType,
                    ["replyCount"] = replies.Count,
                    ["transcript"] = session.LastTranscript,
                    ["intent"] = session.LastIntent,
                    ["glsmPhase"] = WebSocketTurnFinalizationService.ResolveGlsmPhase(session)
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

    private static (string TransId, IReadOnlyList<string> Rules) ResolveLateListenNoInputPayload(
        CloudSession session,
        string? text)
    {
        var transId = session.TurnState.TransId ?? session.LastTransId ?? string.Empty;
        var rules = session.TurnState.ListenRules;

        if (string.IsNullOrWhiteSpace(text))
        {
            return (transId, rules);
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            if (root.TryGetProperty("transID", out var transIdValue) &&
                transIdValue.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(transIdValue.GetString()))
            {
                transId = transIdValue.GetString()!;
            }

            if (root.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("rules", out var ruleValues) &&
                ruleValues.ValueKind == JsonValueKind.Array)
            {
                var parsedRules = ruleValues.EnumerateArray()
                    .Where(static item => item.ValueKind == JsonValueKind.String)
                    .Select(static item => item.GetString() ?? string.Empty)
                    .Where(static rule => !string.IsNullOrWhiteSpace(rule))
                    .ToArray();

                if (parsedRules.Length > 0)
                {
                    rules = parsedRules;
                }
            }
        }
        catch
        {
            // Best effort parsing for late-listen cleanup.
        }

        return (transId, rules);
    }
}
