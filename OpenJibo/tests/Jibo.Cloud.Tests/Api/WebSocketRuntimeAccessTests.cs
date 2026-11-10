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
 [Theory][InlineData("listen")][InlineData("proactive")][InlineData("notification")]
 public async Task Previously_paid_token_cannot_reconnect_after_access_is_suspended(string channel)
 {
  var service=CreateWebSocketService(out var store);using var key=Protocol.RuntimePairingCredentialEvidenceTests.Key();var verified=Protocol.RuntimePairingCredentialEvidenceTests.Verify(store);
  var client=new RuntimeAccessTests.Client();var original=HubTokenCredentialBinding.FromVerification(verified)!;
  var evidence=verified.VerifiedProof!.CreatePairingCredentialEvidence(key)!;
  store.GetOrCreateDevice(client.Identity.DeviceId,null,null);
  var token=channel=="notification"?store.IssueRobotTokenWithCredentialEvidence(client.Identity.DeviceId,original,evidence):store.IssueHubToken(client.Identity.DeviceId,false,original,evidence);
  using var policy=new HubChannelLeasePolicy(HubChannelLeasePolicyTests.WorkingStore().Object,new byte[32]);
  var coordinator=new WebSocketRequestCoordinator(service,new HomeAssistantWebSocketHandler(new HomeAssistantConnectionRegistry()),new RecordingWebSocketTelemetrySink(),store){LeasePolicy=policy,PairingCredentialKey=key,AccessClient=client};
  var first=new FakeWebSocket(new FakeWebSocketFrame(WebSocketMessageType.Close,[]));await coordinator.HandleAsync(AccessChannelContext(first,token,channel));Assert.True(first.Accepted);
  client.Allowed=false;var next=new FakeWebSocket(new FakeWebSocketFrame(WebSocketMessageType.Close,[]));await coordinator.HandleAsync(AccessChannelContext(next,token,channel));Assert.False(next.Accepted);Assert.Equal(2,client.Calls);Assert.NotEqual(Convert.ToHexString(client.Sessions[0]),Convert.ToHexString(client.Sessions[1]));
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
 [Theory]
 [InlineData("listen","suspended")][InlineData("proactive","suspended")][InlineData("notification","suspended")]
 [InlineData("listen","outage")][InlineData("proactive","outage")][InlineData("notification","outage")]
 [InlineData("listen","not-ready")][InlineData("proactive","not-ready")][InlineData("notification","not-ready")]
 public async Task Every_idle_robot_channel_closes_on_lost_authority_and_requires_fresh_reconnect(string channel,string loss)
 {
  var service=CreateWebSocketService(out var store);
  using var key=Protocol.RuntimePairingCredentialEvidenceTests.Key();
  var verified=Protocol.RuntimePairingCredentialEvidenceTests.Verify(store);
  var original=HubTokenCredentialBinding.FromVerification(verified)!;
  var evidence=verified.VerifiedProof!.CreatePairingCredentialEvidence(key)!;
  var client=new RuntimeAccessTests.Client();
  store.GetOrCreateDevice(client.Identity.DeviceId,null,null);
  var token=channel=="notification"?store.IssueRobotTokenWithCredentialEvidence(client.Identity.DeviceId,original,evidence):store.IssueHubToken(client.Identity.DeviceId,false,original,evidence);
  client.Response=()=>client.Calls==1?DateTimeOffset.UtcNow.AddMilliseconds(150):loss=="outage"?throw new IOException("synthetic authorization outage"):null;
  using var policy=new HubChannelLeasePolicy(HubChannelLeasePolicyTests.WorkingStore().Object,new byte[32]);
  var telemetry=new RecordingWebSocketTelemetrySink { OnOpen=_=>{if(loss=="not-ready")client.IsReady=false;return Task.CompletedTask;} };
  var coordinator=new WebSocketRequestCoordinator(service,new HomeAssistantWebSocketHandler(new HomeAssistantConnectionRegistry()),telemetry,store)
  {LeasePolicy=policy,PairingCredentialKey=key,AccessClient=client};
  var socket=new FakeWebSocket(new FakeWebSocketFrame(WebSocketMessageType.Close,[]))
  {PauseBeforeReceiveNumber=1,ReceiveGate=new(TaskCreationOptions.RunContinuationsAsynchronously)};
  await coordinator.HandleAsync(AccessChannelContext(socket,token,channel)).WaitAsync(TimeSpan.FromSeconds(5));
  Assert.True(socket.Accepted);Assert.Equal(WebSocketState.Aborted,socket.State);Assert.Empty(socket.SentPayloads);
  Assert.Equal(loss=="not-ready"?1:2,client.Calls);
  // Re-enabling the client cannot restore an already aborted connection. A new
  // connection must obtain another decision with its own random session binding.
  client.IsReady=true;client.Response=null;client.Allowed=true;
  var reconnect=new FakeWebSocket(new FakeWebSocketFrame(WebSocketMessageType.Close,[]));
  var previousCalls=client.Calls;
  await coordinator.HandleAsync(AccessChannelContext(reconnect,token,channel));
  Assert.True(reconnect.Accepted);Assert.Equal(previousCalls+1,client.Calls);
  Assert.NotEqual(Convert.ToHexString(client.Sessions[0]),Convert.ToHexString(client.Sessions[^1]));
  Assert.Equal(WebSocketState.Aborted,socket.State);
 }
 private static DefaultHttpContext AccessChannelContext(FakeWebSocket socket,string token,string channel)
 {
  var context=LeaseContext(socket,token,channel=="proactive"?"/v1/proactive":channel=="notification"?"/"+token:"/listen");
  if(channel=="notification")context.Request.Host=new HostString("api-socket.jibo.com");
  return context;
 }
}
