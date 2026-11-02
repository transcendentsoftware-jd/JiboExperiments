using System.Security.Cryptography;
using System.Text;
using Jibo.Cloud.Api.Hosting;
using Jibo.Cloud.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Jibo.Cloud.Tests.Api;

public sealed class HubChannelLeasePolicyTests
{
    private static readonly byte[] Key = Enumerable.Repeat((byte)42, 32).ToArray();
    private static HubChannelLease Grant(long generation = 1) => new(generation, DateTimeOffset.UtcNow.AddSeconds(30));

    [Fact]
    public async Task TwoHundredIdleTicksUseTenChecks_WhileInboundAndReplyChecksRemainFresh()
    {
        var clock = new ManualClock(); var store = WorkingStore();
        using var policy = new HubChannelLeasePolicy(store.Object, Key, clock);
        await using var lease = (await policy.AcquireAsync("opaque", HubChannel.Listen, default))!;
        for (var tick = 0; tick < 200; tick++)
        {
            Assert.True(await lease.CheckIdleAsync(default));
            clock.Advance(TimeSpan.FromMilliseconds(50));
        }
        store.Verify(s => s.IsCurrentAsync(It.IsAny<byte[]>(), HubChannel.Listen, It.IsAny<Guid>(), 1, It.IsAny<CancellationToken>()), Times.Exactly(10));
        Assert.True(await lease.CheckAsync(default)); Assert.True(await lease.CheckAsync(default));
        store.Verify(s => s.IsCurrentAsync(It.IsAny<byte[]>(), HubChannel.Listen, It.IsAny<Guid>(), 1, It.IsAny<CancellationToken>()), Times.Exactly(12));
    }

