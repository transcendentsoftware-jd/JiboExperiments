using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Api.Hosting;
using Jibo.Cloud.Tests.Infrastructure;
using Jibo.Cloud.Tests.Protocol;
using Moq;

namespace Jibo.Cloud.Tests.Api;

public sealed class HubRuntimePairingSessionTests
{
    private static readonly RuntimePairingCredentialEvidence Evidence=RuntimePairingInitiationClientTests.Evidence;
    private static readonly HubTokenCredentialBinding Original=new("0123456789abcdef",DateTimeOffset.UtcNow,false);
    internal sealed class Client : IRuntimePairingInitiationClient
    {
        public bool IsReady { get; set; }=true; public int Calls;
        public Func<byte[],CancellationToken,Task<RuntimePairingInitiationResult>>? Send;
        public Task<RuntimePairingInitiationResult> BeginAsync(RuntimePairingCredentialEvidence evidence,byte[] session,CancellationToken ct=default)
        { Calls++; Assert.Equal(Evidence.Binding,evidence.Binding); return Send?.Invoke(session,ct) ?? Task.FromResult(Created(session)); }
        internal static RuntimePairingInitiationResult Created(byte[] session) => new(RuntimePairingInitiationOutcome.Created,
            new(Guid.NewGuid(),Guid.NewGuid(),3,new string('a',64),new string('b',64),RuntimePairingInitiationIssuer.Encode(session),"request",DateTimeOffset.UtcNow.AddMinutes(5)));
    }

    [Theory]
    [InlineData("created",RuntimePairingInitiationOutcome.Created,1)]
    [InlineData("before-credential",RuntimePairingInitiationOutcome.Rejected,0)]
    [InlineData("before-provider",RuntimePairingInitiationOutcome.Unavailable,0)]
    [InlineData("before-lease",RuntimePairingInitiationOutcome.Rejected,0)]
    [InlineData("after-credential",RuntimePairingInitiationOutcome.Uncertain,1)]
    [InlineData("after-lease",RuntimePairingInitiationOutcome.Uncertain,1)]
    [InlineData("wrong-session",RuntimePairingInitiationOutcome.Uncertain,1)]
    [InlineData("expired-response",RuntimePairingInitiationOutcome.Uncertain,1)]
    public async Task OnlyFreshOriginalCredentialAndLeaseCanDispatchAndReleaseDescriptor(string scenario,RuntimePairingInitiationOutcome outcome,int sends)
    {
        using var key=RuntimePairingCredentialEvidenceTests.Key(); var store=new Mock<ICloudStateStore>();
        var reads=0;
        store.Setup(s => s.ReadHubPairingCredentialEvidenceAsync("issued",Original,key,It.IsAny<CancellationToken>()))
            .Returns(() => { reads++; if(scenario=="before-provider") throw new InvalidOperationException("secret-connection");
                return Task.FromResult<RuntimePairingCredentialEvidence?>(scenario=="before-credential" || scenario=="after-credential" && reads>1 ? null : Evidence); });
        var leases=HubChannelLeasePolicyTests.WorkingStore(); var checks=0;
        leases.Setup(s => s.IsCurrentAsync(It.IsAny<byte[]>(),HubChannel.Listen,It.IsAny<Guid>(),1,It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(scenario!="before-lease" && (scenario!="after-lease" || ++checks==1)));
        using var policy=new HubChannelLeasePolicy(leases.Object,new byte[32]); await using var lease=(await policy.AcquireAsync("issued",HubChannel.Listen,default))!;
        var client=new Client { Send=(binding,ct) =>
        {
            var response=Client.Created(binding);
            if(scenario=="wrong-session") response=response with { Descriptor=response.Descriptor! with { SessionBinding="wrong" } };
            if(scenario=="expired-response") response=response with { Descriptor=response.Descriptor! with { ExpiresAt=DateTimeOffset.UtcNow.AddSeconds(-1) } };
            return Task.FromResult(response);
        } };
        var session=new HubRuntimePairingSession(store.Object,"issued",Original,Evidence,key,lease,client,default);
        var result=await session.BeginAsync(); Assert.Equal(outcome,result.Outcome); Assert.Equal(sends,client.Calls);
        if(outcome!=RuntimePairingInitiationOutcome.Created) Assert.Null(result.Descriptor);
        Assert.DoesNotContain("secret-connection",result.ToString());
    }

    [Fact]
    public async Task SingleAttemptPerConnectionPreventsConcurrentAndUncertainRetries()
    {
        using var key=RuntimePairingCredentialEvidenceTests.Key(); var store=new Mock<ICloudStateStore>();
        store.Setup(s => s.ReadHubPairingCredentialEvidenceAsync("issued",Original,key,It.IsAny<CancellationToken>())).ReturnsAsync(Evidence);
        var leases=HubChannelLeasePolicyTests.WorkingStore(); using var policy=new HubChannelLeasePolicy(leases.Object,new byte[32]);
        await using var lease=(await policy.AcquireAsync("issued",HubChannel.Listen,default))!;
        var entered=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var gate=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client=new Client { Send=async (s,ct) => { entered.SetResult(true); await gate.Task; return new(RuntimePairingInitiationOutcome.Uncertain); } };
        var session=new HubRuntimePairingSession(store.Object,"issued",Original,Evidence,key,lease,client,default);
        var first=session.BeginAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RuntimePairingInitiationOutcome.Rejected,(await session.BeginAsync()).Outcome); gate.SetResult(true);
        Assert.Equal(RuntimePairingInitiationOutcome.Uncertain,(await first).Outcome);
        Assert.Equal(RuntimePairingInitiationOutcome.Rejected,(await session.BeginAsync()).Outcome); Assert.Equal(1,client.Calls);
    }

