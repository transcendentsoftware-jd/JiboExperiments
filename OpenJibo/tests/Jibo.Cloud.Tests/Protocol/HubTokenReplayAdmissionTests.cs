using System.Security.Cryptography;
using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.DependencyInjection;
using Jibo.Cloud.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Jibo.Cloud.Tests.Protocol;

public sealed class HubTokenReplayAdmissionTests
{
    private static readonly AwsSigV4OperationPolicy Policy = new("Account.CreateHubToken", "api", "jibo",
        ["Account_20160715.CreateHubToken"], ["api.jibo.com"]);
    private static readonly AwsSigV4ReplayDigestKey Current = new(RandomNumberGenerator.GetBytes(32), 2);
    private static readonly AwsSigV4ReplayDigestKey Previous = new(RandomNumberGenerator.GetBytes(32), 1);

    private static ProtocolEnvelope Signed(InMemoryCloudStateStore store, bool strict = true)
    {
        var signed = AwsSigV4RequestVerifierTests.Sign(store.GetAccount(), "{\"deviceId\":\"test-robot\"}",
            DateTimeOffset.UtcNow,
            strict ? ["host", "x-amz-content-sha256", "x-amz-date", "x-amz-target"] : ["host", "x-amz-content-sha256", "x-amz-date"],
            advertisedPayloadHash: strict ? null : Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant(),
            region: "api", target: "Account_20160715.CreateHubToken");
        return new ProtocolEnvelope { ServicePrefix = "Account_20160715", Operation = "CreateHubToken",
            HostName = signed.HostName, Headers = signed.Headers, BodyText = signed.BodyText, BodyBytes = signed.BodyBytes };
    }

