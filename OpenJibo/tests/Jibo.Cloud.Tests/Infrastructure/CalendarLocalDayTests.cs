using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Calendar;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Runtime.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class CalendarLocalDayTests
{
    [Fact]
    public async Task FeedUsesTurnZoneAndDoesNotReuseAnotherZonesView()
    {
        using var fixture = new Fixture(new DateTimeOffset(2026, 7, 7, 0, 30, 0, TimeSpan.Zero));
        var chicago = await fixture.Provider.GetReportAsync(fixture.Turn("America/Chicago"));
        Assert.Equal(["July 6"], chicago!.EventSummaries);
        Assert.Equal(["July 7"], chicago.TomorrowEventSummaries);
        var tokyo = await fixture.Provider.GetReportAsync(fixture.Turn("Asia/Tokyo"));
        Assert.Equal(["July 7"], tokyo!.EventSummaries);
        Assert.Equal(["July 8"], tokyo.TomorrowEventSummaries);
        Assert.Equal(2, fixture.Fetcher.Calls);
        Assert.Same(tokyo, await fixture.Provider.GetReportAsync(fixture.Turn("Asia/Tokyo")));
        Assert.Equal(2, fixture.Fetcher.Calls);
    }

    [Fact]
    public async Task CachedFeedRollsOverAtLocalMidnightBeforeTtlExpires()
    {
        using var fixture = new Fixture(new DateTimeOffset(2026, 7, 6, 23, 59, 0, TimeSpan.Zero));
        Assert.Equal(["July 6"], (await fixture.Provider.GetReportAsync(fixture.Turn("UTC")))!.EventSummaries);
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(2);
        var next = await fixture.Provider.GetReportAsync(fixture.Turn("UTC"));
        Assert.Equal(["July 7"], next!.EventSummaries);
        Assert.Equal(["July 8"], next.TomorrowEventSummaries);
        Assert.Equal(2, fixture.Fetcher.Calls);
    }

    [Fact]
    public async Task FailureCacheAlsoBelongsToTheRequestedDayAndZone()
    {
        using var fixture = new Fixture(new DateTimeOffset(2026, 7, 6, 23, 59, 50, TimeSpan.Zero));
        fixture.Fetcher.Fail = true;
        var failed = await fixture.Provider.GetReportAsync(fixture.Turn("UTC"));
        Assert.True(failed!.HasServiceError);
        fixture.Fetcher.Fail = false;
        Assert.Same(failed, await fixture.Provider.GetReportAsync(fixture.Turn("UTC")));
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(20);
        Assert.False((await fixture.Provider.GetReportAsync(fixture.Turn("UTC")))!.HasServiceError);
        Assert.Equal(2, fixture.Fetcher.Calls);
    }

    [Fact]
    public async Task ManualEventsAndHolidaysUseTheSameRobotLocalDay()
    {
        using var fixture = new Fixture(new DateTimeOffset(2026, 7, 7, 0, 30, 0, TimeSpan.Zero), configureFeed: false);
        fixture.State.UpsertCalendarEvent(new CalendarEventRecord
        {
            LoopId = fixture.LoopId, Date = new DateOnly(2026, 7, 6), Summary = "Manual today", TimeLabel = "all day"
        });
        fixture.State.UpsertCalendarEvent(new CalendarEventRecord
        {
            LoopId = fixture.LoopId, Date = new DateOnly(2026, 7, 7), Summary = "Manual tomorrow", TimeLabel = "all day"
        });
        fixture.State.UpsertHoliday(new HolidayRecord
        {
            LoopId = fixture.LoopId, Date = new DateOnly(2026, 7, 6), Name = "Local birthday"
        });
        var report = await fixture.Provider.GetReportAsync(fixture.Turn("America/Chicago"));
        Assert.Contains("Manual today", report!.EventSummaries);
        Assert.Contains("Local birthday", report.EventSummaries);
        Assert.DoesNotContain("Manual tomorrow", report.EventSummaries);
        Assert.Contains("Manual tomorrow", report.TomorrowEventSummaries);
        Assert.Equal(0, fixture.Fetcher.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Invalid/Calendar_Zone")]
    public async Task MissingOrInvalidZoneKeepsExplicitServerLocalFallback(string? zone)
    {
        using var fixture = new Fixture(new DateTimeOffset(2026, 7, 7, 0, 30, 0, TimeSpan.Zero));
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(fixture.Clock.Now, TimeZoneInfo.Local).DateTime);
        var report = await fixture.Provider.GetReportAsync(fixture.Turn(zone));
        Assert.Equal([$"July {today.Day}"], report!.EventSummaries);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"openjibo-calendar-local-day-{Guid.NewGuid():N}.json");
        public MutableClock Clock { get; }
        public StubFetcher Fetcher { get; } = new();
        public InMemoryCloudStateStore State { get; } = new();
        public string LoopId { get; }
        public IcalCalendarReportProvider Provider { get; }
        public Fixture(DateTimeOffset now, bool configureFeed = true)
        {
            Clock = new MutableClock(now);
            LoopId = State.GetLoops().First().LoopId;
            var integrations = new InMemoryUserIntegrationStore(new EncryptedUserDataSnapshotStore(_path, new UserDataEncryptionService()));
            if (configureFeed) integrations.UpsertMemberCalendarFeed(LoopId, "calendar-member", "https://calendar.example.test/member.ics");
            Provider = new IcalCalendarReportProvider(integrations, State, Fetcher,
                new CloudStateCalendarReportProvider(State, Clock), NullLogger<IcalCalendarReportProvider>.Instance, Clock);
        }
        public TurnContext Turn(string? zone) => new()
        {
            TimeZone = zone,
            Attributes = new Dictionary<string, object?> { ["loopId"] = LoopId, ["personId"] = "calendar-member" }
        };
        public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class StubFetcher : IIcalFeedFetcher
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public Task<IcalFeedFetchResult> FetchAsync(string icalUrl, CancellationToken cancellationToken = default)
        {
            Calls++;
            var body = "BEGIN:VCALENDAR\n" + string.Join('\n', Enumerable.Range(6, 3).Select(day =>
                $"BEGIN:VEVENT\nDTSTART;VALUE=DATE:202607{day:00}\nSUMMARY:July {day}\nEND:VEVENT")) + "\nEND:VCALENDAR";
            return Task.FromResult(Fail ? new IcalFeedFetchResult(false, null, "synthetic", 503) : new IcalFeedFetchResult(true, body, null, 200));
        }
    }
}
