using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jibo.Cloud.Application.Services;

public enum RuntimePairingInitiationOutcome { Unavailable, Rejected, RateLimited, Created, Uncertain }

public sealed record RuntimePairingDescriptor(Guid PairingId, Guid DeviceKeyId, int CredentialVersion,
    string PairingHandle, string RobotChallenge, string SessionBinding, string RequestDigest, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "RuntimePairingDescriptor { secrets redacted }";
}
public sealed record RuntimePairingInitiationResult(RuntimePairingInitiationOutcome Outcome,
    RuntimePairingDescriptor? Descriptor = null, int RetryAfterSeconds = 0)
{
    public override string ToString() => $"RuntimePairingInitiationResult {{ {Outcome} }}";
}

/// <summary>Transport for already-trusted inputs. The active connection must guard both sides of the call.</summary>
public interface IRuntimePairingInitiationClient
{
    bool IsReady { get; }
    bool IsStatusReady => false;
    Task<RuntimePairingInitiationResult> BeginAsync(RuntimePairingCredentialEvidence credential,
        byte[] sessionBinding, CancellationToken cancellation = default);
    Task<RuntimePairingStatusResult> ReadStatusAsync(RuntimePairingCredentialEvidence credential,
        byte[] sessionBinding, RuntimePairingStatusBinding binding, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        return Task.FromResult(new RuntimePairingStatusResult(RuntimePairingStatusOutcome.Unavailable));
    }
}
public sealed class RejectingRuntimePairingInitiationClient : IRuntimePairingInitiationClient
{
    public static readonly RejectingRuntimePairingInitiationClient Instance = new();
    public bool IsReady => false;
    public Task<RuntimePairingInitiationResult> BeginAsync(RuntimePairingCredentialEvidence credential,
        byte[] sessionBinding, CancellationToken cancellation = default)
    { cancellation.ThrowIfCancellationRequested(); return Task.FromResult(new RuntimePairingInitiationResult(RuntimePairingInitiationOutcome.Unavailable)); }
}

public sealed class RuntimePairingInitiationClientOptions
{
    public const string SectionName = "OpenJibo:ManagedPairingInitiation";
    public bool Enabled { get; set; }
    public bool StatusReadsEnabled { get; set; }
    public string ManagedOrigin { get; set; } = "";
    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "";
    public string KeyId { get; set; } = "";
    public string PrivateKeyPem { get; set; } = "";
    public int LifetimeSeconds { get; set; } = 30;
}

public sealed record RuntimePairingInitiationAssertion(string Assertion,string RequestDigest)
{
    public override string ToString() => "RuntimePairingInitiationAssertion { redacted }";
}

