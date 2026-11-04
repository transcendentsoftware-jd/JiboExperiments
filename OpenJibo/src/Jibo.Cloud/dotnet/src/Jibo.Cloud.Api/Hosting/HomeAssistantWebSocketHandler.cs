using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jibo.Cloud.Api.Hosting;

internal sealed class HomeAssistantWebSocketHandler(
    HomeAssistantConnectionRegistry registry,
    IUserIntegrationStore integrationStore,
    ITransportMetrics transportMetrics,
    ILogger<HomeAssistantWebSocketHandler> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal HomeAssistantWebSocketHandler(HomeAssistantConnectionRegistry registry)
        : this(registry, EmptyUserIntegrationStore.Instance, NullTransportMetrics.Instance,
            NullLogger<HomeAssistantWebSocketHandler>.Instance)
    {
    }

    internal async Task HandleAsync(HttpContext context)
    {
        var connectionId = Guid.NewGuid().ToString("N");
        logger.LogInformation(
            "Home Assistant WebSocket accepted connectionId={ConnectionId} traceId={TraceId} host={Host} " +
            "path={Path} remoteIp={RemoteIp} userAgent={UserAgent}",
            connectionId,
            context.TraceIdentifier,
            context.Request.Host.Host,
            context.Request.Path,
            context.Connection.RemoteIpAddress?.ToString(),
            context.Request.Headers.UserAgent.ToString());
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        transportMetrics.WebSocketConnectionOpened("home-assistant");
        string? instanceId = null;

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var message = await ReceiveTextAsync(socket, context.RequestAborted);
                if (message is null) break;

                if (instanceId is not null && !registry.IsCurrentConnection(instanceId, socket)) break;

                using var document = JsonDocument.Parse(message);
                var root = document.RootElement;
                var type = root.TryGetProperty("type", out var typeElement)
                    ? typeElement.GetString()
                    : null;
                var messageBytes = Encoding.UTF8.GetByteCount(message);
                transportMetrics.WebSocketMessage("in", "home-assistant", "text-json", type, messageBytes);
                logger.LogInformation(
                    "Home Assistant WebSocket message connectionId={ConnectionId} instanceId={InstanceId} messageType={MessageType} bytes={Bytes}",
                    connectionId,
                    instanceId,
                    type ?? "unknown",
                    messageBytes);

                switch (type?.ToLowerInvariant())
                {
                    case "register":
                    {
                        instanceId = root.TryGetProperty("instanceId", out var instanceElement)
                            ? instanceElement.GetString()
                            : null;
                        if (string.IsNullOrWhiteSpace(instanceId))
                        {
                            await registry.SendErrorAsync(socket, "instanceId is required.", context.RequestAborted);
                            continue;
                        }

                        var linkId = root.TryGetProperty("linkId", out var linkElement)
                            ? linkElement.GetString()
                            : null;

                        if (!string.IsNullOrWhiteSpace(instanceId))
                            registry.RemoveConnection(instanceId);

                        var existingLink = TryResolveExistingLink(instanceId, linkId);
                        if (existingLink is not null)
                        {
                            registry.RegisterPairedConnection(instanceId, socket);
                            integrationStore.UpdateHomeAssistantLastSeen(
                                existingLink.LinkId,
                                DateTimeOffset.UtcNow);
                            var linkWithSecret = integrationStore.EnsureHomeAssistantCommandSecret(existingLink.LinkId);
                            if (linkWithSecret is null) break;
                            await registry.SendPairedAsync(
                                instanceId,
                                linkWithSecret.JiboFriendlyName,
                                linkWithSecret.LinkId,
                                linkWithSecret.CommandSecret,
                                context.RequestAborted);
                            logger.LogInformation(
                                "Home Assistant instance {InstanceId} reconnected as paired link {LinkId}",
                                instanceId,
                                linkWithSecret.LinkId);
                            break;
                        }

                        var pending = registry.RegisterConnection(instanceId, socket);
                        await registry.SendVerificationCodeAsync(socket, pending, context.RequestAborted);
                        logger.LogInformation(
                            "Home Assistant instance {InstanceId} registered for pairing",
                            instanceId);
                        break;
                    }
                    case "ping":
                        await registry.SendPongAsync(socket, context.RequestAborted);
                        break;
                    case "command_result":
                        if (!registry.TryCompleteCommandResult(root, instanceId, socket))
                            logger.LogDebug("Ignored unmatched Home Assistant command_result");
                        break;
                    default:
                        await registry.SendErrorAsync(socket, "Unsupported message type.", context.RequestAborted);
                        break;
                }
            }
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(instanceId))
                registry.RemoveConnection(instanceId, socket);
            transportMetrics.WebSocketConnectionClosed("home-assistant");

            logger.LogInformation(
                "Home Assistant WebSocket closed connectionId={ConnectionId} instanceId={InstanceId}",
                connectionId,
                instanceId);
        }
    }

    private HomeAssistantLinkRecord? TryResolveExistingLink(string instanceId, string? linkId)
    {
        // Instance IDs are public labels. Only the saved opaque link ID may resume pairing.
        if (string.IsNullOrWhiteSpace(linkId)) return null;
        var link = integrationStore.FindLinkByLinkId(linkId);
        if (link is null) return null;

        if (!link.HaInstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase)) return null;

        if (!string.IsNullOrWhiteSpace(linkId) &&
            !link.LinkId.Equals(linkId, StringComparison.OrdinalIgnoreCase))
            return null;

        return link;
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();

        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (socket.State == WebSocketState.Open)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cancellationToken);
                return null;
            }

            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return result.MessageType == WebSocketMessageType.Text
            ? Encoding.UTF8.GetString(ms.ToArray())
            : null;
    }

    private sealed class EmptyUserIntegrationStore : IUserIntegrationStore
    {
        public static readonly EmptyUserIntegrationStore Instance = new();

        public IReadOnlyList<HomeAssistantLinkRecord> GetHomeAssistantLinks()
        {
            return [];
        }

        public HomeAssistantLinkRecord? FindLinkByHaInstanceId(string haInstanceId)
        {
            return null;
        }

        public HomeAssistantLinkRecord? FindLinkByLinkId(string linkId)
        {
            return null;
        }

        public HomeAssistantLinkRecord? FindLinkForJibo(string? jiboDeviceId, string? jiboFriendlyId, string? expectedOwnershipEpoch = null)
        {
            return null;
        }

        public HomeAssistantLinkRecord AddHomeAssistantLink(
            string jiboDeviceId,
            string jiboFriendlyName,
            string haInstanceId, string? expectedOwnershipEpoch = null)
        {
            throw new NotSupportedException();
        }

        public HomeAssistantLinkRecord? RemoveHomeAssistantLink(string linkId)
        {
            return null;
        }

        public void UpdateHomeAssistantLastSeen(string linkId, DateTimeOffset lastSeenUtc)
        {
        }

        public HomeAssistantLinkRecord? UpdateHomeAssistantClimateBlacklist(
            string linkId,
            bool blacklistHeat,
            bool blacklistCool)
        {
            return null;
        }

        public HomeAssistantLinkRecord? EnsureHomeAssistantCommandSecret(string linkId)
        {
            return null;
        }

        public IReadOnlyList<MemberCalendarFeedRecord> GetMemberCalendarFeeds(string? loopId = null, string? expectedOwnershipEpoch = null)
        {
            return [];
        }

        public MemberCalendarFeedRecord? FindMemberCalendarFeed(string loopId, string memberId, string? expectedOwnershipEpoch = null)
        {
            return null;
        }

        public MemberCalendarFeedRecord UpsertMemberCalendarFeed(
            string loopId,
            string memberId,
            string icalUrl,
            bool isEnabled = true, string? expectedOwnershipEpoch = null)
        {
            throw new NotSupportedException();
        }

        public MemberCalendarFeedRecord? ClearMemberCalendarFeed(string loopId, string memberId, string? expectedOwnershipEpoch = null)
        {
            return null;
        }

        public MemberCalendarFeedRecord? UpdateMemberCalendarFeedSyncStatus(
            string loopId,
            string memberId,
            DateTimeOffset? lastSuccessUtc,
            string? lastError, string? expectedFeedId = null)
        {
            return null;
        }
    }
}
