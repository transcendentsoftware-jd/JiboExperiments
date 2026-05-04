namespace Jibo.Cloud.Application.Abstractions;

public interface ITurnTelemetrySink
{
    Task RecordTurnDiagnosticAsync(string category, IReadOnlyDictionary<string, object?> details, CancellationToken cancellationToken = default);

    Task RecordTranscriptError(Exception ex, string message, CancellationToken cancellationToken = default);
}
