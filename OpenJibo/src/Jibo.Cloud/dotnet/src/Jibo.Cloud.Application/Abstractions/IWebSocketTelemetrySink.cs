using Jibo.Cloud.Domain.Models;

namespace Jibo.Cloud.Application.Abstractions;

public interface IWebSocketTelemetrySink
{
    Task RecordConnectionOpenedAsync(WebSocketMessageEnvelope envelope, CloudSession session, CancellationToken cancellationToken = default);
    Task RecordInboundAsync(WebSocketMessageEnvelope envelope, CloudSession session, string? messageType, CancellationToken cancellationToken = default);
    Task RecordTurnEventAsync(WebSocketMessageEnvelope envelope, CloudSession session, string eventType, IReadOnlyDictionary<string, object?> details, CancellationToken cancellationToken = default);
    Task RecordOutboundAsync(WebSocketMessageEnvelope envelope, CloudSession session, IReadOnlyList<WebSocketReply> replies, CancellationToken cancellationToken = default);
    Task RecordConnectionClosedAsync(WebSocketMessageEnvelope envelope, CloudSession session, string reason, CancellationToken cancellationToken = default);
}
