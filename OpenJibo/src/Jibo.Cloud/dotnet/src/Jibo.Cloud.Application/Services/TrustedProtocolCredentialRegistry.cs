namespace Jibo.Cloud.Application.Services;

public sealed class TrustedProtocolCredentialEntry
{
    public string? AccessKeyId { get; set; }
    public string? SecretAccessKey { get; set; }
    public override string ToString() => "TrustedProtocolCredentialEntry { redacted }";
}

public sealed class TrustedProtocolCredentialRegistryOptions
{
    public const string SectionName = "OpenJibo:Security:TrustedProtocolCredentials";
    public bool Enabled { get; set; }
    public List<TrustedProtocolCredentialEntry?>? Credentials { get; set; } = [];
    public override string ToString() => "TrustedProtocolCredentialRegistryOptions { redacted }";
}

/// <summary>
/// Immutable server-supplied secret inventory. It proves credential possession,
/// never physical identity, ownership or enrollment. Changing it requires a
/// controlled runtime restart; no traffic or fingerprint can add credentials.
/// </summary>
public sealed class TrustedProtocolCredentialRegistry
{
    public const int MaximumCredentials = 256;
    private readonly Dictionary<string, AwsSigV4Credential> _credentials = new(StringComparer.Ordinal);
    private readonly RuntimePairingCredentialEvidenceKey _evidenceKey;
    private readonly bool _valid;
    public bool Required { get; }
    public bool IsReady => Required && _valid && _evidenceKey.IsReady;

    public TrustedProtocolCredentialRegistry(TrustedProtocolCredentialRegistryOptions options,
        RuntimePairingCredentialEvidenceKey evidenceKey)
    {
        ArgumentNullException.ThrowIfNull(options);
        _evidenceKey = evidenceKey ?? throw new ArgumentNullException(nameof(evidenceKey));
        Required = options.Enabled;
        if (!Required || options.Credentials is not { Count: > 0 and <= MaximumCredentials }) return;
        var candidate = new Dictionary<string, AwsSigV4Credential>(StringComparer.Ordinal);
        var secrets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in options.Credentials)
        {
            if (entry?.AccessKeyId is not { Length: > 0 and <= 128 } id ||
                id.Any(c => c is < '!' or > '~' or '/' or ',') ||
                entry.SecretAccessKey is not { Length: >= 16 and <= 256 } secret ||
                secret.Any(c => c is < '!' or > '~') ||
                !candidate.TryAdd(id, new AwsSigV4Credential(id, secret)) || !secrets.Add(secret)) return;
        }
        _credentials = candidate;
        _valid = true;
    }

    public AwsSigV4Credential? Resolve(string accessKeyId) => IsReady &&
        _credentials.TryGetValue(accessKeyId, out var credential) ? credential : null;

    public bool IsCurrentBinding(IEnumerable<KeyValuePair<string, object?>> metadata,
        RuntimePairingCredentialEvidenceKey? key = null)
    {
        if (!IsReady || !HubTokenCredentialBinding.TryRead(metadata, out var binding) || binding is null)
            return false;
        var evidence = RuntimePairingCredentialEvidence.Read(metadata);
        if (evidence is null) return false;
        // The complete environment-keyed proof chooses a credential; the short
        // fingerprint is only an additional consistency check, never a lookup key.
        foreach (var credential in _credentials.Values)
            if (_evidenceKey.MatchesCurrentCredential(evidence, credential.AccessKeyId, credential.SecretAccessKey) &&
                (key is null || key.MatchesCurrentCredential(evidence, credential.AccessKeyId, credential.SecretAccessKey)))
                return binding.CredentialFingerprint == AwsSigV4RequestVerifier.CreateAccessKeyFingerprint(credential.AccessKeyId);
        return false;
    }

    public override string ToString() => "TrustedProtocolCredentialRegistry { redacted }";
}
