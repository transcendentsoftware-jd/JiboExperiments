using Jibo.Cloud.Infrastructure.Calendar;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class WeeklyIcalRecurrenceTests
{
    private static IReadOnlyList<ParsedIcalEvent> Parse(string start, string rule, DateOnly today, TimeZoneInfo? zone = null) =>
        IcalCalendarParser.ParseEventsForWindow($"BEGIN:VCALENDAR\nBEGIN:VEVENT\n{start}\nRRULE:{rule}\nSUMMARY:Recurring standup\nEND:VEVENT\nEND:VCALENDAR",
            today, today.AddDays(1), zone ?? TimeZoneInfo.Utc);

    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    public void WeeklyByDayIncludesEverySelectedWeekday(int day)
    {
        var events = Parse("DTSTART:20260706T090000Z", "FREQ=WEEKLY;BYDAY=MO,WE,FR", new DateOnly(2026, 7, day));
        Assert.Single(events); Assert.Equal(new DateOnly(2026, 7, day), events[0].Date);
    }

    [Fact]
    public void CountIncludesOccurrencesBeforeTheRequestedWindow()
    {
        Assert.Empty(Parse("DTSTART:20260706T090000Z", "FREQ=WEEKLY;BYDAY=MO,WE,FR;COUNT=3", new DateOnly(2026, 7, 13)));
        Assert.Single(Parse("DTSTART:20260706T090000Z", "FREQ=WEEKLY;BYDAY=MO,WE,FR;COUNT=3", new DateOnly(2026, 7, 8)));
    }

    [Theory]
    [InlineData("SU", 1)]
    [InlineData("MO", 0)]
    public void IntervalUsesTheConfiguredWeekStart(string weekStart, int expected)
    {
        Assert.Equal(expected, Parse("DTSTART:20260705T090000Z", $"FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,SU;WKST={weekStart}", new DateOnly(2026, 7, 6)).Count);
    }

    [Fact]
    public void LongRunningWeeklySeriesDoesNotStopAfter366Steps()
    {
        Assert.Single(Parse("DTSTART:20000103T090000Z", "FREQ=WEEKLY;BYDAY=MO", new DateOnly(2026, 7, 6)));
    }

    [Fact]
    public void SourceUtcWeekdayCanAppearOnPreviousLocalDay()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Calendar minus five", TimeSpan.FromHours(-5), "Test", "Test");
        var events = Parse("DTSTART:20260706T003000Z", "FREQ=WEEKLY;BYDAY=MO,WE", new DateOnly(2026, 7, 7), zone);
        Assert.Single(events); Assert.Equal(new DateOnly(2026, 7, 7), events[0].Date);
        Assert.Equal(new TimeOnly(19, 30), TimeOnly.FromDateTime(events[0].StartLocal));
    }

    [Fact]
    public void NamedSourceZoneRetainsWallTimeAcrossDst()
    {
        var events = Parse("DTSTART;TZID=America/New_York:20260301T090000", "FREQ=WEEKLY;BYDAY=SU", new DateOnly(2026, 3, 15));
        Assert.Single(events); Assert.Equal(13, events[0].StartLocal.Hour);
    }

    [Fact]
    public void UntilIsInclusiveAndIndependentOfServerTimeZone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Calendar plus fourteen", TimeSpan.FromHours(14), "Test", "Test");
        var events = Parse("DTSTART:20260706T090000Z", "FREQ=WEEKLY;BYDAY=MO;UNTIL=20260713T090000Z", new DateOnly(2026, 7, 13), zone);
        Assert.Single(events); Assert.Equal(23, events[0].StartLocal.Hour);
        Assert.Empty(Parse("DTSTART:20260706T090000Z", "FREQ=WEEKLY;BYDAY=MO;UNTIL=20260713T090000Z", new DateOnly(2026, 7, 20), zone));
    }

    [Fact]
    public void AllDayWeeklyDatesRemainCalendarDates()
    {
        var events = Parse("DTSTART;VALUE=DATE:20260706", "FREQ=WEEKLY;BYDAY=MO,WE,FR", new DateOnly(2026, 7, 8));
        Assert.Single(events); Assert.True(events[0].IsAllDay); Assert.Equal("all day", events[0].TimeLabel);
    }

    [Theory]
    [InlineData("FREQ=WEEKLY;BYDAY=MO;BYMONTH=8")]
    [InlineData("FREQ=WEEKLY;BYDAY=1MO")]
    [InlineData("FREQ=WEEKLY;COUNT=0")]
    [InlineData("FREQ=WEEKLY;INTERVAL=0")]
    [InlineData("FREQ=WEEKLY;WKST=UNKNOWN")]
    [InlineData("FREQ=WEEKLY;COUNT=5;COUNT=10")]
    public void UnsupportedOrInvalidWeeklyFiltersAreNotSilentlyIgnored(string rule)
    {
        Assert.Empty(Parse("DTSTART:20260706T090000Z", rule, new DateOnly(2026, 7, 6)));
    }

    [Fact]
    public void NonexistentDstOccurrenceDoesNotConsumeCount()
    {
        var events = Parse("DTSTART;TZID=America/New_York:20260222T023000", "FREQ=WEEKLY;BYDAY=SU;COUNT=4", new DateOnly(2026, 3, 22));
        Assert.Single(events); Assert.Equal(new TimeOnly(6, 30), TimeOnly.FromDateTime(events[0].StartLocal));
    }

    [Fact]
    public void AmbiguousDstOccurrenceUsesFirstOccurrence()
    {
        var events = Parse("DTSTART;TZID=America/New_York:20261025T013000", "FREQ=WEEKLY;COUNT=2;BYDAY=SU", new DateOnly(2026, 11, 1));
        Assert.Single(events); Assert.Equal(new TimeOnly(5, 30), TimeOnly.FromDateTime(events[0].StartLocal));
    }
}
