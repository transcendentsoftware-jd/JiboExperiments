using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jibo.Cloud.Application.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jibo.Cloud.Infrastructure.ManagedPairing;

public sealed class RuntimePairingInitiationClient : IRuntimePairingInitiationClient, IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);
    private readonly RuntimePairingInitiationIssuer _issuer;
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly TimeProvider _clock;
    private int _disposed;
    public bool IsReady => Volatile.Read(ref _disposed) == 0;
    private RuntimePairingInitiationClient(RuntimePairingInitiationIssuer issuer, HttpMessageHandler handler, Uri endpoint, TimeProvider clock)
    { _issuer = issuer; _http = new(handler) { Timeout = Timeout.InfiniteTimeSpan }; _endpoint = endpoint; _clock = clock; }
    public static IRuntimePairingInitiationClient Create(RuntimePairingInitiationClientOptions options, TimeProvider clock) =>
        Create(options, clock, null);
    internal static IRuntimePairingInitiationClient Create(RuntimePairingInitiationClientOptions options, TimeProvider clock, HttpMessageHandler? testHandler)
    {
        if (!options.Enabled || !TryOrigin(options.ManagedOrigin, out var origin) ||
            !RuntimePairingInitiationIssuer.TryCreate(options,clock,out var issuer)) return RejectingRuntimePairingInitiationClient.Instance;
        // No HTTP factory logging/retry handlers, cookies, redirects, custom trust or credential fallback.
        var handler = testHandler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false,
            UseDefaultCredentials = false, CheckCertificateRevocationList = true, AutomaticDecompression = DecompressionMethods.None };
        return new RuntimePairingInitiationClient(issuer!,handler,new Uri(origin!,"/v1/runtime/pairing-challenges"),clock);
    }
    internal static bool TryOrigin(string? value, out Uri? origin)
    {
        origin = null;
        return value is not null && value.Length is > 0 and <= 2048 && value == value.Trim() && !value.Any(char.IsControl) && !value.Contains('\\') &&
            Uri.TryCreate(value,UriKind.Absolute,out origin) && origin.Scheme == "https" && origin.UserInfo == "" &&
            origin.AbsolutePath == "/" && origin.Query == "" && origin.Fragment == "" && origin.Host.Length > 0;
    }
    public async Task<RuntimePairingInitiationResult> BeginAsync(RuntimePairingCredentialEvidence credential, byte[] sessionBinding,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!IsReady || sessionBinding.Length != 32) return new(RuntimePairingInitiationOutcome.Unavailable);
        // Freeze caller-owned bytes before any await.
        var session = sessionBinding.ToArray(); var signed = _issuer.Issue(credential,session);
        if (signed is null) return new(RuntimePairingInitiationOutcome.Unavailable);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(RequestTimeout);
        try { return await SendAsync().WaitAsync(RequestTimeout,cancellation); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { deadline.Cancel(); throw; }
        catch (Exception e) when (e is HttpRequestException or IOException or TimeoutException or OperationCanceledException or InvalidOperationException or FormatException)
        { deadline.Cancel(); return new(RuntimePairingInitiationOutcome.Uncertain); }

        async Task<RuntimePairingInitiationResult> SendAsync()
        {
            deadline.Token.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Post,_endpoint);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            request.Content = new StringContent(JsonSerializer.Serialize(new { assertion = signed.Assertion }),Encoding.UTF8,"application/json");
            using var response = await _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token);
            if (response.RequestMessage?.RequestUri is { } actual && actual != _endpoint) return new(RuntimePairingInitiationOutcome.Uncertain);
            if (response.Content.Headers.ContentLength > 4096 || response.Content.Headers.ContentEncoding.Count != 0) return new(RuntimePairingInitiationOutcome.Uncertain);
            await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var bytes = new MemoryStream(); var buffer = new byte[512]; int count;
            while ((count = await body.ReadAsync(buffer,deadline.Token)) != 0)
            {
                if (bytes.Length + count > 4096) return new(RuntimePairingInitiationOutcome.Uncertain);
                bytes.Write(buffer,0,count);
            }
            deadline.Token.ThrowIfCancellationRequested();
            if (response.StatusCode == HttpStatusCode.Unauthorized && bytes.Length == 0) return new(RuntimePairingInitiationOutcome.Rejected);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && bytes.Length == 0 &&
                response.Headers.RetryAfter?.Delta is { } retry && retry.TotalSeconds is >= 1 and <= 600 && retry.TotalSeconds == Math.Truncate(retry.TotalSeconds))
                return new(RuntimePairingInitiationOutcome.RateLimited,RetryAfterSeconds:(int)retry.TotalSeconds);
            if (response.StatusCode != HttpStatusCode.Created || response.Headers.CacheControl?.NoStore != true ||
                !response.Headers.TryGetValues("Referrer-Policy",out var referrers) || !referrers.SequenceEqual(["no-referrer"]) ||
                !string.Equals(response.Content.Headers.ContentType?.MediaType,"application/json",StringComparison.OrdinalIgnoreCase))
                return new(RuntimePairingInitiationOutcome.Uncertain);
            var descriptor = ReadDescriptor(bytes.ToArray(),RuntimePairingInitiationIssuer.Encode(session),signed.RequestDigest,_clock.GetUtcNow());
            deadline.Token.ThrowIfCancellationRequested();
            return descriptor is null ? new(RuntimePairingInitiationOutcome.Uncertain) : new(RuntimePairingInitiationOutcome.Created,descriptor);
        }
    }
    private static readonly HashSet<string> Fields = ["pairingId","deviceKeyId","credentialVersion","pairingHandle","robotChallenge","sessionBinding","requestDigest","expiresAt"];
    internal static RuntimePairingDescriptor? ReadDescriptor(byte[] bytes,string session,string digest,DateTimeOffset now)
    {
        try
        {
            using var json = JsonDocument.Parse(bytes,new JsonDocumentOptions { MaxDepth = 2 }); var p = json.RootElement;
            if (p.ValueKind != JsonValueKind.Object) return null;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in p.EnumerateObject()) if (!Fields.Contains(property.Name) || !names.Add(property.Name)) return null;
            if (!names.SetEquals(Fields) || !GuidField(p,"pairingId",out var pairing) || !GuidField(p,"deviceKeyId",out var key) ||
                p.GetProperty("credentialVersion").ValueKind != JsonValueKind.Number || !p.GetProperty("credentialVersion").TryGetInt32(out var version) || version < 1 ||
                !Text(p,"pairingHandle",out var handle) || !Hex(handle) || !Text(p,"robotChallenge",out var challenge) || !Hex(challenge) || handle == challenge ||
                !Text(p,"sessionBinding",out var observedSession) || !Same(session,observedSession) ||
                !Text(p,"requestDigest",out var observedDigest) || !Same(digest,observedDigest) ||
                !UtcExpiry(p,out var expiry) || expiry <= now || expiry > now.AddMinutes(5).AddSeconds(30)) return null;
            return new(pairing,key,version,handle,challenge,observedSession,observedDigest,expiry);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException) { return null; }
    }
    private static bool Text(JsonElement p,string name,out string s)
    { var value = p.GetProperty(name); s = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : ""; return s.Length is > 0 and <= 128; }
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
    public override string ToString() => "RuntimePairingInitiationClient { redacted }";
    public void Dispose() { if (Interlocked.Exchange(ref _disposed,1) == 0) { _http.Dispose(); _issuer.Dispose(); } }
}

internal static class RuntimePairingInitiationClientRegistration
{
    internal static void Add(IServiceCollection services,IConfiguration? configuration)
    {
        var options = new RuntimePairingInitiationClientOptions();
        configuration?.GetSection(RuntimePairingInitiationClientOptions.SectionName).Bind(options);
        var client = RuntimePairingInitiationClient.Create(options,TimeProvider.System);
        options.PrivateKeyPem = "";
        services.AddSingleton<IRuntimePairingInitiationClient>(_ => client);
        services.AddSingleton<IRuntimePairingSessionAccessor,AmbientRuntimePairingSession>();
    }
}