    [Fact]
    public async Task StalledAuthorityCannotDispatchAfterBoundedWait()
    {
        using var key=RuntimePairingCredentialEvidenceTests.Key(); var store=new Mock<ICloudStateStore>();
        var pending=new TaskCompletionSource<RuntimePairingCredentialEvidence?>(TaskCreationOptions.RunContinuationsAsynchronously); CancellationToken observed=default;
        store.Setup(s => s.ReadHubPairingCredentialEvidenceAsync("issued",Original,key,It.IsAny<CancellationToken>()))
            .Returns((string _,HubTokenCredentialBinding _,RuntimePairingCredentialEvidenceKey _,CancellationToken ct) => { observed=ct; return pending.Task; });
        var leases=HubChannelLeasePolicyTests.WorkingStore(); using var policy=new HubChannelLeasePolicy(leases.Object,new byte[32]);
        await using var lease=(await policy.AcquireAsync("issued",HubChannel.Listen,default))!; var client=new Client();
        var session=new HubRuntimePairingSession(store.Object,"issued",Original,Evidence,key,lease,client,default,TimeSpan.FromMilliseconds(100),TimeSpan.FromMilliseconds(20));
        Assert.Equal(RuntimePairingInitiationOutcome.Unavailable,(await session.BeginAsync().WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
        Assert.True(observed.IsCancellationRequested); pending.SetResult(Evidence); await Task.Delay(20); Assert.Equal(0,client.Calls);
    }

    [Fact]
    public async Task LateClientResponseAfterTotalBudgetCannotRunPostChecksOrRetry()
    {
        using var key=RuntimePairingCredentialEvidenceTests.Key(); var store=new Mock<ICloudStateStore>(); var reads=0;
        store.Setup(s => s.ReadHubPairingCredentialEvidenceAsync("issued",Original,key,It.IsAny<CancellationToken>()))
            .Returns(() => { reads++; return Task.FromResult<RuntimePairingCredentialEvidence?>(Evidence); });
        var leases=HubChannelLeasePolicyTests.WorkingStore(); using var policy=new HubChannelLeasePolicy(leases.Object,new byte[32]);
        await using var lease=(await policy.AcquireAsync("issued",HubChannel.Listen,default))!;
        var pending=new TaskCompletionSource<RuntimePairingInitiationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed=default; byte[]? sent=null;
        var client=new Client { Send=(s,ct) => { observed=ct; sent=s; return pending.Task; } };
        var session=new HubRuntimePairingSession(store.Object,"issued",Original,Evidence,key,lease,client,default,TimeSpan.FromMilliseconds(100));
        Assert.Equal(RuntimePairingInitiationOutcome.Uncertain,(await session.BeginAsync().WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
        Assert.True(observed.IsCancellationRequested); Assert.NotNull(sent); pending.SetResult(Client.Created(sent));
        await Task.Delay(20); Assert.Equal(1,reads); Assert.Equal(1,client.Calls);
        Assert.Equal(RuntimePairingInitiationOutcome.Rejected,(await session.BeginAsync()).Outcome);
    }

    [Fact]
    public async Task ConnectionCancellationAfterDispatchDiscardsCreatedResponseAndRetainedCapability()
    {
        using var key=RuntimePairingCredentialEvidenceTests.Key(); using var connection=new CancellationTokenSource(); var store=new Mock<ICloudStateStore>();
        store.Setup(s => s.ReadHubPairingCredentialEvidenceAsync("issued",Original,key,It.IsAny<CancellationToken>())).ReturnsAsync(Evidence);
        var leases=HubChannelLeasePolicyTests.WorkingStore(); using var policy=new HubChannelLeasePolicy(leases.Object,new byte[32]);
        await using var lease=(await policy.AcquireAsync("issued",HubChannel.Listen,default))!;
        var client=new Client { Send=(s,ct) => { connection.Cancel(); return Task.FromResult(Client.Created(s)); } };
        var session=new HubRuntimePairingSession(store.Object,"issued",Original,Evidence,key,lease,client,connection.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.BeginAsync());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.BeginAsync()); Assert.Equal(1,client.Calls);
    }
}
