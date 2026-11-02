using System.Security.Cryptography;
using System.Text;
using Jibo.Cloud.Application.Abstractions;

namespace Jibo.Cloud.Api.Hosting;

internal sealed class HubChannelLeaseOptions
{
    public const string SectionName = "OpenJibo:Security:HubChannelLeases";
    public bool Enabled { get; set; }
    public string? ConnectionString { get; set; }
    public string? HmacKey { get; set; }
}

internal sealed class HubChannelLeasePolicy : IDisposable
{
    internal static readonly HubChannelLeasePolicy Disabled = new(false);
    internal static readonly HubChannelLeasePolicy Unavailable = new(true);
    internal const string DigestDomain = "openjibo-hub-channel-lease-v1\0";
    private readonly IHubChannelLeaseStore? _store;
    private readonly byte[]? _key;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _timeout;
    public bool Enabled { get; }
    public bool IsReady => _store is not null;

    private HubChannelLeasePolicy(bool enabled)
    { Enabled = enabled; _clock = TimeProvider.System; _timeout = TimeSpan.FromSeconds(2); }

    internal HubChannelLeasePolicy(IHubChannelLeaseStore store, byte[] key,
        TimeProvider? clock = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length is < 32 or > 64) throw new ArgumentException("Lease key must contain 32 to 64 bytes.");
        _store = store; _key = key.ToArray(); Enabled = true;
        _clock = clock ?? TimeProvider.System;
        _timeout = timeout ?? TimeSpan.FromSeconds(2);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromSeconds(2)) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    internal async Task<HubChannelLeaseHandle?> AcquireAsync(string token, HubChannel channel, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!IsReady || string.IsNullOrWhiteSpace(token) || token.Length > 4096 ||
            channel is not (HubChannel.Listen or HubChannel.Proactive)) return null;
        var digest = HMACSHA256.HashData(_key!, Encoding.UTF8.GetBytes(DigestDomain + token));
        var owner = Guid.NewGuid();
        var started = _clock.GetTimestamp();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(_timeout);
        try
        {
            var result = await _store!.AcquireAsync(digest, channel, owner, deadline.Token)
                .WaitAsync(_timeout, cancellation);
            if (result.Generation < 1 || _clock.GetElapsedTime(started) >= HubChannelLeaseHandle.SafeLifetime) return null;
            return new HubChannelLeaseHandle(_store, digest, channel, owner, result.Generation, started, _clock, _timeout);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception) { deadline.Cancel(); return null; } // uncertain acquisition: never retry or accept
    }

    public void Dispose() { if (_key is not null) CryptographicOperations.ZeroMemory(_key); }
}

/// <summary>Conservative monotonic deadline, separate from the database's 30-second TTL.</summary>
internal sealed class HubChannelLeaseHandle(
    IHubChannelLeaseStore store, byte[] digest, HubChannel channel, Guid owner, long generation,
    long started, TimeProvider clock, TimeSpan timeout) : IAsyncDisposable
{
    internal static readonly TimeSpan SafeLifetime = TimeSpan.FromSeconds(25);
    internal static readonly TimeSpan IdleValidationWindow = TimeSpan.FromSeconds(1);
    private long _grantStarted = started;
    private long _idleCheckedAt;
    private int _hasIdleCheck;
    private string? _failure;
    private int _disposed;
    public string? FailureReason => Volatile.Read(ref _failure);

    private bool LocallyCurrent()
    {
        if (Volatile.Read(ref _disposed) != 0 || FailureReason is not null) return false;
        if (clock.GetElapsedTime(Volatile.Read(ref _grantStarted)) < SafeLifetime) return true;
        Fail("lease-expired"); return false;
    }

    private void Fail(string reason) => Interlocked.CompareExchange(ref _failure, reason, null);

    internal Task<bool> CheckAsync(CancellationToken cancellation) => ValidateAsync(false, cancellation);
    internal Task<bool> RenewAsync(CancellationToken cancellation) => ValidateAsync(true, cancellation);

    // Only the serial idle/watchdog path may reuse a recent positive check.
    // Inbound work and every reply still use CheckAsync without this cache.
    internal async Task<bool> CheckIdleAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!LocallyCurrent()) return false;
        if (Volatile.Read(ref _hasIdleCheck) != 0 &&
            clock.GetElapsedTime(Volatile.Read(ref _idleCheckedAt)) < IdleValidationWindow)
            return LocallyCurrent();
        var requestStarted = clock.GetTimestamp();
        if (!await CheckAsync(cancellation)) return false;
        // Count provider latency against the reuse window, not in addition to it.
        Volatile.Write(ref _idleCheckedAt, requestStarted);
        Volatile.Write(ref _hasIdleCheck, 1);
        return LocallyCurrent();
    }

    private async Task<bool> ValidateAsync(bool renew, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!LocallyCurrent()) return false;
        var requestStarted = clock.GetTimestamp();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);
        try
        {
            bool current;
            if (renew)
            {
                var result = await store.TryRenewAsync(digest, channel, owner, generation, deadline.Token)
                    .WaitAsync(timeout, cancellation);
                current = result?.Generation == generation;
            }
            else current = await store.IsCurrentAsync(digest, channel, owner, generation, deadline.Token)
                .WaitAsync(timeout, cancellation);
            if (!current) { Fail("lease-invalid"); return false; }
            if (!LocallyCurrent()) return false; // an old grant cannot be revived by a late response
            if (renew) Volatile.Write(ref _grantStarted, requestStarted);
            return LocallyCurrent();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception) { deadline.Cancel(); Fail("lease-unavailable"); return false; }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            await store.TryReleaseAsync(digest, channel, owner, generation, deadline.Token).WaitAsync(timeout);
        }
        catch (Exception) { deadline.Cancel(); } // exact-handle release only; expiry is the fallback
    }
}
