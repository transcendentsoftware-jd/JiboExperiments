using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Persistence;

namespace Jibo.Cloud.Tests.Application;

public sealed class RuntimeProtocolAccessTests
{
 [Theory][InlineData("valid")][InlineData("header")][InlineData("body")][InlineData("unpaid")][InlineData("no-signature")]
 public async Task Http_authority_is_derived_before_any_protocol_identity_or_token_mutation(string scenario)
 {
  var store=new InMemoryCloudStateStore();using var key=Protocol.RuntimePairingCredentialEvidenceTests.Key();var client=new RuntimeAccessTests.Client{Allowed=scenario!="unpaid"};
  var body="{\"deviceId\":\""+(scenario=="body"?"other-device":client.Identity.DeviceId)+"\"}";
  var signed=Protocol.AwsSigV4RequestVerifierTests.Sign(store.GetAccount(),body,DateTimeOffset.UtcNow,["host","x-amz-content-sha256","x-amz-date","x-amz-target"],region:"api",target:"Account_20151111.CreateHubToken");
  var envelope=new ProtocolEnvelope{Method=signed.Method,Path=signed.Path,BodyText=signed.BodyText,BodyBytes=signed.BodyBytes,Headers=scenario=="no-signature"?new Dictionary<string,string>():signed.Headers,HostName=signed.HostName,Operation="CreateHubToken",ServicePrefix="Account_20151111",DeviceId=scenario=="header"?"other-device":null};
  var policy=new RuntimeProtocolAccessPolicy(store,client,key);var result=await policy.AuthorizeAsync(envelope,default);
  Assert.Equal(scenario=="valid",result is not null);if(result is not null)Assert.Equal(client.Identity.DeviceId,result.DeviceId);
  if(scenario is "header" or "body" or "no-signature")Assert.Equal(0,client.Calls);
 }
 [Fact] public async Task Legacy_token_with_other_device_binding_is_not_promoted_to_paid_authority()
 {
  var store=new InMemoryCloudStateStore();using var key=Protocol.RuntimePairingCredentialEvidenceTests.Key();var verified=Protocol.RuntimePairingCredentialEvidenceTests.Verify(store);var client=new RuntimeAccessTests.Client();
  var token=store.IssueHubToken("other-device",false,HubTokenCredentialBinding.FromVerification(verified),verified.VerifiedProof!.CreatePairingCredentialEvidence(key));
  var envelope=new ProtocolEnvelope{Headers=new Dictionary<string,string>{{"Authorization","Bearer "+token}}};
  Assert.Null(await new RuntimeProtocolAccessPolicy(store,client,key).AuthorizeAsync(envelope,default));Assert.Equal(0,client.Calls);
 }
 [Fact] public async Task Disabled_profile_preserves_self_hosted_protocol_behavior()
 {
  var store=new InMemoryCloudStateStore();using var key=RuntimePairingCredentialEvidenceKey.Create(new());var envelope=new ProtocolEnvelope();
  Assert.Same(envelope,await new RuntimeProtocolAccessPolicy(store,new UnavailableRuntimeAccessClient(false),key).AuthorizeAsync(envelope,default));
 }
}
