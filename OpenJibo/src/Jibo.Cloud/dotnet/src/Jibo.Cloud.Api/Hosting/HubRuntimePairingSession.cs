using System.Security.Cryptography;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;

namespace Jibo.Cloud.Api.Hosting;

/// <summary>One connection-owned attempt, guarded before dispatch and after a created response.</summary>
internal sealed class HubRuntimePairingSession(ICloudStateStore store,string bearer,HubTokenCredentialBinding original,
    RuntimePairingCredentialEvidence evidence,RuntimePairingCredentialEvidenceKey key,HubChannelLeaseHandle lease,
    IRuntimePairingInitiationClient client,CancellationToken connectionCancellation,
    TimeSpan? totalBudget = null,TimeSpan? authorityTimeout = null,TimeProvider? statusClock = null) : IRuntimePairingSession
{
    private int _attempted;
    private RuntimePairingStatusBinding? _statusBinding;
    private int _statusReading;
    private bool _hasStatusRead;
    private long _lastStatusRead;
    private TimeSpan _statusDelay = TimeSpan.FromSeconds(5);
    private readonly TimeProvider _statusClock = statusClock ?? TimeProvider.System;
    public async Task<RuntimePairingInitiationResult> BeginAsync(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested(); connectionCancellation.ThrowIfCancellationRequested();
        if (!client.IsReady || !key.IsReady) return new(RuntimePairingInitiationOutcome.Unavailable);
        if (Interlocked.CompareExchange(ref _attempted,1,0) != 0) return new(RuntimePairingInitiationOutcome.Rejected);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation,connectionCancellation);
        var budget = totalBudget ?? TimeSpan.FromSeconds(6); deadline.CancelAfter(budget);
        var dispatched = 0;
        try { return await ExecuteAsync().WaitAsync(budget,deadline.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || connectionCancellation.IsCancellationRequested)
        { deadline.Cancel(); throw; }
        catch (Exception)
        { deadline.Cancel(); return new(Volatile.Read(ref dispatched) != 0 ? RuntimePairingInitiationOutcome.Uncertain : RuntimePairingInitiationOutcome.Unavailable); }

        async Task<bool> CredentialCurrentAsync()
        {
            deadline.Token.ThrowIfCancellationRequested();
            var current = await store.ReadHubPairingCredentialEvidenceAsync(bearer,original,key,deadline.Token)
                .WaitAsync(authorityTimeout ?? TimeSpan.FromSeconds(2),deadline.Token);
            deadline.Token.ThrowIfCancellationRequested(); return current == evidence;
        }
        async Task<RuntimePairingInitiationResult> ExecuteAsync()
        {
            if (!await CredentialCurrentAsync()) return new(RuntimePairingInitiationOutcome.Rejected);
            var binding = await lease.CreatePairingSessionBindingAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (binding is null) return new(RuntimePairingInitiationOutcome.Rejected);
            deadline.Token.ThrowIfCancellationRequested();
            Volatile.Write(ref dispatched,1);
            var result = await client.BeginAsync(evidence,binding,deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (result.Outcome != RuntimePairingInitiationOutcome.Created) return result;
            if (!await CredentialCurrentAsync()) return new(RuntimePairingInitiationOutcome.Uncertain);
            var after = await lease.CreatePairingSessionBindingAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (after is not null && CryptographicOperations.FixedTimeEquals(binding,after) && result.Descriptor is not null &&
                result.Descriptor.ExpiresAt > DateTimeOffset.UtcNow &&
                result.Descriptor.SessionBinding == RuntimePairingInitiationIssuer.Encode(binding))
            {
                if (client.IsStatusReady)
                {
                    var challenge = Convert.FromHexString(result.Descriptor.RobotChallenge);
                    try
                    {
                        Volatile.Write(ref _statusBinding, new(result.Descriptor.PairingId, result.Descriptor.DeviceKeyId,
                            result.Descriptor.CredentialVersion, result.Descriptor.SessionBinding,
                            RuntimePairingInitiationIssuer.Encode(SHA256.HashData(challenge)), result.Descriptor.ExpiresAt));
                    }
                    finally { CryptographicOperations.ZeroMemory(challenge); }
                }
                return result;
            }
            return new(RuntimePairingInitiationOutcome.Uncertain);
        }
    }

    public async Task<RuntimePairingStatusResult> ReadStatusAsync(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested(); connectionCancellation.ThrowIfCancellationRequested();
        var expected = Volatile.Read(ref _statusBinding);
        if (!client.IsStatusReady || !key.IsReady || expected is null) return new(RuntimePairingStatusOutcome.Unavailable);
        if (expected.ExpiresAt <= _statusClock.GetUtcNow())
        { Interlocked.Exchange(ref _statusBinding, null); return new(RuntimePairingStatusOutcome.Rejected); }
        if (Interlocked.CompareExchange(ref _statusReading, 1, 0) != 0)
            return new(RuntimePairingStatusOutcome.RateLimited, RetryAfterSeconds: 1);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation, connectionCancellation);
        var budget = totalBudget ?? TimeSpan.FromSeconds(6); deadline.CancelAfter(budget);
        var dispatched = 0;
        try
        {
            var elapsed = _statusClock.GetElapsedTime(_lastStatusRead);
            if (_hasStatusRead && elapsed < _statusDelay)
                return new(RuntimePairingStatusOutcome.RateLimited, RetryAfterSeconds: Math.Max(1, (int)Math.Ceiling((_statusDelay - elapsed).TotalSeconds)));
            _lastStatusRead = _statusClock.GetTimestamp(); _hasStatusRead = true; _statusDelay = TimeSpan.FromSeconds(5);
            var result = await ExecuteAsync().WaitAsync(budget, deadline.Token);
            if (result.Outcome == RuntimePairingStatusOutcome.RateLimited)
            {
                _lastStatusRead = _statusClock.GetTimestamp();
                _statusDelay = TimeSpan.FromSeconds(Math.Max(5, Math.Clamp(result.RetryAfterSeconds, 1, 60)));
            }
            return result;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || connectionCancellation.IsCancellationRequested)
        { deadline.Cancel(); throw; }
        catch (Exception)
        { deadline.Cancel(); return new(Volatile.Read(ref dispatched) != 0 ? RuntimePairingStatusOutcome.Uncertain : RuntimePairingStatusOutcome.Unavailable); }
        finally { Interlocked.Exchange(ref _statusReading, 0); }

        async Task<bool> CredentialCurrentAsync()
        {
            var current = await store.ReadHubPairingCredentialEvidenceAsync(bearer, original, key, deadline.Token)
                .WaitAsync(authorityTimeout ?? TimeSpan.FromSeconds(2), deadline.Token);
            deadline.Token.ThrowIfCancellationRequested(); return current == evidence;
        }
        async Task<RuntimePairingStatusResult> ExecuteAsync()
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (!await CredentialCurrentAsync()) return new(RuntimePairingStatusOutcome.Rejected);
            var before = await lease.CreatePairingSessionBindingAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (before is null || RuntimePairingInitiationIssuer.Encode(before) != expected.SessionBinding)
                return new(RuntimePairingStatusOutcome.Rejected);
            Volatile.Write(ref dispatched, 1);
            var result = await client.ReadStatusAsync(evidence, before, expected, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (result.Outcome != RuntimePairingStatusOutcome.Read) return result;
            if (!await CredentialCurrentAsync()) return new(RuntimePairingStatusOutcome.Uncertain);
            var after = await lease.CreatePairingSessionBindingAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            var snapshot = result.Snapshot;
            return after is not null && CryptographicOperations.FixedTimeEquals(before, after) && snapshot is not null &&
                snapshot.PairingId == expected.PairingId && snapshot.DeviceKeyId == expected.DeviceKeyId &&
                snapshot.CredentialVersion == expected.CredentialVersion && snapshot.SessionBinding == expected.SessionBinding &&
                snapshot.ExpiresAt == expected.ExpiresAt && snapshot.ExpiresAt > _statusClock.GetUtcNow() &&
                snapshot.Status is ("pending" or "authorized")
                ? result : new(RuntimePairingStatusOutcome.Uncertain);
        }
    }
    public override string ToString() => "HubRuntimePairingSession { redacted }";
}