    [Fact]
    public async Task ProviderLatencyCountsAgainstIdleReuseWindow()
    {
        var clock = new ManualClock(); var store = WorkingStore();
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.SetupSequence(s => s.IsCurrentAsync(It.IsAny<byte[]>(), It.IsAny<HubChannel>(), It.IsAny<Guid>(), 1, It.IsAny<CancellationToken>()))
            .Returns(pending.Task).ReturnsAsync(true);
        using var policy = new HubChannelLeasePolicy(store.Object, Key, clock);
        await using var lease = (await policy.AcquireAsync("opaque", HubChannel.Listen, default))!;
        var first = lease.CheckIdleAsync(default);
        clock.Advance(TimeSpan.FromMilliseconds(900)); pending.SetResult(true); Assert.True(await first);
        clock.Advance(TimeSpan.FromMilliseconds(200)); Assert.True(await lease.CheckIdleAsync(default));
        store.Verify(s => s.IsCurrentAsync(It.IsAny<byte[]>(), It.IsAny<HubChannel>(), It.IsAny<Guid>(), 1, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task PositiveIdleCacheCannotBypassCancellationExpiryOrRenewalFailure()
    {
        var clock = new ManualClock(); var store = WorkingStore();
        using var policy = new HubChannelLeasePolicy(store.Object, Key, clock);
        await using var expired = (await policy.AcquireAsync("opaque", HubChannel.Listen, default))!;
        Assert.True(await expired.CheckIdleAsync(default));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => expired.CheckIdleAsync(cancellation.Token));
        clock.Advance(TimeSpan.FromSeconds(25)); Assert.False(await expired.CheckIdleAsync(default));
        Assert.Equal("lease-expired", expired.FailureReason);
        await using var replaced = (await policy.AcquireAsync("opaque", HubChannel.Listen, default))!;
        Assert.True(await replaced.CheckIdleAsync(default));
        store.Setup(s => s.TryRenewAsync(It.IsAny<byte[]>(), It.IsAny<HubChannel>(), It.IsAny<Guid>(), 1, It.IsAny<CancellationToken>())).ReturnsAsync((HubChannelLease?)null);
        Assert.False(await replaced.RenewAsync(default)); Assert.False(await replaced.CheckIdleAsync(default));
        Assert.Equal("lease-invalid", replaced.FailureReason);
        store.Verify(s => s.IsCurrentAsync(It.IsAny<byte[]>(), It.IsAny<HubChannel>(), It.IsAny<Guid>(), 1, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task FailedIdleRefreshInvalidatesPositiveCacheWithoutRetry()
    {
        var clock = new ManualClock(); var store = WorkingStore();
        store.SetupSequence(s => s.IsCurrentAsync(It.IsAny<byte[]>(), It.IsAny<HubChannel>(), It.IsAny<Guid>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true).ThrowsAsync(new InvalidOperationException("private-provider-details"));
        using var policy = new HubChannelLeasePolicy(store.Object, Key, clock);
        await using var lease = (await policy.AcquireAsync("opaque", HubChannel.Listen, default))!;
        Assert.True(await lease.CheckIdleAsync(default)); clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(await lease.CheckIdleAsync(default)); Assert.False(await lease.CheckIdleAsync(default));
        Assert.Equal("lease-unavailable", lease.FailureReason);
        store.Verify(s => s.IsCurrentAsync(It.IsAny<byte[]>(), It.IsAny<HubChannel>(), It.IsAny<Guid>(), 1, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task IdleReuseIsConnectionLocalAndDoesNotCrossChannels()
    {
        var clock = new ManualClock(); var store = WorkingStore();
        using var policy = new HubChannelLeasePolicy(store.Object, Key, clock);
        await using var listen = (await policy.AcquireAsync("opaque", HubChannel.Listen, default))!;
        await using var proactive = (await policy.AcquireAsync("opaque", HubChannel.Proactive, default))!;
        Assert.True(await listen.CheckIdleAsync(default)); Assert.True(await listen.CheckIdleAsync(default));
        Assert.True(await proactive.CheckIdleAsync(default));
        store.Verify(s => s.IsCurrentAsync(It.IsAny<byte[]>(), HubChannel.Listen, It.IsAny<Guid>(), 1, It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.IsCurrentAsync(It.IsAny<byte[]>(), HubChannel.Proactive, It.IsAny<Guid>(), 1, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DigestIsStableDomainSeparatedAndOpaque_WhileOwnersAreUniqueAndChannelsRemainDistinct()
    {
        var observed = new List<(byte[] Digest, HubChannel Channel, Guid Owner)>();
        var store = new Mock<IHubChannelLeaseStore>();
        store.Setup(s => s.AcquireAsync(It.IsAny<byte[]>(), It.IsAny<HubChannel>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((byte[] digest, HubChannel channel, Guid owner, CancellationToken _) =>
            { observed.Add((digest, channel, owner)); return Task.FromResult(Grant()); });
        using var first = new HubChannelLeasePolicy(store.Object, Key);
        using var replica = new HubChannelLeasePolicy(store.Object, Key);
        Assert.NotNull(await first.AcquireAsync("opaque-bearer", HubChannel.Listen, default));
        Assert.NotNull(await replica.AcquireAsync("opaque-bearer", HubChannel.Proactive, default));
        Assert.Equal(observed[0].Digest, observed[1].Digest);
        Assert.NotEqual(observed[0].Owner, observed[1].Owner);
        Assert.Equal(new[] { HubChannel.Listen, HubChannel.Proactive }, observed.Select(o => o.Channel));
        Assert.Equal(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(HubChannelLeasePolicy.DigestDomain + "opaque-bearer")), observed[0].Digest);
        Assert.NotEqual(SHA256.HashData(Encoding.UTF8.GetBytes("opaque-bearer")), observed[0].Digest);
        await first.AcquireAsync("different-bearer", HubChannel.Listen, default);
        Assert.False(observed[0].Digest.SequenceEqual(observed[2].Digest));
    }

    [Fact]
    public async Task UncertainAcquisitionIsBoundedCanceledAndNeverRetried()
    {
        var pending = new TaskCompletionSource<HubChannelLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        var store = new Mock<IHubChannelLeaseStore>();
        store.Setup(s => s.AcquireAsync(It.IsAny<byte[]>(), It.IsAny<HubChannel>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((byte[] _, HubChannel _, Guid _, CancellationToken cancellation) => { observed = cancellation; return pending.Task; });
        using var policy = new HubChannelLeasePolicy(store.Object, Key, timeout: TimeSpan.FromMilliseconds(20));
        Assert.Null(await policy.AcquireAsync("opaque", HubChannel.Listen, default).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(observed.IsCancellationRequested);
        store.Verify(s => s.AcquireAsync(It.IsAny<byte[]>(), It.IsAny<HubChannel>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        pending.SetResult(Grant()); // a late committed acquisition does not accept a connection
    }

    [Fact]
    public void DisabledAndInvalidConfigurationRemainDistinct_WithoutCredentialFallback()
    {
        foreach (var values in new[]
        {
            new Dictionary<string, string?>(),
            new() { ["Enabled"] = "true" },
            new() { ["Enabled"] = "true", ["HmacKey"] = "%%%" },
            new() { ["Enabled"] = "true", ["HmacKey"] = "AQ", ["ConnectionString"] = "Host=localhost;Database=unused;Username=unused" },
            new() { ["Enabled"] = "true", ["HmacKey"] = Convert.ToBase64String(Key).TrimEnd('='), ["ConnectionString"] = "Host=remote.example;SslMode=Require;Database=unused;Username=unused" }
        })
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(p => HubChannelLeaseOptions.SectionName + ":" + p.Key, p => p.Value)).Build();
            var services = new ServiceCollection(); services.AddHubChannelLeases(config);
            using var provider = services.BuildServiceProvider();
            var policy = provider.GetRequiredService<HubChannelLeasePolicy>();
            Assert.Equal(values.ContainsKey("Enabled"), policy.Enabled);
            Assert.False(policy.IsReady);
        }
    }

    [Fact]
    public async Task MonotonicDeadlineIgnoresWallClockChanges_AndExpiredHandleCannotQueryOrRenew()
    {
        var clock = new ManualClock(); var store = WorkingStore();
        using var policy = new HubChannelLeasePolicy(store.Object, Key, clock);
        await using var handle = (await policy.AcquireAsync("opaque", HubChannel.Listen, default))!;
        clock.WallClock = clock.WallClock.AddYears(10);
        Assert.True(await handle.CheckAsync(default));
        clock.Advance(TimeSpan.FromSeconds(25));
        Assert.False(await handle.CheckAsync(default));
        Assert.False(await handle.RenewAsync(default));
        Assert.Equal("lease-expired", handle.FailureReason);
        store.Verify(s => s.IsCurrentAsync(It.IsAny<byte[]>(), It.IsAny<HubChannel>(), It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.TryRenewAsync(It.IsAny<byte[]>(), It.IsAny<HubChannel>(), It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LateRenewalCannotResurrectExpiredLocalGrant()
    {
        var clock = new ManualClock(); var store = WorkingStore();
        var pending = new TaskCompletionSource<HubChannelLease?>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Setup(s => s.TryRenewAsync(It.IsAny<byte[]>(), It.IsAny<HubChannel>(), It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>())).Returns(pending.Task);
        using var policy = new HubChannelLeasePolicy(store.Object, Key, clock);
        await using var handle = (await policy.AcquireAsync("opaque", HubChannel.Listen, default))!;
        var renewing = handle.RenewAsync(default);
        clock.Advance(TimeSpan.FromSeconds(25)); pending.SetResult(Grant());
        Assert.False(await renewing);
        Assert.Equal("lease-expired", handle.FailureReason);
    }

    [Fact]
    public async Task RenewalExtendsDeadlineFromRequestStart_AndWrongGenerationFailsPermanently()
    {
        var clock = new ManualClock(); var store = WorkingStore();
        using var policy = new HubChannelLeasePolicy(store.Object, Key, clock);
        await using var handle = (await policy.AcquireAsync("opaque", HubChannel.Listen, default))!;
        clock.Advance(TimeSpan.FromSeconds(20)); Assert.True(await handle.RenewAsync(default));
        clock.Advance(TimeSpan.FromSeconds(10)); Assert.True(await handle.CheckAsync(default));
        store.Setup(s => s.TryRenewAsync(It.IsAny<byte[]>(), It.IsAny<HubChannel>(), It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>())).ReturnsAsync(Grant(2));
        Assert.False(await handle.RenewAsync(default));
        Assert.Equal("lease-invalid", handle.FailureReason);
        Assert.False(await handle.CheckAsync(default));
    }

    [Fact]
    public async Task UnresponsiveValidationFailsClosed_AndReleaseUsesExactHandleOnceWithBoundedWait()
    {
        var store = WorkingStore();
        var waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken checkedToken = default, releasedToken = default;
        store.Setup(s => s.IsCurrentAsync(It.IsAny<byte[]>(), It.IsAny<HubChannel>(), It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns((byte[] _, HubChannel _, Guid _, long _, CancellationToken cancellation) => { checkedToken = cancellation; return waiting.Task; });
        store.Setup(s => s.TryReleaseAsync(It.IsAny<byte[]>(), It.IsAny<HubChannel>(), It.IsAny<Guid>(), 1, It.IsAny<CancellationToken>()))
            .Returns((byte[] _, HubChannel _, Guid _, long _, CancellationToken cancellation) => { releasedToken = cancellation; return waiting.Task; });
        using var policy = new HubChannelLeasePolicy(store.Object, Key, timeout: TimeSpan.FromMilliseconds(20));
        var handle = (await policy.AcquireAsync("opaque", HubChannel.Proactive, default))!;
        Assert.False(await handle.CheckAsync(default).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("lease-unavailable", handle.FailureReason); Assert.True(checkedToken.IsCancellationRequested);
        await handle.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await handle.DisposeAsync(); Assert.True(releasedToken.IsCancellationRequested);
        store.Verify(s => s.TryReleaseAsync(It.IsAny<byte[]>(), HubChannel.Proactive, It.IsAny<Guid>(), 1, It.IsAny<CancellationToken>()), Times.Once);
        waiting.SetResult(true);
    }

    [Fact]
    public async Task NormalCancellationDoesNotRecordAProviderFailure()
    {
        var store = WorkingStore(); using var policy = new HubChannelLeasePolicy(store.Object, Key);
        await using var handle = (await policy.AcquireAsync("opaque", HubChannel.Listen, default))!;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handle.CheckAsync(cancellation.Token));
        Assert.Null(handle.FailureReason);
    }

    internal static Mock<IHubChannelLeaseStore> WorkingStore()
    {
        var store = new Mock<IHubChannelLeaseStore>();
        store.Setup(s => s.AcquireAsync(It.IsAny<byte[]>(), It.IsAny<HubChannel>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(Grant());
        store.Setup(s => s.TryRenewAsync(It.IsAny<byte[]>(), It.IsAny<HubChannel>(), It.IsAny<Guid>(), 1, It.IsAny<CancellationToken>())).ReturnsAsync(Grant());
        store.Setup(s => s.IsCurrentAsync(It.IsAny<byte[]>(), It.IsAny<HubChannel>(), It.IsAny<Guid>(), 1, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        store.Setup(s => s.TryReleaseAsync(It.IsAny<byte[]>(), It.IsAny<HubChannel>(), It.IsAny<Guid>(), 1, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        return store;
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        public DateTimeOffset WallClock { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => WallClock;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _timestamp, (long)duration.TotalMilliseconds);
    }
}
