using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;

namespace Jibo.Cloud.Application.Services;

public sealed class HomeAssistantConnectionRegistry(ITransportMetrics? transportMetrics = null, IUserIntegrationStore? integrationStore = null)
{
    private static readonly TimeSpan VerificationLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CommandResultTimeout = TimeSpan.FromSeconds(3);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ITransportMetrics _transportMetrics = transportMetrics ?? NullTransportMetrics.Instance;

    private readonly ConcurrentDictionary<string, string> _codeByInstanceId =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, HomeAssistantConnection> _connections =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, PendingHomeAssistantVerification> _pendingByCode =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, PendingCommand>
        _pendingCommandResults = new(StringComparer.OrdinalIgnoreCase);

    public PendingHomeAssistantVerification RegisterConnection(string instanceId, WebSocket socket)
    {
        PurgeExpired();

        var code = GenerateCode();
        var pending = new PendingHomeAssistantVerification(
            instanceId,
            code,
            DateTimeOffset.UtcNow.Add(VerificationLifetime));

        if (_connections.TryGetValue(instanceId, out var previous)) RemovePending(previous);
        _connections[instanceId] = new HomeAssistantConnection(instanceId, socket, PendingCode: code);
        _pendingByCode[code] = pending;
        _codeByInstanceId[instanceId] = code;

        return pending;
    }

    public void RegisterPairedConnection(string instanceId, WebSocket socket)
    {
        if (_connections.TryGetValue(instanceId, out var previous)) RemovePending(previous);
        _connections[instanceId] = new HomeAssistantConnection(instanceId, socket);
    }

    public bool IsInstanceConnected(string instanceId)
    {
        return _connections.TryGetValue(instanceId, out var connection) && IsActive(connection) &&
               connection.Socket.State == WebSocketState.Open;
    }

    public void RemoveConnection(string instanceId)
    {
        if (_connections.TryRemove(instanceId, out var connection)) RemovePending(connection);
    }

    public void RemoveConnection(string instanceId, WebSocket socket)
    {
        if (_connections.TryGetValue(instanceId, out var connection) && ReferenceEquals(connection.Socket, socket) &&
            _connections.TryRemove(new KeyValuePair<string, HomeAssistantConnection>(instanceId, connection)))
        {
            RemovePending(connection);
        }
    }

    private void RemovePending(HomeAssistantConnection connection)
    {
        if (connection.PendingCode is not { } code) return;
        _codeByInstanceId.TryRemove(new KeyValuePair<string, string>(connection.InstanceId, code));
        _pendingByCode.TryRemove(code, out _);
    }

    public void RevokeInactiveConnections()
    {
        foreach (var connection in _connections.Values) IsActive(connection);
    }

    public bool IsCurrentConnection(string instanceId, WebSocket socket) =>
        _connections.TryGetValue(instanceId, out var connection) && ReferenceEquals(connection.Socket, socket) &&
        IsActive(connection);

    private bool IsActive(HomeAssistantConnection connection)
    {
        if (connection.LinkId is null || integrationStore is null ||
            integrationStore.FindLinkByLinkId(connection.LinkId) is not null) return true;
        RemoveConnection(connection.InstanceId, connection.Socket);
        connection.Socket.Abort();
        return false;
    }

    public PendingHomeAssistantVerification? TryGetPendingByCode(string code)
    {
        PurgeExpired();

        var normalized = NormalizeCode(code);
        return _pendingByCode.TryGetValue(normalized, out var pending) &&
               pending.ExpiresAtUtc > DateTimeOffset.UtcNow
            ? pending
            : null;
    }

    public async Task<bool> SendPairedAsync(
        string instanceId,
        string jiboFriendlyName,
        string linkId,
        string commandSecret,
        CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(instanceId, out var connection)) return false;

        if (integrationStore is not null && integrationStore.FindLinkByLinkId(linkId) is null) return false;
        var pairedConnection = connection with { LinkId = linkId };
        if (!_connections.TryUpdate(instanceId, pairedConnection, connection)) return false;
        RemovePending(connection);
        connection = pairedConnection;

        await SendJsonAsync(connection.Socket, new
        {
            type = "paired",
            jiboFriendlyName,
            linkId,
            commandSecret
        }, "paired", cancellationToken);

