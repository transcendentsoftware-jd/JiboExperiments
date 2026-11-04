using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Calendar;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Runtime.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class IcalCalendarReportProviderTests
{
    [Fact]
    public async Task GetReportAsync_UsesMemberFeedAndIsolatesPeople()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openjibo-ical-provider-{Guid.NewGuid():N}.json");
        try
        {
            var integrationStore = new InMemoryUserIntegrationStore(
                new EncryptedUserDataSnapshotStore(path, new UserDataEncryptionService()));
            var cloudState = new InMemoryCloudStateStore();
            var loopId = cloudState.GetLoops().First().LoopId;
            cloudState.SyncPeopleFromLoopUsers(loopId, "robot-zane",
            [
                new LoopUserSnapshot("looper-zane", "Zane", "A", Type: "member"),
                new LoopUserSnapshot("looper-jon", "Jon", "B", Type: "member")
            ]);

            integrationStore.UpsertMemberCalendarFeed(
                loopId,
                "looper-zane",
                "https://calendar.example.com/zane.ics");
            integrationStore.UpsertMemberCalendarFeed(
                loopId,
                "looper-jon",
                "https://calendar.example.com/jon.ics");

            // Provider windows use the robot/local calendar day, not UTC.
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.Local).DateTime);
            var zaneIcs = $"""
                BEGIN:VCALENDAR
                BEGIN:VEVENT
                DTSTART;VALUE=DATE:{today:yyyyMMdd}
                SUMMARY:Zane dentist
                END:VEVENT
                END:VCALENDAR
                """;
            var jonIcs = $"""
                BEGIN:VCALENDAR
                BEGIN:VEVENT
                DTSTART;VALUE=DATE:{today:yyyyMMdd}
                SUMMARY:Jon standup
                END:VEVENT
                END:VCALENDAR
                """;

            var fetcher = new StubIcalFeedFetcher(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["https://calendar.example.com/zane.ics"] = zaneIcs,
                ["https://calendar.example.com/jon.ics"] = jonIcs
            });
            var fallback = new CloudStateCalendarReportProvider(cloudState);
            var provider = new IcalCalendarReportProvider(
                integrationStore,
                cloudState,
                fetcher,
                fallback,
                NullLogger<IcalCalendarReportProvider>.Instance);

            var zaneReport = await provider.GetReportAsync(new TurnContext
            {
                Attributes = new Dictionary<string, object?>
                {
                    ["loopId"] = loopId,
                    ["personId"] = "looper-zane"
                }
            });
            var jonReport = await provider.GetReportAsync(new TurnContext
            {
                Attributes = new Dictionary<string, object?>
                {
                    ["loopId"] = loopId,
                    ["personalReportUserName"] = "Jon"
                }
            });

            Assert.NotNull(zaneReport);
            Assert.Contains("Zane dentist", zaneReport!.EventSummaries);
            Assert.DoesNotContain("Jon standup", zaneReport.EventSummaries);

            Assert.NotNull(jonReport);
            Assert.Contains("Jon standup", jonReport!.EventSummaries);
            Assert.DoesNotContain("Zane dentist", jonReport.EventSummaries);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task GetReportAsync_FallsBackWhenMemberFeedMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openjibo-ical-fallback-{Guid.NewGuid():N}.json");
        try
        {
            var integrationStore = new InMemoryUserIntegrationStore(
                new EncryptedUserDataSnapshotStore(path, new UserDataEncryptionService()));
            var cloudState = new InMemoryCloudStateStore();
            var loopId = cloudState.GetLoops().First().LoopId;
            cloudState.UpsertCalendarEvent(new CalendarEventRecord
            {
                LoopId = loopId,
                Summary = "Manual event",
                TimeLabel = "at 3 p.m.",
                Date = DateOnly.FromDateTime(DateTime.UtcNow)
            });

            var provider = new IcalCalendarReportProvider(
                integrationStore,
                cloudState,
                new StubIcalFeedFetcher(new Dictionary<string, string>()),
                new CloudStateCalendarReportProvider(cloudState),
                NullLogger<IcalCalendarReportProvider>.Instance);

            var report = await provider.GetReportAsync(new TurnContext
            {
                TimeZone = "UTC",
                Attributes = new Dictionary<string, object?>
                {
                    ["loopId"] = loopId,
                    ["personalReportUserName"] = "Nobody"
                }
            });

            Assert.NotNull(report);
            Assert.Contains("Manual event", report!.EventSummaries);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class StubIcalFeedFetcher(IReadOnlyDictionary<string, string> bodies) : IIcalFeedFetcher
    {
        public Task<IcalFeedFetchResult> FetchAsync(string icalUrl, CancellationToken cancellationToken = default)
        {
            if (bodies.TryGetValue(icalUrl, out var body))
                return Task.FromResult(new IcalFeedFetchResult(true, body, null, 200));
            return Task.FromResult(new IcalFeedFetchResult(false, null, "missing", 404));
        }
    }
}
