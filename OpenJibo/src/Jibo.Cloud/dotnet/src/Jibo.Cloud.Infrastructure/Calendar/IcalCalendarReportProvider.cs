using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Runtime.Abstractions;
using Microsoft.Extensions.Logging;

namespace Jibo.Cloud.Infrastructure.Calendar;

public sealed class IcalCalendarReportProvider(
    IUserIntegrationStore integrationStore,
    ICloudStateStore cloudStateStore,
    IIcalFeedFetcher feedFetcher,
    ICalendarReportProvider fallbackProvider,
    ILogger<IcalCalendarReportProvider> logger) : ICalendarReportProvider
{
    private static readonly TimeSpan SuccessCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FailureCacheTtl = TimeSpan.FromSeconds(45);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public async Task<CalendarReportSnapshot?> GetReportAsync(
        TurnContext turn,
        CancellationToken cancellationToken = default)
    {
        var loopId = ResolveLoopId(turn);
        var memberId = ResolveMemberId(turn, loopId);
        if (string.IsNullOrWhiteSpace(memberId))
            return await fallbackProvider.GetReportAsync(turn, cancellationToken);

        var feed = integrationStore.FindMemberCalendarFeed(loopId, memberId);
        if (feed is null || !feed.IsEnabled || string.IsNullOrWhiteSpace(feed.IcalUrl))
            return await fallbackProvider.GetReportAsync(turn, cancellationToken);

        var cacheKey = BuildCacheKey(feed.FeedId, feed.UpdatedUtc);
        if (_cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresUtc > DateTimeOffset.UtcNow)
            return cached.Snapshot;

        try
        {
            var fetch = await feedFetcher.FetchAsync(feed.IcalUrl, cancellationToken);
            if (!fetch.Ok || string.IsNullOrWhiteSpace(fetch.Body))
            {
                logger.LogWarning(
                    "Configured iCal feed failed for loop {LoopId}. Error={Error}",
                    loopId,
                    fetch.Error ?? "unknown");
                integrationStore.UpdateMemberCalendarFeedSyncStatus(
                    loopId,
                    memberId,
                    null,
                    fetch.Error ?? "fetch_failed", feed.FeedId);
                var failureSnapshot = new CalendarReportSnapshot([], [], [], HasServiceError: true);
                _cache[cacheKey] = new CacheEntry(failureSnapshot, DateTimeOffset.UtcNow.Add(FailureCacheTtl));
                return failureSnapshot;
            }

            var localZone = TimeZoneInfo.Local;
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, localZone).DateTime);
            var tomorrow = today.AddDays(1);
            var events = IcalCalendarParser.ParseEventsForWindow(fetch.Body, today, tomorrow, localZone);

            var todayEvents = events.Where(item => item.Date == today).ToArray();
            var tomorrowEvents = events.Where(item => item.Date == tomorrow).ToArray();
            var snapshot = new CalendarReportSnapshot(
                todayEvents.Select(static item => item.Summary).ToArray(),
                todayEvents.Select(static item => item.TimeLabel).ToArray(),
                tomorrowEvents.Select(static item => item.Summary).ToArray());

            integrationStore.UpdateMemberCalendarFeedSyncStatus(loopId, memberId, DateTimeOffset.UtcNow, null, feed.FeedId);
            _cache[cacheKey] = new CacheEntry(snapshot, DateTimeOffset.UtcNow.Add(SuccessCacheTtl));
            return snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "iCal calendar parse failed for loop {LoopId}", loopId);
            integrationStore.UpdateMemberCalendarFeedSyncStatus(loopId, memberId, null, "parse_failed", feed.FeedId);
            var failureSnapshot = new CalendarReportSnapshot([], [], [], HasServiceError: true);
            _cache[cacheKey] = new CacheEntry(failureSnapshot, DateTimeOffset.UtcNow.Add(FailureCacheTtl));
            return failureSnapshot;
        }
    }

    private string ResolveLoopId(TurnContext turn)
    {
        if (turn.Attributes.TryGetValue("loopId", out var loopValue) &&
            loopValue is not null &&
            !string.IsNullOrWhiteSpace(loopValue.ToString()))
            return loopValue.ToString()!.Trim();

        return cloudStateStore.GetLoops().FirstOrDefault()?.LoopId ?? "openjibo-default-loop";
    }

    private string? ResolveMemberId(TurnContext turn, string loopId)
    {
        if (turn.Attributes.TryGetValue("personId", out var personValue) &&
            personValue is not null &&
            !string.IsNullOrWhiteSpace(personValue.ToString()))
            return personValue.ToString()!.Trim();

        if (turn.Attributes.TryGetValue("speakerId", out var speakerValue) &&
            speakerValue is not null &&
            !string.IsNullOrWhiteSpace(speakerValue.ToString()))
            return speakerValue.ToString()!.Trim();

        if (turn.Attributes.TryGetValue("calendarMemberId", out var calendarMemberValue) &&
            calendarMemberValue is not null &&
            !string.IsNullOrWhiteSpace(calendarMemberValue.ToString()))
            return calendarMemberValue.ToString()!.Trim();

        var userName = ReadAttribute(turn, "personalReportUserName") ??
                       ReadAttribute(turn, "userName");
        if (string.IsNullOrWhiteSpace(userName)) return null;

        return ResolveMemberIdByName(loopId, userName);
    }

    private string? ResolveMemberIdByName(string loopId, string userName)
    {
        var normalized = NormalizeName(userName);
        if (string.IsNullOrWhiteSpace(normalized)) return null;

        foreach (var person in cloudStateStore.GetPeople(loopId))
        {
            var candidates = new[]
            {
                person.Alias,
                person.DisplayName,
                person.DisplayName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            };

            if (candidates.Any(candidate =>
                    !string.IsNullOrWhiteSpace(candidate) &&
                    string.Equals(NormalizeName(candidate!), normalized, StringComparison.Ordinal)))
                return person.PersonId;
        }

        var members = cloudStateStore.GetLoopMembers(loopId)
            .Where(static member =>
                !string.Equals(member.Type, "robot", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(member.Status, "removed", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var member in members)
        {
            var candidates = new[]
            {
                member.Nickname,
                member.FirstName,
                string.Join(' ', new[] { member.FirstName, member.LastName }.Where(static part =>
                    !string.IsNullOrWhiteSpace(part)))
            };

            if (candidates.Any(candidate =>
                    !string.IsNullOrWhiteSpace(candidate) &&
                    string.Equals(NormalizeName(candidate!), normalized, StringComparison.Ordinal)))
                return member.Id;
        }

        return null;
    }

    private static string? ReadAttribute(TurnContext turn, string key)
    {
        if (!turn.Attributes.TryGetValue(key, out var value) || value is null)
            return null;
        var text = value.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string NormalizeName(string value)
    {
        return string.Join(
            ' ',
            value.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string BuildCacheKey(string feedId, DateTimeOffset updatedUtc)
    {
        // Never include the private feed URL. Hash feed id + update stamp only.
        var material = $"{feedId}|{updatedUtc.ToUnixTimeSeconds()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash);
    }

    private sealed record CacheEntry(CalendarReportSnapshot Snapshot, DateTimeOffset ExpiresUtc);
}
