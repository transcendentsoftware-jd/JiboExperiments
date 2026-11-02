using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Infrastructure.ManagedPairing;
using Jibo.Cloud.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class RuntimePairingInitiationClientTests
{
    private static readonly DateTimeOffset Now = new(2026,11,2,20,0,0,TimeSpan.Zero);
    internal static RuntimePairingCredentialEvidence Evidence => RuntimePairingCredentialEvidence.Read(new Dictionary<string,object?>
    { [RuntimePairingCredentialEvidence.VersionKey] = "1", [RuntimePairingCredentialEvidence.BindingKey] = Encode(SHA256.HashData("verified-credential"u8)) })!;
    private static readonly byte[] Session = SHA256.HashData("live-generation"u8);
    internal static RuntimePairingInitiationClientOptions Options(ECDsa key) => new()
    { Enabled = true, ManagedOrigin = "https://managed.openjibo.test", Issuer = "runtime.test", Audience = "managed.test", KeyId = "init-key-1", PrivateKeyPem = key.ExportPkcs8PrivateKeyPem() };
    private static string Encode(byte[] bytes) => RuntimePairingInitiationIssuer.Encode(bytes);
    private static byte[] Decode(string s) { var n=s.Replace('-','+').Replace('_','/'); return Convert.FromBase64String(n.PadRight(n.Length+(4-n.Length%4)%4,'=')); }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)
        { Interlocked.Increment(ref Calls); return send(request,cancellation); }
    }
    private static HttpResponseMessage Response(Dictionary<string,object> fields,string? raw = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(raw ?? JsonSerializer.Serialize(fields),Encoding.UTF8,"application/json") };
        response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true }; response.Headers.Add("Referrer-Policy","no-referrer"); return response;
    }
    private static async Task<(Dictionary<string,object> Fields,JsonElement Payload,string Assertion)> Fields(HttpRequestMessage request)
    {
        using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        Assert.Single(body.RootElement.EnumerateObject()); var assertion=body.RootElement.GetProperty("assertion").GetString()!;
        using var json=JsonDocument.Parse(Decode(assertion.Split('.')[1])); var p=json.RootElement.Clone();
        return (new()
        {
            ["pairingId"] = "b7100000-0000-0000-0000-000000000001", ["deviceKeyId"] = "b7200000-0000-0000-0000-000000000001",
            ["credentialVersion"] = 3, ["pairingHandle"] = new string('a',64), ["robotChallenge"] = new string('b',64),
            ["sessionBinding"] = p.GetProperty("session_binding").GetString()!, ["requestDigest"] = Encode(SHA256.HashData(Decode(p.GetProperty("jti").GetString()!))),
            ["expiresAt"] = Now.AddMinutes(5)
        },p,assertion);
    }

    [Fact]
    public async Task RealSignerAndClientProduceDistinctPurposeExactClaimsAndMatchedDescriptor()
    {
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256); string? previousJti=null;
        var handler=new Handler(async (request,cancellation) =>
        {
            Assert.Equal("https://managed.openjibo.test/v1/runtime/pairing-challenges",request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post,request.Method); Assert.True(request.Headers.CacheControl!.NoStore);
            Assert.Null(request.Headers.Authorization);
            var (fields,p,assertion)=await Fields(request); var parts=assertion.Split('.');
            using var header=JsonDocument.Parse(Decode(parts[0]));
            Assert.Equal(3,header.RootElement.EnumerateObject().Count()); Assert.Equal("ES256",header.RootElement.GetProperty("alg").GetString());
            Assert.Equal(RuntimePairingInitiationIssuer.Type,header.RootElement.GetProperty("typ").GetString());
            Assert.NotEqual(Es256RuntimePairingAttestationIssuer.Type,header.RootElement.GetProperty("typ").GetString());
            Assert.True(key.VerifyData(Encoding.ASCII.GetBytes(parts[0]+"."+parts[1]),Decode(parts[2]),HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            Assert.Equal(9,p.EnumerateObject().Count()); Assert.Equal(RuntimePairingInitiationIssuer.Purpose,p.GetProperty("purpose").GetString());
            Assert.Equal(1,p.GetProperty("version").GetInt32()); Assert.Equal("runtime.test",p.GetProperty("iss").GetString()); Assert.Equal("managed.test",p.GetProperty("aud").GetString());
            Assert.Equal(Evidence.Binding,p.GetProperty("credential_binding").GetString()); Assert.Equal(Encode(Session),p.GetProperty("session_binding").GetString());
            Assert.Equal(Now.ToUnixTimeSeconds(),p.GetProperty("iat").GetInt64()); Assert.Equal(30,p.GetProperty("exp").GetInt64()-p.GetProperty("iat").GetInt64());
            var jti=p.GetProperty("jti").GetString()!; Assert.Equal(32,Decode(jti).Length); Assert.NotEqual(previousJti,jti); previousJti=jti;
            return Response(fields);
        });
        using var client=(RuntimePairingInitiationClient)RuntimePairingInitiationClient.Create(Options(key),new Clock(),handler);
        foreach(var _ in Enumerable.Range(0,2))
        {
            var result=await client.BeginAsync(Evidence,Session); Assert.Equal(RuntimePairingInitiationOutcome.Created,result.Outcome);
            Assert.NotNull(result.Descriptor); Assert.DoesNotContain(new string('a',64),result.ToString()); Assert.DoesNotContain(new string('a',64),result.Descriptor.ToString());
        }
        Assert.Equal(2,handler.Calls);
    }

    [Theory]
    [InlineData("unknown")][InlineData("duplicate")][InlineData("string-version")][InlineData("zero-version")]
    [InlineData("guid")][InlineData("same-secrets")][InlineData("uppercase-hex")][InlineData("wrong-session")][InlineData("wrong-request")]
    [InlineData("expired")][InlineData("future")][InlineData("offset")][InlineData("missing-offset")][InlineData("oversized")][InlineData("encoding")][InlineData("chunked-oversized")]
    [InlineData("no-cache-header")][InlineData("referrer")][InlineData("content-type")][InlineData("changed-origin")]
    public async Task InvalidOrSubstitutedResponsesNeverReleaseDescriptor(string scenario)
    {
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var handler=new Handler(async (request,ct) =>
        {
            var (fields,_,_)=await Fields(request);
            switch(scenario)
            {
                case "unknown": fields["robotId"]="untrusted"; break;
                case "string-version": fields["credentialVersion"]="3"; break;
                case "zero-version": fields["credentialVersion"]=0; break;
                case "guid": fields["pairingId"]=Guid.Empty; break;
                case "same-secrets": fields["robotChallenge"]=fields["pairingHandle"]; break;
                case "uppercase-hex": fields["pairingHandle"]=new string('A',64); break;
                case "wrong-session": fields["sessionBinding"]=Encode(new byte[32]); break;
                case "wrong-request": fields["requestDigest"]=Encode(new byte[32]); break;
                case "expired": fields["expiresAt"]=Now; break;
                case "future": fields["expiresAt"]=Now.AddMinutes(6); break;
                case "offset": fields["expiresAt"]=Now.AddMinutes(5).ToOffset(TimeSpan.FromHours(1)); break;
                case "missing-offset": fields["expiresAt"]="2026-11-02T20:05:00"; break;
            }
            var raw=scenario=="duplicate" ? JsonSerializer.Serialize(fields).TrimEnd('}')+",\"pairingId\":\"b7100000-0000-0000-0000-000000000001\"}" : scenario=="oversized" ? new string('x',5000) : null;
            var response=Response(fields,raw);
            if(scenario=="chunked-oversized") { response.Content=new UnknownLengthContent(Encoding.UTF8.GetBytes(new string('x',5000))); response.Content.Headers.ContentType=new("application/json"); }
            if(scenario=="encoding") response.Content.Headers.ContentEncoding.Add("gzip");
            if(scenario=="no-cache-header") response.Headers.CacheControl=null;
            if(scenario=="referrer") { response.Headers.Remove("Referrer-Policy"); response.Headers.Add("Referrer-Policy","unsafe-url"); }
            if(scenario=="content-type") response.Content.Headers.ContentType=new("text/plain");
            if(scenario=="changed-origin") response.RequestMessage=new(HttpMethod.Post,"https://other.test/v1/runtime/pairing-challenges");
            return response;
        });
        using var client=(RuntimePairingInitiationClient)RuntimePairingInitiationClient.Create(Options(key),new Clock(),handler);
        var result=await client.BeginAsync(Evidence,Session); Assert.Equal(RuntimePairingInitiationOutcome.Uncertain,result.Outcome); Assert.Null(result.Descriptor); Assert.Equal(1,handler.Calls);
    }

    [Theory]
    [InlineData(401,RuntimePairingInitiationOutcome.Rejected)]
    [InlineData(429,RuntimePairingInitiationOutcome.RateLimited)]
    [InlineData(503,RuntimePairingInitiationOutcome.Uncertain)]
    [InlineData(302,RuntimePairingInitiationOutcome.Uncertain)]
    public async Task FailureAndRedirectResponsesHaveNoRetry(int status,RuntimePairingInitiationOutcome expected)
    {
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var handler=new Handler((request,ct) =>
        {
            var r=new HttpResponseMessage((HttpStatusCode)status) { Content=new StringContent("") };
            r.Headers.RetryAfter=new(TimeSpan.FromSeconds(30)); r.Headers.Location=new("https://other.test/redirect"); return Task.FromResult(r);
        });
        using var client=(RuntimePairingInitiationClient)RuntimePairingInitiationClient.Create(Options(key),new Clock(),handler);
        var result=await client.BeginAsync(Evidence,Session); Assert.Equal(expected,result.Outcome); Assert.Null(result.Descriptor); Assert.Equal(1,handler.Calls);
        if(status==429) Assert.Equal(30,result.RetryAfterSeconds);
    }

    [Fact]
    public async Task ProviderWaitIsBoundedAndCancellationOrDisposalCannotDispatch()
    {
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256); CancellationToken observed=default;
        var handler=new Handler(async (r,ct) => { observed=ct; await Task.Delay(Timeout.InfiniteTimeSpan,ct); throw new InvalidOperationException(); });
        using var client=(RuntimePairingInitiationClient)RuntimePairingInitiationClient.Create(Options(key),new Clock(),handler);
        using var cancel=new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.BeginAsync(Evidence,Session,cancel.Token)); Assert.Equal(0,handler.Calls);
        Assert.Equal(RuntimePairingInitiationOutcome.Uncertain,(await client.BeginAsync(Evidence,Session).WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
        Assert.True(observed.IsCancellationRequested); Assert.Equal(1,handler.Calls);
        client.Dispose(); Assert.False(client.IsReady); Assert.Equal(RuntimePairingInitiationOutcome.Unavailable,(await client.BeginAsync(Evidence,Session)).Outcome); Assert.Equal(1,handler.Calls);
    }

    [Theory]
    [InlineData("disabled")][InlineData("http")][InlineData("path")][InlineData("query")][InlineData("fragment")][InlineData("user")]
    [InlineData("backslash")][InlineData("public-key")][InlineData("curve")][InlineData("lifetime")][InlineData("issuer")][InlineData("kid")]
    public void InvalidProfileNeverCreatesReadyClient(string scenario)
    {
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256); using var wrong=ECDsa.Create(ECCurve.NamedCurves.nistP384); var o=Options(key);
        switch(scenario)
        {
            case "disabled": o.Enabled=false; break;
            case "http": o.ManagedOrigin="http://managed.test"; break;
            case "path": o.ManagedOrigin+="/extra"; break;
            case "query": o.ManagedOrigin+="?secret=x"; break;
            case "fragment": o.ManagedOrigin+="#x"; break;
            case "user": o.ManagedOrigin="https://user:password@managed.test"; break;
            case "backslash": o.ManagedOrigin="https://managed.test\\"; break;
            case "public-key": o.PrivateKeyPem=key.ExportSubjectPublicKeyInfoPem(); break;
            case "curve": o.PrivateKeyPem=wrong.ExportPkcs8PrivateKeyPem(); break;
            case "lifetime": o.LifetimeSeconds=61; break;
            case "issuer": o.Issuer="runtime.test "; break;
            case "kid": o.KeyId="bad key"; break;
        }
        Assert.False(RuntimePairingInitiationClient.Create(o,new Clock()).IsReady);
    }

    [Fact]
    public void CompletionConfigurationCannotEnableClientOrExposeRawOptions()
    {
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        { ["OpenJibo:Testing:SkipSttDependencyValidation"]="true", ["OpenJibo:ManagedPairingAttestation:Enabled"]="true" }).Build();
        var services=new ServiceCollection(); services.AddLogging(); services.AddOpenJiboCloud(config);
        using var provider=services.BuildServiceProvider(); Assert.False(provider.GetRequiredService<IRuntimePairingInitiationClient>().IsReady);
        Assert.Null(provider.GetService<RuntimePairingInitiationClientOptions>()); Assert.Null(provider.GetRequiredService<IRuntimePairingSessionAccessor>().Current);
    }

    [Fact]
    public async Task AmbientConnectionScopeIsIsolatedNestedAndRevokedInInheritedBackgroundWork()
    {
        var accessor=new AmbientRuntimePairingSession(); var one=new DummySession(); var two=new DummySession();
        var gate=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); Task<IRuntimePairingSession?> captured;
        using(AmbientRuntimePairingSession.Bind(one))
        {
            Assert.Same(one,accessor.Current);
            using(AmbientRuntimePairingSession.Bind(two)) Assert.Same(two,accessor.Current);
            Assert.Same(one,accessor.Current);
            captured=Task.Run(async () => { await gate.Task; return accessor.Current; });
        }
        Assert.Null(accessor.Current); gate.SetResult(true); Assert.Null(await captured);
        var results=await Task.WhenAll(Enumerable.Range(0,2).Select(async _ =>
        { var session=new DummySession(); using var scope=AmbientRuntimePairingSession.Bind(session); await Task.Yield(); return ReferenceEquals(session,accessor.Current); }));
        Assert.All(results,value => Assert.True(value)); Assert.Null(accessor.Current);
    }
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream,TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length=0; return false; }
    }
    private sealed class DummySession : IRuntimePairingSession
    { public Task<RuntimePairingInitiationResult> BeginAsync(CancellationToken ct=default) => Task.FromResult(new RuntimePairingInitiationResult(RuntimePairingInitiationOutcome.Unavailable)); }
}
