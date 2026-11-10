using System.Net.WebSockets;
using Jibo.Cloud.Api.Hosting;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Tests.Application;
using Jibo.Cloud.Tests.Protocol;

namespace Jibo.Cloud.Tests.Api;

public sealed partial class WebSocketRequestCoordinatorTests
{
    [Theory]
    [InlineData("listen",false)][InlineData("proactive",false)][InlineData("notification",false)]
    [InlineData("listen",true)][InlineData("proactive",true)][InlineData("notification",true)]
    public async Task Registry_restart_denies_removed_or_rotated_bearer_reconnect_on_every_channel(string channel, bool rotate)
    {
        var folder=Path.Combine(Path.GetTempPath(),"jibo-registry-socket-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            using var key=RuntimePairingCredentialEvidenceTests.Key();
            var credential=TrustedProtocolCredentialRegistryTests.A; var peer=TrustedProtocolCredentialRegistryTests.B;
            var before=TrustedProtocolCredentialRegistryTests.Registry(key,credential,peer);
            var first=TrustedProtocolCredentialRegistryTests.Store(before,Path.Combine(folder,"state.json"));
            var client=new RuntimeAccessTests.Client();
            first.GetOrCreateDevice(client.Identity.DeviceId,null,null);
            var proof=new AwsSigV4RequestVerifier(first,trustedCredentials:before).Verify(TrustedProtocolCredentialRegistryTests.Sign(credential,client.Identity.DeviceId));
            var binding=HubTokenCredentialBinding.FromVerification(proof)!;
            var evidence=proof.VerifiedProof!.CreatePairingCredentialEvidence(key)!;
            var token=channel=="notification"?first.IssueRobotTokenWithCredentialEvidence(client.Identity.DeviceId,binding,evidence)
                :first.IssueHubToken(client.Identity.DeviceId,false,binding,evidence);
            using var leases=new HubChannelLeasePolicy(HubChannelLeasePolicyTests.WorkingStore().Object,new byte[32]);
            WebSocketRequestCoordinator Coordinator(Jibo.Cloud.Infrastructure.Persistence.InMemoryCloudStateStore store) =>
                new(CreateWebSocketService(out _,store),new HomeAssistantWebSocketHandler(new HomeAssistantConnectionRegistry()),
                    new RecordingWebSocketTelemetrySink(),store){LeasePolicy=leases,PairingCredentialKey=key,AccessClient=client};
            var initial=new FakeWebSocket(new FakeWebSocketFrame(WebSocketMessageType.Close,[]));
            await Coordinator(first).HandleAsync(AccessChannelContext(initial,token,channel)); Assert.True(initial.Accepted);
            var replacement=credential with { SecretAccessKey="rotated-synthetic-secret-A-0004" };
            var after=rotate?TrustedProtocolCredentialRegistryTests.Registry(key,replacement,peer):TrustedProtocolCredentialRegistryTests.Registry(key,peer);
            var restarted=TrustedProtocolCredentialRegistryTests.Store(after,Path.Combine(folder,"state.json"));
            var coordinator=Coordinator(restarted); var denied=new FakeWebSocket(new FakeWebSocketFrame(WebSocketMessageType.Close,[]));
            var context=AccessChannelContext(denied,token,channel); var calls=client.Calls;
            await coordinator.HandleAsync(context);Assert.False(denied.Accepted);Assert.Equal(401,context.Response.StatusCode);Assert.Equal(calls,client.Calls);
            if(rotate)
            {
                var fresh=new AwsSigV4RequestVerifier(restarted,trustedCredentials:after).Verify(TrustedProtocolCredentialRegistryTests.Sign(replacement,client.Identity.DeviceId));
                var newBinding=HubTokenCredentialBinding.FromVerification(fresh)!;var newEvidence=fresh.VerifiedProof!.CreatePairingCredentialEvidence(key)!;
                var newToken=channel=="notification"?restarted.IssueRobotTokenWithCredentialEvidence(client.Identity.DeviceId,newBinding,newEvidence)
                    :restarted.IssueHubToken(client.Identity.DeviceId,false,newBinding,newEvidence);
                var allowed=new FakeWebSocket(new FakeWebSocketFrame(WebSocketMessageType.Close,[]));
                await coordinator.HandleAsync(AccessChannelContext(allowed,newToken,channel));Assert.True(allowed.Accepted);
                Assert.Equal(calls+1,client.Calls);Assert.Null(restarted.FindIssuedToken(token));
            }
        }
        finally { Directory.Delete(folder,true); }
    }
}
