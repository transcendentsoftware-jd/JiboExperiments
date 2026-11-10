using System.Net;
using System.Security.Cryptography;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jibo.Cloud.Application.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
namespace Jibo.Cloud.Infrastructure.ManagedPairing;
public sealed class RuntimeAccessClient:IRuntimeAccessClient,IDisposable
{
 private readonly RuntimeAccessIssuer signer;private readonly HttpClient http;private readonly Uri endpoint;private readonly TimeProvider clock;
 private readonly IReadOnlyDictionary<string,RuntimeAccessIdentity> identities;
 private int disposed;public bool Enabled=>true;public bool IsReady=>Volatile.Read(ref disposed)==0;
 private RuntimeAccessClient(RuntimeAccessIssuer signer,HttpMessageHandler handler,Uri endpoint,TimeProvider clock,IReadOnlyDictionary<string,RuntimeAccessIdentity> identities){this.signer=signer;http=new(handler){Timeout=Timeout.InfiniteTimeSpan};this.endpoint=endpoint;this.clock=clock;this.identities=identities;}
 public static IRuntimeAccessClient Create(RuntimeAccessClientOptions options,TimeProvider clock)=>Create(options,clock,null);
 internal static IRuntimeAccessClient Create(RuntimeAccessClientOptions options,TimeProvider clock,HttpMessageHandler? handler)
 {
  if(options.Identities.Count>1000||options.Identities.Any(p=>p.Key.Length!=43||RuntimePairingCredentialEvidence.Read(new Dictionary<string,object?>{{RuntimePairingCredentialEvidence.VersionKey,"1"},{RuntimePairingCredentialEvidence.BindingKey,p.Key}}) is null||p.Value.RobotId==Guid.Empty||string.IsNullOrWhiteSpace(p.Value.DeviceId)||p.Value.DeviceId!=p.Value.DeviceId.Trim()||p.Value.DeviceId.Length>200||p.Value.DeviceId.Any(char.IsControl)))return new UnavailableRuntimeAccessClient(options.Enabled);
  if(!options.Enabled)return new UnavailableRuntimeAccessClient(false);
  if(!RuntimePairingInitiationClient.TryOrigin(options.ManagedOrigin,out var origin)||!RuntimeAccessIssuer.TryCreate(options,clock,out var signer))return new UnavailableRuntimeAccessClient(true);
  handler??=new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false,UseDefaultCredentials=false,CheckCertificateRevocationList=true,AutomaticDecompression=DecompressionMethods.None};
  return new RuntimeAccessClient(signer!,handler,new Uri(origin!,"/v1/runtime/access-check"),clock,new Dictionary<string,RuntimeAccessIdentity>(options.Identities,StringComparer.Ordinal));
 }
 public RuntimeAccessIdentity? ResolveIdentity(RuntimePairingCredentialEvidence credential)=>identities.GetValueOrDefault(credential.Binding);
 public async Task<DateTimeOffset?> AuthorizeAsync(RuntimePairingCredentialEvidence credential,byte[] session,string operation,CancellationToken cancellation)
 {
  cancellation.ThrowIfCancellationRequested();if(!IsReady||ResolveIdentity(credential) is not {} identity)return null;var proof=signer.Issue(credential,session,operation);if(proof is null)return null;
  using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(2));
  try{return await Send().WaitAsync(TimeSpan.FromSeconds(2),cancellation);}
  catch(OperationCanceledException)when(cancellation.IsCancellationRequested){deadline.Cancel();throw;}
  catch(Exception){deadline.Cancel();return null;}
  async Task<DateTimeOffset?> Send()
  {
   using var request=new HttpRequestMessage(HttpMethod.Post,endpoint);request.Headers.CacheControl=new CacheControlHeaderValue{NoStore=true};
   request.Content=new StringContent(JsonSerializer.Serialize(new{assertion=proof.Assertion}),Encoding.UTF8,"application/json");
   using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token);
   if(response.RequestMessage?.RequestUri is{} actual&&actual!=endpoint||response.Content.Headers.ContentLength>4096||response.Content.Headers.ContentEncoding.Count!=0)return null;
   await using var body=await response.Content.ReadAsStreamAsync(deadline.Token);using var bytes=new MemoryStream();var buffer=new byte[512];int count;
   while((count=await body.ReadAsync(buffer,deadline.Token))!=0){if(bytes.Length+count>4096)return null;bytes.Write(buffer,0,count);}
   deadline.Token.ThrowIfCancellationRequested();
   if(response.StatusCode!=HttpStatusCode.OK||response.Headers.CacheControl?.NoStore!=true||!response.Headers.TryGetValues("Referrer-Policy",out var policy)||!policy.SequenceEqual(["no-referrer"])||response.Content.Headers.ContentType?.MediaType!="application/json")return null;
   return Read(bytes.ToArray(),credential.Binding,RuntimeAccessIssuer.Encode(session),proof.RequestDigest,operation,clock.GetUtcNow(),identity.RobotId,identity.DeviceId);
  }
 }
 internal static DateTimeOffset? Read(byte[] bytes,string credential,string session,string digest,string operation,DateTimeOffset now,Guid robot,string device)
 {
  try
  {
   using var doc=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=2});var p=doc.RootElement;
   var names=new HashSet<string>(StringComparer.Ordinal);if(p.ValueKind!=JsonValueKind.Object)return null;
   foreach(var property in p.EnumerateObject())if(!names.Add(property.Name))return null;
   if(!names.SetEquals(["robotId","deviceId","credentialBinding","sessionBinding","requestDigest","operation","expiresAt"])||
    !GuidField(p,"robotId",out var observedRobot)||observedRobot!=robot||!Text(p,"deviceId",out var observedDevice)||observedDevice!=device||!Text(p,"credentialBinding",out var c)||!Same(credential,c)||!Text(p,"sessionBinding",out var s)||!Same(session,s)||
    !Text(p,"requestDigest",out var d)||!Same(digest,d)||!Text(p,"operation",out var o)||o!=operation||!UtcExpiry(p,out var expiry)||expiry<=now||expiry>now.AddSeconds(5))return null;
   return expiry;
  }
  catch(Exception e)when(e is JsonException or InvalidOperationException or FormatException){return null;}
 }
    private static bool Text(JsonElement p,string name,out string s)
    { var value = p.GetProperty(name); s = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : ""; return s.Length is > 0 and <= 200; }
    private static bool UtcExpiry(JsonElement p,out DateTimeOffset expiry)
    {
        expiry = default;
        return Text(p,"expiresAt",out var text) && (text.EndsWith("Z",StringComparison.Ordinal) || text.EndsWith("+00:00",StringComparison.Ordinal)) &&
            p.GetProperty("expiresAt").TryGetDateTimeOffset(out expiry) && expiry.Offset == TimeSpan.Zero;
    }
    private static bool GuidField(JsonElement p,string name,out Guid id)
    { id = default; return Text(p,name,out var s) && Guid.TryParseExact(s,"D",out id) && id != Guid.Empty && s == id.ToString("D"); }
    private static bool Hex(string s) => s.Length == 64 && s.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
    private static bool Same(string expected,string observed) => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected),Encoding.ASCII.GetBytes(observed));

 public void Dispose(){if(Interlocked.Exchange(ref disposed,1)==0){http.Dispose();signer.Dispose();}}
}
internal static class RuntimeAccessClientRegistration
{
 internal static void Add(IServiceCollection services,IConfiguration? configuration)
 {
  var options=new RuntimeAccessClientOptions();configuration?.GetSection(RuntimeAccessClientOptions.SectionName).Bind(options);
  var client=RuntimeAccessClient.Create(options,TimeProvider.System);options.PrivateKeyPem="";
  services.AddSingleton<IRuntimeAccessClient>(_=>client);services.AddSingleton<RuntimeProtocolAccessPolicy>();
 }
}
