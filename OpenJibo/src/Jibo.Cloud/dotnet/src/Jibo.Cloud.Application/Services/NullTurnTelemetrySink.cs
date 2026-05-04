using Jibo.Cloud.Application.Abstractions;

namespace Jibo.Cloud.Application.Services;

public sealed class NullTurnTelemetrySink : ITurnTelemetrySink
{
    public Task RecordTurnDiagnosticAsync(string category, IReadOnlyDictionary<string, object?> details, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RecordTranscriptError(Exception ex, string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
