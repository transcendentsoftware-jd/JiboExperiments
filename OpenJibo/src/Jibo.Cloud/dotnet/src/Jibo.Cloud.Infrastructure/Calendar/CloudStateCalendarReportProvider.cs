using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Domain.Models;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Infrastructure.Calendar;

public sealed class CloudStateCalendarReportProvider(ICloudStateStore cloudStateStore, TimeProvider? timeProvider = null) : ICalendarReportProvider
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    public Task<CalendarReportSnapshot?> GetReportAsync(
        TurnContext turn,
        CancellationToken cancellationToken = default)
    {
        var loopId = ResolveLoopId(turn);
        var zone = CalendarLocalTime.ResolveZone(turn.TimeZone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), zone).DateTime);
        var tomorrow = today.AddDays(1);

        var calendarEvents = cloudStateStore.GetCalendarEvents(loopId)
            .Where(static calendarEvent => calendarEvent.IsEnabled)
            .Where(calendarEvent => calendarEvent.Date != default)
            .ToArray();

        var holidays = cloudStateStore.GetHolidays(loopId)
            .Where(static holiday => holiday.IsEnabled)
            .Where(holiday => holiday.Date != default)
            .ToArray();

        var todaySummaries = new List<string>();
        var todayTimes = new List<string>();
        var tomorrowSummaries = new List<string>();

        foreach (var entry in calendarEvents
                     .Select(calendarEvent => (
                         calendarEvent.Summary,
                         TimeLabel: calendarEvent.TimeLabel ?? "all day",
                         calendarEvent.Date))
                     .Concat(ToCalendarEntries(holidays)))
        {
            if (entry.Date == today)
            {
                todaySummaries.Add(entry.Summary);
                todayTimes.Add(entry.TimeLabel);
                continue;
            }

            if (entry.Date == tomorrow)
                tomorrowSummaries.Add(entry.Summary);
        }

        return Task.FromResult<CalendarReportSnapshot?>(
            new CalendarReportSnapshot(todaySummaries, todayTimes, tomorrowSummaries));
    }

    private static string ResolveLoopId(TurnContext turn)
    {
        if (turn.Attributes.TryGetValue("loopId", out var loopValue) &&
            loopValue is not null &&
            !string.IsNullOrWhiteSpace(loopValue.ToString()))
            return loopValue.ToString()!.Trim();

        return "openjibo-default-loop";
    }

    private static IEnumerable<(string Summary, string TimeLabel, DateOnly Date)> ToCalendarEntries(
        IEnumerable<HolidayRecord> holidays)
    {
        return holidays.Select(holiday => (
            holiday.Name,
            "all day",
            holiday.Date));
    }
}
