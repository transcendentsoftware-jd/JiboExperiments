using Jibo.Cloud.Domain.Models;

namespace Jibo.Cloud.Application.Abstractions;

public interface IProtocolTelemetrySink
{
    Task RecordAsync(ProtocolEnvelope envelope, ProtocolDispatchResult result, CancellationToken cancellationToken = default);
}
