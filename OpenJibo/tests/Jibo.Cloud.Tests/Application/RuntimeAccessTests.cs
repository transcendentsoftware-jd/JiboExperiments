using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Infrastructure.ManagedPairing;

namespace Jibo.Cloud.Tests.Application;

public sealed class RuntimeAccessTests
{
 internal static RuntimePairingCredentialEvidence Evidence=>RuntimePairingCredentialEvidence.Read(new Dictionary<string,object?>{{RuntimePairingCredentialEvidence.VersionKey,"1"},{RuntimePairingCredentialEvidence.BindingKey,RuntimeAccessIssuer.Encode(new byte[32])}})!;
 internal sealed class Clock:TimeProvider
 {
  public DateTimeOffset Now=new(2026,11,9,20,0,0,TimeSpan.Zero);public long Ticks;
  public override DateTimeOffset GetUtcNow()=>Now;public override long GetTimestamp()=>Ticks;public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
  public void Advance(int seconds){Now=Now.AddSeconds(seconds);Ticks+=TimeSpan.FromSeconds(seconds).Ticks;}
 }
 internal sealed class Client:IRuntimeAccessClient
 {
  public bool Enabled=>true;public bool IsReady{get;set;}=true;public bool Allowed=true;public int Calls;
  public RuntimeAccessIdentity Identity=new(Guid.Parse("d5100000-0000-0000-0000-000000000001"),"synthetic-paid-device");
  public List<byte[]> Sessions=[];public Func<DateTimeOffset> Now=()=>DateTimeOffset.UtcNow;
  public Func<DateTimeOffset?>? Response;
  public RuntimeAccessIdentity? ResolveIdentity(RuntimePairingCredentialEvidence credential)=>Identity;
  public Task<DateTimeOffset?> AuthorizeAsync(RuntimePairingCredentialEvidence credential,byte[] session,string operation,CancellationToken c)
  {c.ThrowIfCancellationRequested();Calls++;Sessions.Add(session.ToArray());return Task.FromResult(Response is not null?Response():Allowed?(DateTimeOffset?)Now().AddSeconds(4):null);}
 }
 [Fact] public async Task Reconnect_uses_a_new_decision_while_old_positive_grants_expire()
 {
  var clock=new Clock();var client=new Client{Now=clock.GetUtcNow};Task<bool> Authority(CancellationToken c)=>Task.FromResult(true);
  var old=new RuntimeAccessSession(client,Evidence,new byte[32],"hub-listen",Authority,clock);Assert.True(await old.CheckAsync(default));Assert.True(await old.CheckAsync(default));Assert.Equal(1,client.Calls);
  client.Allowed=false;var next=new RuntimeAccessSession(client,Evidence,RandomNumberGenerator.GetBytes(32),"hub-listen",Authority,clock);Assert.False(await next.CheckAsync(default));Assert.Equal(2,client.Calls);
  clock.Advance(5);Assert.False(await old.CheckAsync(default));Assert.Equal(3,client.Calls);client.Allowed=true;Assert.False(await old.CheckAsync(default));Assert.Equal(3,client.Calls);
 }
 [Theory][InlineData("clock-forward")][InlineData("monotonic")][InlineData("late-response")][InlineData("authority-after")][InlineData("outage")]
 public async Task Stale_or_uncertain_authority_never_revives_a_grant(string change)
 {
  var clock=new Clock();var client=new Client{Now=clock.GetUtcNow};var authorityCalls=0;
  var session=new RuntimeAccessSession(client,Evidence,new byte[32],"hub-listen",c=>Task.FromResult(change!="authority-after"||++authorityCalls==1),clock);
  if(change=="late-response"){client.Response=()=>{clock.Advance(6);return clock.Now.AddSeconds(4);};Assert.False(await session.CheckAsync(default));return;}
  if(change=="outage"){client.Response=()=>throw new IOException("synthetic provider failure");Assert.False(await session.CheckAsync(default));return;}
  if(change=="authority-after"){Assert.False(await session.CheckAsync(default));return;}
  Assert.True(await session.CheckAsync(default));client.Allowed=false;
  if(change=="clock-forward")clock.Now=clock.Now.AddMinutes(1);else clock.Ticks+=TimeSpan.FromSeconds(5).Ticks;
  Assert.False(await session.CheckAsync(default));
 }
 [Fact] public async Task Cancellation_before_dispatch_performs_no_authorization()
 {
  var client=new Client();using var c=new CancellationTokenSource();c.Cancel();var session=new RuntimeAccessSession(client,Evidence,new byte[32],"hub-listen",_=>Task.FromResult(true));
  await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>session.CheckAsync(c.Token));Assert.Equal(0,client.Calls);
 }
 private sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> send):HttpMessageHandler
 {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken c)=>send(r);}
 private static byte[] Decode(string s)=>Convert.FromBase64String(s.Replace('-','+').Replace('_','/').PadRight(s.Length+(4-s.Length%4)%4,'='));
 [Theory][InlineData("valid")][InlineData("robot")][InlineData("device")][InlineData("session")][InlineData("request")][InlineData("expiry")][InlineData("operation")][InlineData("extra")][InlineData("cache")][InlineData("redirect")]
 public async Task Real_signed_client_requires_exact_correspondence_and_bounded_response(string change)
 {
  using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);var clock=new Clock();var identity=new Client().Identity;
  var options=new RuntimeAccessClientOptions{Enabled=true,ManagedOrigin="https://access.example.test",Issuer="runtime.test",Audience="managed.test",KeyId="key-1",PrivateKeyPem=key.ExportPkcs8PrivateKeyPem(),Identities=new(){{Evidence.Binding,identity}}};
  var handler=new Handler(async request=>
  {
   Assert.Equal("https://access.example.test/v1/runtime/access-check",request.RequestUri!.AbsoluteUri);
   using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync());var assertion=body.RootElement.GetProperty("assertion").GetString()!;var parts=assertion.Split('.');
   Assert.True(key.VerifyData(Encoding.ASCII.GetBytes(parts[0]+"."+parts[1]),Decode(parts[2]),HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
   using var head=JsonDocument.Parse(Decode(parts[0]));Assert.Equal("openjibo-runtime-access+jwt",head.RootElement.GetProperty("typ").GetString());
   using var payload=JsonDocument.Parse(Decode(parts[1]));var p=payload.RootElement;
   Assert.Equal("openjibo-managed-runtime-access",p.GetProperty("purpose").GetString());Assert.Equal(10,p.EnumerateObject().Count());Assert.False(p.TryGetProperty("robot_id",out _));
   var result=new Dictionary<string,object>{{"robotId",identity.RobotId},{"deviceId",identity.DeviceId},{"credentialBinding",Evidence.Binding},{"sessionBinding",p.GetProperty("session_binding").GetString()!},{"requestDigest",RuntimeAccessIssuer.Encode(SHA256.HashData(Decode(p.GetProperty("jti").GetString()!)))},{"operation","hub-listen"},{"expiresAt",clock.Now.AddSeconds(4)}};
   if(change=="robot")result["robotId"]=Guid.NewGuid();if(change=="device")result["deviceId"]="other-device";if(change=="session")result["sessionBinding"]=RuntimeAccessIssuer.Encode(RandomNumberGenerator.GetBytes(32));
   if(change=="request")result["requestDigest"]=RuntimeAccessIssuer.Encode(RandomNumberGenerator.GetBytes(32));if(change=="expiry")result["expiresAt"]=clock.Now.AddSeconds(6);if(change=="operation")result["operation"]="protocol-http";if(change=="extra")result["allowed"]=true;
   var response=new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(result),Encoding.UTF8,"application/json"),RequestMessage=request};
   response.Headers.CacheControl=new CacheControlHeaderValue{NoStore=change!="cache"};response.Headers.Add("Referrer-Policy","no-referrer");if(change=="redirect")response.RequestMessage=new HttpRequestMessage(HttpMethod.Post,"https://other.example.test/");return response;
  });
  var client=RuntimeAccessClient.Create(options,clock,handler);using var cleanup=(IDisposable)client;
  Assert.Equal(change=="valid",await client.AuthorizeAsync(Evidence,new byte[32],"hub-listen",default)is not null);
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void Invalid_configuration_preserves_enabled_fail_closed_state(bool enabled)
 {var client=RuntimeAccessClient.Create(new(){Enabled=enabled,ManagedOrigin="http://insecure.test"},TimeProvider.System);Assert.Equal(enabled,client.Enabled);Assert.False(client.IsReady);}
}
