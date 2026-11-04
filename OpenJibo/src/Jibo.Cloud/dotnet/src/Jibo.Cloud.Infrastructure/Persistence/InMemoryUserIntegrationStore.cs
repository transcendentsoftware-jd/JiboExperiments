using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;

namespace Jibo.Cloud.Infrastructure.Persistence;

public sealed class InMemoryUserIntegrationStore : IUserIntegrationStore
{
    private readonly EncryptedUserDataSnapshotStore _snapshotStore;
    private readonly ICloudStateStore? _cloudStateStore;
    private readonly Lock _syncRoot = new();
    private List<HomeAssistantLinkRecord> _links;
    private List<MemberCalendarFeedRecord> _calendarFeeds;

    public InMemoryUserIntegrationStore(EncryptedUserDataSnapshotStore snapshotStore, ICloudStateStore? cloudStateStore = null)
    {
        _snapshotStore = snapshotStore;
        _cloudStateStore = cloudStateStore;
        var snapshot = snapshotStore.Load();
        _links = snapshot.HomeAssistantLinks.ToList();
        _calendarFeeds = snapshot.MemberCalendarFeeds.ToList();
    }

    public IReadOnlyList<HomeAssistantLinkRecord> GetHomeAssistantLinks()
    {
        lock (_syncRoot)
        {
            return _links.Where(IsActive).ToArray();
        }
    }

    public HomeAssistantLinkRecord? FindLinkByHaInstanceId(string haInstanceId)
    {
        lock (_syncRoot)
        {
            return _links.FirstOrDefault(link =>
                link.HaInstanceId.Equals(haInstanceId, StringComparison.OrdinalIgnoreCase) && IsActive(link));
        }
    }

    public HomeAssistantLinkRecord? FindLinkByLinkId(string linkId)
    {
        lock (_syncRoot)
        {
            return _links.FirstOrDefault(link =>
                link.LinkId.Equals(linkId, StringComparison.OrdinalIgnoreCase) && IsActive(link));
        }
    }

    public HomeAssistantLinkRecord? FindLinkForJibo(string? jiboDeviceId, string? jiboFriendlyId, string? expectedOwnershipEpoch = null)
    {
        lock (_syncRoot)
        {
            return _links.FirstOrDefault(link => MatchesJiboIdentity(link, jiboDeviceId, jiboFriendlyId) && IsActive(link) &&
                (expectedOwnershipEpoch is null || (link.OwnershipEpoch ?? ResolveDeviceScope(link.JiboDeviceId).Legacy) == expectedOwnershipEpoch));
        }
    }

