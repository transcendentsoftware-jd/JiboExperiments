using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jibo.Cloud.Infrastructure.Telemetry;

public sealed class FileTurnTelemetrySink(ILogger<FileTurnTelemetrySink> logger,
    IOptions<TurnTelemetryOptions> options) : ITurnTelemetrySink
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task RecordTurnDiagnosticAsync(string category, IReadOnlyDictionary<string, object?> details, CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        await WriteEventAsync(new
        {
            Type = category,
            Details = details
        }, "Turn telemetry diagnostic", LogLevel.Information, cancellationToken);
    }

    public async Task RecordTranscriptError(Exception ex, string message, CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        await WriteEventAsync(new
        {
            Exception = ex.ToString(),
            Message = message,
            Type = "transcript_error"
        }, "Turn telemetry error", LogLevel.Error, cancellationToken);
    }

    private async Task WriteEventAsync(object payload, string logMessage, LogLevel level, CancellationToken cancellationToken)
    {
        var directory = GetBaseDirectory();
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, $"{DateTimeOffset.UtcNow:yyyyMMdd}.events.ndjson");
        var line = JsonSerializer.Serialize(payload, JsonOptions) + Environment.NewLine;

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(filePath, line, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }

        logger.Log(level, "{LogMessage} {Payload}", logMessage, payload);
    }

    private string GetBaseDirectory()
    {
        return CapturePathResolver.Resolve(
            options.Value.DirectoryPath,
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory);
    }
}
