using Jibo.Cloud.Application.Services;

namespace Jibo.Cloud.Application.Abstractions;

public enum HubTokenReplayAdmissionOutcome { Disabled, Admitted, Rejected, Unavailable }

public interface IHubTokenReplayAdmission
{
    Task<HubTokenReplayAdmissionOutcome> AdmitAsync(AwsSigV4Verification verification,
        CancellationToken cancellationToken = default);
}

/// <summary>Atomic consume, separate from best-effort replay observation. No raw credentials or signatures.</summary>
public interface IHubTokenReplayAdmissionStore
{
    Task<bool> TryConsumeAsync(IReadOnlyList<byte[]> digests, IReadOnlyList<short> keyVersions,
        DateTimeOffset signedAt, CancellationToken cancellationToken = default);
}
