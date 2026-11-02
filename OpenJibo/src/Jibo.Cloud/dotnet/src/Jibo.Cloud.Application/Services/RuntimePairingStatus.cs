namespace Jibo.Cloud.Application.Services;

public enum RuntimePairingStatusOutcome { Unavailable, Rejected, RateLimited, Read, Uncertain }

/// <summary>Connection-owned binding derived only from a validated initiation descriptor.</summary>
public sealed record RuntimePairingStatusBinding(Guid PairingId, Guid DeviceKeyId, int CredentialVersion,
    string SessionBinding, string ChallengeHash, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "RuntimePairingStatusBinding { evidence redacted }";
}
public sealed record RuntimePairingStatusSnapshot(Guid PairingId, Guid DeviceKeyId, int CredentialVersion,
    string SessionBinding, string RequestDigest, string Status, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "RuntimePairingStatusSnapshot { evidence redacted }";
}
public sealed record RuntimePairingStatusResult(RuntimePairingStatusOutcome Outcome,
    RuntimePairingStatusSnapshot? Snapshot = null, int RetryAfterSeconds = 0)
{
    public override string ToString() => $"RuntimePairingStatusResult {{ {Outcome} }}";
}
public sealed record RuntimePairingStatusAssertion(string Assertion, string RequestDigest)
{
    public override string ToString() => "RuntimePairingStatusAssertion { redacted }";
}
