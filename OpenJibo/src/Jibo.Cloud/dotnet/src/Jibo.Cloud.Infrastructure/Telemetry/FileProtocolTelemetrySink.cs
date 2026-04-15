using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jibo.Cloud.Infrastructure.Telemetry;

public sealed class FileProtocolTelemetrySink(
    ILogger<FileProtocolTelemetrySink> logger,
    IOptions<ProtocolTelemetryOptions> options) : IProtocolTelemetrySink
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task RecordAsync(ProtocolEnvelope envelope, ProtocolDispatchResult result, CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        var directory = CapturePathResolver.Resolve(
            options.Value.DirectoryPath,
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory);
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, $"{DateTimeOffset.UtcNow:yyyyMMdd}.events.ndjson");

        var payload = new
        {
            capturedUtc = DateTimeOffset.UtcNow,
            request = new
            {
                envelope.RequestId,
                envelope.Transport,
                envelope.Method,
                envelope.HostName,
                envelope.Path,
                envelope.ServicePrefix,
                envelope.Operation,
                envelope.DeviceId,
                envelope.CorrelationId,
                envelope.FirmwareVersion,
                envelope.ApplicationVersion,
                envelope.Headers,
                envelope.BodyText
            },
            response = new
            {
                result.StatusCode,
                result.ContentType,
                result.Headers,
                result.BodyText
            }
        };

        var line = JsonSerializer.Serialize(payload) + Environment.NewLine;

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
            "HTTP telemetry {Method} {Host}{Path} target={Target} status={StatusCode}",
            envelope.Method,
            envelope.HostName,
            envelope.Path,
            $"{envelope.ServicePrefix}.{envelope.Operation}".Trim('.'),
            result.StatusCode);
    }
}
