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

    public async Task RecordTranscriptError(Exception ex, string message, CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        await WriteErrorAsync(ex, message, cancellationToken);
    }
    
    private async Task WriteErrorAsync(Exception ex, string message, CancellationToken cancellationToken)
    {
        var directory = GetBaseDirectory();
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, $"{DateTimeOffset.UtcNow:yyyyMMdd}.events.ndjson");
        var line = JsonSerializer.Serialize(new { Exception = ex.ToString(), Message = message }, JsonOptions) + Environment.NewLine;

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(filePath, line, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }

        logger.LogError("Turn telemetry Message={Message} Exception={Exception}", message, ex);
    }

    private string GetBaseDirectory()
    {
        return CapturePathResolver.Resolve(
            options.Value.DirectoryPath,
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory);
    }
}