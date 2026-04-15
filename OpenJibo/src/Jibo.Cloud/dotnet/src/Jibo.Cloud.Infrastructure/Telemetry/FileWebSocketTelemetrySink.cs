using System.Collections.Concurrent;
using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jibo.Cloud.Infrastructure.Telemetry;

public sealed class FileWebSocketTelemetrySink(
    ILogger<FileWebSocketTelemetrySink> logger,
    IOptions<WebSocketTelemetryOptions> options) : IWebSocketTelemetrySink
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly ConcurrentDictionary<string, CapturedWebSocketFixtureBuilder> _fixtures = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task RecordConnectionOpenedAsync(WebSocketMessageEnvelope envelope, CloudSession session, CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        _fixtures[session.SessionId] = new CapturedWebSocketFixtureBuilder
        {
            Session = new CapturedWebSocketFixtureSession
            {
                HostName = envelope.HostName,
                Path = envelope.Path,
                Kind = envelope.Kind,
                Token = envelope.Token
            }
        };

        await WriteRecordAsync(BuildRecord("connection_opened", envelope, session, null, "internal", null, null), cancellationToken);
    }

    public Task RecordInboundAsync(WebSocketMessageEnvelope envelope, CloudSession session, string? messageType, CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled)
        {
            return Task.CompletedTask;
        }

        return WriteRecordAsync(BuildRecord("message_in", envelope, session, messageType, "in", null, null), cancellationToken);
    }

    public Task RecordTurnEventAsync(WebSocketMessageEnvelope envelope, CloudSession session, string eventType, IReadOnlyDictionary<string, object?> details, CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled)
        {
            return Task.CompletedTask;
        }

        return WriteRecordAsync(BuildRecord(eventType, envelope, session, null, "internal", null, details), cancellationToken);
    }

    public async Task RecordOutboundAsync(WebSocketMessageEnvelope envelope, CloudSession session, IReadOnlyList<WebSocketReply> replies, CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        var replyTypes = replies
            .Select(reply => ReadReplyType(reply.Text))
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Select(type => type!)
            .ToArray();

        await WriteRecordAsync(BuildRecord("message_out", envelope, session, null, "out", replyTypes, null), cancellationToken);

        if (_fixtures.TryGetValue(session.SessionId, out var fixture))
        {
            fixture.Steps.Add(new CapturedWebSocketFixtureStep
            {
                Text = ParseJsonElement(envelope.Text),
                Binary = envelope.Binary?.Select(value => (int)value).ToArray(),
                ExpectedReplyTypes = replyTypes
            });
        }
    }

    public async Task RecordConnectionClosedAsync(WebSocketMessageEnvelope envelope, CloudSession session, string reason, CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        await WriteRecordAsync(BuildRecord(
            "connection_closed",
            envelope,
            session,
            null,
            "internal",
            null,
            new Dictionary<string, object?> { ["reason"] = reason }), cancellationToken);

        if (!options.Value.ExportFixtures || !_fixtures.TryRemove(session.SessionId, out var fixture) || fixture.Steps.Count == 0)
        {
            return;
        }

        var fixtureName = BuildFixtureName(session, fixture);
        var capturedFixture = new CapturedWebSocketFixture
        {
            Name = fixtureName,
            Session = fixture.Session,
            Steps = [.. fixture.Steps]
        };

        var fixtureDirectory = Path.Combine(GetBaseDirectory(), "fixtures");
        Directory.CreateDirectory(fixtureDirectory);
        var fixturePath = Path.Combine(fixtureDirectory, $"{fixtureName}.flow.json");

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await File.WriteAllTextAsync(fixturePath, JsonSerializer.Serialize(capturedFixture, JsonOptions), cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }

        logger.LogInformation("Exported websocket fixture {FixturePath}", fixturePath);
    }

    private async Task WriteRecordAsync(WebSocketTelemetryRecord record, CancellationToken cancellationToken)
    {
        var directory = GetBaseDirectory();
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, $"{DateTimeOffset.UtcNow:yyyyMMdd}.events.ndjson");
        var line = JsonSerializer.Serialize(record) + Environment.NewLine;

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(filePath, line, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }

        logger.LogInformation(
            "WebSocket telemetry {EventType} session={SessionId} transId={TransId} bufferedBytes={BufferedBytes} replyTypes={ReplyTypes}",
            record.EventType,
            record.SessionId,
            record.TransId,
            record.BufferedAudioBytes,
            string.Join(",", record.ReplyTypes));
    }

    private static WebSocketTelemetryRecord BuildRecord(
        string eventType,
        WebSocketMessageEnvelope envelope,
        CloudSession session,
        string? messageType,
        string direction,
        IReadOnlyList<string>? replyTypes,
        IReadOnlyDictionary<string, object?>? details) => new()
    {
            EventType = eventType,
            SessionId = session.SessionId,
            ConnectionId = envelope.ConnectionId,
            Token = envelope.Token,
            HostName = envelope.HostName,
            Path = envelope.Path,
            Kind = envelope.Kind,
            TransId = session.TurnState.TransId ?? session.LastTransId,
            MessageType = messageType,
            Direction = direction,
            Text = envelope.Text,
            BinaryLength = envelope.Binary?.Length,
            ReplyTypes = replyTypes ?? [],
            BufferedAudioBytes = session.TurnState.BufferedAudioBytes,
            BufferedAudioChunks = session.TurnState.BufferedAudioChunkCount,
            FinalizeAttempts = session.TurnState.FinalizeAttemptCount,
            AwaitingTurnCompletion = session.TurnState.AwaitingTurnCompletion,
            Details = details ?? new Dictionary<string, object?>()
        };

    private static string? ReadReplyType(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                ? type.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static JsonElement? ParseJsonElement(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private string GetBaseDirectory()
    {
        return CapturePathResolver.Resolve(
            options.Value.DirectoryPath,
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory);
    }

    private static string BuildFixtureName(CloudSession session, CapturedWebSocketFixtureBuilder fixture)
    {
        var host = SanitizeName(fixture.Session.HostName);
        var kind = SanitizeName(fixture.Session.Kind);
        var transId = SanitizeName(session.TurnState.TransId ?? session.LastTransId ?? session.SessionId);
        return $"{host}-{kind}-{transId}";
    }

    private static string SanitizeName(string value)
    {
        var chars = value
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray();

        return string.Join(string.Empty, new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    private sealed class CapturedWebSocketFixtureBuilder
    {
        public CapturedWebSocketFixtureSession Session { get; init; } = new();
        public List<CapturedWebSocketFixtureStep> Steps { get; } = [];
    }
}
