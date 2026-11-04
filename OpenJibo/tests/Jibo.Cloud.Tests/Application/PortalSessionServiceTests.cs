using Jibo.Cloud.Application.Services;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jibo.Cloud.Tests.Application;

public sealed class PortalSessionServiceTests
{
    [Fact]
    public void UnconfiguredInstances_DoNotShareSessionSigningKeys()
    {
        var configuration = new ConfigurationBuilder().Build();
        var first = new PortalSessionService(configuration);
        var second = new PortalSessionService(configuration);
        var session = first.CreateSession("synthetic-device", "synthetic-robot");

        Assert.NotNull(first.TryGetSession(session.Token));
        Assert.Null(second.TryGetSession(session.Token));
    }

    [Fact]
    public void ConfiguredInstances_CanShareSessionsAndRejectDifferentSigningKeys()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["OpenJibo:Portal:SessionSigningKey"] = "shared-test-key" }).Build();
        var first = new PortalSessionService(configuration);
        var second = new PortalSessionService(configuration);
        var other = new PortalSessionService(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["OpenJibo:Portal:SessionSigningKey"] = "other-test-key" }).Build());
        var session = first.CreateSession("synthetic-device", "synthetic-robot", "synthetic-user");

        Assert.NotNull(second.TryGetSession(session.Token));
        Assert.Null(other.TryGetSession(session.Token));
    }

    [Fact]
    public void SignedLegacyTokenWithoutPurpose_IsRejected()
    {
        const string signingKey = "legacy-test-key";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["OpenJibo:Portal:SessionSigningKey"] = signingKey }).Build();
        var service = new PortalSessionService(configuration);
        var now = DateTimeOffset.UtcNow;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            deviceId = "portal-admin", friendlyId = "Portal Admin", issuedAtUtc = now.ToUnixTimeSeconds(),
            expiresAtUtc = now.AddHours(1).ToUnixTimeSeconds(), nonce = "legacy-fixture", userId = (string?)null
        });
        string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var signature = HMACSHA256.HashData(SHA256.HashData(Encoding.UTF8.GetBytes(signingKey)), bytes);

        Assert.Null(service.TryGetSession(Encode(bytes) + "." + Encode(signature)));
    }

    [Fact]
    public void SignedVersionTwoOwnershipToken_IsRejected()
    {
        const string signingKey = "legacy-owner-test-key";
        var service = new PortalSessionService(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["OpenJibo:Portal:SessionSigningKey"] = signingKey }).Build());
        var now = DateTimeOffset.UtcNow;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            deviceId = "synthetic-device", friendlyId = "synthetic-robot", issuedAtUtc = now.ToUnixTimeSeconds(),
            expiresAtUtc = now.AddHours(1).ToUnixTimeSeconds(), nonce = "v2-fixture", userId = "old-owner",
            ownerUserId = "old-owner", kind = "robot", version = 2
        });
        string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var signature = HMACSHA256.HashData(SHA256.HashData(Encoding.UTF8.GetBytes(signingKey)), bytes);
        Assert.Null(service.TryGetSession(Encode(bytes) + "." + Encode(signature)));
    }

    [Fact]
    public void RobotMergePreviewToken_BindsSessionRouteSnapshotAndExpiry()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-10T12:00:00Z"));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenJibo:Portal:SessionSigningKey"] = "test-signing-key"
            })
            .Build();
        var service = new PortalSessionService(configuration, clock);
        var session = new PortalSessionService.PortalSession(
            "session-a", "portal-admin", "Administrator", clock.GetUtcNow().AddHours(1));
        var otherSession = session with { Token = "session-b" };

        var token = service.CreateRobotMergePreviewToken(session, "source", "target", "snapshot-a");

        Assert.True(service.TryValidateRobotMergePreviewToken(
            token, session, "SOURCE", "TARGET", "SNAPSHOT-A"));
        Assert.False(service.TryValidateRobotMergePreviewToken(
            token + "x", session, "source", "target", "snapshot-a"));
        Assert.False(service.TryValidateRobotMergePreviewToken(
            token, otherSession, "source", "target", "snapshot-a"));
        Assert.False(service.TryValidateRobotMergePreviewToken(
            token, session, "other-source", "target", "snapshot-a"));
        Assert.False(service.TryValidateRobotMergePreviewToken(
            token, session, "source", "other-target", "snapshot-a"));
        Assert.False(service.TryValidateRobotMergePreviewToken(
            token, session, "source", "target", "snapshot-b"));
        Assert.False(service.TryValidateRobotMergePreviewToken(
            service.CreateSession("admin", "Administrator").Token,
            session, "source", "target", "snapshot-a"));
        Assert.False(service.TryValidateRobotMergePreviewToken(
            service.CreateSession("admin", "Administrator").Token,
            session, "source", "target", "snapshot-a"));

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.False(service.TryValidateRobotMergePreviewToken(
            token, session, "source", "target", "snapshot-a"));
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}
