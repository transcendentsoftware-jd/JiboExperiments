using System.Net.WebSockets;
using Jibo.Cloud.Api.Hosting;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Tests.Application;
using Microsoft.AspNetCore.Http;

namespace Jibo.Cloud.Tests.Api;

public sealed partial class WebSocketRequestCoordinatorTests
{
 [Theory][InlineData("paid")][InlineData("proactive")][InlineData("unpaid")][InlineData("wrong-device")][InlineData("old-token")][InlineData("no-lease")][InlineData("unavailable")]
 public async Task Paid_profile_requires_current_full_evidence_and_explicit_identity_before_accept(string scenario)
 {
  var service=CreateWebSocketService(out var store);using var key=Protocol.RuntimePairingCredentialEvidenceTests.Key();
  var verified=Protocol.RuntimePairingCredentialEvidenceTests.Verify(store);var original=HubTokenCredentialBinding.FromVerification(verified)!;var evidence=verified.VerifiedProof!.CreatePairingCredentialEvidence(key)!;
  var client=new RuntimeAccessTests.Client{Allowed=scenario!="unpaid",IsReady=scenario!="unavailable"};
  var token=store.IssueHubToken(scenario=="wrong-device"?"other-device":client.Identity.DeviceId,false,original,scenario=="old-token"?null:evidence);
  using var policy=scenario=="no-lease"?HubChannelLeasePolicy.Disabled:new HubChannelLeasePolicy(HubChannelLeasePolicyTests.WorkingStore().Object,new byte[32]);
  var socket=new FakeWebSocket(new FakeWebSocketFrame(WebSocketMessageType.Close,[]));var context=LeaseContext(socket,token,scenario=="proactive"?"/v1/proactive":"/listen");
  var coordinator=new WebSocketRequestCoordinator(service,new HomeAssistantWebSocketHandler(new HomeAssistantConnectionRegistry()),new RecordingWebSocketTelemetrySink(),store){LeasePolicy=policy,PairingCredentialKey=key,AccessClient=client};
  await coordinator.HandleAsync(context);Assert.Equal(scenario is "paid" or "proactive",socket.Accepted);
  if(!socket.Accepted)Assert.Contains(context.Response.StatusCode,new[]{401,403,503});
 }
 [Fact] public async Task Previously_paid_token_cannot_reconnect_after_access_is_suspended()
 {
  var service=CreateWebSocketService(out var store);using var key=Protocol.RuntimePairingCredentialEvidenceTests.Key();var verified=Protocol.RuntimePairingCredentialEvidenceTests.Verify(store);
  var client=new RuntimeAccessTests.Client();var token=store.IssueHubToken(client.Identity.DeviceId,false,HubTokenCredentialBinding.FromVerification(verified),verified.VerifiedProof!.CreatePairingCredentialEvidence(key));
  using var policy=new HubChannelLeasePolicy(HubChannelLeasePolicyTests.WorkingStore().Object,new byte[32]);
  var coordinator=new WebSocketRequestCoordinator(service,new HomeAssistantWebSocketHandler(new HomeAssistantConnectionRegistry()),new RecordingWebSocketTelemetrySink(),store){LeasePolicy=policy,PairingCredentialKey=key,AccessClient=client};
  var first=new FakeWebSocket(new FakeWebSocketFrame(WebSocketMessageType.Close,[]));await coordinator.HandleAsync(LeaseContext(first,token,"/listen"));Assert.True(first.Accepted);
  client.Allowed=false;var next=new FakeWebSocket(new FakeWebSocketFrame(WebSocketMessageType.Close,[]));await coordinator.HandleAsync(LeaseContext(next,token,"/listen"));Assert.False(next.Accepted);Assert.Equal(2,client.Calls);Assert.NotEqual(Convert.ToHexString(client.Sessions[0]),Convert.ToHexString(client.Sessions[1]));
 }
 [Fact] public async Task Credential_bound_notification_socket_uses_its_own_fresh_access_decision()
 {
  var service=CreateWebSocketService(out var store);using var key=Protocol.RuntimePairingCredentialEvidenceTests.Key();var verified=Protocol.RuntimePairingCredentialEvidenceTests.Verify(store);var original=HubTokenCredentialBinding.FromVerification(verified)!;var evidence=verified.VerifiedProof!.CreatePairingCredentialEvidence(key)!;
  var client=new RuntimeAccessTests.Client();store.GetOrCreateDevice(client.Identity.DeviceId,null,null);var token=store.IssueRobotTokenWithCredentialEvidence(client.Identity.DeviceId,original,evidence);
  var socket=new FakeWebSocket(new FakeWebSocketFrame(WebSocketMessageType.Close,[]));var context=LeaseContext(socket,token,"/"+token);context.Request.Host=new HostString("api-socket.jibo.com");
  var coordinator=new WebSocketRequestCoordinator(service,new HomeAssistantWebSocketHandler(new HomeAssistantConnectionRegistry()),new RecordingWebSocketTelemetrySink(),store){PairingCredentialKey=key,AccessClient=client};
  await coordinator.HandleAsync(context);Assert.True(socket.Accepted);Assert.Equal(1,client.Calls);
 }
 [Theory][InlineData(false)][InlineData(true)]
 public async Task Every_send_overload_checks_the_connection_guard(bool memory)
 {
  var socket=new FakeWebSocket();using var guarded=new BorrowedAuthorizedWebSocket(socket,_=>throw new OperationCanceledException());
  if(memory)await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await guarded.SendAsync(new ReadOnlyMemory<byte>(new byte[]{1}),WebSocketMessageType.Binary,true,default));
  else await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>guarded.SendAsync(new ArraySegment<byte>(new byte[]{1}),WebSocketMessageType.Binary,true,default));
  Assert.Empty(socket.SentPayloads);
 }
 [Fact] public async Task Expiring_paid_grant_cancels_an_already_open_idle_socket()
 {
  var service=CreateWebSocketService(out var store);using var key=Protocol.RuntimePairingCredentialEvidenceTests.Key();var verified=Protocol.RuntimePairingCredentialEvidenceTests.Verify(store);
  var client=new RuntimeAccessTests.Client();client.Response=()=>client.Calls==1?DateTimeOffset.UtcNow.AddMilliseconds(150):null;
  var token=store.IssueHubToken(client.Identity.DeviceId,false,HubTokenCredentialBinding.FromVerification(verified),verified.VerifiedProof!.CreatePairingCredentialEvidence(key));
  using var policy=new HubChannelLeasePolicy(HubChannelLeasePolicyTests.WorkingStore().Object,new byte[32]);
  var socket=new FakeWebSocket(new FakeWebSocketFrame(WebSocketMessageType.Close,[])){PauseBeforeReceiveNumber=1,ReceiveGate=new(TaskCreationOptions.RunContinuationsAsynchronously)};
  var coordinator=new WebSocketRequestCoordinator(service,new HomeAssistantWebSocketHandler(new HomeAssistantConnectionRegistry()),new RecordingWebSocketTelemetrySink(),store){LeasePolicy=policy,PairingCredentialKey=key,AccessClient=client};
  await coordinator.HandleAsync(LeaseContext(socket,token,"/listen")).WaitAsync(TimeSpan.FromSeconds(5));
  Assert.True(socket.Accepted);Assert.Equal(WebSocketState.Aborted,socket.State);Assert.Empty(socket.SentPayloads);Assert.Null(store.FindActiveSessionByToken(token));Assert.Equal(2,client.Calls);
 }
}
