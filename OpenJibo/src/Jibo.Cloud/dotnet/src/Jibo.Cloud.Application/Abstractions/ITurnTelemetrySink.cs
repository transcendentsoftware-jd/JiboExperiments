namespace Jibo.Cloud.Application.Abstractions;

public interface ITurnTelemetrySink
{
    Task RecordTranscriptError(Exception ex, string message, CancellationToken cancellationToken = default);
}