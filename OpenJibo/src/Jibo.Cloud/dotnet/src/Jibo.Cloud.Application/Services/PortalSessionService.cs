using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Jibo.Cloud.Application.Services;

public sealed class PortalSessionService
{
    public const string RobotSessionKind = "robot";
    public const string AccountSessionKind = "account";
    public const string AdministratorSessionKind = "administrator";
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan RobotMergePreviewLifetime = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly byte[] _signingKey;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _revokedTokens =
        new(StringComparer.OrdinalIgnoreCase);

    public PortalSessionService(IConfiguration configuration)
        : this(configuration, TimeProvider.System)
    {
    }

    public PortalSessionService(IConfiguration configuration, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        var configuredSecret = new[]
        {
            configuration["OpenJibo:Portal:SessionSigningKey"],
            Environment.GetEnvironmentVariable("OPENJIBO_PORTAL_SESSION_SIGNING_KEY"),
            configuration["OpenJibo:Portal:StatusPassword"],
            Environment.GetEnvironmentVariable("OPENJIBO_PORTAL_STATUS_PASSWORD")
        }.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        // Unconfigured instances must never accept a token signed with a public development key.
        // Configure a private shared key explicitly when sessions must survive restarts/replicas.
        _signingKey = configuredSecret is null ? RandomNumberGenerator.GetBytes(32)
            : SHA256.HashData(Encoding.UTF8.GetBytes(configuredSecret));
        _timeProvider = timeProvider;
    }

    public PortalSession CreateSession(string deviceId, string friendlyId, string? userId = null,
        string? ownerUserId = null, string? ownershipEpoch = null) =>
        CreateSessionCore(deviceId, friendlyId, userId, RobotSessionKind, userId ?? ownerUserId, ownershipEpoch);

    public PortalSession CreateAccountSession(string email, string userId) =>
        CreateSessionCore("portal-account", email, RequireValue(userId, nameof(userId)), AccountSessionKind, null, null);

    public PortalSession CreateAdministratorSession() =>
        CreateSessionCore("portal-admin", "Portal Admin", null, AdministratorSessionKind, null, null);

    private PortalSession CreateSessionCore(string deviceId, string friendlyId, string? userId,
        string kind, string? ownerUserId, string? ownershipEpoch)
    {
        PurgeRevocations();

        var now = _timeProvider.GetUtcNow();
        var expiresAt = now.Add(SessionLifetime);
        var payload = new SessionTokenPayload(
            deviceId.Trim(),
            friendlyId.Trim(),
            now.ToUnixTimeSeconds(),
            expiresAt.ToUnixTimeSeconds(),
            Guid.NewGuid().ToString("N"),
            string.IsNullOrWhiteSpace(userId) ? null : userId.Trim(),
            kind, 3, string.IsNullOrWhiteSpace(ownerUserId) ? null : ownerUserId.Trim(), ownershipEpoch);

        var token = BuildToken(payload);
        return new PortalSession(token, payload.DeviceId, payload.FriendlyId, expiresAt, payload.UserId,
            payload.Kind!, payload.OwnerUserId, payload.OwnershipEpoch);
    }

