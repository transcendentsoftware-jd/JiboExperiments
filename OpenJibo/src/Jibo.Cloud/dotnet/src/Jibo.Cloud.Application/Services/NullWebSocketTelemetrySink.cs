using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Domain.Models;

namespace Jibo.Cloud.Application.Services;

public sealed class NullWebSocketTelemetrySink : IWebSocketTelemetrySink
{
    public Task RecordConnectionOpenedAsync(WebSocketMessageEnvelope envelope, CloudSession session, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RecordInboundAsync(WebSocketMessageEnvelope envelope, CloudSession session, string? messageType, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RecordTurnEventAsync(WebSocketMessageEnvelope envelope, CloudSession session, string eventType, IReadOnlyDictionary<string, object?> details, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RecordOutboundAsync(WebSocketMessageEnvelope envelope, CloudSession session, IReadOnlyList<WebSocketReply> replies, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RecordConnectionClosedAsync(WebSocketMessageEnvelope envelope, CloudSession session, string reason, CancellationToken cancellationToken = default) => Task.CompletedTask;
}