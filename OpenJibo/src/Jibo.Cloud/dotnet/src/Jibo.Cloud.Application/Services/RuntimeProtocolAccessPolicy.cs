using System.Security.Cryptography;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Domain.Models;
namespace Jibo.Cloud.Application.Services;
public sealed class RuntimeProtocolAccessPolicy(ICloudStateStore store,IRuntimeAccessClient client,RuntimePairingCredentialEvidenceKey key,
 AwsSigV4RequestVerifier? credentialVerifier=null)
{
 public async Task<ProtocolEnvelope?> AuthorizeAsync(ProtocolEnvelope envelope,CancellationToken cancellation)
 {
  if(!client.Enabled)return envelope;if(!client.IsReady||!key.IsReady)return null;
  string? tokenDeviceId=null;bool bearer=false;RuntimePairingCredentialEvidence? evidence;Func<CancellationToken,Task<bool>> current;
  if(envelope.Headers.TryGetValue("Authorization",out var value)&&value.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase))
  {
   var token=value[7..];var issued=store.FindIssuedToken(token);
   if(issued is null||issued.Token!=token||!HubTokenCredentialBinding.TryRead(issued.Metadata,out var original))return null;
   bearer=true;tokenDeviceId=issued.DeviceId;
   evidence=RuntimePairingCredentialEvidence.Read(issued.Metadata);if(evidence is null)return null;
   var expectedIdentity=client.ResolveIdentity(evidence);if(expectedIdentity is null)return null;
   current=async c=>await store.ReadRuntimeCredentialEvidenceAsync(token,original!,key,expectedIdentity.DeviceId,c)==evidence;
  }
  else
  {
   var verifier=credentialVerifier??new AwsSigV4RequestVerifier(store);var verified=verifier.Verify(envelope);
   evidence=verified.CredentialAuthenticated?verified.VerifiedProof?.CreatePairingCredentialEvidence(key):null;if(evidence is null)return null;
   current=c=>{c.ThrowIfCancellationRequested();var again=verifier.Verify(envelope);return Task.FromResult(again.CredentialAuthenticated&&again.VerifiedProof?.CreatePairingCredentialEvidence(key)==evidence);};
  }
  var identity=client.ResolveIdentity(evidence);if(identity is null||bearer&&tokenDeviceId!=identity.DeviceId)return null;
  if(envelope.DeviceId is {Length:>0} && envelope.DeviceId!=identity.DeviceId)return null;
  var body=envelope.TryParseBody();
  if(body is {ValueKind:System.Text.Json.JsonValueKind.Object})
   foreach(var field in new[]{"deviceId","serial_number","serialNumber","cpuid","cpuId","robotId","friendlyId"})
    if(body.Value.TryGetProperty(field,out var fieldValue)&&fieldValue.ValueKind==System.Text.Json.JsonValueKind.String&&fieldValue.GetString() is {Length:>0} presented&&presented!=identity.DeviceId&&presented!=identity.RobotId.ToString("D"))return null;
  if(!await new RuntimeAccessSession(client,evidence,RandomNumberGenerator.GetBytes(32),envelope.Operation=="CreateHubToken"?"hub-token":"protocol-http",current).CheckAsync(cancellation))return null;
  return new ProtocolEnvelope{RequestId=envelope.RequestId,ReceivedUtc=envelope.ReceivedUtc,Transport=envelope.Transport,Method=envelope.Method,HostName=envelope.HostName,Path=envelope.Path,ServicePrefix=envelope.ServicePrefix,Operation=envelope.Operation,DeviceId=identity.DeviceId,CorrelationId=envelope.CorrelationId,FirmwareVersion=envelope.FirmwareVersion,ApplicationVersion=envelope.ApplicationVersion,BodyText=envelope.BodyText,BodyBytes=envelope.BodyBytes,Headers=envelope.Headers,QueryParameters=envelope.QueryParameters};
 }
}
