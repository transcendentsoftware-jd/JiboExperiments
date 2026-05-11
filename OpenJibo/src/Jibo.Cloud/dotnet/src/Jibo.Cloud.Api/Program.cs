using System.Net.WebSockets;
using System.Text;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenJiboCloud(builder.Configuration);

var app = builder.Build();

app.Logger.LogInformation("Starting Open Jibo Cloud Api version {Version}", OpenJiboCloudBuildInfo.Version);

app.UseWebSockets();

app.Use(async (context, next) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        await next();
        return;
    }

    var kind = ResolveSocketKind(context.Request.Host.Host, context.Request.Path);
    var token = ResolveToken(context.Request);
    switch (kind)
    {
        case "unknown":
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        case "api-socket" when string.IsNullOrWhiteSpace(token):
        case "neo-hub-listen" or "neo-hub-proactive" when string.IsNullOrWhiteSpace(token):
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
    }

    var webSocketService = context.RequestServices.GetRequiredService<JiboWebSocketService>();
    var telemetrySink = context.RequestServices.GetRequiredService<IWebSocketTelemetrySink>();
    
    using var socket = await context.WebSockets.AcceptWebSocketAsync();

    var openEnvelope = new WebSocketMessageEnvelope
    {
        ConnectionId = Guid.NewGuid().ToString("N"),
        HostName = context.Request.Host.Host,
        Path = context.Request.Path.Value ?? "/",
        Kind = kind,
        Token = token
    };
    var openSession = ResolveSession(webSocketService, openEnvelope);
    await telemetrySink.RecordConnectionOpenedAsync(openEnvelope, openSession, context.RequestAborted);

    var isPrematureClose = false;

    while (socket.State == WebSocketState.Open)
    {
        ReceivedSocketMessage received = null!;
        try
        {
            received = await ReceiveAsync(socket, context.RequestAborted);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", context.RequestAborted);
                break;
            }
        }
        catch (WebSocketException exception)
        {
            if (exception.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
            {
                isPrematureClose = true;
                break;
            }
        }
        
        var envelope = new WebSocketMessageEnvelope
        {
            ConnectionId = Guid.NewGuid().ToString("N"),
            HostName = context.Request.Host.Host,
            Path = context.Request.Path.Value ?? "/",
            Kind = kind,
            Token = token,
            Text = received.MessageType == WebSocketMessageType.Text ? Encoding.UTF8.GetString(received.Buffer) : null,
            Binary = received.MessageType == WebSocketMessageType.Binary ? received.Buffer : null
        };

        var replies = await webSocketService.HandleMessageAsync(envelope, context.RequestAborted);
        var session = ResolveSession(webSocketService, envelope);
        await telemetrySink.RecordInboundAsync(envelope, session, ReadMessageType(envelope.Text), context.RequestAborted);
        foreach (var reply in replies)
        {
            if (string.IsNullOrWhiteSpace(reply.Text))
            {
                continue;
            }

            if (reply.DelayMs > 0)
            {
                await Task.Delay(reply.DelayMs, context.RequestAborted);
            }

            var payload = Encoding.UTF8.GetBytes(reply.Text);
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, context.RequestAborted);
        }

        await telemetrySink.RecordOutboundAsync(envelope, session, replies, context.RequestAborted);
    }

    var closeEnvelope = new WebSocketMessageEnvelope
    {
        ConnectionId = Guid.NewGuid().ToString("N"),
        HostName = context.Request.Host.Host,
        Path = context.Request.Path.Value ?? "/",
        Kind = kind,
        Token = token
    };
    var closeSession = ResolveSession(webSocketService, closeEnvelope);
    await telemetrySink.RecordConnectionClosedAsync(closeEnvelope, closeSession, $"socket-loop-ended{(isPrematureClose ? "-prematurely" : string.Empty)}", context.RequestAborted);
});

app.MapGet("/health", () => Results.Json(new
{
    ok = true,
    service = "OpenJibo Cloud Api",
    version = OpenJiboCloudBuildInfo.Version
}));