    public PortalSession? TryGetSession(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        PurgeRevocations();

        var normalizedToken = token.Trim();
        if (_revokedTokens.ContainsKey(normalizedToken))
            return null;

        if (!TryParseToken(normalizedToken, out var payload))
            return null;

        var now = _timeProvider.GetUtcNow();
        if (payload.ExpiresAtUtc <= now.ToUnixTimeSeconds() || payload.IssuedAtUtc > now.ToUnixTimeSeconds())
            return null;

        return new PortalSession(
            normalizedToken,
            payload.DeviceId,
            payload.FriendlyId,
            DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAtUtc),
            payload.UserId, payload.Kind!, payload.OwnerUserId, payload.OwnershipEpoch);
    }

    public void RevokeSession(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return;

        var normalizedToken = token.Trim();
        if (!TryParseToken(normalizedToken, out var payload))
        {
            _revokedTokens[normalizedToken] = _timeProvider.GetUtcNow().Add(SessionLifetime);
            return;
        }

        _revokedTokens[normalizedToken] = DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAtUtc);
        PurgeRevocations();
    }

    public string CreateRobotMergePreviewToken(
        PortalSession session,
        string sourceDeviceId,
        string targetDeviceId,
        string snapshotFingerprint)
    {
        ArgumentNullException.ThrowIfNull(session);
        var now = _timeProvider.GetUtcNow();
        var payload = new RobotMergePreviewTokenPayload(
            "robot-merge-preview.v1",
            Fingerprint(session.Token),
            RequireValue(sourceDeviceId, nameof(sourceDeviceId)),
            RequireValue(targetDeviceId, nameof(targetDeviceId)),
            RequireValue(snapshotFingerprint, nameof(snapshotFingerprint)),
            now.ToUnixTimeSeconds(),
            now.Add(RobotMergePreviewLifetime).ToUnixTimeSeconds());
        return BuildSignedToken(payload);
    }

    public bool TryValidateRobotMergePreviewToken(
        string? token,
        PortalSession session,
        string sourceDeviceId,
        string targetDeviceId,
        string snapshotFingerprint)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(token) ||
            !TryReadSignedPayload(token.Trim(), out var payloadBytes))
            return false;

        RobotMergePreviewTokenPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<RobotMergePreviewTokenPayload>(payloadBytes, JsonOptions);
        }
        catch
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        return payload is not null &&
               string.Equals(payload.Kind, "robot-merge-preview.v1", StringComparison.Ordinal) &&
               payload.IssuedAtUtc <= now && payload.ExpiresAtUtc > now &&
               string.Equals(payload.SessionFingerprint, Fingerprint(session.Token), StringComparison.Ordinal) &&
               string.Equals(payload.SourceDeviceId, sourceDeviceId.Trim(), StringComparison.OrdinalIgnoreCase) &&
               string.Equals(payload.TargetDeviceId, targetDeviceId.Trim(), StringComparison.OrdinalIgnoreCase) &&
               string.Equals(payload.SnapshotFingerprint, snapshotFingerprint.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildTokenString(string payloadJson, string signature)
    {
        return $"{Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson))}.{signature}";
    }

    private string BuildToken(SessionTokenPayload payload)
    {
        return BuildSignedToken(payload);
    }

    private string BuildSignedToken<TPayload>(TPayload payload)
    {
        var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        var signature = Base64UrlEncode(Hmac(Encoding.UTF8.GetBytes(payloadJson)));
        return BuildTokenString(payloadJson, signature);
    }

    private bool TryParseToken(string token, out SessionTokenPayload payload)
    {
        payload = default!;

        if (!TryReadSignedPayload(token, out var payloadBytes)) return false;

        try
        {
            payload = JsonSerializer.Deserialize<SessionTokenPayload>(payloadBytes, JsonOptions)
                      ?? throw new JsonException();
            return payload.Version == 3 &&
                   payload.Kind is RobotSessionKind or AccountSessionKind or AdministratorSessionKind &&
                   (payload.Kind != AccountSessionKind || !string.IsNullOrWhiteSpace(payload.UserId)) &&
                   !string.IsNullOrWhiteSpace(payload.DeviceId) &&
                   !string.IsNullOrWhiteSpace(payload.FriendlyId);
        }
        catch
        {
            payload = default!;
            return false;
        }
    }

    private bool TryReadSignedPayload(string token, out byte[] payloadBytes)
    {
        payloadBytes = [];
        var parts = token.Split('.', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;

        byte[] signatureBytes;
        try
        {
            payloadBytes = Base64UrlDecode(parts[0]);
            signatureBytes = Base64UrlDecode(parts[1]);
        }
        catch
        {
            payloadBytes = [];
            return false;
        }

        var expectedSignature = Hmac(payloadBytes);
        if (CryptographicOperations.FixedTimeEquals(signatureBytes, expectedSignature)) return true;
        payloadBytes = [];
        return false;
    }

    private byte[] Hmac(byte[] payloadBytes)
    {
        using var hmac = new HMACSHA256(_signingKey);
        return hmac.ComputeHash(payloadBytes);
    }

    private void PurgeRevocations()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var pair in _revokedTokens)
        {
            if (pair.Value > now) continue;
            _revokedTokens.TryRemove(pair.Key, out _);
        }
    }

    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
        return Convert.FromBase64String(padded);
    }

    private sealed record SessionTokenPayload(
        string DeviceId,
        string FriendlyId,
        long IssuedAtUtc,
        long ExpiresAtUtc,
        string Nonce,
        string? UserId,
        string? Kind,
        int Version,
        string? OwnerUserId,
        string? OwnershipEpoch);

    private sealed record RobotMergePreviewTokenPayload(
        string Kind,
        string SessionFingerprint,
        string SourceDeviceId,
        string TargetDeviceId,
        string SnapshotFingerprint,
        long IssuedAtUtc,
        long ExpiresAtUtc);

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string RequireValue(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value is required.", parameterName)
            : value.Trim();

    public sealed record PortalSession(
        string Token,
        string DeviceId,
        string FriendlyId,
        DateTimeOffset ExpiresAtUtc,
        string? UserId = null,
        string Kind = RobotSessionKind,
        string? OwnerUserId = null,
        string? OwnershipEpoch = null);
}