        return true;
    }

    public async Task SendVerificationCodeAsync(
        WebSocket socket,
        PendingHomeAssistantVerification pending,
        CancellationToken cancellationToken = default)
    {
        await SendJsonAsync(socket, new
        {
            type = "verification_code",
            code = pending.Code,
            expiresInSeconds = (int)Math.Max(0, (pending.ExpiresAtUtc - DateTimeOffset.UtcNow).TotalSeconds)
        }, "verification_code", cancellationToken);
    }

    public async Task SendErrorAsync(WebSocket socket, string message, CancellationToken cancellationToken = default)
    {
        await SendJsonAsync(socket, new
        {
            type = "error",
            message
        }, "error", cancellationToken);
    }

    public async Task SendPongAsync(WebSocket socket, CancellationToken cancellationToken = default)
    {
        await SendJsonAsync(socket, new { type = "pong" }, "pong", cancellationToken);
    }

    public async Task<bool> SendCommandAsync(
        string instanceId,
        string linkId,
        string commandSecret,
        string command,
        IReadOnlyDictionary<string, string>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(instanceId, out var connection)) return false;
        if (string.IsNullOrWhiteSpace(commandSecret) || !IsActive(connection) ||
            (integrationStore is not null && (connection.LinkId != linkId || integrationStore.FindLinkByLinkId(linkId) is null))) return false;

        var payload = BuildCommandPayload(command, parameters, requestId: null, linkId, commandSecret);
        await SendJsonAsync(connection.Socket, payload, "command", cancellationToken);
        return true;
    }

    public async Task<HomeAssistantCommandResult?> SendCommandAndWaitAsync(
        string instanceId,
        string linkId,
        string commandSecret,
        string command,
        IReadOnlyDictionary<string, string>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(instanceId, out var connection)) return null;
        if (string.IsNullOrWhiteSpace(commandSecret) || !IsActive(connection) ||
            (integrationStore is not null && (connection.LinkId != linkId || integrationStore.FindLinkByLinkId(linkId) is null))) return null;

        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<HomeAssistantCommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCommandResults[requestId] = new PendingCommand(connection, linkId, completion);

        try
        {
            var payload = BuildCommandPayload(command, parameters, requestId, linkId, commandSecret);
            await SendJsonAsync(connection.Socket, payload, "command", cancellationToken);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(CommandResultTimeout);

            try
            {
                var result = await completion.Task.WaitAsync(timeoutCts.Token);
                return IsActive(connection) && (integrationStore is null || integrationStore.FindLinkByLinkId(linkId) is not null)
                    ? result : null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return HomeAssistantCommandResult.Timeout(requestId);
            }
        }
        finally
        {
            _pendingCommandResults.TryRemove(requestId, out _);
        }
    }

    public bool TryCompleteCommandResult(JsonElement root, string? instanceId = null, WebSocket? socket = null)
    {
        var result = HomeAssistantCommandResult.FromJson(root);
        if (string.IsNullOrWhiteSpace(result.RequestId)) return false;

        if (!_pendingCommandResults.TryGetValue(result.RequestId, out var pending) ||
            (instanceId is not null && !pending.Connection.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase)) ||
            (socket is not null && !ReferenceEquals(pending.Connection.Socket, socket)) ||
            !IsActive(pending.Connection) ||
            (integrationStore is not null && integrationStore.FindLinkByLinkId(pending.LinkId) is null)) return false;
        if (!_pendingCommandResults.TryRemove(new KeyValuePair<string, PendingCommand>(result.RequestId, pending))) return false;
        return pending.Completion.TrySetResult(result);
    }

    public async Task SendUnpairedAsync(
        string instanceId,
        string linkId,
        CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(instanceId, out var connection)) return;

        RemovePending(connection);

        await SendJsonAsync(connection.Socket, new
        {
            type = "unpaired",
            linkId
        }, "unpaired", cancellationToken);
    }

    private static Dictionary<string, object?> BuildCommandPayload(
        string command,
        IReadOnlyDictionary<string, string>? parameters,
        string? requestId,
        string linkId,
        string commandSecret)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["type"] = "command",
            ["command"] = command,
            ["linkId"] = linkId,
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                .ToString(CultureInfo.InvariantCulture),
            ["nonce"] = HomeAssistantCommandAuthentication.GenerateNonce()
        };

        if (!string.IsNullOrWhiteSpace(requestId))
            fields["requestId"] = requestId;

        if (parameters is not null)
            foreach (var pair in parameters)
                if (!string.IsNullOrWhiteSpace(pair.Value))
                    fields[pair.Key] = pair.Value;

        fields["signature"] = HomeAssistantCommandAuthentication.Sign(fields, commandSecret);

        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in fields)
            payload[pair.Key] = pair.Value;

        return payload;
    }

    private async Task SendJsonAsync(WebSocket socket, object payload, string messageClass,
        CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open) return;

        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        _transportMetrics.WebSocketMessage("out", "home-assistant", "text-json", messageClass, bytes.Length);
    }

    private void PurgeExpired()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var pair in _pendingByCode)
        {
            if (pair.Value.ExpiresAtUtc > now) continue;
            _pendingByCode.TryRemove(pair.Key, out _);
            _codeByInstanceId.TryRemove(new KeyValuePair<string, string>(pair.Value.InstanceId, pair.Key));
        }
    }

    private static string GenerateCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<byte> bytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(bytes);

        var builder = new StringBuilder(6);
        foreach (var value in bytes)
            builder.Append(alphabet[value % alphabet.Length]);

        return builder.ToString();
    }

    private static string NormalizeCode(string code)
    {
        return new string(code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    }

    public sealed record PendingHomeAssistantVerification(
        string InstanceId,
        string Code,
        DateTimeOffset ExpiresAtUtc);

    private sealed record PendingCommand(HomeAssistantConnection Connection, string LinkId,
        TaskCompletionSource<HomeAssistantCommandResult> Completion);

    private sealed record HomeAssistantConnection(string InstanceId, WebSocket Socket, string? LinkId = null, string? PendingCode = null);
}