app.MapMethods("/{**path}", ["GET", "POST", "PUT"], async (HttpContext context, JiboCloudProtocolService service, IProtocolTelemetrySink telemetrySink, CancellationToken cancellationToken) =>
{
    var envelope = await BuildEnvelopeAsync(context, cancellationToken);
    var result = await service.DispatchAsync(envelope, cancellationToken);
    await telemetrySink.RecordAsync(envelope, result, cancellationToken);

    context.Response.StatusCode = result.StatusCode;
    context.Response.ContentType = result.ContentType;

    foreach (var header in result.Headers)
    {
        context.Response.Headers[header.Key] = header.Value;
    }

    if (!string.IsNullOrEmpty(result.BodyText))
    {
        await context.Response.WriteAsync(result.BodyText, cancellationToken);
    }
});

app.Run();
return;

static async Task<ReceivedSocketMessage> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
{
    var buffer = new byte[8192];
    using var ms = new MemoryStream();

    WebSocketReceiveResult result;
    do
    {
        result = await socket.ReceiveAsync(buffer, cancellationToken);
        ms.Write(buffer, 0, result.Count);
    }
    while (!result.EndOfMessage);

    return new ReceivedSocketMessage(result.MessageType, ms.ToArray());
}

static async Task<ProtocolEnvelope> BuildEnvelopeAsync(HttpContext context, CancellationToken cancellationToken)
{
    context.Request.EnableBuffering();

    using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
    var bodyText = await reader.ReadToEndAsync(cancellationToken);
    context.Request.Body.Position = 0;

    var target = context.Request.Headers["X-Amz-Target"].ToString();
    var targetParts = target.Split('.', 2, StringSplitOptions.RemoveEmptyEntries);

    return new ProtocolEnvelope
    {
        RequestId = Guid.NewGuid().ToString("N"),
        Transport = "http",
        Method = context.Request.Method,
        HostName = context.Request.Host.Host,
        Path = context.Request.Path.Value ?? "/",
        ServicePrefix = targetParts.Length > 0 ? targetParts[0] : null,
        Operation = targetParts.Length > 1 ? targetParts[1] : null,
        DeviceId = context.Request.Headers["X-Jibo-RobotId"].ToString(),
        CorrelationId = context.TraceIdentifier,
        FirmwareVersion = context.Request.Headers["X-OpenJibo-Firmware"].ToString(),
        ApplicationVersion = context.Request.Headers["X-OpenJibo-AppVersion"].ToString(),
        BodyText = bodyText,
        Headers = context.Request.Headers.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.OrdinalIgnoreCase)
    };
}

static string ResolveSocketKind(string host, PathString path)
{
    if (host.Equals("api-socket.jibo.com", StringComparison.OrdinalIgnoreCase))
    {
        return "api-socket";
    }

    if (host.Equals("neo-hub.jibo.com", StringComparison.OrdinalIgnoreCase) &&
        path.StartsWithSegments("/v1/proactive"))
    {
        return "neo-hub-proactive";
    }

    if (host.Equals("neo-hub.jibo.com", StringComparison.OrdinalIgnoreCase))
    {
        return "neo-hub-listen";
    }

    if (host.Equals("openjibo.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("openjibo.ai", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
    {
        return "openjibo";
    }

    return "neo-hub-listen"; // now it assumes all unknown requests are neo-hub. I did this so that people with custom listen servers (like myself) won't get a bunch of 404 messages when doing a HJ request. -ZaneDev (an awful programmer)
}

static string? ResolveToken(HttpRequest request)
{
    var auth = request.Headers.Authorization.ToString();
    if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        return auth["Bearer ".Length..].Trim();
    }

    var path = request.Path.Value;
    if (!string.IsNullOrWhiteSpace(path) && path.Length > 1)
    {
        return path.Trim('/');
    }

    return null;
}

static string ReadMessageType(string? text)
{
    if (string.IsNullOrWhiteSpace(text))
    {
        return "BINARY_OR_EMPTY";
    }

    try
    {
        using var document = System.Text.Json.JsonDocument.Parse(text);
        return document.RootElement.TryGetProperty("type", out var type) && type.ValueKind == System.Text.Json.JsonValueKind.String
            ? type.GetString() ?? "UNKNOWN"
            : "UNKNOWN";
    }
    catch
    {
        return "TEXT";
    }
}

static CloudSession ResolveSession(JiboWebSocketService webSocketService, WebSocketMessageEnvelope envelope)
{
    return webSocketService.GetOrCreateSession(envelope);
}

internal sealed record ReceivedSocketMessage(WebSocketMessageType MessageType, byte[] Buffer);
