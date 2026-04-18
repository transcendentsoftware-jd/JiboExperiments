using Jibo.Cloud.Application.Abstractions;

namespace Jibo.Cloud.Application.Services;

public sealed class NullTurnTelemetrySink : ITurnTelemetrySink
{
    public Task RecordTranscriptError(Exception ex, string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
}