    public HomeAssistantLinkRecord AddHomeAssistantLink(
        string jiboDeviceId,
        string jiboFriendlyName,
        string haInstanceId, string? expectedOwnershipEpoch = null)
    {
        lock (_syncRoot)
        {
            var scope = ResolveDeviceScope(jiboDeviceId);
            RequireEpoch(expectedOwnershipEpoch, scope.Current);
            var existing = _links.FirstOrDefault(link =>
                link.HaInstanceId.Equals(haInstanceId, StringComparison.OrdinalIgnoreCase) &&
                (link.OwnershipEpoch ?? scope.Legacy) == scope.Current &&
                ResolveDeviceScope(link.JiboDeviceId).DeviceId.Equals(scope.DeviceId, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                var updated = CloneLink(
                    existing,
                    jiboDeviceId: jiboDeviceId,
                    jiboFriendlyName: jiboFriendlyName,
                    haInstanceId: haInstanceId,
                    commandSecret: string.IsNullOrWhiteSpace(existing.CommandSecret)
                        ? HomeAssistantCommandAuthentication.GenerateCommandSecret()
                        : existing.CommandSecret,
                    lastSeenUtc: DateTimeOffset.UtcNow);

                _links = _links
                    .Select(link => link.LinkId == existing.LinkId ? updated : link)
                    .ToList();
                PersistLocked();
                return updated;
            }

            var record = new HomeAssistantLinkRecord
            {
                OwnershipEpoch = expectedOwnershipEpoch ?? scope.Current,
                JiboDeviceId = scope.DeviceId,
                JiboFriendlyName = jiboFriendlyName,
                HaInstanceId = haInstanceId,
                CommandSecret = HomeAssistantCommandAuthentication.GenerateCommandSecret()
            };
            _links.Add(record);
            PersistLocked();
            return record;
        }
    }

    public void UpdateHomeAssistantLastSeen(string linkId, DateTimeOffset lastSeenUtc)
    {
        lock (_syncRoot)
        {
            var index = _links.FindIndex(link => link.LinkId.Equals(linkId, StringComparison.OrdinalIgnoreCase) && IsActive(link));
            if (index < 0) return;

            _links[index] = CloneLink(_links[index], lastSeenUtc: lastSeenUtc);
            PersistLocked();
        }
    }

    public HomeAssistantLinkRecord? UpdateHomeAssistantClimateBlacklist(
        string linkId,
        bool blacklistHeat,
        bool blacklistCool)
    {
        lock (_syncRoot)
        {
            var index = _links.FindIndex(link => link.LinkId.Equals(linkId, StringComparison.OrdinalIgnoreCase) && IsActive(link));
            if (index < 0) return null;

            var updated = CloneLink(
                _links[index],
                blacklistHeat: blacklistHeat,
                blacklistCool: blacklistCool);
            _links[index] = updated;
            PersistLocked();
            return updated;
        }
    }

    public HomeAssistantLinkRecord? EnsureHomeAssistantCommandSecret(string linkId)
    {
        lock (_syncRoot)
        {
            var index = _links.FindIndex(link => link.LinkId.Equals(linkId, StringComparison.OrdinalIgnoreCase) && IsActive(link));
            if (index < 0) return null;

            var current = _links[index];
            if (!string.IsNullOrWhiteSpace(current.CommandSecret))
                return current;

            var updated = CloneLink(
                current,
                commandSecret: HomeAssistantCommandAuthentication.GenerateCommandSecret());
            _links[index] = updated;
            PersistLocked();
            return updated;
        }
    }

    public HomeAssistantLinkRecord? RemoveHomeAssistantLink(string linkId)
    {
        lock (_syncRoot)
        {
            var index = _links.FindIndex(link => link.LinkId.Equals(linkId, StringComparison.OrdinalIgnoreCase) && IsActive(link));
            if (index < 0) return null;

            var removed = _links[index];
            _links.RemoveAt(index);
            PersistLocked();
            return removed;
        }
    }

    public IReadOnlyList<MemberCalendarFeedRecord> GetMemberCalendarFeeds(string? loopId = null, string? expectedOwnershipEpoch = null)
    {
        lock (_syncRoot)
        {
            if (string.IsNullOrWhiteSpace(loopId))
                return _calendarFeeds.Where(feed => IsActive(feed) && MatchesEpoch(feed, expectedOwnershipEpoch)).ToArray();

            return _calendarFeeds
                .Where(feed => feed.LoopId.Equals(loopId, StringComparison.OrdinalIgnoreCase) && IsActive(feed) && MatchesEpoch(feed, expectedOwnershipEpoch))
                .ToArray();
        }
    }

    public MemberCalendarFeedRecord? FindMemberCalendarFeed(string loopId, string memberId, string? expectedOwnershipEpoch = null)
    {
        lock (_syncRoot)
        {
            var feed = FindFeedLocked(loopId, memberId);
            return feed is not null && MatchesEpoch(feed, expectedOwnershipEpoch) ? feed : null;
        }
    }

    public MemberCalendarFeedRecord UpsertMemberCalendarFeed(
        string loopId,
        string memberId,
        string icalUrl,
        bool isEnabled = true, string? expectedOwnershipEpoch = null)
    {
        if (string.IsNullOrWhiteSpace(loopId))
            throw new ArgumentException("Loop id is required.", nameof(loopId));
        if (string.IsNullOrWhiteSpace(memberId))
            throw new ArgumentException("Member id is required.", nameof(memberId));
        if (string.IsNullOrWhiteSpace(icalUrl))
            throw new ArgumentException("iCal URL is required.", nameof(icalUrl));

        lock (_syncRoot)
        {
            var scope = ResolveLoopScope(loopId);
            RequireEpoch(expectedOwnershipEpoch, scope.Current);
            var existing = _calendarFeeds.FirstOrDefault(feed =>
                feed.LoopId.Equals(loopId, StringComparison.OrdinalIgnoreCase) &&
                feed.MemberId.Equals(memberId, StringComparison.OrdinalIgnoreCase) &&
                (feed.OwnershipEpoch ?? scope.Legacy) == scope.Current);
            var now = DateTimeOffset.UtcNow;
            var record = new MemberCalendarFeedRecord
            {
                OwnershipEpoch = expectedOwnershipEpoch ?? scope.Current,
                FeedId = existing?.FeedId ?? Guid.NewGuid().ToString("N"),
                LoopId = loopId.Trim(),
                MemberId = memberId.Trim(),
                IcalUrl = icalUrl.Trim(),
                IsEnabled = isEnabled,
                CreatedUtc = existing?.CreatedUtc ?? now,
                UpdatedUtc = now,
                LastSuccessUtc = existing?.LastSuccessUtc,
                LastError = existing?.LastError
            };

            if (existing is null)
                _calendarFeeds.Add(record);
            else
                _calendarFeeds = _calendarFeeds
                    .Select(feed => feed.FeedId == existing.FeedId ? record : feed)
                    .ToList();

            PersistLocked();
            return record;
        }
    }

    public MemberCalendarFeedRecord? ClearMemberCalendarFeed(string loopId, string memberId, string? expectedOwnershipEpoch = null)
    {
        lock (_syncRoot)
        {
            var scope = ResolveLoopScope(loopId);
            RequireEpoch(expectedOwnershipEpoch, scope.Current);
            var existing = _calendarFeeds.FirstOrDefault(feed =>
                feed.LoopId.Equals(loopId, StringComparison.OrdinalIgnoreCase) &&
                feed.MemberId.Equals(memberId, StringComparison.OrdinalIgnoreCase) &&
                (feed.OwnershipEpoch ?? scope.Legacy) == scope.Current);
            if (existing is null) return null;

            _calendarFeeds.RemoveAll(feed => feed.FeedId == existing.FeedId);
            PersistLocked();
            return existing;
        }
    }

    public MemberCalendarFeedRecord? UpdateMemberCalendarFeedSyncStatus(
        string loopId,
        string memberId,
        DateTimeOffset? lastSuccessUtc,
        string? lastError, string? expectedFeedId = null)
    {
        lock (_syncRoot)
        {
            var existing = FindFeedLocked(loopId, memberId);
            if (existing is null || (expectedFeedId is not null && existing.FeedId != expectedFeedId)) return null;

            var updated = new MemberCalendarFeedRecord
            {
                OwnershipEpoch = existing.OwnershipEpoch,
                FeedId = existing.FeedId,
                LoopId = existing.LoopId,
                MemberId = existing.MemberId,
                IcalUrl = existing.IcalUrl,
                IsEnabled = existing.IsEnabled,
                CreatedUtc = existing.CreatedUtc,
                UpdatedUtc = existing.UpdatedUtc,
                LastSuccessUtc = lastSuccessUtc ?? existing.LastSuccessUtc,
                LastError = lastError
            };

            _calendarFeeds = _calendarFeeds
                .Select(feed => feed.FeedId == existing.FeedId ? updated : feed)
                .ToList();
            PersistLocked();
            return updated;
        }
    }

    private MemberCalendarFeedRecord? FindFeedLocked(string loopId, string memberId)
    {
        return _calendarFeeds.FirstOrDefault(feed =>
            feed.LoopId.Equals(loopId, StringComparison.OrdinalIgnoreCase) &&
            feed.MemberId.Equals(memberId, StringComparison.OrdinalIgnoreCase) && IsActive(feed));
    }

    private sealed record OwnershipScope(string DeviceId, string Current, string Legacy);

    private OwnershipScope ResolveDeviceScope(string deviceId)
    {
        var candidates = _cloudStateStore?.FindVisibleIdentityCandidates(deviceId) ?? [];
        var exact = candidates.Where(device => device.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (exact.Length == 0)
            exact = candidates.Where(device => device.RobotId.Equals(deviceId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (_cloudStateStore is not null && exact.Length != 1)
            return new OwnershipScope(deviceId, "unresolved-device:" + deviceId, "unresolved-legacy");
        var canonical = exact.FirstOrDefault()?.DeviceId ?? deviceId;
        var ownership = _cloudStateStore?.GetUserDeviceLink(canonical);
        return new OwnershipScope(canonical, ownership?.EffectiveOwnershipEpoch ?? "unowned",
            ownership?.EffectiveLegacyIntegrationEpoch ?? "unowned");
    }

    private OwnershipScope ResolveLoopScope(string loopId)
    {
        var loop = _cloudStateStore?.GetLoops().FirstOrDefault(item =>
            item.LoopId.Equals(loopId, StringComparison.OrdinalIgnoreCase));
        return ResolveDeviceScope(loop?.RobotId ?? string.Empty);
    }

    private bool IsActive(HomeAssistantLinkRecord link)
    {
        var scope = ResolveDeviceScope(link.JiboDeviceId);
        return (link.OwnershipEpoch ?? scope.Legacy) == scope.Current;
    }

    private bool MatchesEpoch(MemberCalendarFeedRecord feed, string? expected) =>
        expected is null || (feed.OwnershipEpoch ?? ResolveLoopScope(feed.LoopId).Legacy) == expected;

    private bool IsActive(MemberCalendarFeedRecord feed)
    {
        var scope = ResolveLoopScope(feed.LoopId);
        return (feed.OwnershipEpoch ?? scope.Legacy) == scope.Current;
    }

    private static void RequireEpoch(string? expected, string current)
    {
        if (expected is not null && expected != current)
            throw new UnauthorizedAccessException("Robot ownership has changed. Pair again.");
    }

    private void PersistLocked()
    {
        _snapshotStore.Save(new UserIntegrationSnapshot
        {
            SchemaVersion = UserIntegrationSnapshot.CurrentSchemaVersion,
            HomeAssistantLinks = _links.ToArray(),
            MemberCalendarFeeds = _calendarFeeds.ToArray()
        });
    }

    private static HomeAssistantLinkRecord CloneLink(
        HomeAssistantLinkRecord source,
        string? jiboDeviceId = null,
        string? jiboFriendlyName = null,
        string? haInstanceId = null,
        string? commandSecret = null,
        bool? blacklistHeat = null,
        bool? blacklistCool = null,
        DateTimeOffset? lastSeenUtc = null)
    {
        return new HomeAssistantLinkRecord
        {
            OwnershipEpoch = source.OwnershipEpoch,
            LinkId = source.LinkId,
            JiboDeviceId = jiboDeviceId ?? source.JiboDeviceId,
            JiboFriendlyName = jiboFriendlyName ?? source.JiboFriendlyName,
            HaInstanceId = haInstanceId ?? source.HaInstanceId,
            CommandSecret = commandSecret ?? source.CommandSecret ?? string.Empty,
            BlacklistHeat = blacklistHeat ?? source.BlacklistHeat,
            BlacklistCool = blacklistCool ?? source.BlacklistCool,
            PairedAtUtc = source.PairedAtUtc,
            LastSeenUtc = lastSeenUtc ?? source.LastSeenUtc
        };
    }

    private static bool MatchesJiboIdentity(
        HomeAssistantLinkRecord link,
        string? jiboDeviceId,
        string? jiboFriendlyId)
    {
        foreach (var candidate in new[] { jiboDeviceId, jiboFriendlyId }.Where(static value =>
                     !string.IsNullOrWhiteSpace(value)))
            if (link.JiboDeviceId.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
                link.JiboFriendlyName.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }
}