/// <summary>Separate initiation purpose; the existing completion signer is not changed.</summary>
public sealed class RuntimePairingInitiationIssuer : IDisposable
{
    public const string Type = "openjibo-runtime-pairing-initiation+jwt";
    public const string Purpose = "openjibo-managed-pairing-initiation";
    public const string StatusType = "openjibo-runtime-pairing-status+jwt";
    public const string StatusPurpose = "openjibo-managed-pairing-status";
    private readonly ECDsa _key;
    private readonly string _issuer, _audience, _keyId;
    private readonly int _lifetime;
    private readonly TimeProvider _clock;
    private readonly bool _statusEnabled;
    private readonly object _gate = new();
    private bool _disposed;
    private RuntimePairingInitiationIssuer(RuntimePairingInitiationClientOptions options, ECDsa key, TimeProvider clock)
    { _key = key; _issuer = options.Issuer; _audience = options.Audience; _keyId = options.KeyId; _lifetime = options.LifetimeSeconds; _clock = clock; _statusEnabled = options.StatusReadsEnabled; }
    public static bool TryCreate(RuntimePairingInitiationClientOptions options, TimeProvider clock,
        out RuntimePairingInitiationIssuer? issuer)
    {
        issuer = null;
        if (!options.Enabled || !Exact(options.Issuer) || !Exact(options.Audience) ||
            string.IsNullOrEmpty(options.KeyId) || options.KeyId.Length > 64 || options.KeyId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) ||
            options.LifetimeSeconds is < 1 or > 60 || string.IsNullOrWhiteSpace(options.PrivateKeyPem)) return false;
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
    public RuntimePairingInitiationAssertion? Issue(RuntimePairingCredentialEvidence credential, byte[] sessionBinding)
    {
        if (sessionBinding.Length != 32) return null;
        lock (_gate)
        {
            if (_disposed) return null;
            var now = _clock.GetUtcNow(); var jti = RandomNumberGenerator.GetBytes(32);
            var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = _keyId, typ = Type }));
            var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1, purpose = Purpose, iss = _issuer, aud = _audience,
                credential_binding = credential.Binding, session_binding = Encode(sessionBinding),
                iat = now.ToUnixTimeSeconds(), exp = now.AddSeconds(_lifetime).ToUnixTimeSeconds(), jti = Encode(jti)
            }));
            var input = header + "." + payload;
            var signature = _key.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return new(input + "." + Encode(signature), Encode(SHA256.HashData(jti)));
        }
    }
    public RuntimePairingStatusAssertion? IssueStatus(RuntimePairingCredentialEvidence credential, byte[] session,
        RuntimePairingStatusBinding binding)
    {
        if (!_statusEnabled || session.Length != 32 || binding.PairingId == Guid.Empty || binding.DeviceKeyId == Guid.Empty ||
            binding.CredentialVersion < 1 || binding.SessionBinding != Encode(session) ||
            !RuntimePairingCredentialEvidence.TryDecode(binding.ChallengeHash, out var challenge) || challenge.Length != 32)
            return null;
        lock (_gate)
        {
            if (_disposed || binding.ExpiresAt <= _clock.GetUtcNow()) return null;
            var now = _clock.GetUtcNow(); var nonce = RandomNumberGenerator.GetBytes(32);
            var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = _keyId, typ = StatusType }));
            var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1, purpose = StatusPurpose, iss = _issuer, aud = _audience,
                pairing_id = binding.PairingId.ToString("D"), credential_binding = credential.Binding,
                session_binding = Encode(session), challenge_hash = binding.ChallengeHash,
                iat = now.ToUnixTimeSeconds(), exp = now.AddSeconds(_lifetime).ToUnixTimeSeconds(), jti = Encode(nonce)
            }));
            var input = header + "." + payload;
            var signature = _key.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return new(input + "." + Encode(signature), Encode(SHA256.HashData(nonce)));
        }
    }
    public override string ToString() => "RuntimePairingInitiationIssuer { redacted }";
    public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; _key.Dispose(); } }
    private static bool Exact(string? s) => s is not null && s.Length is > 0 and <= 200 && s == s.Trim() && !s.Any(char.IsControl);
    public static string Encode(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');
}

public interface IRuntimePairingSession
{
    Task<RuntimePairingInitiationResult> BeginAsync(CancellationToken cancellation = default);
    Task<RuntimePairingStatusResult> ReadStatusAsync(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        return Task.FromResult(new RuntimePairingStatusResult(RuntimePairingStatusOutcome.Unavailable));
    }
}
public interface IRuntimePairingSessionAccessor { IRuntimePairingSession? Current { get; } }

/// <summary>Server-bound connection context. Mutable holders revoke inherited background contexts on close.</summary>
public sealed class AmbientRuntimePairingSession : IRuntimePairingSessionAccessor
{
    private static readonly AsyncLocal<Scope?> Active = new();
    public IRuntimePairingSession? Current => Active.Value?.Session;
    public static IDisposable Bind(IRuntimePairingSession? session)
    {
        var scope = new Scope(session, Active.Value); Active.Value = scope; return scope;
    }
    private sealed class Scope(IRuntimePairingSession? session, Scope? previous) : IDisposable
    {
        private IRuntimePairingSession? _session = session;
        private int _disposed;
        public IRuntimePairingSession? Session => Volatile.Read(ref _session);
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed,1) != 0) return;
            Interlocked.Exchange(ref _session,null); Active.Value = previous;
        }
    }
}
