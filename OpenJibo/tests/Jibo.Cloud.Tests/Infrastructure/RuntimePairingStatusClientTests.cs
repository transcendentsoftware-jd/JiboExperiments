using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Infrastructure.ManagedPairing;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class RuntimePairingStatusClientTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly byte[] Session = SHA256.HashData("live-generation"u8);
    internal static RuntimePairingStatusBinding Binding => new(Guid.Parse("c7100000-0000-0000-0000-000000000001"),
        Guid.Parse("c7200000-0000-0000-0000-000000000001"), 3, Encode(Session), Encode(SHA256.HashData("challenge"u8)), Now.AddMinutes(5));
    private static string Encode(byte[] bytes) => RuntimePairingInitiationIssuer.Encode(bytes);
    private static byte[] Decode(string s) { var n = s.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(n.PadRight(n.Length + (4 - n.Length % 4) % 4, '=')); }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        { Calls++; return send(request, cancellation); }
    }
    private static RuntimePairingInitiationClient Create(ECDsa key, Handler handler, bool enabled = true)
    {
        var options = RuntimePairingInitiationClientTests.Options(key); options.StatusReadsEnabled = enabled;
        return (RuntimePairingInitiationClient)RuntimePairingInitiationClient.Create(options, new Clock(), handler);
    }
    private static async Task<(Dictionary<string, object> Fields, JsonElement Payload, string Assertion)> Fields(HttpRequestMessage request)
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        Assert.Single(body.RootElement.EnumerateObject()); var assertion = body.RootElement.GetProperty("assertion").GetString()!;
        using var json = JsonDocument.Parse(Decode(assertion.Split('.')[1])); var p = json.RootElement.Clone();
        return (new()
        {
            ["pairingId"] = Binding.PairingId, ["deviceKeyId"] = Binding.DeviceKeyId, ["credentialVersion"] = Binding.CredentialVersion,
            ["sessionBinding"] = Binding.SessionBinding, ["requestDigest"] = Encode(SHA256.HashData(Decode(p.GetProperty("jti").GetString()!))),
            ["status"] = "pending", ["expiresAt"] = Binding.ExpiresAt
        }, p, assertion);
    }
    private static HttpResponseMessage Response(Dictionary<string, object> fields, string? raw = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(raw ?? JsonSerializer.Serialize(fields), Encoding.UTF8, "application/json") };
        response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true }; response.Headers.Add("Referrer-Policy", "no-referrer"); return response;
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("authorized")]
    public async Task StatusProofAndResponseAreJoinedToOriginalDescriptorAndFreshQuery(string phase)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); string? prior = null;
        var handler = new Handler(async (request, ct) =>
        {
            Assert.Equal("https://managed.openjibo.test/v1/runtime/pairing-status", request.RequestUri!.AbsoluteUri);
            var (fields, p, assertion) = await Fields(request); var parts = assertion.Split('.');
            using var header = JsonDocument.Parse(Decode(parts[0]));
            Assert.Equal(RuntimePairingInitiationIssuer.StatusType, header.RootElement.GetProperty("typ").GetString());
            Assert.Equal(RuntimePairingInitiationIssuer.StatusPurpose, p.GetProperty("purpose").GetString());
            Assert.True(key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            Assert.Equal(new[] { "aud", "challenge_hash", "credential_binding", "exp", "iat", "iss", "jti", "pairing_id", "purpose", "session_binding", "version" },
                p.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal));
            Assert.Equal(Binding.PairingId.ToString("D"), p.GetProperty("pairing_id").GetString());
            Assert.Equal(Binding.ChallengeHash, p.GetProperty("challenge_hash").GetString());
            Assert.Equal(RuntimePairingInitiationClientTests.Evidence.Binding, p.GetProperty("credential_binding").GetString());
            var nonce = p.GetProperty("jti").GetString(); Assert.NotEqual(prior, nonce); prior = nonce;
            fields["status"] = phase; return Response(fields);
        });
        using var client = Create(key, handler);
        for (var i = 0; i < 2; i++)
        {
            var result = await client.ReadStatusAsync(RuntimePairingInitiationClientTests.Evidence, Session, Binding);
            Assert.Equal(RuntimePairingStatusOutcome.Read, result.Outcome); Assert.Equal(phase, result.Snapshot!.Status);
            Assert.DoesNotContain(Binding.ChallengeHash, result.ToString());
        }
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("pair")][InlineData("key")][InlineData("version")][InlineData("session")][InlineData("nonce")]
    [InlineData("phase")][InlineData("expiry")][InlineData("no-offset")][InlineData("duplicate")][InlineData("unknown")]
    [InlineData("oversized")][InlineData("chunked")][InlineData("no-store")][InlineData("referrer")][InlineData("encoding")]
    public async Task InvalidStatusSnapshotsNeverReachCaller(string mutation)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var handler = new Handler(async (request, ct) =>
        {
            var (fields, _, _) = await Fields(request);
            switch (mutation)
            {
                case "pair": fields["pairingId"] = Guid.NewGuid(); break;
                case "key": fields["deviceKeyId"] = Guid.NewGuid(); break;
                case "version": fields["credentialVersion"] = 4; break;
                case "session": fields["sessionBinding"] = Encode(new byte[32]); break;
                case "nonce": fields["requestDigest"] = Encode(new byte[32]); break;
                case "phase": fields["status"] = "completed"; break;
                case "expiry": fields["expiresAt"] = Binding.ExpiresAt.AddSeconds(1); break;
                case "no-offset": fields["expiresAt"] = Binding.ExpiresAt.UtcDateTime.ToString("s"); break;
                case "unknown": fields["customerId"] = Guid.NewGuid(); break;
            }
            var raw = mutation == "duplicate" ? JsonSerializer.Serialize(fields).TrimEnd('}') + ",\"status\":\"authorized\"}" : mutation == "oversized" ? new string('x', 5000) : null;
            var response = Response(fields, raw);
            if (mutation == "chunked") { response.Content = new UnknownLengthContent(); response.Content.Headers.ContentType = new("application/json"); }
            if (mutation == "no-store") response.Headers.CacheControl = null;
            if (mutation == "referrer") response.Headers.Remove("Referrer-Policy");
            if (mutation == "encoding") response.Content.Headers.ContentEncoding.Add("gzip");
            return response;
        });
        using var client = Create(key, handler);
        var result = await client.ReadStatusAsync(RuntimePairingInitiationClientTests.Evidence, Session, Binding);
        Assert.Equal(RuntimePairingStatusOutcome.Uncertain, result.Outcome); Assert.Null(result.Snapshot); Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(401, RuntimePairingStatusOutcome.Rejected)]
    [InlineData(429, RuntimePairingStatusOutcome.RateLimited)]
    [InlineData(302, RuntimePairingStatusOutcome.Uncertain)]
    [InlineData(503, RuntimePairingStatusOutcome.Uncertain)]
    public async Task FailureResponsesNeverRetry(int status, RuntimePairingStatusOutcome expected)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var handler = new Handler((r, ct) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("") };
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(30)); response.Headers.Location = new("https://other.test/"); return Task.FromResult(response);
        });
        using var client = Create(key, handler);
        Assert.Equal(expected, (await client.ReadStatusAsync(RuntimePairingInitiationClientTests.Evidence, Session, Binding)).Outcome);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task StatusOptInCancellationAndTimeoutAreIndependentOfInitiationReadiness()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); CancellationToken observed = default;
        var handler = new Handler(async (r, ct) => { observed = ct; await Task.Delay(Timeout.InfiniteTimeSpan, ct); throw new InvalidOperationException(); });
        using (var disabled = Create(key, handler, false))
        {
            Assert.True(disabled.IsReady); Assert.False(disabled.IsStatusReady);
            Assert.Equal(RuntimePairingStatusOutcome.Unavailable, (await disabled.ReadStatusAsync(RuntimePairingInitiationClientTests.Evidence, Session, Binding)).Outcome);
            Assert.Equal(0, handler.Calls);
        }
        var waiting = new Handler(async (r, ct) => { observed = ct; await Task.Delay(Timeout.InfiniteTimeSpan, ct); throw new InvalidOperationException(); });
        using var client = Create(key, waiting);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ReadStatusAsync(RuntimePairingInitiationClientTests.Evidence, Session, Binding, cancelled.Token));
        Assert.Equal(0, waiting.Calls);
        Assert.Equal(RuntimePairingStatusOutcome.Uncertain, (await client.ReadStatusAsync(RuntimePairingInitiationClientTests.Evidence, Session, Binding).WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
        Assert.True(observed.IsCancellationRequested); Assert.Equal(1, waiting.Calls);
    }
    private sealed class UnknownLengthContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(new byte[5000]).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
