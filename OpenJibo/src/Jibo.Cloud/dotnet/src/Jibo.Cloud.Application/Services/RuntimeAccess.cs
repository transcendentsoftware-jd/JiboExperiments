using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Jibo.Cloud.Application.Services;
public sealed record RuntimeAccessIdentity(Guid RobotId,string DeviceId);
public interface IRuntimeAccessClient
{
 bool Enabled {get;} bool IsReady {get;}
 RuntimeAccessIdentity? ResolveIdentity(RuntimePairingCredentialEvidence credential);
 Task<DateTimeOffset?> AuthorizeAsync(RuntimePairingCredentialEvidence credential,byte[] session,string operation,CancellationToken cancellation);
}
public sealed class UnavailableRuntimeAccessClient(bool enabled):IRuntimeAccessClient
{
 public bool Enabled=>enabled;public bool IsReady=>false;
 public RuntimeAccessIdentity? ResolveIdentity(RuntimePairingCredentialEvidence credential)=>null;
 public Task<DateTimeOffset?> AuthorizeAsync(RuntimePairingCredentialEvidence credential,byte[] session,string operation,CancellationToken cancellation)
 {cancellation.ThrowIfCancellationRequested();return Task.FromResult<DateTimeOffset?>(null);}
}
public sealed class RuntimeAccessClientOptions
{
    public const string SectionName = "OpenJibo:RuntimeAccess";
    public Dictionary<string,RuntimeAccessIdentity> Identities {get;set;}=new(StringComparer.Ordinal);
    public bool Enabled { get; set; }
    
    public string ManagedOrigin { get; set; } = "";
    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "";
    public string KeyId { get; set; } = "";
    public string PrivateKeyPem { get; set; } = "";
    public int LifetimeSeconds { get; set; } = 10;
}

public sealed record RuntimeAccessAssertion(string Assertion,string RequestDigest)
{
    public override string ToString() => "RuntimeAccessAssertion { redacted }";
}

/// <summary>Dedicated runtime authorization purpose; pairing proofs cannot authorize service use.</summary>
public sealed class RuntimeAccessIssuer : IDisposable
{
    public const string Type = "openjibo-runtime-access+jwt";
    public const string Purpose = "openjibo-managed-runtime-access";
    private readonly ECDsa _key;
    private readonly string _issuer, _audience, _keyId;
    private readonly int _lifetime;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private bool _disposed;
    private RuntimeAccessIssuer(RuntimeAccessClientOptions options, ECDsa key, TimeProvider clock)
    { _key = key; _issuer = options.Issuer; _audience = options.Audience; _keyId = options.KeyId; _lifetime = options.LifetimeSeconds; _clock = clock; }
    public static bool TryCreate(RuntimeAccessClientOptions options, TimeProvider clock,
        out RuntimeAccessIssuer? issuer)
    {
        issuer = null;
        if (!options.Enabled || !Exact(options.Issuer) || !Exact(options.Audience) ||
            string.IsNullOrEmpty(options.KeyId) || options.KeyId.Length > 64 || options.KeyId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) ||
            options.LifetimeSeconds is < 1 or > 10 || string.IsNullOrWhiteSpace(options.PrivateKeyPem)) return false;
        ECDsa? key = null;
        try
        {
            key = ECDsa.Create(); key.ImportFromPem(options.PrivateKeyPem);
            var parameters = key.ExportParameters(true);
            if (key.KeySize != 256 || parameters.Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value || parameters.D is not { Length: 32 })
                throw new CryptographicException();
            issuer = new(options, key, clock); return true;
        }
        catch (Exception e) when (e is ArgumentException or CryptographicException) { key?.Dispose(); return false; }
    }
    public RuntimeAccessAssertion? Issue(RuntimePairingCredentialEvidence credential, byte[] sessionBinding,string operation)
    {
        if (sessionBinding.Length != 32 || operation is not ("hub-token" or "hub-listen" or "hub-proactive" or "protocol-http" or "robot-socket")) return null;
        lock (_gate)
        {
            if (_disposed) return null;
            var now = _clock.GetUtcNow(); var jti = RandomNumberGenerator.GetBytes(32);
            var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = _keyId, typ = Type }));
            var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1, purpose = Purpose, iss = _issuer, aud = _audience,
                credential_binding = credential.Binding, session_binding = Encode(sessionBinding), operation,
                iat = now.ToUnixTimeSeconds(), exp = now.AddSeconds(_lifetime).ToUnixTimeSeconds(), jti = Encode(jti)
            }));
            var input = header + "." + payload;
            var signature = _key.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return new(input + "." + Encode(signature), Encode(SHA256.HashData(jti)));
        }
    }
    public override string ToString()=>"RuntimeAccessIssuer { redacted }";
    public void Dispose(){lock(_gate){if(_disposed)return;_disposed=true;_key.Dispose();}}
    private static bool Exact(string? s)=>s is not null&&s.Length is >0 and <=200&&s==s.Trim()&&!s.Any(char.IsControl);
    public static string Encode(ReadOnlySpan<byte> bytes)=>Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');
}
