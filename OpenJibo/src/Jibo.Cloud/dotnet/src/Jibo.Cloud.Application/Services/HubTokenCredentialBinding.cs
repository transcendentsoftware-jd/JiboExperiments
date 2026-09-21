using System.Globalization;
using System.Text.Json;

namespace Jibo.Cloud.Application.Services;

/// <summary>
/// Bounded, non-secret evidence that a Hub token was issued after a legacy
/// account credential verified. This is credential continuity only: it does
/// not authenticate a request body, identify physical hardware, or assign a
/// robot.
/// </summary>
public sealed record HubTokenCredentialBinding
{
    public const string VersionMetadataKey = "legacyCredentialBindingVersion";
    public const string FingerprintMetadataKey = "legacyCredentialFingerprint";
    public const string EpochMetadataKey = "legacyCredentialEpoch";
    public const string SignedAtMetadataKey = "legacyCredentialSignedAtUtc";
    public const string OperationAuthenticatedMetadataKey = "legacyCredentialOperationAuthenticated";

    public HubTokenCredentialBinding(
        string credentialFingerprint,
        long credentialEpoch,
        DateTimeOffset signedAt,
        bool operationAuthenticated)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialFingerprint);
        if (credentialFingerprint.Length != 16 ||
            credentialFingerprint.Any(character => !char.IsAsciiHexDigit(character)) ||
            !string.Equals(credentialFingerprint, credentialFingerprint.ToLowerInvariant(), StringComparison.Ordinal))
            throw new ArgumentException("Credential fingerprint must be 16 lowercase hexadecimal characters.",
                nameof(credentialFingerprint));
        if (credentialEpoch < 1)
            throw new ArgumentOutOfRangeException(nameof(credentialEpoch));

        CredentialFingerprint = credentialFingerprint;
        CredentialEpoch = credentialEpoch;
        SignedAt = signedAt.ToUniversalTime();
        OperationAuthenticated = operationAuthenticated;
    }

    public string CredentialFingerprint { get; }
    public long CredentialEpoch { get; }
    public DateTimeOffset SignedAt { get; }
    public bool OperationAuthenticated { get; }

    public static HubTokenCredentialBinding? FromVerification(
        AwsSigV4Verification verification,
        long credentialEpoch) =>
        verification.CredentialAuthenticated &&
        verification.AccessKeyFingerprint is { Length: > 0 } fingerprint &&
        verification.SignedAt is { } signedAt
            ? new HubTokenCredentialBinding(
                fingerprint,
                credentialEpoch,
                signedAt,
                verification.OperationAuthenticated)
            : null;

    public void WriteTo(IDictionary<string, object?> metadata)
    {
        metadata[VersionMetadataKey] = "1";
        metadata[FingerprintMetadataKey] = CredentialFingerprint;
        metadata[EpochMetadataKey] = CredentialEpoch.ToString(CultureInfo.InvariantCulture);
        metadata[SignedAtMetadataKey] = SignedAt.ToString("O", CultureInfo.InvariantCulture);
        metadata[OperationAuthenticatedMetadataKey] = OperationAuthenticated ? "true" : "false";
    }

    public static bool TryRead(
        IEnumerable<KeyValuePair<string, object?>> metadata,
        out HubTokenCredentialBinding? binding)
    {
        binding = null;
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in metadata)
            if (!values.TryAdd(pair.Key, ReadText(pair.Value)))
                return false;
        if (!values.TryGetValue(VersionMetadataKey, out var version) || version != "1" ||
            !values.TryGetValue(FingerprintMetadataKey, out var fingerprint) ||
            !values.TryGetValue(EpochMetadataKey, out var epochText) ||
            !long.TryParse(epochText, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch) ||
            !values.TryGetValue(SignedAtMetadataKey, out var signedAtText) ||
            !DateTimeOffset.TryParseExact(signedAtText, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var signedAt) ||
            signedAt.Offset != TimeSpan.Zero ||
            !values.TryGetValue(OperationAuthenticatedMetadataKey, out var operationText) ||
            operationText is not ("true" or "false"))
            return false;

        var operationAuthenticated = operationText == "true";

        try
        {
            binding = new HubTokenCredentialBinding(fingerprint ?? string.Empty, epoch, signedAt,
                operationAuthenticated);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool ContainsMetadata(IEnumerable<KeyValuePair<string, object?>> metadata) =>
        metadata.Any(pair =>
            pair.Key.Equals(VersionMetadataKey, StringComparison.OrdinalIgnoreCase) ||
            pair.Key.Equals(FingerprintMetadataKey, StringComparison.OrdinalIgnoreCase) ||
            pair.Key.Equals(EpochMetadataKey, StringComparison.OrdinalIgnoreCase) ||
            pair.Key.Equals(SignedAtMetadataKey, StringComparison.OrdinalIgnoreCase) ||
            pair.Key.Equals(OperationAuthenticatedMetadataKey, StringComparison.OrdinalIgnoreCase));

    private static string? ReadText(object? value) => value switch
    {
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        _ => null
    };
}
