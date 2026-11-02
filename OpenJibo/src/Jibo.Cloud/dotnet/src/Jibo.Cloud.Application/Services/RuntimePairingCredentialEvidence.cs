using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jibo.Cloud.Application.Services;

/// <summary>Opaque credential evidence, never physical identity, ownership or co-presence.</summary>
public sealed record RuntimePairingCredentialEvidence
{
    public const string VersionKey = "runtimePairingCredentialVersion";
    public const string BindingKey = "runtimePairingCredentialBinding";
    public string Binding { get; }
    internal RuntimePairingCredentialEvidence(byte[] binding) => Binding = Encode(binding);
    public override string ToString() => "RuntimePairingCredentialEvidence { redacted }";
    public void WriteTo(IDictionary<string, object?> metadata)
    { metadata[VersionKey] = "1"; metadata[BindingKey] = Binding; }
    public static RuntimePairingCredentialEvidence? Read(IEnumerable<KeyValuePair<string, object?>> metadata)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in metadata)
        {
            if (!pair.Key.Equals(VersionKey, StringComparison.OrdinalIgnoreCase) &&
                !pair.Key.Equals(BindingKey, StringComparison.OrdinalIgnoreCase)) continue;
            var text = pair.Value switch { string s => s, JsonElement { ValueKind: JsonValueKind.String } j => j.GetString(), _ => null };
            if (!values.TryAdd(pair.Key, text)) return null;
        }
        if (values.Count != 2 || values[VersionKey] != "1" || !TryDecode(values[BindingKey], out var bytes) || bytes.Length != 32) return null;
        return new(bytes);
    }
    internal static string Encode(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static bool TryDecode(string? value, out byte[] bytes)
    {
        bytes = [];
        if (value is null || value.Length is < 1 or > 100 || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return false;
        try
        {
            var s = value.Replace('-', '+').Replace('_', '/');
            bytes = Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
            return Encode(bytes) == value;
        }
        catch (FormatException) { return false; }
    }
}

public sealed class RuntimePairingCredentialEvidenceOptions
{
    public const string SectionName = "OpenJibo:ManagedPairingCredentialEvidence";
    public bool Enabled { get; set; }
    public string? HmacKey { get; set; }
}

/// <summary>Dedicated environment key; no fallback to replay, lease or session keys.</summary>
public sealed class RuntimePairingCredentialEvidenceKey : IDisposable
{
    private readonly byte[]? _key;
    private int _disposed;
    public bool IsReady => _key is not null && Volatile.Read(ref _disposed) == 0;
    private RuntimePairingCredentialEvidenceKey(byte[]? key) => _key = key;
    public static RuntimePairingCredentialEvidenceKey Create(RuntimePairingCredentialEvidenceOptions options) =>
        options.Enabled && RuntimePairingCredentialEvidence.TryDecode(options.HmacKey, out var key) && key.Length is >= 32 and <= 64
            ? new(key) : new(null);
    // Length-prefixed UTF-8 avoids delimiter/concatenation ambiguity. Include the
    // verified secret revision: rotating a secret without renaming its ID invalidates old evidence.
    internal static byte[] CredentialIdentity(string accessKeyId, string secretAccessKey)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var value in new[] { accessKeyId, secretAccessKey })
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            try
            {
                BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
                hash.AppendData(length); hash.AppendData(bytes);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        return hash.GetHashAndReset();
    }
    internal RuntimePairingCredentialEvidence? Protect(ReadOnlySpan<byte> identity)
    {
        if (!IsReady || identity.Length != 32) return null;
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, _key!);
        hmac.AppendData("openjibo-managed-pairing-credential-v1\0"u8);
        hmac.AppendData(identity);
        return new(hmac.GetHashAndReset());
    }
    public bool MatchesCurrentCredential(RuntimePairingCredentialEvidence evidence, string accessKeyId, string secretAccessKey)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(accessKeyId) || string.IsNullOrWhiteSpace(secretAccessKey)) return false;
        var identity = CredentialIdentity(accessKeyId, secretAccessKey);
        try
        {
            var current = Protect(identity);
            return current is not null && CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(current.Binding), Encoding.ASCII.GetBytes(evidence.Binding));
        }
        finally { CryptographicOperations.ZeroMemory(identity); }
    }
    public override string ToString() => "RuntimePairingCredentialEvidenceKey { redacted }";
    public void Dispose()
    { if (Interlocked.Exchange(ref _disposed, 1) == 0 && _key is not null) CryptographicOperations.ZeroMemory(_key); }
}
