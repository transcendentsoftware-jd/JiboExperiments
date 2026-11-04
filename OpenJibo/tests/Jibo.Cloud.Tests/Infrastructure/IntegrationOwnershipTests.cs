using System.Reflection;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Persistence;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class IntegrationOwnershipTests
{
    [Fact]
    public void TransferDuringWrite_DoesNotOverwriteNewOwnersCalendarOrSecret()
    {
        var state = new InMemoryCloudStateStore();
        state.UpsertDevice(new DeviceRegistration { DeviceId = "race-device", RobotId = "race-robot" });
        var first = state.CreateUser("first@example.invalid", "password123", null, null)!;
        var second = state.CreateUser("second@example.invalid", "password123", null, null)!;
        var oldOwnership = state.LinkUserToDevice(first.Id, "race-device", "claim");
        var loop = state.AddLoop(null, null, "race-device", "race-robot");
        var proxy = DispatchProxy.Create<ICloudStateStore, TransferRaceProxy>();
        var race = (TransferRaceProxy)proxy;
        race.Target = state;
        var integrations = new InMemoryUserIntegrationStore(
            new EncryptedUserDataSnapshotStore(null, new UserDataEncryptionService()), proxy);
        HomeAssistantLinkRecord? newLink = null;
        MemberCalendarFeedRecord? newFeed = null;
        race.AfterOwnershipRead = () =>
        {
            state.LinkUserToDevice(second.Id, "race-device", "transfer");
            newLink = integrations.AddHomeAssistantLink("race-device", "race-robot", "race-ha");
            newFeed = integrations.UpsertMemberCalendarFeed(loop.LoopId, "member", "https://new.example.invalid/private");
        };
        integrations.UpsertMemberCalendarFeed(loop.LoopId, "member", "https://old.example.invalid/delayed",
            expectedOwnershipEpoch: oldOwnership.EffectiveOwnershipEpoch);
        Assert.Equal(newFeed!.FeedId, integrations.FindMemberCalendarFeed(loop.LoopId, "member")!.FeedId);
        Assert.Equal("https://new.example.invalid/private", integrations.FindMemberCalendarFeed(loop.LoopId, "member")!.IcalUrl);
        Assert.Equal(newLink!.CommandSecret, integrations.FindLinkForJibo("race-device", null)!.CommandSecret);

        var returned = state.LinkUserToDevice(first.Id, "race-device", "return");
        race.AfterOwnershipRead = () =>
        {
            state.LinkUserToDevice(second.Id, "race-device", "transfer-again");
            newLink = integrations.AddHomeAssistantLink("race-device", "race-robot", "race-ha");
        };
        var delayed = integrations.AddHomeAssistantLink("race-device", "race-robot", "race-ha", returned.EffectiveOwnershipEpoch);
        Assert.NotEqual(newLink.LinkId, delayed.LinkId);
        Assert.NotEqual(newLink.CommandSecret, delayed.CommandSecret);
        Assert.Equal(newLink.CommandSecret, integrations.FindLinkForJibo("race-device", null)!.CommandSecret);
    }

    public class TransferRaceProxy : DispatchProxy
    {
        public ICloudStateStore Target { get; set; } = null!;
        public Action? AfterOwnershipRead { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var result = targetMethod!.Invoke(Target, args);
            if (targetMethod.Name == nameof(ICloudStateStore.GetUserDeviceLink) && AfterOwnershipRead is { } callback)
            {
                AfterOwnershipRead = null;
                callback();
            }
            return result;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Transfer_PreservesSameOwnerAndSeparatesNewOwnerAcrossEncryptedReload(bool legacy)
    {
        var root = Path.Combine(Path.GetTempPath(), $"openjibo-transfer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var state = new InMemoryCloudStateStore(Path.Combine(root, "state.json"));
        state.UpsertDevice(new DeviceRegistration { DeviceId = "ownership-device", RobotId = "ownership-robot" });
        var first = state.CreateUser("first@example.invalid", "password123", null, null)!;
        var second = state.CreateUser("second@example.invalid", "password123", null, null)!;
        var ownership = state.LinkUserToDevice(first.Id, "ownership-device", "test");
        var loop = state.AddLoop(null, null, "ownership-device", "ownership-robot");
        var snapshot = new EncryptedUserDataSnapshotStore(Path.Combine(root, "integrations.json"), new UserDataEncryptionService());
        if (legacy)
            snapshot.Save(new UserIntegrationSnapshot
            {
                SchemaVersion = 2,
                HomeAssistantLinks = [new HomeAssistantLinkRecord
                {
                    LinkId = "legacy-private-link", JiboDeviceId = "ownership-device",
                    JiboFriendlyName = "ownership-robot", HaInstanceId = "same-ha", CommandSecret = "legacy-secret"
                }],
                MemberCalendarFeeds = [new MemberCalendarFeedRecord
                {
                    FeedId = "legacy-private-feed", LoopId = loop.LoopId, MemberId = "member",
                    IcalUrl = "https://old.example.invalid/private-token"
                }]
            });
        var integrations = new InMemoryUserIntegrationStore(snapshot, state);
        var original = legacy ? integrations.FindLinkByLinkId("legacy-private-link")! :
            integrations.AddHomeAssistantLink("ownership-device", "ownership-robot", "same-ha");
        var oldFeed = legacy ? integrations.FindMemberCalendarFeed(loop.LoopId, "member")! :
            integrations.UpsertMemberCalendarFeed(loop.LoopId, "member", "https://old.example.invalid/private-token");
        Assert.NotNull(original);
        var bytes = File.ReadAllBytes(Path.Combine(root, "integrations.json"));
        Assert.Equal(ownership, state.LinkUserToDevice(first.Id, "OWNERSHIP-DEVICE", "re-pair"));
        Assert.Equal(original.CommandSecret, integrations.FindLinkForJibo("ownership-device", null)!.CommandSecret);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(root, "integrations.json")));

        var transferred = state.LinkUserToDevice(second.Id, "ownership-device", "transfer");
        Assert.NotEqual(ownership.EffectiveOwnershipEpoch, transferred.EffectiveOwnershipEpoch);
        Assert.Null(integrations.FindLinkByLinkId(original.LinkId));
        Assert.Null(integrations.FindMemberCalendarFeed(loop.LoopId, "member"));
        Assert.Throws<UnauthorizedAccessException>(() => integrations.AddHomeAssistantLink(
            "ownership-device", "ownership-robot", "same-ha", ownership.EffectiveOwnershipEpoch));
        Assert.Throws<UnauthorizedAccessException>(() => integrations.UpsertMemberCalendarFeed(
            loop.LoopId, "member", "https://stale.example.invalid", true, ownership.EffectiveOwnershipEpoch));
        var replacement = integrations.AddHomeAssistantLink("ownership-device", "ownership-robot", "same-ha");
        var newFeed = integrations.UpsertMemberCalendarFeed(loop.LoopId, "member", "https://new.example.invalid/private-token");
        Assert.NotEqual(original.LinkId, replacement.LinkId);
        Assert.NotEqual(original.CommandSecret, replacement.CommandSecret);
        Assert.NotEqual(oldFeed.FeedId, newFeed.FeedId);
        Assert.Null(integrations.FindLinkForJibo("ownership-device", null, ownership.EffectiveOwnershipEpoch));
        Assert.Empty(integrations.GetMemberCalendarFeeds(loop.LoopId, ownership.EffectiveOwnershipEpoch));
        Assert.Null(integrations.UpdateMemberCalendarFeedSyncStatus(loop.LoopId, "member", null, "old-fetch", oldFeed.FeedId));
        Assert.Null(integrations.FindMemberCalendarFeed(loop.LoopId, "member")!.LastError);

        state = new InMemoryCloudStateStore(Path.Combine(root, "state.json"));
        Assert.Equal(transferred.EffectiveOwnershipEpoch, state.GetUserDeviceLink("ownership-device")!.EffectiveOwnershipEpoch);
        var reloaded = new InMemoryUserIntegrationStore(snapshot, state);
        Assert.Equal(replacement.CommandSecret, reloaded.FindLinkByLinkId(replacement.LinkId)!.CommandSecret);
        Assert.Null(reloaded.FindLinkByLinkId(original.LinkId));
        state.LinkUserToDevice(first.Id, "ownership-device", "return");
        Assert.Null(reloaded.FindLinkByLinkId(original.LinkId));
        Assert.Null(reloaded.FindLinkByLinkId(replacement.LinkId));
        Assert.Null(reloaded.FindMemberCalendarFeed(loop.LoopId, "member"));
    }
}
