using System.Security.Cryptography;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;

namespace Jibo.Cloud.Api.Hosting;

/// <summary>One connection-owned attempt, guarded before dispatch and after a created response.</summary>
internal sealed class HubRuntimePairingSession(ICloudStateStore store,string bearer,HubTokenCredentialBinding original,
    RuntimePairingCredentialEvidence evidence,RuntimePairingCredentialEvidenceKey key,HubChannelLeaseHandle lease,
    IRuntimePairingInitiationClient client,CancellationToken connectionCancellation,
    TimeSpan? totalBudget = null,TimeSpan? authorityTimeout = null) : IRuntimePairingSession
{
    private int _attempted;
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
            return after is not null && CryptographicOperations.FixedTimeEquals(binding,after) && result.Descriptor is not null &&
                result.Descriptor.ExpiresAt > DateTimeOffset.UtcNow &&
                result.Descriptor.SessionBinding == RuntimePairingInitiationIssuer.Encode(binding)
                ? result : new(RuntimePairingInitiationOutcome.Uncertain);
        }
    }
    public override string ToString() => "HubRuntimePairingSession { redacted }";
}
