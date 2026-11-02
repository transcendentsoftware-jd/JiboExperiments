using System.Security.Cryptography;
using Jibo.Cloud.Api.Hosting;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Tests.Infrastructure;
using Jibo.Cloud.Tests.Protocol;
using Moq;

namespace Jibo.Cloud.Tests.Api;

public sealed class HubRuntimePairingStatusTests
{
    private static readonly RuntimePairingCredentialEvidence Evidence = RuntimePairingInitiationClientTests.Evidence;
    private static readonly HubTokenCredentialBinding Original = new("0123456789abcdef", DateTimeOffset.UtcNow, false);
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => _start.AddMilliseconds(_ticks);
        public void Advance(int seconds) => _ticks += seconds * 1000L;
    }
    private sealed class Client : IRuntimePairingInitiationClient
    {
        public bool IsReady => true;
        public bool IsStatusReady => true;
        public int Reads;
        public Func<RuntimePairingStatusBinding, CancellationToken, Task<RuntimePairingStatusResult>>? Read;
        public Task<RuntimePairingInitiationResult> BeginAsync(RuntimePairingCredentialEvidence c, byte[] session, CancellationToken ct = default) =>
            Task.FromResult(HubRuntimePairingSessionTests.Client.Created(session));
        public Task<RuntimePairingStatusResult> ReadStatusAsync(RuntimePairingCredentialEvidence c, byte[] session, RuntimePairingStatusBinding binding, CancellationToken ct = default)
        {
            Reads++;
            Assert.Equal(RuntimePairingInitiationIssuer.Encode(SHA256.HashData(Convert.FromHexString(new string('b', 64)))), binding.ChallengeHash);
            return Read?.Invoke(binding, ct) ?? Task.FromResult(Snapshot(binding));
        }
        public static RuntimePairingStatusResult Snapshot(RuntimePairingStatusBinding b, string phase = "pending") =>
            new(RuntimePairingStatusOutcome.Read, new(b.PairingId, b.DeviceKeyId, b.CredentialVersion, b.SessionBinding, "query", phase, b.ExpiresAt));
    }

    [Theory]
    [InlineData("read", RuntimePairingStatusOutcome.Read, 1)]
    [InlineData("before-credential", RuntimePairingStatusOutcome.Rejected, 0)]
    [InlineData("after-credential", RuntimePairingStatusOutcome.Uncertain, 1)]
    [InlineData("before-lease", RuntimePairingStatusOutcome.Rejected, 0)]
    [InlineData("after-lease", RuntimePairingStatusOutcome.Uncertain, 1)]
    [InlineData("foreign-pair", RuntimePairingStatusOutcome.Uncertain, 1)]
    public async Task SnapshotRequiresRetainedDescriptorAndFreshAuthorityOnBothSides(string scenario, RuntimePairingStatusOutcome expected, int sends)
    {
        using var key = RuntimePairingCredentialEvidenceTests.Key(); var store = new Mock<ICloudStateStore>(); var reads = 0;
        store.Setup(s => s.ReadHubPairingCredentialEvidenceAsync("issued", Original, key, It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult<RuntimePairingCredentialEvidence?>(++reads == (scenario == "before-credential" ? 3 : scenario == "after-credential" ? 4 : -1) ? null : Evidence));
        var leases = HubChannelLeasePolicyTests.WorkingStore(); var checks = 0;
        leases.Setup(s => s.IsCurrentAsync(It.IsAny<byte[]>(), HubChannel.Listen, It.IsAny<Guid>(), 1, It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(++checks != (scenario == "before-lease" ? 3 : scenario == "after-lease" ? 4 : -1)));
        using var policy = new HubChannelLeasePolicy(leases.Object, new byte[32]);
        await using var lease = (await policy.AcquireAsync("issued", HubChannel.Listen, default))!;
        var client = new Client { Read = (b, ct) => Task.FromResult(scenario == "foreign-pair"
            ? Client.Snapshot(b) with { Snapshot = Client.Snapshot(b).Snapshot! with { PairingId = Guid.NewGuid() } } : Client.Snapshot(b)) };
        var session = new HubRuntimePairingSession(store.Object, "issued", Original, Evidence, key, lease, client, default);
        Assert.Equal(RuntimePairingStatusOutcome.Unavailable, (await session.ReadStatusAsync()).Outcome);
        Assert.Equal(RuntimePairingInitiationOutcome.Created, (await session.BeginAsync()).Outcome);
        var result = await session.ReadStatusAsync(); Assert.Equal(expected, result.Outcome); Assert.Equal(sends, client.Reads);
        if (expected != RuntimePairingStatusOutcome.Read) Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task ExplicitReadsAreThrottledAndServerRetryAfterStartsWhenResponseArrives()
    {
        using var key = RuntimePairingCredentialEvidenceTests.Key(); var store = new Mock<ICloudStateStore>();
        store.Setup(s => s.ReadHubPairingCredentialEvidenceAsync("issued", Original, key, It.IsAny<CancellationToken>())).ReturnsAsync(Evidence);
        var leases = HubChannelLeasePolicyTests.WorkingStore(); using var policy = new HubChannelLeasePolicy(leases.Object, new byte[32]);
        await using var lease = (await policy.AcquireAsync("issued", HubChannel.Listen, default))!;
        var clock = new Clock(); var client = new Client();
        var session = new HubRuntimePairingSession(store.Object, "issued", Original, Evidence, key, lease, client, default, statusClock: clock);
        Assert.Equal(RuntimePairingInitiationOutcome.Created, (await session.BeginAsync()).Outcome);
        Assert.Equal(RuntimePairingStatusOutcome.Read, (await session.ReadStatusAsync()).Outcome);
        Assert.Equal(RuntimePairingStatusOutcome.RateLimited, (await session.ReadStatusAsync()).Outcome); Assert.Equal(1, client.Reads);
        clock.Advance(5);
        client.Read = (b, ct) => { clock.Advance(2); return Task.FromResult(new RuntimePairingStatusResult(RuntimePairingStatusOutcome.RateLimited, RetryAfterSeconds: 30)); };
        Assert.Equal(RuntimePairingStatusOutcome.RateLimited, (await session.ReadStatusAsync()).Outcome);
        Assert.Equal(30, (await session.ReadStatusAsync()).RetryAfterSeconds);
        clock.Advance(29); Assert.Equal(1, (await session.ReadStatusAsync()).RetryAfterSeconds); Assert.Equal(2, client.Reads);
        clock.Advance(1); client.Read = (b, ct) => Task.FromResult(Client.Snapshot(b, "authorized"));
        Assert.Equal("authorized", (await session.ReadStatusAsync()).Snapshot!.Status); Assert.Equal(3, client.Reads);
    }

    [Fact]
    public async Task ClosingConnectionDiscardsStatusAndRevokesLaterReads()
    {
        using var key = RuntimePairingCredentialEvidenceTests.Key(); using var connection = new CancellationTokenSource();
        var store = new Mock<ICloudStateStore>();
        store.Setup(s => s.ReadHubPairingCredentialEvidenceAsync("issued", Original, key, It.IsAny<CancellationToken>())).ReturnsAsync(Evidence);
        var leases = HubChannelLeasePolicyTests.WorkingStore(); using var policy = new HubChannelLeasePolicy(leases.Object, new byte[32]);
        await using var lease = (await policy.AcquireAsync("issued", HubChannel.Listen, default))!;
        var client = new Client { Read = (b, ct) => { connection.Cancel(); return Task.FromResult(Client.Snapshot(b, "authorized")); } };
        var session = new HubRuntimePairingSession(store.Object, "issued", Original, Evidence, key, lease, client, connection.Token);
        Assert.Equal(RuntimePairingInitiationOutcome.Created, (await session.BeginAsync()).Outcome);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReadStatusAsync());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReadStatusAsync()); Assert.Equal(1, client.Reads);
    }
}
