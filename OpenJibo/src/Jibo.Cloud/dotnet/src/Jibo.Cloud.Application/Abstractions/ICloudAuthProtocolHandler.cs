using Jibo.Cloud.Domain.Models;

namespace Jibo.Cloud.Application.Abstractions;

public interface ICloudAuthProtocolHandler
{
    ProtocolDispatchResult HandleAccount(string operation, ProtocolEnvelope envelope);
    Task<ProtocolDispatchResult> HandleAccountAsync(string operation, ProtocolEnvelope envelope,
        CancellationToken cancellationToken = default) => Task.FromResult(HandleAccount(operation, envelope));
    ProtocolDispatchResult HandleNotification(string operation, ProtocolEnvelope envelope);
}
