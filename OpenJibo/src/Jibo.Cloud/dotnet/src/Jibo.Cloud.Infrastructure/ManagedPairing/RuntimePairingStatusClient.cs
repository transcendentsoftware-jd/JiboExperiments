using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jibo.Cloud.Application.Services;

namespace Jibo.Cloud.Infrastructure.ManagedPairing;

public sealed partial class RuntimePairingInitiationClient
{
    public async Task<RuntimePairingStatusResult> ReadStatusAsync(RuntimePairingCredentialEvidence credential,
        byte[] sessionBinding, RuntimePairingStatusBinding binding, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!IsStatusReady || sessionBinding.Length != 32) return new(RuntimePairingStatusOutcome.Unavailable);
        var session = sessionBinding.ToArray(); var signed = _issuer.IssueStatus(credential, session, binding);
        if (signed is null) return new(RuntimePairingStatusOutcome.Rejected);
        var endpoint = new Uri(_endpoint, "/v1/runtime/pairing-status");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(RequestTimeout);
        try { return await SendAsync().WaitAsync(RequestTimeout, cancellation); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { deadline.Cancel(); throw; }
        catch (Exception e) when (e is HttpRequestException or IOException or TimeoutException or OperationCanceledException or InvalidOperationException or FormatException)
        { deadline.Cancel(); return new(RuntimePairingStatusOutcome.Uncertain); }

        async Task<RuntimePairingStatusResult> SendAsync()
        {
            deadline.Token.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            request.Content = new StringContent(JsonSerializer.Serialize(new { assertion = signed.Assertion }), Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.RequestMessage?.RequestUri is { } actual && actual != endpoint ||
                response.Content.Headers.ContentLength > 4096 || response.Content.Headers.ContentEncoding.Count != 0)
                return new(RuntimePairingStatusOutcome.Uncertain);
            await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var bytes = new MemoryStream(); var buffer = new byte[512]; int count;
            while ((count = await body.ReadAsync(buffer, deadline.Token)) != 0)
            {
                if (bytes.Length + count > 4096) return new(RuntimePairingStatusOutcome.Uncertain);
                bytes.Write(buffer, 0, count);
            }
            deadline.Token.ThrowIfCancellationRequested();
            if (response.StatusCode == HttpStatusCode.Unauthorized && bytes.Length == 0) return new(RuntimePairingStatusOutcome.Rejected);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && bytes.Length == 0 && response.Headers.RetryAfter?.Delta is { } retry &&
                retry.TotalSeconds is >= 1 and <= 60 && retry.TotalSeconds == Math.Truncate(retry.TotalSeconds))
                return new(RuntimePairingStatusOutcome.RateLimited, RetryAfterSeconds: (int)retry.TotalSeconds);
            if (response.StatusCode != HttpStatusCode.OK || response.Headers.CacheControl?.NoStore != true ||
                !response.Headers.TryGetValues("Referrer-Policy", out var referrers) || !referrers.SequenceEqual(["no-referrer"]) ||
                !string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                return new(RuntimePairingStatusOutcome.Uncertain);
            var snapshot = ReadStatusSnapshot(bytes.ToArray(), binding, signed.RequestDigest, _clock.GetUtcNow());
            deadline.Token.ThrowIfCancellationRequested();
            return snapshot is null ? new(RuntimePairingStatusOutcome.Uncertain) : new(RuntimePairingStatusOutcome.Read, snapshot);
        }
    }

    private static readonly HashSet<string> StatusFields =
        ["pairingId", "deviceKeyId", "credentialVersion", "sessionBinding", "requestDigest", "status", "expiresAt"];
    internal static RuntimePairingStatusSnapshot? ReadStatusSnapshot(byte[] bytes, RuntimePairingStatusBinding expected,
        string digest, DateTimeOffset now)
    {
        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 2 }); var p = json.RootElement;
            if (p.ValueKind != JsonValueKind.Object) return null;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in p.EnumerateObject()) if (!StatusFields.Contains(property.Name) || !names.Add(property.Name)) return null;
            if (!names.SetEquals(StatusFields) || !GuidField(p, "pairingId", out var pairing) || pairing != expected.PairingId ||
                !GuidField(p, "deviceKeyId", out var device) || device != expected.DeviceKeyId ||
                p.GetProperty("credentialVersion").ValueKind != JsonValueKind.Number ||
                !p.GetProperty("credentialVersion").TryGetInt32(out var version) || version != expected.CredentialVersion ||
                !Text(p, "sessionBinding", out var session) || !Same(expected.SessionBinding, session) ||
                !Text(p, "requestDigest", out var request) || !Same(digest, request) ||
                !Text(p, "status", out var phase) || phase is not ("pending" or "authorized") ||
                !UtcExpiry(p, out var expiry) || expiry != expected.ExpiresAt || expiry <= now) return null;
            return new(pairing, device, version, session, request, phase, expiry);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException) { return null; }
    }
}
