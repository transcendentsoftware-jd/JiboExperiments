using Jibo.Cloud.Application.Abstractions;

namespace Jibo.Cloud.Application.Services;

public sealed class HubTokenReplayAdmissionOptions
{
    public const string SectionName = "OpenJibo:Security:HubTokenReplayAdmission";
    public bool Enabled { get; set; }
    public string? ConnectionString { get; set; }
    public string? HmacKey { get; set; }
    public short KeyVersion { get; set; } = 1;
    public string? PreviousHmacKey { get; set; }
    public short PreviousKeyVersion { get; set; }
}

public sealed class FixedHubTokenReplayAdmission(HubTokenReplayAdmissionOutcome outcome) : IHubTokenReplayAdmission
{
    public Task<HubTokenReplayAdmissionOutcome> AdmitAsync(AwsSigV4Verification verification,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(outcome);
    }
}

/// <summary>Strict, synchronous admission before issuance; no retries after an uncertain consume.</summary>
public sealed class HubTokenReplayAdmission : IHubTokenReplayAdmission
{
    public const string Operation = "Account.CreateHubToken";
    private readonly IHubTokenReplayAdmissionStore _store;
    private readonly AwsSigV4ReplayDigestKey[] _keys;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _timeout;

    public HubTokenReplayAdmission(IHubTokenReplayAdmissionStore store, AwsSigV4ReplayDigestKey current,
        AwsSigV4ReplayDigestKey? previous = null, TimeProvider? clock = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(current);
        ValidateKeySlots(current, previous);
        _store = store;
        _keys = previous is null ? [current] : [current, previous];
        _clock = clock ?? TimeProvider.System;
        _timeout = timeout ?? TimeSpan.FromSeconds(2);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromSeconds(5))
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public static void ValidateKeySlots(AwsSigV4ReplayDigestKey current, AwsSigV4ReplayDigestKey? previous)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (previous is not null && (previous.Version == current.Version || previous.HasSameMaterial(current)))
            throw new ArgumentException("Replay admission key slots must have distinct versions and material.");
    }

    public async Task<HubTokenReplayAdmissionOutcome> AdmitAsync(AwsSigV4Verification verification,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var proof = verification.VerifiedProof;
        // Pin immutable proof classifications as well as the public observation record.
        // A caller cannot upgrade credential-only proof by changing record flags or time.
        if (!verification.OperationAuthenticated || proof is null || proof.AuthenticatedOperation != Operation ||
            verification.SignedAt != proof.SignedAt ||
            (_clock.GetUtcNow() - proof.SignedAt).Duration() > AwsSigV4RequestVerifier.MaximumClockSkew)
            return HubTokenReplayAdmissionOutcome.Rejected;
        var digests = _keys.Select(key => proof.CreateReplayDigest(key, Operation)).ToArray();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            return await _store.TryConsumeAsync(digests, _keys.Select(k => k.Version).ToArray(), proof.SignedAt,
                    deadline.Token).WaitAsync(_timeout, cancellationToken).ConfigureAwait(false)
                ? HubTokenReplayAdmissionOutcome.Admitted : HubTokenReplayAdmissionOutcome.Rejected;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            deadline.Cancel();
            // An uncertain database result may already be committed. Never retry or issue a token.
            return HubTokenReplayAdmissionOutcome.Unavailable;
        }
    }
}
