using Jibo.Cloud.Domain.Models;

namespace Jibo.Cloud.Application.Abstractions;

public interface IUserIntegrationStore
{
    IReadOnlyList<HomeAssistantLinkRecord> GetHomeAssistantLinks();
    HomeAssistantLinkRecord? FindLinkByHaInstanceId(string haInstanceId);
    HomeAssistantLinkRecord? FindLinkByLinkId(string linkId);
    HomeAssistantLinkRecord? FindLinkForJibo(string? jiboDeviceId, string? jiboFriendlyId, string? expectedOwnershipEpoch = null);

    HomeAssistantLinkRecord AddHomeAssistantLink(
        string jiboDeviceId,
        string jiboFriendlyName,
        string haInstanceId, string? expectedOwnershipEpoch = null);

    HomeAssistantLinkRecord? RemoveHomeAssistantLink(string linkId);
    void UpdateHomeAssistantLastSeen(string linkId, DateTimeOffset lastSeenUtc);

    HomeAssistantLinkRecord? UpdateHomeAssistantClimateBlacklist(
        string linkId,
        bool blacklistHeat,
        bool blacklistCool);

    /// <summary>
    /// Ensures the link has a command secret (migrates older links created before per-command auth).
    /// </summary>
    HomeAssistantLinkRecord? EnsureHomeAssistantCommandSecret(string linkId);

    IReadOnlyList<MemberCalendarFeedRecord> GetMemberCalendarFeeds(string? loopId = null, string? expectedOwnershipEpoch = null);
    MemberCalendarFeedRecord? FindMemberCalendarFeed(string loopId, string memberId, string? expectedOwnershipEpoch = null);

    MemberCalendarFeedRecord UpsertMemberCalendarFeed(
        string loopId,
        string memberId,
        string icalUrl,
        bool isEnabled = true, string? expectedOwnershipEpoch = null);

    MemberCalendarFeedRecord? ClearMemberCalendarFeed(string loopId, string memberId, string? expectedOwnershipEpoch = null);

    MemberCalendarFeedRecord? UpdateMemberCalendarFeedSyncStatus(
        string loopId,
        string memberId,
        DateTimeOffset? lastSuccessUtc,
        string? lastError, string? expectedFeedId = null);
}
