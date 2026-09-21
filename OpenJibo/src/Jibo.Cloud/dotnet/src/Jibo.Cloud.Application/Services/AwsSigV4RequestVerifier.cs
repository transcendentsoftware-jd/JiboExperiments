using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Domain.Models;

namespace Jibo.Cloud.Application.Services;

public enum AwsSigV4VerificationOutcome
{
    NotPresented,
    Malformed,
    UnknownCredential,
    Expired,
    SignatureMismatch,
    VerifiedCredentialOnly,
    VerifiedPayloadAndTarget
}

public enum AwsSigV4ScopeClassification
{
    NotEvaluated,
    Match,
    Mismatch
}

public enum AwsSigV4TargetClassification
{
    NotEvaluated,
    MatchSigned,
    MatchUnsigned,
    Missing,
    Mismatch
}

public enum AwsSigV4PayloadClassification
{
    NotEvaluated,
    Bound,
    Mismatch
}

public enum AwsSigV4HostClassification
{
    NotEvaluated,
    Match,
    Missing,
    Mismatch
}

public sealed record AwsSigV4OperationPolicy(
    string Operation,
    string Region,
    string Service,
    IReadOnlyList<string> Targets,
    IReadOnlyList<string> Hosts);

/// <summary>
/// Ephemeral proof material emitted only after a legacy SigV4 signature verifies.
/// Raw fields remain private; callers can derive only the environment-keyed replay digest.
/// </summary>
public sealed class AwsSigV4VerifiedProof
{
    private readonly string _accessKeyId;
    private readonly string _credentialDate;
    private readonly string _region;
    private readonly string _service;
    private readonly string _signedTimestamp;
    private readonly byte[] _signature;

    internal AwsSigV4VerifiedProof(
        string accessKeyId,
        string credentialDate,
        string region,
        string service,
        DateTimeOffset signedAt,
        ReadOnlySpan<byte> signature)
    {
        _accessKeyId = accessKeyId;
        _credentialDate = credentialDate;
        _region = region;
        _service = service;
        _signedTimestamp = signedAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        _signature = signature.ToArray();
    }

    public byte[] CreateReplayDigest(AwsSigV4ReplayDigestKey key, string operation) =>
        AwsSigV4ReplayDigestFactory.Create(
            key,
            operation,
            _accessKeyId,
            _credentialDate,
            _region,
            _service,
            _signedTimestamp,
            _signature);

    public override string ToString() => nameof(AwsSigV4VerifiedProof);
}

public sealed record AwsSigV4Verification(
    AwsSigV4VerificationOutcome Outcome,
    string? AccessKeyFingerprint = null,
    DateTimeOffset? SignedAt = null,
    bool PayloadBound = false,
    bool TargetBound = false,
    AwsSigV4ScopeClassification ScopeClassification = AwsSigV4ScopeClassification.NotEvaluated,
    AwsSigV4TargetClassification TargetClassification = AwsSigV4TargetClassification.NotEvaluated,
    AwsSigV4PayloadClassification PayloadClassification = AwsSigV4PayloadClassification.NotEvaluated,
    AwsSigV4HostClassification HostClassification = AwsSigV4HostClassification.NotEvaluated,
    AwsSigV4VerifiedProof? VerifiedProof = null)
{
    public bool CredentialAuthenticated => Outcome is AwsSigV4VerificationOutcome.VerifiedCredentialOnly or
        AwsSigV4VerificationOutcome.VerifiedPayloadAndTarget;

    public bool OperationAuthenticated => CredentialAuthenticated && PayloadBound &&
        ScopeClassification == AwsSigV4ScopeClassification.Match &&
        TargetClassification == AwsSigV4TargetClassification.MatchSigned &&
        HostClassification == AwsSigV4HostClassification.Match;
}

