namespace Jibo.Cloud.Application.Abstractions;

public enum HubChannel : short { Listen = 1, Proactive = 2 }

public sealed record HubChannelLease(long Generation, DateTimeOffset ExpiresUtc);

/// <summary>
/// Durable per-token/channel lease primitive, not authentication or side-effect fencing.
/// Digests must be opaque, environment-scoped and stable across all participating replicas.
/// Callers must treat exceptions/cancellation as uncertain results and never infer ownership.
/// </summary>
public interface IHubChannelLeaseStore
{
    Task<HubChannelLease> AcquireAsync(byte[] tokenDigest, HubChannel channel, Guid owner,
        CancellationToken cancellationToken = default);
    Task<HubChannelLease?> TryRenewAsync(byte[] tokenDigest, HubChannel channel, Guid owner, long generation,
        CancellationToken cancellationToken = default);
    Task<bool> IsCurrentAsync(byte[] tokenDigest, HubChannel channel, Guid owner, long generation,
        CancellationToken cancellationToken = default);
    Task<bool> TryReleaseAsync(byte[] tokenDigest, HubChannel channel, Guid owner, long generation,
        CancellationToken cancellationToken = default);
}
