using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Domain.Models;

namespace Jibo.Cloud.Application.Services;

public sealed class NullProtocolTelemetrySink : IProtocolTelemetrySink
{
    public Task RecordAsync(ProtocolEnvelope envelope, ProtocolDispatchResult result, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