    [Fact]
    public async Task StrictHandler_ConsumesBeforeIssuance_AndRejectsExactReplayThroughBothCallers()
    {
        var state = new InMemoryCloudStateStore();
        var store = new AtomicTestStore();
        var handler = new CloudAuthProtocolHandler(state,
            hubTokenReplayAdmission: new HubTokenReplayAdmission(store, Current, Previous));
        var envelope = Signed(state);
        var replies = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            new JiboCloudProtocolService(state, authHandler: handler).DispatchAsync(envelope)));
        Assert.Single(replies, r => r.StatusCode == 200);
        Assert.Equal(15, replies.Count(r => r.StatusCode == 401));
        var issued = JsonDocument.Parse(replies.Single(r => r.StatusCode == 200).BodyText!).RootElement
            .GetProperty("token").GetString()!;
        Assert.NotNull(state.FindIssuedToken(issued));
        Assert.Equal(401, handler.HandleAccount("CreateHubToken", envelope).StatusCode);
        Assert.Equal(2, store.Digests.Count);
    }

    [Fact]
    public async Task AsyncDispatcherWaitsForDecision_AndCannotIssueBeforeRejection()
    {
        var real = new InMemoryCloudStateStore();
        var state = new Mock<ICloudStateStore>();
        state.Setup(s => s.GetAccount()).Returns(real.GetAccount());
        var decision = new TaskCompletionSource<HubTokenReplayAdmissionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var admission = new Mock<IHubTokenReplayAdmission>();
        admission.Setup(a => a.AdmitAsync(It.IsAny<AwsSigV4Verification>(), It.IsAny<CancellationToken>())).Returns(decision.Task);
        var handler = new CloudAuthProtocolHandler(state.Object, hubTokenReplayAdmission: admission.Object);
        var dispatch = new JiboCloudProtocolService(real, authHandler: handler).DispatchAsync(Signed(real));
        Assert.False(dispatch.IsCompleted);
        state.Verify(s => s.IssueHubToken(It.IsAny<string>(),It.IsAny<bool>(),It.IsAny<HubTokenCredentialBinding>()),Times.Never);
        decision.SetResult(HubTokenReplayAdmissionOutcome.Rejected);
        Assert.Equal(401,(await dispatch).StatusCode);
        state.Verify(s => s.IssueHubToken(It.IsAny<string>(),It.IsAny<bool>(),It.IsAny<HubTokenCredentialBinding>()),Times.Never);
    }

    [Fact]
    public async Task ForgedRecordCannotUpgradeCredentialOnlyProofOrChangeItsTimestamp()
    {
        var state = new InMemoryCloudStateStore();
        var verifier = new AwsSigV4RequestVerifier(state);
        var store = new Mock<IHubTokenReplayAdmissionStore>(MockBehavior.Strict);
        var service = new HubTokenReplayAdmission(store.Object, Current);
        var weak = verifier.Verify(Signed(state, false), Policy);
        var forged = weak with { Outcome = AwsSigV4VerificationOutcome.VerifiedPayloadAndTarget,
            PayloadBound = true, ScopeClassification = AwsSigV4ScopeClassification.Match,
            TargetClassification = AwsSigV4TargetClassification.MatchSigned, HostClassification = AwsSigV4HostClassification.Match };
        Assert.Equal(HubTokenReplayAdmissionOutcome.Rejected, await service.AdmitAsync(forged));
        var valid = verifier.Verify(Signed(state), Policy);
        Assert.Equal(HubTokenReplayAdmissionOutcome.Rejected,
            await service.AdmitAsync(valid with { SignedAt = valid.SignedAt!.Value.AddSeconds(1) }));
        Assert.Equal(HubTokenReplayAdmissionOutcome.Rejected, await service.AdmitAsync(new(AwsSigV4VerificationOutcome.NotPresented)));
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WrongOperationAndStaleProofCannotReachStore()
    {
        var state = new InMemoryCloudStateStore();
        var verifier = new AwsSigV4RequestVerifier(state);
        var store = new Mock<IHubTokenReplayAdmissionStore>(MockBehavior.Strict);
        var now = DateTimeOffset.UtcNow;
        var notification = AwsSigV4RequestVerifierTests.Sign(state.GetAccount(), "{}", now,
            ["host", "x-amz-content-sha256", "x-amz-date", "x-amz-target"]);
        var other = verifier.Verify(notification, new("Notification.NewRobotToken", "us-east-1", "jibo",
            ["Notification_20150505.NewRobotToken"], ["api.jibo.com"]));
        Assert.True(other.OperationAuthenticated);
        Assert.Equal(HubTokenReplayAdmissionOutcome.Rejected,
            await new HubTokenReplayAdmission(store.Object, Current).AdmitAsync(other));
        var valid = verifier.Verify(Signed(state), Policy);
        Assert.Equal(HubTokenReplayAdmissionOutcome.Rejected,
            await new HubTokenReplayAdmission(store.Object, Current, clock: new FixedClock(now.AddMinutes(6))).AdmitAsync(valid));
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LostResponseOrTimeoutNeverRetriesOrIssuesAToken()
    {
        foreach (var lost in new[] { false, true })
        {
            var real = new InMemoryCloudStateStore();
            var state = new Mock<ICloudStateStore>();
            state.Setup(s => s.GetAccount()).Returns(real.GetAccount());
            var store = new Mock<IHubTokenReplayAdmissionStore>();
            var waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken storeCancellation = default;
            store.Setup(s => s.TryConsumeAsync(It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<IReadOnlyList<short>>(),
                    It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .Returns((IReadOnlyList<byte[]> _, IReadOnlyList<short> _, DateTimeOffset _, CancellationToken ct) =>
                { storeCancellation = ct; return lost ? Task.FromException<bool>(new IOException("private-database-detail")) : waiting.Task; });
            var handler = new CloudAuthProtocolHandler(state.Object, hubTokenReplayAdmission:
                new HubTokenReplayAdmission(store.Object, Current, timeout: TimeSpan.FromMilliseconds(20)));
            var reply = await handler.HandleAccountAsync("CreateHubToken", Signed(real)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(503, reply.StatusCode);
            Assert.DoesNotContain("private", reply.BodyText!);
            Assert.True(storeCancellation.IsCancellationRequested);
            store.Verify(s => s.TryConsumeAsync(It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<IReadOnlyList<short>>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
            state.Verify(s => s.IssueHubToken(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<HubTokenCredentialBinding>()), Times.Never);
            waiting.TrySetResult(true);
        }
    }

    [Fact]
    public async Task DefaultModeKeepsUnsignedCompatibility_EnabledBadConfigIsUnavailable()
    {
        var state = new InMemoryCloudStateStore();
        Assert.Equal(200, (await new CloudAuthProtocolHandler(state).HandleAccountAsync("CreateHubToken", new())).StatusCode);
        foreach (var kind in new[] { "missing", "partial-previous", "duplicate-material", "duplicate-version", "bad-connection" })
        {
            var settings = new Dictionary<string, string?> { [HubTokenReplayAdmissionOptions.SectionName + ":Enabled"] = "true" };
            if (kind != "missing")
            {
                var key = Convert.ToBase64String(new byte[32]);
                settings[HubTokenReplayAdmissionOptions.SectionName + ":HmacKey"] = key;
                settings[HubTokenReplayAdmissionOptions.SectionName + ":ConnectionString"] = kind == "bad-connection" ? "not a connection" : "Host=127.0.0.1;Port=1;Database=unused;Username=unused";
                if (kind != "bad-connection") settings[HubTokenReplayAdmissionOptions.SectionName + ":PreviousHmacKey"] = kind == "duplicate-version" ? Convert.ToBase64String(Enumerable.Repeat((byte)1,32).ToArray()) : key;
                if (kind != "partial-previous") settings[HubTokenReplayAdmissionOptions.SectionName + ":PreviousKeyVersion"] = kind == "duplicate-material" ? "2" : kind == "duplicate-version" ? "1" : "0";
            }
            var services = new ServiceCollection();
            HubTokenReplayAdmissionRegistration.Add(services, new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
            await using var provider = services.BuildServiceProvider();
            var result = await provider.GetRequiredService<IHubTokenReplayAdmission>().AdmitAsync(new(AwsSigV4VerificationOutcome.NotPresented));
            Assert.Equal(HubTokenReplayAdmissionOutcome.Unavailable, result);
        }
    }

    [Fact]
    public async Task CancellationIsPropagated_AndDuplicateKeySlotsAreRejected()
    {
        var state = new InMemoryCloudStateStore();
        var store = new Mock<IHubTokenReplayAdmissionStore>(MockBehavior.Strict);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HubTokenReplayAdmission(store.Object, Current)
            .AdmitAsync(new AwsSigV4RequestVerifier(state).Verify(Signed(state), Policy), cancellation.Token));
        Assert.Throws<ArgumentException>(() => new HubTokenReplayAdmission(store.Object, Current, Current));
        Assert.Throws<ArgumentException>(() => new HubTokenReplayAdmission(store.Object,
            new AwsSigV4ReplayDigestKey(new byte[32],1), new AwsSigV4ReplayDigestKey(new byte[32],2)));
        store.VerifyNoOtherCalls();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class AtomicTestStore : IHubTokenReplayAdmissionStore
    {
        public HashSet<string> Digests { get; } = [];
        public Task<bool> TryConsumeAsync(IReadOnlyList<byte[]> digests, IReadOnlyList<short> versions,
            DateTimeOffset signedAt, CancellationToken cancellationToken = default) => Task.Run(() =>
        {
            lock (Digests)
            {
                var keys = digests.Select(Convert.ToHexString).ToArray();
                if (keys.Any(Digests.Contains)) return false;
                foreach (var key in keys) Digests.Add(key);
                return true;
            }
        }, cancellationToken);
    }
}