/// <summary>
/// Verifies the legacy AWS Signature Version 4 request format emitted by Jibo's
/// installed jibo-server-client. A valid signature proves possession of the
/// exportable legacy account credential. Payload/target binding is reported
/// separately because captured robot traffic sometimes signs SHA-256(empty)
/// while sending a non-empty JSON body and omits x-amz-target from SignedHeaders.
/// </summary>
public sealed partial class AwsSigV4RequestVerifier(
    ICloudStateStore stateStore,
    TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public AwsSigV4Verification Verify(ProtocolEnvelope envelope, AwsSigV4OperationPolicy? policy = null)
    {
        if (!envelope.Headers.TryGetValue("Authorization", out var authorization) ||
            string.IsNullOrWhiteSpace(authorization))
            return new AwsSigV4Verification(AwsSigV4VerificationOutcome.NotPresented);

        if (!authorization.StartsWith("AWS4-HMAC-SHA256 ", StringComparison.Ordinal))
            return new AwsSigV4Verification(AwsSigV4VerificationOutcome.NotPresented);

        // The observed token-issuance operations this verifier inspects use POST /. ProtocolEnvelope
        // intentionally normalizes query parameters and cannot preserve duplicate raw keys,
        // escaped paths, so fail closed instead of claiming to verify a broader request shape.
        if (!envelope.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
            !envelope.Path.Equals("/", StringComparison.Ordinal) ||
            envelope.QueryParameters.Count != 0)
            return Malformed();

        var match = AuthorizationPattern().Match(authorization);
        if (!match.Success) return Malformed();

        var accessKeyId = match.Groups["accessKey"].Value;
        var credentialDate = match.Groups["date"].Value;
        var region = match.Groups["region"].Value;
        var service = match.Groups["service"].Value;
        var signedHeadersText = match.Groups["headers"].Value;
        var presentedSignature = match.Groups["signature"].Value;
        var fingerprint = CreateAccessKeyFingerprint(accessKeyId);

        var account = stateStore.GetAccount();
        if (string.IsNullOrWhiteSpace(account.AccessKeyId) ||
            string.IsNullOrWhiteSpace(account.SecretAccessKey) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(accessKeyId),
                Encoding.UTF8.GetBytes(account.AccessKeyId)))
            return new AwsSigV4Verification(AwsSigV4VerificationOutcome.UnknownCredential, fingerprint);

        if (!TryReadSignedAt(envelope, credentialDate, out var signedAt))
            return Malformed(fingerprint);

        var skew = (_timeProvider.GetUtcNow() - signedAt).Duration();
        if (skew > MaximumClockSkew)
            return new AwsSigV4Verification(AwsSigV4VerificationOutcome.Expired, fingerprint, signedAt);

        var signedHeaders = signedHeadersText.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (!AreCanonicalSignedHeaders(signedHeaders) ||
            !signedHeaders.Contains("host", StringComparer.Ordinal) ||
            !signedHeaders.Contains("x-amz-date", StringComparer.Ordinal) ||
            !signedHeaders.Contains("x-amz-content-sha256", StringComparer.Ordinal))
            return Malformed(fingerprint, signedAt);

        if (!TryBuildCanonicalHeaders(envelope, signedHeaders, out var canonicalHeaders) ||
            !TryGetHeader(envelope, "x-amz-content-sha256", out var presentedPayloadHash) ||
            !HexSha256Pattern().IsMatch(presentedPayloadHash))
            return Malformed(fingerprint, signedAt);

        var canonicalRequest = string.Join('\n',
            envelope.Method.ToUpperInvariant(),
            CanonicalUri(envelope.Path),
            CanonicalQuery(envelope.QueryParameters),
            canonicalHeaders + "\n",
            signedHeadersText,
            presentedPayloadHash.ToLowerInvariant());
        var scope = $"{credentialDate}/{region}/{service}/aws4_request";
        var stringToSign = string.Join('\n',
            "AWS4-HMAC-SHA256",
            signedAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture),
            scope,
            HexSha256(Encoding.UTF8.GetBytes(canonicalRequest)));

        var signingKey = DeriveSigningKey(account.SecretAccessKey, credentialDate, region, service);
        var expectedSignature = HmacSha256(signingKey, Encoding.UTF8.GetBytes(stringToSign));
        byte[] presentedSignatureBytes;
        try
        {
            presentedSignatureBytes = Convert.FromHexString(presentedSignature);
        }
        catch (FormatException)
        {
            return Malformed(fingerprint, signedAt);
        }

        if (!CryptographicOperations.FixedTimeEquals(expectedSignature, presentedSignatureBytes))
            return new AwsSigV4Verification(AwsSigV4VerificationOutcome.SignatureMismatch, fingerprint, signedAt);

        var actualPayloadHash = HexSha256(envelope.BodyBytes ?? Encoding.UTF8.GetBytes(envelope.BodyText));
        var payloadBound = actualPayloadHash.Equals(presentedPayloadHash, StringComparison.OrdinalIgnoreCase);
        var targetBound = signedHeaders.Contains("x-amz-target", StringComparer.Ordinal);
        var scopeClassification = policy is null
            ? AwsSigV4ScopeClassification.NotEvaluated
            : region.Equals(policy.Region, StringComparison.Ordinal) &&
              service.Equals(policy.Service, StringComparison.Ordinal)
                ? AwsSigV4ScopeClassification.Match
                : AwsSigV4ScopeClassification.Mismatch;
        var targetClassification = ClassifyTarget(envelope, signedHeaders, policy);
        var hostClassification = ClassifyHost(envelope, policy);
        var payloadClassification = payloadBound
            ? AwsSigV4PayloadClassification.Bound
            : AwsSigV4PayloadClassification.Mismatch;
        var fullyBound = payloadBound && targetBound &&
                         (policy is null || scopeClassification == AwsSigV4ScopeClassification.Match &&
                             targetClassification == AwsSigV4TargetClassification.MatchSigned &&
                             hostClassification == AwsSigV4HostClassification.Match);
        return new AwsSigV4Verification(
            fullyBound
                ? AwsSigV4VerificationOutcome.VerifiedPayloadAndTarget
                : AwsSigV4VerificationOutcome.VerifiedCredentialOnly,
            fingerprint,
            signedAt,
            payloadBound,
            targetBound,
            scopeClassification,
            targetClassification,
            payloadClassification,
            hostClassification,
            new AwsSigV4VerifiedProof(
                accessKeyId,
                credentialDate,
                region,
                service,
                signedAt,
                presentedSignatureBytes));
    }

    private static AwsSigV4TargetClassification ClassifyTarget(
        ProtocolEnvelope envelope,
        IReadOnlyList<string> signedHeaders,
        AwsSigV4OperationPolicy? policy)
    {
        if (policy is null) return AwsSigV4TargetClassification.NotEvaluated;
        if (!TryGetHeader(envelope, "x-amz-target", out var target) || string.IsNullOrWhiteSpace(target))
            return AwsSigV4TargetClassification.Missing;
        if (!policy.Targets.Contains(target, StringComparer.Ordinal))
            return AwsSigV4TargetClassification.Mismatch;
        return signedHeaders.Contains("x-amz-target", StringComparer.Ordinal)
            ? AwsSigV4TargetClassification.MatchSigned
            : AwsSigV4TargetClassification.MatchUnsigned;
    }

    private static AwsSigV4HostClassification ClassifyHost(
        ProtocolEnvelope envelope,
        AwsSigV4OperationPolicy? policy)
    {
        if (policy is null) return AwsSigV4HostClassification.NotEvaluated;
        if (!TryGetHeader(envelope, "host", out var host) || string.IsNullOrWhiteSpace(host))
            return AwsSigV4HostClassification.Missing;
        var normalized = host.EndsWith(":443", StringComparison.Ordinal)
            ? host[..^4]
            : host;
        return policy.Hosts.Contains(normalized, StringComparer.OrdinalIgnoreCase)
            ? AwsSigV4HostClassification.Match
            : AwsSigV4HostClassification.Mismatch;
    }

    private static bool TryReadSignedAt(ProtocolEnvelope envelope, string credentialDate,
        out DateTimeOffset signedAt)
    {
        signedAt = default;
        if (!TryGetHeader(envelope, "x-amz-date", out var value) ||
            !DateTimeOffset.TryParseExact(value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out signedAt))
            return false;
        return signedAt.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            .Equals(credentialDate, StringComparison.Ordinal);
    }

    private static bool AreCanonicalSignedHeaders(IReadOnlyList<string> headers)
    {
        if (headers.Count == 0) return false;
        for (var index = 0; index < headers.Count; index++)
        {
            var header = headers[index];
            if (header.Length == 0 || !header.Equals(header.ToLowerInvariant(), StringComparison.Ordinal))
                return false;
            if (index > 0 && string.CompareOrdinal(headers[index - 1], header) >= 0) return false;
        }
        return true;
    }

    private static bool TryBuildCanonicalHeaders(ProtocolEnvelope envelope, IReadOnlyList<string> signedHeaders,
        out string canonicalHeaders)
    {
        var builder = new StringBuilder();
        foreach (var header in signedHeaders)
        {
            if (!TryGetHeader(envelope, header, out var value))
            {
                canonicalHeaders = string.Empty;
                return false;
            }
            if (builder.Length > 0) builder.Append('\n');
            builder.Append(header).Append(':').Append(CollapseWhitespace(value));
        }
        canonicalHeaders = builder.ToString();
        return true;
    }

    private static bool TryGetHeader(ProtocolEnvelope envelope, string name, out string value)
    {
        if (envelope.Headers.TryGetValue(name, out value!)) return true;
        value = string.Empty;
        return false;
    }

    private static string CollapseWhitespace(string value) =>
        WhitespacePattern().Replace(value, " ").Trim();

    private static string CanonicalUri(string path)
    {
        if (string.IsNullOrEmpty(path)) return "/";
        var normalized = path.StartsWith('/') ? path : $"/{path}";
        return AwsPercentEncode(normalized, preserveSlash: true);
    }

    private static string CanonicalQuery(IDictionary<string, string> queryParameters) =>
        string.Join('&', queryParameters
            .Select(pair => new KeyValuePair<string, string>(
                AwsPercentEncode(pair.Key, preserveSlash: false),
                AwsPercentEncode(pair.Value, preserveSlash: false)))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ThenBy(pair => pair.Value, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={pair.Value}"));

    private static string AwsPercentEncode(string value, bool preserveSlash)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var builder = new StringBuilder(bytes.Length);
        foreach (var valueByte in bytes)
        {
            var unreserved = valueByte is >= (byte)'A' and <= (byte)'Z' or
                >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or
                (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~';
            if (unreserved || preserveSlash && valueByte == (byte)'/')
                builder.Append((char)valueByte);
            else
                builder.Append('%').Append(valueByte.ToString("X2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    private static byte[] DeriveSigningKey(string secret, string date, string region, string service)
    {
        var dateKey = HmacSha256(Encoding.UTF8.GetBytes($"AWS4{secret}"), Encoding.UTF8.GetBytes(date));
        var regionKey = HmacSha256(dateKey, Encoding.UTF8.GetBytes(region));
        var serviceKey = HmacSha256(regionKey, Encoding.UTF8.GetBytes(service));
        return HmacSha256(serviceKey, Encoding.UTF8.GetBytes("aws4_request"));
    }

    private static byte[] HmacSha256(byte[] key, byte[] value) => HMACSHA256.HashData(key, value);
    private static string HexSha256(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    public static string CreateAccessKeyFingerprint(string accessKeyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessKeyId);
        return HexSha256(Encoding.UTF8.GetBytes(accessKeyId.Trim()))[..16];
    }

    private static AwsSigV4Verification Malformed(string? fingerprint = null, DateTimeOffset? signedAt = null) =>
        new(AwsSigV4VerificationOutcome.Malformed, fingerprint, signedAt);

    [GeneratedRegex(
        "^AWS4-HMAC-SHA256 Credential=(?<accessKey>[^/\\s,]+)/(?<date>\\d{8})/(?<region>[A-Za-z0-9._-]+)/(?<service>[A-Za-z0-9._-]+)/aws4_request, SignedHeaders=(?<headers>[a-z0-9-]+(?:;[a-z0-9-]+)*), Signature=(?<signature>[0-9a-fA-F]{64})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationPattern();

    [GeneratedRegex("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexSha256Pattern();

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();
}
